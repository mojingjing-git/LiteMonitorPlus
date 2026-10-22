using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 主题编辑器与预览接入测试（外观改造计划 A10 / v4 §3.1、§3.2、§7.1）。
    ///
    /// ★ 写入位置：涉及文件读写的用例只在<b>系统临时目录</b>下的独立子目录里进行；
    ///   另有两个用例直接对比默认主题目录（ThemeManager.ThemeDir）的前后快照，
    ///   证明"打开编辑器 / 切换预览主题 / 关闭编辑器"不会写盘。
    ///
    /// ★ 覆盖的用例：
    ///   1. 预览副本逐字段相等（含 7 个新外观字段与 Backdrop），扩展数据不丢；
    ///   2. 已缩放的源不会被二次缩放；
    ///   3. 字体只借用、不释放；
    ///   4. alpha 语义（0.5 → 128 / 1 → 255 / 0 → 0，非法值被拒绝并回退）；
    ///   5. 取消：磁盘文件逐字节不变（SHA256），内存主题恢复原状；
    ///   6. 恢复缺省：默认键不再写进 JSON；
    ///   7. A → B 换主题只改 B；
    ///   8. 初始化 / 切换预览主题 / 关闭编辑器零写盘；
    ///   9. 预览路径不含任何抓屏调用；
    ///  10. 现有旧主题在预览里能正常渲染。
    /// </summary>
    public sealed class ThemeEditorAppearanceTests : IDisposable
    {
        /// <summary>每个用例一个独立临时目录，用例结束即删除。</summary>
        private readonly string _dir;

        public ThemeEditorAppearanceTests()
        {
            _dir = Path.Combine(
                Path.GetTempPath(), "LiteMonitor.Tests", "ThemeEditorAppearanceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
            }
            catch
            {
                // 临时目录清理失败不影响测试结论
            }
        }

        // ================================================================
        // 1. 预览副本正确
        // ================================================================

        [Fact]
        public void 预览副本_布局逐字段相等且含全部新外观字段()
        {
            Theme theme = NewThemeWithAppearance();

            Theme preview = ThemePreviewControl.CreatePreviewTheme(theme, 1.0f);

            Assert.NotNull(preview.Layout);

            // 7 个新外观字段逐个相等
            Assert.Equal(theme.Layout.FrameStyle, preview.Layout.FrameStyle);
            Assert.Equal(theme.Layout.FrameWidth, preview.Layout.FrameWidth);
            Assert.Equal(theme.Layout.FrameLength, preview.Layout.FrameLength);
            Assert.Equal(theme.Layout.Scanline, preview.Layout.Scanline);
            Assert.Equal(theme.Layout.Glow, preview.Layout.Glow);
            Assert.Equal(theme.Layout.Grid, preview.Layout.Grid);
            Assert.Equal(theme.Layout.BarStyle, preview.Layout.BarStyle);

            // ★ 反射逐属性比对：以后新增 Layout 属性漏拷时这个用例会失败
            foreach (PropertyInfo p in typeof(LayoutConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name == nameof(LayoutConfig.ExtensionData)) continue;
                object expected = p.GetValue(theme.Layout);
                object actual = p.GetValue(preview.Layout);
                Assert.True(Equals(expected, actual),
                    $"布局属性 {p.Name} 在预览副本里不一致：期望 {expected}，实际 {actual}");
            }

            // Backdrop 逐字段相等
            Assert.NotNull(preview.Backdrop);
            Assert.Equal(theme.Backdrop!.Mode, preview.Backdrop!.Mode);
            Assert.Equal(theme.Backdrop.Opacity, preview.Backdrop.Opacity);
            Assert.Equal(theme.Backdrop.Blur, preview.Backdrop.Blur);
            Assert.Equal(theme.Backdrop.Tint, preview.Backdrop.Tint);
            Assert.Equal(theme.Backdrop.GradientStart, preview.Backdrop.GradientStart);
            Assert.Equal(theme.Backdrop.GradientEnd, preview.Backdrop.GradientEnd);
            Assert.Equal(theme.Backdrop.GradientAngle, preview.Backdrop.GradientAngle);
            Assert.Equal(theme.Backdrop.Noise, preview.Backdrop.Noise);
            Assert.Equal(theme.Backdrop.EdgeGlow, preview.Backdrop.EdgeGlow);
            Assert.Equal(theme.Backdrop.Fps, preview.Backdrop.Fps);

            // 颜色 / 阈值只读借用（同一个对象，预览不会修改它们）
            Assert.Same(theme.Color, preview.Color);
            Assert.Same(theme.Thresholds, preview.Thresholds);
        }

        [Fact]
        public void 预览副本_扩展数据不丢()
        {
            Theme theme = NewThemeWithAppearance();

            Theme preview = ThemePreviewControl.CreatePreviewTheme(theme, 1.0f);

            // 布局层未知键
            Assert.True(preview.Layout.ExtensionData.ContainsKey("customLayoutKey"));
            Assert.Equal("keep-me", preview.Layout.ExtensionData["customLayoutKey"].GetString());

            // 主题层未知键
            Assert.True(preview.ExtensionData.ContainsKey("customThemeKey"));
            Assert.Equal("keep-too", preview.ExtensionData["customThemeKey"].GetString());
        }

        // ================================================================
        // 2. 只缩放一次
        // ================================================================

        [Fact]
        public void 预览副本_已缩放的源不会被二次缩放()
        {
            Theme theme = NewThemeWithAppearance();

            // 模拟运行时已经按 1.5 缩放过一次
            theme.Layout.Scale(1.5f);
            theme.Layout.LayoutScale = 1.5f;
            int rowHeightAfterFirstScale = theme.Layout.RowHeight;

            Theme preview = ThemePreviewControl.CreatePreviewTheme(theme, 2.0f);

            // LayoutScale 语义保持"已缩放"，不再乘 2.0
            Assert.Equal(1.5f, preview.Layout.LayoutScale);
            Assert.Equal(rowHeightAfterFirstScale, preview.Layout.RowHeight);
            Assert.Equal(theme.Layout.Padding, preview.Layout.Padding);
            Assert.Equal(theme.Layout.GroupSpacing, preview.Layout.GroupSpacing);

            // 新外观字段本来就不参与 Layout.Scale，任何情况下都保持原值
            Assert.Equal(theme.Layout.FrameWidth, preview.Layout.FrameWidth);
            Assert.Equal(theme.Layout.FrameLength, preview.Layout.FrameLength);
        }

        [Fact]
        public void 预览副本_未缩放的源只缩放一次()
        {
            Theme theme = NewThemeWithAppearance();

            Theme preview = ThemePreviewControl.CreatePreviewTheme(theme, 1.5f);

            Assert.Equal(1.5f, preview.Layout.LayoutScale);
            Assert.Equal((int)(theme.Layout.RowHeight * 1.5f), preview.Layout.RowHeight);
            // 新尺寸字段不缩放（由 ThemeAppearanceOptions 统一换算）
            Assert.Equal(theme.Layout.FrameWidth, preview.Layout.FrameWidth);
        }

        // ================================================================
        // 3. 字体只借用
        // ================================================================

        [Fact]
        public void 预览副本_字体只借用不释放()
        {
            Theme theme = NewThemeWithAppearance();
            theme.BuildFonts();

            Font original = theme.FontTitle;
            Assert.NotNull(original);

            Theme preview = ThemePreviewControl.CreatePreviewTheme(theme, 1.0f);

            // 预览主题持有的是同一个字体对象（借用）
            Assert.Same(original, preview.FontTitle);
            Assert.Same(theme.FontGroup, preview.FontGroup);
            Assert.Same(theme.FontItem, preview.FontItem);
            Assert.Same(theme.FontValue, preview.FontValue);

            // 预览副本被丢弃后，原主题的字体仍然可用
            Assert.True(original.Height > 0);
            Assert.Equal(theme.FontTitle.FontFamily.Name, original.FontFamily.Name);
        }

        [Fact]
        public void 预览控件_释放后不释放原主题字体()
        {
            Theme theme = NewThemeWithAppearance();
            theme.BuildFonts();
            Font original = theme.FontTitle;

            RunSta(() =>
            {
                var preview = new ThemePreviewControl();
                preview.SetTheme(theme);
                preview.Dispose();
            });

            // 预览控件释放后，原主题的字体仍然可用（没有被连带 Dispose）
            Assert.True(original.Height > 0);
            Assert.Equal(theme.FontTitle.FontFamily.Name, original.FontFamily.Name);
        }

        // ================================================================
        // 4. alpha 语义
        // ================================================================

        [Theory]
        [InlineData(0.5, 128)]
        [InlineData(1.0, 255)]
        [InlineData(0.0, 0)]
        [InlineData(0.25, 64)]
        [InlineData(0.1, 26)]
        public void alpha_写回后ParseColor得到的alpha字节正确(double alpha, int expectedByte)
        {
            Assert.True(AppearanceColorText.TrySetAlpha("#203040", alpha, out string text));

            Color parsed = ThemeManager.ParseColor(text);

            Assert.Equal(expectedByte, parsed.A);
            // RGB 不变
            Assert.Equal(0x20, parsed.R);
            Assert.Equal(0x30, parsed.G);
            Assert.Equal(0x40, parsed.B);
        }

        [Fact]
        public void alpha_1写成六位十六进制_0写成rgba()
        {
            Assert.True(AppearanceColorText.TrySetAlpha("#203040", 1.0, out string opaque));
            Assert.Equal("#203040", opaque);

            Assert.True(AppearanceColorText.TrySetAlpha("#203040", 0.0, out string transparent));
            Assert.Equal("rgba(32,48,64,0)", transparent);
            Assert.Equal(0, ThemeManager.ParseColor(transparent).A);
        }

        [Theory]
        [InlineData(-0.1)]
        [InlineData(1.1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void alpha_非法值被拒绝并回退(double alpha)
        {
            const string original = "#203040";

            bool ok = AppearanceColorText.TrySetAlpha(original, alpha, out string text);

            Assert.False(ok);
            // 回退：颜色文本保持原样，不产生半合法的颜色
            Assert.Equal(original, text);
        }

        [Fact]
        public void alpha_颜色文本本身非法时同样被拒绝()
        {
            const string broken = "rgba(300,0,0,0.5)";

            bool ok = AppearanceColorText.TrySetAlpha(broken, 0.5, out string text);

            Assert.False(ok);
            Assert.Equal(broken, text);
        }

        [Fact]
        public void alpha_保存后读回仍是128()
        {
            Theme theme = NewThemeWithAppearance();
            Assert.True(AppearanceColorText.TrySetAlpha("#203040", 0.5, out string text));
            theme.Color.Background = text;

            ThemeFileService.SaveTheme(_dir, "Alpha", theme);
            Theme loaded = ThemeFileService.LoadTheme(_dir, "Alpha");

            Color parsed = ThemeManager.ParseColor(loaded.Color.Background);

            Assert.Equal(128, parsed.A);
            Assert.Equal(0x20, parsed.R);
            Assert.Equal(0x30, parsed.G);
            Assert.Equal(0x40, parsed.B);
        }

        [Fact]
        public void alpha_恢复缺省后颜色键不再写出()
        {
            Theme theme = NewThemeWithAppearance();
            Assert.True(AppearanceColorText.TrySetAlpha("#203040", 0.5, out string text));
            theme.Color.Border = text;

            ThemeFileService.SaveTheme(_dir, "AlphaReset", theme);
            string json = File.ReadAllText(Path.Combine(_dir, "AlphaReset.json"));
            Assert.Contains("border", json, StringComparison.OrdinalIgnoreCase);

            // 恢复缺省 = null（可空属性，缺省不写出）
            theme.Color.Border = null;
            ThemeFileService.SaveTheme(_dir, "AlphaReset", theme);
            json = File.ReadAllText(Path.Combine(_dir, "AlphaReset.json"));
            Assert.DoesNotContain("border", json, StringComparison.OrdinalIgnoreCase);
        }

        // ================================================================
        // 5. 取消
        // ================================================================

        [Fact]
        public void 取消_磁盘文件逐字节不变且内存主题恢复原状()
        {
            Theme theme = NewThemeWithAppearance();
            ThemeFileService.SaveTheme(_dir, "Cancel", theme);

            string path = Path.Combine(_dir, "Cancel.json");
            string hashBefore = Sha256OfFile(path);

            // 进入编辑器：加载 + 记快照
            Theme loaded = ThemeFileService.LoadTheme(_dir, "Cancel");
            Theme pristine = AppearanceThemeConfig.CloneConfig(loaded);

            // 改若干字段
            loaded.Layout.FrameStyle = 3;
            loaded.Layout.FrameWidth = 4;
            loaded.Layout.Scanline = 50;
            loaded.Color.Background = "rgba(1,2,3,0.5)";
            loaded.Color.Border = "#010203";
            loaded.Backdrop!.Opacity = 0.1;
            loaded.Backdrop.Mode = "glass";

            // 取消 → 内存中的主题恢复原状
            AppearanceThemeConfig.RestoreConfig(loaded, pristine);

            Assert.Equal(2, loaded.Layout.FrameStyle);
            Assert.Equal(2, loaded.Layout.FrameWidth);
            Assert.Equal(30, loaded.Layout.Scanline);
            Assert.Equal(NewThemeWithAppearance().Color.Background, loaded.Color.Background);
            Assert.Equal("#40FF80", loaded.Color.Border);
            Assert.Equal(NewThemeWithAppearance().Backdrop!.Opacity, loaded.Backdrop!.Opacity);
            Assert.Equal("gradient", loaded.Backdrop.Mode);

            // ★ 磁盘文件逐字节不变
            Assert.Equal(hashBefore, Sha256OfFile(path));
        }

        [Fact]
        public void 取消_编辑器关闭后内存主题恢复原状()
        {
            Theme theme = NewThemeWithAppearance();
            ThemeFileService.SaveTheme(_dir, "CancelForm", theme);

            string path = Path.Combine(_dir, "CancelForm.json");
            string hashBefore = Sha256OfFile(path);

            RunSta(() =>
            {
                // 进入编辑器：加载主题并记快照
                var fields = new AppearanceFields(1.0f);
                fields.Bind(theme);
                Theme pristine = AppearanceThemeConfig.CloneConfig(theme);

                // 模拟用户改了几个字段（不保存）
                theme.Layout.FrameStyle = 3;
                theme.Color.Background = "rgba(9,9,9,0.25)";

                // 取消 → 内存中的主题恢复原状
                AppearanceThemeConfig.RestoreConfig(theme, pristine);

                Assert.Equal(2, theme.Layout.FrameStyle);
                Assert.Equal(NewThemeWithAppearance().Color.Background, theme.Color.Background);
            });

            // ★ 磁盘文件逐字节不变
            Assert.Equal(hashBefore, Sha256OfFile(path));
        }

        // ================================================================
        // 6. 恢复缺省
        // ================================================================

        [Fact]
        public void 恢复缺省_写回值与规范一致且默认键不再写出()
        {
            Theme theme = NewThemeWithAppearance();

            RunSta(() =>
            {
                var fields = new AppearanceFields(1.0f);
                fields.Bind(theme);
                fields.ResetAllToDefaults();
            });

            // 写回的值 = v4 §3.1 的缺省
            Assert.Equal(0, theme.Layout.FrameStyle);
            Assert.Equal(0, theme.Layout.FrameWidth);
            Assert.Equal(0, theme.Layout.FrameLength);
            Assert.Equal(0, theme.Layout.Scanline);
            Assert.Equal(0, theme.Layout.Glow);
            Assert.Equal(0, theme.Layout.Grid);
            Assert.Equal(0, theme.Layout.BarStyle);
            Assert.Null(theme.Backdrop);
            Assert.Null(theme.Color.Border);
            Assert.Null(theme.Color.Shadow);
            Assert.Null(theme.Color.TextSecondary);

            // ★ 文件层面：缺省键不再出现（WhenWritingDefault / WhenWritingNull）
            ThemeFileService.SaveTheme(_dir, "Defaults", theme);
            string json = File.ReadAllText(Path.Combine(_dir, "Defaults.json"));

            Assert.DoesNotContain("frameStyle", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("frameWidth", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("frameLength", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("scanline", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("glow", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("grid", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("barStyle", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("backdrop", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("border", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("shadow", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("textSecondary", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void 恢复缺省_单字段恢复只影响该字段()
        {
            Theme theme = NewThemeWithAppearance();

            RunSta(() =>
            {
                var fields = new AppearanceFields(1.0f);
                fields.Bind(theme);
                fields.ResetField(AppearanceFields.KeyFrameStyle);
            });

            Assert.Equal(0, theme.Layout.FrameStyle);
            // 其它字段保持原值
            Assert.Equal(NewThemeWithAppearance().Layout.FrameWidth, theme.Layout.FrameWidth);
            Assert.Equal(NewThemeWithAppearance().Layout.Scanline, theme.Layout.Scanline);
            Assert.NotNull(theme.Backdrop);
        }

        // ================================================================
        // 7. A → B 换主题只改 B
        // ================================================================

        [Fact]
        public void 从A换到B_只修改B_A的文件逐字节不变()
        {
            Theme a = NewThemeWithAppearance();
            a.Name = "A";
            Theme b = NewThemeWithAppearance();
            b.Name = "B";

            ThemeFileService.SaveTheme(_dir, "A", a);
            ThemeFileService.SaveTheme(_dir, "B", b);

            string pathA = Path.Combine(_dir, "A.json");
            string pathB = Path.Combine(_dir, "B.json");
            string hashA = Sha256OfFile(pathA);
            string hashB = Sha256OfFile(pathB);

            // 编辑器里：载入 A → 改 A（未保存）→ 切到 B → 改 B → 保存 B
            Theme currentA = ThemeFileService.LoadTheme(_dir, "A");
            currentA.Layout.FrameStyle = 3;
            currentA.Color.Background = "#010203";

            Theme currentB = ThemeFileService.LoadTheme(_dir, "B");
            currentB.Layout.FrameStyle = 1;
            currentB.Color.Background = "#040506";
            ThemeFileService.SaveTheme(_dir, "B", currentB);

            // ★ A 的文件逐字节未变
            Assert.Equal(hashA, Sha256OfFile(pathA));

            // B 变了，且改的是 B 自己的内容
            Assert.NotEqual(hashB, Sha256OfFile(pathB));
            Theme reloadedB = ThemeFileService.LoadTheme(_dir, "B");
            Assert.Equal(1, reloadedB.Layout.FrameStyle);
            Assert.Equal("#040506", reloadedB.Color.Background);
        }

        [Fact]
        public void 从A换到B_保存A不会影响B()
        {
            Theme a = NewThemeWithAppearance();
            a.Name = "A";
            Theme b = NewThemeWithAppearance();
            b.Name = "B";

            ThemeFileService.SaveTheme(_dir, "A", a);
            ThemeFileService.SaveTheme(_dir, "B", b);

            string pathB = Path.Combine(_dir, "B.json");
            string hashB = Sha256OfFile(pathB);

            Theme currentA = ThemeFileService.LoadTheme(_dir, "A");
            currentA.Layout.FrameStyle = 2;
            ThemeFileService.SaveTheme(_dir, "A", currentA);

            Assert.Equal(hashB, Sha256OfFile(pathB));
        }

        // ================================================================
        // 8. 初始化不写回
        // ================================================================

        [Fact]
        public void 打开编辑器并切换预览主题后关闭_所有主题文件逐字节不变()
        {
            var before = SnapshotDir(ThemeManager.ThemeDir);

            RunSta(() =>
            {
                using var form = new ThemeEditorForm();
                form.Show();
                Application.DoEvents();

                // 切换预览主题（触发 LoadSelectedTheme）
                FieldInfo listField = typeof(ThemeEditorForm).GetField("lstThemes",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var list = listField?.GetValue(form) as ListBox;
                if (list != null && list.Items.Count > 1)
                {
                    list.SelectedIndex = 1;
                    Application.DoEvents();
                }

                form.Close();
                Application.DoEvents();
            });

            var after = SnapshotDir(ThemeManager.ThemeDir);

            Assert.Equal(before, after);
        }

        [Fact]
        public void 列出主题与加载主题_不会改动任何主题文件()
        {
            var before = SnapshotDir(ThemeManager.ThemeDir);

            foreach (string name in ThemeFileService.ListThemes())
                ThemeFileService.LoadTheme(name);

            var after = SnapshotDir(ThemeManager.ThemeDir);

            Assert.Equal(before, after);
        }

        // ================================================================
        // 9. 预览不启动捕获
        // ================================================================

        [Fact]
        public void 预览相关源码不含任何抓屏调用()
        {
            string previewSource = ReadRepoFile(Path.Combine("src", "ThemeEditor", "ThemePreviewControl.cs"));
            string fieldsSource = ReadRepoFile(Path.Combine("src", "ThemeEditor", "AppearanceFields.cs"));

            string[] forbidden = new[]
            {
                "SetWindowDisplayAffinity",
                "EXCLUDEFROMCAPTURE",
                "WDA_MONITOR",
                "WDA_EXCLUDEFROMCAPTURE",
                "CopyFromScreen",
                "BitBlt",
                "PrintWindow",
                "Graphics.FromHwnd",
                "System.Drawing.Graphics.CopyFromScreen",
                "System.Threading.Timer",
                "System.Windows.Forms.Timer",
                "new Timer(",
                "Task.Run",
                "Thread("
            };

            foreach (string token in forbidden)
            {
                Assert.DoesNotContain(token, previewSource, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(token, fieldsSource, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void 预览渲染_不注册实时背景后端()
        {
            // BackdropRenderer 不传任何实时帧来源 → glass 只会用固定样例图
            using var backdrop = new BackdropRenderer();

            Theme theme = NewThemeWithAppearance();
            theme.Backdrop!.Mode = "glass";

            Theme preview = ThemePreviewControl.CreatePreviewTheme(theme, 1.0f);
            using var sample = ThemePreviewControl.CreatePreviewSample();

            using var bmp = new Bitmap(240, 300);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                var groups = ThemePreviewControl.CreateMockGroups();
                new UILayout(preview).Build(groups);
                ThemePreviewControl.RenderPreview(g, groups, preview, new Rectangle(0, 0, 240, 300), backdrop, sample, 1.0f);
            }

            // 渲染成功且画面有内容（固定样例图确实被画上去了）
            Assert.True(HasVisiblePixel(bmp));
            Assert.Equal(BackdropRenderMode.Glass, backdrop.LastStatus.EffectiveMode);
        }

        // ================================================================
        // 10. 旧主题不被破坏
        // ================================================================

        [Fact]
        public void 现有旧主题在预览里能正常渲染()
        {
            string[] files = Directory.GetFiles(ThemeManager.ThemeDir, "*.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            Assert.True(files.Length >= 10, $"内置主题应有 10 个，实际 {files.Length} 个");

            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                Theme theme = ThemeFileService.LoadTheme(ThemeManager.ThemeDir, name);

                Theme preview = ThemePreviewControl.CreatePreviewTheme(theme, 1.0f);
                Assert.NotNull(preview.Layout);

                using var backdrop = new BackdropRenderer();
                using var sample = ThemePreviewControl.CreatePreviewSample();
                using var bmp = new Bitmap(240, 320);

                using (Graphics g = Graphics.FromImage(bmp))
                {
                    var groups = ThemePreviewControl.CreateMockGroups();
                    new UILayout(preview).Build(groups);
                    ThemePreviewControl.RenderPreview(g, groups, preview, new Rectangle(0, 0, 240, 320), backdrop, sample, 1.0f);
                }

                Assert.True(HasVisiblePixel(bmp), $"主题 {name} 在预览里没有画出任何内容");
            }
        }

        [Fact]
        public void 旧主题缺少新字段时预览副本补缺省值()
        {
            // 直接构造一个"没有新字段"的主题（模拟 10 个旧主题）
            var theme = new Theme { Name = "Legacy" };

            Theme preview = ThemePreviewControl.CreatePreviewTheme(theme, 1.0f);

            Assert.Equal(0, preview.Layout.FrameStyle);
            Assert.Equal(0, preview.Layout.FrameWidth);
            Assert.Equal(0, preview.Layout.FrameLength);
            Assert.Equal(0, preview.Layout.Scanline);
            Assert.Equal(0, preview.Layout.Glow);
            Assert.Equal(0, preview.Layout.Grid);
            Assert.Equal(0, preview.Layout.BarStyle);
            Assert.Null(preview.Backdrop);
        }

        // ================================================================
        // 证据图（供 A13 之前人工复核预览效果）
        // ================================================================

        [Fact]
        public void 预览渲染_产出证据图()
        {
            string evidenceDir = Path.Combine(Path.GetTempPath(), "LiteMonitor.Tests", "A10-evidence");
            Directory.CreateDirectory(evidenceDir);

            // 1) 旧主题（没有新字段）
            string legacyFile = Directory.GetFiles(ThemeManager.ThemeDir, "*.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .First();
            Theme legacy = ThemeFileService.LoadTheme(ThemeManager.ThemeDir, Path.GetFileNameWithoutExtension(legacyFile));
            string legacyPng = Path.Combine(evidenceDir, "preview-legacy.png");
            RenderPreviewPng(legacy, legacyPng, 1.0f, false);

            // 2) 全部外观字段打开 + 渐变背景
            string fullPng = Path.Combine(evidenceDir, "preview-full-appearance.png");
            RenderPreviewPng(NewThemeWithAppearance(), fullPng, 1.0f, false);

            // 3) 玻璃（固定样例图，不是实时桌面）
            Theme glass = NewThemeWithAppearance();
            glass.Backdrop!.Mode = "glass";
            string glassPng = Path.Combine(evidenceDir, "preview-glass.png");
            RenderPreviewPng(glass, glassPng, 1.0f, true);

            Assert.True(new FileInfo(legacyPng).Length > 0);
            Assert.True(new FileInfo(fullPng).Length > 0);
            Assert.True(new FileInfo(glassPng).Length > 0);

            Console.WriteLine("[A10] 预览证据图：" + evidenceDir);
        }

        private static void RenderPreviewPng(Theme theme, string pngPath, float dpiScale, bool withSample)
        {
            Theme preview = ThemePreviewControl.CreatePreviewTheme(theme, dpiScale);

            using var backdrop = new BackdropRenderer();
            using var sample = ThemePreviewControl.CreatePreviewSample();
            using var bmp = new Bitmap(240, 420);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                var groups = ThemePreviewControl.CreateMockGroups();
                int height = new UILayout(preview).Build(groups);
                var bounds = new Rectangle(0, 0, bmp.Width, bmp.Height);
                ThemePreviewControl.RenderPreview(g, groups, preview, bounds, backdrop, withSample ? sample : null, dpiScale);
            }

            bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
        }

        // ================================================================
        // 辅助
        // ================================================================

        /// <summary>
        /// 构造一个"把所有新外观字段都打开"的主题。
        /// </summary>
        private static Theme NewThemeWithAppearance()
        {
            var theme = new Theme { Name = "Appearance", Version = 4 };

            theme.Layout.FrameStyle = 2;
            theme.Layout.FrameWidth = 2;
            theme.Layout.FrameLength = 12;
            theme.Layout.Scanline = 30;
            theme.Layout.Glow = 40;
            theme.Layout.Grid = 20;
            theme.Layout.BarStyle = 1;

            theme.Color.Border = "#40FF80";
            theme.Color.Shadow = "#8040FF";
            theme.Color.TextSecondary = "#B0B0B0";

            theme.Backdrop = new BackdropConfig
            {
                Mode = "gradient",
                Opacity = 0.6,
                Blur = 8,
                Tint = "#203040",
                GradientStart = "#101010",
                GradientEnd = "#303030",
                GradientAngle = 45,
                Noise = 5,
                EdgeGlow = 6,
                Fps = 3
            };

            // 未知键（扩展数据）：预览副本必须原样保留
            theme.Layout.ExtensionData["customLayoutKey"] = JsonElementOf("keep-me");
            theme.ExtensionData["customThemeKey"] = JsonElementOf("keep-too");

            return theme;
        }

        private static System.Text.Json.JsonElement JsonElementOf(string value)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(value));
            return doc.RootElement.Clone();
        }

        /// <summary>文件的 SHA256（十六进制），用于逐字节比对。</summary>
        private static string Sha256OfFile(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }

        /// <summary>目录快照："文件名|SHA256" 列表，用于比对目录是否被改动。</summary>
        private static List<string> SnapshotDir(string dir)
        {
            var list = new List<string>();
            if (!Directory.Exists(dir)) return list;

            foreach (string f in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
                list.Add(Path.GetFileName(f) + "|" + Sha256OfFile(f));

            return list;
        }

        /// <summary>位图里是否存在非透明像素（粗采样，避免逐像素太慢）。</summary>
        private static bool HasVisiblePixel(Bitmap bmp)
        {
            for (int y = 0; y < bmp.Height; y += 3)
            {
                for (int x = 0; x < bmp.Width; x += 3)
                {
                    if (bmp.GetPixel(x, y).A > 0) return true;
                }
            }
            return false;
        }

        /// <summary>读取仓库里的源文件（从测试输出目录向上找仓库根）。</summary>
        private static string ReadRepoFile(string relativePath)
        {
            string root = FindRepoRoot();
            Assert.NotNull(root);

            string full = Path.Combine(root, relativePath);
            Assert.True(File.Exists(full), $"找不到源文件：{full}");

            return File.ReadAllText(full, Encoding.UTF8);
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "LiteMonitor.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>
        /// 在 STA 线程上执行（WinForms 控件需要 STA）。
        /// </summary>
        private static void RunSta(Action action)
        {
            Exception captured = null;

            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (captured != null)
                ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }
}
