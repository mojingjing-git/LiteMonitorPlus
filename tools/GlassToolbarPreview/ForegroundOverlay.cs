using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Vortice.Direct2D1.D2D1;
using static Vortice.DirectWrite.DWrite;

namespace AppearanceMaterialLab;

internal sealed class ForegroundOverlay : IDisposable
{
    private const float PanelLeft = 406;
    private const float PanelTop = 50;
    private const float PanelWidth = 468;
    private const float PanelHeight = 800;
    private const float ContentLeft = 448;
    private const float ContentRight = 832;
    private const float ContentWidth = ContentRight - ContentLeft;

    private readonly ID2D1Factory7 _factory;
    private readonly IDWriteFactory _writeFactory;
    private readonly ID2D1RenderTarget _target;
    private readonly IDWriteTextFormat _title;
    private readonly IDWriteTextFormat _number;
    private readonly IDWriteTextFormat _numberSmall;
    private readonly IDWriteTextFormat _label;
    private readonly IDWriteTextFormat _meta;
    private readonly IDWriteTextFormat _labelRight;
    private readonly IDWriteTextFormat _metaRight;
    private readonly ID2D1SolidColorBrush _text;
    private readonly ID2D1SolidColorBrush _muted;
    private readonly ID2D1SolidColorBrush _faint;
    private readonly ID2D1SolidColorBrush _accent;
    private readonly ID2D1SolidColorBrush _line;
    private readonly ID2D1SolidColorBrush _track;
    private readonly ID2D1SolidColorBrush _highlight;

    public ForegroundOverlay(ID3D11Device device, ID3D11Texture2D texture)
    {
        using IDXGISurface surface = texture.QueryInterface<IDXGISurface>();
        _writeFactory = DWriteCreateFactory<IDWriteFactory>();
        _factory = D2D1CreateFactory<ID2D1Factory7>(
            Vortice.Direct2D1.FactoryType.SingleThreaded,
            DebugLevel.Information);

        RenderTargetProperties properties = new()
        {
            DpiX = 96,
            DpiY = 96,
            MinLevel = Vortice.Direct2D1.FeatureLevel.Default,
            PixelFormat = Vortice.DCommon.PixelFormat.Premultiplied,
            Type = RenderTargetType.Hardware,
            Usage = RenderTargetUsage.None
        };

        _target = _factory.CreateDxgiSurfaceRenderTarget(surface, properties);
        _target.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        _title = CreateTextFormat("Segoe UI Variable Display", 22.0f, FontWeight.SemiBold);
        _number = CreateTextFormat("Segoe UI Variable Display", 26.0f, FontWeight.SemiBold);
        _numberSmall = CreateTextFormat("Segoe UI Variable Display", 18.0f, FontWeight.SemiBold);
        _label = CreateTextFormat("Microsoft YaHei UI", 10.5f, FontWeight.SemiBold);
        _meta = CreateTextFormat("Microsoft YaHei UI", 11.5f, FontWeight.Normal);
        _labelRight = CreateRightAlignedFormat("Microsoft YaHei UI", 10.5f, FontWeight.SemiBold);
        _metaRight = CreateRightAlignedFormat("Microsoft YaHei UI", 11.5f, FontWeight.Normal);

        _text = _target.CreateSolidColorBrush(new Color4(0.095f, 0.138f, 0.176f, 1.0f));
        _muted = _target.CreateSolidColorBrush(new Color4(0.322f, 0.380f, 0.427f, 1.0f));
        _faint = _target.CreateSolidColorBrush(new Color4(0.322f, 0.380f, 0.427f, 0.62f));
        _accent = _target.CreateSolidColorBrush(new Color4(0.157f, 0.494f, 0.525f, 1.0f));
        _line = _target.CreateSolidColorBrush(new Color4(0.36f, 0.43f, 0.50f, 0.25f));
        _track = _target.CreateSolidColorBrush(new Color4(0.30f, 0.38f, 0.45f, 0.12f));
        _highlight = _target.CreateSolidColorBrush(new Color4(0.98f, 0.99f, 1.0f, 0.72f));
    }

    public void Render()
    {
        _target.BeginDraw();
        _target.Clear(new Color4(0, 0, 0, 0));

        DrawText("LiteMonitor++", ContentLeft, PanelTop + 28, 190, 32, _title, _text);
        DrawText("实时监控", ContentLeft, PanelTop + 58, 180, 18, _label, _muted);
        FillRect(ContentRight - 45, PanelTop + 37, 5, 5, _accent);
        DrawText("实时", ContentRight - 35, PanelTop + 31, 35, 18, _labelRight, _accent);
        DrawRule(ContentLeft, PanelTop + 80, ContentWidth, _line, 0.7f);

        DrawCpu(PanelTop + 92);
        DrawGpu(PanelTop + 200);
        DrawMemory(PanelTop + 308);
        DrawDisk(PanelTop + 416);
        DrawNetwork(PanelTop + 524);
        DrawTraffic(PanelTop + 632);

        _target.EndDraw().CheckError();
    }

    private void DrawCpu(float top)
    {
        DrawText("CPU", ContentLeft, top + 3, 100, 18, _label, _muted);
        DrawText("温度", ContentLeft, top + 3, ContentWidth, 18, _labelRight, _faint);
        DrawText("23.8%", ContentLeft, top + 20, 210, 40, _number, _text);
        DrawText("51 °C", ContentLeft, top + 25, ContentWidth, 26, _metaRight, _text);
        DrawMeter(ContentLeft, top + 69, ContentWidth, 3.0f, 0.238f);
        DrawRule(ContentLeft, top + 92, ContentWidth, _line, 0.55f);
    }

    private void DrawGpu(float top)
    {
        DrawText("GPU", ContentLeft, top + 3, 100, 18, _label, _muted);
        DrawText("58 °C", ContentLeft, top + 25, ContentWidth, 26, _metaRight, _text);
        DrawText("34.6%", ContentLeft, top + 20, 210, 40, _number, _text);
        DrawText("显存  6.2 GB", ContentLeft, top + 53, ContentWidth, 22, _metaRight, _muted);
        DrawMeter(ContentLeft, top + 78, ContentWidth, 3.0f, 0.346f);
        DrawRule(ContentLeft, top + 100, ContentWidth, _line, 0.55f);
    }

    private void DrawMemory(float top)
    {
        DrawText("内存", ContentLeft, top + 3, 120, 18, _label, _muted);
        DrawText("已用", ContentLeft, top + 3, ContentWidth, 18, _labelRight, _faint);
        DrawText("28 GB", ContentLeft, top + 20, 210, 40, _number, _text);
        DrawMeter(ContentLeft, top + 69, ContentWidth, 3.0f, 0.62f);
        DrawRule(ContentLeft, top + 92, ContentWidth, _line, 0.55f);
    }

    private void DrawDisk(float top)
    {
        DrawText("磁盘", ContentLeft, top + 3, 100, 18, _label, _muted);

        DrawText("读取", ContentLeft, top + 27, 150, 18, _label, _muted);
        DrawText("128 MB/s", ContentLeft, top + 42, 174, 27, _numberSmall, _text);
        DrawMeter(ContentLeft, top + 74, 174, 3.0f, 0.64f);

        DrawText("写入", ContentLeft + 206, top + 27, 178, 18, _labelRight, _muted);
        DrawText("42 MB/s", ContentLeft + 206, top + 42, 178, 27, _numberSmall, _text);
        DrawMeter(ContentLeft + 206, top + 74, 178, 3.0f, 0.21f);
        DrawRule(ContentLeft, top + 92, ContentWidth, _line, 0.55f);
    }

    private void DrawNetwork(float top)
    {
        DrawText("网络", ContentLeft, top + 3, 120, 18, _label, _muted);

        DrawText("上传", ContentLeft, top + 27, 150, 18, _label, _muted);
        DrawText("3.4 MB/s", ContentLeft, top + 42, 174, 27, _numberSmall, _text);
        DrawMeter(ContentLeft, top + 74, 174, 3.0f, 0.14f);

        DrawText("下载", ContentLeft + 206, top + 27, 178, 18, _labelRight, _muted);
        DrawText("24.8 MB/s", ContentLeft + 206, top + 42, 178, 27, _numberSmall, _text);
        DrawMeter(ContentLeft + 206, top + 74, 178, 3.0f, 0.63f);
        DrawRule(ContentLeft, top + 92, ContentWidth, _line, 0.55f);
    }

    private void DrawTraffic(float top)
    {
        DrawText("流量", ContentLeft, top + 3, 120, 18, _label, _muted);
        DrawText("本次统计", ContentLeft, top + 3, ContentWidth, 18, _labelRight, _faint);
        DrawText("1.26 GB / 6.48 GB", ContentLeft, top + 24, ContentWidth, 34, _numberSmall, _text);
        DrawMeter(ContentLeft, top + 71, ContentWidth, 3.0f, 0.194f);
    }

    private IDWriteTextFormat CreateTextFormat(string family, float size, FontWeight weight)
    {
        IDWriteTextFormat format = _writeFactory.CreateTextFormat(
            family,
            weight,
            Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal,
            size);
        format.WordWrapping = WordWrapping.NoWrap;
        format.TextAlignment = Vortice.DirectWrite.TextAlignment.Leading;
        return format;
    }

    private IDWriteTextFormat CreateRightAlignedFormat(string family, float size, FontWeight weight)
    {
        IDWriteTextFormat format = CreateTextFormat(family, size, weight);
        format.TextAlignment = Vortice.DirectWrite.TextAlignment.Trailing;
        return format;
    }

    private void DrawText(
        string value,
        float left,
        float top,
        float width,
        float height,
        IDWriteTextFormat format,
        ID2D1Brush brush)
    {
        _target.DrawText(value, format, new Rect(left, top, width, height), brush);
    }

    private void DrawRule(float left, float top, float width, ID2D1Brush brush, float strokeWidth)
    {
        _target.DrawLine(
            new System.Numerics.Vector2(left, top),
            new System.Numerics.Vector2(left + width, top),
            brush,
            strokeWidth);
    }

    private void FillRect(float left, float top, float width, float height, ID2D1Brush brush)
    {
        _target.FillRectangle(new Rect(left, top, width, height), brush);
    }

    private void DrawMeter(float left, float top, float width, float height, float progress)
    {
        FillRect(left, top, width, height, _track);
        FillRect(left, top, width * Math.Clamp(progress, 0.0f, 1.0f), height, _accent);
        FillRect(left, top, 18.0f, 1.0f, _highlight);
    }

    public void Dispose()
    {
        _highlight.Dispose();
        _track.Dispose();
        _line.Dispose();
        _accent.Dispose();
        _faint.Dispose();
        _muted.Dispose();
        _text.Dispose();
        _metaRight.Dispose();
        _labelRight.Dispose();
        _meta.Dispose();
        _label.Dispose();
        _numberSmall.Dispose();
        _number.Dispose();
        _title.Dispose();
        _target.Dispose();
        _factory.Dispose();
        _writeFactory.Dispose();
    }
}
