using System.Text.Json;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class RenderingConfigurationTests
{
    [Fact]
    public void ExistingThemeWithoutRenderingKeepsLegacyRoute()
    {
        Theme theme = JsonSerializer.Deserialize<Theme>(
            """
            {
              "name": "Legacy",
              "version": 3
            }
            """)!;

        Assert.Null(theme.Rendering);
    }

    [Fact]
    public void GpuConfigurationPreservesUnknownNestedFields()
    {
        const string json =
            """
            {
              "version": 1,
              "backend": "gpu",
              "material": "liquid_glass",
              "parameters": {
                "liveBackground": true,
                "cornerRadius": 24,
                "futureOptics": { "mode": "preview" }
              },
              "motion": { "targetFps": 30 },
              "fallback": { "backend": "legacy", "material": "legacy" },
              "futureRoute": "preserve-me"
            }
            """;

        ThemeRenderingConfig config = JsonSerializer.Deserialize<ThemeRenderingConfig>(json)!;
        Assert.Empty(config.Validate());
        Assert.True(config.IsGpuRequested);
        Assert.Equal("preserve-me", config.ExtensionData["futureRoute"].GetString());
        Assert.Equal("preview", config.Parameters["futureOptics"].GetProperty("mode").GetString());
    }

    [Fact]
    public void CyberPunkCannotSilentlyEnableLiveCapture()
    {
        ThemeRenderingConfig config = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.CyberPunkMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(true),
                ["primaryCut"] = JsonSerializer.SerializeToElement(14)
            }
        };

        IReadOnlyList<string> errors = config.Validate();

        Assert.Contains(errors, error => error.Contains("must not request desktop capture", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidNumericParameterFailsClosed()
    {
        ThemeRenderingConfig config = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.LiquidGlassMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(true),
                ["refraction"] = JsonSerializer.SerializeToElement(100)
            }
        };

        Assert.Contains(
            config.Validate(),
            error => error.Contains("parameters.refraction", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingRenderingSectionNeverStartsGpu()
    {
        RenderingRouteDecision decision = RenderingRoutePolicy.Decide(new(
            Configuration: null,
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: false,
            WdaCapturePolicyAccepted: true,
            SafeMode: false));

        Assert.Equal(RenderingRoute.Legacy, decision.Route);
        Assert.Equal("rendering_configuration_absent", decision.FallbackCode);
    }

    [Fact]
    public void GpuRouteRequiresIndependentOptIn()
    {
        RenderingRouteDecision decision = RenderingRoutePolicy.Decide(new(
            CreateGpuConfiguration(ThemeRenderingConfig.CyberPunkMaterial),
            GpuEffectsEnabled: false,
            LiveBackgroundEnabled: false,
            WdaCapturePolicyAccepted: true,
            SafeMode: false));

        Assert.Equal(RenderingRoute.Legacy, decision.Route);
        Assert.Equal("gpu_effects_opt_in_required", decision.FallbackCode);
    }

    [Fact]
    public void CyberPunkRouteNeverRequestsDesktopCapture()
    {
        RenderingRouteDecision decision = RenderingRoutePolicy.Decide(new(
            CreateGpuConfiguration(ThemeRenderingConfig.CyberPunkMaterial),
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: false,
            WdaCapturePolicyAccepted: true,
            SafeMode: false));

        Assert.Equal(RenderingRoute.GpuFixedInput, decision.Route);
        Assert.Equal(MaterialKind.CyberPunk, decision.EffectiveMaterial);
        Assert.False(decision.CaptureRequested);
    }

    [Fact]
    public void LiveGlassRequiresAcceptedWdaPolicy()
    {
        ThemeRenderingConfig configuration = CreateGpuConfiguration(
            ThemeRenderingConfig.LiquidGlassMaterial,
            liveBackground: true);

        RenderingRouteDecision withoutAcceptance = RenderingRoutePolicy.Decide(new(
            configuration,
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: true,
            WdaCapturePolicyAccepted: false,
            SafeMode: false));
        RenderingRouteDecision accepted = RenderingRoutePolicy.Decide(new(
            configuration,
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: true,
            WdaCapturePolicyAccepted: true,
            SafeMode: false));

        Assert.Equal(RenderingRoute.GpuFixedInput, withoutAcceptance.Route);
        Assert.False(withoutAcceptance.CaptureRequested);
        Assert.Equal("wda_capture_policy_not_accepted", withoutAcceptance.FallbackCode);
        Assert.Equal(RenderingRoute.GpuLiveBackground, accepted.Route);
        Assert.True(accepted.CaptureRequested);
    }

    [Fact]
    public void SafeModeAndInvalidConfigurationFailClosed()
    {
        RenderingRouteDecision safeMode = RenderingRoutePolicy.Decide(new(
            CreateGpuConfiguration(ThemeRenderingConfig.LiquidGlassMaterial),
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: false,
            WdaCapturePolicyAccepted: true,
            SafeMode: true));
        ThemeRenderingConfig invalid = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.LiquidGlassMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(false),
                ["refraction"] = JsonSerializer.SerializeToElement(99)
            }
        };
        RenderingRouteDecision invalidConfiguration = RenderingRoutePolicy.Decide(new(
            invalid,
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: false,
            WdaCapturePolicyAccepted: true,
            SafeMode: false));

        Assert.Equal(RenderingRoute.Legacy, safeMode.Route);
        Assert.Equal("safe_mode", safeMode.FallbackCode);
        Assert.Equal(RenderingRoute.Legacy, invalidConfiguration.Route);
        Assert.Equal("rendering_configuration_invalid", invalidConfiguration.FallbackCode);
        Assert.NotEmpty(invalidConfiguration.Errors);
    }

    [Fact]
    public void LiveGlassRequiresIndependentCaptureOptIn()
    {
        RenderingRouteDecision decision = RenderingRoutePolicy.Decide(new(
            CreateGpuConfiguration(
                ThemeRenderingConfig.LiquidGlassMaterial,
                liveBackground: true),
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: false,
            WdaCapturePolicyAccepted: true,
            SafeMode: false));

        Assert.Equal(RenderingRoute.GpuFixedInput, decision.Route);
        Assert.False(decision.CaptureRequested);
        Assert.Null(decision.FallbackCode);
    }

    [Fact]
    public void UserLiveOptInOverridesPackagedLiquidGlassDefaultFalse()
    {
        ThemeRenderingConfig packaged = CreateGpuConfiguration(
            ThemeRenderingConfig.LiquidGlassMaterial,
            liveBackground: false);

        RenderingRouteDecision decision = RenderingRoutePolicy.Decide(new(
            packaged,
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: true,
            WdaCapturePolicyAccepted: true,
            SafeMode: false));

        Assert.Equal(RenderingRoute.GpuLiveBackground, decision.Route);
        Assert.True(decision.CaptureRequested);
        Assert.Null(decision.FallbackCode);
        Assert.False(
            packaged.Parameters["liveBackground"].GetBoolean(),
            "Routing must not rewrite the packaged theme default.");
    }

    [Fact]
    public void RuntimeMaterialAndMotionValuesAreParsedAndValidated()
    {
        ThemeRenderingConfig configuration = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.LiquidGlassMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(false),
                ["thickness"] = JsonSerializer.SerializeToElement(2.25),
                ["refraction"] = JsonSerializer.SerializeToElement(3.5),
                ["blurRadius"] = JsonSerializer.SerializeToElement(11),
                ["clearCenterRatio"] = JsonSerializer.SerializeToElement(0.4),
                ["surfaceOpacity"] = JsonSerializer.SerializeToElement(0.2),
                ["highlight"] = JsonSerializer.SerializeToElement(0.7),
                ["counterHighlight"] = JsonSerializer.SerializeToElement(0.3),
                ["shadow"] = JsonSerializer.SerializeToElement(0.6)
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

        Assert.Empty(configuration.Validate());
        Assert.Equal(2.25f, settings.Thickness);
        Assert.Equal(3.5f, settings.Refraction);
        Assert.Equal(11f, settings.BlurRadius);
        Assert.Equal(0.4f, settings.ClearCenterRatio);
        Assert.Equal(0.2f, settings.SurfaceOpacity);
        Assert.Equal(0.7f, settings.Highlight);
        Assert.Equal(0.3f, settings.CounterHighlight);
        Assert.Equal(0.6f, settings.Shadow);
        Assert.Equal(45, settings.TargetFramesPerSecond);
        Assert.False(settings.MotionEnabled);
    }

    [Fact]
    public void InvalidMotionValuesFailClosed()
    {
        ThemeRenderingConfig configuration = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.CyberPunkMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(false)
            },
            Motion = new Dictionary<string, JsonElement>
            {
                ["targetFps"] = JsonSerializer.SerializeToElement(241),
                ["motionEnabled"] = JsonSerializer.SerializeToElement("yes")
            }
        };

        IReadOnlyList<string> errors = configuration.Validate();

        Assert.Contains(errors, error => error.Contains("targetFps", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("motionEnabled", StringComparison.Ordinal));
    }

    [Fact]
    public void ThemeEditorCloneAndRestorePreserveRenderingWithoutAliasing()
    {
        Theme original = new()
        {
            Name = "GPU route",
            Version = 5,
            Rendering = CreateGpuConfiguration(ThemeRenderingConfig.LiquidGlassMaterial)
        };

        Theme snapshot = AppearanceThemeConfig.CloneConfig(original);
        snapshot.Rendering!.Parameters["cornerRadius"] = JsonSerializer.SerializeToElement(31);

        Assert.Equal(
            24,
            original.Rendering!.Parameters["cornerRadius"].GetDouble());
        Assert.Equal(
            31,
            snapshot.Rendering.Parameters["cornerRadius"].GetDouble());

        Theme target = new();
        AppearanceThemeConfig.RestoreConfig(target, snapshot);
        Assert.NotNull(target.Rendering);
        Assert.NotSame(snapshot.Rendering, target.Rendering);
        Assert.Equal(
            31,
            target.Rendering!.Parameters["cornerRadius"].GetDouble());
    }

    [Theory]
    [InlineData("LiquidGlass_GPU.json", ThemeRenderingConfig.LiquidGlassMaterial)]
    [InlineData("CyberPunk_GPU.json", ThemeRenderingConfig.CyberPunkMaterial)]
    public void PackagedGpuThemesHaveValidIndependentRenderingSections(
        string fileName,
        string expectedMaterial)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "resources",
            "themes",
            fileName);
        Theme theme = JsonSerializer.Deserialize<Theme>(
            File.ReadAllText(path),
            ThemeManager.CreateSerializerOptions())!;

        Assert.Equal(5, theme.Version);
        Assert.NotNull(theme.Rendering);
        Assert.Equal(expectedMaterial, theme.Rendering!.Material);
        Assert.Empty(theme.Rendering.Validate());
        Assert.False(
            theme.Rendering.Parameters["liveBackground"].GetBoolean());
    }

    [Fact]
    public void PackagedMaterialDefaultsPreserveApprovedRendererConstants()
    {
        Theme liquid = JsonSerializer.Deserialize<Theme>(
            File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory,
                "resources",
                "themes",
                "LiquidGlass_GPU.json")),
            ThemeManager.CreateSerializerOptions())!;
        MaterialRuntimeSettings glass = MaterialRuntimeSettings.Create(
            liquid.Rendering,
            MaterialKind.LiquidGlass);
        Assert.Equal(26f, glass.CornerRadius);
        Assert.Equal(1.6f, glass.Refraction);
        Assert.Equal(8f, glass.BlurRadius);
        Assert.Equal(1f, glass.Thickness);
        Assert.Equal(0.055f, glass.SurfaceOpacity);
        Assert.Equal(1f, glass.Highlight);
        Assert.Equal(1f, glass.CounterHighlight);
        Assert.Equal(0.34f, glass.Shadow);
        Assert.Equal(0f, glass.ClearCenterRatio);

        Theme cyber = JsonSerializer.Deserialize<Theme>(
            File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory,
                "resources",
                "themes",
                "CyberPunk_GPU.json")),
            ThemeManager.CreateSerializerOptions())!;
        MaterialRuntimeSettings cyberSettings = MaterialRuntimeSettings.Create(
            cyber.Rendering,
            MaterialKind.CyberPunk);
        Assert.Equal(14f, cyberSettings.PrimaryCut);
        Assert.Equal(9f, cyberSettings.SecondaryCut);
        Assert.Equal(1f, cyberSettings.OutlineWidth);
        Assert.Equal(12f, cyberSettings.Glow);
        Assert.Equal(0.12f, cyberSettings.AmbientRatio);
        Assert.Equal(0.965f, cyberSettings.PanelOpacity);
    }

    private static ThemeRenderingConfig CreateGpuConfiguration(
        string material,
        bool liveBackground = false) =>
        new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = material,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(liveBackground),
                ["cornerRadius"] = JsonSerializer.SerializeToElement(24),
                ["primaryCut"] = JsonSerializer.SerializeToElement(14)
            }
        };
}
