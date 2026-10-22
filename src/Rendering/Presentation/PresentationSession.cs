using System.Runtime.InteropServices;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;

namespace LiteMonitor.Rendering.Presentation;

/// <param name="HandleGeneration">
/// 【T02 合同 §7.3 R5】本会话当前持有的句柄代次（来自唯一真相源
/// <see cref="WindowHandleGeneration"/>），供诊断与清理记录对齐。0 = 未知（未接入代次源）。
/// </param>
public sealed record PresentationDiagnostics(
    string WindowHandle,
    WindowCapturePolicy CapturePolicy,
    bool EntireTopLevelWindowExcluded,
    string UserDisclosure,
    long HandleGeneration = 0);

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
    private readonly WindowHandleGeneration? _handleGenerationSource;
    private readonly object _gate = new();
    private RenderLifecycleState _state = RenderLifecycleState.Disabled;
    private IntPtr _windowHandle;
    private RenderGenerations _generations;
    private uint _originalAffinity;
    private uint _effectiveAffinity;
    private bool _hasOriginalAffinity;
    private bool _affinityChangedBySession;
    private IntPtr _originalAffinityWindow;
    private long _handleGeneration;

    /// <summary>
    /// 呈现会话的构造。
    /// <para><b>句柄代次</b>（合同 §7.2 / §7.3 R1、R5 / 主审裁决 D-G）：
    /// <paramref name="handleGenerationSource"/> 是句柄代次的**唯一真相源**实例；
    /// 生产路径必须传 <see cref="WindowHandleGeneration.Instance"/>，
    /// <b>禁止</b>另行 <c>new WindowHandleGeneration()</c>（两份实例会让 GPU 清理链与
    /// live 捕获链读到不同的代次，合同 §7.4 的跨代次丢弃判据会失效）。
    /// 未提供时代次视为未知（0），R1 的单调校验随之跳过。</para>
    /// </summary>
    public PresentationSession(
        IPresentationWindowAdapter windowAdapter,
        WindowHandleGeneration? handleGenerationSource = null)
    {
        _windowAdapter = windowAdapter;
        _handleGenerationSource = handleGenerationSource;
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

    /// <summary>
    /// 【T02 合同 §7.3 R5】本会话当前持有的句柄代次。
    /// <para>0 = 未知（会话未接入代次源，或尚未绑定窗口）。</para>
    /// </summary>
    public long HandleGeneration
    {
        get
        {
            lock (_gate)
            {
                return _handleGeneration;
            }
        }
    }

    /// <summary>本会话读到的**精确**原始 affinity（未修改过时为 0）。</summary>
    public uint OriginalAffinity
    {
        get
        {
            lock (_gate)
            {
                return _originalAffinity;
            }
        }
    }

    /// <summary>原始 affinity 是否由本会话修改过（未修改过 ⇒ 无需恢复）。</summary>
    public bool AffinityChangedBySession
    {
        get
        {
            lock (_gate)
            {
                return _affinityChangedBySession;
            }
        }
    }

    /// <summary>
    /// 本会话是否仍持有"由本程序修改过、尚未恢复"的 affinity —— 即**清理责任是否仍在本会话手里**。
    /// </summary>
    public bool OwnsCapturePolicyCleanup
    {
        get
        {
            lock (_gate)
            {
                return _hasOriginalAffinity && _affinityChangedBySession;
            }
        }
    }

    /// <summary>
    /// 【T02 合同 §6.2 / §7.3 R2】产出一条待移交给 ledger 的清理责任记录。
    /// <para>返回 <c>null</c> = 本会话没有未完成的清理责任（没有修改过 affinity，
    /// 或原值与有效值相同）。记录一经产出即代表"有未完成的清理责任"。</para>
    /// </summary>
    /// <param name="generation">该会话所属的句柄代次（来自唯一真相源）。</param>
    public WindowCapturePolicyCleanup? CreateCapturePolicyCleanup(long generation)
    {
        lock (_gate)
        {
            if (!_hasOriginalAffinity || !_affinityChangedBySession)
            {
                return null;
            }

            return new WindowCapturePolicyCleanup
            {
                WindowHandle = _originalAffinityWindow != IntPtr.Zero
                    ? _originalAffinityWindow
                    : _windowHandle,
                Generation = generation,
                OriginalAffinity = _originalAffinity,
                AffinityChangedBySession = _affinityChangedBySession,
            };
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
            // 【合同 §7.3 R5】记录本会话所属的句柄代次（唯一真相源的当前读数）。
            _handleGeneration = _handleGenerationSource?.Current ?? 0;
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

            // 【合同 §7.3 R1】代次单调校验：新句柄必须属于**严格更新**的句柄代次。
            // 计划 §4.1 :129 —— 不得用 "newHwnd != oldHwnd" 作为唯一重建断言
            // （Windows 可能复用原生句柄数值）。
            long nextGeneration = _handleGenerationSource?.Current ?? 0;
            if (nextGeneration > 0 &&
                _handleGeneration > 0 &&
                nextGeneration <= _handleGeneration)
            {
                errorCode = "presentation_session_generation_not_advanced";
                errorMessage =
                    "The presentation window was not recreated on a newer handle generation.";
                // 【合同 §7.3 R2】拒绝时**不得**改动句柄与代次 ⇒ 直接返回。
                return false;
            }

            _state = RenderLifecycleState.Initializing;
            if (!RestoreOriginalAffinityCore(out errorCode, out errorMessage))
            {
                // 【合同 §7.3 R2】恢复失败 ⇒ _windowHandle 与 _generations 保持旧值，
                // 且本会话**继续持有**清理责任（由调用方移交 ledger）。
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
            _handleGeneration = nextGeneration > 0 ? nextGeneration : _handleGeneration;
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
            _handleGeneration = 0;
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
                    : "The application is not excluded from system screenshots or recordings.",
                _handleGeneration);
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

        // 【合同 §7.3 R3】新句柄必须**重读**原值：禁止任何路径把旧代次的
        // _originalAffinity 带到新句柄 —— 原值与"读到它的那个句柄"必须绑定。
        if (!_hasOriginalAffinity || _originalAffinityWindow != windowHandle)
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
            _originalAffinityWindow = windowHandle;
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
            _originalAffinityWindow = IntPtr.Zero;
            return true;
        }

        // 恢复到**精确原值**（合同 §4.5 :198），写回"读到它的那个句柄"。
        IntPtr affinityWindow = _originalAffinityWindow != IntPtr.Zero
            ? _originalAffinityWindow
            : _windowHandle;
        if (!_windowAdapter.TrySetDisplayAffinity(
                affinityWindow,
                _originalAffinity,
                out string? adapterErrorCode,
                out string? adapterErrorMessage))
        {
            // 【合同 §7.3 R6】恢复失败必须与"状态不允许"（presentation_session_not_recreatable）
            // 可区分；底层适配器的原始码保留在消息里（错误不丢失）。
            errorCode = CapturePolicyCleanupPolicy.RestoreFailedErrorCode;
            errorMessage = adapterErrorMessage is null
                ? "The original capture policy could not be restored."
                : $"{adapterErrorCode}: {adapterErrorMessage}";
            return false;
        }

        _effectiveAffinity = _originalAffinity;
        _hasOriginalAffinity = false;
        _affinityChangedBySession = false;
        _originalAffinity = 0;
        _originalAffinityWindow = IntPtr.Zero;
        return true;
    }
}
