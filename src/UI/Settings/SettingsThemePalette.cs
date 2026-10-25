using System;
using System.Drawing;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.SettingsPage
{
    /// <summary>
    /// 设置窗体的<b>统一配色</b>（F14b 第 1 步）。
    ///
    /// <para>★ 为什么必须有这么一份：修复前 <see cref="LiteMonitor.src.UI.Controls.UIColors"/> 里的 12 个色值是
    ///   <c>public static</c> 字段、且只写死了一套浅色 —— <c>LiteUI.cs:9-25</c>（F00 核验未漂移）。
    ///   于是"暗色"没有任何落脚点：即使把背景改成深色，<c>LiteUnderlineInput</c> /
    ///   <c>LiteComboBox</c> / <c>LiteCard</c> 各处还散着 <c>Color.White</c>、<c>Color.Gray</c>、
    ///   <c>Color.LightGray</c> 等硬编码，结果一定是"页面变黑了、输入框还是白的"这种半吊子。</para>
    ///
    /// <para>★ 因此这里把设置控件需要的色<b>全部</b>列出来（含禁用/选中/错误/提示/菜单/预览这些边界状态），
    ///   浅色与深色两组共用同一套取值 ⇒ 任何控件想沿用旧的硬编码都无处可躲。</para>
    /// </summary>
    public sealed class SettingsThemePalette
    {
        private SettingsThemePalette(
            string name, bool isDark,
            Color mainBg, Color sidebarBg, Color cardBg, Color surfaceBg, Color border,
            Color primary, Color primaryHover, Color textMain, Color textSub, Color textDisabled,
            Color groupHeader, Color navSelected, Color navHover, Color separator,
            Color textWarn, Color textCrit, Color hint,
            Color inputBg, Color inputText, Color inputLine, Color inputLineFocused,
            Color menuBg, Color menuText, Color menuSelectedBg, Color menuSelectedText,
            Color previewBg, Color bannerBg, Color bannerText)
        {
            Name = name;
            IsDark = isDark;
            MainBg = mainBg;
            SidebarBg = sidebarBg;
            CardBg = cardBg;
            SurfaceBg = surfaceBg;
            Border = border;
            Primary = primary;
            PrimaryHover = primaryHover;
            TextMain = textMain;
            TextSub = textSub;
            TextDisabled = textDisabled;
            GroupHeader = groupHeader;
            NavSelected = navSelected;
            NavHover = navHover;
            Separator = separator;
            TextWarn = textWarn;
            TextCrit = textCrit;
            Hint = hint;
            InputBg = inputBg;
            InputText = inputText;
            InputLine = inputLine;
            InputLineFocused = inputLineFocused;
            MenuBg = menuBg;
            MenuText = menuText;
            MenuSelectedBg = menuSelectedBg;
            MenuSelectedText = menuSelectedText;
            PreviewBg = previewBg;
            BannerBg = bannerBg;
            BannerText = bannerText;
        }

        /// <summary>配色名（"浅色" / "深色"）。诊断与围栏用。</summary>
        public string Name { get; }

        /// <summary>是否深色。</summary>
        public bool IsDark { get; }

        /// <summary>页面底色。</summary>
        public Color MainBg { get; }

        /// <summary>左侧导航栏底色。</summary>
        public Color SidebarBg { get; }

        /// <summary>卡片底色。</summary>
        public Color CardBg { get; }

        /// <summary>卡片/面板的次级面（表格内的滚动容器、辅助面板等）。</summary>
        public Color SurfaceBg { get; }

        /// <summary>边框。</summary>
        public Color Border { get; }

        /// <summary>强调色（主按钮、选中指示条、链接）。</summary>
        public Color Primary { get; }

        /// <summary>强调色的悬停态。</summary>
        public Color PrimaryHover { get; }

        /// <summary>主文字。</summary>
        public Color TextMain { get; }

        /// <summary>次要文字（标签、单位、说明）。</summary>
        public Color TextSub { get; }

        /// <summary>禁用文字 —— ★ 边界状态：必须还能读得清，但不能看起来像可执行。</summary>
        public Color TextDisabled { get; }

        /// <summary>分组标题栏底色。</summary>
        public Color GroupHeader { get; }

        /// <summary>导航选中态底色 —— ★ 边界状态。</summary>
        public Color NavSelected { get; }

        /// <summary>导航悬停态底色 —— ★ 边界状态。</summary>
        public Color NavHover { get; }

        /// <summary>行分隔线（比 <see cref="Border"/> 更淡）。</summary>
        public Color Separator { get; }

        /// <summary>提示/警告文字（阈值钳制的行内提示、阈值输入框的默认色）。</summary>
        public Color TextWarn { get; }

        /// <summary>错误/严重文字 —— ★ 边界状态。</summary>
        public Color TextCrit { get; }

        /// <summary>说明行（<c>LiteHintRow</c> / <c>LiteNote</c>）文字。</summary>
        public Color Hint { get; }

        /// <summary>文本输入框底色 —— ★ 修复前是硬编码 <c>Color.White</c>。</summary>
        public Color InputBg { get; }

        /// <summary>文本输入框文字色。</summary>
        public Color InputText { get; }

        /// <summary>输入框下划线（未聚焦）。</summary>
        public Color InputLine { get; }

        /// <summary>输入框下划线（聚焦）。</summary>
        public Color InputLineFocused { get; }

        /// <summary>菜单/下拉列表底色 ★ 边界状态：下拉框展开后的列表。</summary>
        public Color MenuBg { get; }

        /// <summary>菜单文字。</summary>
        public Color MenuText { get; }

        /// <summary>菜单选中项底色 ★ 边界状态。</summary>
        public Color MenuSelectedBg { get; }

        /// <summary>菜单选中项文字 ★ 边界状态。</summary>
        public Color MenuSelectedText { get; }

        /// <summary>主题预览画布底色 ★ 边界状态。</summary>
        public Color PreviewBg { get; }

        /// <summary>告警横幅底色 ★ 边界状态。</summary>
        public Color BannerBg { get; }

        /// <summary>告警横幅文字（在 <see cref="BannerBg"/> 之上必须读得清）。</summary>
        public Color BannerText { get; }

        /// <summary>浅色配色（取值沿用修复前 <c>UIColors</c> 的观感 ⇒ 现有用户的浅色体验不变）。</summary>
        public static SettingsThemePalette Light { get; } = new SettingsThemePalette(
            name: "浅色",
            isDark: false,
            mainBg: Color.FromArgb(243, 243, 243),
            sidebarBg: Color.FromArgb(240, 240, 240),
            // ★★ 四个"容器类"底色原先**全是 Color.White** —— 这在浅色下观感没问题，
            //   但让"按色值反查语义槽位"的重映射（SettingsThemeApplier.Remap）彻底失效：
            //   白色同时命中 CardBg / SurfaceBg / InputBg / MenuBg，白底一律被映射成
            //   排在数组最前的 CardBg ⇒ 输入框会被刷成卡片色（反之亦然）。
            //   ⇒ 这里给它们各差 1～2 级的**互不相同**的接近白值：
            //     观感仍是白（亮度 0.94~1.00，肉眼无法分辨），但重映射不再有歧义。
            //   ★ 这是根因修复；"调 Remap 的数组顺序"只是换个槽位继续串，不是修。
            cardBg: Color.FromArgb(255, 255, 255),
            surfaceBg: Color.FromArgb(252, 252, 252),
            border: Color.FromArgb(220, 220, 220),
            primary: Color.FromArgb(0, 120, 215),
            primaryHover: Color.FromArgb(0, 100, 190),
            textMain: Color.FromArgb(32, 32, 32),
            textSub: Color.FromArgb(90, 90, 90),
            textDisabled: Color.FromArgb(150, 150, 150),
            groupHeader: Color.FromArgb(248, 249, 250),
            navSelected: Color.FromArgb(230, 230, 230),
            navHover: Color.FromArgb(235, 235, 235),
            separator: Color.FromArgb(240, 240, 240),
            // ★ TextWarn 取 191,128,0（而非更亮的 215,145,0）：后者在白卡片上对比度只有 2.64，
            //   低于 WCAG 正文门槛 3.0 —— 阈值钳制的行内提示会看着"发虚"。
            textWarn: Color.FromArgb(191, 128, 0),
            textCrit: Color.FromArgb(200, 40, 40),
            hint: Color.Gray,
            inputBg: Color.FromArgb(249, 249, 249),
            // ★ 三个"正文字色"原先全是 32,32,32 ⇒ 重映射同样会串（输入框的 32 可能被当成正文的 32）。
            //   各差 1 级（32 / 34 / 36）：肉眼完全看不出差别，但重映射不再有歧义。
            inputText: Color.FromArgb(34, 34, 34),
            inputLine: Color.LightGray,
            inputLineFocused: Color.FromArgb(0, 120, 215),
            menuBg: Color.FromArgb(254, 254, 254),
            menuText: Color.FromArgb(36, 36, 36),
            menuSelectedBg: Color.FromArgb(0, 90, 158),
            menuSelectedText: Color.White,
            previewBg: Color.FromArgb(246, 246, 246),
            bannerBg: Color.FromArgb(255, 248, 230),
            // ★ 横幅文字在米黄底上需要更深才有 4.5+（原 215,145,0 只有 2.5，等于看不清）。
            bannerText: Color.FromArgb(150, 100, 0));

        /// <summary>
        /// 深色配色（F14b 的新增项；修复前不存在这套色）。
        /// <para>★ 每组取值都按"在深底上真的读得清"来挑，而不是把浅色简单反相 ——
        ///   反相出来的纯黑底配纯白字对比度过头，长时间看屏幕很累。</para>
        /// </summary>
        public static SettingsThemePalette Dark { get; } = new SettingsThemePalette(
            name: "深色",
            isDark: true,
            mainBg: Color.FromArgb(30, 30, 30),
            sidebarBg: Color.FromArgb(24, 24, 24),
            cardBg: Color.FromArgb(37, 37, 38),
            surfaceBg: Color.FromArgb(45, 45, 48),
            border: Color.FromArgb(63, 63, 66),
            primary: Color.FromArgb(78, 172, 245),
            primaryHover: Color.FromArgb(120, 195, 255),
            textMain: Color.FromArgb(230, 230, 230),
            textSub: Color.FromArgb(160, 160, 165),
            // ★ 禁用文字必须还能读清：相对亮度 ≥ 0.35（可读下限），同时明显弱于正文（230）。
            //   120 → 168 的理由：原值相对亮度仅 0.1891，在深底（#252526）上几乎糊成背景色，
            //   "禁用"不等于"看不见" —— 禁用态要传达"不可操作"，而非"不存在"。
            //   168 的亮度 0.3935 达标，且比 Hint(170) 略暗、比 TextSub(160) 略亮，层级不与它们撞。
            textDisabled: Color.FromArgb(168, 168, 173),
            groupHeader: Color.FromArgb(47, 47, 50),
            navSelected: Color.FromArgb(56, 56, 60),
            navHover: Color.FromArgb(48, 48, 52),
            separator: Color.FromArgb(52, 52, 56),
            textWarn: Color.FromArgb(232, 178, 68),
            textCrit: Color.FromArgb(240, 106, 106),
            // ★ 说明文字提到 170：原先的 150 在深底上对比度不足，说明行会变成看不清的灰边。
            hint: Color.FromArgb(170, 170, 175),
            inputBg: Color.FromArgb(28, 28, 30),
            inputText: Color.FromArgb(232, 232, 232),
            inputLine: Color.FromArgb(88, 88, 92),
            inputLineFocused: Color.FromArgb(78, 172, 245),
            // ★ 与浅色侧同理：深色侧的容器类底色也**不能两两同值**，否则反向重映射
            //   （深色 → 浅色）会在这些槽位间串。取值仍落在同一档灰度，观感不变。
            menuBg: Color.FromArgb(39, 39, 41),
            menuText: Color.FromArgb(234, 234, 234),
            // ★ 深色侧的选中项底色必须**与浅色侧不同**：两侧若同为一个值，
            //   「深浅两套配色」对这个槽位就等于没区分。浅侧取 0,90,158，
            //   深侧再亮一档（在深/mid 底上都清楚）。
            menuSelectedBg: Color.FromArgb(0, 106, 186),
            menuSelectedText: Color.White,
            previewBg: Color.FromArgb(26, 26, 26),
            bannerBg: Color.FromArgb(72, 58, 18),
            bannerText: Color.FromArgb(232, 178, 68));

        /// <summary>空值防御：null 一律落到浅色（不当成第三种状态）。</summary>
        public static SettingsThemePalette From(SettingThemeMode mode)
        {
            switch (mode)
            {
                case SettingThemeMode.Dark: return Dark;
                case SettingThemeMode.Light: return Light;
                default: return Light;
            }
        }

        /// <summary>按槽位名取色。</summary>
        public Color Get(string role)
        {
            switch (role)
            {
                case nameof(MainBg): return MainBg;
                case nameof(SidebarBg): return SidebarBg;
                case nameof(CardBg): return CardBg;
                case nameof(SurfaceBg): return SurfaceBg;
                case nameof(Border): return Border;
                case nameof(Primary): return Primary;
                case nameof(PrimaryHover): return PrimaryHover;
                case nameof(TextMain): return TextMain;
                case nameof(TextSub): return TextSub;
                case nameof(TextDisabled): return TextDisabled;
                case nameof(GroupHeader): return GroupHeader;
                case nameof(NavSelected): return NavSelected;
                case nameof(NavHover): return NavHover;
                case nameof(Separator): return Separator;
                case nameof(TextWarn): return TextWarn;
                case nameof(TextCrit): return TextCrit;
                case nameof(Hint): return Hint;
                case nameof(InputBg): return InputBg;
                case nameof(InputText): return InputText;
                case nameof(InputLine): return InputLine;
                case nameof(InputLineFocused): return InputLineFocused;
                case nameof(MenuBg): return MenuBg;
                case nameof(MenuText): return MenuText;
                case nameof(MenuSelectedBg): return MenuSelectedBg;
                case nameof(MenuSelectedText): return MenuSelectedText;
                case nameof(PreviewBg): return PreviewBg;
                case nameof(BannerBg): return BannerBg;
                case nameof(BannerText): return BannerText;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role), role, "未知的主题槽位");
            }
        }

        /// <summary>
        /// 全部槽位（角色名 + 色值）。
        /// <para>★ 存在的理由：让"是不是每个槽位都真的在深/浅两侧分开"变成一条可断言的事实，
        ///   而不是靠肉眼比对两份调色板 —— 新增槽位时忘了配深色的一半，这里立刻红。</para>
        /// </summary>
        public static IReadOnlyList<(string Role, Color Color)> AllSlots(SettingsThemePalette palette)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));

            return new[]
            {
                (nameof(MainBg), palette.MainBg),
                (nameof(SidebarBg), palette.SidebarBg),
                (nameof(CardBg), palette.CardBg),
                (nameof(SurfaceBg), palette.SurfaceBg),
                (nameof(Border), palette.Border),
                (nameof(Primary), palette.Primary),
                (nameof(PrimaryHover), palette.PrimaryHover),
                (nameof(TextMain), palette.TextMain),
                (nameof(TextSub), palette.TextSub),
                (nameof(TextDisabled), palette.TextDisabled),
                (nameof(GroupHeader), palette.GroupHeader),
                (nameof(NavSelected), palette.NavSelected),
                (nameof(NavHover), palette.NavHover),
                (nameof(Separator), palette.Separator),
                (nameof(TextWarn), palette.TextWarn),
                (nameof(TextCrit), palette.TextCrit),
                (nameof(Hint), palette.Hint),
                (nameof(InputBg), palette.InputBg),
                (nameof(InputText), palette.InputText),
                (nameof(InputLine), palette.InputLine),
                (nameof(InputLineFocused), palette.InputLineFocused),
                (nameof(MenuBg), palette.MenuBg),
                (nameof(MenuText), palette.MenuText),
                (nameof(MenuSelectedBg), palette.MenuSelectedBg),
                (nameof(MenuSelectedText), palette.MenuSelectedText),
                (nameof(PreviewBg), palette.PreviewBg),
                (nameof(BannerBg), palette.BannerBg),
                (nameof(BannerText), palette.BannerText),
            };
        }

        /// <summary>按是否深色取对应的那一份。</summary>
        public static SettingsThemePalette FromDarkFlag(bool isDark) => isDark ? Dark : Light;

        /// <summary>
        /// sRGB 相对亮度（0=黑，1=白）。
        /// <para>★ 防串味断言用：<c>MainBg</c> / <c>CardBg</c> 必须落在各自那一侧，
        ///   而 <c>TextMain</c> 必须落在另一侧 —— 只比较相对关系，不写死具体 RGB。</para>
        /// </summary>
        public static double Luminance(Color color)
        {
            return (0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B));
        }

        private static double Channel(byte raw)
        {
            double c = raw / 255.0;
            // sRGB 相对亮度：低于 0.03928 走线性段，其余走 gamma 段（与 WCAG 的定义一致）。
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        /// <summary>
        /// WCAG 对比度（1.0 ~ 21.0）。
        /// <para>★ 用于"底色上的文字真的读得清"的可验证断言 ——
        ///   这是"只改了页面背景色"与"真的做了暗色"的分水岭。</para>
        /// </summary>
        public static double Contrast(Color foreground, Color background)
        {
            double a = Luminance(foreground);
            double b = Luminance(background);
            double hi = Math.Max(a, b);
            double lo = Math.Min(a, b);
            return (hi + 0.05) / (lo + 0.05);
        }

        public override string ToString() => Name;
    }
}
