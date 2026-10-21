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
    private GpuSceneRenderer? _renderer;
    private PresentationSession? _presentation;
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
    private GpuStartupAttempt? _startupAttempt;

    public GpuPresentationHostController(
        Form form,
        MaterialKind material,
        bool liveBackgroundRequested,
        bool horizontalLayout = false,
        ThemeRenderingConfig? renderingConfig = null,
        float userScale = 1f,
        GpuStartupGuard? startupGuard = null,
        Theme? theme = null)
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
        _scheduler = new FrameScheduler(_runtimeSettings.TargetFramesPerSecond);
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _timer = new System.Windows.Forms.Timer { Interval = 8 };
        _timer.Tick += OnTimerTick;
    }

    public bool IsActive =>
        !_disposed &&
        _renderer is not null &&
        _presentation?.State == RenderLifecycleState.Running;

    public bool NeedsRestart =>
        !_disposed &&
        _renderer is null &&
        _presentation is null &&
        !_suspended;

    public bool RecoveryAttempted => _recoveryAttempted;

    public bool RequestsLiveBackground => _liveBackgroundRequested;

    public bool IsLiveCaptureStarted => _liveCaptureStarted && _renderer?.IsLiveCaptureActive == true;

    public bool WindowCapturePolicyRestored => _presentation is null;

    public BackgroundInputDiagnostics? BackgroundInput => _renderer?.CurrentBackgroundInput;

    public bool IsSuspended => _suspended;

    public int CaptureSessionsCreated =>
        _renderer?.CaptureSessionsCreated ?? 0;

    public MaterialKind Material => _material;

    public RenderGenerations Generations => _generations;

    public string? LastErrorCode => _lastErrorCode;

    public string? LastErrorMessage => _lastErrorMessage;

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
            _capturePolicy = capturePolicy;
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
            _presentation = new PresentationSession(
                new Win32PresentationWindowAdapter());
            if (!_presentation.Start(
                    _form.Handle,
                    _generations,
                    capturePolicy,
                    out errorCode,
                    out errorMessage))
            {
                StopCore();
                EnterFallback(errorCode, errorMessage);
                return false;
            }

            _lifecycle.MarkRunning();
            if (_liveBackgroundRequested)
            {
                if (!activeRenderer.TryStartLiveCapture(
                        _generations,
                        out errorCode,
                        out errorMessage))
                {
                    // Keep the readable fixed GPU material, but withdraw the
                    // window-wide capture exclusion because live capture is not active.
                    if (!_presentation.SetCapturePolicy(
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
            if (!_liveBackgroundRequested || _liveCaptureStarted)
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
        _presentation?.Suspend(out _);
    }

    public void Resume()
    {
        if (_disposed || _suspended is false)
        {
            return;
        }

        EnsureOwnerThread();
        if (_presentation is null || _renderer is null)
        {
            return;
        }

        if (!_presentation.Resume(out string resumeCode))
        {
            _lastErrorCode = resumeCode;
            _lastErrorMessage = "The presentation session could not resume.";
            return;
        }

        if (_liveBackgroundRequested)
        {
            if (!_presentation.SetCapturePolicy(WindowCapturePolicy.WdaExcludeFromCapture,
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
                if (!_presentation.SetCapturePolicy(
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
        if (_renderer is null ||
            _presentation is null ||
            _presentation.State is not RenderLifecycleState.Running and
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
            if (!_presentation.SetCapturePolicy(
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
            if (!_presentation.SetCapturePolicy(
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
                if (!_presentation.SetCapturePolicy(
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
        }
    }

    public void NotifyOutputChanged(bool force = false)
    {
        if (_disposed || _suspended || !_liveBackgroundRequested ||
            _renderer is null || _presentation is null) return;
        EnsureOwnerThread();
        if (!force && _renderer.IsCaptureOutputCurrent) return;

        _renderer.StopLiveCapture();
        _liveCaptureStarted = false;
        if (!_presentation.SetCapturePolicy(WindowCapturePolicy.WdaExcludeFromCapture,
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
        if (!_presentation.SetCapturePolicy(WindowCapturePolicy.None, out policyCode, out policyMessage))
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
        _presentation?.Diagnostics(_capturePolicy);

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
            if (_liveCaptureStarted && !_renderer.IsLiveCaptureActive)
            {
                var captureDiagnostic = _renderer.Diagnostics();
                _renderer.StopLiveCapture();
                _liveCaptureStarted = false;
                if (_presentation?.SetCapturePolicy(WindowCapturePolicy.None, out var code, out var message) == false)
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
            OnRenderFailure(exception);
        }
    }

    private void EnterFallback(string? errorCode, string? errorMessage)
    {
        _lastErrorCode = errorCode;
        _lastErrorMessage = errorMessage;
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
        if (_presentation is not null)
        {
            _presentation.BeginStop(out _);
            if (!_presentation.CompleteStop(out string? restoreCode, out string? restoreMessage))
            {
                _lastErrorCode = restoreCode ?? "gpu_capture_policy_restore_failed";
                _lastErrorMessage = restoreMessage;
                // Preserve ownership and retry once while the HWND still exists.
                _presentation.BeginStop(out _);
                if (_presentation.CompleteStop(out _, out _)) _presentation = null;
            }
            else _presentation = null;
        }

        _renderer?.Dispose();
        _renderer = null;
        _startupAttempt?.Dispose();
        _startupAttempt = null;
        if (_lifecycle.State is RenderLifecycleState.Running or
            RenderLifecycleState.Recovering or
            RenderLifecycleState.Suspended)
        {
            _lifecycle.BeginStop();
            _lifecycle.CompleteStop();
        }
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
            if (_presentation is not null) StopCore();
            return;
        }

        EnsureOwnerThread();
        _disposed = true;
        StopCore();
        _timer.Tick -= OnTimerTick;
        _timer.Dispose();
    }
}
