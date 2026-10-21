using System.Numerics;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;

namespace LiteMonitor.Rendering.Materials;

public struct MaterialFrameConstants
{
    public Vector4 Viewport;
    public Vector4 RootRect;
    public Vector4 Panel0;
    public Vector4 Panel1;
    public Vector4 Panel2;
    public Vector4 Panel3;
    public Vector4 Panel4;
    public Vector4 Panel5;
    public Vector4 Panel6;
    public Vector4 Panel7;
    public Vector4 Panel8;
    public Vector4 Panel9;
    public Vector4 Panel10;
    public Vector4 Panel11;
    public Vector4 Theme0;
    public Vector4 Theme1;
    public Vector4 Theme2;
    public Vector4 Theme3;
    public Vector4 Theme4;
    public Vector4 Theme5;
    public Vector4 Motion;
    public Vector4 Desktop;
    public Vector4 Window;
    public Vector4 State;
}

public readonly record struct SceneMaterialLayout(
    MaterialFrameConstants Constants,
    int GroupCount)
{
    public SceneBackgroundMode BackgroundMode { get; init; }
}

public static class SceneMaterialLayoutBuilder
{
    public const int MaximumGroups = 12;

    public static int ToolbarCornerRadius(float logicalRadius, int dpi, float userScale, int width, int height) =>
        (int)Math.Clamp(MathF.Round(logicalRadius * dpi / 96f * userScale, MidpointRounding.AwayFromZero),
            0, Math.Max(0, Math.Min(width, height) / 2));

    public static SceneMaterialLayout Build(
        SceneSnapshot scene,
        MaterialKind material,
        int physicalWidth,
        int physicalHeight,
        Rectangle contentRectangle,
        Rectangle captureRectangle,
        bool hasLiveBackground,
        float animationSeconds,
        MaterialRuntimeSettings? runtimeSettings = null,
        float userScale = 1f) =>
        BuildCore(
            scene,
            material,
            physicalWidth,
            physicalHeight,
            contentRectangle,
            captureRectangle,
            hasLiveBackground
                ? SceneBackgroundMode.LiveCapture
                : SceneBackgroundMode.Procedural,
            animationSeconds,
            runtimeSettings,
            userScale);

    public static SceneMaterialLayout Build(
        SceneSnapshot scene,
        MaterialKind material,
        int physicalWidth,
        int physicalHeight,
        Rectangle contentRectangle,
        Rectangle captureRectangle,
        SceneBackgroundMode backgroundMode,
        float animationSeconds,
        MaterialRuntimeSettings? runtimeSettings = null,
        float userScale = 1f) =>
        BuildCore(
            scene,
            material,
            physicalWidth,
            physicalHeight,
            contentRectangle,
            captureRectangle,
            backgroundMode,
            animationSeconds,
            runtimeSettings,
            userScale);

    private static SceneMaterialLayout BuildCore(
        SceneSnapshot scene,
        MaterialKind material,
        int physicalWidth,
        int physicalHeight,
        Rectangle contentRectangle,
        Rectangle captureRectangle,
        SceneBackgroundMode backgroundMode,
        float animationSeconds,
        MaterialRuntimeSettings? runtimeSettings,
        float userScale)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (physicalWidth <= 0 || physicalHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalWidth));
        }

        SceneGroup[] groups = scene.Groups
            .OrderBy(group => group.Order)
            .Take(MaximumGroups)
            .ToArray();
        float scale = scene.Dpi / 96.0f;
        Vector4[] panels = new Vector4[MaximumGroups];
        for (int index = 0; index < groups.Length; index++)
        {
            SceneGroup group = groups[index];
            panels[index] = new Vector4(
                (float)group.Bounds.X * scale + 1,
                (float)group.Bounds.Y * scale + 1,
                Math.Max(0, (float)group.Bounds.Width * scale - 2),
                Math.Max(0, (float)group.Bounds.Height * scale - 2));
        }

        MaterialRuntimeSettings settings =
            runtimeSettings ?? MaterialRuntimeSettings.Create(null, material);
        float normalizedUserScale =
            float.IsFinite(userScale) && userScale > 0 ? userScale : 1f;
        float styleScale = scale * normalizedUserScale;
        bool floatingToolbar =
            material == MaterialKind.LiquidGlass &&
            settings.GlassStyle == GlassSurfaceStyle.FloatingToolbar;
        float glassCornerRadius =
            floatingToolbar ? ToolbarCornerRadius(settings.CornerRadius, scene.Dpi, normalizedUserScale,
                physicalWidth, physicalHeight) : settings.CornerRadius;
        float glassRefraction =
            floatingToolbar ? settings.Refraction * styleScale : settings.Refraction;
        float glassBlurRadius =
            floatingToolbar ? settings.BlurRadius * styleScale : settings.BlurRadius;
        float glassThickness =
            floatingToolbar ? settings.Thickness * styleScale : settings.Thickness;
        float glassShadowRadius =
            floatingToolbar ? settings.ShadowRadius * styleScale : 0;
        (Vector4 theme0, Vector4 theme1, Vector4 theme2, Vector4 theme3, Vector4 theme4) =
            material switch
            {
                MaterialKind.LiquidGlass => (
                    new Vector4(0.72f, 0.88f, 0.98f, settings.TintStrength),
                    new Vector4(
                        glassCornerRadius,
                        glassRefraction,
                        glassBlurRadius,
                        glassThickness),
                    new Vector4(
                        0.025f,
                        0.065f,
                        0.10f,
                        settings.Shadow),
                    new Vector4(
                        0.86f,
                        0.97f,
                        1.0f,
                        settings.ClearCenterRatio),
                    new Vector4(
                        settings.Highlight,
                        settings.CounterHighlight,
                        0,
                        0)),
                MaterialKind.CyberPunk => (
                    new Vector4(
                        0.042f,
                        0.074f,
                        0.086f,
                        settings.PanelOpacity),
                    new Vector4(settings.PrimaryCut, settings.SecondaryCut, 1.5f, 6f),
                    new Vector4(0.24f, 0.37f, 0.45f, 1f),
                    new Vector4(0.24f, 0.88f, 0.94f, 1f),
                    new Vector4(
                        settings.Glow / 12f,
                        settings.OutlineWidth,
                        settings.PanelOpacity,
                        settings.AmbientRatio)),
                _ => (
                    new Vector4(0.18f, 0.20f, 0.22f, 1f),
                    Vector4.Zero,
                    Vector4.Zero,
                    Vector4.One,
                    Vector4.Zero)
            };

        if (floatingToolbar)
        {
            theme0 = new Vector4(0.78f, 0.83f, 0.90f, settings.TintStrength);
            theme3 = new Vector4(0.94f, 0.97f, 1.0f, 1.0f);
        }

        MaterialFrameConstants constants = new()
        {
            Viewport = new Vector4(
                physicalWidth,
                physicalHeight,
                material == MaterialKind.CyberPunk ? 1 : 0,
                Math.Max(0, animationSeconds)),
            RootRect = new Vector4(
                floatingToolbar ? 0 : 1,
                floatingToolbar ? 0 : 1,
                Math.Max(0, physicalWidth - (floatingToolbar ? 0 : 2)),
                Math.Max(0, physicalHeight - (floatingToolbar ? 0 : 2))),
            Panel0 = panels[0],
            Panel1 = panels[1],
            Panel2 = panels[2],
            Panel3 = panels[3],
            Panel4 = panels[4],
            Panel5 = panels[5],
            Panel6 = panels[6],
            Panel7 = panels[7],
            Panel8 = panels[8],
            Panel9 = panels[9],
            Panel10 = panels[10],
            Panel11 = panels[11],
            Theme0 = theme0,
            Theme1 = theme1,
            Theme2 = theme2,
            Theme3 = theme3,
            Theme4 = theme4,
            Theme5 = material == MaterialKind.LiquidGlass
                ? new Vector4(
                    (float)settings.GlassStyle,
                    settings.FogStrength,
                    settings.TintStrength,
                    glassShadowRadius)
                : Vector4.Zero,
            Motion = new Vector4(
                settings.MotionEnabled ? 1 : 0,
                settings.TargetFramesPerSecond,
                settings.StateTransitionMilliseconds,
                settings.ScannerPeriodMilliseconds),
            Desktop = new Vector4(
                captureRectangle.X,
                captureRectangle.Y,
                Math.Max(1, captureRectangle.Width),
                Math.Max(1, captureRectangle.Height)),
            Window = new Vector4(
                contentRectangle.X,
                contentRectangle.Y,
                contentRectangle.Width,
                contentRectangle.Height),
            State = new Vector4(
                (float)backgroundMode,
                groups.Length,
                scale,
                0)
        };
        return new SceneMaterialLayout(constants, groups.Length)
        {
            BackgroundMode = backgroundMode
        };
    }
}
