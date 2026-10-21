using System.Numerics;
using Vortice.DCommon;
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
    private readonly ID2D1Factory7 _factory;
    private readonly IDWriteFactory _writeFactory;
    private readonly IDWriteTextFormat _display;
    private readonly IDWriteTextFormat _title;
    private readonly IDWriteTextFormat _body;
    private readonly IDWriteTextFormat _caption;
    private readonly ID2D1RenderTarget _target;
    private readonly ID2D1SolidColorBrush _text;
    private readonly ID2D1SolidColorBrush _muted;
    private readonly ID2D1SolidColorBrush _cyan;
    private readonly ID2D1SolidColorBrush _amber;
    private readonly ID2D1SolidColorBrush _danger;
    private readonly ID2D1SolidColorBrush _shadow;
    private readonly ID2D1SolidColorBrush _success;
    private readonly ID2D1SolidColorBrush _line;
    private readonly ID2D1SolidColorBrush _lineStrong;
    private readonly ID2D1SolidColorBrush _track;
    private readonly ID2D1SolidColorBrush _glassFill;
    private readonly ID2D1SolidColorBrush _cyanLow;
    private readonly ID2D1SolidColorBrush _amberLow;
    private readonly ID2D1SolidColorBrush _dangerLow;
    private LabMode? _renderedMode;
    private bool? _renderedLive;
    private bool? _renderedCaptureRequested;

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
        _display = CreateTextFormat("Segoe UI Variable", 34, FontWeight.SemiBold);
        _title = CreateTextFormat("Segoe UI Variable", 18, FontWeight.SemiBold);
        _body = CreateTextFormat("Cascadia Mono", 14, FontWeight.Normal);
        _caption = CreateTextFormat("Cascadia Mono", 11, FontWeight.Normal);
        _text = _target.CreateSolidColorBrush(new Color4(0.96f, 0.98f, 1.0f, 1.0f));
        _muted = _target.CreateSolidColorBrush(new Color4(0.62f, 0.72f, 0.79f, 1.0f));
        _cyan = _target.CreateSolidColorBrush(new Color4(0.28f, 0.90f, 0.95f, 1.0f));
        _amber = _target.CreateSolidColorBrush(new Color4(1.0f, 0.72f, 0.30f, 1.0f));
        _danger = _target.CreateSolidColorBrush(new Color4(1.0f, 0.32f, 0.40f, 1.0f));
        _shadow = _target.CreateSolidColorBrush(new Color4(0.01f, 0.02f, 0.03f, 0.72f));
        _success = _target.CreateSolidColorBrush(new Color4(0.34f, 0.91f, 0.77f, 1.0f));
        _line = _target.CreateSolidColorBrush(new Color4(0.38f, 0.58f, 0.66f, 0.58f));
        _lineStrong = _target.CreateSolidColorBrush(new Color4(0.36f, 0.66f, 0.72f, 0.72f));
        _track = _target.CreateSolidColorBrush(new Color4(0.72f, 0.84f, 0.90f, 0.13f));
        _glassFill = _target.CreateSolidColorBrush(new Color4(0.84f, 0.97f, 1.0f, 0.72f));
        _cyanLow = _target.CreateSolidColorBrush(new Color4(0.28f, 0.90f, 0.95f, 0.18f));
        _amberLow = _target.CreateSolidColorBrush(new Color4(1.0f, 0.72f, 0.30f, 0.18f));
        _dangerLow = _target.CreateSolidColorBrush(new Color4(1.0f, 0.32f, 0.40f, 0.16f));
    }

    public bool NeedsRender(LabMode mode, bool live, bool captureRequested)
    {
        return _renderedMode != mode ||
            _renderedLive != live ||
            _renderedCaptureRequested != captureRequested;
    }

    public void Render(LabMode mode, bool live, bool captureRequested)
    {
        _renderedMode = mode;
        _renderedLive = live;
        _renderedCaptureRequested = captureRequested;
        _target.BeginDraw();
        _target.Clear(new Color4(0, 0, 0, 0));

        if (mode == LabMode.Glass)
        {
            DrawGlass(live, captureRequested);
        }
        else
        {
            DrawCyber();
        }

        _target.EndDraw().CheckError();
    }

    private void DrawGlass(bool live, bool captureRequested)
    {
        DrawShadowedText("SYSTEM PULSE", 80, 96, 250, 30, _title);
        DrawStatusMark(338, 105, _success);
        DrawText("NOMINAL", 350, 98, 74, 20, _caption, _success);
        DrawShadowedText("62%", 80, 138, 220, 62, _display);
        DrawShadowedText("CPU LOAD", 80, 210, 180, 24, _caption);
        DrawGlassMeter(80, 246, 240, 3, 0.62f);
        string sourceLabel = live
            ? "LIVE DESKTOP / GPU MATERIAL"
            : captureRequested
                ? "LIVE CAPTURE / WAITING FRAME"
                : "FIXED MATERIAL PROBE";
        DrawShadowedText(sourceLabel, 80, 274, 300, 22, _caption);

        DrawShadowedText("实时性能 / LIVE TELEMETRY", 464, 92, 570, 32, _title);
        DrawStatusMark(1118, 101, _success);
        DrawText("ALL SENSORS SYNCED", 1130, 94, 116, 20, _caption, _muted);
        DrawRule(464, 132, 736, _line, 0.65f);

        DrawShadowedText("CPU", 464, 146, 120, 22, _caption);
        DrawShadowedText("62%", 464, 170, 210, 50, _display);
        DrawShadowedText("LOAD / 16 THREADS", 646, 190, 132, 20, _caption);
        DrawGlassMeter(464, 224, 276, 3, 0.62f);

        DrawShadowedText("GPU", 812, 146, 120, 22, _caption);
        DrawShadowedText("78%", 812, 170, 210, 50, _display);
        DrawShadowedText("PACKAGE 71°C", 1006, 190, 142, 20, _caption);
        DrawGlassMeter(812, 224, 276, 3, 0.78f);

        DrawShadowedText("MEMORY", 464, 252, 150, 22, _caption);
        DrawShadowedText("54%", 464, 276, 190, 44, _display);
        DrawGlassMeter(464, 326, 276, 3, 0.54f);

        DrawShadowedText("NETWORK", 812, 252, 140, 22, _caption);
        DrawShadowedText("24.8 MB/s", 812, 278, 278, 38, _title);
        DrawGlassMeter(812, 326, 276, 3, 0.248f);
        DrawLine(780, 148, 780, 338, _line, 0.5f);

        DrawStatusMark(464, 356, _success);
        DrawShadowedText("READABLE FOREGROUND / NO REFRACTION", 476, 349, 420, 22, _caption);
        DrawShadowedText("FRAME 016", 1118, 349, 82, 22, _caption);

        DrawShadowedText("GPU PACKAGE", 80, 356, 220, 30, _title);
        DrawStatusMark(338, 365, _amber);
        DrawText("THERMAL", 350, 358, 50, 20, _caption, _amber);
        DrawShadowedText("71°C", 80, 392, 210, 62, _display);
        DrawShadowedText("+12°C HEADROOM", 294, 422, 116, 22, _caption);
        DrawRule(80, 458, 296, _line, 0.55f);
        DrawShadowedText("CPU FAN", 80, 472, 120, 20, _caption);
        DrawShadowedText("1260 RPM", 230, 468, 146, 24, _body);
        DrawShadowedText("CASE FAN", 80, 504, 120, 20, _caption);
        DrawShadowedText("890 RPM", 230, 500, 146, 24, _body);
        DrawStatusMark(80, 542, _success);
        DrawShadowedText("SYSTEM NOMINAL", 92, 536, 220, 22, _caption);

        DrawShadowedText("SENSOR FABRIC", 464, 448, 260, 30, _title);
        DrawShadowedText("4-BAY THERMAL BUS / 64 MS", 980, 454, 222, 20, _caption);
        DrawRule(464, 486, 736, _line, 0.55f);
        DrawGlassCell(464, 500, 176, "CPU FAN", "1260 RPM", "NOMINAL", _success);
        DrawGlassCell(640, 500, 176, "CASE FAN", "890 RPM", "NOMINAL", _success);
        DrawGlassCell(816, 500, 176, "GPU 02", "OFFLINE", "SENSOR LOST", _danger);
        DrawGlassCell(992, 500, 176, "GPU ARRAY", "3 / 4", "ACTIVE", _cyan);
        DrawLine(780, 506, 780, 628, _line, 0.5f);
        DrawRule(464, 646, 736, _line, 0.48f);
        DrawShadowedText("LINK STATE", 464, 670, 120, 20, _caption);
        DrawShadowedText("SYNCHRONIZED", 592, 664, 190, 26, _body);
        DrawShadowedText("LAST PACKET", 980, 670, 100, 20, _caption);
        DrawShadowedText("02 MS AGO", 1080, 664, 120, 26, _body);
    }

    private void DrawCyber()
    {
        DrawCyberDecoration(32, 32, 730, 736, 16);
        DrawCyberDecoration(778, 32, 470, 244, 14);
        DrawCyberDecoration(778, 292, 470, 476, 14);

        DrawText("NODE // REAL-TIME TELEMETRY", 72, 66, 560, 34, _title, _cyan);
        DrawText("GPU ARRAY / CH-01 / LOCKED", 72, 94, 360, 20, _caption, _muted);
        DrawStatusMark(650, 73, _success);
        DrawText("BUS NOMINAL", 662, 66, 84, 20, _caption, _success);
        DrawCyberRule(68, 112, 658);

        DrawText("CPU", 72, 122, 60, 20, _caption, _muted);
        DrawText("62.0%", 72, 140, 230, 48, _display, _text);
        DrawText("PACKAGE 71°C", 300, 146, 160, 24, _body, _amber);
        DrawCyberMeterTicks(72, 196, 650, 10, 0.62f, _cyan);

        DrawText("GPU", 72, 226, 60, 20, _caption, _muted);
        DrawText("78.0%", 72, 244, 230, 48, _display, _text);
        DrawText("BOARD 43°C", 300, 250, 160, 24, _body, _muted);
        DrawCyberMeterTicks(72, 310, 650, 10, 0.78f, _cyan);

        DrawText("MEMORY", 72, 338, 110, 20, _caption, _muted);
        DrawText("54.0%", 72, 358, 230, 38, _title, _text);
        DrawText("18.4 / 32 GB", 560, 362, 160, 20, _caption, _muted);
        DrawCyberMeterTicks(72, 422, 650, 9, 0.54f, _cyan);

        DrawText("NETWORK", 72, 450, 110, 20, _caption, _muted);
        DrawText("24.8 MB/s", 72, 470, 280, 38, _title, _text);
        DrawText("UP 03.4 / DOWN 21.4", 490, 480, 230, 20, _caption, _muted);
        DrawCyberMeterTicks(72, 534, 650, 9, 0.248f, _cyan);

        DrawText("THERMAL ARRAY / 4-GPU BUS", 72, 566, 330, 22, _caption, _muted);
        DrawRule(72, 594, 650, _line, 0.72f);
        DrawHardwareBay(72, 610, 150, 70, "GPU 01", "64°C", "ONLINE", _success);
        DrawHardwareBay(238, 610, 150, 70, "GPU 02", "OFFLINE", "NO SIGNAL", _danger);
        DrawHardwareBay(404, 610, 150, 70, "GPU 03", "67°C", "ONLINE", _success);
        DrawHardwareBay(570, 610, 152, 70, "GPU 04", "72°C", "WARNING", _amber);
        DrawText("SYNC / LOCKED", 72, 720, 220, 22, _caption, _cyan);
        DrawText("FRAME 016 / 16.7 MS", 548, 720, 174, 22, _caption, _muted);

        DrawText("CHANNEL A", 816, 66, 250, 30, _title, _cyan);
        DrawText("NETWORK / NORTHBRIDGE", 816, 96, 250, 20, _caption, _muted);
        DrawStatusMark(1168, 73, _success);
        DrawText("LINK", 1180, 66, 34, 20, _caption, _success);
        DrawCyberRule(812, 118, 400);
        DrawText("24.8 MB/s", 816, 142, 300, 52, _display, _text);
        DrawText("UP  03.4", 816, 206, 112, 22, _body, _muted);
        DrawText("DOWN 21.4", 956, 206, 150, 22, _body, _muted);
        DrawGlassMeter(816, 242, 392, 2, 0.248f);
        DrawText("PING 12 MS", 816, 250, 130, 18, _caption, _muted);
        DrawText("PACKET LOSS 0.0%", 1010, 250, 198, 18, _caption, _muted);

        DrawText("CHANNEL B", 816, 326, 250, 30, _title, _cyan);
        DrawText("THERMAL / AUXILIARY", 816, 356, 250, 20, _caption, _muted);
        DrawStatusMark(1168, 333, _amber);
        DrawText("WARN", 1180, 326, 40, 20, _caption, _amber);
        DrawCyberRule(812, 386, 400);
        DrawText("WARNING / THERMAL", 816, 402, 290, 20, _caption, _amber);
        DrawText("GPU 71°C", 816, 424, 280, 48, _display, _amber);
        DrawText("CPU FAN   1260 RPM", 816, 490, 300, 24, _body, _text);
        DrawText("CASE FAN   890 RPM", 816, 522, 300, 24, _body, _text);
        DrawRule(816, 560, 392, _line, 0.58f);
        DrawText("SYSTEM STATUS", 816, 578, 230, 20, _caption, _muted);
        DrawText("NOMINAL", 816, 602, 260, 48, _display, _cyan);
        DrawText("BAY 01 / 03 ONLINE", 816, 662, 220, 20, _caption, _muted);
        DrawText("GPU 02 OFFLINE", 1052, 662, 156, 20, _caption, _danger);
        DrawText("SENSOR BUS", 816, 716, 120, 20, _caption, _muted);
        DrawText("STABLE / 4 NODES", 942, 716, 210, 20, _caption, _success);
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
        return format;
    }

    private void DrawShadowedText(
        string value,
        float left,
        float top,
        float width,
        float height,
        IDWriteTextFormat format)
    {
        Rect layout = new(left, top, width, height);
        Rect shadowLayout = new(left + 1.5f, top + 2.0f, width, height);
        _target.DrawText(value, format, shadowLayout, _shadow);
        _target.DrawText(value, format, layout, _text);
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
        _target.DrawLine(new Vector2(left, top), new Vector2(left + width, top), brush, strokeWidth);
    }

    private void DrawLine(float x1, float y1, float x2, float y2, ID2D1Brush brush, float strokeWidth)
    {
        _target.DrawLine(new Vector2(x1, y1), new Vector2(x2, y2), brush, strokeWidth);
    }

    private void FillRect(float left, float top, float width, float height, ID2D1Brush brush)
    {
        _target.FillRectangle(new Rect(left, top, width, height), brush);
    }

    private void DrawStatusMark(float left, float top, ID2D1Brush brush)
    {
        FillRect(left, top, 6, 6, brush);
        FillRect(left + 2, top + 2, 2, 2, _text);
    }

    private void DrawGlassMeter(float left, float top, float width, float height, float progress)
    {
        FillRect(left, top, width, height, _track);
        FillRect(left, top, width * Math.Clamp(progress, 0, 1), height, _glassFill);
        for (int x = 0; x <= (int)width; x += 32)
        {
            DrawLine(left + x, top - 2, left + x, top - 1, _line, 0.5f);
        }
    }

    private void DrawGlassCell(
        float left,
        float top,
        float width,
        string label,
        string value,
        string status,
        ID2D1Brush statusBrush)
    {
        FillRect(left, top + 5, 2, 60, statusBrush);
        DrawLine(left + 12, top + 18, left + width - 12, top + 18, _line, 0.6f);
        DrawShadowedText(label, left, top, width, 20, _caption);
        DrawShadowedText(value, left, top + 26, width, 34, _title);
        DrawStatusMark(left, top + 68, statusBrush);
        DrawShadowedText(status, left + 12, top + 62, width - 12, 20, _caption);
    }

    private void DrawCyberDecoration(float left, float top, float width, float height, float cut)
    {
        float right = left + width;
        float bottom = top + height;
        DrawLine(left + cut + 22, top + 7, left + cut + 96, top + 7, _lineStrong, 1.0f);
        DrawLine(right - 116, top + height - 8, right - cut - 24, top + height - 8, _lineStrong, 0.8f);
        DrawLine(left + 8, top + cut + 24, left + 8, top + cut + 92, _line, 0.8f);
        FillRect(left + 20, top + 20, 24, 3, _cyanLow);
        FillRect(right - 54, bottom - 24, 24, 3, _amberLow);
    }

    private void DrawCyberRule(float left, float top, float width)
    {
        _target.DrawLine(new Vector2(left, top), new Vector2(left + width, top), _lineStrong, 0.8f);
        FillRect(left, top - 1, 48, 2, _cyan);
        FillRect(left + width - 36, top - 1, 14, 2, _amber);
        FillRect(left + width - 18, top - 1, 5, 2, _lineStrong);
    }

    private void DrawCyberMeterTicks(
        float left,
        float top,
        float width,
        float height,
        float progress,
        ID2D1Brush accent)
    {
        for (int x = 0; x <= (int)width; x += 24)
        {
            DrawLine(left + x, top - 3, left + x, top - 1, _line, 0.7f);
        }

        float leadingEdge = left + width * Math.Clamp(progress, 0, 1);
        DrawLine(leadingEdge, top - 2, leadingEdge, top + height + 2, accent, 1.3f);
        FillRect(left, top, width, height, _track);
        FillRect(left, top, Math.Max(0, leadingEdge - left), height, accent);
    }

    private void DrawHardwareBay(
        float left,
        float top,
        float width,
        float height,
        string bay,
        string value,
        string status,
        ID2D1Brush statusBrush)
    {
        FillRect(left, top, width, height, _track);
        DrawLine(left, top, left + width, top, _lineStrong, 0.8f);
        DrawLine(left, top + height, left + width, top + height, _line, 0.6f);
        FillRect(left, top, 3, height, statusBrush);
        DrawText(bay, left + 12, top + 8, width - 24, 18, _caption, _muted);
        DrawText(value, left + 12, top + 28, width - 24, 22, _body, _text);
        DrawText(status, left + 12, top + 50, width - 24, 18, _caption, statusBrush);
    }

    public void Dispose()
    {
        _cyanLow.Dispose();
        _amberLow.Dispose();
        _dangerLow.Dispose();
        _glassFill.Dispose();
        _track.Dispose();
        _lineStrong.Dispose();
        _line.Dispose();
        _success.Dispose();
        _shadow.Dispose();
        _danger.Dispose();
        _amber.Dispose();
        _cyan.Dispose();
        _muted.Dispose();
        _text.Dispose();
        _caption.Dispose();
        _body.Dispose();
        _title.Dispose();
        _display.Dispose();
        _target.Dispose();
        _factory.Dispose();
        _writeFactory.Dispose();
    }
}
