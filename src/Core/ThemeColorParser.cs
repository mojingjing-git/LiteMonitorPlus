using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using LiteMonitor.Rendering.Configuration;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// [S04] 统一颜色校验结果。
    /// ★ 解析失败是独立状态（<see cref="Success"/>=false），与"合法的透明色"
    /// （Success=true 且 <see cref="Color"/> 为全透明）严格区分——缓存同样按此区分，
    /// 不会把两者混成同一条"成功"记录。
    /// </summary>
    public readonly struct ThemeColorParseResult
    {
        public bool Success { get; init; }
        public Color Color { get; init; }

        /// <summary>失败原因（可定位到具体输入形态）；成功时为 null。</summary>
        public string? Diagnostic { get; init; }

        public static ThemeColorParseResult Ok(Color color)
            => new() { Success = true, Color = color, Diagnostic = null };

        public static ThemeColorParseResult Fail(string diagnostic)
            => new() { Success = false, Color = Color.Transparent, Diagnostic = diagnostic };
    }

    /// <summary>
    /// [S04] 主题颜色统一校验器（工作室 / 主题市场 / 运行时共用同一规则）。
    ///
    /// 固定合同（计划 §6.1）：
    /// - <c>#RRGGBB</c> / <c>#AARRGGBB</c>（八位 alpha 在最前两位）/ <c>rgba(r,g,b,a)</c>
    ///   / 已知命名色保持原义；alpha 既有取值约定不变（hex 直写、rgba 0–1）；
    /// - 合法透明色（<c>transparent</c>、alpha 0）是有效输入；
    /// - null / 空白回落 <see cref="Color.Transparent"/> 属于既有合法约定（主题 DTO 可空字段）；
    /// - 其余输入（非法十六进制、错误长度、未知命名色、越界/非有限 rgba）一律**明确失败**
    ///   并携带可定位诊断，不再静默回退。
    ///
    /// 运行时兜底语义由 <see cref="ThemeManager.ParseColor"/> 维持（失败仍回透明、不抛异常），
    /// 但诊断会输出；工作室与市场直接消费 <see cref="TryParse"/> 的成败状态。
    /// </summary>
    public static class ThemeColorParser
    {
        private static readonly object CacheLock = new();
        private static readonly Dictionary<string, ThemeColorParseResult> Cache =
            new(StringComparer.Ordinal);

        /// <summary>color 节点内的已知颜色字段（未知扩展字段不按颜色解析——计划 §6.2.4）。</summary>
        internal static readonly string[] KnownColorFieldNames =
        {
            nameof(ColorConfig.Background),
            nameof(ColorConfig.TextTitle),
            nameof(ColorConfig.TextGroup),
            nameof(ColorConfig.TextPrimary),
            nameof(ColorConfig.ValueSafe),
            nameof(ColorConfig.ValueWarn),
            nameof(ColorConfig.ValueCrit),
            nameof(ColorConfig.BarBackground),
            nameof(ColorConfig.BarLow),
            nameof(ColorConfig.BarMid),
            nameof(ColorConfig.BarHigh),
            nameof(ColorConfig.GroupBackground),
            nameof(ColorConfig.Border),
            nameof(ColorConfig.Shadow),
            nameof(ColorConfig.TextSecondary)
        };

        /// <summary>backdrop 节点内的已知颜色字段（Mode/Opacity 等非色字段不在列）。</summary>
        internal static readonly string[] KnownBackdropColorFieldNames =
        {
            nameof(BackdropConfig.Tint),
            nameof(BackdropConfig.GradientStart),
            nameof(BackdropConfig.GradientEnd)
        };

        /// <summary>rendering.fallbackStyle 节点内的已知颜色字段。</summary>
        internal static readonly string[] KnownFallbackColorFieldNames =
        {
            nameof(ThemeFallbackStyle.Background),
            nameof(ThemeFallbackStyle.PanelBackground),
            nameof(ThemeFallbackStyle.Border),
            nameof(ThemeFallbackStyle.Text),
            nameof(ThemeFallbackStyle.MutedText),
            nameof(ThemeFallbackStyle.Accent)
        };

        /// <summary>
        /// 统一解析入口。成功时返回解析后的颜色；失败时返回独立失败状态与诊断。
        /// 结果缓存区分成功与失败，同一输入结论稳定。
        /// </summary>
        public static ThemeColorParseResult TryParse(string? input)
        {
            // null / 空白：主题 DTO 的可空字段约定，回落透明属合法输入（§6.1）。
            if (string.IsNullOrWhiteSpace(input))
                return ThemeColorParseResult.Ok(Color.Transparent);

            string key = input.Trim();

            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out ThemeColorParseResult cached))
                    return cached;
            }

            ThemeColorParseResult result = ParseCore(key);

            lock (CacheLock)
            {
                Cache[key] = result;
            }
            return result;
        }

        /// <summary>主题切换时清理缓存（与 <see cref="ThemeManager.ClearCaches"/> 配套）。</summary>
        public static void ClearCache()
        {
            lock (CacheLock)
            {
                Cache.Clear();
            }
        }

        private static ThemeColorParseResult ParseCore(string value)
        {
            if (value[0] == '#')
            {
                ReadOnlySpan<char> hex = value.AsSpan(1);
                if (hex.Length != 6 && hex.Length != 8)
                    return ThemeColorParseResult.Fail(
                        $"十六进制颜色长度必须是 6 或 8 位（#RRGGBB / #AARRGGBB，八位时 alpha 在最前两位），实际 {hex.Length} 位");

                int a = 255;
                int rgbStart = 0;
                if (hex.Length == 8)
                {
                    if (!TryHexByte(hex.Slice(0, 2), out a))
                        return ThemeColorParseResult.Fail("非法十六进制字符（alpha 段）：" + value);
                    rgbStart = 2;
                }

                if (!TryHexByte(hex.Slice(rgbStart, 2), out int r) ||
                    !TryHexByte(hex.Slice(rgbStart + 2, 2), out int g) ||
                    !TryHexByte(hex.Slice(rgbStart + 4, 2), out int b))
                    return ThemeColorParseResult.Fail("非法十六进制字符：" + value);

                return ThemeColorParseResult.Ok(Color.FromArgb(a, r, g, b));
            }

            if (value.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) &&
                value.EndsWith(")", StringComparison.Ordinal))
                return ParseRgba(value);

            // 命名色：Color.FromName 对未知名返回 (0,0,0,0) 且 IsKnownColor=false，
            // 用 IsKnownColor 区分"合法命名色（含 Transparent）"与"未知名称"。
            Color named = Color.FromName(value);
            if (named.IsKnownColor)
                return ThemeColorParseResult.Ok(named);

            return ThemeColorParseResult.Fail("未知命名色：" + value);
        }

        private static bool TryHexByte(ReadOnlySpan<char> hex, out int value)
        {
            // Convert.ToInt32(string, 16) 接受前导符号与空白，这里逐字符校验更严格。
            if (hex.Length != 2 || !Uri.IsHexDigit(hex[0]) || !Uri.IsHexDigit(hex[1]))
            {
                value = 0;
                return false;
            }
            value = Convert.ToInt32(hex.ToString(), 16);
            return true;
        }

        /// <summary>
        /// rgba(r,g,b,a)：r/g/b 为 0–255 整数，a 为 0–1 实数（InvariantCulture，
        /// 不受系统区域设置影响）；越界 / NaN / Infinity / 分段数错误一律明确失败。
        /// </summary>
        private static ThemeColorParseResult ParseRgba(string value)
        {
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open)
                return ThemeColorParseResult.Fail("rgba 括号不配对：" + value);

            string body = value.Substring(open + 1, close - open - 1);
            string[] parts = body.Split(',');
            if (parts.Length != 4)
                return ThemeColorParseResult.Fail($"rgba 需要 4 个分段（r,g,b,a），实际 {parts.Length} 个");

            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ||
                !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int g) ||
                !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int b))
                return ThemeColorParseResult.Fail("rgba 的 r/g/b 必须是整数：" + value);

            if (r < 0 || r > 255 || g < 0 || g > 255 || b < 0 || b > 255)
                return ThemeColorParseResult.Fail($"rgba 的 r/g/b 超出 0–255：{r},{g},{b}");

            if (!double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double a))
                return ThemeColorParseResult.Fail("rgba 的 alpha 必须是 0–1 实数（InvariantCulture）：" + value);
            if (double.IsNaN(a) || double.IsInfinity(a))
                return ThemeColorParseResult.Fail("rgba 的 alpha 不能是 NaN/Infinity：" + value);
            if (a < 0d || a > 1d)
                return ThemeColorParseResult.Fail("rgba 的 alpha 超出 0–1：" + value);

            int alpha = (int)Math.Round(a * 255d, MidpointRounding.AwayFromZero);
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;

            return ThemeColorParseResult.Ok(Color.FromArgb(alpha, r, g, b));
        }

        /// <summary>
        /// 校验主题对象内的全部已知颜色字段（color 节点 + backdrop 节点 +
        /// rendering.fallbackStyle 节点）。返回 "字段路径: 诊断" 列表；空列表 = 全部合法。
        /// 未知扩展字段一律跳过，不按颜色解析。
        /// </summary>
        public static IReadOnlyList<string> ValidateTheme(Theme? theme)
        {
            List<string> failures = new();
            if (theme == null) return failures;

            CollectColorFields(theme.Color, "color", KnownColorFieldNames, failures);

            if (theme.Backdrop != null)
                CollectColorFields(theme.Backdrop, "backdrop", KnownBackdropColorFieldNames, failures);

            if (theme.Rendering?.FallbackStyle != null)
                CollectColorFields(
                    theme.Rendering.FallbackStyle, "rendering.fallbackStyle",
                    KnownFallbackColorFieldNames, failures);

            return failures;
        }

        private static void CollectColorFields(object owner, string section, string[] fieldNames, List<string> failures)
        {
            Type type = owner.GetType();
            foreach (string field in fieldNames)
            {
                var property = type.GetProperty(field, BindingFlags.Public | BindingFlags.Instance);
                string? raw = property?.GetValue(owner) as string;
                if (string.IsNullOrWhiteSpace(raw))
                    continue; // 空白回落透明是合法约定，不产诊断
                if (!TryParse(raw).Success)
                    failures.Add($"{section}.{field}: {TryParse(raw).Diagnostic}");
            }
        }

        /// <summary>
        /// [S04] 主题市场载荷校验：只校验 color / backdrop / rendering.fallbackStyle
        /// 节点内的**已知**字段；节点缺失或字段缺失不报错；未知扩展字段不按颜色解析。
        /// 返回空列表 = 通过。
        /// </summary>
        public static IReadOnlyList<string> ValidateThemeJson(JsonElement root)
        {
            List<string> failures = new();
            if (root.ValueKind != JsonValueKind.Object) return failures;

            ValidateSection(root, "color", KnownColorFieldNames, failures);
            ValidateSection(root, "backdrop", KnownBackdropColorFieldNames, failures);

            if (root.TryGetProperty("rendering", out JsonElement rendering) &&
                rendering.ValueKind == JsonValueKind.Object &&
                rendering.TryGetProperty("fallbackStyle", out JsonElement fallbackStyle) &&
                fallbackStyle.ValueKind == JsonValueKind.Object)
            {
                foreach (string field in KnownFallbackColorFieldNames)
                {
                    string camel = char.ToLowerInvariant(field[0]) + field[1..];
                    if (fallbackStyle.TryGetProperty(camel, out JsonElement value) &&
                        value.ValueKind == JsonValueKind.String)
                    {
                        string raw = value.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            var parsed = TryParse(raw);
                            if (!parsed.Success)
                                failures.Add($"rendering.fallbackStyle.{camel}: {parsed.Diagnostic}");
                        }
                    }
                }
            }

            return failures;
        }

        private static void ValidateSection(JsonElement root, string section, string[] fieldNames, List<string> failures)
        {
            if (!root.TryGetProperty(section, out JsonElement node) ||
                node.ValueKind != JsonValueKind.Object)
                return;

            foreach (string field in fieldNames)
            {
                if (!node.TryGetProperty(field, out JsonElement value) ||
                    value.ValueKind != JsonValueKind.String)
                    continue; // 缺失/非字符串走模型默认或既有解析诊断，这里只管"写了颜色值"的字段
                string raw = value.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                var parsed = TryParse(raw);
                if (!parsed.Success)
                    failures.Add($"{section}.{field}: {parsed.Diagnostic}");
            }
        }
    }
}
