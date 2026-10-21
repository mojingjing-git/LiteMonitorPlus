using System.Runtime.InteropServices;
using LiteMonitor.Rendering.Contracts;

namespace LiteMonitor.Rendering.Presentation;

public sealed record PresentationDiagnostics(
    string WindowHandle,
    WindowCapturePolicy CapturePolicy,
    bool EntireTopLevelWindowExcluded,
    string UserDisclosure);

public interface IPresentationWindowAdapter
{
    bool TryGetDisplayAffinity(
        IntPtr windowHandle,
        out uint affinity,
        out string? errorCode,
        out string? errorMessage);

    bool TrySetDisplayAffinity(
        IntPtr windowHandle,
        uint affinity,
        out string? errorCode,
        out string? errorMessage);
}

public sealed class Win32PresentationWindowAdapter : IPresentationWindowAdapter
{
    public const uint WdaNone = 0x00000000;
    public const uint WdaExcludeFromCapture = 0x00000011;

    public bool TryGetDisplayAffinity(
        IntPtr windowHandle,
        out uint affinity,
        out string? errorCode,
        out string? errorMessage)
    {
        affinity = 0;
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            errorCode = "presentation_window_invalid";
            errorMessage = "The presentation window handle is not valid.";
            return false;
        }

        if (!GetWindowDisplayAffinity(windowHandle, out affinity))
        {
            int win32Error = Marshal.GetLastWin32Error();
            errorCode = "presentation_capture_policy_read_failed";
            errorMessage = $"GetWindowDisplayAffinity failed with Win32 error {win32Error}.";
            return false;
        }

        errorCode = null;
        errorMessage = null;
        return true;
    }

    public bool TrySetDisplayAffinity(
        IntPtr windowHandle,
        uint affinity,
        out string? errorCode,
        out string? errorMessage)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            // A destroyed HWND no longer carries an application-owned affinity.
            errorCode = null;
            errorMessage = null;
            return true;
        }

        if (!SetWindowDisplayAffinity(windowHandle, affinity))
        {
            int win32Error = Marshal.GetLastWin32Error();
            errorCode = "presentation_capture_policy_failed";
            errorMessage = $"SetWindowDisplayAffinity failed with Win32 error {win32Error}.";
            return false;
        }

        errorCode = null;
        errorMessage = null;
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(
        IntPtr windowHandle,
        out uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(
        IntPtr windowHandle,
        uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);
}

public sealed class PresentationSession
{
    private readonly IPresentationWindowAdapter _windowAdapter;
    private readonly object _gate = new();
    private RenderLifecycleState _state = RenderLifecycleState.Disabled;
    private IntPtr _windowHandle;
    private RenderGenerations _generations;
    private uint _originalAffinity;
    private uint _effectiveAffinity;
    private bool _hasOriginalAffinity;
    private bool _affinityChangedBySession;

    public PresentationSession(IPresentationWindowAdapter windowAdapter)
    {
        _windowAdapter = windowAdapter;
    }

    public RenderLifecycleState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public IntPtr WindowHandle
    {
        get
        {
            lock (_gate)
            {
                return _windowHandle;
            }
        }
    }

    public bool Start(
        IntPtr windowHandle,
        RenderGenerations generations,
        WindowCapturePolicy policy,
        out string? errorCode,
        out string? errorMessage)
    {
        lock (_gate)
        {
            if (_state is not RenderLifecycleState.Disabled and not RenderLifecycleState.Fallback)
            {
                errorCode = "presentation_session_already_active";
                errorMessage = "A presentation session must be fully stopped before another can start.";
                return false;
            }

            _state = RenderLifecycleState.Initializing;
            if (!ApplyCapturePolicyCore(
                    windowHandle,
                    policy,
                    out errorCode,
                    out errorMessage))
            {
                _state = RenderLifecycleState.Fallback;
                return false;
            }

            _windowHandle = windowHandle;
            _generations = generations;
            _state = RenderLifecycleState.Running;
            return true;
        }
    }

    public bool RecreateWindow(
        IntPtr windowHandle,
        RenderGenerations generations,
        WindowCapturePolicy policy,
        out string? errorCode,
        out string? errorMessage)
    {
        lock (_gate)
        {
            if (_state is not RenderLifecycleState.Suspended and not RenderLifecycleState.Running)
            {
                errorCode = "presentation_session_not_recreatable";
                errorMessage = "Only an active or suspended presentation session can recreate its window.";
                return false;
            }

            _state = RenderLifecycleState.Initializing;
            if (!RestoreOriginalAffinityCore(out errorCode, out errorMessage))
            {
                _state = RenderLifecycleState.Fallback;
                return false;
            }

            if (!ApplyCapturePolicyCore(
                    windowHandle,
                    policy,
                    out errorCode,
                    out errorMessage))
            {
                _state = RenderLifecycleState.Fallback;
                return false;
            }

            _windowHandle = windowHandle;
            _generations = generations;
            _state = RenderLifecycleState.Running;
            return true;
        }
    }

    public bool Suspend(out string errorCode)
    {
        lock (_gate)
        {
            if (_state != RenderLifecycleState.Running)
            {
                errorCode = "presentation_session_not_running";
                return false;
            }

            _state = RenderLifecycleState.Suspended;
            errorCode = string.Empty;
            return true;
        }
    }

    public bool Resume(out string errorCode)
    {
        lock (_gate)
        {
            if (_state != RenderLifecycleState.Suspended)
            {
                errorCode = "presentation_session_not_suspended";
                return false;
            }

            _state = RenderLifecycleState.Running;
            errorCode = string.Empty;
            return true;
        }
    }

    public bool SetCapturePolicy(
        WindowCapturePolicy policy,
        out string? errorCode,
        out string? errorMessage)
    {
        lock (_gate)
        {
            if (_state is not RenderLifecycleState.Running and
                not RenderLifecycleState.Suspended)
            {
                errorCode = "presentation_session_not_active";
                errorMessage = "Capture policy can only change on an active presentation session.";
                return false;
            }

            if (!ApplyCapturePolicyCore(
                    _windowHandle,
                    policy,
                    out errorCode,
                    out errorMessage))
            {
                _state = RenderLifecycleState.Fallback;
                return false;
            }

            return true;
        }
    }

    public bool BeginStop(out string errorCode)
    {
        lock (_gate)
        {
            if (_state == RenderLifecycleState.Disabled)
            {
                errorCode = string.Empty;
                return true;
            }

            if (_state == RenderLifecycleState.Stopping)
            {
                errorCode = "presentation_stop_already_started";
                return false;
            }

            _state = RenderLifecycleState.Stopping;
            errorCode = string.Empty;
            return true;
        }
    }

    public bool CompleteStop(out string? errorCode, out string? errorMessage)
    {
        lock (_gate)
        {
            if (_state != RenderLifecycleState.Stopping)
            {
                errorCode = "presentation_stop_not_started";
                errorMessage = "CompleteStop requires a prior BeginStop transition.";
                return false;
            }

            if (!RestoreOriginalAffinityCore(out errorCode, out errorMessage))
            {
                _state = RenderLifecycleState.Fallback;
                return false;
            }

            _windowHandle = IntPtr.Zero;
            _generations = default;
            _state = RenderLifecycleState.Disabled;
            errorCode = null;
            errorMessage = null;
            return true;
        }
    }

    public PresentationDiagnostics Diagnostics(WindowCapturePolicy requestedPolicy)
    {
        lock (_gate)
        {
            bool excluded =
                requestedPolicy == WindowCapturePolicy.WdaExcludeFromCapture &&
                _effectiveAffinity == Win32PresentationWindowAdapter.WdaExcludeFromCapture &&
                _state is RenderLifecycleState.Running or
                    RenderLifecycleState.Suspended or
                    RenderLifecycleState.Initializing;
            return new PresentationDiagnostics(
                $"0x{_windowHandle.ToInt64():X}",
                requestedPolicy,
                excluded,
                excluded
                    ? "Supported system screenshots and recordings exclude the entire application while live glass is active."
                    : "The application is not excluded from system screenshots or recordings.");
        }
    }

    private bool ApplyCapturePolicyCore(
        IntPtr windowHandle,
        WindowCapturePolicy policy,
        out string? errorCode,
        out string? errorMessage)
    {
        if (policy == WindowCapturePolicy.None)
        {
            return RestoreOriginalAffinityCore(out errorCode, out errorMessage);
        }

        if (!_hasOriginalAffinity)
        {
            if (!_windowAdapter.TryGetDisplayAffinity(
                    windowHandle,
                    out _originalAffinity,
                    out errorCode,
                    out errorMessage))
            {
                return false;
            }

            _hasOriginalAffinity = true;
            _effectiveAffinity = _originalAffinity;
        }

        if (_effectiveAffinity == Win32PresentationWindowAdapter.WdaExcludeFromCapture)
        {
            errorCode = null;
            errorMessage = null;
            return true;
        }

        if (!_windowAdapter.TrySetDisplayAffinity(
                windowHandle,
                Win32PresentationWindowAdapter.WdaExcludeFromCapture,
                out errorCode,
                out errorMessage))
        {
            return false;
        }

        _effectiveAffinity =
            Win32PresentationWindowAdapter.WdaExcludeFromCapture;
        _affinityChangedBySession = true;
        return true;
    }

    private bool RestoreOriginalAffinityCore(
        out string? errorCode,
        out string? errorMessage)
    {
        errorCode = null;
        errorMessage = null;
        if (!_hasOriginalAffinity || !_affinityChangedBySession)
        {
            _hasOriginalAffinity = false;
            _affinityChangedBySession = false;
            _effectiveAffinity = 0;
            _originalAffinity = 0;
            return true;
        }

        if (!_windowAdapter.TrySetDisplayAffinity(
                _windowHandle,
                _originalAffinity,
                out errorCode,
                out errorMessage))
        {
            return false;
        }

        _effectiveAffinity = _originalAffinity;
        _hasOriginalAffinity = false;
        _affinityChangedBySession = false;
        _originalAffinity = 0;
        return true;
    }
}
