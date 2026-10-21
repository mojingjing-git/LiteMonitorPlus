using System.Text.Json;
using System.Text.Json.Serialization;
using LiteMonitor.Rendering.Contracts;

namespace LiteMonitor.Rendering.Configuration;

public sealed class ThemeRenderingConfig
{
    public const int CurrentVersion = 1;
    public const string GpuBackend = "gpu";
    public const string LegacyBackend = "legacy";
    public const string LiquidGlassMaterial = "liquid_glass";
    public const string CyberPunkMaterial = "cyber_punk";
    public const string LegacyMaterial = "legacy";
    public const string StandardGlassStyle = "standard";
    public const string FloatingToolbarGlassStyle = "floatingtoolbar";

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("backend")]
    public string Backend { get; set; } = LegacyBackend;

    [JsonPropertyName("material")]
    public string Material { get; set; } = LegacyMaterial;

    [JsonPropertyName("parameters")]
    public Dictionary<string, JsonElement> Parameters { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("motion")]
    public Dictionary<string, JsonElement> Motion { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("fallback")]
    public Dictionary<string, JsonElement> Fallback { get; set; } = new(StringComparer.Ordinal)
    {
        ["backend"] = JsonSerializer.SerializeToElement(LegacyBackend),
        ["material"] = JsonSerializer.SerializeToElement(LegacyMaterial)
    };

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = new(StringComparer.Ordinal);

    public bool IsGpuRequested =>
        string.Equals(Backend, GpuBackend, StringComparison.OrdinalIgnoreCase);

    public RenderingConfig ToContract()
    {
        Dictionary<string, JsonElement> CopyDictionary(Dictionary<string, JsonElement> source) =>
            source.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);

        return new RenderingConfig(
            Version,
            Backend,
            Material,
            CopyDictionary(Parameters ?? new Dictionary<string, JsonElement>()),
            CopyDictionary(Motion ?? new Dictionary<string, JsonElement>()),
            CopyDictionary(Fallback ?? new Dictionary<string, JsonElement>()));
    }

    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];

        if (Version != CurrentVersion)
        {
            errors.Add($"rendering.version must be {CurrentVersion}.");
        }

        string backend = Backend?.Trim().ToLowerInvariant() ?? string.Empty;
        string material = Material?.Trim().ToLowerInvariant() ?? string.Empty;
        if (backend is not GpuBackend and not LegacyBackend)
        {
            errors.Add("rendering.backend must be gpu or legacy.");
        }

        if (material is not LiquidGlassMaterial and not CyberPunkMaterial and not LegacyMaterial)
        {
            errors.Add("rendering.material is not supported.");
        }

        if (backend == LegacyBackend && material != LegacyMaterial)
        {
            errors.Add("rendering.backend=legacy requires rendering.material=legacy.");
        }

        if (backend == GpuBackend && material == LegacyMaterial)
        {
            errors.Add("rendering.backend=gpu requires a GPU material.");
        }

        if (material == LiquidGlassMaterial && !HasBoolean(Parameters, "liveBackground"))
        {
            errors.Add("liquid_glass requires parameters.liveBackground.");
        }

        if (material == CyberPunkMaterial && HasBoolean(Parameters, "liveBackground") &&
            GetBoolean(Parameters, "liveBackground"))
        {
            errors.Add("cyber_punk must not request desktop capture.");
        }

        ValidateNumber(Parameters, "cornerRadius", 0, 64, errors);
        ValidateNumber(Parameters, "thickness", 0, 8, errors);
        ValidateNumber(Parameters, "refraction", 0, 8, errors);
        ValidateNumber(Parameters, "blurRadius", 0, 32, errors);
        ValidateNumber(Parameters, "primaryCut", 0, 64, errors);
        ValidateNumber(Parameters, "secondaryCut", 0, 32, errors);
        ValidateNumber(Parameters, "glow", 0, 100, errors);
        ValidateNumber(Parameters, "outlineWidth", 0, 8, errors);
        ValidateNumber(Parameters, "ambientRatio", 0, 1, errors);
        ValidateNumber(Parameters, "panelOpacity", 0, 1, errors);
        ValidateNumber(Parameters, "clearCenterRatio", 0, 1, errors);
        ValidateNumber(Parameters, "surfaceOpacity", 0, 1, errors);
        ValidateNumber(Parameters, "highlight", 0, 1, errors);
        ValidateNumber(Parameters, "counterHighlight", 0, 1, errors);
        ValidateNumber(Parameters, "shadow", 0, 1, errors);
        ValidateNumber(Parameters, "shadowRadius", 0, 64, errors);
        ValidateNumber(Parameters, "fogStrength", 0, 1, errors);
        ValidateNumber(Parameters, "tintStrength", 0, 1, errors);
        ValidateEnumString(
            Parameters,
            "glassStyle",
            [StandardGlassStyle, FloatingToolbarGlassStyle],
            errors);
        ValidateInteger(Motion, "targetFps", 1, 240, errors);
        ValidateInteger(Motion, "stateTransitionMs", 0, 5000, errors);
        ValidateInteger(Motion, "scannerPeriodMs", 0, 60000, errors);

        if (Motion is not null &&
            Motion.ContainsKey("motionEnabled") &&
            !HasBoolean(Motion, "motionEnabled"))
        {
            errors.Add("rendering.motion.motionEnabled must be a boolean.");
        }

        string fallbackBackend = GetString(Fallback, "backend");
        string fallbackMaterial = GetString(Fallback, "material");
        if (fallbackBackend != LegacyBackend || fallbackMaterial != LegacyMaterial)
        {
            errors.Add("rendering.fallback must select legacy/legacy.");
        }

        return errors;
    }

    private static bool HasBoolean(Dictionary<string, JsonElement>? values, string key) =>
        values is not null &&
        values.TryGetValue(key, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool GetBoolean(Dictionary<string, JsonElement>? values, string key) =>
        values is not null &&
        values.TryGetValue(key, out JsonElement value) &&
        value.ValueKind == JsonValueKind.True;

    private static string GetString(Dictionary<string, JsonElement>? values, string key) =>
        values is not null &&
        values.TryGetValue(key, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
            : string.Empty;

    private static void ValidateNumber(
        Dictionary<string, JsonElement>? values,
        string key,
        double minimum,
        double maximum,
        List<string> errors)
    {
        if (values is null || !values.TryGetValue(key, out JsonElement value))
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double number) ||
            !double.IsFinite(number) ||
            number < minimum ||
            number > maximum)
        {
            errors.Add($"rendering.parameters.{key} must be a finite number in [{minimum}, {maximum}].");
        }
    }

    private static void ValidateInteger(
        Dictionary<string, JsonElement>? values,
        string key,
        int minimum,
        int maximum,
        List<string> errors)
    {
        if (values is null || !values.TryGetValue(key, out JsonElement value))
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int number) ||
            number < minimum ||
            number > maximum)
        {
            errors.Add($"rendering.motion.{key} must be an integer in [{minimum}, {maximum}].");
        }
    }

    private static void ValidateEnumString(
        Dictionary<string, JsonElement>? values,
        string key,
        IReadOnlyCollection<string> supported,
        List<string> errors)
    {
        if (values is null || !values.TryGetValue(key, out JsonElement value))
        {
            return;
        }

        string normalized =
            value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
                : string.Empty;
        if (!supported.Contains(normalized, StringComparer.Ordinal))
        {
            errors.Add(
                $"rendering.parameters.{key} must be one of: {string.Join(", ", supported)}.");
        }
    }
}
