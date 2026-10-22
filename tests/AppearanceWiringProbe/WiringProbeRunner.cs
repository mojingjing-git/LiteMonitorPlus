using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;
using LiteMonitor.src.UI.Helpers;

namespace LiteMonitor.AppearanceWiringProbe
{
    /// <summary>
    /// A13 接线自证探针的用例执行器。
    ///
    /// ★ 核心断言（对应 v4 §7.2「关闭新能力时像素应一致」）：
    /// 1. <b>旧主题</b>：走**新入口**（带外观运行时）与走**旧入口**的画面<b>逐像素一致</b>
    ///    —— 证明 A13 接线没有改变任何旧主题的观感。
    /// 2. <b>机能主题</b>：新入口与旧入口<b>必须有差异</b>，且背景层必须真的接管底板
    ///    —— 证明接线不是"接了但没生效"。
    /// 3. <b>接线不变量</b>：旧主题的外观快照里所有新开关都是关的；快照在参数不变时复用、
    ///    参数变化时重建；换肤时代次自增。
    /// 4. <b>窗口轮廓接线</b>：切角主题必须传 DONOTROUND(1)，旧主题必须回到 ROUND(2)。
    /// </summary>
    internal sealed class WiringProbeRunner
    {
        /// <summary>竖屏缩放档位（覆盖 96 / 120 / 144 / 192 DPI 与 1× / 2× 用户缩放）。</summary>
        private static readonly (int Dpi, float UserScale)[] PortraitScales =
        {
            (96, 1.0f),
            (120, 1.0f),
            (144, 2.0f),
            (192, 2.0f)
        };

        /// <summary>横屏缩放档位。</summary>
        private static readonly (int Dpi, float UserScale)[] HorizontalScales =
        {
            (96, 1.0f),
            (192, 2.0f)
        };

        /// <summary>已执行的用例。</summary>
        public List<ProbeCase> Cases { get; } = new();

        /// <summary>
        /// 像素证据（PNG）输出目录；为 null / 空时不导出 PNG。
        /// <para>哈希只能回答"变没变"，PNG 才能回答"变在哪"。</para>
        /// </summary>
        public string? PngDir { get; set; }

        /// <summary>执行全部用例。</summary>
        public void RunAll()
        {
            RunLegacyPortraitParity();
            RunLegacyHorizontalParity();
            RunMechPortraitEffective();
            RunMechHorizontalEffective();
            RunLegacyOptionsInvariants();
            RunRuntimeSnapshotReuse();
            RunGroupRegionBufferReuse();
            RunThemeGenerationBump();
            RunBasePlateAnchoring();
            RunWindowShapeWiring();
        }

        // ============================================================
        // ① 旧主题竖屏：新旧入口逐像素一致
        // ============================================================

        private void RunLegacyPortraitParity()
        {
            foreach (string themeName in WiringFixture.LegacyThemeNames)
            {
                foreach ((int dpi, float userScale) in PortraitScales)
                {
                    float dpiScale = dpi / 96f;
                    float finalScale = dpiScale * userScale;

                    Theme theme = WiringFixture.LoadTheme(themeName);
                    theme.Scale(dpiScale, userScale);          // 与生产 ApplyTheme 同一条路径
                    WiringScene scene = WiringFixture.BuildPortrait(theme, finalScale);

                    using Bitmap legacy = WiringFixture.Render(scene, null);   // 旧入口
                    using AppearanceRuntime runtime = new AppearanceRuntime();
                    runtime.Update(theme, dpiScale, userScale, new Rectangle(0, 0, scene.Width, scene.Height));
                    using Bitmap wired = WiringFixture.Render(scene, runtime); // 新入口（A13 接线后的生产路径）

                    int diff = WiringFixture.CountDifferentPixels(legacy, wired);
                    ProbeCase c = NewCase($"旧主题竖屏像素一致/{themeName}/dpi{dpi}/scale{userScale:0.0}");
                    c.Details["width"] = scene.Width;
                    c.Details["height"] = scene.Height;
                    c.Details["differentPixels"] = diff;
                    c.Details["legacyPixelSha256"] = WiringFixture.HashPixels(legacy);
                    c.Details["wiredPixelSha256"] = WiringFixture.HashPixels(wired);
                    if (diff != 0)
                    {
                        c.Fail($"新入口与旧入口有 {diff} 个像素不同（期望 0）");
                    }
                    Add(c);
                }
            }
        }

        // ============================================================
        // ② 旧主题横屏：新旧入口逐像素一致
        // ============================================================

        private void RunLegacyHorizontalParity()
        {
            foreach (string themeName in WiringFixture.LegacyThemeNames)
            {
                foreach ((int dpi, float userScale) in HorizontalScales)
                {
                    float dpiScale = dpi / 96f;
                    float finalScale = dpiScale * userScale;

                    Theme theme = WiringFixture.LoadTheme(themeName);
                    theme.Scale(dpiScale, userScale);
                    int width = (int)(WiringFixture.PanelWidthLogical * finalScale);
                    int height = (int)(70 * finalScale);
                    WiringScene scene = WiringFixture.BuildHorizontal(theme, width, height);

                    using Bitmap legacy = WiringFixture.Render(scene, null);
                    using AppearanceRuntime runtime = new AppearanceRuntime();
                    runtime.Update(theme, dpiScale, userScale, new Rectangle(0, 0, scene.Width, scene.Height));
                    using Bitmap wired = WiringFixture.Render(scene, runtime);

                    int diff = WiringFixture.CountDifferentPixels(legacy, wired);
                    ProbeCase c = NewCase($"旧主题横屏像素一致/{themeName}/dpi{dpi}/scale{userScale:0.0}");
                    c.Details["width"] = scene.Width;
                    c.Details["height"] = scene.Height;
                    c.Details["differentPixels"] = diff;
                    // ★ 像素哈希：用于"渲染路径优化前后画面必须完全一致"的回归对比
                    c.Details["legacyPixelSha256"] = WiringFixture.HashPixels(legacy);
                    c.Details["wiredPixelSha256"] = WiringFixture.HashPixels(wired);
                    if (diff != 0)
                    {
                        c.Fail($"新入口与旧入口有 {diff} 个像素不同（期望 0）");
                    }
                    Add(c);
                }
            }
        }

        // ============================================================
        // ③ 机能主题竖屏：新装饰真的生效，且背景层接管底板
        // ============================================================

        private void RunMechPortraitEffective()
        {
            foreach ((int dpi, float userScale) in PortraitScales)
            {
                float dpiScale = dpi / 96f;
                float finalScale = dpiScale * userScale;

                Theme theme = WiringFixture.LoadTheme(WiringFixture.MechThemeName);
                theme.Scale(dpiScale, userScale);
                WiringScene scene = WiringFixture.BuildPortrait(theme, finalScale);

                using Bitmap legacy = WiringFixture.Render(scene, null);
                using AppearanceRuntime runtime = new AppearanceRuntime();
                ThemeAppearanceOptions options = runtime.Update(
                    theme, dpiScale, userScale, new Rectangle(0, 0, scene.Width, scene.Height));
                using Bitmap wired = WiringFixture.Render(scene, runtime);

                int diff = WiringFixture.CountDifferentPixels(legacy, wired);
                ProbeCase c = NewCase($"机能主题竖屏装饰生效/dpi{dpi}/scale{userScale:0.0}");
                c.Details["width"] = scene.Width;
                c.Details["height"] = scene.Height;
                c.Details["differentPixels"] = diff;
                // ★ 像素哈希：用于"渲染路径优化前后画面必须完全一致"的回归对比
                c.Details["legacyPixelSha256"] = WiringFixture.HashPixels(legacy);
                c.Details["wiredPixelSha256"] = WiringFixture.HashPixels(wired);
                c.Details["frameStyle"] = options.FrameStyle;
                c.Details["frameLengthPx"] = options.FrameLengthPx;
                c.Details["scanline"] = options.Scanline;
                c.Details["glow"] = options.Glow;
                c.Details["grid"] = options.Grid;
                c.Details["barStyle"] = options.BarStyle;

                // 像素证据：导出新入口的画面 PNG，供"渲染路径优化前后像素零变化"逐像素比对
                if (!string.IsNullOrEmpty(PngDir))
                {
                    string pngName = $"mech-portrait-dpi{dpi}-scale{userScale:0.0}.png";
                    WiringFixture.SavePng(wired, Path.Combine(PngDir!, pngName));
                    c.Details["wiredPng"] = pngName;
                }

                if (diff <= 0)
                {
                    c.Fail("新入口与旧入口完全相同（期望新装饰产生差异）");
                }
                if (!options.FrameEnabled) c.Fail("options.FrameEnabled 为 false（期望切角外框开启）");
                if (options.FrameLengthPx <= 0) c.Fail($"options.FrameLengthPx = {options.FrameLengthPx}（期望 > 0）");
                if (!options.ScanlineEnabled) c.Fail("options.ScanlineEnabled 为 false（期望扫描线开启）");
                if (!options.GlowEnabled) c.Fail("options.GlowEnabled 为 false（期望辉光开启）");
                if (!options.GridEnabled) c.Fail("options.GridEnabled 为 false（期望网格开启）");
                if (options.BarStyle == ThemeAppearanceOptions.BarStyleLegacy) c.Fail("options.BarStyle 仍是旧条（期望渐变条）");

                // 背景层必须接管底板：渐变真的铺出来了
                BackdropStatus backdrop = ReadBackdropStatus(runtime, options);
                c.Details["backdropEffectiveMode"] = backdrop.EffectiveMode.ToString();
                c.Details["backdropLayers"] = backdrop.Layers.ToString();
                if (backdrop.EffectiveMode != BackdropRenderMode.Gradient)
                {
                    c.Fail($"背景层有效模式 = {backdrop.EffectiveMode}（期望 Gradient）");
                }
                if ((backdrop.Layers & BackdropLayer.Tint) != 0)
                {
                    c.Fail("背景层含 Tint 层（opacity=0 时期望 Tint 被关闭）");
                }

                // 像素证据：底板必须让位 —— 新画面里**不应再有任何一个底板色像素**，
                // 渐变应铺满整个客户区。旧画面作为对照组：那里本来就有一大片底板色。
                Color basePlate = ThemeManager.ParseColor(theme.Color.Background);
                long total = (long)scene.Width * scene.Height;
                long legacyNonBase = CountNonBasePixels(legacy, basePlate);
                long wiredNonBase = CountNonBasePixels(wired, basePlate);
                c.Details["totalPixels"] = total;
                c.Details["legacyNonBasePixels"] = legacyNonBase;
                c.Details["wiredNonBasePixels"] = wiredNonBase;
                c.Details["legacyBasePlatePixels"] = total - legacyNonBase;
                c.Details["wiredBasePlatePixels"] = total - wiredNonBase;
                if (wiredNonBase != total)
                {
                    c.Fail($"新画面里还有 {total - wiredNonBase} 个底板色像素（期望 0）" +
                           " —— 旧的实心底板没有让位，渐变被盖住了");
                }
                if (legacyNonBase >= total)
                {
                    c.Fail("对照组异常：旧画面里没有任何底板色像素，断言失去意义");
                }

                // 覆盖度证据：背景层必须把**整个客户区**铺满 ——
                // 客户区最外一圈若 alpha < 255，说明底板 / 渐变没盖到边界（漏补底色）。
                (int rowMin, int rowMax, int rowNotOpaque) = WiringFixture.GetRowAlphaSummary(wired, 0);
                (int colMin, int colMax, int colNotOpaque) = WiringFixture.GetColumnAlphaSummary(wired, 0);
                int semiTransparent = WiringFixture.CountSemiTransparentPixels(wired);
                c.Details["clientBounds"] = $"{options.ClientBounds.X},{options.ClientBounds.Y}," +
                                           $"{options.ClientBounds.Width},{options.ClientBounds.Height}";
                c.Details["canvasSize"] = $"{scene.Width},{scene.Height}";
                c.Details["gradientStartColor"] = options.GradientStartColor.ToString();
                c.Details["gradientEndColor"] = options.GradientEndColor.ToString();
                c.Details["gradientStartAlpha"] = options.GradientStartColor.A;
                c.Details["gradientEndAlpha"] = options.GradientEndColor.A;
                c.Details["wiredSemiTransparentPixels"] = semiTransparent;
                c.Details["wiredRow0AlphaMin"] = rowMin;
                c.Details["wiredRow0AlphaMax"] = rowMax;
                c.Details["wiredRow0NotOpaque"] = rowNotOpaque;
                c.Details["wiredCol0AlphaMin"] = colMin;
                c.Details["wiredCol0AlphaMax"] = colMax;
                c.Details["wiredCol0NotOpaque"] = colNotOpaque;

                // ★ 断言：背景层必须真的铺满整个客户区。
                //   根因（见 A14 验收文档 §2.3）：绘制层开了 SmoothingMode.AntiAlias，
                //   PixelOffsetMode 默认对位图等价 Half —— 严格按 ClientBounds 填充时
                //   抗锯齿只覆盖最外一圈像素的约一半，客户区边界必然留下半透明像素。
                if (semiTransparent != 0)
                {
                    c.Fail($"新画面里还有 {semiTransparent} 个半透明像素（期望 0）" +
                           " —— 背景层没把客户区铺满，最外一圈会漏色");
                }
                if (rowNotOpaque != 0 || colNotOpaque != 0)
                {
                    c.Fail($"客户区首行有 {rowNotOpaque} 个、首列有 {colNotOpaque} 个半透明像素（期望 0）" +
                           " —— 背景层没覆盖到客户区边界");
                }

                Add(c);
            }
        }

        // ============================================================
        // ④ 机能主题横屏：新装饰真的生效
        // ============================================================

        private void RunMechHorizontalEffective()
        {
            foreach ((int dpi, float userScale) in HorizontalScales)
            {
                float dpiScale = dpi / 96f;
                float finalScale = dpiScale * userScale;

                Theme theme = WiringFixture.LoadTheme(WiringFixture.MechThemeName);
                theme.Scale(dpiScale, userScale);
                int width = (int)(WiringFixture.PanelWidthLogical * finalScale);
                int height = (int)(70 * finalScale);
                WiringScene scene = WiringFixture.BuildHorizontal(theme, width, height);

                using Bitmap legacy = WiringFixture.Render(scene, null);
                using AppearanceRuntime runtime = new AppearanceRuntime();
                ThemeAppearanceOptions options = runtime.Update(
                    theme, dpiScale, userScale, new Rectangle(0, 0, scene.Width, scene.Height));
                using Bitmap wired = WiringFixture.Render(scene, runtime);

                int diff = WiringFixture.CountDifferentPixels(legacy, wired);
                ProbeCase c = NewCase($"机能主题横屏装饰生效/dpi{dpi}/scale{userScale:0.0}");
                c.Details["differentPixels"] = diff;
                c.Details["legacyPixelSha256"] = WiringFixture.HashPixels(legacy);
                c.Details["wiredPixelSha256"] = WiringFixture.HashPixels(wired);
                if (diff <= 0)
                {
                    c.Fail("新入口与旧入口完全相同（期望新装饰产生差异）");
                }
                if (!options.FrameEnabled) c.Fail("options.FrameEnabled 为 false（期望外框开启）");
                Add(c);
            }
        }

        // ============================================================
        // ⑤ 旧主题：外观快照里的新开关必须全是关的
        // ============================================================

        private void RunLegacyOptionsInvariants()
        {
            ProbeCase c = NewCase("旧主题/外观快照不变量（全部新开关关闭）");
            int checkedThemes = 0;

            foreach (string themeName in WiringFixture.LegacyThemeNames)
            {
                Theme theme = WiringFixture.LoadTheme(themeName);
                theme.Scale(1f, 1f);

                using AppearanceRuntime runtime = new AppearanceRuntime();
                Rectangle bounds = new Rectangle(0, 0, 240, 820);
                ThemeAppearanceOptions options = runtime.Update(theme, 1f, 1f, bounds);
                checkedThemes++;

                if (options.FrameEnabled) c.Fail($"{themeName}: FrameEnabled 为 true（旧主题不应有外框）");
                if (options.FrameWidthPx != 0) c.Fail($"{themeName}: FrameWidthPx = {options.FrameWidthPx}（期望 0）");
                if (options.FrameLengthPx != 0) c.Fail($"{themeName}: FrameLengthPx = {options.FrameLengthPx}（期望 0）");
                if (options.ScanlineEnabled) c.Fail($"{themeName}: ScanlineEnabled 为 true");
                if (options.GlowEnabled) c.Fail($"{themeName}: GlowEnabled 为 true");
                if (options.GridEnabled) c.Fail($"{themeName}: GridEnabled 为 true");
                if (options.BarStyle != ThemeAppearanceOptions.BarStyleLegacy) c.Fail($"{themeName}: BarStyle = {options.BarStyle}（期望旧条）");
                if (!string.Equals(options.BackdropMode, ThemeAppearanceOptions.BackdropModeSolid, StringComparison.Ordinal))
                    c.Fail($"{themeName}: BackdropMode = {options.BackdropMode}（期望 solid）");
                if (options.BackgroundFrame != null) c.Fail($"{themeName}: BackgroundFrame 非空（旧主题不应有实时帧）");
                if (options.ClientBounds != bounds) c.Fail($"{themeName}: ClientBounds 与传入的不一致");
            }

            c.Details["checkedThemes"] = checkedThemes;
            Add(c);
        }

        // ============================================================
        // ⑥ 外观运行时：参数不变时复用快照，参数变化时重建
        // ============================================================

        private void RunRuntimeSnapshotReuse()
        {
            ProbeCase c = NewCase("外观运行时/快照复用与失效");
            Theme theme = WiringFixture.LoadTheme("DarkFlat_Classic");
            theme.Scale(1f, 1f);

            using AppearanceRuntime runtime = new AppearanceRuntime();
            Rectangle bounds = new Rectangle(0, 0, 240, 820);

            ThemeAppearanceOptions first = runtime.Update(theme, 1f, 1f, bounds);
            bool reused = true;
            for (int i = 0; i < 100; i++)
            {
                ThemeAppearanceOptions again = runtime.Update(theme, 1f, 1f, bounds);
                if (!ReferenceEquals(again, first)) { reused = false; break; }
            }
            c.Details["reusedFor100Calls"] = reused;
            if (!reused) c.Fail("参数未变时快照被重建了（热路径不应产生新分配）");

            Rectangle bigger = new Rectangle(0, 0, 250, 820);
            ThemeAppearanceOptions resized = runtime.Update(theme, 1f, 1f, bigger);
            if (ReferenceEquals(resized, first)) c.Fail("客户区尺寸变了但快照没有重建");

            ThemeAppearanceOptions restored = runtime.Update(theme, 1f, 1f, bounds);
            if (ReferenceEquals(restored, first)) c.Fail("尺寸变回来后错误地复用了旧快照");

            float newDpi = 1.25f;
            ThemeAppearanceOptions dpiChanged = runtime.Update(theme, newDpi, 1f, bounds);
            if (ReferenceEquals(dpiChanged, restored)) c.Fail("DPI 变了但快照没有重建");
            c.Details["dpiScaleUsed"] = newDpi;

            Add(c);
        }

        // ============================================================
        // ⑦ 组区域缓冲复用不产生跨帧污染
        // ============================================================

        /// <summary>
        /// 窗口底纹要"排除组块区域"，组块矩形随布局变化。
        /// <c>AppearanceRuntime.RentGroupRegions</c> 用**一个复用的 List** 承载它
        /// （避免每帧 new List 破坏"新效果关闭 → 热路径不增加托管分配"）。
        /// <para>
        /// ★ 本用例盯的是**复用是否安全**：先用 7 组把缓冲填满，再渲染 3 组场景，
        ///   结果必须与"干净运行时直接渲染 3 组"逐像素一致 ——
        ///   否则说明上一次的组区域残留进了这一帧。
        /// </para>
        /// </summary>
        private void RunGroupRegionBufferReuse()
        {
            ProbeCase c = NewCase("外观运行时/组区域缓冲复用不产生跨帧污染");

            try
            {
                Theme theme = WiringFixture.LoadTheme(WiringFixture.MechThemeName);
                theme.Scale(1f, 1f);

                using AppearanceRuntime runtime = new AppearanceRuntime();

                // 大场景：7 组（用来"污染"复用缓冲）
                WiringScene big = WiringFixture.BuildPortrait(theme, 1f, WiringFixture.GroupCount);
                // 小场景：3 组
                WiringScene small = WiringFixture.BuildPortrait(theme, 1f, 3);

                // 画布按小场景的尺寸建（两张图才能逐像素比）
                using Bitmap canvas = new Bitmap(
                    Math.Max(1, small.Width), Math.Max(1, small.Height), PixelFormat.Format32bppPArgb);

                runtime.Update(theme, 1f, 1f, new Rectangle(0, 0, small.Width, small.Height));
                WiringFixture.RenderOnto(canvas, big, runtime);      // 先画 7 组
                WiringFixture.ClearCanvas(canvas);
                WiringFixture.RenderOnto(canvas, small, runtime);    // 再画 3 组（缓冲此时应已正确清空）

                // 对照：干净运行时直接渲染 3 组
                using AppearanceRuntime freshRuntime = new AppearanceRuntime();
                freshRuntime.Update(theme, 1f, 1f, new Rectangle(0, 0, small.Width, small.Height));
                using Bitmap reference = WiringFixture.Render(small, freshRuntime);

                int diff = WiringFixture.CountDifferentPixels(reference, canvas);
                c.Details["differentPixels"] = diff;
                c.Details["bigGroups"] = WiringFixture.GroupCount;
                c.Details["smallGroups"] = 3;
                c.Details["size"] = $"{small.Width}x{small.Height}";
                if (diff != 0)
                {
                    c.Fail($"复用缓冲后 3 组场景与干净运行时相差 {diff} 个像素（期望 0）—— 组区域缓冲有跨帧污染");
                }
            }
            catch (Exception ex)
            {
                c.Fail("用例抛出异常：" + ex.GetType().Name + " — " + ex.Message);
            }

            Add(c);
        }

        // ============================================================
        // ⑧ 换肤 → 主题代次自增
        // ============================================================

        private void RunThemeGenerationBump()
        {
            ProbeCase c = NewCase("外观运行时/换肤时代次自增");
            Theme first = WiringFixture.LoadTheme("DarkFlat_Classic");
            Theme second = WiringFixture.LoadTheme("CyberPunk_Mech");

            using AppearanceRuntime runtime = new AppearanceRuntime();
            Rectangle bounds = new Rectangle(0, 0, 240, 820);

            runtime.Update(first, 1f, 1f, bounds);
            int gen0 = runtime.ThemeGeneration;

            runtime.OnThemeChanged(second, 1f, 1f, bounds);
            int gen1 = runtime.ThemeGeneration;

            runtime.OnThemeChanged(second, 1f, 1f, bounds);
            int gen2 = runtime.ThemeGeneration;

            c.Details["generationBefore"] = gen0;
            c.Details["generationAfterFirstChange"] = gen1;
            c.Details["generationAfterSecondChange"] = gen2;
            if (gen1 != gen0 + 1) c.Fail($"第一次换肤后代次从 {gen0} 变成 {gen1}（期望 +1）");
            if (gen2 != gen1 + 1) c.Fail($"第二次换肤后代次从 {gen1} 变成 {gen2}（期望 +1）");
            Add(c);
        }

        // ============================================================
        // ============================================================
        // ⑨ 底板锚定与局部失效补底色（独立验收 R5）
        // ============================================================

        /// <summary>
        /// 底板必须锚定**完整客户区**，否则局部失效时补不上底色。
        /// <para>
        /// ★ 反例来自独立验收 R5：客户区 240×160、背景 #123456、调用方裁剪 (37,43,20,20)，
        ///   新入口绘制后，失效区内点 (40,45) 仍是预填的洋红色 FFFF00FF。
        ///   根因：旧实现用 g.VisibleClipBounds 定位底板 —— 局部失效时它就是那块裁剪区，
        ///   底板会从窗口原点偏移着画，压根没盖到失效区。
        /// <para>
        /// ★ 本用例用"预填哨兵色 + 裁剪区"复现：渲染后裁剪区内若还有哨兵色，
        ///   就说明那块没被补上底色。旧入口**保留**该缺陷（它拿不到完整客户区，
        ///   改动会破坏旧主题像素基线），所以只对新入口断言。
        /// </para>
        /// </summary>
        private void RunBasePlateAnchoring()
        {
            const string backgroundHex = "#123456";
            Color sentinel = Color.Magenta;
            Rectangle clipRect = new Rectangle(37, 43, 20, 20);

            foreach (bool horizontal in new[] { false, true })
            {
                string label = horizontal ? "横屏" : "竖屏";
                ProbeCase c = NewCase($"底板锚定/局部失效补底色/{label}");

                Theme theme = WiringFixture.LoadTheme(WiringFixture.LegacyThemeNames[0]);
                theme.Color.Background = backgroundHex;

                WiringScene scene = horizontal
                    ? WiringFixture.BuildHorizontal(theme, WiringFixture.PanelWidthLogical, 160)
                    : WiringFixture.BuildPortrait(theme, 1.0f);

                // ---- 新入口：底板锚定完整客户区 ⇒ 失效区必须被补上底色 ----
                using Bitmap canvas = new Bitmap(scene.Width, scene.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    g.Clear(sentinel);
                }

                using (AppearanceRuntime runtime = new AppearanceRuntime())
                {
                    runtime.Update(theme, 1.0f, 1.0f, new Rectangle(0, 0, scene.Width, scene.Height));
                    WiringFixture.RenderOnto(canvas, scene, runtime, clipRect);
                }

                int sentinelLeft = WiringFixture.CountColorPixelsInRect(canvas, clipRect, sentinel);
                c.Details["clientBounds"] = $"0,0,{scene.Width},{scene.Height}";
                c.Details["clipRect"] = $"{clipRect.X},{clipRect.Y},{clipRect.Width},{clipRect.Height}";
                c.Details["sentinelPixelsLeftInClip"] = sentinelLeft;
                c.Details["clipAreaPixels"] = clipRect.Width * clipRect.Height;

                if (sentinelLeft != 0)
                {
                    c.Fail($"失效区内还有 {sentinelLeft} 个哨兵色像素（期望 0）" +
                           " —— 底板没有锚定完整客户区，局部重绘会漏补底色");
                }

                // ---- 旧入口对照：记录现状（已知继承问题，不在此断言）----
                using Bitmap legacyCanvas = new Bitmap(scene.Width, scene.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(legacyCanvas))
                {
                    g.Clear(sentinel);
                }
                WiringFixture.RenderOnto(legacyCanvas, scene, null, clipRect);
                c.Details["legacySentinelPixelsLeftInClip"] =
                    WiringFixture.CountColorPixelsInRect(legacyCanvas, clipRect, sentinel);

                Add(c);
            }
        }

        // ============================================================
        // ⑩ 窗口轮廓接线：切角 → DONOTROUND(1)，旧主题 → ROUND(2)
        // ============================================================
        private void RunWindowShapeWiring()
        {
            ProbeCase c = NewCase("窗口轮廓/接线行为（切角 DONOTROUND=1，旧主题回 ROUND=2）");

            try
            {
                // ★ WinForms 窗体必须在 STA 线程上创建与操作（与 A09 自证探针同一条规矩）
                Exception? error = null;
                Thread thread = new Thread(() =>
                {
                    try { RunWindowShapeWiringCore(c); }
                    catch (Exception ex) { error = ex; }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                thread.Join();
                if (error != null) throw error;
            }
            catch (Exception ex)
            {
                c.Fail("窗口轮廓接线用例抛出异常：" + ex.GetType().Name + " — " + ex.Message);
            }

            Add(c);
        }

        private void RunWindowShapeWiringCore(ProbeCase c)
        {
            using Form form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),   // 离屏，避免打扰用户
                ClientSize = new Size(240, 820)
            };
            _ = form.Handle;   // 只创建句柄，不显示（不打扰桌面、不做屏幕捕获）

            using WindowAppearanceController controller = new WindowAppearanceController(form);

            // --- 旧主题：无自定义形状 → 回到系统圆角 ROUND(2) ---
            Theme legacy = WiringFixture.LoadTheme("DarkFlat_Classic");
            legacy.Scale(1f, 1f);
            using (AppearanceRuntime runtime = new AppearanceRuntime())
            {
                ThemeAppearanceOptions options = runtime.Update(legacy, 1f, 1f, form.ClientRectangle);
                WindowShapeResult legacyResult = controller.Apply(options, legacy.Layout.CornerRadius);
                c.Details["legacyStrategy"] = legacyResult.Strategy.ToString();
                c.Details["legacyCornerPreference"] = legacyResult.CornerPreference;
                if (legacyResult.CornerPreference != WindowAppearanceController.DwmcpRound)
                    c.Fail($"旧主题的 DWM 角偏好 = {legacyResult.CornerPreference}（期望 {WindowAppearanceController.DwmcpRound} = ROUND）");
            }

            // --- 机能主题：切角 → DONOTROUND(1) + Region ---
            Theme mech = WiringFixture.LoadTheme(WiringFixture.MechThemeName);
            mech.Scale(1f, 1f);
            using (AppearanceRuntime runtime = new AppearanceRuntime())
            {
                ThemeAppearanceOptions options = runtime.Update(mech, 1f, 1f, form.ClientRectangle);
                WindowShapeResult mechResult = controller.Apply(options, mech.Layout.CornerRadius);
                c.Details["mechStrategy"] = mechResult.Strategy.ToString();
                c.Details["mechCornerPreference"] = mechResult.CornerPreference;
                c.Details["mechRegionApplied"] = mechResult.RegionApplied;
                c.Details["mechCornerLengthPx"] = mechResult.CornerLengthPx;

                if (mechResult.CornerPreference != WindowAppearanceController.DwmcpDoNotRound)
                    c.Fail($"切角的 DWM 角偏好 = {mechResult.CornerPreference}（期望 {WindowAppearanceController.DwmcpDoNotRound} = DONOTROUND）");
                if (!mechResult.RegionApplied)
                    c.Fail("切角没有设置 Region（窗口形状不会跟着画框切角）");
                if (mechResult.CornerLengthPx != options.FrameLengthPx)
                    c.Fail($"切角边长 = {mechResult.CornerLengthPx}（期望与 options.FrameLengthPx = {options.FrameLengthPx} 相同）");
            }

            // --- 尺寸变化后重应用：策略必须保持切角 ---
            form.ClientSize = new Size(260, 900);
            WindowShapeResult reapplied = controller.Reapply();
            c.Details["reappliedCornerPreference"] = reapplied.CornerPreference;
            if (reapplied.CornerPreference != WindowAppearanceController.DwmcpDoNotRound)
                c.Fail($"尺寸变化后重应用的角偏好 = {reapplied.CornerPreference}（期望保持 DONOTROUND）");

            // --- 回旧主题：必须恢复 ROUND(2) ---
            using (AppearanceRuntime runtime = new AppearanceRuntime())
            {
                ThemeAppearanceOptions options = runtime.Update(legacy, 1f, 1f, form.ClientRectangle);
                WindowShapeResult back = controller.Apply(options, legacy.Layout.CornerRadius);
                c.Details["backToLegacyCornerPreference"] = back.CornerPreference;
                if (back.CornerPreference != WindowAppearanceController.DwmcpRound)
                    c.Fail($"切角切回旧主题后角偏好 = {back.CornerPreference}（期望恢复 ROUND）");
            }
        }

        // ============================================================
        // 辅助
        // ============================================================

        /// <summary>
        /// 读取一次背景层的渲染状态。
        /// <para>
        /// 用一块临时画布渲染 —— 这样不会污染用例里的比对画布，
        /// 也不会把装饰层的像素混进来。
        /// </para>
        /// </summary>
        private static BackdropStatus ReadBackdropStatus(AppearanceRuntime runtime, ThemeAppearanceOptions options)
        {
            using Bitmap scratch = new Bitmap(Math.Max(1, options.ClientBounds.Width), Math.Max(1, options.ClientBounds.Height));
            using Graphics g = Graphics.FromImage(scratch);
            return runtime.Backdrop.Render(g, options);
        }

        /// <summary>统计与给定底板色不同的像素数。</summary>
        private static long CountNonBasePixels(Bitmap bitmap, Color basePlate)
        {
            byte[] pixels = WiringFixture.GetPixels(bitmap);
            byte br = basePlate.R, bg = basePlate.G, bb = basePlate.B;
            long count = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] != bb || pixels[i + 1] != bg || pixels[i + 2] != br) count++;
            }
            return count;
        }

        private static ProbeCase NewCase(string name) => new ProbeCase { Name = name };

        private void Add(ProbeCase c)
        {
            Cases.Add(c);
            Console.WriteLine($"[{(c.Passed ? "PASS" : "FAIL")}] {c.Name}");
            foreach (string f in c.Failures) Console.WriteLine("        - " + f);
        }
    }

    /// <summary>一个探针用例。</summary>
    internal sealed class ProbeCase
    {
        public string Name = "";
        public bool Passed = true;
        public List<string> Failures = new();
        public JsonObject Details = new();

        public void Fail(string reason)
        {
            Passed = false;
            Failures.Add(reason);
        }
    }
}
