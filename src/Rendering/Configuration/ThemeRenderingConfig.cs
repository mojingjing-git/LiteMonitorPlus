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

    /// <summary>
    /// 【GPU T05 / 合同 §9.2-§9.6】legacy 回退样式块（**加法式兄弟块**）。
    ///
    /// ★ 为什么是兄弟块而不是塞进 <see cref="Fallback"/>：
    ///   <see cref="Fallback"/> 的类型是 Dictionary&lt;string, JsonElement&gt;，
    ///   放进去的新字段必然是 JsonElement，直接违反"新增字段必须用纯 CLR 类型"的硬依赖；
    ///   兄弟块既满足纯 CLR，又满足"样式随主题走、不建文件名映射表"的意图，且对既有字段零破坏性。
    ///   <see cref="Fallback"/> 的类型、backend/material 语义与校验规则**一字未改**。
    ///
    /// ★ 序列化缺省：null 时不写出 ⇒ 旧主题的保存结果与像素输出不变。
    /// </summary>
    [JsonPropertyName("fallbackStyle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThemeFallbackStyle? FallbackStyle { get; set; }

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

    public IReadOnlyList<string> Validate() => Validate(out _);

    /// <summary>
    /// 校验主题，并额外返回**非致命**诊断。
    ///
    /// ★【GPU T05】新增兄弟块 <c>rendering.fallbackStyle</c> 的校验从这里接入：
    ///   1. <see cref="Fallback"/>（backend/material 必须 legacy/legacy）的规则**一字未改**；
    ///   2. <c>fallbackStyle</c> 的非法值属于**非致命**问题（合同 §5.3 规则 1）——
    ///      一旦进入本方法的错误列表，<c>RenderingRoutePolicy.Decide</c> 会把**整份主题**
    ///      判为不可用（rendering_configuration_invalid），一条样式笔误就会让 GPU 路由整体失效；
    ///      因此这里只产出诊断码（<c>gpu_fallback_style_invalid</c>），由调用方按需上报。
    /// </summary>
    /// <param name="diagnostics">非致命诊断（当前只含 fallbackStyle 相关），不参与"整份主题是否可用"的判定。</param>
    public IReadOnlyList<string> Validate(out IReadOnlyList<string> diagnostics)
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

        // 【GPU T05】新增兄弟块 rendering.fallbackStyle 的校验接入点：
        //   非法值只进诊断通道，不进上面的错误列表（合同 §5.3 规则 1）。
        diagnostics = LegacyFallbackStyleResolver.ValidateFallbackStyle(this);

        return errors;
    }

    /// <summary>
    /// 【GPU T05】只校验新增的 <c>rendering.fallbackStyle</c> 兄弟块。
    /// 返回诊断码（<c>gpu_fallback_style_invalid</c>），**不**影响整份主题是否可用。
    /// </summary>
    public IReadOnlyList<string> ValidateFallbackStyle() =>
        LegacyFallbackStyleResolver.ValidateFallbackStyle(this);

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

/// <summary>
/// 【GPU T05 / 合同 §9.3】legacy 回退样式块（<c>rendering.fallbackStyle</c>）的类型化载体。
///
/// ★ 6 个业务字段**全部是纯 CLR 类型**（<see cref="string"/>），不含 JsonElement：
///   编辑器克隆路径（AppearanceFields.CloneValue）对 struct 按值返回，
///   用纯 CLR 类型可彻底避开 JsonElement 的脱离问题。
///   JsonElement 只出现在 <see cref="ExtensionData"/> 里（仅承载块内未知键，且解析时已做脱离克隆）。
/// </summary>
[JsonConverter(typeof(ThemeFallbackStyleJsonConverter))]
public sealed class ThemeFallbackStyle
{
    /// <summary>解析期标记：整个 fallbackStyle 块不是 JSON 对象。</summary>
    public const string InvalidBlockField = "*";

    /// <summary>背景色（页面底色）。</summary>
    [JsonPropertyName("background")]
    public string? Background { get; set; }

    /// <summary>分组底色。</summary>
    [JsonPropertyName("panelBackground")]
    public string? PanelBackground { get; set; }

    /// <summary>边框色。</summary>
    [JsonPropertyName("border")]
    public string? Border { get; set; }

    /// <summary>主前景色。</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>次要前景色。</summary>
    [JsonPropertyName("mutedText")]
    public string? MutedText { get; set; }

    /// <summary>强调色（告警语义之外的装饰强调）。</summary>
    [JsonPropertyName("accent")]
    public string? Accent { get; set; }

    /// <summary>块内未知键（原样保留，保存后不丢）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 解析期发现的非法字段名（非字符串值 / 整块不是对象）。
    /// 只作诊断用，**不**进 <see cref="ThemeRenderingConfig.Validate"/> 的错误列表。
    /// </summary>
    [JsonIgnore]
    public List<string> InvalidFields { get; set; } = new();
}

/// <summary>
/// 【GPU T05】<see cref="ThemeFallbackStyle"/> 的 JSON 转换器。
///
/// ★ 为什么需要自定义转换器：
///   STJ 默认把 JSON 里的数字/对象直接喂给 string 属性会抛 JsonException，
///   那样"字段存在但不是 string"会让**整份主题加载失败**。
///   合同 §9.5 要求这类值被忽略并产生诊断，因此这里手工解析：
///   - 已知键只接受字符串；非字符串 → 忽略该字段并记入 InvalidFields；
///   - 未知键 → 进 ExtensionData（原样保留，解析时已脱离）；
///   - 整块不是对象 → 忽略整块并记入 InvalidFields（<see cref="ThemeFallbackStyle.InvalidBlockField"/>）。
/// </summary>
public sealed class ThemeFallbackStyleJsonConverter : JsonConverter<ThemeFallbackStyle>
{
    public override ThemeFallbackStyle? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        // "fallbackStyle": null ⇒ 未声明（与缺省同义）
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        var style = new ThemeFallbackStyle();

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            style.InvalidFields.Add(ThemeFallbackStyle.InvalidBlockField);
            reader.Skip();
            return style;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return style;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Skip();
                continue;
            }

            string name = reader.GetString() ?? string.Empty;
            if (!reader.Read())
            {
                break;
            }

            // 已知键按大小写不敏感匹配（与既有序列化选项口径一致）
            switch (name.ToLowerInvariant())
            {
                case "background":
                    style.Background = ReadStyleValue(ref reader, style, "background");
                    break;
                case "panelbackground":
                    style.PanelBackground = ReadStyleValue(ref reader, style, "panelBackground");
                    break;
                case "border":
                    style.Border = ReadStyleValue(ref reader, style, "border");
                    break;
                case "text":
                    style.Text = ReadStyleValue(ref reader, style, "text");
                    break;
                case "mutedtext":
                    style.MutedText = ReadStyleValue(ref reader, style, "mutedText");
                    break;
                case "accent":
                    style.Accent = ReadStyleValue(ref reader, style, "accent");
                    break;
                default:
                    style.ExtensionData[name] = ReadUnknownValue(ref reader);
                    break;
            }
        }

        return style;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ThemeFallbackStyle value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteStyleValue(writer, "background", value.Background);
        WriteStyleValue(writer, "panelBackground", value.PanelBackground);
        WriteStyleValue(writer, "border", value.Border);
        WriteStyleValue(writer, "text", value.Text);
        WriteStyleValue(writer, "mutedText", value.MutedText);
        WriteStyleValue(writer, "accent", value.Accent);
        foreach (KeyValuePair<string, JsonElement> pair in value.ExtensionData)
        {
            writer.WritePropertyName(pair.Key);
            pair.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static string? ReadStyleValue(
        ref Utf8JsonReader reader,
        ThemeFallbackStyle style,
        string field)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Null:
                return null;
            default:
                // 非字符串值（数字/对象/数组/布尔）→ 忽略该字段并记诊断（合同 §9.5）
                reader.Skip();
                style.InvalidFields.Add(field);
                return null;
        }
    }

    private static JsonElement ReadUnknownValue(ref Utf8JsonReader reader)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        // 脱离父文档 ⇒ 后续克隆 / 保存都不会抛 ObjectDisposedException
        return document.RootElement.Clone();
    }

    private static void WriteStyleValue(Utf8JsonWriter writer, string name, string? value)
    {
        // 未声明的字段不写出（null = 未声明）
        if (value is null) return;
        writer.WriteString(name, value);
    }
}
