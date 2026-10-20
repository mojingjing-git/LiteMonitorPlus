using System;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Threading;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.WebServer
{
    /// <summary>
    /// Web 端主题颜色快照（**不可变**）。
    ///
    /// ★ 合同（外观改造计划 A11 / v4 §7.3）：
    /// 1. 快照只保存**已经解析好的 Color**（值类型 → 天然不可变），所有属性只读；
    ///    线程之间传递的是同一份内容，不存在"读到一半被换掉"的可能。
    /// 2. 快照由 **UI 层发布**（见 <see cref="WebThemeColorPublisher"/>），
    ///    HTML 构造**只消费**快照：不解析主题、不读主题文件、不碰硬件。
    /// 3. 规范化只发生在**运行时副本**里，**绝不回写用户 JSON**。
    /// 4. 非法 / 未知色值一律走**写死的回退色**：
    ///    主题原文永远不会进入 CSS（→ 注入面为零，见 <see cref="TryParseThemeColor"/>）。
    /// </summary>
    public sealed class WebThemeColorSnapshot
    {
        // ============================================================
        // 回退色（写死的安全色；与网页内置默认配色一致）
        // ============================================================

        /// <summary>背景色回退（深色中性）。</summary>
        public static readonly Color FallbackBackground = Color.FromArgb(255, 0x09, 0x09, 0x0b);
        /// <summary>组块背景色回退（深色中性）。</summary>
        public static readonly Color FallbackGroupBackground = Color.FromArgb(255, 0x14, 0x14, 0x17);
        /// <summary>描边色回退（深色中性）。</summary>
        public static readonly Color FallbackBorder = Color.FromArgb(255, 0x27, 0x27, 0x2a);
        /// <summary>主文字色回退（浅色中性）。</summary>
        public static readonly Color FallbackTextPrimary = Color.FromArgb(255, 0xf4, 0xf4, 0xf5);
        /// <summary>次级文字色回退（浅色中性）。</summary>
        public static readonly Color FallbackTextSecondary = Color.FromArgb(255, 0x71, 0x71, 0x7a);
        /// <summary>正常值色回退（绿）。</summary>
        public static readonly Color FallbackValueSafe = Color.FromArgb(255, 0x10, 0xb9, 0x81);
        /// <summary>警告值色回退（橙）。</summary>
        public static readonly Color FallbackValueWarn = Color.FromArgb(255, 0xf5, 0x9e, 0x0b);
        /// <summary>危险值色回退（红）。</summary>
        public static readonly Color FallbackValueCrit = Color.FromArgb(255, 0xef, 0x44, 0x44);

        /// <summary>
        /// 全回退快照（UI 层还没发布任何快照时使用）。
        /// ★ 页面外观与改造前**完全一致**（网页 CSS 里的默认值就是这组颜色）。
        /// </summary>
        public static WebThemeColorSnapshot Default { get; } = new WebThemeColorSnapshot(
            FallbackBackground,
            FallbackGroupBackground,
            FallbackBorder,
            FallbackTextPrimary,
            FallbackTextSecondary,
            FallbackValueSafe,
            FallbackValueWarn,
            FallbackValueCrit);

        // ============================================================
        // 快照内容（全部只读）
        // ============================================================

        /// <summary>页面背景色（对应主题的 Background）。</summary>
        public Color Background { get; }
        /// <summary>卡片 / 组块背景色（对应主题的 GroupBackground）。</summary>
        public Color GroupBackground { get; }
        /// <summary>边框色（对应主题的 Border；主题没写时用回退色）。</summary>
        public Color Border { get; }
        /// <summary>主文字色（对应主题的 TextPrimary）。</summary>
        public Color TextPrimary { get; }
        /// <summary>次级文字色（对应主题的 TextSecondary；主题没写时用回退色）。</summary>
        public Color TextSecondary { get; }
        /// <summary>正常值色（对应主题的 ValueSafe）。</summary>
        public Color ValueSafe { get; }
        /// <summary>警告值色（对应主题的 ValueWarn）。</summary>
        public Color ValueWarn { get; }
        /// <summary>危险值色（对应主题的 ValueCrit）。</summary>
        public Color ValueCrit { get; }

        private WebThemeColorSnapshot(
            Color background,
            Color groupBackground,
            Color border,
            Color textPrimary,
            Color textSecondary,
            Color valueSafe,
            Color valueWarn,
            Color valueCrit)
        {
            Background = background;
            GroupBackground = groupBackground;
            Border = border;
            TextPrimary = textPrimary;
            TextSecondary = textSecondary;
            ValueSafe = valueSafe;
            ValueWarn = valueWarn;
            ValueCrit = valueCrit;
        }

        /// <summary>
        /// 用**已经解析好的颜色**构造快照（供 UI 层直接发布已知的运行时颜色）。
        /// </summary>
        public static WebThemeColorSnapshot Create(
            Color background,
            Color groupBackground,
            Color border,
            Color textPrimary,
            Color textSecondary,
            Color valueSafe,
            Color valueWarn,
            Color valueCrit)
        {
            return new WebThemeColorSnapshot(
                background, groupBackground, border, textPrimary,
                textSecondary, valueSafe, valueWarn, valueCrit);
        }

        /// <summary>
        /// 从主题对象生成快照（**发布方**用；HTML 构造侧不调用本方法）。
        ///
        /// ★ 只读主题，不修改主题：解析结果全部落在新建的快照里，
        ///   用户 JSON 与主题对象**一个字节都不会变**。
        /// </summary>
        /// <param name="theme">当前主题（可为 null → 全部走回退色）。</param>
        public static WebThemeColorSnapshot FromTheme(Theme? theme)
        {
            ColorConfig? cfg = theme?.Color;
            return new WebThemeColorSnapshot(
                ParseOrFallback(cfg?.Background, FallbackBackground),
                ParseOrFallback(cfg?.GroupBackground, FallbackGroupBackground),
                ParseOrFallback(cfg?.Border, FallbackBorder),
                ParseOrFallback(cfg?.TextPrimary, FallbackTextPrimary),
                ParseOrFallback(cfg?.TextSecondary, FallbackTextSecondary),
                ParseOrFallback(cfg?.ValueSafe, FallbackValueSafe),
                ParseOrFallback(cfg?.ValueWarn, FallbackValueWarn),
                ParseOrFallback(cfg?.ValueCrit, FallbackValueCrit));
        }

        // ============================================================
        // 颜色解析（严格；非法即失败，绝不原文回传）
        // ============================================================

        /// <summary>
        /// 严格解析主题里的颜色字符串。
        ///
        /// ★ 接受的写法（与主题模型 <c>ThemeManager.ParseColor</c> 的语义一致）：
        ///   - <c>#RRGGBB</c>      → 不透明（alpha = 255）
        ///   - <c>#AARRGGBB</c>    → **ARGB**（alpha 在前）
        ///   - <c>rgba(r,g,b,a)</c> → r/g/b 为 0–255 整数，a 为 0–1
        ///   - 已知颜色名（如 <c>Red</c>）
        ///
        /// ★ 拒绝的写法：空串 / 非法 hex / 位数不对 / 越界数值 / NaN / Infinity /
        ///   未知颜色名 —— 一律返回 false，由调用方改用**写死的回退色**。
        ///   原始字符串**绝不**被回传，因此不可能成为 CSS 注入内容。
        /// </summary>
        /// <param name="raw">主题里的原始颜色字符串（可空）。</param>
        /// <param name="color">解析成功时的颜色；失败时为 <see cref="Color.Empty"/>。</param>
        /// <returns>是否解析成功。</returns>
        public static bool TryParseThemeColor(string? raw, out Color color)
        {
            color = Color.Empty;
            if (raw == null) return false;

            string s = raw.Trim();
            if (s.Length == 0) return false;

            if (s[0] == '#')
            {
                return TryParseHexColor(s, out color);
            }

            if (s.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) && s.EndsWith(")", StringComparison.Ordinal))
            {
                return TryParseRgbaColor(s, out color);
            }

            // 颜色名：只接受 .NET 已知颜色（IsKnownColor），
            // 未知名字 → 非法（回退色），不会变成"透明黑"这种静默错误。
            Color named;
            try
            {
                named = Color.FromName(s);
            }
            catch
            {
                return false;
            }
            if (!named.IsKnownColor) return false;

            color = named;
            return true;
        }

        /// <summary>
        /// 解析 <c>#RRGGBB</c> / <c>#AARRGGBB</c>。
        ///
        /// ★ 8 位时按 **ARGB** 拆解（alpha 是第一对），
        ///   绝不能当成 CSS 的 <c>#RRGGBBAA</c>（alpha 在最后）——
        ///   主题的写法与 CSS 的写法在这里语义不同，混用会把颜色整个搞错。
        /// </summary>
        private static bool TryParseHexColor(string s, out Color color)
        {
            color = Color.Empty;
            ReadOnlySpan<char> hex = s.AsSpan(1);
            if (hex.Length != 6 && hex.Length != 8) return false;

            Span<int> parts = stackalloc int[4];   // [0]=a, [1]=r, [2]=g, [3]=b
            for (int i = 0; i < hex.Length; i += 2)
            {
                if (!TryParseHexByte(hex.Slice(i, 2), out int v)) return false;
                parts[i / 2] = v;
            }

            if (hex.Length == 6)
            {
                color = Color.FromArgb(255, parts[0], parts[1], parts[2]);
            }
            else
            {
                color = Color.FromArgb(parts[0], parts[1], parts[2], parts[3]);
            }
            return true;
        }

        /// <summary>解析两位十六进制数（0–255）。</summary>
        private static bool TryParseHexByte(ReadOnlySpan<char> two, out int value)
        {
            value = 0;
            if (two.Length != 2) return false;
            int hi = HexValue(two[0]);
            int lo = HexValue(two[1]);
            if (hi < 0 || lo < 0) return false;
            value = (hi << 4) | lo;
            return true;
        }

        /// <summary>十六进制字符 → 数值（非法返回 -1）。</summary>
        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>
        /// 解析 <c>rgba(r,g,b,a)</c>：r/g/b 为 0–255 整数，a 为 0–1（1 = 不透明）。
        /// 越界 / NaN / Infinity 一律拒绝；alpha×255 按 AwayFromZero 舍入。
        /// </summary>
        private static bool TryParseRgbaColor(string s, out Color color)
        {
            color = Color.Empty;

            int open = s.IndexOf('(');
            int close = s.LastIndexOf(')');
            if (open < 0 || close <= open) return false;

            string body = s.Substring(open + 1, close - open - 1);
            string[] parts = body.Split(',');
            if (parts.Length != 4) return false;

            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ||
                !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int g) ||
                !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int b))
            {
                return false;
            }
            if (r < 0 || r > 255 || g < 0 || g > 255 || b < 0 || b > 255) return false;

            if (!double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double a)) return false;
            if (double.IsNaN(a) || double.IsInfinity(a)) return false;
            if (a < 0d || a > 1d) return false;

            int alpha = (int)Math.Round(a * 255d, MidpointRounding.AwayFromZero);
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;

            color = Color.FromArgb(alpha, r, g, b);
            return true;
        }

        /// <summary>解析失败就用回退色（**不会**把原始字符串带出去）。</summary>
        private static Color ParseOrFallback(string? raw, Color fallback)
        {
            return TryParseThemeColor(raw, out Color c) ? c : fallback;
        }
    }

    /// <summary>
    /// Web 端 CSS 生成（**消费方**：只读快照，不解析主题）。
    ///
    /// ★ 输出规则（v4 §7.3）：
    ///   - 颜色统一输出为 <c>rgba(r,g,b,a)</c>：r/g/b 是 0–255 整数，a 是 0–1 小数；
    ///   - 小数一律用 <see cref="CultureInfo.InvariantCulture"/>（不受系统区域设置影响）；
    ///   - 主题里的 <c>#AARRGGBB</c> 在快照阶段就按 ARGB 拆成了 Color，
    ///     这里**不会**把它当成 CSS 的 <c>#RRGGBBAA</c>；
    ///   - 生成的文本只可能包含固定标识符、整数与 <c>rgba(...)</c> 字面量，
    ///     主题原文与任何用户输入都**不可能**出现在这里（→ 无注入面）。
    /// </summary>
    public static class WebThemeCss
    {
        /// <summary>
        /// 生成注入到网页 <c>&lt;style&gt;</c> 里的 CSS 变量块（<c>:root { ... }</c>）。
        /// </summary>
        /// <param name="snapshot">UI 层发布的颜色快照；null = 未发布 → 用全回退配色。</param>
        public static string BuildCssVariables(WebThemeColorSnapshot? snapshot)
        {
            WebThemeColorSnapshot s = snapshot ?? WebThemeColorSnapshot.Default;

            StringBuilder sb = new StringBuilder(320);
            sb.Append(":root {\n");
            AppendVariable(sb, "--bg", s.Background);
            AppendVariable(sb, "--card", s.GroupBackground);
            AppendVariable(sb, "--border", s.Border);
            AppendVariable(sb, "--text-main", s.TextPrimary);
            AppendVariable(sb, "--text-sub", s.TextSecondary);
            AppendVariable(sb, "--c-0", s.ValueSafe);
            AppendVariable(sb, "--c-1", s.ValueWarn);
            AppendVariable(sb, "--c-2", s.ValueCrit);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 把一个 Color 规范化成 CSS 颜色字面量：<c>rgba(r,g,b,a)</c>。
        ///
        /// ★ a 是 0–1 的小数（1 = 不透明），最多 3 位小数（去掉多余的 0）；
        ///   小数点用 invariant culture → 不受系统区域设置影响。
        /// </summary>
        public static string ToCssColor(Color color)
        {
            double alpha = color.A / 255d;

            StringBuilder sb = new StringBuilder(24);
            sb.Append("rgba(");
            sb.Append(color.R.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(color.G.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(color.B.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(alpha.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(')');
            return sb.ToString();
        }

        /// <summary>追加一条 CSS 自定义属性。</summary>
        private static void AppendVariable(StringBuilder sb, string name, Color color)
        {
            sb.Append("    ");
            sb.Append(name);
            sb.Append(": ");
            sb.Append(ToCssColor(color));
            sb.Append(";\n");
        }
    }

    /// <summary>
    /// 主题颜色快照的**发布点**（UI 层 → Web 层）。
    ///
    /// ★ 为什么是一个静态发布点：
    ///   Web 会话管理器由 <c>LiteWebServer</c> 创建，
    ///   而发布方是 UI 层（换肤时才知道新颜色）——两边没有天然的实例引用关系。
    ///   用一个静态发布点，UI 层换肤时调一次 <see cref="Publish"/> 即可，
    ///   Web 侧只在**生成 HTML 的那一刻**读一次当前快照。
    ///
    /// ★ 语义（刻意取舍，见 A11 结果文档）：
    ///   - **不推送**：快照不会进入 WebSocket 广播载荷；
    ///   - **不轮询**：没有任何定时器 / 颜色轮询；
    ///   - 换主题后**刷新浏览器**即可拿到新配色。
    /// </summary>
    public static class WebThemeColorPublisher
    {
        private static WebThemeColorSnapshot? _current;

        /// <summary>当前已发布的快照（未发布时为 null）。</summary>
        public static WebThemeColorSnapshot Current => Volatile.Read(ref _current);

        /// <summary>发布一份新的快照（UI 层换肤时调用）。传 null = 撤销发布。</summary>
        public static void Publish(WebThemeColorSnapshot? snapshot)
        {
            Volatile.Write(ref _current, snapshot);
        }

        /// <summary>从主题发布快照（UI 层最常用的一行接法）。</summary>
        public static void PublishFromTheme(Theme? theme)
        {
            Publish(WebThemeColorSnapshot.FromTheme(theme));
        }

        /// <summary>撤销发布（回到全回退配色）。</summary>
        public static void Clear()
        {
            Volatile.Write(ref _current, null);
        }
    }
}
