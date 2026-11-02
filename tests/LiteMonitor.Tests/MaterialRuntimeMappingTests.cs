using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using System.Text.Json;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Materials;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class MaterialRuntimeMappingTests
{
    [Fact]
    public void LiquidGlassParametersReachFrameConstants()
    {
        ThemeRenderingConfig configuration = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.LiquidGlassMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(false),
                ["cornerRadius"] = JsonSerializer.SerializeToElement(31),
                ["thickness"] = JsonSerializer.SerializeToElement(2.2),
                ["refraction"] = JsonSerializer.SerializeToElement(3.3),
                ["blurRadius"] = JsonSerializer.SerializeToElement(7),
                ["clearCenterRatio"] = JsonSerializer.SerializeToElement(0.4),
                ["surfaceOpacity"] = JsonSerializer.SerializeToElement(0.2),
                ["highlight"] = JsonSerializer.SerializeToElement(0.6),
                ["counterHighlight"] = JsonSerializer.SerializeToElement(0.3),
                ["shadow"] = JsonSerializer.SerializeToElement(0.5)
            },
            Motion = new Dictionary<string, JsonElement>
            {
                ["targetFps"] = JsonSerializer.SerializeToElement(45),
                ["motionEnabled"] = JsonSerializer.SerializeToElement(false)
            }
        };
        MaterialRuntimeSettings settings = MaterialRuntimeSettings.Create(
            configuration,
            MaterialKind.LiquidGlass);

        SceneMaterialLayout layout = SceneMaterialLayoutBuilder.Build(
            Scene(),
            MaterialKind.LiquidGlass,
            320,
            240,
            new Rectangle(0, 0, 320, 240),
            new Rectangle(0, 0, 320, 240),
            hasLiveBackground: false,
            animationSeconds: 8,
            settings);

        Assert.Equal(31, layout.Constants.Theme1.X);
        Assert.Equal(3.3f, layout.Constants.Theme1.Y);
        Assert.Equal(7, layout.Constants.Theme1.Z);
        Assert.Equal(2.2f, layout.Constants.Theme1.W);
        Assert.Equal(0.2f, layout.Constants.Theme0.W);
        Assert.Equal(0.4f, layout.Constants.Theme3.W);
        Assert.Equal(0.6f, layout.Constants.Theme4.X);
        Assert.Equal(0.3f, layout.Constants.Theme4.Y);
        Assert.Equal(0.5f, layout.Constants.Theme2.W);
        Assert.Equal(0, layout.Constants.Motion.X);
        Assert.Equal(45, layout.Constants.Motion.Y);
    }

    [Fact]
    public void CyberParametersReachFrameConstants()
    {
        ThemeRenderingConfig configuration = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.CyberPunkMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(false),
                ["primaryCut"] = JsonSerializer.SerializeToElement(21),
                ["secondaryCut"] = JsonSerializer.SerializeToElement(7),
                ["outlineWidth"] = JsonSerializer.SerializeToElement(2),
                ["glow"] = JsonSerializer.SerializeToElement(24),
                ["ambientRatio"] = JsonSerializer.SerializeToElement(0.3),
                ["panelOpacity"] = JsonSerializer.SerializeToElement(0.8)
            }
        };
        MaterialRuntimeSettings settings = MaterialRuntimeSettings.Create(
            configuration,
            MaterialKind.CyberPunk);

        SceneMaterialLayout layout = SceneMaterialLayoutBuilder.Build(
            Scene(),
            MaterialKind.CyberPunk,
            320,
            240,
            new Rectangle(0, 0, 320, 240),
            new Rectangle(0, 0, 320, 240),
            hasLiveBackground: false,
            animationSeconds: 1,
            settings);

        Assert.Equal(21, layout.Constants.Theme1.X);
        Assert.Equal(7, layout.Constants.Theme1.Y);
        Assert.Equal(2, layout.Constants.Theme4.X);
        Assert.Equal(2, layout.Constants.Theme4.Y);
        Assert.Equal(0.8f, layout.Constants.Theme4.Z);
        Assert.Equal(0.3f, layout.Constants.Theme4.W);
    }

    [Fact]
    public void NamedVisualPresetsMapToDistinctConstantsAndThemeColors()
    {
        ThemeRenderingConfig configuration = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.CyberPunkMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["visualPreset"] = JsonSerializer.SerializeToElement("nixieBrass")
            }
        };
        var theme = new Theme
        {
            Font = new FontConfig
            {
                Family = "Bahnschrift",
                ValueFamily = "Cascadia Mono",
                Title = 12,
                Group = 11,
                Item = 10,
                Value = 18
            },
            Color = new ColorConfig
            {
                Background = "#120E0B",
                GroupBackground = "#1F1712",
                Border = "#8A6538",
                Shadow = "#00000055",
                TextPrimary = "#FFD6A0",
                TextSecondary = "#B88652",
                TextGroup = "#B88652",
                ValueSafe = "#FFD6A0",
                ValueWarn = "#FF7A1A",
                ValueCrit = "#FF2200",
                BarBackground = "#18120D",
                BarLow = "#FFA028",
                BarMid = "#FF5900",
                BarHigh = "#FF2200"
            }
        };

        MaterialRuntimeSettings settings = MaterialRuntimeSettings.Create(
            configuration,
            MaterialKind.CyberPunk,
            theme);
        SceneMaterialLayout layout = SceneMaterialLayoutBuilder.Build(
            Scene(),
            MaterialKind.CyberPunk,
            320,
            240,
            new Rectangle(0, 0, 320, 240),
            new Rectangle(0, 0, 320, 240),
            hasLiveBackground: false,
            animationSeconds: 0,
            settings);

        Assert.Equal(GpuVisualPreset.NixieBrass, settings.VisualPreset);
        Assert.Equal((float)GpuVisualPreset.NixieBrass, layout.Constants.PresetStyle.X);
        Assert.Equal(0x12 / 255f, layout.Constants.PaletteBase.X, 3);
        Assert.Equal(0x1F / 255f, layout.Constants.PalettePanel.X, 3);
        Assert.Equal(0x8A / 255f, layout.Constants.PaletteBorder.X, 3);
        Assert.Equal(0xFF / 255f, layout.Constants.PaletteAccent.X, 3);
        Assert.True(settings.ForegroundTheme.UsesThemeTypography);
        Assert.Equal("Bahnschrift", settings.ForegroundTheme.UiFontFamily);
        Assert.Equal("Cascadia Mono", settings.ForegroundTheme.ValueFontFamily);
    }

    [Theory]
    [InlineData(
        ThemeRenderingConfig.LiquidGlassMaterial,
        MaterialKind.LiquidGlass,
        "nebulaAurora",
        GpuVisualPreset.NebulaAurora)]
    [InlineData(
        ThemeRenderingConfig.CyberPunkMaterial,
        MaterialKind.CyberPunk,
        "nixieBrass",
        GpuVisualPreset.NixieBrass)]
    [InlineData(
        ThemeRenderingConfig.CyberPunkMaterial,
        MaterialKind.CyberPunk,
        "crtPhosphor",
        GpuVisualPreset.CrtPhosphor)]
    public void EveryNamedPresetHasItsOwnStableShaderId(
        string materialName,
        MaterialKind material,
        string presetName,
        GpuVisualPreset expected)
    {
        ThemeRenderingConfig configuration = Configuration(materialName, presetName);
        MaterialRuntimeSettings settings = MaterialRuntimeSettings.Create(
            configuration,
            material);
        SceneMaterialLayout layout = SceneMaterialLayoutBuilder.Build(
            Scene(),
            material,
            320,
            240,
            new Rectangle(0, 0, 320, 240),
            new Rectangle(0, 0, 320, 240),
            hasLiveBackground: false,
            animationSeconds: 0,
            settings);

        Assert.Equal(expected, settings.VisualPreset);
        Assert.Equal((float)expected, layout.Constants.PresetStyle.X);
    }

    [Fact]
    public void DefaultAndMissingPresetsKeepCompleteDefaultForeground()
    {
        var theme = new Theme
        {
            Font = new FontConfig { Family = "Custom UI", ValueFamily = "Custom Mono" },
            Color = new ColorConfig
            {
                TextPrimary = "#123456",
                TextSecondary = "#234567",
                ValueSafe = "#345678",
                ValueWarn = "#456789",
                ValueCrit = "#56789A",
                BarLow = "#6789AB",
                Border = "#789ABC",
                Shadow = "#89ABCD",
                BarBackground = "#9ABCDE",
                TextGroup = "#ABCDEF",
                BarMid = "#BCDEF0",
                BarHigh = "#CDEF01"
            }
        };
        ThemeRenderingConfig missing = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.CyberPunkMaterial
        };
        ThemeRenderingConfig explicitDefault = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.CyberPunkMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["visualPreset"] = JsonSerializer.SerializeToElement("default")
            }
        };

        ForegroundThemeStyle expected = ForegroundThemeStyle.Default(MaterialKind.CyberPunk);
        Assert.Equal(
            expected,
            MaterialRuntimeSettings.Create(
                missing,
                MaterialKind.CyberPunk,
                theme).ForegroundTheme);
        Assert.Equal(
            expected,
            MaterialRuntimeSettings.Create(
                explicitDefault,
                MaterialKind.CyberPunk,
                theme).ForegroundTheme);
    }

    [Fact]
    public void VisualPresetParameterValidatesMaterialPairs()
    {
        ThemeRenderingConfig valid = Configuration(
            ThemeRenderingConfig.LiquidGlassMaterial,
            "nebulaAurora");
        Assert.Empty(valid.Validate());

        ThemeRenderingConfig wrongPair = Configuration(
            ThemeRenderingConfig.CyberPunkMaterial,
            "nebulaAurora");
        Assert.Contains(
            wrongPair.Validate(),
            error => error.Contains("visualPreset", StringComparison.OrdinalIgnoreCase));

        ThemeRenderingConfig unknown = Configuration(
            ThemeRenderingConfig.CyberPunkMaterial,
            "not-a-preset");
        Assert.Contains(
            unknown.Validate(),
            error => error.Contains("visualPreset", StringComparison.OrdinalIgnoreCase));

        ThemeRenderingConfig nonString = Configuration(
            ThemeRenderingConfig.CyberPunkMaterial,
            "nixieBrass");
        nonString.Parameters["visualPreset"] = JsonSerializer.SerializeToElement(1);
        Assert.Contains(
            nonString.Validate(),
            error => error.Contains("visualPreset", StringComparison.OrdinalIgnoreCase));

        ThemeRenderingConfig nebulaLive = Configuration(
            ThemeRenderingConfig.LiquidGlassMaterial,
            "nebulaAurora");
        nebulaLive.Parameters["liveBackground"] = JsonSerializer.SerializeToElement(true);
        Assert.Contains(
            nebulaLive.Validate(),
            error => error.Contains("liveBackground", StringComparison.OrdinalIgnoreCase));

        ThemeRenderingConfig nebulaToolbar = Configuration(
            ThemeRenderingConfig.LiquidGlassMaterial,
            "nebulaAurora");
        nebulaToolbar.Parameters["glassStyle"] =
            JsonSerializer.SerializeToElement("FloatingToolbar");
        Assert.Contains(
            nebulaToolbar.Validate(),
            error => error.Contains("floatingToolbar", StringComparison.OrdinalIgnoreCase));

        var legacy = new ThemeRenderingConfig
        {
            Backend = ThemeRenderingConfig.LegacyBackend,
            Material = ThemeRenderingConfig.LegacyMaterial
        };
        Assert.Empty(legacy.Validate());

        legacy.Parameters["visualPreset"] =
            JsonSerializer.SerializeToElement("default");
        // "default" 语义等同缺省，对 legacy 主题合法（仅清掉命名预设），不应整份判无效。
        Assert.Empty(legacy.Validate());
    }

    [Fact]
    public void MaterialFrameConstantsKeepFrozenOffsetsAndGrowOnlyByFiveVectors()
    {
        Assert.Equal(464, Unsafe.SizeOf<MaterialFrameConstants>());
        Assert.Equal(
            (nint)368,
            Marshal.OffsetOf<MaterialFrameConstants>(nameof(MaterialFrameConstants.State)));
        Assert.Equal(
            (nint)384,
            Marshal.OffsetOf<MaterialFrameConstants>(nameof(MaterialFrameConstants.PresetStyle)));
        Assert.Equal(
            (nint)400,
            Marshal.OffsetOf<MaterialFrameConstants>(nameof(MaterialFrameConstants.PaletteBase)));
        Assert.Equal(
            (nint)416,
            Marshal.OffsetOf<MaterialFrameConstants>(nameof(MaterialFrameConstants.PalettePanel)));
        Assert.Equal(
            (nint)432,
            Marshal.OffsetOf<MaterialFrameConstants>(nameof(MaterialFrameConstants.PaletteBorder)));
        Assert.Equal(
            (nint)448,
            Marshal.OffsetOf<MaterialFrameConstants>(nameof(MaterialFrameConstants.PaletteAccent)));
    }

    private static ThemeRenderingConfig Configuration(string material, string preset) =>
        new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = material,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(false),
                ["visualPreset"] = JsonSerializer.SerializeToElement(preset)
            }
        };

    private static SceneSnapshot Scene() =>
        new(
            RenderingContractVersion.Core,
            new RenderGenerations(1, 1, 1, 1, 1),
            320,
            240,
            96,
            ImmutableArray<SceneGroup>.Empty,
            ImmutableArray<SceneNotice>.Empty);
}
