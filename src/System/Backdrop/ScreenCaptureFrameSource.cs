using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiteMonitor.src.SystemServices.Backdrop
{
    // ================================================================
    // 实时背景捕获后端（屏幕捕获 + 瞬时排除）
    // ================================================================

    /// <summary>
    /// 捕获请求快照（**不可变**）。
    ///
    /// ★ 合同要点（合同 §1 / §3）：
    /// 1. 这是一份**请求快照**，不是可变对象；发起时把三个代次一起带上。
    /// 2. <see cref="BlurPx"/> 是**物理像素**的 σ（生产链路上直接取 <c>options.BlurPx</c>），
    ///    捕获请求**不再乘缩放**（合同 §11）。
    /// 3. <see cref="ScreenBounds"/> 是客户区在**屏幕坐标系**里的物理矩形，
    ///    允许**负坐标**（副屏在主屏左侧时就是负的，A06 实测通过）。
    /// </summary>
    public readonly struct BackdropCaptureRequest
    {
        /// <summary>构造一份捕获请求快照。</summary>
        /// <param name="windowHandle">被捕获窗口的顶层句柄（用于施加显示亲和性）。</param>
        /// <param name="screenBounds">客户区在屏幕坐标系里的物理矩形（可含负坐标）。</param>
        /// <param name="deviceDpi">窗口的实际 DPI（96 = 100%）。</param>
        /// <param name="userScale">用户缩放倍率。</param>
        /// <param name="blurPx">模糊半径（物理像素 σ，取 <c>options.BlurPx</c>）。</param>
        /// <param name="captureScale">捕获降采样比例（A06 生产建议 0.25）。</param>
        /// <param name="fps">捕获帧率（合同 §6.3：默认 2 fps / 上限 6 fps）。</param>
        /// <param name="themeGeneration">主题代次。</param>
        /// <param name="handleGeneration">窗口句柄代次。</param>
        /// <param name="sizeGeneration">尺寸代次。</param>
        public BackdropCaptureRequest(
            IntPtr windowHandle,
            Rectangle screenBounds,
            int deviceDpi,
            double userScale,
            double blurPx,
            double captureScale,
            int fps,
            int themeGeneration,
            int handleGeneration,
            int sizeGeneration)
        {
            WindowHandle = windowHandle;
            ScreenBounds = screenBounds;
            DeviceDpi = deviceDpi;
            UserScale = userScale;
            BlurPx = blurPx;
            CaptureScale = captureScale;
            Fps = fps;
            ThemeGeneration = themeGeneration;
            HandleGeneration = handleGeneration;
            SizeGeneration = sizeGeneration;
        }

        /// <summary>被捕获窗口的顶层句柄。</summary>
        public IntPtr WindowHandle { get; }

        /// <summary>客户区在屏幕坐标系里的物理矩形（**可含负坐标**）。</summary>
        public Rectangle ScreenBounds { get; }

        /// <summary>窗口的实际 DPI（96 = 100%）。</summary>
        public int DeviceDpi { get; }

        /// <summary>用户缩放倍率。</summary>
        public double UserScale { get; }

        /// <summary>模糊半径（**物理像素** σ，等于 <c>options.BlurPx</c>）。</summary>
        public double BlurPx { get; }

        /// <summary>捕获降采样比例（0 &lt; s ≤ 1）。</summary>
        public double CaptureScale { get; }

        /// <summary>捕获帧率（1–6）。</summary>
        public int Fps { get; }

        /// <summary>主题代次。</summary>
        public int ThemeGeneration { get; }

        /// <summary>窗口句柄代次。</summary>
        public int HandleGeneration { get; }

        /// <summary>尺寸代次。</summary>
        public int SizeGeneration { get; }

        /// <summary>合成比例 = DPI 比例 × 用户缩放。</summary>
        public double EffectiveScale => (DeviceDpi > 0 ? DeviceDpi / 96.0 : 1.0) * (UserScale > 0 ? UserScale : 1.0);

        /// <summary>请求是否可用。</summary>
        public bool IsValid
        {
            get
            {
                if (WindowHandle == IntPtr.Zero) return false;
                if (ScreenBounds.Width <= 0 || ScreenBounds.Height <= 0) return false;
                if (Fps <= 0) return false;
                if (CaptureScale <= 0 || CaptureScale > 1) return false;
                return true;
            }
        }

        /// <summary>请求不可用的原因（人类可读）；可用时为空串。</summary>
        public string InvalidReason
        {
            get
            {
                if (WindowHandle == IntPtr.Zero) return "窗口句柄为空。";
                if (ScreenBounds.Width <= 0 || ScreenBounds.Height <= 0)
                    return $"客户区矩形无效（{ScreenBounds.Width}×{ScreenBounds.Height}）。";
                if (Fps <= 0) return $"帧率无效（{Fps}）。";
                if (CaptureScale <= 0 || CaptureScale > 1) return $"降采样比例无效（{CaptureScale}）。";
                return string.Empty;
            }
        }
    }

    /// <summary>捕获后端状态（用于诊断与降级原因）。</summary>
    public enum BackdropCaptureState
    {
        /// <summary>未启动。</summary>
        Stopped = 0,

        /// <summary>正在捕获。</summary>
        Running = 1,

        /// <summary>捕获请求无效。</summary>
        InvalidRequest = 2,

        /// <summary>捕获失败（可诊断）。</summary>
        CaptureFailed = 3,

        /// <summary>窗口句柄已失效（已停止该句柄的捕获并恢复显示亲和性）。</summary>
        HandleInvalid = 4,

        /// <summary>已取消 / 已停止。</summary>
        Cancelled = 5
    }

    /// <summary>
    /// 一帧的代次戳（合同 §3）。
    /// <para>★ 帧携带它被处理时的三个代次；UI 侧比对当前代次，
    /// 不匹配的帧必须丢弃（合同 §3.1 第 4 条）。</para>
    /// </summary>
    public readonly struct BackdropFrameStamp
    {
        /// <summary>构造一个代次戳。</summary>
        public BackdropFrameStamp(int themeGeneration, int handleGeneration, int sizeGeneration, int width, int height, double blurPx)
        {
            ThemeGeneration = themeGeneration;
            HandleGeneration = handleGeneration;
            SizeGeneration = sizeGeneration;
            Width = width;
            Height = height;
            BlurPx = blurPx;
        }

        /// <summary>主题代次。</summary>
        public int ThemeGeneration { get; }

        /// <summary>窗口句柄代次。</summary>
        public int HandleGeneration { get; }

        /// <summary>尺寸代次。</summary>
        public int SizeGeneration { get; }

        /// <summary>帧的物理宽度（像素）。</summary>
        public int Width { get; }

        /// <summary>帧的物理高度（像素）。</summary>
        public int Height { get; }

        /// <summary>该帧实际使用的模糊半径（物理像素 σ）。</summary>
        public double BlurPx { get; }

        /// <summary>该帧的代次是否仍与请求一致（一致才允许交给 UI）。</summary>
        /// <param name="request">当前捕获请求。</param>
        public bool Matches(in BackdropCaptureRequest request)
        {
            return ThemeGeneration == request.ThemeGeneration
                && HandleGeneration == request.HandleGeneration
                && SizeGeneration == request.SizeGeneration;
        }
    }

    /// <summary>
    /// 单帧抓屏的分段耗时（**经过时间**口径：墙钟，含等待与抢占）。
    /// <para>★ <see cref="ExcludeWindowMs"/> 是 M2 合同 §6.2 要求**并列记录**的两段之一
    /// （瞬时排除窗口的时长：排除属性生效期间无法捕获）。</para>
    /// </summary>
    public readonly struct BackdropCaptureTiming
    {
        /// <summary>构造一份抓屏耗时。</summary>
        public BackdropCaptureTiming(double captureMs, double excludeWindowMs, int frameWidth, int frameHeight)
        {
            CaptureMs = captureMs;
            ExcludeWindowMs = excludeWindowMs;
            FrameWidth = frameWidth;
            FrameHeight = frameHeight;
        }

        /// <summary>抓屏耗时（毫秒，经过时间口径）。</summary>
        public double CaptureMs { get; }

        /// <summary>瞬时排除窗口的时长（毫秒，经过时间口径）。</summary>
        public double ExcludeWindowMs { get; }

        /// <summary>抓到的帧宽（像素，物理）。</summary>
        public int FrameWidth { get; }

        /// <summary>抓到的帧高（像素，物理）。</summary>
        public int FrameHeight { get; }
    }

    /// <summary>
    /// 捕获分段计量（合同 §6.2）。
    ///
    /// ★ 两种口径严格分开（合同 §6.1）：
    /// 1. <see cref="CaptureMs"/> / <see cref="FilterMs"/> / <see cref="PublishMs"/> /
    ///    <see cref="WaitUiMs"/> / <see cref="ExcludeWindowMs"/> / <see cref="ElapsedMs"/> /
    ///    <see cref="ElapsedMsP95"/> 都是**经过时间**（墙钟，含等待与抢占）→ 延迟判据。
    /// 2. <see cref="CpuMsTotal"/> 是**CPU 时间**（进程处理器时间）→ CPU 预算。
    ///
    /// ★ <see cref="WaitUiMs"/> 与 <see cref="ExcludeWindowMs"/> **并列记录**：
    ///   它们是"后台占用"里最容易漏掉、也最容易失控的两段（合同 §6.2）。
    /// </summary>
    public readonly struct BackdropCaptureMetrics
    {
        /// <summary>构造一份捕获计量。</summary>
        public BackdropCaptureMetrics(
            bool success,
            string failureReason,
            double captureMs,
            double filterMs,
            double publishMs,
            double waitUiMs,
            double excludeWindowMs,
            double cpuMsTotal,
            double elapsedMs,
            double elapsedMsP95,
            int frameWidth,
            int frameHeight,
            int boxRadius,
            double sigmaEffectiveDisplayPx,
            long tempPeakBytes,
            int frameCount)
        {
            Success = success;
            FailureReason = failureReason;
            CaptureMs = captureMs;
            FilterMs = filterMs;
            PublishMs = publishMs;
            WaitUiMs = waitUiMs;
            ExcludeWindowMs = excludeWindowMs;
            CpuMsTotal = cpuMsTotal;
            ElapsedMs = elapsedMs;
            ElapsedMsP95 = elapsedMsP95;
            FrameWidth = frameWidth;
            FrameHeight = frameHeight;
            BoxRadius = boxRadius;
            SigmaEffectiveDisplayPx = sigmaEffectiveDisplayPx;
            TempPeakBytes = tempPeakBytes;
            FrameCount = frameCount;
        }

        /// <summary>最近一次捕获是否成功。</summary>
        public bool Success { get; }

        /// <summary>失败原因（人类可读）；成功时为空串。</summary>
        public string FailureReason { get; }

        /// <summary>捕获耗时（**经过时间**，含后端自身开销）。</summary>
        public double CaptureMs { get; }

        /// <summary>降采样 + 物理模糊半径滤波耗时（**经过时间**）。</summary>
        public double FilterMs { get; }

        /// <summary>发布候选帧耗时（**经过时间**，含代次校验准备）。</summary>
        public double PublishMs { get; }

        /// <summary>**等待 UI 接管**的时长（**经过时间**）。</summary>
        public double WaitUiMs { get; }

        /// <summary>**瞬时排除窗口**的时长（**经过时间**；排除属性生效期间无法捕获）。</summary>
        public double ExcludeWindowMs { get; }

        /// <summary>本帧合计的**处理器时间**（CPU 口径；单帧数值只作指示，判定预算需批量测量）。</summary>
        public double CpuMsTotal { get; }

        /// <summary>本帧端到端**经过时间**（捕获 + 滤波 + 发布）。</summary>
        public double ElapsedMs { get; }

        /// <summary>最近若干帧端到端经过时间的 **p95**（窗口 64 帧）。</summary>
        public double ElapsedMsP95 { get; }

        /// <summary>已发布帧的物理宽度（像素）。</summary>
        public int FrameWidth { get; }

        /// <summary>已发布帧的物理高度（像素）。</summary>
        public int FrameHeight { get; }

        /// <summary>实际使用的 box 半径 r。</summary>
        public int BoxRadius { get; }

        /// <summary>折算回显示尺寸的等效标准差（物理像素）。</summary>
        public double SigmaEffectiveDisplayPx { get; }

        /// <summary>最近一帧的临时位图峰值字节数。</summary>
        public long TempPeakBytes { get; }

        /// <summary>累计成功发布的帧数。</summary>
        public int FrameCount { get; }
    }

    /// <summary>
    /// 实时背景捕获后端：屏幕捕获 + <c>WDA_EXCLUDEFROMCAPTURE</c> 瞬时排除。
    ///
    /// ★ 技术路线（A06 裁决，唯一放行路线）：
    /// 1. 抓屏走 **GDI+ 的 <c>Graphics.CopyFromScreen</c>**（A06 实测：桌面 DC BitBlt(SRCCOPY)、
    ///    BitBlt|CAPTUREBLT、CopyFromScreen 三种屏幕捕获后端在 EXCLUDEFROMCAPTURE 下
    ///    **像素级完全一致**，与参考图差 0.00；本实现取其中最简单的一种，
    ///    并用 24bpp 目标位图避免 alpha 不确定导致整帧发黑）。
    /// 2. 抓屏期间用 <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c> **瞬时排除**本窗口，
    ///    抓完立即还原 —— 避免"自己抓到自己"的自反馈（A06 §4.6 记录的自反馈会造成亮度递归放大）。
    /// 3. **DWM 系统材质（Mica/Acrylic）路线已在 A06 实测失败，本类不提供任何 DWM 入口。**
    ///
    /// ★ 合同要点：
    /// 1. <see cref="IsPreview"/> **恒为 false**：本类提供的是**真实捕获帧**，
    ///    诊断里必须报 <c>LiveFrame</c>（合同 §2.2 第 1 条）。
    /// 2. **绘制路径绝不抓屏**：抓屏只发生在 <see cref="Start"/> 之后的后台线程里；
    ///    <see cref="TryGetFrame"/> 只读"当前这一帧"，绝不启动抓屏（合同 §5.2 第 5 条红线）。
    /// 3. **所有权**：本类是**生产者**，自己创建、自己管理的中间位图由自己释放；
    ///    <see cref="TryGetFrame"/> 返回的帧是**借用**引用，调用方与渲染器都**不得**释放
    ///    （合同 §4.4）。
    /// 4. **pending 最多一份**：新帧发布前先释放旧的 pending，不允许堆积（合同 §4.2 第 5 条）。
    /// 5. **取消/失败必须恢复显示亲和性**：否则窗口会在用户录屏里永久消失（合同 §5.2 第 3 条）。
    /// 6. **代次**：帧携带它被处理时的三个代次；代次不匹配的帧由本类自己回收，不交给 UI。
    /// </summary>
    public sealed class ScreenCaptureFrameSource : IBackdropFrameSource, IDisposable
    {
        /// <summary>默认捕获降采样比例（A06 §8.2 生产建议 0.25）。</summary>
        public const double DefaultCaptureScale = 0.25;

        /// <summary>停止时等待后台线程退出的上限（毫秒）；超时后仍会强制恢复显示亲和性。</summary>
        private const int StopTimeoutMs = 2000;

        /// <summary>p95 计算窗口（帧数）。</summary>
        private const int ElapsedWindowSize = 64;

        // ---- 状态 ----
        private readonly object _sync = new();        // 保护请求 / 状态 / 生命周期
        private readonly object _pendingLock = new(); // 保护 pending / active 位图

        private CancellationTokenSource? _cts;
        private Thread? _worker;
        private BackdropCaptureRequest _request;
        private int _state = (int)BackdropCaptureState.Stopped;
        private string _unavailableReason = "捕获服务尚未启动。";
        private bool _disposed;
        private volatile bool _stopRequested;

        // ---- 帧交接（pending → active）----
        private Bitmap? _pending;          // 后台线程拥有；尚未交给 UI
        private BackdropFrameStamp _pendingStamp;
        private long _pendingPublishedTicks;
        private Bitmap? _active;           // 已被 UI 取用（**借用**引用；本类负责释放）
        private BackdropFrameStamp _activeStamp;

        // ---- 计量 ----
        private readonly double[] _elapsedSamples = new double[ElapsedWindowSize];
        private int _elapsedCount;
        private int _elapsedIndex;
        private readonly MetricsSlot _metrics = new MetricsSlot();

        // ================================================================
        // IBackdropFrameSource
        // ================================================================

        /// <summary>后端是否已就绪（正在捕获且最近一次捕获没有失败）。</summary>
        public bool IsReady => Volatile.Read(ref _state) == (int)BackdropCaptureState.Running;

        /// <summary>
        /// 未就绪原因（人类可读）。<see cref="IsReady"/> 为 false 时必定非空，
        /// 该文本会进入降级诊断信息。
        /// </summary>
        public string UnavailableReason
        {
            get
            {
                string reason = Volatile.Read(ref _unavailableReason);
                return string.IsNullOrEmpty(reason) ? "实时背景后端不可用。" : reason;
            }
        }

        /// <summary>
        /// ★ 恒为 false：本类提供的是**真实捕获帧**。
        /// <para>合同 §2.2 第 1 条：真实捕获后端必须返回 false，
        /// 否则诊断会把实时帧误记成 <c>PreviewFrame</c>。</para>
        /// </summary>
        public bool IsPreview => false;

        /// <summary>
        /// 取当前帧（**借用**引用，调用方与渲染器都**不得释放**）。
        /// <para>★ 只读当前已发布的帧，**绝不**在这里启动抓屏（合同 §5.2 第 5 条红线）。</para>
        /// <para>★ 必须在 **UI 线程**调用：本类会在此刻释放上一帧，
        /// 只有在 UI 线程、且此刻没有绘制正在使用旧帧时释放才是安全的。</para>
        /// </summary>
        /// <param name="blurPx">要求的模糊半径（物理像素，来自 <c>options.BlurPx</c>）。</param>
        /// <param name="frame">取到的帧（借用，调用方不得释放）；取不到时为 null。</param>
        /// <returns>取到有效帧时为 true。</returns>
        public bool TryGetFrame(double blurPx, out Image? frame)
        {
            frame = null;
            if (_disposed) return false;

            LastRequestedBlurPx = blurPx;

            BackdropCaptureRequest request;
            lock (_sync) { request = _request; }

            // ★ 接管 pending（若代次已过期则由本类自己回收，不交给 UI）
            AdoptPending(request);

            lock (_pendingLock)
            {
                if (_active == null) return false;

                // ★ 代次比对：active 帧的代次必须仍与当前请求一致（合同 §3.1 第 4 条）
                if (!_activeStamp.Matches(request))
                {
                    _active.Dispose();
                    _active = null;
                    return false;
                }

                frame = _active;   // ★ 借用引用：调用方与渲染器都不得释放
                return true;
            }
        }

        // ================================================================
        // 生命周期
        // ================================================================

        /// <summary>
        /// 启动捕获（后台线程）。
        /// <para>★ 由接线方在**确定要开实时玻璃**时调用；绝不在 Paint 里调用。</para>
        /// </summary>
        /// <param name="request">捕获请求快照。</param>
        public void Start(BackdropCaptureRequest request)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ScreenCaptureFrameSource));
                if (_worker != null) throw new InvalidOperationException("捕获服务已在运行；请先调用 Stop()。");

                _request = request;
                _cts = new CancellationTokenSource();
                _stopRequested = false;
                SetStateLocked(BackdropCaptureState.Running, string.Empty);

                _worker = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Name = "LiteMonitor 背景捕获",
                };
                _worker.Start();
            }
        }

        /// <summary>
        /// 更新捕获请求（尺寸 / 句柄 / DPI / 模糊半径变化时）。
        /// <para>★ 代次变化后，**旧代次的帧会被自动丢弃**（本类在取帧时比对代次）。</para>
        /// </summary>
        /// <param name="request">新的捕获请求快照。</param>
        public void UpdateRequest(BackdropCaptureRequest request)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ScreenCaptureFrameSource));
                _request = request;
            }
        }

        /// <summary>
        /// 停止捕获并释放资源：取消 → 有上限地等待后台退出 → **恢复显示亲和性** → 释放 pending。
        /// <para>★ 取消必须可等待但有上限，不阻塞 UI 无限等（合同 §5.2 第 2 条）。</para>
        /// </summary>
        public void Stop()
        {
            CancellationTokenSource? cts;
            Thread? worker;
            IntPtr hwnd;

            lock (_sync)
            {
                cts = _cts;
                worker = _worker;
                hwnd = _request.WindowHandle;
                _cts = null;
                _worker = null;
            }

            _stopRequested = true;

            try { cts?.Cancel(); } catch { /* 取消失败不影响收尾 */ }

            if (worker != null && worker.IsAlive)
            {
                if (!worker.Join(StopTimeoutMs))
                {
                    // ★ 后台线程没在上限内退出：仍然强制恢复显示亲和性，
                    //   否则窗口会在用户录屏里永久消失（合同 §5.2 第 3 条）。
                    RestoreAffinity(hwnd);
                }
            }

            cts?.Dispose();

            // pending 由本类拥有：停止后直接释放（active 留给 UI 线程释放或 Dispose 释放）
            lock (_pendingLock)
            {
                _pending?.Dispose();
                _pending = null;
            }

            SetState(BackdropCaptureState.Stopped, "捕获服务已停止。");
        }

        /// <summary>
        /// 释放本类持有的全部资源。
        /// <para>★ 必须在 **UI 线程**、且确认没有绘制正在进行时调用。</para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            Stop();

            lock (_pendingLock)
            {
                _active?.Dispose();
                _active = null;
            }

            _disposed = true;
        }

        // ================================================================
        // 诊断
        // ================================================================

        /// <summary>当前状态。</summary>
        public BackdropCaptureState State => (BackdropCaptureState)Volatile.Read(ref _state);

        /// <summary>
        /// 当前进程的 DPI 感知等级（诊断用）。
        /// <para>1 = 不感知（坐标会被虚拟化，抓屏会错位）；2 = 系统感知；3 = 每显示器感知（期望值）。</para>
        /// </summary>
        public int DpiAwareness => BackdropCaptureNative.GetDpiAwareness();

        /// <summary>最近一次捕获的分段计量（快照）。</summary>
        public BackdropCaptureMetrics LastMetrics
        {
            get
            {
                lock (_metrics)
                {
                    return new BackdropCaptureMetrics(
                        _metrics.Success,
                        _metrics.FailureReason,
                        _metrics.CaptureMs,
                        _metrics.FilterMs,
                        _metrics.PublishMs,
                        _metrics.WaitUiMs,
                        _metrics.ExcludeWindowMs,
                        _metrics.CpuMsTotal,
                        _metrics.ElapsedMs,
                        ComputeP95(),
                        _metrics.FrameWidth,
                        _metrics.FrameHeight,
                        _metrics.BoxRadius,
                        _metrics.SigmaEffectiveDisplayPx,
                        _metrics.TempPeakBytes,
                        _metrics.FrameCount);
                }
            }
        }

        /// <summary>最近一次 <see cref="TryGetFrame"/> 传入的模糊半径（物理像素）。</summary>
        public double LastRequestedBlurPx { get; private set; }

        /// <summary>
        /// 当前请求的模糊半径与已发布帧的模糊半径是否一致。
        /// <para>不一致说明上层改了请求但新帧还没产出（诊断用；不影响当前帧的可用性）。</para>
        /// </summary>
        public bool LastBlurMismatch
        {
            get
            {
                BackdropCaptureRequest request;
                lock (_sync) { request = _request; }
                return Math.Abs(request.BlurPx - LastRequestedBlurPx) > 0.01
                    && Math.Abs(_activeStamp.BlurPx - LastRequestedBlurPx) > 0.01;
            }
        }

        /// <summary>取客户区在屏幕坐标系里的物理矩形（可含负坐标）。</summary>
        /// <param name="hwnd">顶层窗口句柄。</param>
        /// <param name="bounds">成功时为客户区的屏幕矩形。</param>
        /// <returns>取到有效矩形时为 true。</returns>
        public static bool TryGetClientScreenBounds(IntPtr hwnd, out Rectangle bounds)
        {
            return BackdropCaptureNative.TryGetClientScreenBounds(hwnd, out bounds);
        }

        // ================================================================
        // 后台捕获循环
        // ================================================================

        private void CaptureLoop()
        {
            CancellationToken token;
            lock (_sync) { token = _cts?.Token ?? new CancellationToken(); }

            int failureStreak = 0;

            try
            {
                while (!token.IsCancellationRequested && !_stopRequested)
                {
                    BackdropCaptureRequest request;
                    lock (_sync) { request = _request; }

                    if (!request.IsValid)
                    {
                        SetState(BackdropCaptureState.InvalidRequest, "捕获请求无效：" + request.InvalidReason);
                        break;
                    }

                    if (!BackdropCaptureNative.IsWindow(request.WindowHandle))
                    {
                        // ★ 句柄失效：停止该句柄的捕获并恢复显示亲和性（合同 §5.1）
                        RestoreAffinity(request.WindowHandle);
                        SetState(BackdropCaptureState.HandleInvalid, "窗口句柄已失效，捕获已停止。");
                        break;
                    }

                    // ★ 窗口隐藏 / 最小化 ⇒ 暂停捕获（合同 §5.1）。
                    //   恢复可见后自然继续，**不补历史帧、不追帧**。
                    if (!BackdropCaptureNative.IsWindowVisible(request.WindowHandle))
                    {
                        int pauseMs = (int)Math.Round(1000.0 / Math.Max(1, request.Fps));
                        if (token.WaitHandle.WaitOne(pauseMs)) break;
                        continue;
                    }

                    long tickStart = Stopwatch.GetTimestamp();
                    double cpu0 = CurrentCpuMs();

                    FrameOutcome outcome;
                    try
                    {
                        outcome = CaptureAndProcess(request, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        outcome = FrameOutcome.Failed("捕获异常：" + ex.Message);
                    }

                    double cpuTotal = CurrentCpuMs() - cpu0;

                    if (outcome.Bitmap == null)
                    {
                        failureStreak++;
                        SetState(BackdropCaptureState.CaptureFailed,
                            string.IsNullOrEmpty(outcome.FailureReason) ? "捕获失败。" : outcome.FailureReason);

                        // ★ 受控重试：指数退避，**不在每次 Paint 里无限重试**（合同 §5.2 第 4 条）
                        int backoff = (int)Math.Min(2000, 200 * Math.Pow(2, Math.Min(failureStreak - 1, 4)));
                        if (token.WaitHandle.WaitOne(backoff)) break;
                        continue;
                    }

                    failureStreak = 0;
                    SetState(BackdropCaptureState.Running, string.Empty);

                    // ★ 发布：pending 最多一份（发布新帧前先释放旧的）
                    long publishStart = Stopwatch.GetTimestamp();
                    bool published = Publish(outcome.Bitmap!, outcome.Stamp);
                    double publishMs = ElapsedMsSince(publishStart);

                    double elapsed = ElapsedMsSince(tickStart);
                    RecordMetrics(outcome, publishMs, cpuTotal, elapsed);

                    if (!published)
                    {
                        // 停止过程中：帧不发布，直接释放
                        outcome.Bitmap!.Dispose();
                    }

                    // 等待下一帧（扣掉本帧已用时间，维持目标帧率）
                    int intervalMs = (int)Math.Round(1000.0 / Math.Max(1, request.Fps));
                    int wait = (int)Math.Max(0, intervalMs - ElapsedMsSince(tickStart));
                    if (token.WaitHandle.WaitOne(wait)) break;
                }
            }
            finally
            {
                // ★ 退出前必须恢复显示亲和性（否则窗口在录屏里永久消失）
                BackdropCaptureRequest request;
                lock (_sync) { request = _request; }
                RestoreAffinity(request.WindowHandle);

                SetState(BackdropCaptureState.Cancelled, "捕获服务已停止。");
            }
        }

        // 一次"抓屏 + 处理"的完整过程
        private FrameOutcome CaptureAndProcess(in BackdropCaptureRequest request, CancellationToken token)
        {
            // ---- 抓屏（含瞬时排除）----
            Image? capturedImage;
            BackdropCaptureTiming timing;
            string? captureError;
            if (!TryCaptureFrame(request, out capturedImage, out timing, out captureError) || capturedImage == null)
            {
                return FrameOutcome.Failed(captureError ?? "捕获失败。");
            }

            Bitmap captured = (Bitmap)capturedImage;

            try
            {
                token.ThrowIfCancellationRequested();
            }
            catch
            {
                captured.Dispose();
                throw;
            }

            // ---- 降采样 + 物理模糊半径滤波 ----
            var contract = BackdropBlurContract.Create(request.BlurPx, request.CaptureScale);
            Bitmap? processed = BackdropImageProcessor.Process(captured, contract, out BackdropProcessMetrics filterMetrics);

            // ★ 中间位图由生产者侧释放（合同 §4.2 第 6 条：失败路径必须释放）
            captured.Dispose();

            if (processed == null)
            {
                return FrameOutcome.Failed(filterMetrics.FailureReason ?? "图像处理失败。");
            }

            var stamp = new BackdropFrameStamp(
                request.ThemeGeneration,
                request.HandleGeneration,
                request.SizeGeneration,
                processed.Width,
                processed.Height,
                request.BlurPx);

            return new FrameOutcome
            {
                Bitmap = processed,
                Stamp = stamp,
                CaptureMs = timing.CaptureMs,
                FilterMs = filterMetrics.FilterElapsedMs,
                FilterCpuMs = filterMetrics.FilterCpuMs,
                ExcludeWindowMs = timing.ExcludeWindowMs,
                BoxRadius = contract.BoxRadius,
                SigmaEffectiveDisplayPx = contract.SigmaEffectiveDisplayPx,
                TempPeakBytes = filterMetrics.PeakTempBytes,
            };
        }

        /// <summary>
        /// 抓取**一帧**屏幕内容（不启动线程、不做滤波）。
        ///
        /// ★ 所有权：成功时 <paramref name="frame"/> 是**调用方的新所有权**（必须释放）；
        ///   失败时为 null，且不遗留任何位图。
        /// ★ 瞬时排除：抓屏期间把窗口排除出捕获，抓完立即还原（失败/异常也会还原）。
        /// ★ 这是给"自建捕获服务"用的**单帧原语**：服务层自己决定何时调用、何时取消。
        /// </summary>
        /// <param name="request">捕获请求快照。</param>
        /// <param name="frame">抓到的位图（所有权转移给调用方）；失败时为 null。</param>
        /// <param name="timing">分段耗时（**经过时间**口径）。</param>
        /// <param name="error">失败原因（人类可读）；成功时为 null。</param>
        /// <returns>抓到有效帧时为 true。</returns>
        public bool TryCaptureFrame(
            in BackdropCaptureRequest request,
            out Image? frame,
            out BackdropCaptureTiming timing,
            out string? error)
        {
            frame = null;
            timing = default;
            error = null;

            // ★ DPI 感知前置条件：进程不感知 DPI 时，ClientToScreen 返回的是**虚拟化**坐标，
            //   而 CopyFromScreen 按**物理**坐标取屏幕内容 ⇒ 抓到的区域与窗口错位。
            //   （实测：不感知进程里用 ClientToScreen 的坐标抓屏，抓不到窗口自己的像素。）
            int awareness = BackdropCaptureNative.GetDpiAwareness();
            if (!BackdropCaptureNative.HasPhysicalScreenCoordinates(awareness))
            {
                error = $"进程 DPI 感知等级={awareness}（期望 2/3 = 每显示器感知），" +
                        "屏幕坐标会被系统虚拟化、抓屏区域会错位，本帧放弃。" +
                        "请在启动时调用 Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)。";
                return false;
            }

            // 客户区必须完整落在虚拟桌面内，否则抓到的是"被裁掉一半"的画面
            Rectangle virtualScreen = BackdropCaptureNative.GetVirtualScreenBounds();
            if (!virtualScreen.IsEmpty && !virtualScreen.Contains(request.ScreenBounds))
            {
                error = $"客户区矩形 {request.ScreenBounds} 不完整落在虚拟桌面 {virtualScreen} 内，本帧跳过。";
                return false;
            }

            // ---- 瞬时排除：抓屏期间把本窗口从捕获中排除，避免自反馈 ----
            uint originalAffinity = BackdropCaptureNative.WDA_NONE;
            if (!BackdropCaptureNative.GetWindowDisplayAffinity(request.WindowHandle, out originalAffinity))
            {
                originalAffinity = BackdropCaptureNative.WDA_NONE;
            }

            long excludeStart = Stopwatch.GetTimestamp();
            if (!BackdropCaptureNative.SetWindowDisplayAffinity(request.WindowHandle, BackdropCaptureNative.WDA_EXCLUDEFROMCAPTURE))
            {
                int err = Marshal.GetLastWin32Error();
                error = $"设置显示亲和性失败（win32={err}）：无法把窗口从捕获中排除，本帧放弃（否则会自反馈）。";
                return false;
            }

            Bitmap captured;
            double captureMs;
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                captured = CaptureScreen(request.ScreenBounds);
                captureMs = ElapsedMsSince(t0);
            }
            catch (Exception ex)
            {
                error = "抓屏失败：" + ex.Message;
                return false;
            }
            finally
            {
                // ★ 必须恢复显示亲和性：失败 / 取消 / 异常都要走这条路（合同 §5.2 第 3 条）
                BackdropCaptureNative.SetWindowDisplayAffinity(request.WindowHandle, originalAffinity);
            }

            double excludeMs = ElapsedMsSince(excludeStart);
            timing = new BackdropCaptureTiming(captureMs, excludeMs, captured.Width, captured.Height);
            frame = captured;
            return true;
        }

        /// <summary>
        /// 恢复窗口的显示亲和性（把 <c>WDA_EXCLUDEFROMCAPTURE</c> 排除属性还原）。
        /// <para>★ **幂等**：可被重复调用；窗口已失效时静默返回（合同 §5.2 第 3 条）。</para>
        /// </summary>
        public void RestoreDisplayAffinity()
        {
            BackdropCaptureRequest request;
            lock (_sync) { request = _request; }
            RestoreAffinity(request.WindowHandle);
        }

        // 桌面捕获：GDI+ 的 CopyFromScreen 抓**屏幕矩形**。
        // ★ 用 24bpp 目标位图：没有 alpha 通道，抓到的内容**确定是不透明的真实像素**
        //   （32bpp 目标位图在部分抓屏路径下 alpha 会是 0，整帧会被当成透明而发黑）。
        // ★ 源坐标可以为负：CopyFromScreen 抓的是虚拟桌面（多屏并集），
        //   副屏在主屏左侧时 X 为负（A06 实测通过）。
        // ★ A06 实测：桌面 DC BitBlt(SRCCOPY)、BitBlt|CAPTUREBLT、CopyFromScreen
        //   三种屏幕捕获后端在 EXCLUDEFROMCAPTURE 下**像素级完全一致**（与参考图差 0.00），
        //   本实现取其中最简单的一种。
        private static Bitmap CaptureScreen(Rectangle bounds)
        {
            int w = bounds.Width;
            int h = bounds.Height;

            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            try
            {
                using Graphics g = Graphics.FromImage(bmp);
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                return bmp;
            }
            catch
            {
                // 失败路径必须释放已创建但未交付的位图（合同 §4.2 第 6 条）
                bmp.Dispose();
                throw;
            }
        }

        // ================================================================
        // 帧交接
        // ================================================================

        // 发布候选帧（后台线程调用）。返回 false 表示正在停止、帧不应发布。
        private bool Publish(Bitmap frame, in BackdropFrameStamp stamp)
        {
            if (_stopRequested) return false;

            lock (_pendingLock)
            {
                if (_stopRequested) return false;

                Bitmap? old = _pending;
                _pending = frame;
                _pendingStamp = stamp;
                _pendingPublishedTicks = Stopwatch.GetTimestamp();

                // ★ pending 最多一份：先释放旧的再挂新的（合同 §4.2 第 5 条）
                old?.Dispose();
            }

            return true;
        }

        // 接管 pending（UI 线程调用）。代次过期的帧由本类自己回收，不交给 UI。
        private void AdoptPending(in BackdropCaptureRequest request)
        {
            lock (_pendingLock)
            {
                if (_pending == null) return;

                if (!_pendingStamp.Matches(request))
                {
                    // ★ 代次不匹配的帧由发布方自己回收，不得丢给 UI（合同 §4.2 第 4 条）
                    _pending.Dispose();
                    _pending = null;
                    return;
                }

                Bitmap next = _pending;
                _pending = null;

                // ★ 旧 active 在这里释放：此刻在 UI 线程，且上一次绘制已经结束，
                //   没有任何 Paint 正在使用旧帧（合同 §4.2 第 2 条）。
                if (_active != null)
                {
                    _active.Dispose();
                    _active = null;
                }

                _active = next;
                _activeStamp = _pendingStamp;

                double waitUi = (Stopwatch.GetTimestamp() - _pendingPublishedTicks) * 1000.0 / Stopwatch.Frequency;
                lock (_metrics) { _metrics.WaitUiMs = waitUi; }
            }
        }

        // ================================================================
        // 计量与状态
        // ================================================================

        private void RecordMetrics(in FrameOutcome outcome, double publishMs, double cpuTotal, double elapsed)
        {
            lock (_metrics)
            {
                _metrics.Success = true;
                _metrics.FailureReason = string.Empty;
                _metrics.CaptureMs = outcome.CaptureMs;
                _metrics.FilterMs = outcome.FilterMs;
                _metrics.PublishMs = publishMs;
                _metrics.ExcludeWindowMs = outcome.ExcludeWindowMs;
                _metrics.CpuMsTotal = cpuTotal;
                _metrics.ElapsedMs = elapsed;
                _metrics.FrameWidth = outcome.Stamp.Width;
                _metrics.FrameHeight = outcome.Stamp.Height;
                _metrics.BoxRadius = outcome.BoxRadius;
                _metrics.SigmaEffectiveDisplayPx = outcome.SigmaEffectiveDisplayPx;
                _metrics.TempPeakBytes = outcome.TempPeakBytes;
                _metrics.FrameCount++;

                if (_elapsedCount < _elapsedSamples.Length)
                {
                    _elapsedSamples[_elapsedCount++] = elapsed;
                }
                else
                {
                    _elapsedSamples[_elapsedIndex] = elapsed;
                    _elapsedIndex = (_elapsedIndex + 1) % _elapsedSamples.Length;
                }
            }
        }

        // p95（经过时间口径）。调用方必须已持有 _metrics 锁。
        private double ComputeP95()
        {
            if (_elapsedCount <= 0) return 0;
            var sorted = new double[_elapsedCount];
            Array.Copy(_elapsedSamples, sorted, _elapsedCount);
            Array.Sort(sorted);
            int index = (int)Math.Ceiling(0.95 * sorted.Length) - 1;
            if (index < 0) index = 0;
            if (index >= sorted.Length) index = sorted.Length - 1;
            return sorted[index];
        }

        private void SetState(BackdropCaptureState state, string reason)
        {
            lock (_sync)
            {
                SetStateLocked(state, reason);
            }
        }

        // 调用方必须已持有 _sync 锁
        private void SetStateLocked(BackdropCaptureState state, string reason)
        {
            Volatile.Write(ref _state, (int)state);
            Volatile.Write(ref _unavailableReason, reason ?? string.Empty);
        }

        // 恢复显示亲和性（窗口必须仍有效）
        private static void RestoreAffinity(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                if (!BackdropCaptureNative.IsWindow(hwnd)) return;
                BackdropCaptureNative.SetWindowDisplayAffinity(hwnd, BackdropCaptureNative.WDA_NONE);
            }
            catch
            {
                // 恢复失败无法再做什么；不抛异常，避免打断收尾流程
            }
        }

        // 进程 CPU 时间（毫秒）。★ CPU 口径，不是经过时间（合同 §6.1）。
        private static double CurrentCpuMs()
        {
            try
            {
                return s_cpuProcess.TotalProcessorTime.TotalMilliseconds;
            }
            catch
            {
                return 0;
            }
        }

        // 经过时间（毫秒）。★ 墙钟口径，不是 CPU 时间（合同 §6.1）。
        private static double ElapsedMsSince(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        }

        private static readonly System.Diagnostics.Process s_cpuProcess = System.Diagnostics.Process.GetCurrentProcess();

        // ================================================================
        // 内部数据结构
        // ================================================================

        // 一次"抓屏 + 处理"的结果
        private sealed class FrameOutcome
        {
            public Bitmap? Bitmap;
            public BackdropFrameStamp Stamp;
            public string FailureReason = string.Empty;
            public double CaptureMs;
            public double FilterMs;
            public double FilterCpuMs;
            public double ExcludeWindowMs;
            public int BoxRadius;
            public double SigmaEffectiveDisplayPx;
            public long TempPeakBytes;

            public static FrameOutcome Failed(string reason)
            {
                return new FrameOutcome { FailureReason = reason };
            }
        }

        // 可变的计量槽（对外暴露的是不可变快照）
        private sealed class MetricsSlot
        {
            public bool Success;
            public string FailureReason = string.Empty;
            public double CaptureMs;
            public double FilterMs;
            public double PublishMs;
            public double WaitUiMs = -1;
            public double ExcludeWindowMs;
            public double CpuMsTotal;
            public double ElapsedMs;
            public int FrameWidth;
            public int FrameHeight;
            public int BoxRadius;
            public double SigmaEffectiveDisplayPx;
            public long TempPeakBytes;
            public int FrameCount;
        }
    }
}
