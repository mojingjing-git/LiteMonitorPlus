using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;

namespace LiteMonitor.AppearanceM2Probe
{
    // ================================================================
    // M2 合同 §9「运行时门槛」自证探针
    //
    // 覆盖 M2 合同 §9 中依赖**运行时行为**的八条门槛：
    //   #1 捕获后端 IsPreview => false
    //   #2 预览路径后端调用次数 = 0
    //   #3 Source 三值语义正确（真实 / 预览 / 降级）
    //   #4 代次不匹配的帧被丢弃且被回收
    //   #5 pending 最多一份
    //   #6 取消后恢复显示亲和性
    //   #8 CPU 与经过时间分别计量
    //   #9 缓冲预算有界
    //
    // ★ 隔离约束（与源码级探针一致）：
    //   1. **绝不做真实屏幕捕获**：所有"捕获"都由探针自有的假后端现画小位图，
    //      不调用 CopyFromScreen / BitBlt / 任何抓屏 API；
    //   2. 不创建 HardwareMonitor、不 Open 硬件；
    //   3. 不读也不写用户配置目录（只用内存里的 Theme 对象）；
    //   4. 不引入任何 NuGet 依赖，只引用主项目与 BCL。
    // ================================================================

    /// <summary>
    /// 位图释放探测：判断一张 <see cref="Bitmap"/> 是否已经被 Dispose。
    ///
    /// ★ 为什么需要它：<c>System.Drawing.Bitmap</c> 是 sealed，无法继承，
    ///   因此探针没法像"继承 Image 打点"那样精确统计释放次数
    ///   （net8.0 的 <c>Image</c> 无参构造是 protected internal，
    ///     跨程序集派生类调用 <c>base()</c> 会被 C# 拒绝 —— 已实测）。
    ///   这里改用"还能不能再拿到 Graphics"作为释放判据。
    /// ★ 探针启动时会**自检**该判据：自检不通过时，所有"是否泄漏"的判定
    ///   一律标为"不可判定"，绝不谎报"已释放"。
    /// </summary>
    internal static class BitmapReleaseProbe
    {
        /// <summary>释放探测是否可靠（自检通过）。</summary>
        public static bool Reliable { get; private set; }

        /// <summary>自检结论（人类可读）。</summary>
        public static string Note { get; private set; } = "尚未自检。";

        /// <summary>执行自检：存活位图不得被判为已释放，已释放位图必须被识别。</summary>
        public static void SelfCheck()
        {
            try
            {
                using (Bitmap live = new Bitmap(4, 4, PixelFormat.Format32bppArgb))
                {
                    if (IsDisposed(live))
                    {
                        Reliable = false;
                        Note = "自检失败：存活位图被误判为已释放。";
                        return;
                    }
                }

                Bitmap dead = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
                dead.Dispose();
                if (!IsDisposed(dead))
                {
                    Reliable = false;
                    Note = "自检失败：已释放位图未被识别 —— 当前运行时的释放探测不可用，" +
                           "泄漏相关判定将标为不可判定。";
                    return;
                }

                Reliable = true;
                Note = "自检通过：释放探测在当前运行时可用（Graphics.FromImage 判据）。";
            }
            catch (Exception ex)
            {
                Reliable = false;
                Note = "自检异常：" + ex.Message;
            }
        }

        /// <summary>位图是否已被释放（两级判据：Graphics.FromImage → LockBits）。</summary>
        public static bool IsDisposed(Bitmap bitmap)
        {
            try
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    GC.KeepAlive(g);
                }
            }
            catch (Exception)
            {
                return true;
            }

            try
            {
                BitmapData data = bitmap.LockBits(
                    new Rectangle(0, 0, 1, 1), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                bitmap.UnlockBits(data);
                GC.KeepAlive(data);
            }
            catch (Exception)
            {
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// 假捕获后端：实现 <see cref="IBackdropCaptureBackend"/>。
    ///
    /// ★ 它**不碰屏幕**：每一帧都是现画的一张小位图（无任何抓屏 API）。
    /// ★ 用计数器证明所有权流向：产出数 / 被释放数 / 恢复亲和性次数 / 捕获次数。
    /// ★ 捕获闸门（<see cref="UseGate"/>）让探针能精确控制"这一帧属于哪一代次"。
    /// </summary>
    internal sealed class FakeCaptureBackend : IBackdropCaptureBackend
    {
        private readonly object _sync = new();
        private readonly List<Bitmap> _produced = new();

        private int _captureCalls;
        private int _restoreCalls;
        private bool _ready = true;
        private string _unavailableReason = "假后端已就绪（探针注入）。";
        private ManualResetEventSlim? _entered;
        private ManualResetEventSlim? _gate;

        /// <summary>按调用序号分段的闸门（第 N 次捕获用第 N 个闸门；不足则沿用最后一个）。</summary>
        private readonly List<(ManualResetEventSlim Entered, ManualResetEventSlim Gate)> _steps = new();

        /// <summary>当前正在进行的捕获调用数。</summary>
        private int _concurrentCalls;

        /// <summary>捕获调用并发峰值。</summary>
        private int _maxConcurrentCalls;

        /// <summary>产出位图的宽度（像素）。</summary>
        public int FrameWidth { get; set; } = 64;

        /// <summary>产出位图的高度（像素）。</summary>
        public int FrameHeight { get; set; } = 48;

        /// <summary>每次捕获的额外耗时（毫秒），让经过时间口径可测。</summary>
        public int CaptureDelayMs { get; set; }

        /// <summary>是否让捕获直接失败。</summary>
        public bool FailCapture { get; set; }

        /// <summary>后端是否就绪。</summary>
        public bool IsReady
        {
            get { lock (_sync) return _ready; }
        }

        /// <summary>未就绪原因（人类可读）。</summary>
        public string UnavailableReason
        {
            get { lock (_sync) return _unavailableReason; }
        }

        /// <summary>被调用的捕获次数。</summary>
        public int CaptureCalls => Volatile.Read(ref _captureCalls);

        /// <summary>被调用的 RestoreDisplayAffinity 次数。</summary>
        public int RestoreCalls => Volatile.Read(ref _restoreCalls);

        /// <summary>已产出的位图数量。</summary>
        public int ProducedCount
        {
            get { lock (_sync) return _produced.Count; }
        }

        /// <summary>已产出的位图（强引用：用于统计释放情况）。</summary>
        public IReadOnlyList<Bitmap> Produced
        {
            get { lock (_sync) return _produced.ToArray(); }
        }

        /// <summary>
        /// 捕获调用的**并发峰值**。
        /// <para>★ 用来证明"不会有两个捕获循环同时存在"（A15 独立复核 S1 / P1）：
        ///   峰值 > 1 即意味着两个后台线程同时在抓屏。</para>
        /// </summary>
        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

        /// <summary>
        /// 追加一个**按调用序号分段**的闸门：第 N 次捕获会停在第 N 个闸门上。
        /// <para>★ 与 <see cref="UseGate"/> 的区别：后者所有调用共用一组闸门；
        ///   这里可以精确控制"第一次捕获停在闸门 1、第二次停在闸门 2"，
        ///   用来复现"旧轮次卡住 + 新轮次启动"的时序。</para>
        /// </summary>
        public void AddGatedStep(ManualResetEventSlim entered, ManualResetEventSlim gate)
        {
            lock (_sync)
            {
                _steps.Add((entered, gate));
            }
        }

        /// <summary>设置就绪状态与原因。</summary>
        public void SetReady(bool ready, string reason)
        {
            lock (_sync)
            {
                _ready = ready;
                _unavailableReason = reason;
            }
        }

        /// <summary>装上捕获闸门：进入捕获时置 <paramref name="entered"/>，等待 <paramref name="gate"/>。</summary>
        public void UseGate(ManualResetEventSlim entered, ManualResetEventSlim gate)
        {
            lock (_sync)
            {
                _entered = entered;
                _gate = gate;
            }
        }

        /// <summary>捕获一帧（现画一张小位图；所有权转移给调用方）。</summary>
        public bool TryCapture(BackdropServiceRequest request, out Image? frame, out BackdropServiceTiming timing)
        {
            int callIndex = Interlocked.Increment(ref _captureCalls) - 1;

            // ★ 并发峰值统计：证明"不会有两个捕获循环同时存在"（S1 / P1）
            int current = Interlocked.Increment(ref _concurrentCalls);
            int observed = Volatile.Read(ref _maxConcurrentCalls);
            while (current > observed)
            {
                int prev = Interlocked.CompareExchange(ref _maxConcurrentCalls, current, observed);
                if (prev == observed) break;
                observed = Volatile.Read(ref _maxConcurrentCalls);
            }

            try
            {
                ManualResetEventSlim? entered;
                ManualResetEventSlim? gate;
                lock (_sync)
                {
                    if (_steps.Count > 0)
                    {
                        // ★ 按调用序号取闸门（第 N 次捕获用第 N 个），可复现"旧轮卡住、新轮启动"
                        (ManualResetEventSlim Entered, ManualResetEventSlim Gate) step =
                            _steps[Math.Min(callIndex, _steps.Count - 1)];
                        entered = step.Entered;
                        gate = step.Gate;
                    }
                    else
                    {
                        entered = _entered;
                        gate = _gate;
                    }
                }

                entered?.Set();

                if (gate is not null && !gate.Wait(TimeSpan.FromSeconds(5)))
                {
                    // 闸门超时：按"捕获失败"返回 —— 探针绝不允许挂死。
                    frame = null;
                    timing = BackdropServiceTiming.Empty;
                    return false;
                }

                if (CaptureDelayMs > 0) Thread.Sleep(CaptureDelayMs);

                lock (_sync)
                {
                    if (FailCapture)
                    {
                        frame = null;
                        timing = BackdropServiceTiming.Empty;
                        return false;
                    }

                    Bitmap bitmap = new Bitmap(FrameWidth, FrameHeight, PixelFormat.Format32bppArgb);
                    _produced.Add(bitmap);
                    frame = bitmap;
                }

                timing = new BackdropServiceTiming(excludeWindowMs: 0d, backendCaptureMs: 1d, backendFilterMs: 0d);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentCalls);
            }
        }

        /// <summary>恢复显示亲和性（只计数，不碰窗口）。</summary>
        public void RestoreDisplayAffinity() => Interlocked.Increment(ref _restoreCalls);
    }

    /// <summary>
    /// 假帧来源：实现 <see cref="IBackdropFrameSource"/>，用于渲染路径。
    /// ★ 统计 <see cref="TryGetFrame"/> 的调用次数 —— 用来证明"预览路径根本不访问后端"。
    /// </summary>
    internal sealed class FakeFrameSource : IBackdropFrameSource
    {
        private readonly object _sync = new();
        private int _tryGetFrameCalls;
        private bool _ready = true;
        private bool _isPreview;
        private bool _returnsFrame = true;
        private Image? _frame;
        private string _unavailableReason = "假帧来源未提供原因（探针注入）。";

        /// <summary>构造一个假帧来源。</summary>
        public FakeFrameSource(bool isPreview, Image? frame = null)
        {
            _isPreview = isPreview;
            _frame = frame;
        }

        /// <summary>TryGetFrame 被调用的次数。</summary>
        public int TryGetFrameCalls => Volatile.Read(ref _tryGetFrameCalls);

        /// <summary>是否就绪。</summary>
        public bool IsReady
        {
            get { lock (_sync) return _ready; }
        }

        /// <summary>未就绪原因（人类可读）。</summary>
        public string UnavailableReason
        {
            get { lock (_sync) return _unavailableReason; }
        }

        /// <summary>自报的来源（false = 真实捕获帧；true = 预览固定图）。</summary>
        public bool IsPreview
        {
            get { lock (_sync) return _isPreview; }
        }

        /// <summary>设置就绪状态与原因。</summary>
        public void SetReady(bool ready, string reason)
        {
            lock (_sync)
            {
                _ready = ready;
                _unavailableReason = reason;
            }
        }

        /// <summary>设置"就绪但取不到帧"的注入故障。</summary>
        public void SetReturnsFrame(bool returnsFrame)
        {
            lock (_sync) { _returnsFrame = returnsFrame; }
        }

        /// <summary>取当前帧（借用；调用方不得释放）。</summary>
        public bool TryGetFrame(double blurPx, out Image? frame)
        {
            Interlocked.Increment(ref _tryGetFrameCalls);
            lock (_sync)
            {
                if (!_returnsFrame)
                {
                    frame = null;
                    return false;
                }

                frame = _frame;
                return frame is not null;
            }
        }
    }

    /// <summary>
    /// 固定样例图适配器：把一张固定图包装成 <see cref="IBackdropFrameSource"/>。
    /// ★ 合同 §2.2 第 3 条：**任何**把固定/合成图包装成帧后端的代码
    ///   （探针、测试、未来适配器）必须自报 <c>IsPreview =&gt; true</c>。
    /// </summary>
    internal sealed class FixedImageFrameSource : IBackdropFrameSource
    {
        private readonly Image? _frame;

        /// <summary>构造一个固定样例图适配器。</summary>
        public FixedImageFrameSource(Image? frame)
        {
            _frame = frame;
        }

        /// <summary>是否有固定样例图。</summary>
        public bool IsReady => _frame is not null;

        /// <summary>未就绪原因（人类可读）。</summary>
        public string UnavailableReason => _frame is null ? "固定样例图不可用（探针注入）。" : string.Empty;

        /// <summary>★ 恒为 true：固定样例图不是实时捕获结果。</summary>
        public bool IsPreview => true;

        /// <summary>取固定样例图（借用；调用方不得释放）。</summary>
        public bool TryGetFrame(double blurPx, out Image? frame)
        {
            frame = _frame;
            return _frame is not null;
        }
    }

    /// <summary>
    /// 受预算约束的图像处理适配器：把 <see cref="BackdropImageProcessor"/>
    /// （带"缓冲预算前置检查"）接到 <see cref="IBackdropFrameProcessor"/> 上。
    /// ★ 所有权：输入是**借用**（本类绝不释放它）；输出是**新所有权**（交给调用方）。
    /// </summary>
    internal sealed class BudgetFrameProcessor : IBackdropFrameProcessor
    {
        private int _processCalls;
        private int _rejectedCalls;

        /// <summary>处理调用次数。</summary>
        public int ProcessCalls => Volatile.Read(ref _processCalls);

        /// <summary>被拒绝（超预算 / 失败）的次数。</summary>
        public int RejectedCalls => Volatile.Read(ref _rejectedCalls);

        /// <summary>最近一次失败原因（人类可读）。</summary>
        public string LastFailureReason { get; private set; } = "";

        /// <summary>最近一次处理的源图像素数。</summary>
        public long LastSourcePixels { get; private set; }

        /// <summary>处理一帧（降采样 + 物理模糊半径滤波）。</summary>
        public bool TryProcess(BackdropServiceRequest request, Image source, out Image? processed)
        {
            Interlocked.Increment(ref _processCalls);

            BackdropBlurContract contract = BackdropBlurContract.Create(request.BlurPx, request.CaptureScale);
            BackdropProcessMetrics metrics;
            Bitmap? result = BackdropImageProcessor.Process(source, contract, out metrics);

            LastSourcePixels = (long)source.Width * source.Height;

            if (result is null)
            {
                Interlocked.Increment(ref _rejectedCalls);
                LastFailureReason = metrics.FailureReason ?? "（未提供原因）";
                processed = null;
                return false;
            }

            processed = result;
            return true;
        }
    }

    /// <summary>
    /// M2 合同 §9 的运行时门槛实现（第 1 / 2 / 3 / 4 / 5 / 6 / 8 / 9 条）。
    ///
    /// ★ 每个用例都有**有界等待**：任何一步都设上限，失败时给出可读原因，绝不挂死。
    /// </summary>
    internal sealed class RuntimeGateRunner
    {
        /// <summary>本次运行产生的用例。</summary>
        public List<ProbeCase> Cases { get; } = new();

        /// <summary>运行选定的运行时门槛。</summary>
        /// <param name="includeDeferredPerformance">
        /// 是否运行 CPU/经过时间配对与缓冲预算用例。默认保留原入口语义。
        /// </param>
        public void RunAll(bool includeDeferredPerformance = true)
        {
            BitmapReleaseProbe.SelfCheck();

            Cases.Add(RunIsPreviewGate());
            Cases.Add(RunPreviewPathBackendCallGate());
            Cases.Add(RunSourceSemanticsGate());
            Cases.Add(RunStaleGenerationGate());
            Cases.Add(RunPendingSingleGate());
            Cases.Add(RunRestoreAffinityGate());
            if (includeDeferredPerformance)
            {
                Cases.Add(RunTwoClockGate());
                Cases.Add(RunBufferBudgetGate());
            }

            // ★ A15 独立复核（2026-10-21）补的**生命周期时序**回归：
            //   这四条缺陷在合同 §9 的十条门槛里**都没有覆盖**（门槛只测了
            //   "正常 Stop"、"pending 上限"、"代次不匹配"），必须单独硬断言。
            Cases.Add(RunRunIsolationGate());          // S1（P1）
            Cases.Add(RunInvalidatedLeaseGate());      // S2
            Cases.Add(RunSuspendedInFlightGate());     // S3
            Cases.Add(RunRecoveryStateTextGate());     // S4
        }

        // ---------------------------------------------------------------- 门槛 #1

        /// <summary>门槛 #1：捕获后端 <c>IsPreview =&gt; false</c>。</summary>
        private ProbeCase RunIsPreviewGate()
        {
            ProbeCase c = new ProbeCase { Name = "M2 门槛#1 捕获后端 IsPreview=false" };

            FakeCaptureBackend backend = new FakeCaptureBackend();
            using (BackdropCaptureService service = new BackdropCaptureService(backend))
            {
                bool isPreview = service.IsPreview;
                c.Require(!isPreview,
                    "BackdropCaptureService.IsPreview 应为 false，实际为 true（真实捕获后端必须自报非预览）。");
                c.Detail("BackdropCaptureService.IsPreview", isPreview);
                c.Detail("BackdropCaptureService.IsPreviewType", nameof(BackdropCaptureService));
            }

            using (ScreenCaptureFrameSource screen = new ScreenCaptureFrameSource())
            {
                bool isPreview = screen.IsPreview;
                c.Require(!isPreview,
                    "ScreenCaptureFrameSource.IsPreview 应为 false，实际为 true（真实捕获后端必须自报非预览）。");
                c.Detail("ScreenCaptureFrameSource.IsPreview", isPreview);
                c.Detail("ScreenCaptureFrameSource.IsPreviewType", nameof(ScreenCaptureFrameSource));
            }

            using (Bitmap sample = CreateSampleBitmap(64, 48))
            {
                FixedImageFrameSource previewSource = new FixedImageFrameSource(sample);
                bool isPreview = previewSource.IsPreview;
                c.Require(isPreview,
                    "把固定样例图包成帧后端的适配器必须自报 IsPreview=true，实际为 false" +
                    "（否则固定图会被误记成实时帧，合同 §2.2 第 3 条）。");
                c.Detail("FixedImageFrameSource.IsPreview", isPreview);
                c.Detail("FixedImageFrameSource.IsPreviewType", nameof(FixedImageFrameSource));
            }

            c.Detail("gate", "M2 合同 §9 第 1 条");
            c.Detail("rule", "真实捕获类型（BackdropCaptureService / ScreenCaptureFrameSource）IsPreview 恒为 false；" +
                             "把固定样例图包成帧后端的适配器 IsPreview 恒为 true。");
            c.Detail("screenCapturePerformed", false);
            return c;
        }

        // ---------------------------------------------------------------- 门槛 #2

        /// <summary>门槛 #2：预览路径后端调用次数 = 0。</summary>
        private ProbeCase RunPreviewPathBackendCallGate()
        {
            ProbeCase c = new ProbeCase { Name = "M2 门槛#2 预览路径后端调用次数=0" };

            using Bitmap sample = CreateSampleBitmap(64, 48);

            // 计数型假后端（IsPreview=false，形态与真实捕获后端一致）
            FakeFrameSource backend = new FakeFrameSource(isPreview: false, frame: sample);

            BackdropStatus status;
            using (AppearanceRuntime runtime = new AppearanceRuntime(backend, sample))
            {
                ThemeAppearanceOptions options = runtime.Update(
                    CreateGlassTheme(), 1f, 1f, new Rectangle(0, 0, 320, 200));

                using Bitmap canvas = new Bitmap(320, 200, PixelFormat.Format32bppArgb);
                using Graphics g = Graphics.FromImage(canvas);
                status = runtime.Backdrop.Render(g, options);
            }

            int calls = backend.TryGetFrameCalls;
            c.Require(calls == 0,
                $"预览路径不得访问实时后端，实际 TryGetFrame 被调用 {calls} 次。");
            c.Require(status.Source == BackdropFrameSource.PreviewFrame,
                $"预览路径的 Source 应为 PreviewFrame，实际为 {status.Source}。");
            c.Require(status.EffectiveMode == BackdropRenderMode.Glass,
                $"预览路径的有效模式应为 Glass，实际为 {status.EffectiveMode}。");

            c.Detail("gate", "M2 合同 §9 第 2 条");
            c.Detail("previewSourceProvided", true);
            c.Detail("backendTryGetFrameCalls", calls);
            c.Detail("status.Source", status.Source.ToString());
            c.Detail("status.EffectiveMode", status.EffectiveMode.ToString());
            c.Detail("status.IsDegraded", status.IsDegraded);
            c.Detail("status.ReasonCode", status.ReasonCode.ToString());
            c.Detail("status.Reason", status.Reason ?? "(null)");
            c.Detail("rule", "options.PreviewSource != null ⇒ 背景层完全不访问 IBackdropFrameSource（结构性保证）。");
            c.Detail("screenCapturePerformed", false);
            return c;
        }

        // ---------------------------------------------------------------- 门槛 #3

        /// <summary>门槛 #3：<c>Source</c> 三值语义正确（真实 / 预览 / 降级）。</summary>
        private ProbeCase RunSourceSemanticsGate()
        {
            ProbeCase c = new ProbeCase { Name = "M2 门槛#3 Source 三值语义正确" };
            JsonArray scenarios = new JsonArray();

            // (a) 真实捕获：后端 IsPreview=false 且给出有效帧 ⇒ LiveFrame
            using (Bitmap live = CreateSampleBitmap(64, 48))
            {
                FakeFrameSource source = new FakeFrameSource(isPreview: false, frame: live);
                BackdropStatus status = RenderWith(source, previewSource: null);

                c.Require(status.Source == BackdropFrameSource.LiveFrame,
                    $"真实捕获帧的 Source 应为 LiveFrame，实际为 {status.Source}。");
                c.Require(status.EffectiveMode == BackdropRenderMode.Glass,
                    $"真实捕获帧的有效模式应为 Glass，实际为 {status.EffectiveMode}。");
                c.Require(source.TryGetFrameCalls == 1,
                    $"真实捕获路径应访问后端一次，实际 {source.TryGetFrameCalls} 次。");

                scenarios.Add(ScenarioJson("a 真实捕获（IsPreview=false，有效帧）", status, source.TryGetFrameCalls));
            }

            // (b) 预览固定图：options.PreviewSource != null ⇒ PreviewFrame
            using (Bitmap sample = CreateSampleBitmap(64, 48))
            {
                FakeFrameSource source = new FakeFrameSource(isPreview: false, frame: sample);
                BackdropStatus status = RenderWith(source, previewSource: sample);

                c.Require(status.Source == BackdropFrameSource.PreviewFrame,
                    $"预览固定图的 Source 应为 PreviewFrame，实际为 {status.Source}。");
                c.Require(status.EffectiveMode == BackdropRenderMode.Glass,
                    $"预览固定图的有效模式应为 Glass，实际为 {status.EffectiveMode}。");
                c.Require(source.TryGetFrameCalls == 0,
                    $"预览固定图路径不应访问后端，实际 {source.TryGetFrameCalls} 次。");

                scenarios.Add(ScenarioJson("b 预览固定图（options.PreviewSource != null）", status, source.TryGetFrameCalls));
            }

            // (c1) 后端未就绪 ⇒ 走降级链
            {
                FakeFrameSource source = new FakeFrameSource(isPreview: false, frame: null);
                source.SetReady(false, "假后端未就绪（探针注入）。");
                BackdropStatus status = RenderWith(source, previewSource: null);

                c.Require(status.Source != BackdropFrameSource.LiveFrame && status.Source != BackdropFrameSource.PreviewFrame,
                    $"后端不可用时 Source 既不应是 LiveFrame 也不应是 PreviewFrame，实际为 {status.Source}。");
                c.Require(status.IsDegraded,
                    $"后端不可用时应发生降级，实际 IsDegraded=false（{status.Source}）。");
                c.Require(status.ReasonCode != BackdropDegradeReason.None,
                    $"后端不可用时应带降级原因码，实际为 {status.ReasonCode}。");
                c.Require(!string.IsNullOrWhiteSpace(status.Reason),
                    "后端不可用时应带可读降级原因文本，实际为空。");

                scenarios.Add(ScenarioJson("c1 后端未就绪（降级链）", status, source.TryGetFrameCalls));
            }

            // (c2) 后端就绪但取不到有效帧 ⇒ 走降级链
            {
                FakeFrameSource source = new FakeFrameSource(isPreview: false, frame: null);
                source.SetReady(true, "假后端就绪但取不到帧（探针注入）。");
                source.SetReturnsFrame(false);
                BackdropStatus status = RenderWith(source, previewSource: null);

                c.Require(status.Source != BackdropFrameSource.LiveFrame && status.Source != BackdropFrameSource.PreviewFrame,
                    $"后端无有效帧时 Source 既不应是 LiveFrame 也不应是 PreviewFrame，实际为 {status.Source}。");
                c.Require(status.IsDegraded,
                    $"后端无有效帧时应发生降级，实际 IsDegraded=false（{status.Source}）。");
                c.Require(status.ReasonCode != BackdropDegradeReason.None,
                    $"后端无有效帧时应带降级原因码，实际为 {status.ReasonCode}。");
                c.Require(!string.IsNullOrWhiteSpace(status.Reason),
                    "后端无有效帧时应带可读降级原因文本，实际为空。");

                scenarios.Add(ScenarioJson("c2 后端就绪但无有效帧（降级链）", status, source.TryGetFrameCalls));
            }

            c.Detail("gate", "M2 合同 §9 第 3 条");
            c.Detail("scenarios", scenarios);
            c.Detail("rule", "LiveFrame ⇔ 实时后端给出有效帧且 IsPreview=false；" +
                             "PreviewFrame ⇔ options.PreviewSource != null 或后端 IsPreview=true；" +
                             "其余走降级链（Gradient / Legacy），且必须带原因码与可读原因文本。");
            c.Detail("screenCapturePerformed", false);
            return c;
        }

        // ---------------------------------------------------------------- 门槛 #4

        /// <summary>门槛 #4：代次不匹配的帧被丢弃且被回收。</summary>
        private ProbeCase RunStaleGenerationGate()
        {
            ProbeCase c = new ProbeCase { Name = "M2 门槛#4 代次不匹配的帧被丢弃且被回收" };

            FakeCaptureBackend backend = new FakeCaptureBackend();
            using ManualResetEventSlim entered = new ManualResetEventSlim(false);
            using ManualResetEventSlim gate = new ManualResetEventSlim(false);
            backend.UseGate(entered, gate);

            BackdropServiceRequest oldRequest = CreateRequest(theme: 1, handle: 1, size: 1);
            BackdropServiceRequest newRequest = CreateRequest(theme: 2, handle: 2, size: 2);

            List<BackdropFrameLease> leases = new();
            long staleDropped;
            int acquired;
            int staleHandedOver;
            bool staleFrameRecycled;
            int liveAfterStop;
            bool stopped;

            using (BackdropCaptureService service = new BackdropCaptureService(backend, processor: null, takeoverTimeoutMs: 50d))
            {
                service.Start(oldRequest);

                bool enteredCapture = entered.Wait(TimeSpan.FromSeconds(5));
                c.Require(enteredCapture, "假后端未在 5 秒内进入捕获（探针闸门失效）。");
                if (!enteredCapture)
                {
                    c.Detail("gate", "M2 合同 §9 第 4 条");
                    return c;
                }

                // ★ 换掉三个代次（主题 / 句柄 / 尺寸）——闸门里的那一帧仍属于旧代次
                service.UpdateRequest(newRequest);

                // 放行闸门：旧代次的帧此刻才被生产出来
                gate.Set();

                bool staleCounted = WaitUntil(() => service.Metrics.FramesDroppedStale >= 1, 5000);
                staleDropped = service.Metrics.FramesDroppedStale;
                c.Require(staleCounted,
                    $"代次不匹配的帧未被后台回收（FramesDroppedStale={staleDropped}，期望 ≥1）。");

                // 旧代次的那一帧（第一次产出的位图）必须已被服务回收
                IReadOnlyList<Bitmap> produced = backend.Produced;
                Bitmap? staleFrame = produced.Count > 0 ? produced[0] : null;
                staleFrameRecycled = staleFrame is not null && BitmapReleaseProbe.IsDisposed(staleFrame);
                if (BitmapReleaseProbe.Reliable)
                {
                    c.Require(staleFrameRecycled,
                        "代次不匹配的帧仍处于存活状态 —— 说明它既没被服务回收，也没交给 UI（泄漏）。");
                }

                // ★ 新代次的帧是**合法**可接管的：这里只校验"接管到的帧代次必须与当前一致"
                WaitUntil(() => service.PendingFrameCount > 0, 3000);
                acquired = 0;
                staleHandedOver = 0;
                while (service.TryAcquireFrame(out BackdropFrameLease? lease) && lease is not null)
                {
                    leases.Add(lease);
                    acquired++;
                    if (!lease.Request.MatchesGeneration(newRequest)) staleHandedOver++;
                    if (staleFrame is not null && ReferenceEquals(lease.Frame, staleFrame)) staleHandedOver++;
                    if (acquired >= 3) break;
                }

                c.Require(staleHandedOver == 0,
                    $"有 {staleHandedOver} 帧旧代次的帧被交给了 UI（代次校验失效）。");

                stopped = service.Stop(TimeSpan.FromSeconds(3));
            }

            // UI 释放自己接管的租约（UI 才是这些位图的所有者）
            foreach (BackdropFrameLease lease in leases) lease.Dispose();

            liveAfterStop = CountLive(backend.Produced);
            if (BitmapReleaseProbe.Reliable)
            {
                c.Require(liveAfterStop == 0,
                    $"停止后仍有 {liveAfterStop} 张位图未被释放（假后端产出 {backend.ProducedCount} 张）。");
            }

            c.Detail("gate", "M2 合同 §9 第 4 条");
            c.Detail("oldGeneration", "theme=1, handle=1, size=1");
            c.Detail("newGeneration", "theme=2, handle=2, size=2");
            c.Detail("metrics.FramesDroppedStale", staleDropped);
            c.Detail("staleFrameRecycledByService", staleFrameRecycled);
            c.Detail("framesAcquiredByUi", acquired);
            c.Detail("staleFramesHandedToUi", staleHandedOver);
            c.Detail("backendProducedFrames", backend.ProducedCount);
            c.Detail("liveFramesAfterStop", liveAfterStop);
            c.Detail("stopCompletedWithinTimeout", stopped);
            c.Detail("releaseProbeReliable", BitmapReleaseProbe.Reliable);
            c.Detail("releaseProbeNote", BitmapReleaseProbe.Note);
            c.Detail("rule", "代次变更后：旧代次帧不得被 TryAcquireFrame 交给 UI，且必须由服务自己回收" +
                             "（Metrics.FramesDroppedStale 递增 + 存活位图数归零）；" +
                             "新代次的帧能接管是**正常**行为，不算缺陷。");
            c.Detail("screenCapturePerformed", false);
            return c;
        }

        // ---------------------------------------------------------------- 门槛 #5

        /// <summary>门槛 #5：pending 最多一份。</summary>
        private ProbeCase RunPendingSingleGate()
        {
            ProbeCase c = new ProbeCase { Name = "M2 门槛#5 pending 最多一份" };

            FakeCaptureBackend backend = new FakeCaptureBackend();
            int maxPending = 0;
            int maxLive = 0;
            int samples = 0;
            bool stopped;
            int liveAfterStop;
            int produced;

            using (BackdropCaptureService service = new BackdropCaptureService(backend, processor: null, takeoverTimeoutMs: 5d))
            {
                service.Start(CreateRequest(theme: 1, handle: 1, size: 1));

                // ★ UI 一帧也不取：服务必须自己保证 pending ≤ 1
                Stopwatch sw = Stopwatch.StartNew();
                Stopwatch liveClock = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 1500)
                {
                    int pending = service.PendingFrameCount;
                    if (pending > maxPending) maxPending = pending;
                    samples++;

                    if (liveClock.ElapsedMilliseconds >= 50)
                    {
                        liveClock.Restart();
                        int live = CountLive(backend.Produced);
                        if (live > maxLive) maxLive = live;
                    }

                    Thread.Sleep(5);
                }

                c.Require(maxPending <= 1,
                    $"pending 候选帧数应始终 ≤1，实际观测到 {maxPending}。");
                c.Require(maxLive <= 2,
                    $"存活位图数应保持有界（≤2：pending + 在途），实际观测到 {maxLive}。");

                stopped = service.Stop(TimeSpan.FromSeconds(3));
            }

            liveAfterStop = CountLive(backend.Produced);
            produced = backend.ProducedCount;

            if (BitmapReleaseProbe.Reliable)
            {
                c.Require(liveAfterStop == 0,
                    $"停止后仍有 {liveAfterStop} 张位图未被释放（产出 {produced} 张）。");
            }

            c.Detail("gate", "M2 合同 §9 第 5 条");
            c.Detail("sampleCount", samples);
            c.Detail("maxPendingFrameCount", maxPending);
            c.Detail("maxLiveFrames", maxLive);
            c.Detail("liveFramesAfterStop", liveAfterStop);
            c.Detail("backendProducedFrames", produced);
            c.Detail("stopCompletedWithinTimeout", stopped);
            c.Detail("releaseProbeReliable", BitmapReleaseProbe.Reliable);
            c.Detail("releaseProbeNote", BitmapReleaseProbe.Note);
            c.Detail("rule", "UI 不取帧时，服务连续产出多帧：PendingFrameCount 恒 ≤1；" +
                             "存活位图数（假后端产出 - 已释放）有界；停止后归零。");
            c.Detail("screenCapturePerformed", false);
            return c;
        }

        // ---------------------------------------------------------------- 门槛 #6

        /// <summary>门槛 #6：取消后恢复显示亲和性。</summary>
        private ProbeCase RunRestoreAffinityGate()
        {
            ProbeCase c = new ProbeCase { Name = "M2 门槛#6 取消后恢复显示亲和性" };

            FakeCaptureBackend backend = new FakeCaptureBackend { CaptureDelayMs = 2 };
            int restoreCalls;
            bool stopped;
            int captureCalls;

            using (BackdropCaptureService service = new BackdropCaptureService(backend, processor: null, takeoverTimeoutMs: 5d))
            {
                service.Start(CreateRequest(theme: 1, handle: 1, size: 1));
                WaitUntil(() => backend.CaptureCalls >= 1, 3000);
                captureCalls = backend.CaptureCalls;

                stopped = service.Stop(TimeSpan.FromSeconds(3));
                restoreCalls = backend.RestoreCalls;
            }

            c.Require(restoreCalls >= 1,
                $"停止后必须恢复显示亲和性，实际 RestoreDisplayAffinity 被调用 {restoreCalls} 次。");
            c.Require(stopped, "后台任务未在停止上限内退出（取消必须有上限且可等待）。");

            c.Detail("gate", "M2 合同 §9 第 6 条");
            c.Detail("backendCaptureCalls", captureCalls);
            c.Detail("backendRestoreDisplayAffinityCalls", restoreCalls);
            c.Detail("stopCompletedWithinTimeout", stopped);
            c.Detail("rule", "服务 Stop（有上限等待）后，后端 RestoreDisplayAffinity 至少被调用一次。");
            c.Detail("screenCapturePerformed", false);
            return c;
        }

        // ---------------------------------------------------------------- 门槛 #8

        /// <summary>门槛 #8：CPU 与经过时间分别计量。</summary>
        private ProbeCase RunTwoClockGate()
        {
            ProbeCase c = new ProbeCase { Name = "M2 门槛#8 CPU 与经过时间分别计量" };

            // ---- 1) 结构断言：两个口径必须是**不同字段**，且同时存在 ----
            PropertyInfo[] properties = typeof(BackdropServiceMetrics).GetProperties();

            string[] cpuClockFields = { "CpuMsTotal", "CpuMsPerFrameAvg" };
            string[] elapsedClockFields =
            {
                "ElapsedMsLast", "ElapsedMsP95", "ElapsedMsMax",
                "LastCaptureMs", "LastFilterMs", "LastPublishMs", "LastWaitUiMs", "LastExcludeWindowMs",
                "AvgCaptureMs", "AvgFilterMs", "AvgPublishMs", "AvgWaitUiMs", "AvgExcludeWindowMs",
                "MaxWaitUiMs", "MaxExcludeWindowMs"
            };

            foreach (string name in cpuClockFields.Concat(elapsedClockFields))
            {
                PropertyInfo? property = properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
                c.Require(property != null, $"BackdropServiceMetrics 缺少字段 {name}。");
                c.Require(property != null && property.PropertyType == typeof(double),
                    $"BackdropServiceMetrics.{name} 的类型应为 double（实际 {property?.PropertyType.Name}）。");
            }

            // 两个口径的字段名集合互不相交（不得是同一个字段被重复解读）
            c.Require(cpuClockFields.Intersect(elapsedClockFields, StringComparer.Ordinal).Count() == 0,
                "CPU 口径字段与经过时间口径字段出现重名 —— 两个口径被混用了。");

            // ---- 2) 运行时断言：两个口径都被服务分别填值 ----
            FakeCaptureBackend backend = new FakeCaptureBackend { CaptureDelayMs = 5 };
            BackdropServiceMetrics metrics;

            using (BackdropCaptureService service = new BackdropCaptureService(backend, processor: null, takeoverTimeoutMs: 5d))
            {
                service.Start(CreateRequest(theme: 1, handle: 1, size: 1));
                WaitUntil(() => service.Metrics.FramesCaptured >= 2, 8000);
                metrics = service.Metrics;
                service.Stop(TimeSpan.FromSeconds(3));
            }

            c.Require(metrics.FramesCaptured >= 1, "服务没有产出任何一轮捕获，两个口径都无从比较。");
            c.Require(metrics.ElapsedMsLast > 0d,
                $"经过时间口径（ElapsedMsLast）应为正，实际 {metrics.ElapsedMsLast}。");
            c.Require(metrics.LastWaitUiMs > 0d,
                $"等待 UI 接管段（LastWaitUiMs）应为正（UI 不取帧时服务会等满预算），实际 {metrics.LastWaitUiMs}。");
            // ★ CPU 口径与经过时间口径是**两个独立时钟**：CPU 口径的差值按 ~15.6 ms 粒度取整，
            //   单轮里可能出现"CPU 差值 15.6 ms > 墙钟 10 ms"的现象 —— 这正是两者独立的证据，
            //   因此这里给一个粒度级的容差，而不是拿它当"混用"的判据。
            c.Require(metrics.CpuMsTotal <= metrics.ElapsedMsLast + 20d,
                $"CPU 口径（{metrics.CpuMsTotal} ms）不应显著大于经过时间口径（{metrics.ElapsedMsLast} ms）" +
                "（容差 20 ms = Windows 进程 CPU 时钟粒度）。");

            // 经过时间口径 = 捕获 + 滤波 + 发布 + 等待 UI 接管（四段墙钟之和）
            double segmentSum = metrics.LastCaptureMs + metrics.LastFilterMs
                                + metrics.LastPublishMs + metrics.LastWaitUiMs;
            c.Require(Math.Abs(metrics.ElapsedMsLast - segmentSum) < 0.01d,
                $"经过时间口径应等于四段墙钟之和（{segmentSum}），实际 {metrics.ElapsedMsLast}。");

            // ★ CPU 口径为 0 时，**不得**据此断言"经过时间也为 0"
            if (metrics.CpuMsTotal <= 0d)
            {
                c.Detail("cpuClockNote",
                    "CPU 口径记 0：Windows 进程 CPU 时钟粒度约 15.6 ms，单轮差值落在粒度以内 ⇒ " +
                    "这是【不可判定】，不表示零开销；因此不得据此断言经过时间也为 0" +
                    $"（实测经过时间 ElapsedMsLast={metrics.ElapsedMsLast} ms ≠ 0）。");
            }

            c.Detail("gate", "M2 合同 §9 第 8 条");
            c.Detail("cpuClockFields", new JsonArray(cpuClockFields.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()));
            c.Detail("elapsedClockFields", new JsonArray(elapsedClockFields.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()));
            c.Detail("metrics.CpuMsTotal", JsonValue.Create(metrics.CpuMsTotal));
            c.Detail("metrics.CpuMsPerFrameAvg", JsonValue.Create(metrics.CpuMsPerFrameAvg));
            c.Detail("metrics.ElapsedMsLast", JsonValue.Create(metrics.ElapsedMsLast));
            c.Detail("metrics.ElapsedMsP95", JsonValue.Create(metrics.ElapsedMsP95));
            c.Detail("metrics.ElapsedMsMax", JsonValue.Create(metrics.ElapsedMsMax));
            c.Detail("metrics.LastCaptureMs", JsonValue.Create(metrics.LastCaptureMs));
            c.Detail("metrics.LastFilterMs", JsonValue.Create(metrics.LastFilterMs));
            c.Detail("metrics.LastPublishMs", JsonValue.Create(metrics.LastPublishMs));
            c.Detail("metrics.LastWaitUiMs", JsonValue.Create(metrics.LastWaitUiMs));
            c.Detail("metrics.LastExcludeWindowMs", JsonValue.Create(metrics.LastExcludeWindowMs));
            c.Detail("metrics.FramesCaptured", metrics.FramesCaptured);
            c.Detail("rule", "CPU 口径（CpuMsTotal / CpuMsPerFrameAvg）与经过时间口径" +
                             "（ElapsedMsLast / ElapsedMsP95 / ElapsedMsMax + captureMs/filterMs/publishMs/waitUiMs/excludeWindowMs 五段）" +
                             "必须同时存在且互为不同字段；CPU 口径为 0 只说明 15.6 ms 时钟粒度下不可判定。");
            c.Detail("screenCapturePerformed", false);
            return c;
        }

        // ---------------------------------------------------------------- 门槛 #9

        /// <summary>门槛 #9：缓冲预算有界。</summary>
        private ProbeCase RunBufferBudgetGate()
        {
            ProbeCase c = new ProbeCase { Name = "M2 门槛#9 缓冲预算有界" };

            // ---- 1) 预算常量 ----
            c.Require(BackdropImageProcessor.MaxBufferPixels == 2_000_000,
                $"MaxBufferPixels 应为 2000000，实际 {BackdropImageProcessor.MaxBufferPixels}。");
            c.Require(BackdropImageProcessor.MaxBufferBytes == 32L * 1024 * 1024,
                $"MaxBufferBytes 应为 32 MiB（33554432），实际 {BackdropImageProcessor.MaxBufferBytes}。");

            // ---- 2) 超限源图被直接拒绝（先判后分配） ----
            const int overWidth = 2000;
            const int overHeight = 2000;
            long overPixels = (long)overWidth * overHeight;
            string processFailureReason;
            bool processRejected;
            long tempBytesBefore;
            long tempBytesAfter;

            using (Bitmap oversized = new Bitmap(overWidth, overHeight, PixelFormat.Format32bppArgb))
            {
                tempBytesBefore = BackdropImageProcessor.CurrentTempBytes;
                BackdropBlurContract contract = BackdropBlurContract.Create(12d, 0.25d);
                BackdropProcessMetrics metrics;
                Bitmap? processed = BackdropImageProcessor.Process(oversized, contract, out metrics);
                tempBytesAfter = BackdropImageProcessor.CurrentTempBytes;

                processRejected = processed is null && !metrics.Success;
                processFailureReason = metrics.FailureReason ?? "(null)";

                c.Require(processed is null,
                    $"超预算源图应被拒绝，实际返回了 {processed?.Width}×{processed?.Height} 的位图。");
                c.Require(!metrics.Success, "超预算源图的处理结果应为失败。");
                c.Require(!string.IsNullOrWhiteSpace(metrics.FailureReason),
                    "超预算时应给出可读原因（实际为空）。");
                c.Require(tempBytesAfter == tempBytesBefore,
                    $"超预算时不应分配临时缓冲（{tempBytesBefore} → {tempBytesAfter}）。");
            }

            // ---- 3) 服务侧：喂超限尺寸 ⇒ 不得照单全收 ----
            FakeCaptureBackend backend = new FakeCaptureBackend { FrameWidth = overWidth, FrameHeight = overHeight };
            BudgetFrameProcessor processor = new BudgetFrameProcessor();
            long published;
            long captureFailures;
            BackdropDegradeReason degradeReason;
            string degradeText;
            bool stopped;

            using (BackdropCaptureService service = new BackdropCaptureService(backend, processor, takeoverTimeoutMs: 20d))
            {
                service.Start(CreateRequest(theme: 1, handle: 1, size: 1));

                bool failed = WaitUntil(() => service.Metrics.CaptureFailures >= 1, 8000);
                BackdropCaptureStatus status = service.Status;

                published = service.Metrics.FramesPublished;
                captureFailures = service.Metrics.CaptureFailures;
                degradeReason = status.DegradeReason;
                degradeText = status.DegradeReasonText ?? "";

                c.Require(failed, "服务没有记录超预算帧的失败（CaptureFailures 仍为 0）。");
                c.Require(published == 0,
                    $"超预算帧不应被发布，实际发布了 {published} 帧。");
                c.Require(degradeReason != BackdropDegradeReason.None,
                    $"超预算帧应带降级原因码，实际为 {degradeReason}。");
                c.Require(!string.IsNullOrWhiteSpace(degradeText),
                    "超预算帧应带可读降级原因文本（实际为空）。");

                stopped = service.Stop(TimeSpan.FromSeconds(3));
            }

            int liveAfterStop = CountLive(backend.Produced);
            if (BitmapReleaseProbe.Reliable)
            {
                c.Require(liveAfterStop == 0,
                    $"超预算帧被拒绝后仍应被释放，实际还有 {liveAfterStop} 张未释放。");
            }

            // ---- 4) 无处理器直通路径：服务层**自身**的缓冲预算闸门（§6.3 加固项） ----
            // ★ 2026-10-21 加固后语义反转：原先这里只"记录缺口"（服务在 processor == null 时
            //   不查预算，会把后端给出的超限尺寸原样发布）。现在服务层已有**发布前**闸门，
            //   这条路径必须**同样**拒收超限帧 —— 闸门与是否配置 processor 无关。
            FakeCaptureBackend rawBackend = new FakeCaptureBackend { FrameWidth = overWidth, FrameHeight = overHeight };
            long rawPublished;
            long rawCaptureFailures;
            BackdropDegradeReason rawDegradeReason;
            string rawDegradeText;
            bool rawStopped;
            using (BackdropCaptureService rawService = new BackdropCaptureService(rawBackend, processor: null, takeoverTimeoutMs: 20d))
            {
                rawService.Start(CreateRequest(theme: 1, handle: 1, size: 1));
                bool rawFailed = WaitUntil(() => rawService.Metrics.CaptureFailures >= 1, 8000);
                BackdropCaptureStatus rawStatus = rawService.Status;

                rawPublished = rawService.Metrics.FramesPublished;
                rawCaptureFailures = rawService.Metrics.CaptureFailures;
                rawDegradeReason = rawStatus.DegradeReason;
                rawDegradeText = rawStatus.DegradeReasonText ?? "";

                c.Require(rawFailed,
                    "无处理器直通路径没有记录超预算帧的失败（CaptureFailures 仍为 0）——服务层闸门可能未生效。");
                c.Require(rawPublished == 0,
                    $"无处理器直通路径不应发布超限帧，实际发布了 {rawPublished} 帧。");
                c.Require(rawCaptureFailures >= 1,
                    $"无处理器直通路径应计入捕获失败，实际 CaptureFailures={rawCaptureFailures}。");
                c.Require(rawDegradeReason != BackdropDegradeReason.None,
                    $"无处理器直通路径的超预算帧应带降级原因码，实际为 {rawDegradeReason}。");
                c.Require(!string.IsNullOrWhiteSpace(rawDegradeText),
                    "无处理器直通路径的超预算帧应带可读降级原因文本（实际为空）。");

                rawStopped = rawService.Stop(TimeSpan.FromSeconds(3));
            }

            int rawLiveAfterStop = CountLive(rawBackend.Produced);
            if (BitmapReleaseProbe.Reliable)
            {
                c.Require(rawLiveAfterStop == 0,
                    $"无处理器直通路径拒收超限帧后仍应被释放，实际还有 {rawLiveAfterStop} 张未释放。");
            }

            c.Detail("gate", "M2 合同 §9 第 9 条");
            c.Detail("MaxBufferPixels", BackdropImageProcessor.MaxBufferPixels);
            c.Detail("MaxBufferBytes", BackdropImageProcessor.MaxBufferBytes);
            c.Detail("MaxBufferBytesMiB", BackdropImageProcessor.MaxBufferBytes / (1024 * 1024));
            c.Detail("oversizedSource", $"{overWidth}x{overHeight} = {overPixels} 像素（> MaxBufferPixels）");
            c.Detail("processorRejectedOversized", processRejected);
            c.Detail("processorFailureReason", processFailureReason);
            c.Detail("tempBytesBefore", tempBytesBefore);
            c.Detail("tempBytesAfter", tempBytesAfter);
            c.Detail("service.PublishedFrames", published);
            c.Detail("service.CaptureFailures", captureFailures);
            c.Detail("service.DegradeReason", degradeReason.ToString());
            c.Detail("service.DegradeReasonText", degradeText);
            c.Detail("processor.ProcessCalls", processor.ProcessCalls);
            c.Detail("processor.RejectedCalls", processor.RejectedCalls);
            c.Detail("processor.LastSourcePixels", processor.LastSourcePixels);
            c.Detail("liveFramesAfterStop", liveAfterStop);
            c.Detail("stopCompletedWithinTimeout", stopped);
            c.Detail("rawPathWithoutProcessor.PublishedFrames", rawPublished);
            c.Detail("rawPathWithoutProcessor.CaptureFailures", rawCaptureFailures);
            c.Detail("rawPathWithoutProcessor.DegradeReason", rawDegradeReason.ToString());
            c.Detail("rawPathWithoutProcessor.DegradeReasonText", rawDegradeText);
            c.Detail("rawPathWithoutProcessor.LiveFramesAfterStop", rawLiveAfterStop);
            c.Detail("rawPathWithoutProcessor.StopCompletedWithinTimeout", rawStopped);
            c.Detail("rawPathNote",
                "无处理器直通路径（processor == null）现在**同样**被服务层的发布前缓冲预算闸门拦下：" +
                $"实测 published={rawPublished}、CaptureFailures={rawCaptureFailures}、" +
                $"原因码={rawDegradeReason}。闸门与是否配置 processor 无关" +
                "（这正是 2026-10-21 加固项修掉的缺口：原先该路径会把超限尺寸原样发布）。");
            c.Detail("releaseProbeReliable", BitmapReleaseProbe.Reliable);
            c.Detail("releaseProbeNote", BitmapReleaseProbe.Note);
            c.Detail("rule", "MaxBufferPixels == 2000000 且 MaxBufferBytes == 32 MiB；" +
                             "超限源图必须被拒绝（先判后分配）；" +
                             "服务在**配置了**处理器和**未配置**处理器（processor == null）两种情况下，"+
                             "都不得发布超限帧，且被拒绝的帧必须被释放。");
            c.Detail("screenCapturePerformed", false);
            return c;
        }

        // ---------------------------------------------------------------- 辅助

        /// <summary>造一份"玻璃模式"主题（纯内存对象，不读写用户配置）。</summary>
        private static Theme CreateGlassTheme()
        {
            return new Theme
            {
                Name = "AppearanceM2Probe",
                Backdrop = new BackdropConfig
                {
                    Mode = ThemeAppearanceOptions.BackdropModeGlass,
                    Opacity = 0.75,
                    Blur = 12,
                    Noise = 0,
                    EdgeGlow = 0,
                    Fps = 6
                }
            };
        }

        /// <summary>造一份固定样例位图（离屏，不涉及屏幕捕获）。</summary>
        private static Bitmap CreateSampleBitmap(int width, int height)
        {
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(255, 40, 60, 90));
            }
            return bitmap;
        }

        /// <summary>造一份捕获请求（代次可指定）。</summary>
        private static BackdropServiceRequest CreateRequest(int theme, long handle, long size)
        {
            return new BackdropServiceRequest(
                windowHandle: IntPtr.Zero,
                physicalClientBounds: new Rectangle(0, 0, 320, 200),
                dpiScale: 1f,
                userScale: 1f,
                themeGeneration: theme,
                handleGeneration: handle,
                sizeGeneration: size,
                blurPx: 12d,
                targetFps: 6);
        }

        /// <summary>渲染一次背景层并返回状态（离屏画布）。</summary>
        private static BackdropStatus RenderWith(IBackdropFrameSource? source, Image? previewSource)
        {
            ThemeAppearanceOptions options = ThemeAppearanceOptions.Create(
                CreateGlassTheme(), 1f, 1f, new Rectangle(0, 0, 320, 200), 0, null, previewSource);

            using BackdropRenderer renderer = new BackdropRenderer(source);
            using Bitmap canvas = new Bitmap(320, 200, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(canvas);
            return renderer.Render(g, options);
        }

        /// <summary>统计仍然存活的位图数量（假后端产出 - 已释放）。</summary>
        private static int CountLive(IReadOnlyList<Bitmap> produced)
        {
            int live = 0;
            foreach (Bitmap bitmap in produced)
            {
                if (!BitmapReleaseProbe.IsDisposed(bitmap)) live++;
            }
            return live;
        }

        /// <summary>有界等待：条件成立即返回 true；超时返回 false。</summary>
        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(5);
            }
            return condition();
        }

        /// <summary>有界等待接管一帧（成功即返回租约；失败时租约为 null）。</summary>
        private static bool TryAcquireWithTimeout(BackdropCaptureService service, out BackdropFrameLease? lease, int timeoutMs)
        {
            lease = null;
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (service.TryAcquireFrame(out lease) && lease is not null) return true;
                lease = null;
                Thread.Sleep(5);
            }
            return false;
        }

        // ================================================================
        // A15 独立复核（2026-10-21）补的**生命周期时序**回归
        //
        // ★ 为什么单独成组：合同 §9 的十条门槛**都没有覆盖**这四条缺陷 ——
        //   #4 只测"捕获返回前换代"，没测"已接管的 active 帧作废后还能不能借"；
        //   #5 只测 pending 上限，没接管过 active；
        //   #6 用快速假后端测正常 Stop，没测"Stop 超时后重启"与"暂停期间在途帧返回"。
        // ================================================================

        /// <summary>
        /// S1（P1）：Stop 超时后**禁止重启**；旧轮次的帧**不得**发布；不得出现两个捕获循环。
        /// </summary>
        private ProbeCase RunRunIsolationGate()
        {
            ProbeCase c = new ProbeCase { Name = "A15 复核 S1 停止超时后禁止重启且旧轮次不得发布帧" };

            using ManualResetEventSlim entered1 = new(false);
            using ManualResetEventSlim gate1 = new(false);
            using ManualResetEventSlim entered2 = new(false);
            using ManualResetEventSlim gate2 = new(false);

            FakeCaptureBackend backend = new FakeCaptureBackend { FrameWidth = 8, FrameHeight = 8 };
            backend.AddGatedStep(entered1, gate1);
            backend.AddGatedStep(entered2, gate2);

            using BackdropCaptureService service =
                new BackdropCaptureService(backend, processor: null, takeoverTimeoutMs: 20d);
            BackdropServiceRequest request = CreateRequest(theme: 1, handle: 1, size: 1);

            service.Start(request);

            bool entered = entered1.Wait(TimeSpan.FromSeconds(5));
            c.Require(entered, "第一次捕获没有进入后端闸门（服务可能没跑起来）。");
            if (!entered) { service.Stop(TimeSpan.FromSeconds(2)); return c; }

            // ★ 后端卡住时，Stop 必须超时返回 false（不能谎报"已停止"）
            bool stopped = service.Stop(TimeSpan.FromMilliseconds(20));
            c.Require(!stopped, $"后端卡住时 Stop 应报告失败（实际 returned={stopped}）。");

            // ★ 核心断言：用**完全相同**的请求重启必须被拒绝。
            //   代次相同 ⇒ 单靠代次校验区分不出新旧轮次，必须靠轮次身份。
            bool restartRejected = false;
            string restartMessage = "(未被拒绝)";
            try
            {
                service.Start(request);
            }
            catch (InvalidOperationException ex)
            {
                restartRejected = true;
                restartMessage = ex.Message;
            }
            c.Require(restartRejected,
                "Stop 超时后用**完全相同**的请求重启必须被拒绝（否则旧线程与新线程会同时抓屏并交叉发布帧）。");

            // 放行旧捕获，等旧轮次彻底退出
            gate1.Set();
            bool exited = WaitUntil(() => !service.IsRunning, 8000);
            c.Require(exited, "旧轮次在放行后应当退出（IsRunning 仍为 true）。");

            long publishedByOldRun = service.Metrics.FramesPublished;
            c.Require(publishedByOldRun == 0,
                $"已请求停止的旧轮次的帧**不得**被发布（FramesPublished={publishedByOldRun}）。");

            c.Require(backend.MaxConcurrentCalls <= 1,
                $"捕获后端的并发峰值应 ≤ 1（实际 {backend.MaxConcurrentCalls}）—— 峰值 > 1 说明两个捕获循环并存。");

            // 放行第二道闸门（若第二轮真的启动了，避免后台线程一直挂着）
            gate2.Set();

            // ★ 旧轮次退出后必须允许重新启动（服务不能被永久锁死）
            bool restartAfterExitOk = true;
            string restartAfterExitError = "";
            try
            {
                service.Start(request);
            }
            catch (Exception ex)
            {
                restartAfterExitOk = false;
                restartAfterExitError = ex.Message;
            }
            c.Require(restartAfterExitOk,
                $"旧轮次退出后必须允许重新启动（实际报错：{restartAfterExitError}）。");

            if (restartAfterExitOk) service.Stop(TimeSpan.FromSeconds(3));

            c.Detail("source", "A15 独立复核 S1（P1）：Stop 超时后重启导致新旧捕获线程重叠");
            c.Detail("stopReturned", stopped);
            c.Detail("restartRejected", restartRejected);
            c.Detail("restartRejectionMessage", restartMessage);
            c.Detail("oldRunPublishedFrames", publishedByOldRun);
            c.Detail("backendMaxConcurrentCalls", backend.MaxConcurrentCalls);
            c.Detail("backendCaptureCalls", backend.CaptureCalls);
            c.Detail("oldRunExited", exited);
            c.Detail("restartAfterExitOk", restartAfterExitOk);
            c.Detail("rule", "Stop 超时后禁止重启（单后台任务）；旧轮次的帧不得发布；捕获后端并发峰值必须为 1。");
            return c;
        }

        /// <summary>
        /// S2：租约作废（代次变化）/ 暂停 / 停止 / Dispose 之后，当前帧**不得**再被借出，
        /// 且 <c>EffectiveMode</c> 不得再报 Glass。
        /// </summary>
        private ProbeCase RunInvalidatedLeaseGate()
        {
            ProbeCase c = new ProbeCase { Name = "A15 复核 S2 作废/暂停/停止后的帧不得再借出" };

            FakeCaptureBackend backend = new FakeCaptureBackend { FrameWidth = 8, FrameHeight = 8 };
            using BackdropCaptureService service =
                new BackdropCaptureService(backend, processor: null, takeoverTimeoutMs: 20d);

            service.Start(CreateRequest(theme: 1, handle: 1, size: 1));

            BackdropFrameLease? lease = null;
            bool acquired = WaitUntil(() => TryAcquireWithTimeout(service, out lease, 200), 5000);
            c.Require(acquired && lease is not null, "未能接管到第一帧（后台没有产出可用帧）。");
            if (!acquired || lease is null) { service.Stop(TimeSpan.FromSeconds(2)); return c; }

            bool borrowableBefore = service.TryGetFrame(0d, out _);
            c.Require(borrowableBefore, "接管后当前帧应当可以借出。");
            c.Require(service.IsReady, "接管后 IsReady 应当为 true。");
            c.Require(service.Status.EffectiveMode == BackdropRenderMode.Glass,
                $"接管后 EffectiveMode 应当是 Glass（实际 {service.Status.EffectiveMode}）。");

            // ★ 代次变化 ⇒ 租约作废 ⇒ 不得再借出
            service.UpdateRequest(CreateRequest(theme: 2, handle: 1, size: 1));

            bool borrowableAfter = service.TryGetFrame(0d, out _);
            c.Require(!borrowableAfter, "租约因代次变化作废后，当前帧**不得**再被借出。");
            c.Require(!service.IsReady, "租约作废后 IsReady 应当为 false。");
            c.Require(service.Status.EffectiveMode != BackdropRenderMode.Glass,
                $"租约作废后 EffectiveMode 不应是 Glass（实际 {service.Status.EffectiveMode}）。");

            // ★ 暂停 ⇒ 不得借出
            service.SetSuspended(true);
            c.Require(!service.TryGetFrame(0d, out _), "暂停期间当前帧不得被借出。");
            c.Require(!service.IsReady, "暂停期间 IsReady 应当为 false。");
            c.Require(service.Status.EffectiveMode != BackdropRenderMode.Glass,
                $"暂停期间 EffectiveMode 不应是 Glass（实际 {service.Status.EffectiveMode}）。");

            // 恢复后应当能接管到新的一帧并重新可借出
            service.SetSuspended(false);
            BackdropFrameLease? lease2 = null;
            bool acquired2 = WaitUntil(() => TryAcquireWithTimeout(service, out lease2, 200), 5000);
            c.Require(acquired2 && lease2 is not null, "恢复后应当能接管到新的一帧。");
            c.Require(service.TryGetFrame(0d, out _), "恢复并接管后当前帧应当可以借出。");

            // ★ 停止 ⇒ 不得借出，且不再报 Glass
            service.Stop(TimeSpan.FromSeconds(3));
            c.Require(!service.TryGetFrame(0d, out _), "停止后当前帧不得再被借出。");
            c.Require(!service.IsReady, "停止后 IsReady 应当为 false。");
            c.Require(service.Status.EffectiveMode != BackdropRenderMode.Glass,
                $"停止后 EffectiveMode 不应是 Glass（实际 {service.Status.EffectiveMode}）。");

            // ★ Dispose 后仍不得报告 Glass
            service.Dispose();
            c.Require(!service.TryGetFrame(0d, out _), "Dispose 后当前帧不得再被借出。");
            c.Require(!service.IsReady, "Dispose 后 IsReady 应当为 false。");
            c.Require(service.Status.EffectiveMode != BackdropRenderMode.Glass,
                $"Dispose 后 EffectiveMode 不应是 Glass（实际 {service.Status.EffectiveMode}）。");
            c.Require(!service.Status.LiveFrameAvailable, "Dispose 后 LiveFrameAvailable 应当为 false。");

            lease?.Dispose();
            lease2?.Dispose();

            c.Detail("source", "A15 独立复核 S2：已失效的 active 帧仍被借出、Dispose 后仍报告 Glass");
            c.Detail("borrowedBeforeInvalidation", borrowableBefore);
            c.Detail("borrowedAfterInvalidation", borrowableAfter);
            c.Detail("rule", "借用与状态共用同一个可用性判定：租约有效 + 服务未 Dispose + 未暂停 + 当前轮未请求停止。");
            return c;
        }

        /// <summary>
        /// S3：暂停期间，已经进入后端的**在途帧**返回后不得发布、不得被接管，
        /// 且不得把状态改回 Running。
        /// </summary>
        private ProbeCase RunSuspendedInFlightGate()
        {
            ProbeCase c = new ProbeCase { Name = "A15 复核 S3 暂停期间的在途帧不得发布" };

            using ManualResetEventSlim entered = new(false);
            using ManualResetEventSlim gate = new(false);

            FakeCaptureBackend backend = new FakeCaptureBackend { FrameWidth = 8, FrameHeight = 8 };
            backend.AddGatedStep(entered, gate);

            using BackdropCaptureService service =
                new BackdropCaptureService(backend, processor: null, takeoverTimeoutMs: 20d);
            service.Start(CreateRequest(theme: 1, handle: 1, size: 1));

            bool inFlight = entered.Wait(TimeSpan.FromSeconds(5));
            c.Require(inFlight, "捕获没有进入后端闸门（无法复现'在途帧'）。");
            if (!inFlight) { service.Stop(TimeSpan.FromSeconds(2)); return c; }

            // ★ 暂停（此时那一帧还在后端里）
            service.SetSuspended(true);
            c.Require(service.Status.State == BackdropServiceState.Suspended,
                $"暂停后状态应为 Suspended（实际 {service.Status.State}）。");

            // ★ 放行在途捕获
            gate.Set();

            bool processed = WaitUntil(() => service.Metrics.FramesCaptured >= 1, 5000);
            c.Require(processed, "在途捕获返回后后台没有完成记账（服务可能没跑起来）。");
            Thread.Sleep(200);

            c.Require(service.PendingFrameCount == 0,
                $"暂停期间在途帧**不得**进入 pending（实际 {service.PendingFrameCount}）。");
            c.Require(service.Metrics.FramesPublished == 0,
                $"暂停期间在途帧**不得**被发布（FramesPublished={service.Metrics.FramesPublished}）。");

            bool acquired = service.TryAcquireFrame(out BackdropFrameLease? lease);
            c.Require(!acquired, "暂停期间不得接管到任何帧。");
            lease?.Dispose();

            c.Require(service.Status.State == BackdropServiceState.Suspended,
                $"暂停期间状态不得被改回 Running（实际 {service.Status.State}）。");

            service.Stop(TimeSpan.FromSeconds(3));

            c.Detail("source", "A15 独立复核 S3：暂停后，在途结果能重新发布并将 Suspended 改回 Running");
            c.Detail("pendingWhileSuspended", service.PendingFrameCount);
            c.Detail("publishedWhileSuspended", service.Metrics.FramesPublished);
            c.Detail("acquiredWhileSuspended", acquired);
            c.Detail("finalState", service.Status.State.ToString());
            c.Detail("rule", "发布与接管的同步边界必须检查暂停/停止状态；暂停期间的在途结果一律丢弃并释放。");
            return c;
        }

        /// <summary>
        /// S4：捕获从失败**恢复成功**后，状态说明必须同步更新（不能仍是失败时的文案）；
        /// 历史失败原因仍保留在 <c>LastFailureReason</c>。
        /// </summary>
        private ProbeCase RunRecoveryStateTextGate()
        {
            ProbeCase c = new ProbeCase { Name = "A15 复核 S4 失败恢复后状态说明必须同步" };

            FakeCaptureBackend backend = new FakeCaptureBackend
            {
                FrameWidth = 8,
                FrameHeight = 8,
                FailCapture = true
            };

            using BackdropCaptureService service =
                new BackdropCaptureService(backend, processor: null, takeoverTimeoutMs: 20d);
            service.Start(CreateRequest(theme: 1, handle: 1, size: 1));

            bool failed = WaitUntil(() => service.Metrics.CaptureFailures >= 1, 8000);
            c.Require(failed, "没有记录到捕获失败（后端应先失败一次）。");
            if (!failed) { service.Stop(TimeSpan.FromSeconds(2)); return c; }

            BackdropCaptureStatus failedStatus = service.Status;
            string failureText = failedStatus.StateText ?? "";
            c.Require(!string.IsNullOrWhiteSpace(failureText), "失败后 StateText 应当有可读说明。");
            c.Require(failedStatus.State == BackdropServiceState.Faulted,
                $"捕获失败后状态应为 Faulted（实际 {failedStatus.State}）。");

            // ★ 恢复：后端不再失败 ⇒ 下一轮成功（注意失败退避，最长等 15 秒）
            backend.FailCapture = false;
            bool recovered = WaitUntil(() => service.Status.State == BackdropServiceState.Running, 15000);
            c.Require(recovered, $"后端恢复后状态应回到 Running（实际 {service.Status.State}）。");

            BackdropCaptureStatus okStatus = service.Status;
            c.Require(okStatus.DegradeReason == BackdropDegradeReason.None,
                $"恢复后不应再有降级原因码（实际 {okStatus.DegradeReason}）。");
            c.Require(!string.IsNullOrWhiteSpace(okStatus.StateText), "恢复后 StateText 不应为空。");
            c.Require(okStatus.StateText != failureText,
                $"恢复后 StateText 必须更新（仍显示失败时的文案：\"{failureText}\"）。");
            c.Require(!string.IsNullOrWhiteSpace(okStatus.LastFailureReason),
                "历史失败原因应保留在 LastFailureReason。");

            service.Stop(TimeSpan.FromSeconds(3));

            c.Detail("source", "A15 独立复核 S4：捕获恢复成功后，StateText 继续显示旧故障");
            c.Detail("failureStateText", failureText);
            c.Detail("recoveredStateText", okStatus.StateText ?? "");
            c.Detail("recoveredDegradeReason", okStatus.DegradeReason.ToString());
            c.Detail("lastFailureReason", okStatus.LastFailureReason ?? "");
            c.Detail("rule", "成功转换时同步设置当前状态说明；历史故障只留在 LastFailureReason。");
            return c;
        }

        /// <summary>把一次渲染场景整理成证据条目。</summary>
        private static JsonNode ScenarioJson(string label, BackdropStatus status, int backendCalls)
        {
            return new JsonObject
            {
                ["scenario"] = label,
                ["Source"] = status.Source.ToString(),
                ["EffectiveMode"] = status.EffectiveMode.ToString(),
                ["IsDegraded"] = status.IsDegraded,
                ["ReasonCode"] = status.ReasonCode.ToString(),
                ["Reason"] = status.Reason ?? "(null)",
                ["backendTryGetFrameCalls"] = backendCalls
            };
        }
    }
}
