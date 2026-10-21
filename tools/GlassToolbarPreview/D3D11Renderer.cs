using System.Diagnostics;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

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
    private readonly ID3D11Texture2D _backgroundTexture;
    private readonly ID3D11ShaderResourceView _backgroundView;
    private readonly ID3D11RenderTargetView _renderTargetView;
    private readonly ID3D11Texture2D _sceneTexture;
    private readonly ID3D11RenderTargetView _sceneRenderTargetView;
    private readonly ID3D11Texture2D _foregroundTexture;
    private readonly ID3D11RenderTargetView _foregroundRenderTargetView;
    private readonly ID3D11ShaderResourceView _foregroundView;
    private readonly ForegroundOverlay _foreground;
    private readonly string _adapterDescription;
    private readonly string _fixtureSha256;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _foregroundRendered;
    private bool _disposed;
    private readonly LabOptions _options;

    public D3D11Renderer(Form form, LabOptions options)
    {
        _form = form;
        _options = options;
        int width = form.ClientSize.Width;
        int height = form.ClientSize.Height;

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

            _swapChain = factory.CreateSwapChainForHwnd(_device, form.Handle, description, null, null);
            factory.MakeWindowAssociation(form.Handle, WindowAssociationFlags.IgnoreAltEnter);
        }

        byte[] vertexBytecode = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Shaders", "MaterialVS.cso"));
        byte[] pixelBytecode = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Shaders", "MaterialPS.cso"));
        _vertexShader = _device.CreateVertexShader(vertexBytecode);
        _pixelShader = _device.CreatePixelShader(pixelBytecode);
        _constants = _device.CreateBuffer(
            (uint)Marshal.SizeOf<ShaderConstants>(),
            BindFlags.ConstantBuffer);
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _rasterizer = _device.CreateRasterizerState(RasterizerDescription.CullNone);

        FixedBackground fixture = FixedBackground.Create();
        _fixtureSha256 = fixture.Sha256;
        _backgroundTexture = _device.CreateTexture2D(
            Format.B8G8R8A8_UNorm,
            (uint)fixture.Width,
            (uint)fixture.Height,
            bindFlags: BindFlags.ShaderResource);
        _context.UpdateSubresource(
            fixture.Pixels,
            _backgroundTexture,
            rowPitch: (uint)(fixture.Width * 4),
            depthPitch: (uint)fixture.Pixels.Length);
        _backgroundView = _device.CreateShaderResourceView(_backgroundTexture);

        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device.CreateRenderTargetView(backBuffer);
        _sceneTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
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
            Width = (uint)width,
            Height = (uint)height,
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

    public string AdapterDescription => _adapterDescription;

    public void Render()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int width = _form.ClientSize.Width;
        int height = _form.ClientSize.Height;

        if (!_foregroundRendered)
        {
            _context.ClearRenderTargetView(_foregroundRenderTargetView, new Color4(0, 0, 0, 0));
            _foreground.Render();
            _context.Flush();
            _foregroundRendered = true;
        }

        bool legacy = _options.Diagnostic.StartsWith("legacy", StringComparison.Ordinal);
        ShaderConstants constants = new()
        {
            Viewport = new Vector4(width, height, 0, (float)_clock.Elapsed.TotalSeconds),
            Panel0 = new Vector4(406, 50, 468, 800),
            Panel1 = Vector4.Zero,
            Panel2 = Vector4.Zero,
            Panel3 = Vector4.Zero,
            Theme0 = new Vector4(0.78f, 0.83f, 0.90f, legacy ? 0.052f : 0.038f),
            Theme1 = new Vector4(30.0f, 8.0f, legacy ? 12.0f : 8.0f, legacy ? 0.25f : 0.16f),
            Theme2 = new Vector4(8.0f, 30.0f, 0.115f, 0.72f),
            Theme3 = new Vector4(0.94f, 0.97f, 1.0f, 1.0f),
            Desktop = new Vector4(0, 0, FixedBackground.FixtureWidth, FixedBackground.FixtureHeight),
            Window = new Vector4(0, 0, width, height),
            Bars = new Vector4(0.238f, 0.346f, 0.875f, 0.194f),
            Flags = new Vector4(legacy ? 1 : 0,
                _options.Diagnostic.EndsWith("no-shadow", StringComparison.Ordinal) ? 1 : 0,
                _options.Diagnostic == "no-refraction" ? 1 : 0, 0)
        };

        _context.UpdateSubresource(in constants, _constants);
        _context.OMSetRenderTargets(_sceneRenderTargetView);
        _context.RSSetViewport(0, 0, width, height);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.RSSetState(_rasterizer);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.VSSetConstantBuffer(0, _constants);
        _context.PSSetConstantBuffer(0, _constants);
        _context.PSSetShaderResource(0, _backgroundView);
        _context.PSSetShaderResource(1, _foregroundView);
        _context.PSSetSampler(0, _sampler);
        _context.Draw(3, 0);

        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _context.CopyResource(backBuffer, _sceneTexture);
        _swapChain.Present(1, PresentFlags.None).CheckError();
    }

    public void CaptureBackBuffer(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        int width = _form.ClientSize.Width;
        int height = _form.ClientSize.Height;
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

        _context.CopyResource(staging, _sceneTexture);
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
                byte* source = (byte*)mapped.DataPointer;
                byte* destination = (byte*)data.Scan0;
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(
                        source + (long)y * mapped.RowPitch,
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

        File.WriteAllText(
            Path.ChangeExtension(path, ".diag.json"),
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    adapter = _adapterDescription,
                    materialRevision = "toolbar-light-mist-v4",
                    diagnostic = _options.Diagnostic,
                    output = new { width, height, format = "B8G8R8A8_UNorm" },
                    glassPanel = new { x = 406, y = 50, width = 468, height = 800, radius = 30 },
                    fixture = new
                    {
                        width = FixedBackground.FixtureWidth,
                        height = FixedBackground.FixtureHeight,
                        sha256 = _fixtureSha256,
                        generatedOnce = true
                    },
                    capturedAt = DateTimeOffset.Now
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
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
        _foreground.Dispose();
        _foregroundView.Dispose();
        _foregroundRenderTargetView.Dispose();
        _foregroundTexture.Dispose();
        _sceneRenderTargetView.Dispose();
        _sceneTexture.Dispose();
        _renderTargetView.Dispose();
        _backgroundView.Dispose();
        _backgroundTexture.Dispose();
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
