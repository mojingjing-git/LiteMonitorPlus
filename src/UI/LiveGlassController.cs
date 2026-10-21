using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;

namespace LiteMonitor
{
    /// <summary>
    /// 实时玻璃诊断日志 —— 极简、只写不读、失败静默。
    /// <para>
    /// ★ 为什么需要它：玻璃"不生效"时，屏幕上**看不出任何异常**（背景层会静默降级，
    ///   画出来只是一块浅色板）。没有日志就只能靠猜。本文件把服务的真实状态
    ///   （状态 / 生效模式 / 降级原因 / 后端原因 / 已发布帧数）落盘，
    ///   让"为什么没出帧"变成可复核的事实而不是推测。
    /// </para>
    /// <para>★ 日志写在 exe 同目录，文件名 <c>liveglass.log</c>；只在**状态变化**时写。</para>
    /// </summary>
    internal static class LiveGlassLog
    {
        private static readonly object Sync = new();
        private static string? _path;

        /// <summary>日志文件路径（exe 同目录）。</summary>
        public static string LogPath =>
            _path ??= Path.Combine(AppContext.BaseDirectory, "liveglass.log");

        /// <summary>追加一行日志（任何失败都静默 —— 诊断设施不该影响主流程）。</summary>
        public static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    File.AppendAllText(
                        LogPath,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message + Environment.NewLine);
                }
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }

    /// <summary>
    /// 实时玻璃（M2）的主面板接线控制器 —— A16。
    /// <para>
    /// ★ 职责边界：
    /// <list type="number">
    /// <item>按用户开关创建 / 销毁 <see cref="BackdropCaptureService"/>（**唯一**的后台捕获循环）。
    ///   引擎 <see cref="ScreenCaptureFrameSource"/> 只作为**单次抓屏原语**使用
    ///   （合同 §13.2 第 3 条、§13.5：绝不出现两个捕获循环并存）。</item>
    /// <item>把窗口的可见性 / 句柄 / 位置 / 尺寸 / 主题变化翻译成服务的
    ///   <c>SetSuspended</c> / <c>UpdateRequest</c>（合同 §3.4 第 4~5 条）。</item>
    /// <item>在每帧绘制**开始前**完成帧的租约交接：有新帧时先接管，再释放被替换的旧帧；
    ///   没有新帧时继续持有当前有效租约。</item>
    /// <item>把服务的**实际**降级状态暴露给 UI（合同 §7「降级不静默」）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ★ 为什么用 <see cref="LiveGlassFrameSourceProxy"/> 而不是直接把服务塞给
    /// <see cref="AppearanceRuntime"/>：实时玻璃**默认关闭**（合同 §8.1）。
    /// 外观运行时在构造时就要拿到帧来源，而服务只在开关打开时才存在。
    /// 代理是一个**终身唯一**的中转对象，开关只切换它背后的服务，
    /// 因此不需要重建 <see cref="AppearanceRuntime"/>（重建会丢掉装饰/纹理缓存）。
    /// </para>
    /// </summary>
    internal sealed class LiveGlassController : IDisposable
    {
        /// <summary>停止服务时等待后台线程退出的上限。</summary>
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

        private readonly Form _form;
        private readonly Func<ThemeAppearanceOptions?> _optionsProvider;
        private readonly Func<BackdropCaptureService>? _serviceFactory;
        private readonly LiveGlassFrameSourceProxy _frameSource = new LiveGlassFrameSourceProxy();

        private ScreenCaptureFrameSource? _engine;
        private BackdropCaptureService? _service;

        /// <summary>当前绘制租约；只在换成新帧、失效、暂停或停止时释放。</summary>
        private BackdropFrameLease? _lease;

        private long _handleGeneration = 1;
        private long _sizeGeneration = 1;
        private bool _suspended;
        private bool _disposed;
        private string _lastError = string.Empty;
        private string _lastLoggedKey = string.Empty;
        private BackdropServiceRequest? _lastRequest;

        internal LiveGlassController(Form form, Func<ThemeAppearanceOptions?> optionsProvider)
            : this(form, optionsProvider, null)
        {
        }

        internal LiveGlassController(
            Form form,
            Func<ThemeAppearanceOptions?> optionsProvider,
            Func<BackdropCaptureService>? serviceFactory)
        {
            _form = form ?? throw new ArgumentNullException(nameof(form));
            _optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
            _serviceFactory = serviceFactory;
        }

        /// <summary>给外观运行时使用的帧来源代理（终身唯一，不随开关重建）。</summary>
        internal IBackdropFrameSource FrameSource => _frameSource;

        /// <summary>用户开关是否已打开。</summary>
        internal bool Enabled { get; private set; }

        /// <summary>后台捕获服务是否已创建（不一定正在出帧）。</summary>
        internal bool IsRunning => _service is not null;

        /// <summary>
        /// 当前状态说明（人类可读）—— 设置页展示用。
        /// <para>★ 反映的是**实际**状态，不是配置值：配置说"开"但服务起不来时，
        /// 这里必须显示真实原因（合同 §7「降级不静默」）。</para>
        /// </summary>
        internal string StatusText
        {
            get
            {
                if (!Enabled) return "实时玻璃未启用。";

                BackdropCaptureService? svc = _service;
                if (svc is null)
                {
                    return string.IsNullOrEmpty(_lastError)
                        ? "实时玻璃尚未启动（等待窗口句柄就绪）。"
                        : _lastError;
                }

                BackdropCaptureStatus st = svc.Status;
                if (!string.IsNullOrEmpty(st.DegradeReasonText)) return st.DegradeReasonText!;
                if (!string.IsNullOrEmpty(st.StateText)) return st.StateText!;
                return st.State.ToString();
            }
        }

        /// <summary>首次启用时触发一次（由 UI 层负责弹副作用提示）。</summary>
        internal event Action? FirstEnabled;

        /// <summary>
        /// 最近的**降级原因**（可读中文）；没有降级时为空串。
        /// <para>★ 主面板用它显示"玻璃为什么没生效"（合同 §7「降级不静默」）。</para>
        /// </summary>
        internal string LastDegradeReason
        {
            get
            {
                BackdropCaptureService? svc = _service;
                if (svc is null) return string.Empty;

                BackdropCaptureStatus st = svc.Status;
                if (!string.IsNullOrEmpty(st.DegradeReasonText)) return st.DegradeReasonText!;
                if (!string.IsNullOrEmpty(st.BackendUnavailableReason)) return st.BackendUnavailableReason!;
                return string.Empty;
            }
        }

        /// <summary>已发布到 UI 的帧数（诊断用；0 = 一帧都没出）。</summary>
        internal long PublishedFrames =>
            _service?.Metrics.FramesPublished ?? 0L;

        internal BackdropServiceRequest? CurrentRequest => _lastRequest;

        internal BackdropFrameLease? CurrentFrameLease => _lease;

        internal BackdropCaptureService? CaptureService => _service;

        // ================================================================
        // 开关
        // ================================================================

        /// <summary>
        /// 打开 / 关闭实时玻璃。
        /// <para>★ 打开时如果后台任务仍在退出（上一轮 `Stop` 超时），服务会抛
        /// <see cref="InvalidOperationException"/>（合同 §13.5）—— 这里**如实**记为失败状态，
        /// 不吞异常、不假装成功。</para>
        /// </summary>
        internal void SetEnabled(bool enabled)
        {
            if (_disposed) return;
            if (Enabled == enabled) return;

            Enabled = enabled;
            if (enabled)
            {
                StartService();
                FirstEnabled?.Invoke();
            }
            else
            {
                StopService();
            }
        }

        private void StartService()
        {
            if (_service is not null) return;

            _lastError = string.Empty;

            try
            {
                if (_serviceFactory is null)
                {
                    // 引擎只作单次抓屏原语：绝不调用它的 Start()
                    // （那会启动引擎自带的捕获循环 ⇒ 两个循环并存 ⇒ 击穿 CPU 预算）。
                    _engine = new ScreenCaptureFrameSource();
                    IBackdropCaptureBackend backend = new ScreenCaptureBackendAdapter(_engine);
                    IBackdropFrameProcessor? processor = new BackdropBlurProcessorAdapter();
                    _service = new BackdropCaptureService(backend, processor);
                }
                else
                {
                    _service = _serviceFactory()
                        ?? throw new InvalidOperationException("注入的实时玻璃捕获服务工厂返回了 null。");
                }

                _frameSource.Attach(_service);

                // 首帧请求：窗口句柄 / 客户区 / 主题快照此时可能还没就绪，
                // 拿不到就等 HandleCreated / 尺寸变化时再补（见 RefreshRequest）。
                BackdropServiceRequest? request = BuildRequest();
                if (request is null)
                {
                    _lastError = "实时玻璃已开启，等待窗口句柄就绪后开始捕获。";
                    LiveGlassLog.Write("StartService：" + _lastError + "（稍后由 RefreshRequest 补启动）");
                    return;
                }

                _service.Start(request);
                ApplySuspension();
                LiveGlassLog.Write("StartService：已启动。句柄=" + request.WindowHandle
                    + " 客户区(物理)=" + request.PhysicalClientBounds
                    + " 主题代次=" + request.ThemeGeneration
                    + " 句柄代次=" + request.HandleGeneration
                    + " 尺寸代次=" + request.SizeGeneration
                    + " Blur=" + request.BlurPx + " Fps=" + request.TargetFps);
            }
            catch (InvalidOperationException ex)
            {
                // ★ 合同 §13.5：上一轮的后台任务还没退出 ⇒ 拒绝重启。
                //   这是**预期内**的失败，不是致命错误；如实显示原因，让用户稍后重试。
                _lastError = ex.Message;
                LiveGlassLog.Write("StartService：被拒（上一轮任务尚未退出）：" + ex.Message);
                StopService(keepEnabled: true);
            }
            catch (Exception ex)
            {
                _lastError = "实时玻璃启动失败：" + ex.Message;
                LiveGlassLog.Write("StartService：失败：" + ex);
                StopService(keepEnabled: true);
            }
        }

        private void StopService(bool keepEnabled = false)
        {
            BackdropCaptureService? svc = _service;
            ScreenCaptureFrameSource? engine = _engine;

            _service = null;
            _engine = null;
            _frameSource.Detach();

            ReleaseHeldFrame();
            _lastRequest = null;

            try { svc?.Stop(StopTimeout); } catch { /* 停止失败不阻断关闭流程 */ }
            try { svc?.Dispose(); } catch { /* 同上 */ }
            try { engine?.Dispose(); } catch { /* 同上 */ }

            if (!keepEnabled) _suspended = false;
        }

        // ================================================================
        // 窗口事件
        // ================================================================

        /// <summary>窗口可见性变化：隐藏 / 最小化 ⇒ 暂停捕获（合同 §5.1）。</summary>
        internal void OnVisibilityChanged(bool visible)
        {
            _suspended = !visible;
            ApplySuspension();
            ReleaseHeldFrame();
        }

        /// <summary>窗口句柄重建：句柄代次 +1 并更新请求。</summary>
        internal void OnHandleChanged()
        {
            _handleGeneration++;
            RefreshRequest();
            ReleaseHeldFrame();
        }

        /// <summary>窗口尺寸变化：尺寸代次 +1 并更新请求。</summary>
        internal void OnSizeChanged()
        {
            _sizeGeneration++;
            RefreshRequest();
            ReleaseHeldFrame();
        }

        /// <summary>窗口位置变化：推进客户区几何代次并刷新物理屏幕矩形。</summary>
        internal void OnLocationChanged()
        {
            _sizeGeneration++;
            RefreshRequest();
            ReleaseHeldFrame();
        }

        /// <summary>主题 / DPI 变化：主题代次由外观运行时自增，这里同步请求。</summary>
        internal void OnAppearanceChanged()
        {
            RefreshRequest();
            ReleaseHeldFrame();
        }

        private void ApplySuspension()
        {
            BackdropCaptureService? svc = _service;
            if (svc is null) return;
            try { svc.SetSuspended(_suspended); }
            catch { /* 暂停失败不影响绘制 */ }
        }

        private void ReleaseHeldFrame()
        {
            BackdropFrameLease? lease = _lease;
            _lease = null;
            lease?.Dispose();
        }

        private void RefreshRequest()
        {
            BackdropCaptureService? svc = _service;
            if (svc is null) return;

            BackdropServiceRequest? request = BuildRequest();
            if (request is null) return;

            try
            {
                // ★ 关键修正（A16 复核）：服务**可能还没启动过**。
                //   构造期句柄/客户区未就绪时 StartService() 会跳过 Start，
                //   而 UpdateRequest 只能更新**已启动**服务的请求 —— 它不会启动服务。
                //   结果：服务永远不抓屏，玻璃永远不生效，且没有任何报错。
                //   ⇒ 这里必须判断"是否已在运行"：没运行就 Start，运行了才 Update。
                if (svc.IsRunning)
                {
                    svc.UpdateRequest(request);
                }
                else
                {
                    svc.Start(request);
                    LiveGlassLog.Write("RefreshRequest：服务此前未运行，已在此启动（句柄/客户区此刻就绪）。");
                }
            }
            catch (InvalidOperationException ex)
            {
                // ★ 合同 §13.5：上一轮的后台任务还没退出 ⇒ 拒绝重启。如实记录，不吞。
                LiveGlassLog.Write("RefreshRequest：启动被拒（上一轮任务尚未退出）：" + ex.Message);
            }
            catch (Exception ex)
            {
                LiveGlassLog.Write("RefreshRequest：失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 构造当前捕获请求；窗口句柄 / 客户区 / 主题快照任一未就绪时返回 null。
        /// </summary>
        private BackdropServiceRequest? BuildRequest()
        {
            ThemeAppearanceOptions? options = _optionsProvider();
            if (options is null) return null;

            IntPtr handle = _form.Handle;
            if (handle == IntPtr.Zero) return null;

            // ★ 物理客户区屏幕矩形：抓屏按**物理**坐标取像素，
            //   用逻辑 ClientRectangle 会因 DPI 缩放而错位。
            if (!ScreenCaptureFrameSource.TryGetClientScreenBounds(handle, out Rectangle bounds)) return null;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            _lastRequest = BackdropServiceRequest.FromOptions(
                options,
                handle,
                bounds,
                _handleGeneration,
                _sizeGeneration);
            return _lastRequest;
        }

        // ================================================================
        // 绘制路径
        // ================================================================

        /// <summary>
        /// 绘制**开始前**调用：完成本帧的租约交接。
        /// <para>
        /// ★ 优先接管新帧，成功后才释放被替换的旧租约；没有新帧时保留当前有效租约。
        ///   因此同一次绘制内的多次调用不会把帧释放到脚下。
        /// </para>
        /// </summary>
        internal void BeginPaint()
        {
            BackdropCaptureService? svc = _service;
            if (svc is null)
            {
                ReleaseHeldFrame();
                return;
            }

            BackdropFrameLease? nextLease = null;
            if (svc.TryAcquireFrame(out nextLease) && nextLease is not null)
            {
                BackdropFrameLease? previousLease = _lease;
                _lease = nextLease;
                previousLease?.Dispose();
            }
            else if (_lease is { IsValid: false })
            {
                ReleaseHeldFrame();
            }

            LogStatusIfChanged(svc);
        }

        /// <summary>把服务的真实状态落盘（**只在变化时**写，避免每帧刷屏）。</summary>
        private void LogStatusIfChanged(BackdropCaptureService svc)
        {
            BackdropCaptureStatus st = svc.Status;
            string key = st.State + "|" + st.EffectiveMode + "|" + (st.DegradeReasonText ?? "") + "|"
                       + (st.BackendUnavailableReason ?? "") + "|" + (st.StateText ?? "")
                       + "|" + svc.Metrics.FramesPublished;

            if (key == _lastLoggedKey) return;
            _lastLoggedKey = key;

            LiveGlassLog.Write(
                "状态=" + st.State
                + " 生效模式=" + st.EffectiveMode
                + " 后端就绪=" + st.BackendReady
                + " 已发布帧=" + svc.Metrics.FramesPublished
                + " 连续失败=" + st.ConsecutiveFailures
                + " | 状态说明=" + st.StateText
                + " | 降级原因=" + st.DegradeReasonText
                + " | 后端原因=" + st.BackendUnavailableReason
                + " | 最后失败=" + st.LastFailureReason);
        }

        // ================================================================
        // 释放
        // ================================================================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopService();
            Enabled = false;
        }
    }

    /// <summary>
    /// 实时玻璃的帧来源代理 —— 转发给"当前"的后台捕获服务。
    /// <para>
    /// ★ 存在的唯一理由：实时玻璃**默认关闭**（合同 §8.1），
    ///   而 <see cref="AppearanceRuntime"/> 在构造时就要拿到帧来源。
    ///   代理让外观运行时终身持有**同一个**帧来源对象，
    ///   开关只切换它背后的服务 —— 外观运行时不必重建，装饰/纹理缓存得以保留。
    /// </para>
    /// <para>
    /// ★ 它**不是**第二个捕获循环：代理本身不抓屏、不持有任何后台任务，
    ///   只是一个转发器（合同 §13.2「服务是唯一的循环」）。
    /// </para>
    /// </summary>
    internal sealed class LiveGlassFrameSourceProxy : IBackdropFrameSource
    {
        private BackdropCaptureService? _service;

        /// <summary>绑定当前服务（开关打开时）。</summary>
        internal void Attach(BackdropCaptureService service) => _service = service;

        /// <summary>解绑（开关关闭时）。</summary>
        internal void Detach() => _service = null;

        /// <summary>本代理提供的永远是**实时捕获帧**，不是预览固定图（合同 §2.2 第 1 条）。</summary>
        public bool IsPreview => false;

        /// <summary>是否有一帧可以借出。</summary>
        public bool IsReady
        {
            get
            {
                BackdropCaptureService? svc = _service;
                return svc is not null && svc.IsReady;
            }
        }

        /// <summary>不可用原因（人类可读）；可用时为空串（合同 §7「降级不静默」）。</summary>
        public string UnavailableReason
        {
            get
            {
                BackdropCaptureService? svc = _service;
                return svc is null ? "实时玻璃未启用。" : svc.UnavailableReason;
            }
        }

        /// <summary>取当前帧（**借用**：调用方与渲染器都不得释放它）。</summary>
        public bool TryGetFrame(double blurPx, out Image? frame)
        {
            BackdropCaptureService? svc = _service;
            if (svc is null)
            {
                frame = null;
                return false;
            }

            return svc.TryGetFrame(blurPx, out frame);
        }
    }
}
