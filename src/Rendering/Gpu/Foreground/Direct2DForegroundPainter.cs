using System.Numerics;
using LiteMonitor.Rendering.Contracts;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct2D1.D2D1;
using static Vortice.DirectWrite.DWrite;

namespace LiteMonitor.Rendering.Gpu.Foreground;

/// <summary>
/// Paints the immutable foreground command list into the transparent foreground
/// texture consumed by the material pass. Text never enters refraction or blur.
/// </summary>
public sealed class Direct2DForegroundPainter : IDisposable
{
    private readonly ID2D1Factory7 _factory;
    private readonly IDWriteFactory _writeFactory;
    private readonly ID2D1RenderTarget _target;
    private readonly IDWriteTextFormat _display;
    private readonly IDWriteTextFormat _title;
    private readonly IDWriteTextFormat _body;
    private readonly IDWriteTextFormat _caption;
    private readonly Dictionary<ForegroundTone, ID2D1SolidColorBrush> _brushes;
    private readonly ID2D1SolidColorBrush _shadow;
    private readonly ID2D1SolidColorBrush _rule;
    private readonly ID2D1SolidColorBrush _track;
    private readonly ID2D1SolidColorBrush? _groupTitle;
    private readonly Dictionary<ForegroundTone, ID2D1SolidColorBrush> _barBrushes;
    private string _lastSignature = string.Empty;
    private bool _disposed;

    private Direct2DForegroundPainter(
        ID2D1Factory7 factory,
        IDWriteFactory writeFactory,
        ID2D1RenderTarget target,
        MaterialKind material,
        float dpiScale,
        ForegroundThemeStyle style)
    {
        if (!style.IsValid)
        {
            throw new ArgumentException(
                "The foreground theme style is invalid.",
                nameof(style));
        }

        _factory = factory;
        _writeFactory = writeFactory;
        _target = target;
        _target.TextAntialiasMode =
            Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        float scale = Math.Clamp(dpiScale, 0.5f, 4.0f);
        FontWeight emphasis =
            style.BoldText ? FontWeight.SemiBold : FontWeight.Normal;
        _display = CreateTextFormat(
            style.DisplayFontFamily ?? style.ValueFontFamily,
            style.DisplaySizeDip * scale,
            emphasis);
        _title = CreateTextFormat(
            style.UiFontFamily,
            style.TitleSizeDip * scale,
            emphasis);
        _body = CreateTextFormat(
            style.ValueFontFamily,
            style.BodySizeDip * scale,
            style.UsesThemeTypography ? emphasis : FontWeight.Normal);
        _caption = CreateTextFormat(
            style.CaptionFontFamily ?? style.UiFontFamily,
            style.CaptionSizeDip * scale,
            style.UsesThemeTypography ? emphasis : FontWeight.Normal);

        _brushes = new Dictionary<ForegroundTone, ID2D1SolidColorBrush>
        {
            [ForegroundTone.Primary] = Brush(style.Primary),
            [ForegroundTone.Muted] = Brush(style.Muted),
            [ForegroundTone.Success] = Brush(style.Success),
            [ForegroundTone.Warning] = Brush(style.Warning),
            [ForegroundTone.Danger] = Brush(style.Danger),
            [ForegroundTone.Accent] = Brush(style.Accent)
        };
        _shadow = Brush(style.Shadow);
        _rule = Brush(style.Rule);
        _track = Brush(style.TrackColor ?? style.Rule);
        _groupTitle = style.GroupTitleColor is RgbaColor groupTitle ? Brush(groupTitle) : null;
        _barBrushes = new()
        {
            [ForegroundTone.Success] = Brush(style.BarSafe ?? style.Success),
            [ForegroundTone.Warning] = Brush(style.BarWarn ?? style.Warning),
            [ForegroundTone.Danger] = Brush(style.BarCrit ?? style.Danger)
        };

        ID2D1SolidColorBrush Brush(RgbaColor color) =>
            _target.CreateSolidColorBrush(
                new Color4(color.R, color.G, color.B, color.A));
    }

    public static Direct2DForegroundPainter Create(
        ID3D11Texture2D texture,
        MaterialKind material,
        float dpiScale,
        ForegroundThemeStyle? foregroundTheme = null)
    {
        ArgumentNullException.ThrowIfNull(texture);
        using IDXGISurface surface = texture.QueryInterface<IDXGISurface>();
        ID2D1Factory7? factory = null;
        IDWriteFactory? writeFactory = null;
        ID2D1RenderTarget? target = null;
        try
        {
            factory = D2D1CreateFactory<ID2D1Factory7>(
                Vortice.Direct2D1.FactoryType.SingleThreaded,
                DebugLevel.None);
            writeFactory = DWriteCreateFactory<IDWriteFactory>();

            RenderTargetProperties properties = new()
            {
                DpiX = 96,
                DpiY = 96,
                MinLevel = Vortice.Direct2D1.FeatureLevel.Default,
                PixelFormat = PixelFormat.Premultiplied,
                Type = RenderTargetType.Hardware,
                Usage = RenderTargetUsage.None
            };

            target = factory.CreateDxgiSurfaceRenderTarget(surface, properties);
            Direct2DForegroundPainter painter = new(
                factory,
                writeFactory,
                target,
                material,
                dpiScale,
                foregroundTheme ??
                    ForegroundThemeStyle.Default(material));
            factory = null;
            writeFactory = null;
            target = null;
            return painter;
        }
        catch
        {
            target?.Dispose();
            factory?.Dispose();
            writeFactory?.Dispose();
            throw;
        }
    }

    public static float ResolveFontScale(int dpi, float userScale)
    {
        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi));
        }

        float normalizedUserScale =
            float.IsFinite(userScale) && userScale > 0 ? userScale : 1f;
        return Math.Clamp((dpi / 96f) * normalizedUserScale, 0.5f, 4f);
    }

    public bool NeedsPaint(ForegroundDrawList drawList)
    {
        ArgumentNullException.ThrowIfNull(drawList);
        string signature = BuildSignature(drawList);
        return !string.Equals(signature, _lastSignature, StringComparison.Ordinal);
    }

    public void Paint(ForegroundDrawList drawList, bool force = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(drawList);
        if (!drawList.IsValid)
        {
            throw new ArgumentException("Foreground draw list is invalid.", nameof(drawList));
        }

        string signature = BuildSignature(drawList);
        if (!force && string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastSignature = signature;
        _target.BeginDraw();
        _target.Clear(new Color4(0, 0, 0, 0));
        foreach (ForegroundCommand command in drawList.Commands)
        {
            PaintCommand(command);
        }

        _target.EndDraw().CheckError();
    }

    private void PaintCommand(ForegroundCommand command)
    {
        ID2D1SolidColorBrush tone = BrushFor(command.Tone);
        Rect bounds = new(
            command.Bounds.X,
            command.Bounds.Y,
            command.Bounds.Width,
            command.Bounds.Height);

        switch (command.Kind)
        {
            case ForegroundCommandKind.Text:
                if (command.TextRole == ForegroundTextRole.Title && _groupTitle is not null) tone = _groupTitle;
                IDWriteTextFormat format = FormatFor(command.TextRole);
                Rect shadowBounds = new(
                    command.Bounds.X + 0.8f,
                    command.Bounds.Y + 1.1f,
                    command.Bounds.Width,
                    command.Bounds.Height);
                _target.DrawText(command.Text, format, shadowBounds, _shadow);
                _target.DrawText(command.Text, format, bounds, tone);
                break;
            case ForegroundCommandKind.Rule:
                _target.DrawLine(
                    new Vector2(command.Bounds.X, command.Bounds.Y),
                    new Vector2(command.Bounds.Right, command.Bounds.Y),
                    _rule,
                    Math.Max(0.5f, command.StrokeWidth));
                break;
            case ForegroundCommandKind.ProgressTrack:
                _target.FillRectangle(bounds, _track);
                break;
            case ForegroundCommandKind.ProgressFill:
                if (command.Progress > 0)
                {
                    Rect fill = new(
                        command.Bounds.X,
                        command.Bounds.Y,
                        command.Bounds.Width * command.Progress,
                        command.Bounds.Height);
                    _target.FillRectangle(fill, _barBrushes.TryGetValue(command.Tone, out var bar) ? bar : tone);
                }
                break;
            case ForegroundCommandKind.StatusMark:
                _target.FillRectangle(bounds, tone);
                break;
        }
    }

    private IDWriteTextFormat FormatFor(ForegroundTextRole role) =>
        role switch
        {
            ForegroundTextRole.Display => _display,
            ForegroundTextRole.Title => _title,
            ForegroundTextRole.Body => _body,
            _ => _caption
        };

    private ID2D1SolidColorBrush BrushFor(ForegroundTone tone) =>
        _brushes.TryGetValue(tone, out ID2D1SolidColorBrush? brush)
            ? brush
            : _brushes[ForegroundTone.Primary];

    private IDWriteTextFormat CreateTextFormat(
        string family,
        float size,
        FontWeight weight)
    {
        IDWriteTextFormat format = _writeFactory.CreateTextFormat(
            family,
            weight,
            Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal,
            size);
        format.WordWrapping = WordWrapping.NoWrap;
        format.TextAlignment = TextAlignment.Leading;
        format.ParagraphAlignment = ParagraphAlignment.Near;
        return format;
    }

    private static string BuildSignature(ForegroundDrawList drawList)
    {
        return string.Create(
            drawList.Commands.Length * 32 + 16,
            drawList,
            static (span, list) =>
            {
                int offset = 0;
                foreach (ForegroundCommand command in list.Commands)
                {
                    string value =
                        $"{command.Kind}|{command.StableId}|{command.Text}|" +
                        $"{command.Tone}|{command.Bounds.X:F2}|{command.Bounds.Y:F2}|" +
                        $"{command.Bounds.Width:F2}|{command.Bounds.Height:F2}|" +
                        $"{command.Progress:F4};";
                    if (value.Length > span.Length - offset)
                    {
                        return;
                    }

                    value.AsSpan().CopyTo(span[offset..]);
                    offset += value.Length;
                }
            });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (ID2D1SolidColorBrush brush in _brushes.Values)
        {
            brush.Dispose();
        }

        _rule.Dispose();
        _track.Dispose();
        _groupTitle?.Dispose();
        foreach (var brush in _barBrushes.Values) brush.Dispose();
        _shadow.Dispose();
        _caption.Dispose();
        _body.Dispose();
        _title.Dispose();
        _display.Dispose();
        _target.Dispose();
        _factory.Dispose();
        _writeFactory.Dispose();
    }
}
