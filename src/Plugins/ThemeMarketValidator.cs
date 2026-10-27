using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiteMonitor.Rendering.Configuration;
#if !THEME_MARKET_CLI
// 仅 CLI 共享编译（LiteMonitor.Cli）需要切断：src.Core 的 Theme DTO 会拖入整个主题模型。
using LiteMonitor.src.Core;
#endif

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 主题市场内容校验结果。错误码稳定，供客户端/仓库 CLI 同例同判据。
    /// </summary>
    public sealed record ThemeMarketValidationResult(bool IsValid, string ErrorCode, string Message)
    {
        public static ThemeMarketValidationResult Ok() => new(true, "ok", "");

        public static ThemeMarketValidationResult Fail(string code, string message)
            => new(false, code, message);
    }

    /// <summary>
    /// 主题市场共享内容校验（无网络、无文件写入、无 Settings/窗口初始化副作用）。
    ///
    /// 规则来源：
    /// - 严格 UTF-8（拒绝替换字符式的“伪解码”）；
    /// - 单一 JSON 对象、无注释/尾逗号；
    /// - 解码后的属性名和字符串值都不得出现 http:// / https://；
    /// - 载荷 1–262144 字节，声明大小必须等于实际字节数；
    /// - route/material 与 rendering.backend/material 互验；
    /// - rendering 块存在时复用 <see cref="ThemeRenderingConfig.Validate()"/> 全规则集；
    /// - rendering 缺席时按纯 legacy 解释（与 ThemeManager.IsGpuTheme 容错口径一致）。
    /// </summary>
    public static class ThemeMarketValidator
    {
        public const int MinThemeBytes = 1;
        public const int MaxThemeBytes = 256 * 1024;

        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private static readonly JsonDocumentOptions DocumentOptions = new()
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        };

        private static readonly JsonSerializerOptions ThemeOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            IgnoreReadOnlyProperties = true,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        };

        public static ThemeMarketValidationResult ValidateFile(string path, string route, string material, int? declaredSize = null)
        {
            try
            {
                return Validate(File.ReadAllBytes(path), route, material, declaredSize);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ThemeMarketValidationResult.Fail("theme_read_failed", "无法读取主题文件：" + ex.Message);
            }
        }

        public static ThemeMarketValidationResult Validate(byte[] raw, string route, string material, int? declaredSize = null)
        {
            if (raw == null || raw.Length < MinThemeBytes)
                return ThemeMarketValidationResult.Fail("theme_empty", "主题载荷为空");

            if (raw.Length > MaxThemeBytes)
                return ThemeMarketValidationResult.Fail("theme_too_large",
                    $"主题载荷 {raw.Length} 字节超过上限 {MaxThemeBytes} 字节");

            if (declaredSize.HasValue && declaredSize.Value != raw.Length)
                return ThemeMarketValidationResult.Fail("theme_size_mismatch",
                    $"索引声明大小 {declaredSize.Value} 字节与实际载荷 {raw.Length} 字节不一致");

            string content;
            try
            {
                content = StrictUtf8.GetString(raw);
                if (content.Length > 0 && content[0] == '\uFEFF')
                    content = content[1..]; // 接受带 BOM 的既有主题，但生成器仍输出无 BOM
            }
            catch (DecoderFallbackException ex)
            {
                return ThemeMarketValidationResult.Fail("theme_utf8_invalid",
                    "主题载荷不是合法 UTF-8 文本：" + ex.Message);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(content, DocumentOptions);
            }
            catch (JsonException ex)
            {
                return ThemeMarketValidationResult.Fail("theme_json_invalid",
                    "主题载荷不是合法 JSON：" + ex.Message);
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return ThemeMarketValidationResult.Fail("theme_root_not_object", "主题文件顶层必须是 JSON 对象");

                if (ContainsRemoteReference(root))
                {
                    return ThemeMarketValidationResult.Fail("theme_remote_reference",
                        "主题文件包含远程资源引用（http/https），违反主题包安全契约");
                }

                // [S04] 已知颜色字段统一校验（与运行时/工作室同一校验器）：
                // 只校验 color / backdrop / rendering.fallbackStyle 内的已知字段，
                // 未知扩展字段不按颜色解析（计划 §6.2.4）。
                // CLI 共享编译不做本项（依赖 src.Core 的 ThemeColorParser；与
                // fallbackStyle 诊断项在 CLI 缺席的既有取舍一致）。
#if !THEME_MARKET_CLI
                IReadOnlyList<string> colorFailures = ThemeColorParser.ValidateThemeJson(root);
                if (colorFailures.Count > 0)
                    return ThemeMarketValidationResult.Fail("theme_color_invalid",
                        "主题颜色字段未通过校验：" + string.Join("; ", colorFailures));
#endif

                string normalizedRoute = (route ?? "").Trim().ToLowerInvariant();
                string normalizedMaterial = (material ?? "").Trim().ToLowerInvariant();
                if (normalizedRoute is not ThemeRenderingConfig.GpuBackend and not ThemeRenderingConfig.LegacyBackend)
                    return ThemeMarketValidationResult.Fail("theme_route_invalid",
                        "索引 route 非法（必须是 gpu 或 legacy）：" + (route ?? "<null>"));

                if (normalizedMaterial is not ThemeRenderingConfig.LiquidGlassMaterial
                    and not ThemeRenderingConfig.CyberPunkMaterial
                    and not ThemeRenderingConfig.LegacyMaterial)
                    return ThemeMarketValidationResult.Fail("theme_material_invalid",
                        "索引 material 非法（必须是 liquid_glass、cyber_punk 或 legacy）：" + (material ?? "<null>"));

                bool hasRendering = root.TryGetProperty("rendering", out JsonElement rendering);
                if (hasRendering && rendering.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
                    return ThemeMarketValidationResult.Fail("theme_rendering_invalid",
                        "rendering 必须是 JSON 对象或 null");

                if (!hasRendering || rendering.ValueKind == JsonValueKind.Null)
                {
                    if (normalizedRoute != ThemeRenderingConfig.LegacyBackend ||
                        normalizedMaterial != ThemeRenderingConfig.LegacyMaterial)
                    {
                        return ThemeMarketValidationResult.Fail("theme_route_mismatch",
                            "主题缺少 rendering 配置（按 legacy 解释），与索引 route/material 声明不一致");
                    }

                    return ThemeMarketValidationResult.Ok();
                }

                if (!rendering.TryGetProperty("backend", out JsonElement backendElement) ||
                    backendElement.ValueKind != JsonValueKind.String)
                    return ThemeMarketValidationResult.Fail("theme_rendering_backend_missing",
                        "rendering.backend 声明缺失或非法，无法与索引 route 互验");

                string backend = backendElement.GetString() ?? "";
                if (!string.Equals(backend, normalizedRoute, StringComparison.OrdinalIgnoreCase))
                    return ThemeMarketValidationResult.Fail("theme_route_mismatch",
                        $"rendering.backend（{backend}）与索引 route（{normalizedRoute}）不一致");

                ThemeDocument? document;
                try
                {
                    document = JsonSerializer.Deserialize<ThemeDocument>(content, ThemeOptions);
                }
                catch (JsonException ex)
                {
                    return ThemeMarketValidationResult.Fail("theme_json_invalid",
                        "主题反序列化失败：" + ex.Message);
                }

                ThemeRenderingConfig? config = document?.Rendering;
                if (config == null)
                    return ThemeMarketValidationResult.Fail("theme_rendering_invalid", "主题 rendering 配置解析为空");

                if (!string.Equals(config.Material, normalizedMaterial, StringComparison.OrdinalIgnoreCase))
                    return ThemeMarketValidationResult.Fail("theme_material_mismatch",
                        $"rendering.material（{config.Material}）与索引 material（{normalizedMaterial}）不一致");

                IReadOnlyList<string> errors;
                try
                {
                    errors = config.Validate();
                }
                catch (Exception ex)
                {
                    return ThemeMarketValidationResult.Fail("theme_rendering_invalid",
                        "主题渲染参数校验异常：" + ex.Message);
                }

                if (errors.Count > 0)
                    return ThemeMarketValidationResult.Fail("theme_rendering_invalid",
                        "主题渲染参数未通过校验：" + string.Join("; ", errors));

                return ThemeMarketValidationResult.Ok();
            }
        }

        private static bool ContainsRemoteReference(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        if (ContainsRemoteReference(property.Name) ||
                            ContainsRemoteReference(property.Value))
                            return true;
                    }
                    return false;

                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        if (ContainsRemoteReference(item))
                            return true;
                    }
                    return false;

                case JsonValueKind.String:
                    return ContainsRemoteReference(element.GetString() ?? "");

                default:
                    return false;
            }
        }

        private static bool ContainsRemoteReference(string text)
            => text.IndexOf("http://", StringComparison.OrdinalIgnoreCase) >= 0
               || text.IndexOf("https://", StringComparison.OrdinalIgnoreCase) >= 0;

        private sealed class ThemeDocument
        {
            [JsonPropertyName("rendering")]
            public ThemeRenderingConfig? Rendering { get; set; }
        }
    }
}
