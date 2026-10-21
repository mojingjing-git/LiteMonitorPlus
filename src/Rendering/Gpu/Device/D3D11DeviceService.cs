using System.Runtime.InteropServices;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Gpu.Geometry;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LiteMonitor.Rendering.Gpu.Device;

public sealed record GpuDeviceInfo(
    string AdapterName,
    string FeatureLevel,
    bool IsHardware,
    string VendorDevice,
    long AdapterLuid);

public sealed record GpuTextureProbeResult(
    string Description,
    long ActiveBytesDuringLease,
    long ActiveResourceCountDuringLease);

public sealed class GpuOwnedResource<T> : IDisposable
    where T : IDisposable
{
    private readonly GpuResourceLease _lease;
    private int _disposed;

    public GpuOwnedResource(T resource, GpuResourceLease lease)
    {
        Value = resource;
        _lease = lease;
    }

    public T Value { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Value.Dispose();
        _lease.Dispose();
    }
}

public sealed class D3D11DeviceService : IDisposable
{
    private readonly GpuResourceLease _deviceLease;
    private readonly int _ownerThreadId;
    private int _disposed;

    private D3D11DeviceService(
        ID3D11Device device,
        ID3D11DeviceContext context,
        GpuDeviceInfo info,
        GpuResourceLedger ledger,
        GpuResourceLease deviceLease)
    {
        Device = device;
        Context = context;
        Info = info;
        _ledger = ledger;
        _deviceLease = deviceLease;
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    private readonly GpuResourceLedger _ledger;

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public GpuDeviceInfo Info { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public static bool TryCreate(
        GpuResourceLedger ledger,
        RenderGenerations generations,
        out D3D11DeviceService? service,
        out string? errorCode,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        GpuResourceLease? lease = null;
        try
        {
            FeatureLevel[] levels =
            [
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            ];
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                levels,
                out device,
                out context).CheckError();

            using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
            using IDXGIAdapter1 adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
            AdapterDescription1 description = adapter1.Description1;
            if (!ledger.TryReserve(
                    GpuResourceKind.Device,
                    bytes: 0,
                    $"d3d11-device:{description.Description}",
                    generations,
                    out lease,
                    out string? rejection))
            {
                throw new InvalidOperationException(rejection);
            }

            GpuDeviceInfo info = new(
                description.Description,
                device.FeatureLevel.ToString(),
                IsHardware: true,
                VendorDevice: $"0x{description.VendorId:X4}:0x{description.DeviceId:X4}",
                AdapterLuid: ToLuidValue(
                    description.Luid.LowPart,
                    description.Luid.HighPart));
            service = new D3D11DeviceService(device, context, info, ledger, lease!);
            device = null;
            context = null;
            lease = null;
            errorCode = null;
            errorMessage = null;
            return true;
        }
        catch (Exception exception) when (
            exception is SharpGenException or
            InvalidOperationException or
            COMException)
        {
            service = null;
            errorCode = "gpu_device_initialization_failed";
            errorMessage = exception.Message;
            return false;
        }
        finally
        {
            lease?.Dispose();
            context?.Dispose();
            device?.Dispose();
        }
    }

    public GpuOwnedResource<ID3D11Texture2D> CreateTexture(
        string label,
        uint width,
        uint height,
        Format format,
        BindFlags bindFlags,
        ResourceUsage usage,
        CpuAccessFlags cpuAccessFlags,
        RenderGenerations generations)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        EnsureOwnerThread();
        if (width == 0 || height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        long bytes = checked((long)width * height * 4L);
        GpuResourceLease lease = _ledger.Reserve(
            GpuResourceKind.Texture,
            bytes,
            label,
            generations);
        try
        {
            ID3D11Texture2D texture = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = usage,
                BindFlags = bindFlags,
                CPUAccessFlags = cpuAccessFlags,
                MiscFlags = ResourceOptionFlags.None
            });
            return new GpuOwnedResource<ID3D11Texture2D>(texture, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public GpuOwnedResource<ID3D11ShaderResourceView> CreateShaderResourceView(
        string label,
        ID3D11Texture2D texture,
        RenderGenerations generations)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        EnsureOwnerThread();
        GpuResourceLease lease = _ledger.Reserve(
            GpuResourceKind.Texture,
            bytes: 0,
            label,
            generations);
        try
        {
            return new GpuOwnedResource<ID3D11ShaderResourceView>(
                Device.CreateShaderResourceView(texture),
                lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public GpuTextureProbeResult ProbeOffscreenTexture(
        uint width,
        uint height,
        RenderGenerations generations)
    {
        using GpuOwnedResource<ID3D11Texture2D> texture = CreateTexture(
            "resource-contract-probe",
            width,
            height,
            Format.B8G8R8A8_UNorm,
            BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            generations);
        Texture2DDescription description = texture.Value.Description;
        GpuResourceSnapshot snapshot = _ledger.Snapshot();
        return new GpuTextureProbeResult(
            $"{description.Width}x{description.Height}:{description.Format}",
            snapshot.ActiveBytes,
            snapshot.ActiveResourceCount);
    }

    public void ProbeOwnershipFromCurrentThread() => EnsureOwnerThread();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        EnsureOwnerThread();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
        _deviceLease.Dispose();
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "The D3D11 device and immediate context can only be used by their owning thread.");
        }
    }

    private static long ToLuidValue(uint lowPart, int highPart) =>
        unchecked((long)(
            ((ulong)(uint)highPart << 32) |
            lowPart));

}
