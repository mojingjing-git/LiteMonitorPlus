using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LiteMonitor.Rendering.Capture;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Gpu.Device;
using LiteMonitor.Rendering.Gpu.Foreground;
using LiteMonitor.Rendering.Materials;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace LiteMonitor.Rendering.Presentation;

public enum GpuRendererInitializationStage
{
    AfterSwapChain,
    AfterShaderResources,
    BeforeSizeDependentResources
}

public sealed record GpuSceneRendererDiagnostics(
    string AdapterName,
    string AdapterVendorDevice,
    long AdapterLuid,
    string PixelFormat,
    bool LiveCaptureRequested,
    CaptureLifecycleState CaptureState,
    double? LastPresentIntervalMilliseconds,
    int PresentSyncInterval,
    TimingPercentiles PresentIntervals,
    TimingPercentiles RenderDurations,
    bool GpuTimestampQueriesCreated,
    string GpuTimestampStatus,
    string? GpuTimestampUnavailableReason,
    double? LastGpuPassMilliseconds,
    TimingPercentiles GpuPassDurations,
    double? LastCompositorFrameAgeMilliseconds,
    TimingPercentiles CompositorFrameAges,
    uint LastPresentCount,
    uint LastSyncRefreshCount,
    bool CaptureSupportsTimestamp,
    long CaptureAdapterLuid,
    double? CaptureFrameAgeMilliseconds,
    TimingPercentiles CaptureFrameAges,
    int CaptureRecoveryAttempt,
    string? CaptureCode,
    string? CaptureMessage,
    long ResourceBytes,
    long ResourceCount);

/// <summary>
/// Production D3D11 scene renderer for a selected top-level HWND. It owns one
/// device/context, one swap chain, one foreground painter and at most one
/// Desktop Duplication capture source.
/// </summary>
public sealed class GpuSceneRenderer : IDisposable, IFixedBackgroundTarget
{
    private readonly IntPtr _windowHandle;
    private readonly GpuResourceLedger _ledger;
    private readonly D3D11DeviceService _deviceService;
    private IDXGIFactory2? _factory;
    private IDXGISwapChain1? _swapChain;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11Buffer? _constants;
    private ID3D11SamplerState? _sampler;
    private ID3D11RasterizerState? _rasterizer;
    private GpuOwnedResource<ID3D11Texture2D>? _emptyTexture;
    private GpuOwnedResource<ID3D11ShaderResourceView>? _emptyView;
    private GpuOwnedResource<ID3D11Texture2D>? _fixedTexture;
    private GpuOwnedResource<ID3D11ShaderResourceView>? _fixedView;
    private FixedBackgroundInput? _fixedInput;
    private SceneBackgroundMode _backgroundMode;
    private bool _foregroundWasDisabled;
    private GpuResourceLease? _swapChainLease;
    private GpuResourceLease? _shaderLease;
    private MaterialKind _material;
    private MaterialRuntimeSettings _runtimeSettings;
    private int _dpi;
    private float _userScale;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RollingTimingWindow _presentIntervals = new(2048);
    private readonly RollingTimingWindow _renderDurations = new(2048);
    private readonly RollingTimingWindow _gpuPassDurations = new(2048);
    private readonly RollingTimingWindow _compositorFrameAges = new(2048);
    private readonly RollingTimingWindow _captureFrameAges = new(2048);
    private readonly List<GpuTimestampQuerySet> _gpuTimestampQueries = [];
    private readonly List<GpuResourceLease> _gpuTimestampLeases = [];
    private GpuOwnedResource<ID3D11Texture2D>? _sceneTexture;
    private ID3D11RenderTargetView? _sceneRenderTargetView;
    private GpuOwnedResource<ID3D11Texture2D>? _foregroundTexture;
    private ID3D11RenderTargetView? _foregroundRenderTargetView;
    private GpuOwnedResource<ID3D11ShaderResourceView>? _foregroundView;
    private Direct2DForegroundPainter? _foregroundPainter;
    private DesktopDuplicationFrameSource? _captureSource;
    private IntPtr _captureMonitor;

    // This also remembers a failed output selection: retry on the next
    // monitor change or explicit settings/display change, not every frame.
    public bool IsCaptureOutputCurrent =>
        _captureMonitor != IntPtr.Zero &&
        _captureMonitor == MonitorFromWindow(_windowHandle, 2);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    private RenderGenerations _generations;
    private int _width;
    private int _height;
    private double? _lastPresentIntervalMilliseconds;
    private double? _lastGpuPassMilliseconds;
    private string _gpuTimestampStatus = "not_created";
    private string? _gpuTimestampUnavailableReason;
    private double? _lastCompositorFrameAgeMilliseconds;
    private long _lastPresentTimestamp;
    private uint _lastPresentCount;
    private uint _lastSyncRefreshCount;
    private CaptureDiagnostic? _lastCaptureDiagnostic;
    private int _captureSessionsCreated;
    private bool _disposed;

    private GpuSceneRenderer(
        IntPtr windowHandle,
        GpuResourceLedger ledger,
        D3D11DeviceService deviceService,
        RenderGenerations generations,
        MaterialKind material,
        MaterialRuntimeSettings runtimeSettings,
        int dpi,
        float userScale,
        Action<GpuRendererInitializationStage>? initializationHook,
        int width,
        int height)
    {
        _windowHandle = windowHandle;
        _ledger = ledger;
        _deviceService = deviceService;
        _generations = generations;
        _material = material;
        _runtimeSettings = runtimeSettings;
        _dpi = dpi;
        _userScale = userScale;
        _width = width;
        _height = height;

        try
        {
            using IDXGIDevice dxgiDevice = deviceService.Device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
            _factory = adapter.GetParent<IDXGIFactory2>();
            SwapChainDescription1 swapChainDescription = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None
            };
            _swapChainLease = ledger.Reserve(
                GpuResourceKind.SwapChain,
                bytes: (long)width * height * 4 * 2,
                "production-scene-swap-chain",
                generations);
            _shaderLease = ledger.Reserve(
                GpuResourceKind.Shader,
                bytes: 0,
                "production-scene-material-shaders",
                generations);
            _swapChain = _factory.CreateSwapChainForHwnd(
                deviceService.Device,
                windowHandle,
                swapChainDescription,
                null,
                null);
            _factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter);
            initializationHook?.Invoke(
                GpuRendererInitializationStage.AfterSwapChain);

            string shaderRoot = Path.Combine(AppContext.BaseDirectory, "resources", "shaders");
            byte[] vertexBytecode = File.ReadAllBytes(
                Path.Combine(shaderRoot, "SceneMaterialVS.cso"));
            byte[] pixelBytecode = File.ReadAllBytes(
                Path.Combine(shaderRoot, "SceneMaterialPS.cso"));
            _vertexShader = deviceService.Device.CreateVertexShader(vertexBytecode);
            _pixelShader = deviceService.Device.CreatePixelShader(pixelBytecode);
            _constants = deviceService.Device.CreateBuffer(
                (uint)Marshal.SizeOf<MaterialFrameConstants>(),
                BindFlags.ConstantBuffer);
            _sampler = deviceService.Device.CreateSamplerState(
                SamplerDescription.LinearClamp);
            _rasterizer = deviceService.Device.CreateRasterizerState(
                RasterizerDescription.CullNone);

            _emptyTexture = deviceService.CreateTexture(
                "production-empty-background",
                1,
                1,
                Format.B8G8R8A8_UNorm,
                BindFlags.ShaderResource,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                generations);
            _deviceService.Context.UpdateSubresource(
                new byte[] { 0, 0, 0, 255 },
                _emptyTexture.Value,
                rowPitch: 4,
                depthPitch: 4);
            _emptyView = deviceService.CreateShaderResourceView(
                "production-empty-background-view",
                _emptyTexture.Value,
                generations);
            initializationHook?.Invoke(
                GpuRendererInitializationStage.AfterShaderResources);

            initializationHook?.Invoke(
                GpuRendererInitializationStage.BeforeSizeDependentResources);
            TryCreateGpuTimestampQueries();
            CreateSizeDependentResources();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public MaterialKind Material => _material;

    public void UpdateConfiguration(
        MaterialKind material,
        MaterialRuntimeSettings runtimeSettings,
        int dpi,
        float userScale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        _deviceService.ProbeOwnershipFromCurrentThread();
        if (material is not MaterialKind.LiquidGlass and not MaterialKind.CyberPunk)
        {
            throw new ArgumentOutOfRangeException(nameof(material));
        }

        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi));
        }

        if (!float.IsFinite(userScale) || userScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userScale));
        }

        if (_foregroundTexture is null)
        {
            throw new InvalidOperationException(
                "The foreground target is unavailable for a configuration update.");
        }

        Direct2DForegroundPainter replacement =
            Direct2DForegroundPainter.Create(
                _foregroundTexture.Value,
                material,
                Direct2DForegroundPainter.ResolveFontScale(dpi, userScale),
                runtimeSettings.ForegroundTheme);
        Direct2DForegroundPainter? previous = _foregroundPainter;
        _foregroundPainter = replacement;
        _material = material;
        _runtimeSettings = runtimeSettings;
        _dpi = dpi;
        _userScale = userScale;
        previous?.Dispose();
    }

    public int Width => _width;

    public int Height => _height;

    public bool IsLiveCaptureActive =>
        _captureSource is not null &&
        _captureSource.State is CaptureLifecycleState.Running or
            CaptureLifecycleState.Initializing or
            CaptureLifecycleState.AccessLost;

    public BackgroundInputDiagnostics CurrentBackgroundInput => _backgroundMode == SceneBackgroundMode.LiveCapture
        ? new(_backgroundMode, "desktop-duplication", _captureSource?.Bounds.Width ?? 0,
            _captureSource?.Bounds.Height ?? 0, null, null, null)
            { PhysicalOrigin = _captureSource?.Bounds.Location ?? Point.Empty }
        : new(_backgroundMode, _fixedInput is not null ? "fixed-texture" : "procedural",
            _fixedInput?.Width ?? _width, _fixedInput?.Height ?? _height,
            _fixedInput?.ContentVersion, _fixedInput?.PixelSha256, _fixedInput?.OpaquePixelBounds)
            { PhysicalOrigin = _fixedInput?.Origin ?? Point.Empty };

    public bool TrySetFixedBackground(FixedBackgroundInput input,
        out string? errorCode, out string? errorMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        _deviceService.ProbeOwnershipFromCurrentThread();
        GpuOwnedResource<ID3D11Texture2D>? texture = null;
        GpuOwnedResource<ID3D11ShaderResourceView>? view = null;
        try
        {
            texture = _deviceService.CreateTexture("fixed-background", (uint)input.Width,
                (uint)input.Height, Format.B8G8R8A8_UNorm, BindFlags.ShaderResource,
                ResourceUsage.Default, CpuAccessFlags.None, _generations);
            _deviceService.Context.UpdateSubresource(input.Bgra8Pixels.ToArray(), texture.Value,
                rowPitch: (uint)input.RowPitch, depthPitch: (uint)input.Bgra8Pixels.Length);
            view = _deviceService.CreateShaderResourceView("fixed-background-view", texture.Value, _generations);
            ClearFixedBackground();
            _fixedTexture = texture;
            _fixedView = view;
            _fixedInput = input;
            _backgroundMode = SceneBackgroundMode.FixedTexture;
            texture = null;
            view = null;
            errorCode = errorMessage = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SharpGenException or COMException)
        {
            errorCode = "fixed_background_upload_failed";
            errorMessage = ex.Message;
            return false;
        }
        finally { view?.Dispose(); texture?.Dispose(); }
    }

    public void ClearFixedBackground()
    {
        _deviceService.Context.PSSetShaderResource(0, null!);
        _fixedView?.Dispose(); _fixedView = null;
        _fixedTexture?.Dispose(); _fixedTexture = null;
        _fixedInput = null;
        if (_backgroundMode == SceneBackgroundMode.FixedTexture)
            _backgroundMode = SceneBackgroundMode.Procedural;
    }

    public int CaptureSessionsCreated => _captureSessionsCreated;

    public static bool TryCreate(
        IntPtr windowHandle,
        RenderGenerations generations,
        int physicalWidth,
        int physicalHeight,
        MaterialKind material,
        GpuResourceLedger ledger,
        MaterialRuntimeSettings? runtimeSettings,
        int dpi,
        float userScale,
        Action<GpuRendererInitializationStage>? initializationHook,
        out GpuSceneRenderer? renderer,
        out string? errorCode,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        renderer = null;
        if (windowHandle == IntPtr.Zero ||
            physicalWidth <= 0 ||
            physicalHeight <= 0 ||
            dpi <= 0 ||
            material is not MaterialKind.LiquidGlass and not MaterialKind.CyberPunk)
        {
            errorCode = "gpu_scene_renderer_invalid_request";
            errorMessage = "A valid HWND, positive physical size and GPU material are required.";
            return false;
        }

        if (!D3D11DeviceService.TryCreate(
                ledger,
                generations,
                out D3D11DeviceService? deviceService,
                out errorCode,
                out errorMessage))
        {
            return false;
        }

        try
        {
            renderer = new GpuSceneRenderer(
                windowHandle,
                ledger,
                deviceService!,
                generations,
                material,
                runtimeSettings ?? MaterialRuntimeSettings.Create(null, material),
                dpi,
                userScale,
                initializationHook,
                physicalWidth,
                physicalHeight);
            deviceService = null;
            errorCode = null;
            errorMessage = null;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidOperationException or
            SharpGenException or
            COMException)
        {
            errorCode = "gpu_scene_renderer_initialization_failed";
            errorMessage = exception.Message;
            return false;
        }
        finally
        {
            deviceService?.Dispose();
        }
    }

    public bool TryStartLiveCapture(
        RenderGenerations generations,
        out string? errorCode,
        out string? errorMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_material != MaterialKind.LiquidGlass)
        {
            errorCode = "capture_material_forbidden";
            errorMessage = "The cyberpunk material must not create a desktop capture source.";
            return false;
        }

        IntPtr monitor = MonitorFromWindow(_windowHandle, 2);
        if (_captureSource is not null && monitor == _captureMonitor)
        {
            errorCode = null;
            errorMessage = null;
            return true;
        }

        StopLiveCapture();
        _captureMonitor = monitor;
        try
        {
            // Select by the HWND's current monitor, independently of the last
            // output presented by the swap chain. Never reuse a primary-screen
            // frame after crossing into a negative desktop coordinate.
            using IDXGIDevice dxgiDevice = _deviceService.Device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
            IDXGIOutput? selected = null;
            for (uint index = 0; adapter.EnumOutputs(index, out IDXGIOutput candidate).Success; index++)
            {
                using (candidate)
                {
                    if (candidate.Description.Monitor != monitor) continue;
                    selected = candidate.QueryInterface<IDXGIOutput>();
                    break;
                }
            }
            using IDXGIOutput? output = selected;
            if (output is null)
            {
                errorCode = "capture_output_not_on_render_adapter";
                errorMessage = "The current monitor is unavailable on the rendering adapter; fixed material is retained.";
                return false;
            }
            if (!DesktopDuplicationFrameSource.TryCreate(
                    _deviceService,
                    output,
                    generations,
                    out DesktopDuplicationFrameSource? source,
                    out errorCode,
                    out errorMessage))
            {
                return false;
            }

            _generations = generations;
            _captureSource = source;
            _captureSessionsCreated++;
            _lastCaptureDiagnostic = new CaptureDiagnostic(
                source!.State,
                "capture_started",
                "Desktop Duplication started for the selected output.",
                null,
                0);
            return true;
        }
        catch (Exception exception) when (exception is SharpGenException or COMException or InvalidOperationException)
        {
            errorCode = "capture_output_selection_failed";
            errorMessage = exception.Message;
            return false;
        }
    }

    public void StopLiveCapture()
    {
        DesktopDuplicationFrameSource? source = _captureSource;
        _captureSource = null;
        source?.Dispose();
        _lastCaptureDiagnostic = new CaptureDiagnostic(
            CaptureLifecycleState.Disabled,
            "capture_stopped",
            "Desktop Duplication is stopped.",
            null,
            0);
    }

    public void Render(
        SceneSnapshot scene,
        RenderGenerations generations,
        Rectangle contentRectangle,
        TimeSpan animationTime,
        bool captureRequested,
        bool horizontalLayout = false,
        Rectangle? materialRoot = null,
        bool drawForeground = true)
    {
        long renderStartedTimestamp = Stopwatch.GetTimestamp();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        if (!scene.IsValid ||
            _sceneRenderTargetView is null || _foregroundPainter is null ||
            _foregroundView is null || _constants is null)
        {
            throw new InvalidOperationException("The GPU scene renderer is not ready to draw.");
        }

        if (!_generations.CanAccept(generations))
        {
            throw new InvalidOperationException("The scene generation does not match the renderer session.");
        }

        ForegroundDrawList drawList = ForegroundDrawListBuilder.Build(
            scene,
            horizontalLayout);
        PollGpuTimestampQueries();
        GpuTimestampQuerySet? timestampQueries = TakeAvailableTimestampQueries();
        if (timestampQueries is not null)
        {
            _deviceService.Context.Begin(timestampQueries.Disjoint);
            _deviceService.Context.End(timestampQueries.Start);
        }

        _foregroundPainter.Paint(drawList, force: _foregroundWasDisabled);
        _foregroundWasDisabled = !drawForeground;
        if (!drawForeground)
            _deviceService.Context.ClearRenderTargetView(_foregroundRenderTargetView!, new Color4(0, 0, 0, 0));

        GpuDesktopFrameLease? liveLease = null;
        ID3D11ShaderResourceView backgroundView = _emptyView.Value;
        Rectangle captureRectangle = new(0, 0, _width, _height);
        bool hasLiveBackground = false;
        if (_fixedInput is not null && _fixedView is not null)
        {
            backgroundView = _fixedView.Value;
            captureRectangle = new Rectangle(contentRectangle.X + _fixedInput.Origin.X,
                contentRectangle.Y + _fixedInput.Origin.Y, _fixedInput.Width, _fixedInput.Height);
        }
        if (captureRequested && _material == MaterialKind.LiquidGlass)
        {
            if (_captureSource is null &&
                !TryStartLiveCapture(generations, out _, out _))
            {
                captureRequested = false;
            }

            if (_captureSource is not null)
            {
                DesktopCaptureRequestResult result = _captureSource.Acquire(generations, 0);
                _lastCaptureDiagnostic = result.Diagnostic;
                liveLease = result.Lease as GpuDesktopFrameLease;
                if (result.Result == DesktopCaptureResult.FrameAvailable &&
                    result.Diagnostic.LastFrameAgeMilliseconds is double frameAge)
                {
                    _captureFrameAges.Add(frameAge);
                }

                if (liveLease is not null && _captureSource.CurrentShaderResourceView is ID3D11ShaderResourceView view)
                {
                    backgroundView = view;
                    captureRectangle = _captureSource.Bounds;
                    hasLiveBackground = true;
                }
            }
        }

        try
        {
            SceneMaterialLayout layout = SceneMaterialLayoutBuilder.Build(
                scene,
                _material,
                _width,
                _height,
                contentRectangle,
                captureRectangle,
                hasLiveBackground
                    ? SceneBackgroundMode.LiveCapture
                    : _fixedInput is not null ? SceneBackgroundMode.FixedTexture : SceneBackgroundMode.Procedural,
                _runtimeSettings.MotionEnabled
                    ? (float)animationTime.TotalSeconds
                    : 0,
                _runtimeSettings,
                _userScale);
            MaterialFrameConstants constants = layout.Constants;
            // The texture is in DXGI's unrotated orientation; desktop/window
            // bounds remain signed physical coordinates in display space.
            constants.State.W = hasLiveBackground ? (float)_captureSource!.Rotation : 0;
            if (materialRoot is Rectangle root)
                constants.RootRect = new System.Numerics.Vector4(root.X, root.Y, root.Width, root.Height);
            _backgroundMode = layout.BackgroundMode;
            _deviceService.Context.UpdateSubresource(
                in constants,
                _constants);

            _deviceService.Context.OMSetRenderTargets(_sceneRenderTargetView);
            _deviceService.Context.RSSetViewport(0, 0, _width, _height);
            _deviceService.Context.IASetPrimitiveTopology(
                PrimitiveTopology.TriangleList);
            _deviceService.Context.RSSetState(_rasterizer);
            _deviceService.Context.VSSetShader(_vertexShader);
            _deviceService.Context.PSSetShader(_pixelShader);
            _deviceService.Context.VSSetConstantBuffer(0, _constants);
            _deviceService.Context.PSSetConstantBuffer(0, _constants);
            _deviceService.Context.PSSetShaderResource(0, backgroundView);
            _deviceService.Context.PSSetShaderResource(1, _foregroundView.Value);
            _deviceService.Context.PSSetSampler(0, _sampler);
            _deviceService.Context.Draw(3, 0);
            if (timestampQueries is not null)
            {
                _deviceService.Context.End(timestampQueries.End);
                _deviceService.Context.End(timestampQueries.Disjoint);
            }

            using ID3D11Texture2D backBuffer =
                _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _deviceService.Context.CopyResource(backBuffer, _sceneTexture!.Value);
            // Present(0) only records submission cadence. Synchronize the
            // production frame to the compositor so present timing, frame
            // age and the 30 FPS budget describe an actually displayed frame.
            _swapChain.Present(1, PresentFlags.None).CheckError();
            RecordPresentTiming(renderStartedTimestamp);
        }
        finally
        {
            liveLease?.Dispose();
            _deviceService.Context.PSSetShaderResource(0, null!);
            _deviceService.Context.PSSetShaderResource(1, null!);
        }
    }

    public void Resize(
        RenderGenerations generations,
        int physicalWidth,
        int physicalHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (physicalWidth <= 0 || physicalHeight <= 0 ||
            (physicalWidth == _width && physicalHeight == _height))
        {
            return;
        }

        _deviceService.Context.ClearState();
        _deviceService.Context.Flush();
        _foregroundPainter?.Dispose();
        _foregroundPainter = null;
        _foregroundView?.Dispose();
        _foregroundView = null;
        _foregroundRenderTargetView?.Dispose();
        _foregroundRenderTargetView = null;
        _foregroundTexture?.Dispose();
        _foregroundTexture = null;
        _sceneRenderTargetView?.Dispose();
        _sceneRenderTargetView = null;
        _sceneTexture?.Dispose();
        _sceneTexture = null;

        _swapChain.ResizeBuffers(
            2,
            (uint)physicalWidth,
            (uint)physicalHeight,
            Format.B8G8R8A8_UNorm,
            SwapChainFlags.None).CheckError();
        _width = physicalWidth;
        _height = physicalHeight;
        _generations = generations;
        _captureSource?.UpdateGenerations(generations);
        CreateSizeDependentResources();
    }

    public GpuSceneRendererDiagnostics Diagnostics()
    {
        GpuResourceSnapshot resources = _ledger.Snapshot();
        return new GpuSceneRendererDiagnostics(
            _deviceService.Info.AdapterName,
            _deviceService.Info.VendorDevice,
            _deviceService.Info.AdapterLuid,
            "B8G8R8A8_UNorm",
            _captureSource is not null,
            _captureSource?.State ?? CaptureLifecycleState.Disabled,
            _lastPresentIntervalMilliseconds,
            1,
            _presentIntervals.Snapshot(),
            _renderDurations.Snapshot(),
            _gpuTimestampQueries.Count > 0,
            _gpuTimestampStatus,
            _gpuTimestampUnavailableReason,
            _lastGpuPassMilliseconds,
            _gpuPassDurations.Snapshot(),
            _lastCompositorFrameAgeMilliseconds,
            _compositorFrameAges.Snapshot(),
            _lastPresentCount,
            _lastSyncRefreshCount,
            _captureSource?.Capabilities.SupportsTimestamp ?? false,
            _captureSource?.DescriptorAdapterLuid ?? 0,
            _captureSource?.LastFrameAgeMilliseconds,
            _captureFrameAges.Snapshot(),
            _lastCaptureDiagnostic?.RecoveryAttempt ?? 0,
            _lastCaptureDiagnostic?.Code,
            _lastCaptureDiagnostic?.Message,
            resources.ActiveBytes,
            resources.ActiveResourceCount);
    }

    public void ResetPerformanceWindows()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _presentIntervals.Reset();
        _renderDurations.Reset();
        _gpuPassDurations.Reset();
        _compositorFrameAges.Reset();
        _captureFrameAges.Reset();
        _lastPresentIntervalMilliseconds = null;
        _lastGpuPassMilliseconds = null;
        _lastCompositorFrameAgeMilliseconds = null;
        _lastPresentTimestamp = 0;
    }

    public Rectangle CaptureBounds =>
        _captureSource?.Bounds ?? new Rectangle(0, 0, _width, _height);

    /// <summary>
    /// Explicit cold-path evidence capture. Never called from the render loop.
    /// </summary>
    public void CaptureScenePng(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sceneTexture is null)
        {
            throw new InvalidOperationException("The scene render target is unavailable.");
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using ID3D11Texture2D staging = _deviceService.Device.CreateTexture2D(
            new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            });
        _deviceService.Context.CopyResource(staging, _sceneTexture.Value);
        MappedSubresource mapped = _deviceService.Context.Map(
            staging,
            0,
            MapMode.Read,
            Vortice.Direct3D11.MapFlags.None);
        try
        {
            using Bitmap bitmap = new(_width, _height, PixelFormat.Format32bppArgb);
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, _width, _height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[_width * 4];
                for (int y = 0; y < _height; y++)
                {
                    Marshal.Copy(
                        IntPtr.Add(mapped.DataPointer, checked(y * (int)mapped.RowPitch)),
                        row,
                        0,
                        row.Length);
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            bitmap.Save(path, ImageFormat.Png);
        }
        finally
        {
            _deviceService.Context.Unmap(staging, 0);
        }
    }

    private void CreateSizeDependentResources()
    {
        _sceneTexture = _deviceService.CreateTexture(
            "production-scene-target",
            (uint)_width,
            (uint)_height,
            Format.B8G8R8A8_UNorm,
            BindFlags.RenderTarget | BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            _generations);
        _sceneRenderTargetView = _deviceService.Device.CreateRenderTargetView(
            _sceneTexture.Value);
        _foregroundTexture = _deviceService.CreateTexture(
            "production-foreground-target",
            (uint)_width,
            (uint)_height,
            Format.B8G8R8A8_UNorm,
            BindFlags.RenderTarget | BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            _generations);
        _foregroundRenderTargetView =
            _deviceService.Device.CreateRenderTargetView(_foregroundTexture.Value);
        _foregroundView = _deviceService.CreateShaderResourceView(
            "production-foreground-view",
            _foregroundTexture.Value,
            _generations);
        _foregroundPainter = Direct2DForegroundPainter.Create(
            _foregroundTexture.Value,
            _material,
            Direct2DForegroundPainter.ResolveFontScale(_dpi, _userScale),
            _runtimeSettings.ForegroundTheme);
    }

    private void RecordPresentTiming(long renderStartedTimestamp)
    {
        long presentedTimestamp = Stopwatch.GetTimestamp();
        if (_lastPresentTimestamp != 0)
        {
            _lastPresentIntervalMilliseconds =
                (presentedTimestamp - _lastPresentTimestamp) * 1000.0 /
                Stopwatch.Frequency;
            _presentIntervals.Add(_lastPresentIntervalMilliseconds.Value);
        }

        _renderDurations.Add(
            (presentedTimestamp - renderStartedTimestamp) * 1000.0 /
            Stopwatch.Frequency);
        _lastPresentTimestamp = presentedTimestamp;

        Result statisticsResult = _swapChain.GetFrameStatistics(
            out FrameStatistics statistics);
        if (statisticsResult.Failure)
        {
            return;
        }

        _lastPresentCount = statistics.PresentCount;
        _lastSyncRefreshCount = statistics.SyncRefreshCount;
        if (statistics.SyncQPCTime <= 0 ||
            statistics.SyncQPCTime > presentedTimestamp)
        {
            return;
        }

        double frameAgeMilliseconds =
            (presentedTimestamp - statistics.SyncQPCTime) * 1000.0 /
            Stopwatch.Frequency;
        if (!double.IsFinite(frameAgeMilliseconds) ||
            frameAgeMilliseconds is < 0 or > 5000)
        {
            return;
        }

        _lastCompositorFrameAgeMilliseconds = frameAgeMilliseconds;
        _compositorFrameAges.Add(frameAgeMilliseconds);
    }

    private void TryCreateGpuTimestampQueries()
    {
        const int querySetCount = 3;
        try
        {
            for (int index = 0; index < querySetCount; index++)
            {
                GpuResourceLease? lease = null;
                ID3D11Query? disjoint = null;
                ID3D11Query? start = null;
                ID3D11Query? end = null;
                try
                {
                    lease = _ledger.Reserve(
                        GpuResourceKind.Query,
                        bytes: 0,
                        $"production-gpu-timestamp-{index}",
                        _generations);
                    disjoint = _deviceService.Device.CreateQuery(
                        QueryType.TimestampDisjoint,
                        QueryFlags.None);
                    start = _deviceService.Device.CreateQuery(
                        QueryType.Timestamp,
                        QueryFlags.None);
                    end = _deviceService.Device.CreateQuery(
                        QueryType.Timestamp,
                        QueryFlags.None);
                    _gpuTimestampLeases.Add(lease);
                    _gpuTimestampQueries.Add(
                        new GpuTimestampQuerySet(disjoint, start, end));
                }
                catch
                {
                    end?.Dispose();
                    start?.Dispose();
                    disjoint?.Dispose();
                    lease?.Dispose();
                    throw;
                }
            }
        }
        catch (Exception exception) when (
            exception is SharpGenException or
            InvalidOperationException or
            COMException)
        {
            foreach (GpuTimestampQuerySet querySet in _gpuTimestampQueries)
            {
                querySet.Dispose();
            }

            _gpuTimestampQueries.Clear();
            foreach (GpuResourceLease lease in _gpuTimestampLeases)
            {
                lease.Dispose();
            }

            _gpuTimestampLeases.Clear();
            _lastGpuPassMilliseconds = null;
            _gpuTimestampStatus = "unavailable";
            _gpuTimestampUnavailableReason =
                $"{exception.GetType().Name}: {exception.Message}";
            return;
        }

        _gpuTimestampStatus = "created_no_completed_samples";
    }

    private GpuTimestampQuerySet? TakeAvailableTimestampQueries()
    {
        foreach (GpuTimestampQuerySet querySet in _gpuTimestampQueries)
        {
            if (querySet.InFlight)
            {
                continue;
            }

            querySet.InFlight = true;
            return querySet;
        }

        return null;
    }

    private void PollGpuTimestampQueries()
    {
        foreach (GpuTimestampQuerySet querySet in _gpuTimestampQueries)
        {
            if (!querySet.InFlight)
            {
                continue;
            }

            if (!_deviceService.Context.GetData(
                    querySet.Disjoint,
                    AsyncGetDataFlags.DoNotFlush,
                    out QueryDataTimestampDisjoint disjoint) ||
                !_deviceService.Context.GetData(
                    querySet.Start,
                    AsyncGetDataFlags.DoNotFlush,
                    out ulong start) ||
                !_deviceService.Context.GetData(
                    querySet.End,
                    AsyncGetDataFlags.DoNotFlush,
                    out ulong end))
            {
                continue;
            }

            querySet.InFlight = false;
            if (disjoint.Disjoint ||
                disjoint.Frequency == 0 ||
                end < start)
            {
                continue;
            }

            double milliseconds =
                (end - start) * 1000.0 / disjoint.Frequency;
            if (!double.IsFinite(milliseconds) ||
                milliseconds is < 0 or > 5000)
            {
                continue;
            }

            _lastGpuPassMilliseconds = milliseconds;
            _gpuPassDurations.Add(milliseconds);
            _gpuTimestampStatus = "samples_available";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopLiveCapture();
        ClearFixedBackground();
        _foregroundPainter?.Dispose();
        _foregroundPainter = null;
        _foregroundView?.Dispose();
        _foregroundView = null;
        _foregroundRenderTargetView?.Dispose();
        _foregroundRenderTargetView = null;
        _foregroundTexture?.Dispose();
        _foregroundTexture = null;
        _sceneRenderTargetView?.Dispose();
        _sceneRenderTargetView = null;
        _sceneTexture?.Dispose();
        _sceneTexture = null;
        foreach (GpuTimestampQuerySet querySet in _gpuTimestampQueries)
        {
            querySet.Dispose();
        }

        _gpuTimestampQueries.Clear();
        foreach (GpuResourceLease lease in _gpuTimestampLeases)
        {
            lease.Dispose();
        }

        _gpuTimestampLeases.Clear();
        _rasterizer?.Dispose();
        _sampler?.Dispose();
        _constants?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _emptyView?.Dispose();
        _emptyTexture?.Dispose();
        _swapChain?.Dispose();
        _factory?.Dispose();
        _swapChainLease?.Dispose();
        _shaderLease?.Dispose();
        _deviceService.Dispose();
    }

    private sealed class GpuTimestampQuerySet : IDisposable
    {
        public GpuTimestampQuerySet(
            ID3D11Query disjoint,
            ID3D11Query start,
            ID3D11Query end)
        {
            Disjoint = disjoint;
            Start = start;
            End = end;
        }

        public ID3D11Query Disjoint { get; }

        public ID3D11Query Start { get; }

        public ID3D11Query End { get; }

        public bool InFlight { get; set; }

        public void Dispose()
        {
            End.Dispose();
            Start.Dispose();
            Disjoint.Dispose();
        }
    }
}
