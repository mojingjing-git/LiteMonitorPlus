namespace LiteMonitor.AppearanceThemeCheck
{
    /// <summary>
    /// 一个待校验主题的"期望参数表"。
    ///
    /// ★ 这张表就是 A12 交付的参数说明文档的数据源：
    ///   程序用它校验磁盘上的主题文件，文档用它说明每个参数的取值与含义。
    ///   两边必须同步修改，否则校验会失败（这是刻意设计，防止文档与实现漂移）。
    /// </summary>
    internal sealed class ThemeSpec
    {
        /// <summary>主题名（同时是文件名，不含扩展名）。</summary>
        public string Name = string.Empty;

        /// <summary>相对仓库根目录的路径。</summary>
        public string RelativePath = string.Empty;

        /// <summary>期望的主题代次。</summary>
        public int Version;

        /// <summary>期望的外框样式。</summary>
        public int FrameStyle;

        /// <summary>期望的描边宽度（96 DPI 逻辑值）。</summary>
        public int FrameWidth;

        /// <summary>期望的切角边长（96 DPI 逻辑值）。</summary>
        public int FrameLength;

        /// <summary>期望的扫描线强度。</summary>
        public int Scanline;

        /// <summary>期望的辉光强度。</summary>
        public int Glow;

        /// <summary>期望的网格强度。</summary>
        public int Grid;

        /// <summary>期望的进度条样式。</summary>
        public int BarStyle;

        /// <summary>期望的背景模式（直接写字符串，不引用 internal 的模式名常量）。</summary>
        public string BackdropMode = string.Empty;

        /// <summary>期望的背景染色系数。</summary>
        public double BackdropOpacity;

        /// <summary>期望的染色色原始字符串。</summary>
        public string Tint = string.Empty;

        /// <summary>期望的渐变起始色原始字符串。</summary>
        public string GradientStart = string.Empty;

        /// <summary>期望的渐变结束色原始字符串。</summary>
        public string GradientEnd = string.Empty;

        /// <summary>期望的渐变角度。</summary>
        public double GradientAngle;

        /// <summary>期望的噪点强度。</summary>
        public int Noise;

        /// <summary>期望的边缘辉光强度。</summary>
        public int EdgeGlow;

        /// <summary>期望的颜色表：键为 ColorConfig 的属性名，值为颜色原始字符串。</summary>
        public Dictionary<string, string> Colors = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>构造一份主题期望表。</summary>
        public static ThemeSpec Create(
            string name, string relativePath, int version,
            int frameStyle, int frameWidth, int frameLength,
            int scanline, int glow, int grid, int barStyle,
            string backdropMode, double backdropOpacity,
            string tint, string gradientStart, string gradientEnd, double gradientAngle,
            int noise, int edgeGlow,
            params (string Key, string Value)[] colors)
        {
            ThemeSpec spec = new ThemeSpec
            {
                Name = name,
                RelativePath = relativePath,
                Version = version,
                FrameStyle = frameStyle,
                FrameWidth = frameWidth,
                FrameLength = frameLength,
                Scanline = scanline,
                Glow = glow,
                Grid = grid,
                BarStyle = barStyle,
                BackdropMode = backdropMode,
                BackdropOpacity = backdropOpacity,
                Tint = tint,
                GradientStart = gradientStart,
                GradientEnd = gradientEnd,
                GradientAngle = gradientAngle,
                Noise = noise,
                EdgeGlow = edgeGlow,
            };
            foreach ((string key, string value) in colors)
                spec.Colors[key] = value;
            return spec;
        }
    }

    /// <summary>
    /// A12 的全部期望参数表：主主题 1 个 + 可读性夹具 2 个。
    /// </summary>
    internal static class ThemeSpecs
    {
        /// <summary>主主题：随程序发布到 resources/themes，用户可见、可切换。</summary>
        public static readonly ThemeSpec Main = ThemeSpec.Create(
            name: "CyberPunk_Mech",
            relativePath: "resources/themes/CyberPunk_Mech.json",
            version: 4,
            frameStyle: 1, frameWidth: 3, frameLength: 14,
            scanline: 30, glow: 55, grid: 20, barStyle: 1,
            backdropMode: "gradient", backdropOpacity: 0.0,
            tint: "rgba(0,229,255,0.25)",
            gradientStart: "#0A1020", gradientEnd: "#140A1E", gradientAngle: 135,
            noise: 0, edgeGlow: 0,
            ("Background", "#070A0F"),
            ("GroupBackground", "#1A2230"),
            ("Border", "#5CF3FF"),
            ("Shadow", "rgba(0,229,255,0.35)"),
            ("TextTitle", "#5CF3FF"),
            ("TextGroup", "#A6BED4"),
            ("TextPrimary", "#E9F1F8"),
            ("TextSecondary", "#A9BDD0"),
            ("ValueSafe", "#3AE08D"),
            ("ValueWarn", "#FFC24B"),
            ("ValueCrit", "#FF5C7A"),
            ("BarBackground", "#0E1520"),
            ("BarLow", "#5CF3FF"),
            ("BarMid", "#FFC24B"),
            ("BarHigh", "#FF5C7A"));

        /// <summary>亮底夹具：验证同一套参数在浅背景下的可读性。</summary>
        public static readonly ThemeSpec Light = ThemeSpec.Create(
            name: "CyberPunk_Mech_Light",
            relativePath: "tests/AppearanceThemeCheck/fixtures/CyberPunk_Mech_Light.json",
            version: 4,
            frameStyle: 1, frameWidth: 3, frameLength: 14,
            scanline: 30, glow: 55, grid: 20, barStyle: 1,
            backdropMode: "gradient", backdropOpacity: 0.0,
            tint: "rgba(28,110,138,0.25)",
            gradientStart: "#E0E8F1", gradientEnd: "#DCE6F0", gradientAngle: 135,
            noise: 0, edgeGlow: 0,
            ("Background", "#E0E8F1"),
            ("GroupBackground", "#FFFFFF"),
            ("Border", "#1C6E8A"),
            ("Shadow", "rgba(28,110,138,0.28)"),
            ("TextTitle", "#0B4A63"),
            ("TextGroup", "#33475B"),
            ("TextPrimary", "#16202B"),
            ("TextSecondary", "#43566A"),
            ("ValueSafe", "#0A6E3C"),
            ("ValueWarn", "#7A4E00"),
            ("ValueCrit", "#A8102C"),
            ("BarBackground", "#D3DCE6"),
            ("BarLow", "#0B6E8C"),
            ("BarMid", "#8A5500"),
            ("BarHigh", "#A8102C"));

        /// <summary>复杂背景夹具：渐变 + 染色 + 噪点 + 边缘辉光同时开启。</summary>
        public static readonly ThemeSpec Complex = ThemeSpec.Create(
            name: "CyberPunk_Mech_Complex",
            relativePath: "tests/AppearanceThemeCheck/fixtures/CyberPunk_Mech_Complex.json",
            version: 4,
            frameStyle: 1, frameWidth: 3, frameLength: 14,
            scanline: 30, glow: 55, grid: 20, barStyle: 1,
            backdropMode: "gradient", backdropOpacity: 0.35,
            tint: "rgba(255,46,151,0.25)",
            gradientStart: "#0A1830", gradientEnd: "#2A0A24", gradientAngle: 135,
            noise: 22, edgeGlow: 45,
            ("Background", "#06080D"),
            ("GroupBackground", "#18202E"),
            ("Border", "#7DF9FF"),
            ("Shadow", "rgba(255,46,151,0.30)"),
            ("TextTitle", "#7DF9FF"),
            ("TextGroup", "#B9D2E8"),
            ("TextPrimary", "#E9F1F8"),
            ("TextSecondary", "#A9BDD0"),
            ("ValueSafe", "#3AE08D"),
            ("ValueWarn", "#FFC24B"),
            ("ValueCrit", "#FF6B85"),
            ("BarBackground", "#0E1520"),
            ("BarLow", "#7DF9FF"),
            ("BarMid", "#FFC24B"),
            ("BarHigh", "#FF6B85"));

        /// <summary>
        /// 液态玻璃主题（A16 产出）：请求**实时玻璃**背景。
        /// <para>
        /// ★ 与其它两个夹具不同，它是**随程序发布**的正式主题（不是夹具），
        ///   因此要接受与主主题同等的全部校验：参数、归一化、背景规则、
        ///   Load→Save→Load 往返、可读性判据。
        /// </para>
        /// <para>
        /// ★ 背景模式是 <c>glass</c>：真实帧不可用时按 glass → gradient → solid 降级，
        ///   降级后的渐变两端固定 ⇒ 画面不会突然变成另一种配色。
        /// </para>
        /// <para>
        /// ★ 第二轮实机反馈后改为**深色磨砂玻璃**：浅色玻璃 + 厚纱罩在物理上
        ///   等于"磨砂亚克力"，且把真实背景洗到只剩 35%。深底 + 亮字 + 低 alpha
        ///   深色纱罩才是经典 Mica/Acrylic 形态。
        /// <para>
        /// ★ <c>frameStyle = 2</c>（圆角描边）：液态玻璃的轮廓特征是**圆角**，
        ///   切角（1）是赛博朋克语言，不是玻璃语言。
        /// </para>
        /// </summary>
        public static readonly ThemeSpec LiquidGlass = ThemeSpec.Create(
            name: "LiquidGlass",
            relativePath: "resources/themes/LiquidGlass.json",
            version: 4,
            frameStyle: 2, frameWidth: 3, frameLength: 14,
            scanline: 18, glow: 55, grid: 10, barStyle: 1,
            backdropMode: "glass", backdropOpacity: 0.72,
            tint: "rgba(9,17,30,1.0)",
            gradientStart: "#1B2C44", gradientEnd: "#0A1220", gradientAngle: 90,
            noise: 0, edgeGlow: 0,
            ("Background", "#0A1220"),
            ("GroupBackground", "rgba(22,42,66,0.86)"),
            ("Border", "#7FD4FF"),
            ("Shadow", "rgba(0,0,0,0.45)"),
            ("TextTitle", "#C7ECFF"),
            ("TextGroup", "#A6C8E0"),
            ("TextPrimary", "#EAF4FC"),
            ("TextSecondary", "#9FB9CE"),
            ("ValueSafe", "#4ADE80"),
            ("ValueWarn", "#FBBF24"),
            ("ValueCrit", "#F87171"),
            ("BarBackground", "#16283C"),
            ("BarLow", "#4ADE80"),
            ("BarMid", "#FBBF24"),
            ("BarHigh", "#F87171"));

        /// <summary>全部待校验主题。</summary>
        public static ThemeSpec[] All() => new[] { Main, Light, Complex, LiquidGlass };
    }
}
