using System.Diagnostics;
using System.Windows.Forms;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Gpu.Device;
using LiteMonitor.Rendering.Gpu.Effects;
using LiteMonitor.src.Core;
using LiteMonitor.Rendering.Materials;

namespace LiteMonitor.Rendering.Presentation;

/// <summary>
/// UI-thread owner for the production GPU presentation lifetime. It creates no
/// device, capture source or timer until a GPU route is explicitly selected.
/// </summary>
public sealed class GpuPresentationHostController : IDisposable
{
    private readonly Form _form;
    private MaterialKind _material;
    private bool _liveBackgroundRequested;
    private bool _horizontalLayout;
    private MaterialRuntimeSettings _runtimeSettings;
    private float _userScale;
    private readonly GpuStartupGuard _startupGuard;
    private readonly GpuResourceLedger _ledger = new(
        byteBudget: 1024L * 1024 * 1024,
        resourceBudget: 256);
    private readonly GpuDeviceLifecycle _lifecycle = new();
    private readonly FrameScheduler _scheduler;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly int _ownerThreadId;
    private readonly GpuPresentationSessionOwner _sessionOwner = new();
    private readonly IPresentationWindowAdapter _windowAdapter;
    private readonly WindowHandleGeneration _handleGeneration;
    private GpuSceneRenderer? _renderer;
    private SceneSnapshot? _scene;
    private RenderGenerations _generations;
    private bool _suspended;
    private bool _disposed;
    private bool _liveCaptureStarted;
    private bool _recoveryPending;
    private bool _recoveryAttempted;
    private WindowCapturePolicy _capturePolicy;
    private string? _lastErrorCode;
    private string? _lastErrorMessage;
    private string? _capturePolicyFallbackCode;
    private string? _cleanupHandoffError;

    /// <summary>
    /// 【R02】呈现失败的**根因码**（第一次失败时写入，之后**永不**被清理诊断顶替）。
    /// <para>★ 合同 §5.3 规则 2 / U11：故障回退必须**保留失败原因**，
    /// 不得把它退化成"普通 fallback code"。</para>
    /// </summary>
    private string? _failureReasonCode;
    private string? _failureReasonMessage;

    /// <summary>【R02】故障通知订阅者抛出的异常消息（不影响宿主释放）。</summary>
    private string? _failureNotificationError;

    private PresentationBindingState _bindingState = PresentationBindingState.Unbound;
    private FirstFrameOutcome _firstFrameOutcome = FirstFrameOutcome.NotAttempted;
    private GpuStartupAttempt? _startupAttempt;

    /// <summary>
    /// 生产渲染宿主的构造。
    /// <para><b>句柄代次</b>（合同 §7.2 / 主审裁决 D-G）：默认使用进程内**唯一**实例
    /// <see cref="WindowHandleGeneration.Instance"/>；<paramref name="handleGeneration"/>
    /// 仅供**测试隔离**使用 —— 生产路径**禁止**另行 <c>new WindowHandleGeneration()</c>
    /// （两份实例会让 GPU 清理链与 live 捕获链读到不同的代次，
    /// 合同 §7.4 的跨代次丢弃判据会恰好在它要防的场景里失效）。</para>
    /// </summary>
    public GpuPresentationHostController(
        Form form,
        MaterialKind material,
        bool liveBackgroundRequested,
        bool horizontalLayout = false,
        ThemeRenderingConfig? renderingConfig = null,
        float userScale = 1f,
        GpuStartupGuard? startupGuard = null,
        Theme? theme = null,
        IPresentationWindowAdapter? windowAdapter = null,
        WindowHandleGeneration? handleGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (material is not MaterialKind.LiquidGlass and not MaterialKind.CyberPunk)
        {
            throw new ArgumentOutOfRangeException(nameof(material));
        }

        _form = form;
        _material = material;
        _liveBackgroundRequested = liveBackgroundRequested;
        _horizontalLayout = horizontalLayout;
        _runtimeSettings = MaterialRuntimeSettings.Create(renderingConfig, material, theme);
        _userScale =
            float.IsFinite(userScale) && userScale > 0 ? userScale : 1f;
        _startupGuard = startupGuard ?? GpuStartupGuard.Shared;
        _windowAdapter = windowAdapter ?? new Win32PresentationWindowAdapter();
        _handleGeneration = handleGeneration ?? WindowHandleGeneration.Instance;
        _scheduler = new FrameScheduler(_runtimeSettings.TargetFramesPerSecond);
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _timer = new System.Windows.Forms.Timer { Interval = 8 };
        _timer.Tick += OnTimerTick;
    }

    public bool IsActive =>
        !_disposed &&
        _renderer is not null &&
        _sessionOwner.Session?.State == RenderLifecycleState.Running;

    public bool NeedsRestart =>
        !_disposed &&
        _renderer is null &&
        !_sessionOwner.OwnsSession &&
        !_suspended;

    public bool RecoveryAttempted => _recoveryAttempted;

    public bool RequestsLiveBackground => _liveBackgroundRequested;

    public bool IsLiveCaptureStarted => _liveCaptureStarted && _renderer?.IsLiveCaptureActive == true;

    /// <summary>
    /// 宿主是否已经**释放**呈现会话。
    /// <para>【T02 合同 §6.2 / INV-1】本成员只表示"宿主是否还持有会话"，
    /// **绝不**表达"清理责任是否还在" —— 清理责任由 ledger 独立持有。
    /// 恢复失败时宿主同样会释放自己（GPU-001 的根因修复），因此本成员在停止后恒为 true。</para>
    /// </summary>
    public bool WindowCapturePolicyRestored => !_sessionOwner.OwnsSession;

    /// <summary>
    /// 【T02 合同 §2 维度 6】本宿主的 HWND 呈现历史（未绑定 / 已绑定 / 已尝试呈现 / 已成功呈现）。
    /// <para>单调推进、停止后不回退 —— 协调器据此判断"是否需要受控重建 HWND"
    /// （计划 §4.1 :123-126）。</para>
    /// </summary>
    public PresentationBindingState BindingState => _bindingState;

    /// <summary>【T02 合同 §2 维度 8】首帧结果（未尝试 / 成功 / 失败 / 结果不明）。</summary>
    public FirstFrameOutcome FirstFrameOutcome => _firstFrameOutcome;

    /// <summary>
    /// 【T02 合同 §10 GPU-003】宿主侧二次校验发现的原因码
    /// （例如 <c>wda_capture_policy_unsupported_on_layered_window</c>）。
    /// </summary>
    public string? CapturePolicyFallbackCode => _capturePolicyFallbackCode;

    /// <summary>
    /// 【T02 合同 §6.2 / INV-6】新代次取得 WDA 所有权前的"同一代次是否仍有未完成清理"查询。
    /// <para>由 owner B 的 ledger 持有者注入；未注入时**不阻止**启动（保持既有行为）。</para>
    /// </summary>
    public Func<long, bool>? CapturePolicyCleanupPendingProbe { get; set; }

    /// <summary>
    /// 【LM-074】同一代次是否仍被"**未完成的 affinity 清理**"占用的闸门查询。
    /// <para>★ 与 <see cref="CapturePolicyCleanupPendingProbe"/> <b>并列</b>：后者只能表达 bool
    /// （"还有没有未完成的责任"），本探针返回**阻断原因码**，二者必须**可区分** ——
    /// "还在按合同 §6.4 重试"与"已经耗尽但**从未恢复**"是两种不同的故障
    /// （后者会让 HWND 上残留 <c>WDA_EXCLUDEFROMCAPTURE</c>）。</para>
    /// <para>由 owner B 的 ledger 持有者注入；未注入时退回到
    /// <see cref="CapturePolicyCleanupPendingProbe"/>（保持既有行为）。</para>
    /// </summary>
    public Func<long, string?>? CapturePolicyCleanupBlockingProbe { get; set; }

    /// <summary>
    /// 【T02 合同 §6.2】清理责任移交出口：宿主恢复失败时把清理记录交给订阅者。
    /// <para><b>集成者须知（owner B）</b>：在创建宿主时订阅本事件，把记录加入 ledger；
    /// ledger 负责退避重试（§6.4）与终结迁移（§6.3）。</para>
    /// </summary>
    public event Action<WindowCapturePolicyCleanup>? CapturePolicyCleanupRequired;

    /// <summary>最近一次移交出去的清理责任；从未移交过则为 <c>null</c>。</summary>
    public WindowCapturePolicyCleanup? LastCapturePolicyCleanup { get; private set; }

    /// <summary>移交出口的订阅者抛出的异常消息（不影响宿主释放）。</summary>
    public string? CleanupHandoffError => _cleanupHandoffError;

    /// <summary>
    /// 宿主呈现会话的所有权容器（合同 §6.2）。
    /// <para>公开它是为了让 owner B 与测试能观察到"宿主是否还持有会话"，
    /// 并在宿主尚未启动时也能注入一个会话来验证停止路径。</para>
    /// </summary>
    public GpuPresentationSessionOwner SessionOwner => _sessionOwner;

    /// <summary>当前句柄代次（唯一真相源的当前读数）。</summary>
    public long CurrentHandleGeneration => _handleGeneration.Current;

    public BackgroundInputDiagnostics? BackgroundInput => _renderer?.CurrentBackgroundInput;

    public bool IsSuspended => _suspended;

    public int CaptureSessionsCreated =>
        _renderer?.CaptureSessionsCreated ?? 0;

    public MaterialKind Material => _material;

    public RenderGenerations Generations => _generations;

    public string? LastErrorCode => _lastErrorCode;

    public string? LastErrorMessage => _lastErrorMessage;

    /// <summary>
    /// 【R02】呈现失败的**根因码**（第一次失败时写入；之后的清理诊断**不得**顶替它）。
    /// <para>★ 合同 §5.3 规则 2 / U11：故障回退必须**保留失败原因**，
    /// 不得把它退化成"普通 fallback code"。</para>
    /// </summary>
    public string? FailureReasonCode => _failureReasonCode;

    /// <summary>【R02】呈现失败的根因消息。</summary>
    public string? FailureReasonMessage => _failureReasonMessage;

    /// <summary>【R02】故障通知订阅者抛出的异常消息（不影响宿主释放）。</summary>
    public string? FailureNotificationError => _failureNotificationError;

    /// <summary>
    /// 【R02 / 合同 §4.4 / U11】故障通知出口：宿主把故障报告交给 UI 生命周期所有者。
    /// <para><b>集成者须知（owner B）</b>：订阅者**只允许**记录 / 入队 ——
    /// 本事件可能在 <b>GPU 帧绘制栈内</b>触发（计划 §4.3 :169 禁止在那里递归执行 legacy 转换）
    /// ⇒ 真正的转换必须由订阅者安排到 UI 安全点再执行。</para>
    /// </summary>
    public event Action<GpuFailureReport>? PresentationFailed;

    /// <summary>
    /// 【R02】构造一次故障报告快照（纯数据；呈现历史与首帧结果取自**本代次**）。
    /// </summary>
    public GpuFailureReport DescribeFailure(GpuFailureStage stage) =>
        new(
            stage,
            _failureReasonCode ?? _lastErrorCode ?? "gpu_presentation_failed",
            _failureReasonMessage ?? _lastErrorMessage,
            _bindingState,
            _firstFrameOutcome);

    public GpuResourceSnapshot ResourceSnapshot => _ledger.Snapshot();

    public bool Start(
        WindowCapturePolicy capturePolicy,
        out string? errorCode,
        out string? errorMessage)
    {
        ThrowIfDisposed();
        EnsureOwnerThread();
        if (IsActive)
        {
            errorCode = null;
            errorMessage = null;
            return true;
        }

        if (_renderer is not null)
        {
            errorCode = "gpu_presentation_suspended";
            errorMessage = "A suspended GPU session must be resumed instead of restarted.";
            return false;
        }

        if (_recoveryPending)
        {
            if (_recoveryAttempted)
            {
                errorCode = "gpu_presentation_recovery_exhausted";
                errorMessage = "The single automatic GPU recovery attempt has already failed.";
                return false;
            }

            _recoveryAttempted = true;
            _recoveryPending = false;
        }

        if (!_form.IsHandleCreated || _form.Handle == IntPtr.Zero)
        {
            errorCode = "gpu_presentation_handle_not_ready";
            errorMessage = "The main window handle is not ready.";
            return false;
        }

        // 【合同 §10 GPU-003 / 裁决 D4】宿主侧二次校验：
        // WS_EX_LAYERED 与 WDA_EXCLUDEFROMCAPTURE 互斥 ⇒ 分层窗口上不得申请 WDA 排除，
        // 有效路由降为 GpuFixedInput（期望路由不变），原因码与 owner A 的路由分支保持一致。
        WindowCapturePolicy effectiveCapturePolicy = capturePolicy;
        bool liveBackgroundAllowed = _liveBackgroundRequested;
        _capturePolicyFallbackCode = null;
        if (capturePolicy == WindowCapturePolicy.WdaExcludeFromCapture &&
            WindowExtendedStyle.IsLayered(_form.Handle))
        {
            effectiveCapturePolicy = WindowCapturePolicy.None;
            liveBackgroundAllowed = false;
            _capturePolicyFallbackCode = "wda_capture_policy_unsupported_on_layered_window";
        }

        // 【合同 §6.2 INV-6 / LM-073】同一代次仍有未完成的清理 ⇒ 拒绝叠加新的 affinity 所有者。
        if (effectiveCapturePolicy == WindowCapturePolicy.WdaExcludeFromCapture &&
            TryBlockCapturePolicyOwnership(
                _handleGeneration.Current,
                out errorCode,
                out errorMessage))
        {
            return false;
        }

        int width = Math.Max(1, _form.ClientSize.Width);
        int height = Math.Max(1, _form.ClientSize.Height);
        _generations = new RenderGenerations(
            Theme: 1,
            Window: 1,
            Size: 1,
            Topology: 1,
            Device: 1);

        try
        {
            if (!_startupGuard.TryBeginAttempt(
                    out GpuStartupAttempt? startupAttempt,
                    out errorCode,
                    out errorMessage))
            {
                EnterFallback(errorCode, errorMessage);
                return false;
            }

            _startupAttempt = startupAttempt;
            _capturePolicy = effectiveCapturePolicy;
            _lifecycle.Probe();
            _lifecycle.Initialize();
            if (!GpuSceneRenderer.TryCreate(
                    _form.Handle,
                    _generations,
                    width,
                    height,
                    _material,
                    _ledger,
                    _runtimeSettings,
                    _form.DeviceDpi,
                    _userScale,
                    initializationHook: null,
                    out GpuSceneRenderer? renderer,
                    out errorCode,
                    out errorMessage))
            {
                EnterFallback(errorCode, errorMessage);
                return false;
            }

            GpuSceneRenderer activeRenderer = renderer ??
                throw new InvalidOperationException(
                    "GPU renderer creation reported success without a renderer.");
            _renderer = activeRenderer;
            SetToolbarFixedBackground();
            PresentationSession presentation = new(_windowAdapter, _handleGeneration);
            _sessionOwner.Bind(presentation);
            if (!presentation.Start(
                    _form.Handle,
                    _generations,
                    effectiveCapturePolicy,
                    out errorCode,
                    out errorMessage))
            {
                StopCore();
                EnterFallback(errorCode, errorMessage);
                return false;
            }

            _lifecycle.MarkRunning();
            _bindingState = PresentationBindingState.Bound;
            if (liveBackgroundAllowed)
            {
                if (!activeRenderer.TryStartLiveCapture(
                        _generations,
                        out errorCode,
                        out errorMessage))
                {
                    // Keep the readable fixed GPU material, but withdraw the
                    // window-wide capture exclusion because live capture is not active.
                    if (!presentation.SetCapturePolicy(
                            WindowCapturePolicy.None,
                            out string? policyErrorCode,
                            out string? policyErrorMessage))
                    {
                        StopCore();
                        EnterFallback(
                            policyErrorCode ??
                                "gpu_presentation_capture_policy_restore_failed",
                            policyErrorMessage ??
                                "The capture exclusion could not be restored.");
                        return false;
                    }

                    _capturePolicy = WindowCapturePolicy.None;
                    _lastErrorCode = errorCode;
                    _lastErrorMessage = errorMessage;
                    _liveCaptureStarted = false;
                }
                else
                {
                    _liveCaptureStarted = true;
                }
            }

            _suspended = false;
            _scheduler.Reset();
            _timer.Start();
            if (!liveBackgroundAllowed || _liveCaptureStarted)
            {
                _lastErrorCode = null;
                _lastErrorMessage = null;
            }

            errorCode = null;
            errorMessage = null;
            return true;
        }
        catch (Exception exception)
        {
            errorCode = "gpu_presentation_start_failed";
            errorMessage = exception.Message;
            StopCore();
            EnterFallback(errorCode, errorMessage);
            return false;
        }
    }

    public void UpdateScene(SceneSnapshot scene)
    {
        ThrowIfDisposed();
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Generations.CanAccept(_generations))
        {
            _scene = scene;
        }
    }

    public void RenderNow()
    {
        ThrowIfDisposed();
        EnsureOwnerThread();
        if (!IsActive || _suspended || _renderer is null || _scene is null)
        {
            return;
        }

        RenderScene(_scheduler.TryTakeFrame(out FrameTick tick)
            ? tick.AnimationTime
            : TimeSpan.Zero);
    }

    public void Suspend()
    {
        if (_disposed || !IsActive)
        {
            return;
        }

        EnsureOwnerThread();
        _suspended = true;
        _timer.Stop();
        _renderer?.StopLiveCapture();
        _liveCaptureStarted = false;
        _sessionOwner.Session?.Suspend(out _);
    }

    public void Resume()
    {
        if (_disposed || _suspended is false)
        {
            return;
        }

        EnsureOwnerThread();

        // ★【LM-073 / 合同 §6.2 INV-6】Resume 会**重新施加** WDA 排除 ⇒ 必须先过同一个闸门。
        //   拒绝时按既有失败路径处理（停止宿主 + 记录失败原因），不得留下"半恢复"的会话。
        if (_liveBackgroundRequested &&
            TryBlockCapturePolicyOwnership(
                _handleGeneration.Current,
                out string? blockCode,
                out string? blockMessage))
        {
            StopCore();
            EnterFallback(blockCode, blockMessage);
            return;
        }

        PresentationSession? presentation = _sessionOwner.Session;
        if (presentation is null || _renderer is null)
        {
            return;
        }

        if (!presentation.Resume(out string resumeCode))
        {
            _lastErrorCode = resumeCode;
            _lastErrorMessage = "The presentation session could not resume.";
            return;
        }

        if (_liveBackgroundRequested)
        {
            if (!presentation.SetCapturePolicy(WindowCapturePolicy.WdaExcludeFromCapture,
                    out string? exclusionCode, out string? exclusionMessage))
            {
                StopCore();
                EnterFallback(exclusionCode, exclusionMessage);
                return;
            }
            _capturePolicy = WindowCapturePolicy.WdaExcludeFromCapture;
            _liveCaptureStarted = _renderer.TryStartLiveCapture(
                _generations,
                out string? errorCode,
                out string? errorMessage);
            if (!_liveCaptureStarted)
            {
                if (!presentation.SetCapturePolicy(
                        WindowCapturePolicy.None,
                        out string? policyErrorCode,
                        out string? policyErrorMessage))
                {
                    StopCore();
                    EnterFallback(
                        policyErrorCode ??
                            "gpu_presentation_capture_policy_restore_failed",
                        policyErrorMessage ??
                            "The capture exclusion could not be restored.");
                    return;
                }

                _capturePolicy = WindowCapturePolicy.None;
                _lastErrorCode = errorCode;
                _lastErrorMessage = errorMessage;
            }
        }

        _suspended = false;
        _scheduler.Reset();
        _timer.Start();
    }

    public bool TryReconfigure(
        MaterialKind material,
        bool liveBackgroundRequested,
        bool horizontalLayout,
        ThemeRenderingConfig? renderingConfig,
        float userScale,
        out string? errorCode,
        out string? errorMessage,
        Theme? theme = null)
    {
        ThrowIfDisposed();
        EnsureOwnerThread();

        // ★【LM-073 / 合同 §6.2 INV-6 / §4.5 :202】取得 WDA 所有权前必须过**同一个**闸门。
        //   闸门被放在"宿主是否可重配置"判定**之前**：闸门回答的是"能否取得 WDA 所有权"，
        //   它是所有 WDA 施加路径的**共同前置条件** ⇒ 拒绝原因不得被通用的
        //   "gpu_presentation_not_reconfigurable" 掩盖。
        //   ★ 只有**施加**（liveBackgroundRequested = true）才受闸门约束；
        //   **释放** WDA 的分支（liveBackgroundRequested = false）不受约束。
        if (liveBackgroundRequested &&
            TryBlockCapturePolicyOwnership(
                _handleGeneration.Current,
                out errorCode,
                out errorMessage))
        {
            return false;
        }

        PresentationSession? presentation = _sessionOwner.Session;
        if (_renderer is null ||
            presentation is null ||
            presentation.State is not RenderLifecycleState.Running and
                not RenderLifecycleState.Suspended)
        {
            errorCode = "gpu_presentation_not_reconfigurable";
            errorMessage =
                "Only an active or suspended GPU presentation can be reconfigured.";
            return false;
        }

        if (material is not MaterialKind.LiquidGlass and
            not MaterialKind.CyberPunk)
        {
            errorCode = "gpu_presentation_invalid_material";
            errorMessage = "The GPU presentation material is unsupported.";
            return false;
        }

        MaterialRuntimeSettings runtimeSettings =
            MaterialRuntimeSettings.Create(renderingConfig, material, theme);
        float normalizedUserScale =
            float.IsFinite(userScale) && userScale > 0 ? userScale : 1f;

        if (!liveBackgroundRequested)
        {
            _renderer.StopLiveCapture();
            _liveCaptureStarted = false;
            if (!presentation.SetCapturePolicy(
                    WindowCapturePolicy.None,
                    out errorCode,
                    out errorMessage))
            {
                return false;
            }

            _capturePolicy = WindowCapturePolicy.None;
        }
        else
        {
            if (!presentation.SetCapturePolicy(
                    WindowCapturePolicy.WdaExcludeFromCapture,
                    out errorCode,
                    out errorMessage))
            {
                return false;
            }

            _capturePolicy = WindowCapturePolicy.WdaExcludeFromCapture;
        }

        try
        {
            _renderer.UpdateConfiguration(
                material,
                runtimeSettings,
                _form.DeviceDpi,
                normalizedUserScale);
        }
        catch (Exception exception)
        {
            errorCode = "gpu_presentation_reconfigure_failed";
            errorMessage = exception.Message;
            return false;
        }

        _material = material;
        _liveBackgroundRequested = liveBackgroundRequested;
        _horizontalLayout = horizontalLayout;
        _runtimeSettings = runtimeSettings;
        _userScale = normalizedUserScale;
        _scheduler.UpdateTargetFramesPerSecond(
            runtimeSettings.TargetFramesPerSecond);
        _scene = null;
        try { SetToolbarFixedBackground(); }
        catch (Exception ex)
        {
            errorCode = "gpu_fixed_background_failed";
            errorMessage = ex.Message;
            return false;
        }

        if (liveBackgroundRequested && !_suspended)
        {
            _liveCaptureStarted = _renderer.TryStartLiveCapture(
                _generations,
                out string? captureErrorCode,
                out string? captureErrorMessage);
            if (!_liveCaptureStarted)
            {
                if (!presentation.SetCapturePolicy(
                        WindowCapturePolicy.None,
                        out string? policyErrorCode,
                        out string? policyErrorMessage))
                {
                    errorCode = policyErrorCode ??
                        "gpu_presentation_capture_policy_restore_failed";
                    errorMessage = policyErrorMessage ??
                        "The capture exclusion could not be restored.";
                    return false;
                }

                _capturePolicy = WindowCapturePolicy.None;
                _lastErrorCode = captureErrorCode;
                _lastErrorMessage = captureErrorMessage;
            }
        }
        else
        {
            _liveCaptureStarted = false;
        }

        if (!liveBackgroundRequested || _liveCaptureStarted)
        {
            _lastErrorCode = null;
            _lastErrorMessage = null;
        }

        errorCode = null;
        errorMessage = null;
        return true;
    }

    public void NotifySizeChanged()
    {
        if (_disposed || _renderer is null || !IsActive)
        {
            return;
        }

        EnsureOwnerThread();
        int width = Math.Max(1, _form.ClientSize.Width);
        int height = Math.Max(1, _form.ClientSize.Height);
        if (width == _renderer.Width && height == _renderer.Height)
        {
            return;
        }

        _generations = _generations with { Size = _generations.Size + 1 };
        try
        {
            _renderer.Resize(_generations, width, height);
            SetToolbarFixedBackground();
            _scene = null;
        }
        catch (Exception exception)
        {
            EnterFallback(
                "gpu_presentation_resize_failed",
                exception.Message);
            StopCore();

            // ★【R02】运行故障同样要上报。
            //   ★ 阶段 = Runtime ⇒ 宿主**仍保留**既有的一次自动恢复
            //   （计划 §4.4 :186 "最终失败才进入完整 legacy 切换"）。
            RaisePresentationFailed(DescribeFailure(GpuFailureStage.Runtime));
        }
    }

    public void NotifyOutputChanged(bool force = false)
    {
        PresentationSession? presentation = _sessionOwner.Session;
        if (_disposed || _suspended || !_liveBackgroundRequested) return;
        EnsureOwnerThread();

        // ★【LM-073 / 合同 §6.2 INV-6】重新施加 WDA 排除之前必须先过**同一个**闸门。
        //   ★ 拒绝时**不得**停止 live 捕获、**不得**触碰 HWND 的 affinity
        //   （本方法可能在 GPU 帧绘制栈内被调用 ⇒ 只记录错误码，不做重量级动作）。
        if (TryBlockCapturePolicyOwnership(
                _handleGeneration.Current,
                out string? blockCode,
                out string? blockMessage))
        {
            _lastErrorCode = blockCode;
            _lastErrorMessage = blockMessage;
            return;
        }

        if (_renderer is null || presentation is null) return;
        if (!force && _renderer.IsCaptureOutputCurrent) return;

        _renderer.StopLiveCapture();
        _liveCaptureStarted = false;
        if (!presentation.SetCapturePolicy(WindowCapturePolicy.WdaExcludeFromCapture,
                out string? policyCode, out string? policyMessage))
        {
            OnRenderFailure(new InvalidOperationException(policyCode + ": " + policyMessage));
            return;
        }
        _capturePolicy = WindowCapturePolicy.WdaExcludeFromCapture;
        if (_renderer.TryStartLiveCapture(_generations, out string? code, out string? message))
        {
            _liveCaptureStarted = true;
            _lastErrorCode = null;
            _lastErrorMessage = null;
            return;
        }
        // No stale frame from the previous monitor is allowed on failure.
        _lastErrorCode = code;
        _lastErrorMessage = message;
        if (!presentation.SetCapturePolicy(WindowCapturePolicy.None, out policyCode, out policyMessage))
        {
            OnRenderFailure(new InvalidOperationException(policyCode + ": " + policyMessage));
            return;
        }
        _capturePolicy = WindowCapturePolicy.None;
    }

    public void OnRenderFailure(Exception exception)
    {
        if (_disposed)
        {
            return;
        }

        EnterFallback(
            "gpu_presentation_render_failed",
            exception.Message);
        _recoveryPending = true;
        StopCore();

        // ★【R02】把故障报告交给 UI 生命周期所有者。
        //   ★ 订阅者**只允许**记录 / 入队 —— 本方法可能在 GPU 帧绘制栈内被调用
        //   （计划 §4.3 :169 禁止在那里递归执行 legacy 转换），
        //   真正的转换必须由订阅者安排到 UI 安全点（UI Tick）再执行。
        //   ★ 阶段判定：首帧从未成功 ⇒ FirstFrame（计划 §4.1 :124 保守重建的依据）；
        //   已经呈现过 ⇒ Runtime（先走既有的一次自动恢复，最终失败才回退 legacy）。
        RaisePresentationFailed(DescribeFailure(
            _firstFrameOutcome == FirstFrameOutcome.Succeeded
                ? GpuFailureStage.Runtime
                : GpuFailureStage.FirstFrame));
    }

    /// <summary>
    /// 【R02】通知订阅者（订阅者抛出的异常**不得**阻断宿主的资源释放）。
    /// </summary>
    private void RaisePresentationFailed(GpuFailureReport report)
    {
        Action<GpuFailureReport>? handlers = PresentationFailed;
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<GpuFailureReport>)handler)(report);
            }
            catch (Exception exception)
            {
                _failureNotificationError = exception.Message;
            }
        }
    }

    private void SetToolbarFixedBackground()
    {
        if (_renderer is null) return;
        if (_runtimeSettings.GlassStyle != GlassSurfaceStyle.FloatingToolbar)
        {
            _renderer.ClearFixedBackground();
            return;
        }
        if (!_renderer.TrySetFixedBackground(ToolbarPreviewFixture.CreateInput(_renderer.Width, _renderer.Height),
                out string? code, out string? message))
            throw new InvalidOperationException(code + ": " + message);
    }

    public GpuSceneRendererDiagnostics? Diagnostics() =>
        _renderer?.Diagnostics();

    public void ResetPerformanceWindows() =>
        _renderer?.ResetPerformanceWindows();

    public PresentationDiagnostics? PresentationDiagnostics() =>
        _sessionOwner.Session?.Diagnostics(_capturePolicy);

    public void CaptureScenePng(string path) =>
        _renderer?.CaptureScenePng(path);

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!IsActive)
        {
            _timer.Stop();
            return;
        }

        if (!_scheduler.TryTakeFrame(out FrameTick tick))
        {
            return;
        }

        RenderScene(tick.AnimationTime);
    }

    private void RenderScene(TimeSpan animationTime)
    {
        if (_renderer is null || _scene is null)
        {
            return;
        }

        // 【合同 §2 维度 6】呈现历史：进入绘制即视为"已尝试呈现"（单调推进，不回退）。
        if (_bindingState == PresentationBindingState.Bound)
        {
            _bindingState = PresentationBindingState.PresentAttempted;
        }

        try
        {
            NotifyOutputChanged();
            if (_renderer is null || _scene is null) return;
            _renderer.Render(
                _scene,
                _generations,
                _form.RectangleToScreen(_form.ClientRectangle),
                animationTime,
                _liveBackgroundRequested && _liveCaptureStarted,
                _horizontalLayout);
            // 【合同 §2 维度 6】渲染器报告已成功呈现 ⇒ 呈现历史落到 Presented。
            if (_renderer.PresentedFrameCount > 0)
            {
                _bindingState = PresentationBindingState.Presented;
                if (_firstFrameOutcome == FirstFrameOutcome.NotAttempted)
                {
                    _firstFrameOutcome = FirstFrameOutcome.Succeeded;
                }
            }

            if (_liveCaptureStarted && !_renderer.IsLiveCaptureActive)
            {
                var captureDiagnostic = _renderer.Diagnostics();
                _renderer.StopLiveCapture();
                _liveCaptureStarted = false;
                if (_sessionOwner.Session?.SetCapturePolicy(WindowCapturePolicy.None, out var code, out var message) == false)
                {
                    OnRenderFailure(new InvalidOperationException(code + ": " + message));
                    return;
                }
                _capturePolicy = WindowCapturePolicy.None;
                _lastErrorCode = captureDiagnostic.CaptureCode ?? "live_capture_unavailable";
                _lastErrorMessage = captureDiagnostic.CaptureMessage;
            }
            _startupAttempt?.MarkFirstSuccessfulFrame();
        }
        catch (Exception exception)
        {
            // 【合同 §2 维度 8 / U03】首次呈现调用期间抛错 ⇒ 是否已改变 HWND 状态无法确定
            // ⇒ 记为"结果不明"，它是计划 §4.1 :124 保守重建的依据。
            if (_firstFrameOutcome == FirstFrameOutcome.NotAttempted)
            {
                _firstFrameOutcome = _renderer is not null && _renderer.PresentAttemptCount > 0
                    ? FirstFrameOutcome.Unknown
                    : FirstFrameOutcome.Failed;
            }

            OnRenderFailure(exception);
        }
    }

    /// <summary>
    /// 【LM-073 / 合同 §6.2 INV-6 / §4.5 :202】同一代次的 <b>WDA 取得闸门</b>。
    /// <para>★ 唯一判据来源 = owner B 注入的 ledger 查询
    /// （<see cref="CapturePolicyCleanupBlockingProbe"/> 优先，
    /// <see cref="CapturePolicyCleanupPendingProbe"/> 兜底）：
    /// 同一 HWND（同一代次）仍有未完成的 affinity 清理 ⇒ <b>拒绝</b>叠加新的 affinity 所有者。</para>
    /// <para>★ 四个"要施加 <c>WDA_EXCLUDEFROMCAPTURE</c>"的入口
    /// （<c>Start</c> / <c>TryReconfigure</c> 的 live 分支 / <c>Resume</c> /
    /// <c>NotifyOutputChanged</c>）<b>共用</b>本方法 ⇒ 不得各自另立判据。</para>
    /// <para>★ <b>释放</b> WDA 的分支（<c>SetCapturePolicy(None)</c>）<b>不</b>受本闸门约束
    /// —— 释放只会让 HWND 回到原策略，不会叠加第二个所有者。</para>
    /// </summary>
    /// <returns><c>true</c> = 命中闸门（调用方必须拒绝并返回 <paramref name="errorCode"/>）。</returns>
    private bool TryBlockCapturePolicyOwnership(
        long generation,
        out string? errorCode,
        out string? errorMessage)
    {
        errorCode = null;
        errorMessage = null;

        // ★【LM-074】原因探针优先：它能区分"还在重试"与"已耗尽且从未恢复"。
        string? blocking = CapturePolicyCleanupBlockingProbe?.Invoke(generation);
        if (!string.IsNullOrEmpty(blocking))
        {
            errorCode = blocking;
            errorMessage = string.Equals(
                blocking,
                CapturePolicyCleanupLedger.UnresolvedBlockingErrorCode,
                StringComparison.Ordinal)
                    ? "The capture policy cleanup of this handle generation exhausted its " +
                      "retry budget without restoring the original policy."
                    : "The capture policy cleanup of this handle generation is still pending.";
            return true;
        }

        // ★ 既有探针（只能表达 bool）—— 未注入原因探针时保持既有行为。
        if (CapturePolicyCleanupPendingProbe?.Invoke(generation) != true)
        {
            return false;
        }

        errorCode = CapturePolicyCleanupLedger.PendingBlockingErrorCode;
        errorMessage =
            "The capture policy cleanup of this handle generation is still pending.";
        return true;
    }

    private void EnterFallback(string? errorCode, string? errorMessage)
    {
        _lastErrorCode = errorCode;
        _lastErrorMessage = errorMessage;

        // ★【R02】首次失败原因**只写一次** —— 之后的清理诊断（affinity 恢复失败等）
        //   不得顶替它（合同 §5.3 规则 2 / U11：有效后端状态与清理告警同时可见，
        //   不覆盖故障根因）。
        if (string.IsNullOrEmpty(_failureReasonCode) && !string.IsNullOrEmpty(errorCode))
        {
            _failureReasonCode = errorCode;
            _failureReasonMessage = errorMessage;
        }

        if (_lifecycle.State is RenderLifecycleState.Probing or
            RenderLifecycleState.Initializing or
            RenderLifecycleState.Running)
        {
            try
            {
                _lifecycle.Fail(errorCode ?? "gpu_presentation_failed");
                if (_lifecycle.State == RenderLifecycleState.Fallback)
                {
                    _lifecycle.BeginStop();
                    _lifecycle.CompleteStop();
                }
            }
            catch (InvalidOperationException)
            {
                // Fallback is best-effort bookkeeping; resource release is the
                // contract that matters after a presentation failure.
            }
        }
    }

    private void StopCore()
    {
        _timer.Stop();
        _liveCaptureStarted = false;
        _scene = null;

        // 【合同 §6.2 / 裁决 D2 —— GPU-001 的根因修复】
        // 释放宿主持有的会话并把清理责任**移交**出去：
        // 无论 affinity 恢复成功与否，宿主都不再持有会话引用；
        // 清理责任**绝不**通过"宿主引用非空"表达（那正是 GPU-001 的链条起点）。
        WindowCapturePolicyCleanup? cleanup = _sessionOwner.Stop(
            _handleGeneration.Current,
            DateTimeOffset.UtcNow,
            out string? stopErrorCode,
            out string? stopErrorMessage);
        if (cleanup is not null)
        {
            _lastErrorCode = stopErrorCode ?? "gpu_capture_policy_restore_failed";
            _lastErrorMessage = stopErrorMessage;
            HandOffCapturePolicyCleanup(cleanup);
        }
        else if (stopErrorCode is not null)
        {
            // 第一次恢复失败、内联重试成功：保留既有诊断（宿主仍记录首次失败）。
            _lastErrorCode = stopErrorCode;
            _lastErrorMessage = stopErrorMessage;
        }

        _renderer?.Dispose();
        _renderer = null;

        // 【合同 §10 GPU-005 / 裁决 D6】启动尝试的终结时机。
        ReleaseStartupAttempt(cleanup is null);

        if (_lifecycle.State is RenderLifecycleState.Running or
            RenderLifecycleState.Recovering or
            RenderLifecycleState.Suspended)
        {
            _lifecycle.BeginStop();
            _lifecycle.CompleteStop();
        }
    }

    /// <summary>
    /// 【合同 §6.2】清理责任的移交出口。
    /// <para>宿主把记录交给订阅者（owner B 的 ledger 持有者），并**不再触碰**该 HWND ——
    /// 后续恢复、退避重试与终结迁移都由 ledger 负责（合同 §6.3 / §6.4）。</para>
    /// </summary>
    private void HandOffCapturePolicyCleanup(WindowCapturePolicyCleanup cleanup)
    {
        LastCapturePolicyCleanup = cleanup;
        Action<WindowCapturePolicyCleanup>? handlers = CapturePolicyCleanupRequired;
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<WindowCapturePolicyCleanup>)handler)(cleanup);
            }
            catch (Exception exception)
            {
                // 移交出口的订阅者异常**不得**阻断宿主的资源释放。
                _cleanupHandoffError = exception.Message;
            }
        }
    }

    /// <summary>
    /// 【合同 §10 GPU-005 / 裁决 D6】启动尝试的终结时机。
    /// <para>pending 只允许被两种事件清除：① 首帧成功；② 受控放弃且**停止流程本身干净**。
    /// 首帧失败且停止流程不干净（例如 affinity 恢复失败）⇒ pending 必须保留，
    /// 下次启动进入 conservative、等待用户显式重试。</para>
    /// </summary>
    private void ReleaseStartupAttempt(bool stopClean)
    {
        GpuStartupAttempt? attempt = _startupAttempt;
        _startupAttempt = null;
        if (attempt is null)
        {
            return;
        }

        if (stopClean || attempt.Succeeded)
        {
            attempt.Dispose();
            return;
        }

        if (TryAbandonPreservingPending(attempt))
        {
            return;
        }

        // 兜底：不结束该尝试 ⇒ guard 的 pending 标记得以保留（保守启动）。
        // 【集成者须知（owner A）】实现 IGpuStartupAttemptAbandon 可让该尝试
        // 在**不**删除 pending 标记的前提下被正常注销。
    }

    /// <summary>
    /// 运行时能力探测：owner A 一旦在 <c>GpuStartupAttempt</c> 上实现
    /// <see cref="IGpuStartupAttemptAbandon"/>，本分支即刻生效。
    /// <para><c>GpuStartupAttempt</c> 是 sealed 类型，因此只能用运行时类型探测，
    /// 不能用静态模式匹配。</para>
    /// </summary>
    private static bool TryAbandonPreservingPending(GpuStartupAttempt attempt)
    {
        object candidate = attempt;
        if (candidate is IGpuStartupAttemptAbandon abandon)
        {
            abandon.AbandonPreservingPending();
            return true;
        }

        return false;
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "GPU presentation must be controlled on the UI thread that created it.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            if (_sessionOwner.OwnsSession) StopCore();
            return;
        }

        EnsureOwnerThread();
        _disposed = true;
        StopCore();
        _timer.Tick -= OnTimerTick;
        _timer.Dispose();
    }
}
