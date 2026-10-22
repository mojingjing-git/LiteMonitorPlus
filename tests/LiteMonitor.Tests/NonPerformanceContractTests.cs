using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Materials;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class NonPerformanceContractTests
{
    [Fact]
    public void MaterialConstantAbiSlotsAreFrozen()
    {
        Assert.Equal(384, Marshal.SizeOf<MaterialFrameConstants>());
        Assert.Equal(0, Marshal.OffsetOf<MaterialFrameConstants>(
            nameof(MaterialFrameConstants.Viewport)).ToInt32());
        Assert.Equal(224, Marshal.OffsetOf<MaterialFrameConstants>(
            nameof(MaterialFrameConstants.Theme0)).ToInt32());
        Assert.Equal(288, Marshal.OffsetOf<MaterialFrameConstants>(
            nameof(MaterialFrameConstants.Theme4)).ToInt32());
        Assert.Equal(304, Marshal.OffsetOf<MaterialFrameConstants>(
            nameof(MaterialFrameConstants.Theme5)).ToInt32());
        Assert.Equal(320, Marshal.OffsetOf<MaterialFrameConstants>(
            nameof(MaterialFrameConstants.Motion)).ToInt32());
        Assert.Equal(368, Marshal.OffsetOf<MaterialFrameConstants>(
            nameof(MaterialFrameConstants.State)).ToInt32());
    }

    [Fact]
    public void FixedBackgroundInputCopiesPixelsAndComputesHash()
    {
        byte[] pixels =
        [
            1, 2, 3, 255,
            5, 6, 7, 255
        ];

        bool created = FixedBackgroundInput.TryCreate(
            pixels,
            width: 2,
            height: 1,
            rowPitch: 8,
            dpi: 96,
            opaquePixelBounds: new PixelRectI(0, 0, 2, 1),
            contentVersion: "fixture-v1",
            out FixedBackgroundInput? input,
            out string? errorCode,
            out string? errorMessage);

        Assert.True(created, $"{errorCode}: {errorMessage}");
        Assert.NotNull(input);
        pixels[0] = 99;
        Assert.Equal(1, input!.Bgra8Pixels.Span[0]);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(
                new byte[] { 1, 2, 3, 255, 5, 6, 7, 255 })).ToLowerInvariant(),
            input.PixelSha256);
        Assert.Equal(FixedBackgroundInput.PixelFormat, input.PixelFormatName);
        Assert.Equal(FixedBackgroundInput.ColorSpace, input.ColorSpaceName);
    }

    [Fact]
    public void FixedBackgroundInputRejectsIncompleteRows()
    {
        bool created = FixedBackgroundInput.TryCreate(
            new byte[5],
            width: 2,
            height: 1,
            rowPitch: 4,
            dpi: 96,
            opaquePixelBounds: new PixelRectI(0, 0, 2, 1),
            contentVersion: "fixture-v1",
            out _,
            out string? errorCode,
            out _);

        Assert.False(created);
        Assert.Equal("fixed_background_invalid_stride", errorCode);
    }

    [Fact]
    public void FloatingToolbarParametersReachRuntimeSettings()
    {
        ThemeRenderingConfig configuration = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.LiquidGlassMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(false),
                ["glassStyle"] = JsonSerializer.SerializeToElement("floatingToolbar"),
                ["fogStrength"] = JsonSerializer.SerializeToElement(0.16),
                ["tintStrength"] = JsonSerializer.SerializeToElement(0.038),
                ["shadowRadius"] = JsonSerializer.SerializeToElement(30)
            }
        };

        Assert.Empty(configuration.Validate());
        MaterialRuntimeSettings settings = MaterialRuntimeSettings.Create(
            configuration,
            MaterialKind.LiquidGlass);

        Assert.Equal(GlassSurfaceStyle.FloatingToolbar, settings.GlassStyle);
        Assert.Equal(0.16f, settings.FogStrength);
        Assert.Equal(0.038f, settings.TintStrength);
        Assert.Equal(30f, settings.ShadowRadius);
    }

    [Fact]
    public void InvalidGlassStyleAndStrengthsFailClosed()
    {
        ThemeRenderingConfig configuration = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.LiquidGlassMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(false),
                ["glassStyle"] = JsonSerializer.SerializeToElement("unknown"),
                ["fogStrength"] = JsonSerializer.SerializeToElement(1.1),
                ["tintStrength"] = JsonSerializer.SerializeToElement(-0.1),
                ["shadowRadius"] = JsonSerializer.SerializeToElement(65)
            }
        };

        IReadOnlyList<string> errors = configuration.Validate();

        Assert.Contains(errors, error => error.Contains("glassStyle", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("fogStrength", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("tintStrength", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("shadowRadius", StringComparison.Ordinal));
    }

    [Fact]
    public void FloatingStyleDipGeometryScalesOnceWithoutRescalingSceneBounds()
    {
        ThemeRenderingConfig configuration = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.LiquidGlassMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(false),
                ["glassStyle"] = JsonSerializer.SerializeToElement("floatingToolbar"),
                ["cornerRadius"] = JsonSerializer.SerializeToElement(30),
                ["thickness"] = JsonSerializer.SerializeToElement(8),
                ["refraction"] = JsonSerializer.SerializeToElement(8),
                ["blurRadius"] = JsonSerializer.SerializeToElement(8),
                ["shadowRadius"] = JsonSerializer.SerializeToElement(30),
                ["shadow"] = JsonSerializer.SerializeToElement(0.115),
                ["highlight"] = JsonSerializer.SerializeToElement(0.72)
            }
        };
        MaterialRuntimeSettings settings = MaterialRuntimeSettings.Create(
            configuration,
            MaterialKind.LiquidGlass);
        SceneSnapshot scene = new(
            RenderingContractVersion.Core,
            new RenderGenerations(1, 1, 1, 1, 1),
            LogicalWidth: 400,
            LogicalHeight: 300,
            Dpi: 144,
            Groups:
            [
                new SceneGroup(
                    "group",
                    "Group",
                    ImmutableArray<SceneMetric>.Empty,
                    new LogicalRect(20, 30, 100, 50),
                    0)
            ],
            Notices: ImmutableArray<SceneNotice>.Empty);

        SceneMaterialLayout layout = SceneMaterialLayoutBuilder.Build(
            scene,
            MaterialKind.LiquidGlass,
            physicalWidth: 800,
            physicalHeight: 600,
            contentRectangle: new Rectangle(0, 0, 800, 600),
            captureRectangle: new Rectangle(0, 0, 1920, 1080),
            backgroundMode: SceneBackgroundMode.FixedTexture,
            animationSeconds: 0,
            runtimeSettings: settings,
            userScale: 1.5f);

        Assert.Equal(SceneBackgroundMode.FixedTexture, layout.BackgroundMode);
        Assert.Equal(1, layout.Constants.State.X);
        Assert.Equal(31, layout.Constants.Panel0.X);
        Assert.Equal(46, layout.Constants.Panel0.Y);
        Assert.Equal(148, layout.Constants.Panel0.Z);
        Assert.Equal(73, layout.Constants.Panel0.W);
        // 圆角是像素几何：30 DIP × (144/96) × 1.5 = 67.5，取整到整像素 = 68
        // （窗口 Region 必须是整数边界，所以圆角按整数像素下发）。
        Assert.Equal(68f, layout.Constants.Theme1.X);
        Assert.Equal(18, layout.Constants.Theme1.Y);
        Assert.Equal(18, layout.Constants.Theme1.Z);
        Assert.Equal(18, layout.Constants.Theme1.W);
        Assert.Equal(1, layout.Constants.Theme5.X);
        Assert.Equal(0.16f, layout.Constants.Theme5.Y);
        Assert.Equal(0.038f, layout.Constants.Theme5.Z);
        // 阴影半径是着色器参数，不是 Region 边界，保留亚像素精度。
        Assert.Equal(67.5f, layout.Constants.Theme5.W);
        Assert.Equal(0.72f, layout.Constants.Theme4.X);
        Assert.Equal(0.115f, layout.Constants.Theme2.W);
    }

    [Fact]
    public void ForegroundThemeFactoryUsesThemeFontsAndColors()
    {
        Theme theme = new()
        {
            Font = new FontConfig
            {
                Family = "UI Test",
                ValueFamily = "Value Test",
                Title = 12,
                Group = 11,
                Item = 9,
                Value = 15,
                Bold = false
            },
            Color = new ColorConfig
            {
                TextPrimary = "#112233",
                TextSecondary = "#445566",
                ValueSafe = "#00AA44",
                ValueWarn = "#CC8800",
                ValueCrit = "#CC2233",
                BarLow = "#336699",
                Border = "rgba(10,20,30,0.25)",
                Shadow = "rgba(0,0,0,0.5)"
            }
        };

        ForegroundThemeStyle style = ThemeForegroundStyleFactory.Create(
            theme,
            MaterialKind.LiquidGlass);

        Assert.True(style.IsValid);
        Assert.Equal("UI Test", style.UiFontFamily);
        Assert.Equal("Value Test", style.ValueFontFamily);
        // 四个字号槽按"字体族"分工，两个 UI 字体槽取组标题/条目字号，
        // 两个值字体槽取数值字号；Font.Title（面板主标题）在 GPU 面板里没有对应槽位。
        Assert.Equal(15f, style.DisplaySizeDip);
        Assert.Equal(11f, style.TitleSizeDip);
        Assert.Equal(15f, style.BodySizeDip);
        Assert.Equal(9f, style.CaptionSizeDip);
        Assert.False(style.BoldText);
        Assert.Equal(17f / 255f, style.Primary.R, precision: 5);
        // 主题颜色是 8-bit sRGB：alpha 0.25 会量化成 64/255，不是精确的 0.25。
        Assert.Equal(64f / 255f, style.Rule.A, precision: 5);
    }
}
