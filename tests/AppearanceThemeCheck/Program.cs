using System.Drawing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackdropLayer = LiteMonitor.src.SystemServices.Backdrop.BackdropLayer;
using BackdropModeResolver = LiteMonitor.src.SystemServices.Backdrop.BackdropModeResolver;
using BackdropRenderMode = LiteMonitor.src.SystemServices.Backdrop.BackdropRenderMode;
using Theme = LiteMonitor.src.Core.Theme;
using ThemeAppearanceOptions = LiteMonitor.src.Core.ThemeAppearanceOptions;
using ThemeFileService = LiteMonitor.ThemeEditor.ThemeFileService;
using ThemeManager = LiteMonitor.src.Core.ThemeManager;
using BackdropConfig = LiteMonitor.src.Core.BackdropConfig;
using ColorConfig = LiteMonitor.src.Core.ColorConfig;
using FontConfig = LiteMonitor.src.Core.FontConfig;
using LayoutConfig = LiteMonitor.src.Core.LayoutConfig;
using ThresholdConfig = LiteMonitor.src.Core.ThresholdConfig;

namespace LiteMonitor.AppearanceThemeCheck
{
    /// <summary>
    /// A12 主题参数校验程序。
    ///
    /// 校验范围（全部是纯 CPU 判定，不做屏幕捕获、不移动/隐藏窗口、不改窗口透明度）：
    /// 1. 基线：10 个旧主题的 SHA256 与 A00 基线表逐一对齐，主题目录文件集合不越界；
    /// 2. 加载：主主题与两个夹具能被 ThemeFileService 正常加载；
    /// 3. 参数：磁盘文件里的参数与期望参数表逐项相等；
    /// 4. 归一化：ThemeAppearanceOptions.Create 在多组缩放/尺寸下无意外夹紧，
    ///    且归一化**不回写**原 Theme 对象；
    /// 5. 背景规则：非 solid 模式必须显式写 opacity；opacity>0 必须显式写 tint；
    ///    透明 tint 不破坏 glass → gradient → solid 降级链；
    /// 6. 往返：保存后再加载，所有字段值不变，源文件显式写出的键没有丢失；
    /// 7. 可读性：按最坏合成面计算 WCAG 对比度并对照判据表；
    /// 8. 预览一致性（A13 收口 R2）：编辑器预览与主渲染入口的效果必须一致
    ///    （只切 Glow / BarStyle 时，预览像素必须跟着变）。
    ///
    /// 退出码：0 = 全部通过；1 = 有失败项；2 = 环境定位失败。
    /// </summary>
    internal static class Program
    {
        /// <summary>10 个旧主题的 SHA256 前 16 位基线（来源：docs/superpowers/plans/2026-10-20-appearance-a00-baseline.md）。</summary>
        private static readonly (string Name, string Sha16)[] LegacyBaseline =
        {
            ("AuroraDream_Purple", "0D098F3AA66EA7BC"),
            ("CyberBlue_Neon", "213D6F92AE3D0075"),
            ("DarkFlat_Classic", "84B905210DE2FAE8"),
            ("DarkNeo_Modern", "619A4400D272CA74"),
            ("DeepOcean_Blue", "E5E3E7D0FFD38E76"),
            ("Dracula_Vampire", "A365E91E2364B932"),
            ("LightPure_Elegant", "950DCCDEA5E3BA53"),
            ("MatrixGreen_Terminal", "2E2038E6CE356D35"),
            ("Monokai_Pro", "7A347796A001B13F"),
            ("SolarAmber_Warm", "1D6182A337F22BCD"),
        };

        /// <summary>归一化检查用的缩放/尺寸矩阵：竖屏常规、竖屏 125%、150%+用户 2.0 倍、窄窗低缩放。</summary>
        private static readonly (float Dpi, float User, int W, int H)[] ScaleMatrix =
        {
            (1.0f, 1.0f, 240, 788),
            (1.25f, 1.0f, 300, 985),
            (1.5f, 2.0f, 360, 1182),
            (0.75f, 0.5f, 160, 520),
        };

        /// <summary>判据阈值：正文类文本。</summary>
        private const double MinTextContrast = 4.5;

        /// <summary>判据阈值：进度条等非文本图形。</summary>
        private const double MinGraphicContrast = 3.0;

        /// <summary>判据阈值：组块与窗口底的可分性。</summary>
        private const double MinGroupSeparation = 1.2;

        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Console.OutputEncoding = new UTF8Encoding(false);

            string? repoRoot = LocateRepoRoot();
            if (repoRoot is null)
            {
                Console.Error.WriteLine("[FAIL] 找不到仓库根目录（需要同时存在 LiteMonitor.csproj 与 resources/themes）。");
                return 2;
            }

            Console.WriteLine("LiteMonitor++ A12 主题参数校验");
            Console.WriteLine("仓库根目录：" + repoRoot);
            Console.WriteLine();

            string themeDir = Path.Combine(repoRoot, "resources", "themes");
            string roundTripDir = Path.Combine(AppContext.BaseDirectory, "roundtrip");

            CheckBaseline(themeDir);

            foreach (ThemeSpec spec in ThemeSpecs.All())
                CheckTheme(spec, repoRoot, roundTripDir);

            PreviewConsistencyCheck.Run(repoRoot);

            Console.WriteLine();
            Console.WriteLine($"合计：通过 {_passed} 项，失败 {_failed} 项。");
            return _failed == 0 ? 0 : 1;
        }

        // ============================================================
        // 基线
        // ============================================================

        /// <summary>校验 10 个旧主题未被改动，且主题目录里没有多余文件。</summary>
        private static void CheckBaseline(string themeDir)
        {
            Section("基线：旧主题 SHA256 与主题目录文件集合");

            foreach ((string name, string sha) in LegacyBaseline)
            {
                string path = Path.Combine(themeDir, name + ".json");
                if (!File.Exists(path))
                {
                    Fail($"旧主题缺失：{name}.json");
                    continue;
                }

                string actual = Sha256Prefix(path, 16);
                Check(actual == sha, $"{name}.json 的 SHA256 前 16 位", $"期望 {sha}，实际 {actual}");
            }

            List<string> actualFiles = Directory.GetFiles(themeDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(x => x != null)
                .Select(x => x!)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            List<string> expectedFiles = LegacyBaseline.Select(x => x.Name)
                .Append(ThemeSpecs.Main.Name)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            Check(actualFiles.SequenceEqual(expectedFiles),
                "主题目录文件集合（10 个旧主题 + A12 主主题）",
                $"期望 {string.Join(", ", expectedFiles)}；实际 {string.Join(", ", actualFiles)}");
        }

        // ============================================================
        // 单个主题
        // ============================================================

        /// <summary>对一个主题做全部校验。</summary>
        private static void CheckTheme(ThemeSpec spec, string repoRoot, string roundTripDir)
        {
            Section($"主题：{spec.Name}（{spec.RelativePath}）");

            string path = Path.Combine(repoRoot, spec.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                Fail($"主题文件缺失：{spec.RelativePath}");
                return;
            }

            Theme theme;
            try
            {
                theme = ThemeFileService.LoadTheme(Path.GetDirectoryName(path)!, spec.Name);
            }
            catch (Exception ex)
            {
                Fail($"加载失败（ThemeFileService → LoadTheme）：{ex.Message}");
                return;
            }
            Check(true, "加载成功（ThemeFileService → LoadTheme）");

            CheckModelValues(spec, theme);

            string before = Snapshot(theme);
            CheckNormalization(spec, theme);
            Check(Snapshot(theme) == before, "归一化不回写原 Theme 对象（只作用于运行时副本）");

            CheckBackdropRules(spec, path, theme);
            CheckRoundTrip(spec, path, roundTripDir);
            CheckContrast(spec, theme);
        }

        /// <summary>校验模型里的参数值与期望表一致。</summary>
        private static void CheckModelValues(ThemeSpec spec, Theme theme)
        {
            Section($"{spec.Name}：参数值");

            Check(theme.Version == spec.Version, "version", $"期望 {spec.Version}，实际 {theme.Version}");
            Check(theme.Name == spec.Name, "name", $"期望 {spec.Name}，实际 {theme.Name}");

            Check(theme.Layout.FrameStyle == spec.FrameStyle, "layout.frameStyle", $"期望 {spec.FrameStyle}，实际 {theme.Layout.FrameStyle}");
            Check(theme.Layout.FrameWidth == spec.FrameWidth, "layout.frameWidth", $"期望 {spec.FrameWidth}，实际 {theme.Layout.FrameWidth}");
            Check(theme.Layout.FrameLength == spec.FrameLength, "layout.frameLength", $"期望 {spec.FrameLength}，实际 {theme.Layout.FrameLength}");
            Check(theme.Layout.Scanline == spec.Scanline, "layout.scanline", $"期望 {spec.Scanline}，实际 {theme.Layout.Scanline}");
            Check(theme.Layout.Glow == spec.Glow, "layout.glow", $"期望 {spec.Glow}，实际 {theme.Layout.Glow}");
            Check(theme.Layout.Grid == spec.Grid, "layout.grid", $"期望 {spec.Grid}，实际 {theme.Layout.Grid}");
            Check(theme.Layout.BarStyle == spec.BarStyle, "layout.barStyle", $"期望 {spec.BarStyle}，实际 {theme.Layout.BarStyle}");

            BackdropConfig? backdrop = theme.Backdrop;
            if (backdrop is null)
            {
                Fail("backdrop 段缺失");
                return;
            }

            Check(string.Equals(backdrop.Mode, spec.BackdropMode, StringComparison.Ordinal), "backdrop.mode", $"期望 {spec.BackdropMode}，实际 {backdrop.Mode}");
            Check(Math.Abs(backdrop.Opacity - spec.BackdropOpacity) < 1e-9, "backdrop.opacity", $"期望 {spec.BackdropOpacity}，实际 {backdrop.Opacity}");
            Check(Math.Abs(backdrop.GradientAngle - spec.GradientAngle) < 1e-9, "backdrop.gradientAngle", $"期望 {spec.GradientAngle}，实际 {backdrop.GradientAngle}");
            Check(backdrop.Noise == spec.Noise, "backdrop.noise", $"期望 {spec.Noise}，实际 {backdrop.Noise}");
            Check(backdrop.EdgeGlow == spec.EdgeGlow, "backdrop.edgeGlow", $"期望 {spec.EdgeGlow}，实际 {backdrop.EdgeGlow}");
            CheckColor(spec.Tint, backdrop.Tint, "backdrop.tint");
            CheckColor(spec.GradientStart, backdrop.GradientStart, "backdrop.gradientStart");
            CheckColor(spec.GradientEnd, backdrop.GradientEnd, "backdrop.gradientEnd");

            foreach ((string key, string expected) in spec.Colors)
            {
                string? actual = GetColorValue(theme.Color, key);
                CheckColor(expected, actual, "color." + key);
            }
        }

        /// <summary>校验归一化结果：逻辑值不被夹紧，物理像素按公式换算并按客户区二次夹紧。</summary>
        private static void CheckNormalization(ThemeSpec spec, Theme theme)
        {
            Section($"{spec.Name}：归一化（ThemeAppearanceOptions → Create）");

            foreach ((float dpi, float user, int w, int h) in ScaleMatrix)
            {
                string tag = $"dpi={dpi} user={user} bounds={w}x{h}";
                ThemeAppearanceOptions options = ThemeAppearanceOptions.Create(theme, dpi, user, new Rectangle(0, 0, w, h));
                float scale = dpi * user;

                Check(options.FrameStyle == spec.FrameStyle, $"{tag} frameStyle 不变", $"实际 {options.FrameStyle}");
                Check(options.FrameWidth == spec.FrameWidth, $"{tag} frameWidth 逻辑值不夹紧", $"实际 {options.FrameWidth}");
                Check(options.FrameLength == spec.FrameLength, $"{tag} frameLength 逻辑值不夹紧", $"实际 {options.FrameLength}");
                Check(options.Scanline == spec.Scanline && options.Glow == spec.Glow && options.Grid == spec.Grid && options.BarStyle == spec.BarStyle,
                    $"{tag} 强度与样式不夹紧", $"实际 scanline={options.Scanline} glow={options.Glow} grid={options.Grid} barStyle={options.BarStyle}");

                int expectWidthPx = Math.Min((int)Math.Round(spec.FrameWidth * scale, MidpointRounding.AwayFromZero), w / 2);
                int expectLengthPx = Math.Min((int)Math.Round(spec.FrameLength * scale, MidpointRounding.AwayFromZero), h / 2);
                Check(options.FrameWidthPx == expectWidthPx, $"{tag} frameWidthPx 换算正确", $"期望 {expectWidthPx}，实际 {options.FrameWidthPx}");
                Check(options.FrameLengthPx == expectLengthPx, $"{tag} frameLengthPx 换算正确", $"期望 {expectLengthPx}，实际 {options.FrameLengthPx}");

                Check(string.Equals(options.BackdropMode, spec.BackdropMode, StringComparison.Ordinal), $"{tag} backdrop.mode 归一化后不变", $"实际 {options.BackdropMode}");
                Check(Math.Abs(options.BackdropOpacity - spec.BackdropOpacity) < 1e-9, $"{tag} backdrop.opacity 归一化后不变", $"实际 {options.BackdropOpacity}");

                Check(options.FrameEnabled, $"{tag} FrameEnabled 为真（允许消费 BorderColor）");
                Check(options.GlowEnabled, $"{tag} GlowEnabled 为真（允许消费 ShadowColor）");
                Check(options.ScanlineEnabled, $"{tag} ScanlineEnabled 为真");
                Check(options.GridEnabled, $"{tag} GridEnabled 为真");
                Check(options.BarStyleEnabled, $"{tag} BarStyleEnabled 为真");
            }
        }

        /// <summary>校验背景参数规则：显式 opacity、显式 tint、降级链不被透明 tint 破坏。</summary>
        private static void CheckBackdropRules(ThemeSpec spec, string path, Theme theme)
        {
            Section($"{spec.Name}：背景参数规则");

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!TryGetProperty(document.RootElement, "backdrop", out JsonElement backdrop))
            {
                Fail("源 JSON 缺少 backdrop 段");
                return;
            }

            string mode = TryGetProperty(backdrop, "mode", out JsonElement modeEl) ? modeEl.GetString() ?? string.Empty : "solid";
            bool hasOpacity = TryGetProperty(backdrop, "opacity", out JsonElement opacityEl);

            if (!string.Equals(mode, "solid", StringComparison.OrdinalIgnoreCase))
            {
                Check(hasOpacity,
                    $"mode={mode} 时必须显式写 opacity",
                    "缺省会取默认 0.75；tint 缺省又取 background（不透明），染色层会盖掉渐变");
            }

            double opacity = hasOpacity ? opacityEl.GetDouble() : 0.75;
            if (opacity > 0)
            {
                Check(TryGetProperty(backdrop, "tint", out _),
                    "opacity>0 时必须显式写 tint",
                    "否则 tint 缺省取 background（不透明），有效染色 alpha 会远高于预期");
            }

            ThemeAppearanceOptions options = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, new Rectangle(0, 0, 240, 788));
            int tintAlpha = BackdropModeResolver.ComputeTintAlpha(options);
            int expectAlpha = (int)Math.Round(options.TintColor.A * Math.Min(opacity, 1.0), MidpointRounding.AwayFromZero);
            Check(tintAlpha == expectAlpha,
                "ComputeTintAlpha 与「Tint alpha × Opacity」一致",
                $"期望 {expectAlpha}，实际 {tintAlpha}（Tint alpha={options.TintColor.A}，Opacity={opacity}）");

            Check(BackdropModeResolver.IsGradientUsable(options),
                "渐变可用（两端至少一端非全透明）",
                "透明 tint 不影响该判定，因此 glass → gradient → solid 降级链不会被破坏");

            BackdropLayer layers = BackdropModeResolver.ComputeLayers(options, BackdropRenderMode.Gradient);
            if (opacity <= 0)
                Check((layers & BackdropLayer.Tint) == 0, "opacity=0 → 渐变模式不产生染色层", $"实际层 {layers}");
            else
                Check((layers & BackdropLayer.Tint) != 0, "opacity>0 → 渐变模式产生染色层", $"实际层 {layers}");
        }

        /// <summary>校验往返保存：字段值不变、源文件显式键不丢、保存结果无重复键。</summary>
        private static void CheckRoundTrip(ThemeSpec spec, string path, string roundTripDir)
        {
            Section($"{spec.Name}：往返保存");

            Theme theme = ThemeFileService.LoadTheme(Path.GetDirectoryName(path)!, spec.Name);

            Directory.CreateDirectory(roundTripDir);
            string savedPath = Path.Combine(roundTripDir, spec.Name + ".json");
            if (File.Exists(savedPath)) File.Delete(savedPath);

            try
            {
                ThemeFileService.SaveTheme(roundTripDir, spec.Name, theme);
            }
            catch (Exception ex)
            {
                Fail($"保存失败：{ex.Message}");
                return;
            }
            Check(File.Exists(savedPath), "保存产出文件（ThemeFileService → SaveTheme）");

            Theme reloaded;
            try
            {
                reloaded = ThemeFileService.LoadTheme(roundTripDir, spec.Name);
            }
            catch (Exception ex)
            {
                Fail($"重新加载失败：{ex.Message}");
                return;
            }

            List<string> diffs = CompareThemes(theme, reloaded);
            Check(diffs.Count == 0, "往返后所有字段值不变", string.Join("；", diffs));

            using JsonDocument source = JsonDocument.Parse(File.ReadAllText(path));
            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(savedPath));
            List<string> problems = new List<string>();
            CompareJson(source.RootElement, saved.RootElement, spec.Name, problems);
            Check(problems.Count == 0, "源文件显式写出的键在往返后全部保留且值相等", string.Join("；", problems));

            List<string> duplicates = FindDuplicateKeys(File.ReadAllText(savedPath));
            Check(duplicates.Count == 0, "保存结果没有重复键", string.Join("；", duplicates));
        }

        /// <summary>按最坏合成面计算对比度并对照判据表。</summary>
        private static void CheckContrast(ThemeSpec spec, Theme theme)
        {
            Section($"{spec.Name}：可读性判据");

            ThemeAppearanceOptions options = ThemeAppearanceOptions.Create(theme, 1.0f, 1.0f, new Rectangle(0, 0, 240, 788));

            Color textTitle = ThemeManager.ParseColor(theme.Color.TextTitle);
            Color textGroup = ThemeManager.ParseColor(theme.Color.TextGroup);
            Color textPrimary = ThemeManager.ParseColor(theme.Color.TextPrimary);
            Color textSecondary = ThemeManager.ParseColor(theme.Color.TextSecondary);
            Color valueSafe = ThemeManager.ParseColor(theme.Color.ValueSafe);
            Color valueWarn = ThemeManager.ParseColor(theme.Color.ValueWarn);
            Color valueCrit = ThemeManager.ParseColor(theme.Color.ValueCrit);
            Color barBackground = ThemeManager.ParseColor(theme.Color.BarBackground);
            Color barLow = ThemeManager.ParseColor(theme.Color.BarLow);
            Color barMid = ThemeManager.ParseColor(theme.Color.BarMid);
            Color barHigh = ThemeManager.ParseColor(theme.Color.BarHigh);
            Color groupBackground = options.GroupBackgroundColor;
            Color background = options.BackgroundColor;

            (double titleRatio, Color titleSurface) = WorstAgainst(options, textTitle);
            (double groupRatio, Color groupSurface) = WorstAgainst(options, textGroup);

            List<string> rows = new List<string>();

            Console.WriteLine($"    背景承载面（最坏合成）= {ContrastModel.ToHex(titleSurface)} / {ContrastModel.ToHex(groupSurface)}");
            Console.WriteLine($"    组块承载面 = {ContrastModel.ToHex(groupBackground)}；进度条槽 = {ContrastModel.ToHex(barBackground)}");

            CheckText(rows, titleRatio, "textTitle vs 背景承载面", textTitle, titleSurface);
            CheckText(rows, groupRatio, "textGroup vs 背景承载面", textGroup, groupSurface);

            CheckText(rows, ContrastModel.ContrastRatio(textPrimary, groupBackground), "textPrimary vs 组块承载面", textPrimary, groupBackground);
            CheckText(rows, ContrastModel.ContrastRatio(textSecondary, groupBackground), "textSecondary vs 组块承载面", textSecondary, groupBackground);
            CheckText(rows, ContrastModel.ContrastRatio(valueSafe, groupBackground), "valueSafe vs 组块承载面", valueSafe, groupBackground);
            CheckText(rows, ContrastModel.ContrastRatio(valueWarn, groupBackground), "valueWarn vs 组块承载面", valueWarn, groupBackground);
            CheckText(rows, ContrastModel.ContrastRatio(valueCrit, groupBackground), "valueCrit vs 组块承载面", valueCrit, groupBackground);

            CheckGraphic(rows, ContrastModel.ContrastRatio(barLow, barBackground), "barLow vs 进度条槽", barLow, barBackground);
            CheckGraphic(rows, ContrastModel.ContrastRatio(barMid, barBackground), "barMid vs 进度条槽", barMid, barBackground);
            CheckGraphic(rows, ContrastModel.ContrastRatio(barHigh, barBackground), "barHigh vs 进度条槽", barHigh, barBackground);

            CheckGraphic(rows, ContrastModel.ContrastRatio(options.BorderColor, titleSurface), "border vs 背景承载面", options.BorderColor, titleSurface);

            double separation = ContrastModel.ContrastRatio(groupBackground, background);
            Check(separation >= MinGroupSeparation,
                "组块面 vs 窗口底可分性",
                $"{ContrastModel.ToHex(groupBackground)} vs {ContrastModel.ToHex(background)} = {separation:F3}（判据 ≥ {MinGroupSeparation:F1}）");
            rows.Add($"组块面 vs 窗口底可分性: {ContrastModel.ToHex(groupBackground)} vs {ContrastModel.ToHex(background)} = {separation:F3}（判据 ≥ {MinGroupSeparation:F1}）");

            Console.WriteLine("    判据实测值：");
            foreach (string row in rows)
                Console.WriteLine("      " + row);
        }

        // ============================================================
        // 辅助
        // ============================================================

        /// <summary>在渐变两端中挑出对给定文字色更不利的一面，并返回对比度。</summary>
        private static (double Ratio, Color Surface) WorstAgainst(ThemeAppearanceOptions options, Color text)
        {
            Color fromStart = ContrastModel.BuildWorstCaseSurface(options, options.GradientStartColor);
            Color fromEnd = ContrastModel.BuildWorstCaseSurface(options, options.GradientEndColor);
            double r1 = ContrastModel.ContrastRatio(text, fromStart);
            double r2 = ContrastModel.ContrastRatio(text, fromEnd);
            return r1 <= r2 ? (r1, fromStart) : (r2, fromEnd);
        }

        /// <summary>文本类对比度判据（≥ 4.5:1）。</summary>
        private static void CheckText(List<string> rows, double ratio, string label, Color fg, Color bg)
        {
            string detail = $"{ContrastModel.ToHex(fg)} vs {ContrastModel.ToHex(bg)} = {ratio:F2}（判据 ≥ {MinTextContrast:F1}）";
            rows.Add($"{label}: {detail}");
            Check(ratio >= MinTextContrast, label, detail);
        }

        /// <summary>非文本类对比度判据（≥ 3:1）。</summary>
        private static void CheckGraphic(List<string> rows, double ratio, string label, Color fg, Color bg)
        {
            string detail = $"{ContrastModel.ToHex(fg)} vs {ContrastModel.ToHex(bg)} = {ratio:F2}（判据 ≥ {MinGraphicContrast:F1}）";
            rows.Add($"{label}: {detail}");
            Check(ratio >= MinGraphicContrast, label, detail);
        }

        /// <summary>把任意值转成稳定的比较字符串。</summary>
        private static string FormatValue(object? value)
        {
            return value switch
            {
                null => "<null>",
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                float f => f.ToString("R", CultureInfo.InvariantCulture),
                bool b => b.ToString(),
                _ => value.ToString() ?? "<null>",
            };
        }

        /// <summary>读取 ColorConfig 里指定键的原始字符串。</summary>
        private static string? GetColorValue(ColorConfig color, string key)
        {
            return key.ToLowerInvariant() switch
            {
                "background" => color.Background,
                "groupbackground" => color.GroupBackground,
                "border" => color.Border,
                "shadow" => color.Shadow,
                "texttitle" => color.TextTitle,
                "textgroup" => color.TextGroup,
                "textprimary" => color.TextPrimary,
                "textsecondary" => color.TextSecondary,
                "valuesafe" => color.ValueSafe,
                "valuewarn" => color.ValueWarn,
                "valuecrit" => color.ValueCrit,
                "barbackground" => color.BarBackground,
                "barlow" => color.BarLow,
                "barmid" => color.BarMid,
                "barhigh" => color.BarHigh,
                _ => null,
            };
        }

        /// <summary>比较两个颜色的 RGBA 是否相等（两边都走生产用的 ThemeManager → ParseColor）。</summary>
        private static void CheckColor(string expected, string? actual, string label)
        {
            if (actual is null)
            {
                Fail($"{label} 缺失（期望 {expected}）");
                return;
            }

            Color a = ThemeManager.ParseColor(expected);
            Color b = ThemeManager.ParseColor(actual);
            bool same = a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;
            Check(same, $"{label} = {actual}", $"期望 {expected}");
        }

        /// <summary>把主题的所有已知字段拍平成字符串，用于检测"归一化是否回写了原对象"。</summary>
        private static string Snapshot(Theme t)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(t.Version).Append('|').Append(t.Name).Append('|');

            LayoutConfig l = t.Layout;
            sb.Append(l.Width).Append(',').Append(l.LayoutScale.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(l.RowHeight).Append(',').Append(l.Padding).Append(',').Append(l.CornerRadius).Append(',')
              .Append(l.GroupRadius).Append(',').Append(l.GroupPadding).Append(',').Append(l.GroupSpacing).Append(',')
              .Append(l.GroupBottom).Append(',').Append(l.ItemGap).Append(',').Append(l.GroupTitleOffset).Append(',')
              .Append(l.FrameStyle).Append(',').Append(l.FrameWidth).Append(',').Append(l.FrameLength).Append(',')
              .Append(l.Scanline).Append(',').Append(l.Glow).Append(',').Append(l.Grid).Append(',').Append(l.BarStyle)
              .Append('|');

            FontConfig f = t.Font;
            sb.Append(f.Family).Append(',').Append(f.ValueFamily).Append(',')
              .Append(f.Title.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.Group.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.Item.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.Value.ToString(CultureInfo.InvariantCulture)).Append(',').Append(f.Bold).Append('|');

            ColorConfig c = t.Color;
            sb.Append(c.Background).Append(',').Append(c.GroupBackground).Append(',').Append(c.Border).Append(',')
              .Append(c.Shadow).Append(',').Append(c.TextTitle).Append(',').Append(c.TextGroup).Append(',')
              .Append(c.TextPrimary).Append(',').Append(c.TextSecondary).Append(',').Append(c.ValueSafe).Append(',')
              .Append(c.ValueWarn).Append(',').Append(c.ValueCrit).Append(',').Append(c.BarBackground).Append(',')
              .Append(c.BarLow).Append(',').Append(c.BarMid).Append(',').Append(c.BarHigh).Append('|');

            ThresholdConfig th = t.Thresholds;
            sb.Append(th.Load.Warn).Append(',').Append(th.Load.Crit).Append(',')
              .Append(th.Temp.Warn).Append(',').Append(th.Temp.Crit).Append(',')
              .Append(th.Vram.Warn).Append(',').Append(th.Vram.Crit).Append(',')
              .Append(th.Mem.Warn).Append(',').Append(th.Mem.Crit).Append(',')
              .Append(th.NetKBps.Warn).Append(',').Append(th.NetKBps.Crit).Append('|');

            if (t.Backdrop is null)
            {
                sb.Append("backdrop=null");
            }
            else
            {
                BackdropConfig b = t.Backdrop;
                sb.Append(b.Mode).Append(',').Append(b.Opacity.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(b.Blur.ToString(CultureInfo.InvariantCulture)).Append(',').Append(b.Tint).Append(',')
                  .Append(b.GradientStart).Append(',').Append(b.GradientEnd).Append(',')
                  .Append(b.GradientAngle.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(b.Noise).Append(',').Append(b.EdgeGlow).Append(',').Append(b.Fps);
            }

            return sb.ToString();
        }

        /// <summary>比较两个主题对象的已知字段，返回差异描述列表。</summary>
        private static List<string> CompareThemes(Theme a, Theme b)
        {
            List<string> diffs = new List<string>();

            void Eq(string label, object? x, object? y)
            {
                string sx = FormatValue(x);
                string sy = FormatValue(y);
                if (!string.Equals(sx, sy, StringComparison.Ordinal))
                    diffs.Add($"{label}: {sx} → {sy}");
            }
            Eq("version", a.Version.ToString(CultureInfo.InvariantCulture), b.Version.ToString(CultureInfo.InvariantCulture));
            Eq("name", a.Name, b.Name);

            Eq("layout.width", a.Layout.Width.ToString(CultureInfo.InvariantCulture), b.Layout.Width.ToString(CultureInfo.InvariantCulture));
            Eq("layout.layoutScale", a.Layout.LayoutScale, b.Layout.LayoutScale);
            Eq("layout.rowHeight", a.Layout.RowHeight.ToString(CultureInfo.InvariantCulture), b.Layout.RowHeight.ToString(CultureInfo.InvariantCulture));
            Eq("layout.padding", a.Layout.Padding.ToString(CultureInfo.InvariantCulture), b.Layout.Padding.ToString(CultureInfo.InvariantCulture));
            Eq("layout.cornerRadius", a.Layout.CornerRadius.ToString(CultureInfo.InvariantCulture), b.Layout.CornerRadius.ToString(CultureInfo.InvariantCulture));
            Eq("layout.groupRadius", a.Layout.GroupRadius.ToString(CultureInfo.InvariantCulture), b.Layout.GroupRadius.ToString(CultureInfo.InvariantCulture));
            Eq("layout.groupPadding", a.Layout.GroupPadding.ToString(CultureInfo.InvariantCulture), b.Layout.GroupPadding.ToString(CultureInfo.InvariantCulture));
            Eq("layout.groupSpacing", a.Layout.GroupSpacing.ToString(CultureInfo.InvariantCulture), b.Layout.GroupSpacing.ToString(CultureInfo.InvariantCulture));
            Eq("layout.groupBottom", a.Layout.GroupBottom.ToString(CultureInfo.InvariantCulture), b.Layout.GroupBottom.ToString(CultureInfo.InvariantCulture));
            Eq("layout.itemGap", a.Layout.ItemGap.ToString(CultureInfo.InvariantCulture), b.Layout.ItemGap.ToString(CultureInfo.InvariantCulture));
            Eq("layout.groupTitleOffset", a.Layout.GroupTitleOffset.ToString(CultureInfo.InvariantCulture), b.Layout.GroupTitleOffset.ToString(CultureInfo.InvariantCulture));
            Eq("layout.frameStyle", a.Layout.FrameStyle.ToString(CultureInfo.InvariantCulture), b.Layout.FrameStyle.ToString(CultureInfo.InvariantCulture));
            Eq("layout.frameWidth", a.Layout.FrameWidth.ToString(CultureInfo.InvariantCulture), b.Layout.FrameWidth.ToString(CultureInfo.InvariantCulture));
            Eq("layout.frameLength", a.Layout.FrameLength.ToString(CultureInfo.InvariantCulture), b.Layout.FrameLength.ToString(CultureInfo.InvariantCulture));
            Eq("layout.scanline", a.Layout.Scanline.ToString(CultureInfo.InvariantCulture), b.Layout.Scanline.ToString(CultureInfo.InvariantCulture));
            Eq("layout.glow", a.Layout.Glow.ToString(CultureInfo.InvariantCulture), b.Layout.Glow.ToString(CultureInfo.InvariantCulture));
            Eq("layout.grid", a.Layout.Grid.ToString(CultureInfo.InvariantCulture), b.Layout.Grid.ToString(CultureInfo.InvariantCulture));
            Eq("layout.barStyle", a.Layout.BarStyle.ToString(CultureInfo.InvariantCulture), b.Layout.BarStyle.ToString(CultureInfo.InvariantCulture));

            Eq("font.family", a.Font.Family, b.Font.Family);
            Eq("font.valueFamily", a.Font.ValueFamily, b.Font.ValueFamily);
            Eq("font.title", a.Font.Title, b.Font.Title);
            Eq("font.group", a.Font.Group, b.Font.Group);
            Eq("font.item", a.Font.Item, b.Font.Item);
            Eq("font.value", a.Font.Value, b.Font.Value);
            Eq("font.bold", a.Font.Bold.ToString(), b.Font.Bold.ToString());

            foreach (string key in new[]
            {
                "Background", "GroupBackground", "Border", "Shadow", "TextTitle", "TextGroup", "TextPrimary",
                "TextSecondary", "ValueSafe", "ValueWarn", "ValueCrit", "BarBackground", "BarLow", "BarMid", "BarHigh"
            })
            {
                Eq("color." + key, GetColorValue(a.Color, key), GetColorValue(b.Color, key));
            }

            Eq("thresholds.load.warn", a.Thresholds.Load.Warn, b.Thresholds.Load.Warn);
            Eq("thresholds.load.crit", a.Thresholds.Load.Crit, b.Thresholds.Load.Crit);
            Eq("thresholds.temp.warn", a.Thresholds.Temp.Warn, b.Thresholds.Temp.Warn);
            Eq("thresholds.temp.crit", a.Thresholds.Temp.Crit, b.Thresholds.Temp.Crit);
            Eq("thresholds.vram.warn", a.Thresholds.Vram.Warn, b.Thresholds.Vram.Warn);
            Eq("thresholds.vram.crit", a.Thresholds.Vram.Crit, b.Thresholds.Vram.Crit);
            Eq("thresholds.mem.warn", a.Thresholds.Mem.Warn, b.Thresholds.Mem.Warn);
            Eq("thresholds.mem.crit", a.Thresholds.Mem.Crit, b.Thresholds.Mem.Crit);
            Eq("thresholds.netKBps.warn", a.Thresholds.NetKBps.Warn, b.Thresholds.NetKBps.Warn);
            Eq("thresholds.netKBps.crit", a.Thresholds.NetKBps.Crit, b.Thresholds.NetKBps.Crit);

            if (a.Backdrop is null || b.Backdrop is null)
            {
                Eq("backdrop", a.Backdrop?.Mode ?? "<null>", b.Backdrop?.Mode ?? "<null>");
                return diffs;
            }

            Eq("backdrop.mode", a.Backdrop.Mode, b.Backdrop.Mode);
            Eq("backdrop.opacity", a.Backdrop.Opacity, b.Backdrop.Opacity);
            Eq("backdrop.blur", a.Backdrop.Blur, b.Backdrop.Blur);
            Eq("backdrop.tint", a.Backdrop.Tint, b.Backdrop.Tint);
            Eq("backdrop.gradientStart", a.Backdrop.GradientStart, b.Backdrop.GradientStart);
            Eq("backdrop.gradientEnd", a.Backdrop.GradientEnd, b.Backdrop.GradientEnd);
            Eq("backdrop.gradientAngle", a.Backdrop.GradientAngle, b.Backdrop.GradientAngle);
            Eq("backdrop.noise", a.Backdrop.Noise.ToString(CultureInfo.InvariantCulture), b.Backdrop.Noise.ToString(CultureInfo.InvariantCulture));
            Eq("backdrop.edgeGlow", a.Backdrop.EdgeGlow.ToString(CultureInfo.InvariantCulture), b.Backdrop.EdgeGlow.ToString(CultureInfo.InvariantCulture));
            Eq("backdrop.fps", a.Backdrop.Fps.ToString(CultureInfo.InvariantCulture), b.Backdrop.Fps.ToString(CultureInfo.InvariantCulture));

            return diffs;
        }

        /// <summary>比较两个 JSON 树：源里显式写出的键必须在下游存在且值相等（允许下游多出默认物化出来的键）。</summary>
        private static void CompareJson(JsonElement src, JsonElement dst, string path, List<string> problems)
        {
            if (src.ValueKind == JsonValueKind.Object && dst.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in src.EnumerateObject())
                {
                    if (!TryGetProperty(dst, property.Name, out JsonElement child))
                    {
                        problems.Add($"{path}.{property.Name} 丢失");
                        continue;
                    }
                    CompareJson(property.Value, child, path + "." + property.Name, problems);
                }
                return;
            }

            if (src.ValueKind == JsonValueKind.Array && dst.ValueKind == JsonValueKind.Array)
            {
                if (src.GetArrayLength() != dst.GetArrayLength())
                {
                    problems.Add($"{path} 数组长度不一致");
                    return;
                }
                int index = 0;
                foreach (JsonElement item in src.EnumerateArray())
                {
                    CompareJson(item, dst[index], $"{path}[{index}]", problems);
                    index++;
                }
                return;
            }

            if (!JsonValueEquals(src, dst))
                problems.Add($"{path}: {src.GetRawText()} → {dst.GetRawText()}");
        }

        /// <summary>JSON 值相等（数字按数值比较，其余按原始文本比较）。</summary>
        private static bool JsonValueEquals(JsonElement a, JsonElement b)
        {
            if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
                return Math.Abs(a.GetDouble() - b.GetDouble()) < 1e-9;
            if (a.ValueKind != b.ValueKind) return false;
            return string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal);
        }

        /// <summary>大小写不敏感地取属性。</summary>
        private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
        {
            if (obj.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in obj.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }
            value = default;
            return false;
        }

        /// <summary>找出 JSON 里重复出现的属性名（同一对象内，忽略大小写）。</summary>
        private static List<string> FindDuplicateKeys(string json)
        {
            List<string> duplicates = new List<string>();
            Stack<HashSet<string>> stack = new Stack<HashSet<string>>();
            try
            {
                Utf8JsonReader reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        stack.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    }
                    else if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        if (stack.Count > 0) stack.Pop();
                    }
                    else if (reader.TokenType == JsonTokenType.PropertyName && stack.Count > 0)
                    {
                        string name = reader.GetString() ?? string.Empty;
                        if (!stack.Peek().Add(name)) duplicates.Add(name);
                    }
                }
            }
            catch (JsonException)
            {
                duplicates.Add("JSON 解析失败");
            }
            return duplicates;
        }

        /// <summary>计算文件 SHA256 的前 N 位十六进制（大写）。</summary>
        private static string Sha256Prefix(string path, int length)
        {
            using FileStream stream = File.OpenRead(path);
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < hash.Length && sb.Length < length; i++)
                sb.Append(hash[i].ToString("X2", CultureInfo.InvariantCulture));
            return sb.ToString(0, Math.Min(length, sb.Length));
        }

        /// <summary>从程序所在目录向上找仓库根目录。</summary>
        private static string? LocateRepoRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                bool hasProject = File.Exists(Path.Combine(dir.FullName, "LiteMonitor.csproj"));
                bool hasThemes = Directory.Exists(Path.Combine(dir.FullName, "resources", "themes"));
                if (hasProject && hasThemes) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>打印一个分组标题。</summary>
        internal static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title + " ==");
        }

        /// <summary>记录一次通过。</summary>
        internal static void Check(bool condition, string label, string? detail = null)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("  [PASS] " + label);
                return;
            }

            _failed++;
            Console.WriteLine("  [FAIL] " + label + (detail is null ? "" : " —— " + detail));
        }

        /// <summary>记录一次失败（没有对照值）。</summary>
        internal static void Fail(string message)
        {
            _failed++;
            Console.WriteLine("  [FAIL] " + message);
        }
    }
}
