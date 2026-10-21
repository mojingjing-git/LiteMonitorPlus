using LiteMonitor.Rendering.Contracts;
using LiteMonitor.src.Core;

namespace LiteMonitor.Rendering.Materials;

/// <summary>
/// Converts the existing theme font/color section into the immutable foreground
/// contract. Missing or invalid individual colors fall back per role so legacy
/// readability is preserved.
/// </summary>
public static class ThemeForegroundStyleFactory
{
    public static ForegroundThemeStyle Create(
        Theme? theme,
        MaterialKind material)
    {
        ForegroundThemeStyle defaults = ForegroundThemeStyle.Default(material);
        if (theme is null)
        {
            return defaults;
        }

        string uiFamily = NormalizeFamily(theme.Font.Family, defaults.UiFontFamily);
        string valueFamily = NormalizeFamily(
            theme.Font.ValueFamily,
            defaults.ValueFontFamily);
        bool bold = theme.Font.Bold;

        return new ForegroundThemeStyle(
            uiFamily,
            valueFamily,
            ClampSize(theme.Font.Value, defaults.DisplaySizeDip),
            ClampSize(theme.Font.Group, defaults.TitleSizeDip),
            ClampSize(theme.Font.Value, defaults.BodySizeDip),
            ClampSize(theme.Font.Item, defaults.CaptionSizeDip),
            bold,
            Parse(theme.Color.TextPrimary, defaults.Primary),
            Parse(
                string.IsNullOrWhiteSpace(theme.Color.TextSecondary)
                    ? theme.Color.TextGroup
                    : theme.Color.TextSecondary,
                defaults.Muted),
            Parse(theme.Color.ValueSafe, defaults.Success),
            Parse(theme.Color.ValueWarn, defaults.Warning),
            Parse(theme.Color.ValueCrit, defaults.Danger),
            Parse(theme.Color.BarLow, defaults.Accent),
            Parse(theme.Color.Border, defaults.Rule),
            Parse(theme.Color.Shadow, defaults.Shadow))
        {
            DisplayFontFamily = valueFamily,
            CaptionFontFamily = uiFamily,
            UsesThemeTypography = true,
            TrackColor = Parse(theme.Color.BarBackground, defaults.Rule),
            GroupTitleColor = Parse(theme.Color.TextGroup, defaults.Primary),
            BarSafe = Parse(theme.Color.BarLow, defaults.Success),
            BarWarn = Parse(theme.Color.BarMid, defaults.Warning),
            BarCrit = Parse(theme.Color.BarHigh, defaults.Danger)
        };
    }

    private static string NormalizeFamily(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static float ClampSize(double value, float fallback) =>
        double.IsFinite(value) && value > 0
            ? (float)Math.Clamp(value, 1, 256)
            : fallback;

    private static RgbaColor Parse(string? value, RgbaColor fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        Color color = ThemeManager.ParseColor(value);
        RgbaColor parsed = new(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f);
        return parsed.IsValid ? parsed : fallback;
    }
}
