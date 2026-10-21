using System.Text.Json;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Materials;
using LiteMonitor.src.Core;

namespace LiteMonitor.Rendering.Configuration;

/// <summary>
/// Validated values consumed by the production material pass and scheduler.
/// Unknown extension fields remain in ThemeRenderingConfig and are not claimed
/// as supported by this value object.
/// </summary>
public sealed record MaterialRuntimeSettings(
    MaterialKind Material,
    float CornerRadius,
    float Thickness,
    float Refraction,
    float BlurRadius,
    float ClearCenterRatio,
    float SurfaceOpacity,
    float Highlight,
    float CounterHighlight,
    float Shadow,
    float PrimaryCut,
    float SecondaryCut,
    float OutlineWidth,
    float Glow,
    float AmbientRatio,
    float PanelOpacity,
    bool MotionEnabled,
    int TargetFramesPerSecond,
    int StateTransitionMilliseconds,
    int ScannerPeriodMilliseconds,
    GlassSurfaceStyle GlassStyle,
    float FogStrength,
    float TintStrength,
    float ShadowRadius,
    ForegroundThemeStyle ForegroundTheme)
{
    public static MaterialRuntimeSettings Create(
        ThemeRenderingConfig? configuration,
        MaterialKind material,
        ForegroundThemeStyle? foregroundTheme = null)
    {
        Dictionary<string, JsonElement> parameters =
            configuration?.Parameters ?? new Dictionary<string, JsonElement>();
        Dictionary<string, JsonElement> motion =
            configuration?.Motion ?? new Dictionary<string, JsonElement>();
        bool toolbar = ReadGlassStyle(parameters, "glassStyle", GlassSurfaceStyle.Standard) == GlassSurfaceStyle.FloatingToolbar;

        return material switch
        {
            MaterialKind.LiquidGlass => new MaterialRuntimeSettings(
                material,
                CornerRadius: ReadSingle(parameters, "cornerRadius", toolbar ? 30 : 26, 0, 64),
                Thickness: ReadSingle(parameters, "thickness", toolbar ? 8 : 1, 0, 8),
                Refraction: ReadSingle(parameters, "refraction", toolbar ? 8 : 1.6f, 0, 8),
                BlurRadius: ReadSingle(parameters, "blurRadius", 8, 0, 32),
                ClearCenterRatio: ReadSingle(parameters, "clearCenterRatio", 0, 0, 1),
                SurfaceOpacity: ReadSingle(parameters, "surfaceOpacity", 0.055f, 0, 1),
                Highlight: ReadSingle(parameters, "highlight", toolbar ? 0.72f : 1, 0, 1),
                CounterHighlight: ReadSingle(parameters, "counterHighlight", 1, 0, 1),
                Shadow: ReadSingle(parameters, "shadow", toolbar ? 0.115f : 0.34f, 0, 1),
                PrimaryCut: 0,
                SecondaryCut: 0,
                OutlineWidth: 0,
                Glow: 0,
                AmbientRatio: 0,
                PanelOpacity: 1,
                MotionEnabled: ReadBoolean(motion, "motionEnabled", true),
                TargetFramesPerSecond: ReadInt32(motion, "targetFps", 30, 1, 240),
                StateTransitionMilliseconds: ReadInt32(motion, "stateTransitionMs", 180, 0, 5000),
                ScannerPeriodMilliseconds: ReadInt32(motion, "scannerPeriodMs", 0, 0, 60000),
                GlassStyle: ReadGlassStyle(
                    parameters,
                    "glassStyle",
                    GlassSurfaceStyle.Standard),
                FogStrength: ReadSingle(parameters, "fogStrength", toolbar ? 0.16f : 0.42f, 0, 1),
                TintStrength: ReadSingle(
                    parameters,
                    "tintStrength",
                    toolbar ? 0.038f : ReadSingle(parameters, "surfaceOpacity", 0.055f, 0, 1),
                    0,
                    1),
                ShadowRadius: ReadSingle(parameters, "shadowRadius", toolbar ? 30 : 0, 0, 64),
                ForegroundTheme:
                    foregroundTheme ??
                    ForegroundThemeStyle.Default(MaterialKind.LiquidGlass)),
            MaterialKind.CyberPunk => new MaterialRuntimeSettings(
                material,
                CornerRadius: 0,
                Thickness: 0,
                Refraction: 0,
                BlurRadius: 0,
                ClearCenterRatio: 0,
                SurfaceOpacity: 1,
                Highlight: 1,
                CounterHighlight: 1,
                Shadow: 0.34f,
                PrimaryCut: ReadSingle(parameters, "primaryCut", 14, 0, 64),
                SecondaryCut: ReadSingle(parameters, "secondaryCut", 9, 0, 32),
                OutlineWidth: ReadSingle(parameters, "outlineWidth", 1, 0, 8),
                Glow: ReadSingle(parameters, "glow", 12, 0, 100),
                AmbientRatio: ReadSingle(parameters, "ambientRatio", 0.12f, 0, 1),
                PanelOpacity: ReadSingle(parameters, "panelOpacity", 0.965f, 0, 1),
                MotionEnabled: ReadBoolean(motion, "motionEnabled", true),
                TargetFramesPerSecond: ReadInt32(motion, "targetFps", 30, 1, 240),
                StateTransitionMilliseconds: ReadInt32(motion, "stateTransitionMs", 140, 0, 5000),
                ScannerPeriodMilliseconds: ReadInt32(motion, "scannerPeriodMs", 2800, 0, 60000),
                GlassStyle: GlassSurfaceStyle.Standard,
                FogStrength: 0,
                TintStrength: 1,
                ShadowRadius: 0,
                ForegroundTheme:
                    foregroundTheme ??
                    ForegroundThemeStyle.Default(MaterialKind.CyberPunk)),
            _ => new MaterialRuntimeSettings(
                MaterialKind.Legacy,
                0,
                0,
                0,
                0,
                0,
                1,
                1,
                1,
                0.34f,
                0,
                0,
                0,
                0,
                0,
                1,
                MotionEnabled: false,
                TargetFramesPerSecond: 30,
                StateTransitionMilliseconds: 0,
                ScannerPeriodMilliseconds: 0,
                GlassStyle: GlassSurfaceStyle.Standard,
                FogStrength: 0,
                TintStrength: 0,
                ShadowRadius: 0,
                ForegroundTheme:
                    foregroundTheme ??
                    ForegroundThemeStyle.Default(MaterialKind.Legacy))
        };
    }

    public static MaterialRuntimeSettings Create(
        ThemeRenderingConfig? configuration,
        MaterialKind material,
        Theme? theme)
    {
        var settings = Create(configuration, material);
        return settings.GlassStyle == GlassSurfaceStyle.FloatingToolbar && theme is not null
            ? settings with { ForegroundTheme = ThemeForegroundStyleFactory.Create(theme, material) }
            : settings;
    }

    private static float ReadSingle(
        IReadOnlyDictionary<string, JsonElement> values,
        string key,
        float fallback,
        float minimum,
        float maximum)
    {
        if (!values.TryGetValue(key, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetSingle(out float number) ||
            !float.IsFinite(number))
        {
            return fallback;
        }

        return Math.Clamp(number, minimum, maximum);
    }

    private static int ReadInt32(
        IReadOnlyDictionary<string, JsonElement> values,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!values.TryGetValue(key, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int number))
        {
            return fallback;
        }

        return Math.Clamp(number, minimum, maximum);
    }

    private static bool ReadBoolean(
        IReadOnlyDictionary<string, JsonElement> values,
        string key,
        bool fallback)
    {
        if (!values.TryGetValue(key, out JsonElement value) ||
            value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return fallback;
        }

        return value.GetBoolean();
    }

    private static GlassSurfaceStyle ReadGlassStyle(
        IReadOnlyDictionary<string, JsonElement> values,
        string key,
        GlassSurfaceStyle fallback)
    {
        if (!values.TryGetValue(key, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        string normalized = value.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            ThemeRenderingConfig.FloatingToolbarGlassStyle =>
                GlassSurfaceStyle.FloatingToolbar,
            ThemeRenderingConfig.StandardGlassStyle =>
                GlassSurfaceStyle.Standard,
            _ => fallback
        };
    }
}
