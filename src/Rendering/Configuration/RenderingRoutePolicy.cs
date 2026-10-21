using LiteMonitor.Rendering.Contracts;

namespace LiteMonitor.Rendering.Configuration;

public enum RenderingRoute
{
    Legacy,
    GpuFixedInput,
    GpuLiveBackground
}

public sealed record RenderingRouteRequest(
    ThemeRenderingConfig? Configuration,
    bool GpuEffectsEnabled,
    bool LiveBackgroundEnabled,
    bool WdaCapturePolicyAccepted,
    bool SafeMode);

public sealed record RenderingRouteDecision(
    RenderingRoute Route,
    RenderBackendKind EffectiveBackend,
    MaterialKind EffectiveMaterial,
    bool CaptureRequested,
    string? FallbackCode,
    IReadOnlyList<string> Errors)
{
    public bool IsGpu => Route != RenderingRoute.Legacy;
}

public static class RenderingRoutePolicy
{
    public static RenderingRouteDecision Decide(RenderingRouteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ThemeRenderingConfig? configuration = request.Configuration;
        if (configuration is null)
        {
            return Legacy("rendering_configuration_absent");
        }

        IReadOnlyList<string> errors = configuration.Validate();
        if (errors.Count > 0)
        {
            return new RenderingRouteDecision(
                RenderingRoute.Legacy,
                RenderBackendKind.LegacyGdiPlus,
                MaterialKind.Legacy,
                CaptureRequested: false,
                FallbackCode: "rendering_configuration_invalid",
                errors);
        }

        string backend = configuration.Backend.Trim().ToLowerInvariant();
        string material = configuration.Material.Trim().ToLowerInvariant();
        if (backend == ThemeRenderingConfig.LegacyBackend)
        {
            return Legacy(null);
        }

        if (request.SafeMode)
        {
            return Fallback("safe_mode");
        }

        if (!request.GpuEffectsEnabled)
        {
            return Fallback("gpu_effects_opt_in_required");
        }

        MaterialKind materialKind = material switch
        {
            ThemeRenderingConfig.LiquidGlassMaterial => MaterialKind.LiquidGlass,
            ThemeRenderingConfig.CyberPunkMaterial => MaterialKind.CyberPunk,
            _ => MaterialKind.Legacy
        };

        if (materialKind == MaterialKind.CyberPunk)
        {
            return new RenderingRouteDecision(
                RenderingRoute.GpuFixedInput,
                RenderBackendKind.Gpu,
                MaterialKind.CyberPunk,
                CaptureRequested: false,
                FallbackCode: null,
                errors);
        }

        // The material declares live-background capability/default in the theme,
        // while the user setting is the independent opt-in. A packaged default
        // of false must not make the settings switch ineffective.
        bool liveBackground = request.LiveBackgroundEnabled;
        if (!liveBackground)
        {
            return new RenderingRouteDecision(
                RenderingRoute.GpuFixedInput,
                RenderBackendKind.Gpu,
                MaterialKind.LiquidGlass,
                CaptureRequested: false,
                FallbackCode: null,
                errors);
        }

        if (!request.WdaCapturePolicyAccepted)
        {
            return new RenderingRouteDecision(
                RenderingRoute.GpuFixedInput,
                RenderBackendKind.Gpu,
                MaterialKind.LiquidGlass,
                CaptureRequested: false,
                FallbackCode: "wda_capture_policy_not_accepted",
                errors);
        }

        return new RenderingRouteDecision(
            RenderingRoute.GpuLiveBackground,
            RenderBackendKind.Gpu,
            MaterialKind.LiquidGlass,
            CaptureRequested: true,
            FallbackCode: null,
            errors);
    }

    private static RenderingRouteDecision Legacy(string? fallbackCode) =>
        new(
            RenderingRoute.Legacy,
            RenderBackendKind.LegacyGdiPlus,
            MaterialKind.Legacy,
            CaptureRequested: false,
            fallbackCode,
            []);

    private static RenderingRouteDecision Fallback(string code) =>
        new(
            RenderingRoute.Legacy,
            RenderBackendKind.LegacyGdiPlus,
            MaterialKind.Legacy,
            CaptureRequested: false,
            code,
            []);

    private static bool GetBoolean(
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> values,
        string key) =>
        values.TryGetValue(key, out System.Text.Json.JsonElement value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.True;
}
