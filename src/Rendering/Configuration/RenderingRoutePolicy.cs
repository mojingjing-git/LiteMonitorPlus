using LiteMonitor.Rendering.Contracts;

namespace LiteMonitor.Rendering.Configuration;

public enum RenderingRoute
{
    Legacy,
    GpuFixedInput,
    GpuLiveBackground
}

/// <summary>
/// 【T02 合同 §3.1 / 裁决 D9 配套】期望路由的输入。
/// <para><c>WindowIsLayered</c> 是**唯一**新增成员，且为**尾部可选参数、默认 false</c>：
/// 既有 <c>src/UI/UIController.cs:941-946</c> 的 4 处具名参数构造**无需改动即可编译**，
/// 且未显式传入时 <c>Decide</c> 的输出与改造前**逐位相同**（合同 §3.1"既有行为零变化"）。</para>
/// </summary>
public sealed record RenderingRouteRequest(
    ThemeRenderingConfig? Configuration,
    bool GpuEffectsEnabled,
    bool LiveBackgroundEnabled,
    bool WdaCapturePolicyAccepted,
    bool SafeMode,
    bool WindowIsLayered = false);

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

        // 【T02 合同 §3.1 / GPU-003】期望仍是 GPU live，但窗口处于分层窗口 ⇒ 不得申请 WDA 排除
        // ⇒ 降级为 GpuFixedInput（既有路线，RenderingRoutePolicy.cs:95-102 同一形状），
        //    并给出独立原因码；期望路由 = 有效路由中的"有效"侧由协调器承担。
        //
        // 放置位置：既有 :104-113 的 WdaCapturePolicyAccepted 判定**之后** ⇒
        // safe_mode（:61）、gpu_effects_opt_in_required（:66）等既有原因码**优先级不变**。
        if (request.WindowIsLayered)
        {
            return new RenderingRouteDecision(
                RenderingRoute.GpuFixedInput,
                RenderBackendKind.Gpu,
                MaterialKind.LiquidGlass,
                CaptureRequested: false,
                FallbackCode: "wda_capture_policy_unsupported_on_layered_window",
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
