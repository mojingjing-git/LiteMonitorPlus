using System.Collections.Immutable;
using System.Text.Json;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Materials;
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
