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

        if (materialKind == MaterialKind.CyberPunk ||
            UsesFixedNamedVisualPreset(configuration))
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

        // 【步0 实验 2026-10-24 / R3 §3.2】layered 判定降级为**仅诊断**。
        // 原硬闸（T02 合同 GPU-003："分层 ⇒ GpuFixedInput + wda_capture_policy_unsupported_on_layered_window"）
        // 的前提"WS_EX_LAYERED 与 WDA_EXCLUDEFROMCAPTURE 互斥"已被 legacy 路线在**同一块分层窗口**
        // （Opacity=0.85）上实测证伪（R3 §1.4：生效模式=Glass、已发布帧持续增长、亲和性 0x11）。
        // 本实验不再因分层改变 Route；分层事实以诊断条目记入 Errors，供状态行与排障读取。
        // 实测结论决定本改动去留（R3 §3.4 三分支：A 保留 / C 强制不透明 / B 另出计划）。
        if (request.WindowIsLayered)
        {
            errors = errors.Append("diagnostic_window_is_layered").ToList();
        }

        return new RenderingRouteDecision(
            RenderingRoute.GpuLiveBackground,
            RenderBackendKind.Gpu,
            MaterialKind.LiquidGlass,
            CaptureRequested: true,
            FallbackCode: null,
            errors);
    }

    private static bool UsesFixedNamedVisualPreset(
        ThemeRenderingConfig configuration)
    {
        if (configuration.Parameters is not { } parameters ||
            !parameters.TryGetValue(
                "visualPreset",
                out System.Text.Json.JsonElement value) ||
            value.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return false;
        }

        string? preset = value.GetString()?.Trim();
        return string.Equals(
                   preset,
                   ThemeRenderingConfig.NebulaAuroraVisualPreset,
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   preset,
                   ThemeRenderingConfig.NixieBrassVisualPreset,
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   preset,
                   ThemeRenderingConfig.CrtPhosphorVisualPreset,
                   StringComparison.OrdinalIgnoreCase);
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
