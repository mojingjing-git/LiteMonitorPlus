using System.Drawing.Imaging;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using DrawingSize = System.Drawing.Size;

namespace AppearanceMaterialLab;

internal sealed unsafe class D3D11Renderer : IDisposable
{
    private readonly Form _form;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11Texture2D _fixedBackgroundTexture;
    private readonly ID3D11ShaderResourceView _fixedBackgroundView;
    private readonly ID3D11Texture2D _blackTexture;
    private readonly ID3D11ShaderResourceView _blackView;
    private readonly string _adapterDescription;
    private readonly string _fixtureSha256;
    private ID3D11RenderTargetView? _renderTargetView;
    private ID3D11Texture2D? _sceneTexture;
    private ID3D11RenderTargetView? _sceneRenderTargetView;
    private ID3D11Texture2D? _foregroundTexture;
    private ID3D11RenderTargetView? _foregroundRenderTargetView;
    private ID3D11ShaderResourceView? _foregroundView;
    private DesktopDuplicator? _duplicator;
    private ID3D11Texture2D? _liveBackgroundTexture;
    private ID3D11ShaderResourceView? _liveBackgroundView;
    private CaptureAttemptResult _lastCaptureResult;
    private IntPtr _affinityWindow;
    private bool _excludeRequested;
    private readonly Stopwatch _captureClock = Stopwatch.StartNew();
    private long _nextCaptureAtMilliseconds;
    private ForegroundOverlay? _foreground;
    private DrawingSize _clientSize;
    private bool _disposed;

    public D3D11Renderer(Form form, LabMode mode, bool liveCapture)
    {
        _form = form;
        Mode = mode;
        LiveCapture = liveCapture;
        _clientSize = form.ClientSize;

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
            out _device,
            out _context).CheckError();

        using (IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        using (IDXGIAdapter adapter = dxgiDevice.GetAdapter())
        using (IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>())
        {
            using IDXGIAdapter1 adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
            _adapterDescription = adapter1.Description1.Description;
            SwapChainDescription1 description = new()
            {
                Width = (uint)_clientSize.Width,
                Height = (uint)_clientSize.Height,
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

            _swapChain = factory.CreateSwapChainForHwnd(_device, form.Handle, description, null, null);
            factory.MakeWindowAssociation(form.Handle, WindowAssociationFlags.IgnoreAltEnter);
        }

        byte[] vertexBytecode = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Shaders", "MaterialVS.cso"));
        byte[] pixelBytecode = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Shaders", "MaterialPS.cso"));
        _vertexShader = _device.CreateVertexShader(vertexBytecode);
        _pixelShader = _device.CreatePixelShader(pixelBytecode);
        _constants = _device.CreateBuffer(
            (uint)Marshal.SizeOf<ShaderConstants>(),
            BindFlags.ConstantBuffer);
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _rasterizer = _device.CreateRasterizerState(RasterizerDescription.CullNone);

        FixedBackground fixture = FixedBackground.Create();
        _fixtureSha256 = fixture.Sha256;
        _fixedBackgroundTexture = _device.CreateTexture2D(
            Format.B8G8R8A8_UNorm,
            (uint)fixture.Width,
            (uint)fixture.Height,
            bindFlags: BindFlags.ShaderResource);
        _context.UpdateSubresource(
            fixture.Pixels,
            _fixedBackgroundTexture,
            rowPitch: (uint)(fixture.Width * 4),
            depthPitch: (uint)fixture.Pixels.Length);
        _fixedBackgroundView = _device.CreateShaderResourceView(_fixedBackgroundTexture);
        byte[] blackPixel = [0, 0, 0, 255];
        _blackTexture = _device.CreateTexture2D(
            blackPixel,
            Format.B8G8R8A8_UNorm,
            1,
            1,
            bindFlags: BindFlags.ShaderResource);
        _blackView = _device.CreateShaderResourceView(_blackTexture);

        CreateSwapChainResources();
    }

    public LabMode Mode { get; set; }

    public bool LiveCapture { get; set; }

    public double Seconds { get; set; }

    public void Resize(DrawingSize clientSize)
    {
        if (clientSize.Width <= 0 || clientSize.Height <= 0 || clientSize == _clientSize)
        {
            return;
        }

        _context.UnsetRenderTargets();
        DisposeSwapChainResources();
        _contractSwapChainResize(clientSize);
        _clientSize = clientSize;
        CreateSwapChainResources();
    }

    public void Render()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_renderTargetView is null || _foreground is null)
        {
            return;
        }

        EnsureBackgroundSource();
        if (LiveCapture && _duplicator is not null && _liveBackgroundTexture is not null &&
            _captureClock.ElapsedMilliseconds >= _nextCaptureAtMilliseconds)
        {
            _lastCaptureResult = _duplicator.TryCopyFrame(_liveBackgroundTexture);
            _nextCaptureAtMilliseconds = _captureClock.ElapsedMilliseconds + 33;
        }

        bool hasLiveFrame = LiveCapture && _duplicator?.HasFrame == true;
        if (_foreground.NeedsRender(Mode, hasLiveFrame, LiveCapture))
        {
            _context.ClearRenderTargetView(_foregroundRenderTargetView, new Color4(0, 0, 0, 0));
            _foreground.Render(Mode, hasLiveFrame, LiveCapture);
            _context.Flush();
        }
        Rectangle bounds = _form.RectangleToScreen(_form.ClientRectangle);
        FixedBackground fixture = FixedBackground.Create();
        ShaderConstants constants = new()
        {
            Viewport = new Vector4(_clientSize.Width, _clientSize.Height, Mode == LabMode.Glass ? 0 : 1, (float)Seconds),
            Panel0 = Mode == LabMode.Glass
                ? new Vector4(48, 64, 360, 240)
                : new Vector4(32, 32, 730, 736),
            Panel1 = Mode == LabMode.Glass
                ? new Vector4(432, 64, 800, 326)
                : new Vector4(778, 32, 470, 244),
            Panel2 = Mode == LabMode.Glass
                ? new Vector4(48, 328, 360, 256)
                : new Vector4(778, 292, 470, 476),
            Panel3 = Mode == LabMode.Glass
                ? new Vector4(432, 414, 800, 322)
                : Vector4.Zero,
            Theme0 = Mode == LabMode.Glass
                ? new Vector4(0.72f, 0.88f, 0.98f, 0.065f)
                : new Vector4(0.052f, 0.092f, 0.122f, 0.95f),
            Theme1 = Mode == LabMode.Glass
                ? new Vector4(26, 14, 1.6f, 8.0f)
                : new Vector4(16, 9, 1.5f, 6.0f),
            Theme2 = Mode == LabMode.Glass
                ? new Vector4(0.025f, 0.065f, 0.10f, 0.40f)
                : new Vector4(0.24f, 0.37f, 0.45f, 1.0f),
            Theme3 = Mode == LabMode.Glass
                ? new Vector4(0.86f, 0.97f, 1.0f, 1.0f)
                : new Vector4(0.24f, 0.88f, 0.94f, 1.0f),
            Desktop = new Vector4(0, 0, FixedBackground.FixtureWidth, FixedBackground.FixtureHeight),
            Window = new Vector4(220, 100, _clientSize.Width, _clientSize.Height),
            Bars = new Vector4(0.62f, 0.78f, 0.248f, 0.41f),
            Flags = new Vector4(LiveCapture ? 1 : 0, 0, 0, 0)
        };

        if (LiveCapture)
        {
            if (_duplicator is not null)
            {
                Rectangle captureBounds = _duplicator.Bounds;
                constants.Desktop = new Vector4(
                    captureBounds.Left,
                    captureBounds.Top,
                    captureBounds.Width,
                    captureBounds.Height);
                constants.Window = new Vector4(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            }
        }
        else
        {
            // Map the fixed fixture into the same desktop-sized image as the final sample host.
            constants.Desktop = new Vector4(0, 0, fixture.Width, fixture.Height);
            constants.Window = new Vector4(
                Math.Max(0, (fixture.Width - _clientSize.Width) / 2),
                Math.Max(0, (fixture.Height - _clientSize.Height) / 2),
                _clientSize.Width,
                _clientSize.Height);
        }

        _context.UpdateSubresource(in constants, _constants);
        _context.OMSetRenderTargets(_sceneRenderTargetView ?? _renderTargetView);
        _context.RSSetViewport(0, 0, _clientSize.Width, _clientSize.Height);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.RSSetState(_rasterizer);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.VSSetConstantBuffer(0, _constants);
        _context.PSSetConstantBuffer(0, _constants);
        ID3D11ShaderResourceView activeBackgroundView =
            hasLiveFrame && _liveBackgroundView is not null
                ? _liveBackgroundView
                : LiveCapture
                    ? _blackView
                    : _fixedBackgroundView;
        _context.PSSetShaderResource(0, activeBackgroundView);
        _context.PSSetShaderResource(1, _foregroundView!);
        _context.PSSetSampler(0, _sampler);
        _context.Draw(3, 0);
        if (_sceneTexture is not null)
        {
            using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _context.CopyResource(backBuffer, _sceneTexture);
        }
        _swapChain.Present(1, PresentFlags.None).CheckError();
    }

    public string AdapterDescription => _adapterDescription;

    public void SetExcludedFromCapture(IntPtr windowHandle, bool excluded)
    {
        if (!NativeMethods.IsWindow(windowHandle))
        {
            return;
        }

        uint affinity = excluded
            ? NativeMethods.WdaExcludeFromCapture
            : NativeMethods.WdaNone;
        if (!NativeMethods.SetWindowDisplayAffinity(windowHandle, affinity))
        {
            throw new InvalidOperationException(
                $"SetWindowDisplayAffinity failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        _affinityWindow = windowHandle;
        _excludeRequested = excluded;
    }

    public void CaptureBackBuffer(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ID3D11Texture2D backBuffer = _sceneTexture
            ?? throw new InvalidOperationException("The scene render target is unavailable.");
        using ID3D11Texture2D staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_clientSize.Width,
            Height = (uint)_clientSize.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });

        _context.CopyResource(staging, backBuffer);
        MappedSubresource mapped = _context.Map(
            staging,
            0,
            MapMode.Read,
            Vortice.Direct3D11.MapFlags.None);
        try
        {
            using Bitmap bitmap = new(_clientSize.Width, _clientSize.Height, PixelFormat.Format32bppArgb);
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                byte* source = (byte*)mapped.DataPointer;
                byte* destination = (byte*)data.Scan0;
                for (int y = 0; y < _clientSize.Height; y++)
                {
                    Buffer.MemoryCopy(
                        source + (long)y * mapped.RowPitch,
                        destination + (long)y * data.Stride,
                        data.Stride,
                        _clientSize.Width * 4L);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            bitmap.Save(path, ImageFormat.Png);

            double? frameAge = _duplicator is null
                ? null
                : _duplicator.LastValidFrameAgeMilliseconds;
            uint actualDisplayAffinity = uint.MaxValue;
            if (_affinityWindow != IntPtr.Zero)
            {
                NativeMethods.GetWindowDisplayAffinity(_affinityWindow, out actualDisplayAffinity);
            }
            ID3D11Texture2D diagnosticTexture = _liveBackgroundTexture ?? _fixedBackgroundTexture;
            Texture2DDescription description = diagnosticTexture.Description;
            File.WriteAllText(
                Path.ChangeExtension(path, ".diag.json"),
                System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        adapter = _adapterDescription,
                        mode = Mode.ToString(),
                        liveCapture = LiveCapture,
                        presentation = new
                        {
                            host = _form.TopLevel ? "hwnd" : "child",
                            _form.FormBorderStyle,
                            _form.TopMost,
                            _form.Opacity,
                            extendedStyle = $"0x{NativeMethods.GetExtendedStyle(_form.Handle):X16}",
                            affinityWindow = $"0x{_affinityWindow.ToInt64():X}",
                            excludeRequested = _excludeRequested,
                            actualDisplayAffinity = $"0x{actualDisplayAffinity:X8}"
                        },
                        clientSize = new { _clientSize.Width, _clientSize.Height },
                        backgroundTexture = new
                        {
                            description.Width,
                            description.Height,
                            format = description.Format.ToString(),
                            bindFlags = description.BindFlags.ToString()
                        },
                        fixtureSha256 = _fixtureSha256,
                        captureResult = _lastCaptureResult.ToString(),
                        captureHasFrame = _duplicator?.HasFrame ?? false,
                        captureSampleCount = _duplicator?.CaptureSampleCount ?? 0,
                        captureP95IntervalMs = _duplicator?.P95CaptureIntervalMilliseconds,
                        captureFrameAgeMs = frameAge.HasValue && double.IsFinite(frameAge.Value)
                            ? frameAge.Value
                            : (double?)null,
                        captureBounds = _duplicator?.Bounds,
                        shaderSourceSha256 = "see evidence manifest",
                        capturedAt = DateTimeOffset.Now
                    },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            SaveTextureToPng(
                diagnosticTexture,
                (int)diagnosticTexture.Description.Width,
                (int)diagnosticTexture.Description.Height,
                Path.ChangeExtension(path, ".background.png"));
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    private void SaveTextureToPng(ID3D11Texture2D source, int width, int height, string path)
    {
        using ID3D11Texture2D staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });
        _context.CopyResource(staging, source);
        MappedSubresource mapped = _context.Map(
            staging,
            0,
            MapMode.Read,
            Vortice.Direct3D11.MapFlags.None);
        try
        {
            using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                byte* sourcePixels = (byte*)mapped.DataPointer;
                byte* destination = (byte*)data.Scan0;
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(
                        sourcePixels + (long)y * mapped.RowPitch,
                        destination + (long)y * data.Stride,
                        data.Stride,
                        width * 4L);
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
            _context.Unmap(staging, 0);
        }
    }

    private void CreateSwapChainResources()
    {
        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device.CreateRenderTargetView(backBuffer);
        _sceneTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_clientSize.Width,
            Height = (uint)_clientSize.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });
        _sceneRenderTargetView = _device.CreateRenderTargetView(_sceneTexture);
        _foregroundTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_clientSize.Width,
            Height = (uint)_clientSize.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });
        _foregroundRenderTargetView = _device.CreateRenderTargetView(_foregroundTexture);
        _foregroundView = _device.CreateShaderResourceView(_foregroundTexture);
        _foreground = new ForegroundOverlay(_device, _foregroundTexture);
    }

    private void EnsureBackgroundSource()
    {
        if (LiveCapture && _duplicator is null)
        {
            _duplicator = new DesktopDuplicator(_device, _context, _swapChain);
            Rectangle bounds = _duplicator.Bounds;
            _liveBackgroundTexture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)bounds.Width,
                Height = (uint)bounds.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            });
            _liveBackgroundView = _device.CreateShaderResourceView(_liveBackgroundTexture);
            using ID3D11RenderTargetView clearView = _device.CreateRenderTargetView(_liveBackgroundTexture);
            _context.ClearRenderTargetView(clearView, new Color4(0, 0, 0, 1));
            _lastCaptureResult = CaptureAttemptResult.NoFrameYet;
            _nextCaptureAtMilliseconds = 0;
            _captureClock.Restart();
        }
        else if (!LiveCapture && _duplicator is not null)
        {
            _liveBackgroundView?.Dispose();
            _liveBackgroundView = null;
            _liveBackgroundTexture?.Dispose();
            _liveBackgroundTexture = null;
            _duplicator.Dispose();
            _duplicator = null;
        }
    }

    private void DisposeSwapChainResources()
    {
        _foreground?.Dispose();
        _foreground = null;
        _foregroundView?.Dispose();
        _foregroundView = null;
        _foregroundRenderTargetView?.Dispose();
        _foregroundRenderTargetView = null;
        _foregroundTexture?.Dispose();
        _foregroundTexture = null;
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        _sceneRenderTargetView?.Dispose();
        _sceneRenderTargetView = null;
        _sceneTexture?.Dispose();
        _sceneTexture = null;
    }

    private void _contractSwapChainResize(DrawingSize clientSize)
    {
        _swapChain.ResizeBuffers(
            0,
            (uint)clientSize.Width,
            (uint)clientSize.Height,
            Format.B8G8R8A8_UNorm,
            SwapChainFlags.None).CheckError();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _context.ClearState();
        _context.Flush();
        DisposeSwapChainResources();
        _liveBackgroundView?.Dispose();
        _liveBackgroundTexture?.Dispose();
        _duplicator?.Dispose();
        _fixedBackgroundView.Dispose();
        _fixedBackgroundTexture.Dispose();
        _blackView.Dispose();
        _blackTexture.Dispose();
        _rasterizer.Dispose();
        _sampler.Dispose();
        _constants.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderConstants
    {
        public Vector4 Viewport;
        public Vector4 Panel0;
        public Vector4 Panel1;
        public Vector4 Panel2;
        public Vector4 Panel3;
        public Vector4 Theme0;
        public Vector4 Theme1;
        public Vector4 Theme2;
        public Vector4 Theme3;
        public Vector4 Desktop;
        public Vector4 Window;
        public Vector4 Bars;
        public Vector4 Flags;
    }
}
