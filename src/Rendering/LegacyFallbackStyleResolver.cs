using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using LiteMonitor.Rendering.Configuration;

namespace LiteMonitor.Rendering;

/// <summary>
/// 【GPU T05 / 合同 §9.3-§9.6】解析后的 legacy 回退样式（纯 CLR、不可变）。
///
/// ★ 字段是**主题原生色值文本**（不是 Color）：消费方按既有口径
///   <c>ThemeManager.ParseColor</c> 解析即可，保证与主题其它颜色同一套解析规则。
/// </summary>
/// <param name="Background">背景色（页面底色）。</param>
/// <param name="PanelBackground">分组底色。</param>
/// <param name="Border">边框色。</param>
/// <param name="Text">主前景色。</param>
/// <param name="MutedText">次要前景色。</param>
/// <param name="Accent">强调色。</param>
/// <param name="Diagnostics">非致命诊断（当前只含 <c>gpu_fallback_style_invalid</c>）。</param>
public sealed record LegacyFallbackStyle(
    string Background,
    string PanelBackground,
    string Border,
    string Text,
    string MutedText,
    string Accent,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>是否有非法值诊断（此时对应字段已退回默认值）。</summary>
    public bool HasDiagnostics => Diagnostics.Count > 0;
}

/// <summary>
/// 【GPU T05】legacy 回退样式解析器。
///
/// ★ 合同要点：
///   1. **样式随主题走**：数据来自主题自身的 <c>rendering.fallbackStyle</c> 兄弟块，
///      不建立"主题文件名 → 样式"的映射表；
///   2. **不改主题、不改用户 Skin、不改原 GPU 外观**：本解析器是**纯函数**，
///      只读主题、返回一份独立快照，绝不回写主题对象（也不碰 ThemeManager.Current）；
///   3. **缺省 / 非法值都有明确行为**：缺省用中性可读默认值；非法值产生诊断
///      （<see cref="InvalidStyleDiagnosticCode"/>）并对该字段退回默认值，
///      非法值**不**进主题校验的错误列表（合同 §5.3 规则 1：非致命问题一旦进错误列表，
///      会让整份主题被判为不可用）；
///   4. **可读**：默认配色与三份内置 GPU 主题的回退配色都保证正文对比度 ≥ 4.5。
/// </summary>
public static class LegacyFallbackStyleResolver
{
    /// <summary>回退样式非法值的诊断码（合同 §5 错误码表）。</summary>
    public const string InvalidStyleDiagnosticCode = "gpu_fallback_style_invalid";

    // ★ 中性可读默认值（缺省 / 第三方 GPU 主题未声明 fallbackStyle 时使用）。
    //   无主题色相，正文对比度 ≥ 4.5、次要文字 ≥ 3.0（见 ThemeFallbackStyleTests 的断言）。
    public const string DefaultBackground = "#1E2228";
    public const string DefaultPanelBackground = "#282D35";
    public const string DefaultBorder = "#3C444F";
    public const string DefaultText = "#F2F4F7";
    public const string DefaultMutedText = "#B4BBC4";
    public const string DefaultAccent = "#8FB4D9";

    /// <summary>6 个业务字段的读取清单（JSON 名 + 取值器）。</summary>
    private static readonly (string Name, Func<ThemeFallbackStyle, string?> Read)[] StyleFields =
    [
        ("background", style => style.Background),
        ("panelBackground", style => style.PanelBackground),
        ("border", style => style.Border),
        ("text", style => style.Text),
        ("mutedText", style => style.MutedText),
        ("accent", style => style.Accent),
    ];

    /// <summary>
    /// 校验 <c>rendering.fallbackStyle</c> 兄弟块，返回**非致命**诊断。
    /// 空集合 = 没有非法值。
    /// </summary>
    public static IReadOnlyList<string> ValidateFallbackStyle(ThemeRenderingConfig? configuration)
    {
        List<string> diagnostics = [];
        ThemeFallbackStyle? style = configuration?.FallbackStyle;
        if (style == null)
        {
            // 未声明 ⇒ 用默认值，不算非法
            return diagnostics;
        }

        // 解析期就判定的非法值（非字符串值 / 整块不是对象）
        if (style.InvalidFields != null)
        {
            foreach (string field in style.InvalidFields)
            {
                diagnostics.Add(Diagnostic(field));
            }
        }

        foreach ((string name, Func<ThemeFallbackStyle, string?> read) in StyleFields)
        {
            string? value = read(style);
            if (string.IsNullOrWhiteSpace(value))
            {
                // 未声明 ⇒ 用默认值，不算非法
                continue;
            }

            if (!IsSupportedColorText(value))
            {
                diagnostics.Add(Diagnostic(name));
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// 解析 legacy 回退样式：缺省 / 非法值都退回中性可读默认值，并带上诊断。
    /// **不修改**传入的主题对象。
    /// </summary>
    public static LegacyFallbackStyle Resolve(ThemeRenderingConfig? configuration)
    {
        IReadOnlyList<string> diagnostics = ValidateFallbackStyle(configuration);
        ThemeFallbackStyle? style = configuration?.FallbackStyle;

        HashSet<string> invalid = new(StringComparer.OrdinalIgnoreCase);
        if (style?.InvalidFields != null)
        {
            foreach (string field in style.InvalidFields)
            {
                invalid.Add(field);
            }
        }

        string Pick(string name, Func<ThemeFallbackStyle, string?> read, string fallback)
        {
            if (style == null) return fallback;
            if (invalid.Contains(name)) return fallback;                 // 解析期非法 ⇒ 默认值
            string? value = read(style);
            if (string.IsNullOrWhiteSpace(value)) return fallback;      // 缺省 ⇒ 默认值
            if (!IsSupportedColorText(value)) return fallback;          // 颜色不可解析 ⇒ 默认值
            return value;
        }

        return new LegacyFallbackStyle(
            Pick("background", s => s.Background, DefaultBackground),
            Pick("panelBackground", s => s.PanelBackground, DefaultPanelBackground),
            Pick("border", s => s.Border, DefaultBorder),
            Pick("text", s => s.Text, DefaultText),
            Pick("mutedText", s => s.MutedText, DefaultMutedText),
            Pick("accent", s => s.Accent, DefaultAccent),
            diagnostics);
    }

    /// <summary>
    /// 判断色值文本是否是既有解析器（<c>ThemeManager.ParseColor</c>）能接受的格式：
    /// <c>#RRGGBB</c> / <c>#AARRGGBB</c> / <c>rgba(r,g,b,a)</c> / 已知颜色名。
    /// 接受集合与该解析器保持一致（多写一个"格式是否合法"的判据，
    /// 因为 ParseColor 对非法值只回退为全透明色，不抛异常、无法据此判定合法性）。
    /// </summary>
    public static bool IsSupportedColorText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim();

        if (value[0] == '#')
        {
            ReadOnlySpan<char> hex = value.AsSpan(1);
            if (hex.Length != 6 && hex.Length != 8) return false;
            foreach (char c in hex)
            {
                if (!Uri.IsHexDigit(c)) return false;
            }
            return true;
        }

        if (value.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) &&
            value.EndsWith(")", StringComparison.Ordinal))
        {
            return TryParseRgba(value, out _);
        }

        return Color.FromName(value).IsKnownColor;
    }

    private static string Diagnostic(string field) =>
        $"{InvalidStyleDiagnosticCode}: rendering.fallbackStyle.{field}";

    /// <summary>
    /// rgba(r,g,b,a) 的合法性判定（接受条件与 <c>ThemeManager.ParseRgbaColor</c> 一致）：
    /// r/g/b 为 0-255 整数，a 为 0-1 实数；越界 / NaN / Infinity 一律非法。
    /// </summary>
    private static bool TryParseRgba(string value, out Color color)
    {
        color = Color.Transparent;

        int open = value.IndexOf('(');
        int close = value.LastIndexOf(')');
        if (open < 0 || close <= open) return false;

        string body = value.Substring(open + 1, close - open - 1);
        string[] parts = body.Split(',');
        if (parts.Length != 4) return false;

        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ||
            !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int g) ||
            !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int b))
        {
            return false;
        }

        if (r < 0 || r > 255 || g < 0 || g > 255 || b < 0 || b > 255) return false;

        if (!double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double a))
        {
            return false;
        }

        if (double.IsNaN(a) || double.IsInfinity(a)) return false;
        if (a < 0d || a > 1d) return false;

        int alpha = (int)Math.Round(a * 255d, MidpointRounding.AwayFromZero);
        color = Color.FromArgb(alpha, r, g, b);
        return true;
    }
}
