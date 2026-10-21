using System.Diagnostics;
using System.Runtime.InteropServices;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Gpu.Device;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LiteMonitor.Rendering.Capture;

public enum DesktopCaptureResult
{
    FrameAvailable,
    LastFrameRetained,
    NoFrameYet,
    AccessLost,
    Failed,
    StaleGeneration,
    Disposed
}

public sealed class DesktopCaptureRequestResult
{
    public DesktopCaptureRequestResult(
        DesktopCaptureResult result,
        IBackgroundFrameLease? lease,
        CaptureDiagnostic diagnostic)
    {
        Result = result;
        Lease = lease;
        Diagnostic = diagnostic;
    }

    public DesktopCaptureResult Result { get; }

    public IBackgroundFrameLease? Lease { get; }

    public CaptureDiagnostic Diagnostic { get; }
}

public sealed class GpuDesktopFrameLease : IBackgroundFrameLease
{
    private readonly DesktopDuplicationFrameSource _source;
    private int _disposed;

    internal GpuDesktopFrameLease(
        DesktopDuplicationFrameSource source,
        long leaseId,
        RenderGenerations generations,
        ID3D11Texture2D texture,
        GpuOutputDescriptor descriptor)
    {
        _source = source;
        LeaseId = leaseId;
        Generations = generations;
        Texture = texture;
        Descriptor = descriptor;
    }

    public long LeaseId { get; }

    public bool IsPreview => false;

    public RenderGenerations Generations { get; }

    public GpuOutputDescriptor Descriptor { get; }

    public ID3D11Texture2D Texture { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _source.ReleaseLease(this);
    }
}

public sealed class DesktopDuplicationFrameSource : IBackgroundFrameSource
{
    private readonly D3D11DeviceService _deviceService;
    private readonly IDXGIOutput1 _output;
    private readonly long _adapterLuid;
    private readonly object _gate = new();
    private IDXGIOutputDuplication? _duplication;
    private GpuOwnedResource<ID3D11Texture2D>? _frameTexture;
    private GpuOwnedResource<ID3D11ShaderResourceView>? _frameView;
    private long _nextLeaseId;
    private long _activeLeaseId;
    private long _nextRecoveryAttemptTimestamp;
    private long? _lastPresentTimestampQpc;
    private double? _lastFrameAgeMilliseconds;
    private bool _hasValidFrameTimestamp;
    private bool _hasFrame;
    private bool _disposed;
    private CaptureLifecycleState _state = CaptureLifecycleState.Disabled;
    private RenderGenerations _generations;
    private int _recoveryAttempt;
    private string? _lastErrorCode;
    private string? _lastErrorMessage;

    private DesktopDuplicationFrameSource(
        D3D11DeviceService deviceService,
        IDXGIOutput1 output,
        RenderGenerations generations)
    {
        _deviceService = deviceService;
        _output = output;
        _adapterLuid = deviceService.Info.AdapterLuid;
        _generations = generations;
        OutputBounds = _output.Description.DesktopCoordinates;
        IntPtr outputHandle = _output.Description.Monitor;
        if (outputHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The DXGI output does not identify a monitor.");
        }

        _state = CaptureLifecycleState.Initializing;
    }

    public CaptureCapabilities Capabilities => new(
        Available: _state is CaptureLifecycleState.Running or CaptureLifecycleState.AccessLost,
        SupportsWindowExclusion: true,
        SupportsTimestamp: _hasValidFrameTimestamp,
        SupportsHdr: false,
        ExclusionPolicy: WindowCapturePolicy.WdaExcludeFromCapture,
        BackendId: "dxgi-desktop-duplication",
        Limitation: "SDR BGRA8 output only; HDR and cross-adapter timestamping are not verified.");

    public CaptureLifecycleState State => _state;

    public Rectangle Bounds => OutputBounds;

    public long DescriptorAdapterLuid => _adapterLuid;

    public double? LastFrameAgeMilliseconds => _lastFrameAgeMilliseconds;

    public ID3D11Texture2D? CurrentTexture => _frameTexture?.Value;

    public ID3D11ShaderResourceView? CurrentShaderResourceView => _frameView?.Value;

    private Rectangle OutputBounds { get; set; }

    public ModeRotation Rotation { get; private set; }

    public static bool TryCreate(
        D3D11DeviceService deviceService,
        IDXGIOutput output,
        RenderGenerations generations,
        out DesktopDuplicationFrameSource? source,
        out string? errorCode,
        out string? errorMessage)
    {
        IDXGIOutput1? ownedOutput = null;
        DesktopDuplicationFrameSource? created = null;
        try
        {
            ownedOutput = output.QueryInterface<IDXGIOutput1>();
            created = new DesktopDuplicationFrameSource(
                deviceService,
                ownedOutput,
                generations);
            ownedOutput = null;
            if (!created.TryInitializeDuplication())
            {
                throw new InvalidOperationException(
                    $"{created._lastErrorCode}: {created._lastErrorMessage}");
            }

            created._state = CaptureLifecycleState.Running;
            source = created;
            errorCode = null;
            errorMessage = null;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            SharpGenException or
            COMException)
        {
            created?.Dispose();
            source = null;
            errorCode = "desktop_capture_initialization_failed";
            errorMessage = exception.Message;
            return false;
        }
        finally
        {
            ownedOutput?.Dispose();
        }
    }

    public bool TryAcquireLatest(
        RenderGenerations requestedGenerations,
        int timeoutMilliseconds,
        out IBackgroundFrameLease? lease,
        out CaptureDiagnostic diagnostic)
    {
        DesktopCaptureRequestResult result = Acquire(requestedGenerations, timeoutMilliseconds);
        lease = result.Lease;
        diagnostic = result.Diagnostic;
        return result.Result is DesktopCaptureResult.FrameAvailable or
            DesktopCaptureResult.LastFrameRetained;
    }

    public DesktopCaptureRequestResult Acquire(
        RenderGenerations requestedGenerations,
        int timeoutMilliseconds = 0)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Result(DesktopCaptureResult.Disposed, "capture_source_disposed", "The capture source has been disposed.");
            }

            if (!_generations.CanAccept(requestedGenerations))
            {
                return Result(DesktopCaptureResult.StaleGeneration, "capture_generation_mismatch", "The requested generation does not match the capture session.");
            }

            if (_state == CaptureLifecycleState.AccessLost)
            {
                long now = Stopwatch.GetTimestamp();
                if (now < _nextRecoveryAttemptTimestamp)
                {
                    return Result(
                        DesktopCaptureResult.AccessLost,
                        "capture_recovery_deferred",
                        "Desktop capture recovery is waiting for the duplication session to settle.");
                }

                _recoveryAttempt++;
                _state = CaptureLifecycleState.Initializing;
                if (!TryInitializeDuplication())
                {
                    _state = CaptureLifecycleState.AccessLost;
                    _nextRecoveryAttemptTimestamp =
                        now + Stopwatch.Frequency / 4;
                    return Result(
                        DesktopCaptureResult.AccessLost,
                        _lastErrorCode ?? "capture_access_not_recovered",
                        _lastErrorMessage ??
                            "Desktop access could not be recovered.");
                }

                _state = CaptureLifecycleState.Running;
                _nextRecoveryAttemptTimestamp = 0;
            }

            if (_state != CaptureLifecycleState.Running || _duplication is null)
            {
                return Result(DesktopCaptureResult.Failed, "capture_not_running", "The desktop capture source is not running.");
            }

            if (_activeLeaseId != 0)
            {
                return Result(
                    DesktopCaptureResult.Failed,
                    "capture_lease_in_use",
                    "The current desktop frame lease must be released before requesting another frame.");
            }

            Result acquireResult = _duplication.AcquireNextFrame(
                (uint)Math.Max(0, timeoutMilliseconds),
                out OutduplFrameInfo frameInfo,
                out IDXGIResource? desktopResource);
            if (acquireResult == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                return RetainedOrEmpty("capture_wait_timeout");
            }

            if (acquireResult == Vortice.DXGI.ResultCode.AccessLost)
            {
                _hasFrame = false;
                _state = CaptureLifecycleState.AccessLost;
                _nextRecoveryAttemptTimestamp =
                    Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
                _duplication.Dispose();
                _duplication = null;
                return Result(DesktopCaptureResult.AccessLost, "capture_access_lost", "Desktop access was lost.");
            }

            if (acquireResult.Failure)
            {
                _hasFrame = false;
                _lastErrorCode = "capture_acquire_failed";
                _lastErrorMessage = $"AcquireNextFrame failed with HRESULT 0x{acquireResult.Code:X8}.";
                return Result(DesktopCaptureResult.Failed, _lastErrorCode, _lastErrorMessage);
            }

            try
            {
                using ID3D11Texture2D sourceTexture =
                    desktopResource.QueryInterface<ID3D11Texture2D>();
                if (sourceTexture.Description.Format != Format.B8G8R8A8_UNorm)
                {
                    _lastErrorCode = "capture_format_unsupported";
                    _lastErrorMessage =
                        $"Desktop Duplication returned {sourceTexture.Description.Format}; only B8G8R8A8_UNorm is supported.";
                    return Result(DesktopCaptureResult.Failed, _lastErrorCode, _lastErrorMessage);
                }

                EnsureFrameTexture(sourceTexture.Description);
                _deviceService.Context.CopyResource(_frameTexture!.Value, sourceTexture);
                long presentTimestamp = frameInfo.LastPresentTime;
                if (presentTimestamp > 0 &&
                    presentTimestamp <= Stopwatch.GetTimestamp() &&
                    CalculateAgeMilliseconds(presentTimestamp) is double age &&
                    age is >= 0 and <= 5000)
                {
                    _lastPresentTimestampQpc = presentTimestamp;
                    _lastFrameAgeMilliseconds = age;
                    _hasValidFrameTimestamp = true;
                }
                else
                {
                    _lastFrameAgeMilliseconds = null;
                    _hasValidFrameTimestamp = false;
                }

                _hasFrame = true;
                _recoveryAttempt = 0;
                return CreateLease(
                    DesktopCaptureResult.FrameAvailable,
                    frameInfo.AccumulatedFrames > 0,
                    null);
            }
            finally
            {
                desktopResource?.Dispose();
                _duplication.ReleaseFrame().CheckError();
            }
        }
    }

    internal void ReleaseLease(GpuDesktopFrameLease lease)
    {
        lock (_gate)
        {
            if (_activeLeaseId == lease.LeaseId)
            {
                _activeLeaseId = 0;
            }
        }
    }

    public void UpdateGenerations(RenderGenerations generations)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _generations = generations;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _state = CaptureLifecycleState.Stopping;
            _duplication?.Dispose();
            _duplication = null;
            _frameView?.Dispose();
            _frameView = null;
            _frameTexture?.Dispose();
            _frameTexture = null;
            _hasFrame = false;
            _activeLeaseId = 0;
            _output.Dispose();
            _state = CaptureLifecycleState.Disabled;
        }
    }

    private bool TryInitializeDuplication()
    {
        try
        {
            _duplication?.Dispose();
            _duplication = _output.DuplicateOutput(_deviceService.Device);
            OutputBounds = _output.Description.DesktopCoordinates;
            Rotation = _duplication.Description.Rotation;
            _hasFrame = false;

            _lastErrorCode = null;
            _lastErrorMessage = null;
            return true;
        }
        catch (Exception exception) when (
            exception is SharpGenException or
            InvalidOperationException or
            COMException)
        {
            _duplication?.Dispose();
            _duplication = null;
            _lastErrorCode = "desktop_capture_initialize_failed";
            _lastErrorMessage = exception.Message;
            return false;
        }
    }

    private void EnsureFrameTexture(Texture2DDescription description)
    {
        // Portrait outputs still supply an unrotated surface. Allocate from
        // the acquired texture, not the display-space desktop rectangle.
        if (_frameTexture is not null &&
            _frameTexture.Value.Description.Width == description.Width &&
            _frameTexture.Value.Description.Height == description.Height)
            return;

        GpuOwnedResource<ID3D11Texture2D>? texture = null;
        GpuOwnedResource<ID3D11ShaderResourceView>? view = null;
        try
        {
            texture = _deviceService.CreateTexture("desktop-duplication-frame",
                description.Width, description.Height, description.Format,
                BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, _generations);
            view = _deviceService.CreateShaderResourceView("desktop-duplication-view", texture.Value, _generations);
            _frameView?.Dispose();
            _frameTexture?.Dispose();
            _frameTexture = texture;
            _frameView = view;
            texture = null;
            view = null;
        }
        finally
        {
            view?.Dispose();
            texture?.Dispose();
        }
    }

    private DesktopCaptureRequestResult Result(
        DesktopCaptureResult result,
        string code,
        string message) =>
        new(
            result,
            lease: null,
            new CaptureDiagnostic(
                _state,
                code,
                message,
                LastFrameAgeMilliseconds,
                _recoveryAttempt));

    private DesktopCaptureRequestResult RetainedOrEmpty(string code)
    {
        if (_hasFrame && _frameTexture is not null)
        {
            _state = CaptureLifecycleState.Running;
            return CreateLease(
                DesktopCaptureResult.LastFrameRetained,
                newFrame: false,
                code);
        }

        return Result(
            DesktopCaptureResult.NoFrameYet,
            code,
            "No completed desktop frame is available yet.");
    }

    private DesktopCaptureRequestResult CreateLease(
        DesktopCaptureResult result,
        bool newFrame,
        string? code)
    {
        if (_frameTexture is null)
        {
            return Result(
                DesktopCaptureResult.Failed,
                "capture_texture_missing",
                "The desktop frame texture is unavailable.");
        }

        long leaseId = ++_nextLeaseId;
        _activeLeaseId = leaseId;
        GpuOutputDescriptor descriptor = new(
            AdapterLuid: _adapterLuid,
            Width: _frameTexture.Value.Description.Width,
            Height: _frameTexture.Value.Description.Height,
            PixelFormat: "B8G8R8A8_UNorm",
            ColorSpace: "sRGB",
            PresentationTimestampMicroseconds:
                QpcToMicroseconds(_lastPresentTimestampQpc),
            ContentRectangle: new LogicalRect(
                OutputBounds.X,
                OutputBounds.Y,
                OutputBounds.Width,
                OutputBounds.Height));
        GpuDesktopFrameLease lease = new(
            this,
            leaseId,
            _generations,
            _frameTexture.Value,
            descriptor);
        return new DesktopCaptureRequestResult(
            result,
            lease,
            new CaptureDiagnostic(
                _state,
                code ?? (newFrame ? "capture_frame_available" : "capture_last_frame_retained"),
                newFrame ? "A new desktop frame is available." : "The last valid desktop frame was retained.",
                LastFrameAgeMilliseconds,
                _recoveryAttempt));
    }

    private static double CalculateAgeMilliseconds(long presentTimestampQpc) =>
        Math.Clamp(
            (Stopwatch.GetTimestamp() - presentTimestampQpc) * 1000.0 /
            Stopwatch.Frequency,
            0,
            5000);

    private static long QpcToMicroseconds(long? qpc)
    {
        if (!qpc.HasValue || qpc.Value <= 0)
        {
            return 0;
        }

        return (long)Math.Round(
            qpc.Value * 1_000_000.0 / Stopwatch.Frequency);
    }
}
