using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AppearanceMaterialLab;

internal enum CaptureAttemptResult
{
    FrameCopied,
    NoNewFrame,
    NoFrameYet,
    AccessLost,
    Timeout,
    Failed
}

internal sealed class DesktopDuplicator : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<double> _captureIntervals = new();
    private IDXGIOutputDuplication? _duplication;
    private long? _lastValidFrameMilliseconds;
    private bool _hasFrame;
    private bool _disposed;

    public DesktopDuplicator(ID3D11Device device, ID3D11DeviceContext context, IDXGISwapChain1 swapChain)
    {
        _device = device;
        _context = context;
        using IDXGIOutput output = swapChain.GetContainingOutput();
        using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
        OutputDescription = output1.Description;
        _duplication = output1.DuplicateOutput(_device);
    }

    public OutputDescription OutputDescription { get; }

    public Rectangle Bounds => OutputDescription.DesktopCoordinates;

    public bool HasFrame => _hasFrame;

    public int CaptureSampleCount => _captureIntervals.Count;

    public double? P95CaptureIntervalMilliseconds
    {
        get
        {
            if (_captureIntervals.Count == 0)
            {
                return null;
            }

            double[] ordered = _captureIntervals.OrderBy(value => value).ToArray();
            int index = Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1);
            return ordered[index];
        }
    }

    public double LastValidFrameAgeMilliseconds =>
        _lastValidFrameMilliseconds is long timestamp
            ? Math.Max(0, _clock.Elapsed.TotalMilliseconds - timestamp)
            : double.PositiveInfinity;

    public CaptureAttemptResult TryCopyFrame(ID3D11Texture2D destination, int timeoutMilliseconds = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_duplication is null)
        {
            return CaptureAttemptResult.Failed;
        }

        Result acquireResult = _duplication.AcquireNextFrame(
            (uint)Math.Max(0, timeoutMilliseconds),
            out OutduplFrameInfo frameInfo,
            out IDXGIResource? desktopResource);

        if (acquireResult == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            return _hasFrame ? CaptureAttemptResult.Timeout : CaptureAttemptResult.NoFrameYet;
        }

        if (acquireResult == Vortice.DXGI.ResultCode.AccessLost)
        {
            _hasFrame = false;
            return CaptureAttemptResult.AccessLost;
        }

        if (acquireResult.Failure)
        {
            _hasFrame = false;
            return CaptureAttemptResult.Failed;
        }

        try
        {
            using ID3D11Texture2D source = desktopResource.QueryInterface<ID3D11Texture2D>();
            _context.CopyResource(destination, source);
            long now = _clock.ElapsedMilliseconds;
            if (_lastValidFrameMilliseconds is long previous)
            {
                _captureIntervals.Enqueue(Math.Max(0, now - previous));
                while (_captureIntervals.Count > 600)
                {
                    _captureIntervals.Dequeue();
                }
            }
            _lastValidFrameMilliseconds = now;
            _hasFrame = true;
            return frameInfo.AccumulatedFrames > 0
                ? CaptureAttemptResult.FrameCopied
                : CaptureAttemptResult.NoNewFrame;
        }
        finally
        {
            desktopResource?.Dispose();
            _duplication.ReleaseFrame().CheckError();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _duplication?.Dispose();
        _duplication = null;
    }
}
