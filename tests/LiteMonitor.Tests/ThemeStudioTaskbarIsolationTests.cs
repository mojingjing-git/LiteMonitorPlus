using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// N01（样式隔离门禁 G9）：任务栏同源预览与生产静态样式的隔离验证。
    /// <para>
    /// 危害防线：预览绘制如果写回 TaskbarRenderer 进程级静态字段，
    /// 会破坏或释放用户在主配置中自定的字体并篡改四组监控项状态配色；
    /// 本测试套件确保在预览任意生命周期（创建、多次绘制、明暗切换、多实例交替、
    /// 销毁以及工作室窗体开闭）中，生产静态快照逐字段绝对恒定。
    /// </para>
    /// </summary>
    public class ThemeStudioTaskbarIsolationTests : IDisposable
    {
        public ThemeStudioTaskbarIsolationTests()
        {
            // 测试前重置生产静态环境为初始安全配置
            TaskbarRenderer.ReloadStyle(new Settings());
        }

        public void Dispose()
        {
            // 测试后恢复生产静态环境为默认状态，避免后续测试受到残留样式污染
            try
            {
                TaskbarRenderer.ReloadStyle(new Settings());
            }
            catch
            {
            }
        }

        /// <summary>STA 线程调度器（WinForms 控件及 GDI+ 操作规范）。</summary>
        private static void RunSta(Action action)
        {
            Exception? captured = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromMinutes(2));

            if (captured != null)
                ExceptionDispatchInfo.Capture(captured).Throw();
        }

        /// <summary>构造非默认的独特生产任务栏配置。</summary>
        private static Settings CreateProductionCustomSettings()
        {
            return new Settings
            {
                TaskbarCustomLayout = true,
                TaskbarFontFamily = "Courier New",
                TaskbarFontSize = 13.5f,
                TaskbarFontBold = true,
                TaskbarCustomStyle = true,
                TaskbarColorLabel = "#123456",
                TaskbarColorSafe = "#654321",
                TaskbarColorWarn = "#AABBCC",
                TaskbarColorCrit = "#DDEEFF",
            };
        }

        /// <summary>构造用于预览的测试主题。</summary>
        private static Theme CreateTestTheme(string name, string background = "#202830")
        {
            var theme = new Theme
            {
                Name = name,
                Version = 3,
                Layout = new LayoutConfig { Padding = 10, RowHeight = 36, CornerRadius = 8 },
                Color = new ColorConfig
                {
                    Background = background,
                    TextPrimary = "#EAEAEA",
                    TextTitle = "#FFFFFF"
                },
            };
            theme.BuildFonts();
            return theme;
        }

        /// <summary>
        /// 断言两个生产快照的字体名称、字号、字形样式、UseCustom 与四组颜色逐字段严格一致。
        /// </summary>
        private static void AssertSnapshotsEqual(
            TaskbarRenderer.TaskbarStyleSnapshot expected,
            TaskbarRenderer.TaskbarStyleSnapshot actual)
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.FontFamilyName, actual.FontFamilyName);
            Assert.Equal(expected.FontSize, actual.FontSize);
            Assert.Equal(expected.FontStyle, actual.FontStyle);
            Assert.Equal(expected.UseCustom, actual.UseCustom);
            Assert.Equal(expected.LabelColor, actual.LabelColor);
            Assert.Equal(expected.SafeColor, actual.SafeColor);
            Assert.Equal(expected.WarnColor, actual.WarnColor);
            Assert.Equal(expected.CritColor, actual.CritColor);
            Assert.Equal(expected, actual);
        }

        // ====================================================================
        // 用例 1（核心生产样式防污染）
        // ====================================================================
        [Fact(DisplayName = "用例1_核心生产样式防污染_多次预览绘制与明暗切换不改变生产静态快照")]
        public void 用例1_核心生产样式防污染_多次预览绘制与明暗切换不改变生产静态快照()
        {
            RunSta(() =>
            {
                // 1. 预置非默认的生产任务栏字体（"Courier New", 13.5f, Bold）和四组独特的自定义配色
                var prodCfg = CreateProductionCustomSettings();
                TaskbarRenderer.ReloadStyle(prodCfg);

                // 2. 捕获初始生产快照 S0 并验证预置准确生效
                var s0 = TaskbarRenderer.CaptureStyleState();
                Assert.NotNull(s0);
                Assert.Equal("Courier New", s0.FontFamilyName);
                Assert.Equal(13.5f, s0.FontSize);
                Assert.Equal(FontStyle.Bold, s0.FontStyle);
                Assert.True(s0.UseCustom);
                Assert.Equal(ColorTranslator.FromHtml("#123456"), s0.LabelColor);
                Assert.Equal(ColorTranslator.FromHtml("#654321"), s0.SafeColor);
                Assert.Equal(ColorTranslator.FromHtml("#AABBCC"), s0.WarnColor);
                Assert.Equal(ColorTranslator.FromHtml("#DDEEFF"), s0.CritColor);

                // 3. 创建 TaskbarPreviewControl（使用不同的主题与配置）
                var theme = CreateTestTheme("PreviewTheme-Case1");
                try
                {
                    using var control = new TaskbarPreviewControl();
                    var previewCfg = new Settings
                    {
                        TaskbarCustomLayout = true,
                        TaskbarFontFamily = "Arial",
                        TaskbarFontSize = 9.5f,
                        TaskbarFontBold = false,
                        TaskbarCustomStyle = true,
                        TaskbarColorLabel = "#998877",
                        TaskbarColorSafe = "#112233",
                        TaskbarColorWarn = "#445566",
                        TaskbarColorCrit = "#778899",
                    };
                    control.ApplySettings(previewCfg);
                    control.SetTheme(theme);

                    using var bmp = new Bitmap(Math.Max(320, control.ContentWidth), Math.Max(32, control.ContentHeight));
                    using var g = Graphics.FromImage(bmp);

                    // 多次 RenderPreview（包括深色与浅色模式切换、单双行切换）
                    control.LightMode = true;
                    control.RenderPreview(g);

                    control.LightMode = false;
                    control.RenderPreview(g);

                    control.LightMode = true;
                    control.RenderPreview(g);

                    control.SingleLine = true;
                    control.RenderPreview(g);

                    control.SingleLine = false;
                    control.RenderPreview(g);

                    // 4. 再次记录快照 S1，断言逐字段完全一致
                    var s1 = TaskbarRenderer.CaptureStyleState();
                    AssertSnapshotsEqual(s0, s1);
                }
                finally
                {
                    theme.DisposeFonts();
                }
            });
        }

        // ====================================================================
        // 用例 2（多实例交替绘制与背景切换）
        // ====================================================================
        [Fact(DisplayName = "用例2_多实例交替绘制与背景切换_生产静态快照自始至终未受干扰")]
        public void 用例2_多实例交替绘制与背景切换_生产静态快照自始至终未受干扰()
        {
            RunSta(() =>
            {
                var prodCfg = CreateProductionCustomSettings();
                TaskbarRenderer.ReloadStyle(prodCfg);
                var s0 = TaskbarRenderer.CaptureStyleState();

                var themeA = CreateTestTheme("Theme-InstanceA", "#1E1E1E");
                var themeB = CreateTestTheme("Theme-InstanceB", "#F5F5F5");

                try
                {
                    using var controlA = new TaskbarPreviewControl();
                    using var controlB = new TaskbarPreviewControl();

                    var cfgA = new Settings
                    {
                        TaskbarCustomLayout = true,
                        TaskbarFontFamily = "Segoe UI",
                        TaskbarFontSize = 10.0f,
                        TaskbarFontBold = false,
                        TaskbarCustomStyle = true,
                        TaskbarColorLabel = "#111111",
                        TaskbarColorSafe = "#222222",
                        TaskbarColorWarn = "#333333",
                        TaskbarColorCrit = "#444444",
                        TaskbarColorBg = "#E0E0E0",
                    };
                    var cfgB = new Settings
                    {
                        TaskbarCustomLayout = true,
                        TaskbarFontFamily = "Times New Roman",
                        TaskbarFontSize = 15.0f,
                        TaskbarFontBold = true,
                        TaskbarCustomStyle = true,
                        TaskbarColorLabel = "#555555",
                        TaskbarColorSafe = "#666666",
                        TaskbarColorWarn = "#777777",
                        TaskbarColorCrit = "#888888",
                        TaskbarColorBg = "#223344",
                    };

                    controlA.ApplySettings(cfgA);
                    controlA.SetTheme(themeA);

                    controlB.ApplySettings(cfgB);
                    controlB.SetTheme(themeB);

                    using var bmpA = new Bitmap(Math.Max(320, controlA.ContentWidth), Math.Max(32, controlA.ContentHeight));
                    using var gA = Graphics.FromImage(bmpA);

                    using var bmpB = new Bitmap(Math.Max(320, controlB.ContentWidth), Math.Max(32, controlB.ContentHeight));
                    using var gB = Graphics.FromImage(bmpB);

                    // 阶段 1：交替浅深绘制
                    controlA.LightMode = true;
                    controlA.RenderPreview(gA);
                    AssertSnapshotsEqual(s0, TaskbarRenderer.CaptureStyleState());

                    controlB.LightMode = false;
                    controlB.RenderPreview(gB);
                    AssertSnapshotsEqual(s0, TaskbarRenderer.CaptureStyleState());

                    // 阶段 2：背景色切换、单行切换与重绘
                    controlA.LightMode = false;
                    controlA.SingleLine = true;
                    controlA.RenderPreview(gA);
                    AssertSnapshotsEqual(s0, TaskbarRenderer.CaptureStyleState());

                    controlB.LightMode = true;
                    controlB.SingleLine = false;
                    controlB.RenderPreview(gB);
                    AssertSnapshotsEqual(s0, TaskbarRenderer.CaptureStyleState());

                    // 阶段 3：更新配置并重新生成内部样式上下文
                    cfgA.TaskbarFontSize = 11.0f;
                    controlA.ApplySettings(cfgA);
                    controlA.RenderPreview(gA);
                    AssertSnapshotsEqual(s0, TaskbarRenderer.CaptureStyleState());

                    // 终态断言：静态生产快照完好无损
                    var sFinal = TaskbarRenderer.CaptureStyleState();
                    AssertSnapshotsEqual(s0, sFinal);
                }
                finally
                {
                    themeA.DisposeFonts();
                    themeB.DisposeFonts();
                }
            });
        }

        // ====================================================================
        // 用例 3（主题工作室打开与关闭全生命周期隔离）
        // ====================================================================
        [Fact(DisplayName = "用例3_主题工作室打开与关闭全生命周期隔离_生产样式不被修改或篡改")]
        public void 用例3_主题工作室打开与关闭全生命周期隔离_生产样式不被修改或篡改()
        {
            RunSta(() =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "litemonitor-studio-iso-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                ThemeDocumentStore.SetThemeDirForTests(tempDir);
                ThemeDocumentStore.SetRecoveryDirForTests(Path.Combine(tempDir, "recovery"));

                try
                {
                    const string testThemeJson = @"{
  ""version"": 3,
  ""name"": ""任务栏隔离验证主题"",
  ""layout"": { ""rowHeight"": 40, ""padding"": 12 },
  ""font"": { ""family"": ""Microsoft YaHei UI"", ""value"": 12.0 },
  ""color"": { ""background"": ""#303030"", ""textTitle"": ""#FFFFFF"" }
}";
                    File.WriteAllText(Path.Combine(tempDir, "任务栏隔离验证主题.json"), testThemeJson, new UTF8Encoding(false));

                    // 1. 预置生产样式
                    var prodCfg = CreateProductionCustomSettings();
                    TaskbarRenderer.ReloadStyle(prodCfg);
                    var s0 = TaskbarRenderer.CaptureStyleState();

                    // 2. 启动 ThemeStudioForm，验证初始化期间生产快照不变
                    using (var form = new ThemeStudioForm())
                    {
                        form.Show();
                        Application.DoEvents();
                        AssertSnapshotsEqual(s0, TaskbarRenderer.CaptureStyleState());

                        // 3. 打开主题并切换到任务栏预览
                        form.OpenThemeForTesting("任务栏隔离验证主题");
                        form.SelectPreviewShapeForTesting(2); // 2 = Taskbar
                        form.RefreshPreviewForTesting();
                        Application.DoEvents();

                        // 验证任务栏预览展示时生产快照恒定
                        AssertSnapshotsEqual(s0, TaskbarRenderer.CaptureStyleState());
                        Assert.NotNull(form.TaskbarPreviewForTesting);

                        // 4. 切换到横向预览与竖向预览
                        form.SelectPreviewShapeForTesting(1); // 1 = Horizontal
                        form.RefreshPreviewForTesting();
                        Application.DoEvents();
                        AssertSnapshotsEqual(s0, TaskbarRenderer.CaptureStyleState());

                        form.SelectPreviewShapeForTesting(0); // 0 = Vertical
                        form.RefreshPreviewForTesting();
                        Application.DoEvents();
                        AssertSnapshotsEqual(s0, TaskbarRenderer.CaptureStyleState());

                        // 5. 再次切回任务栏预览并触发重绘
                        form.SelectPreviewShapeForTesting(2); // 2 = Taskbar
                        form.RefreshPreviewForTesting();
                        Application.DoEvents();
                        AssertSnapshotsEqual(s0, TaskbarRenderer.CaptureStyleState());

                        // 关闭窗体
                        form.Close();
                    }

                    Application.DoEvents();

                    // 6. 关闭窗体后断言生产样式快照依旧保持预置状态（无 FormClosed 篡改）
                    var sAfterClose = TaskbarRenderer.CaptureStyleState();
                    AssertSnapshotsEqual(s0, sAfterClose);
                }
                finally
                {
                    ThemeDocumentStore.SetThemeDirForTests(null);
                    ThemeDocumentStore.ResetRecoveryDir();
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            });
        }

        // ====================================================================
        // 用例 4（字体所有权与销毁安全）
        // ====================================================================
        [Fact(DisplayName = "用例4_字体所有权与销毁安全_预览控件Dispose不释放或破坏生产静态Font")]
        public void 用例4_字体所有权与销毁安全_预览控件Dispose不释放或破坏生产静态Font()
        {
            RunSta(() =>
            {
                // 1. 预置生产字体与配置
                var prodCfg = new Settings
                {
                    TaskbarCustomLayout = true,
                    TaskbarFontFamily = "Georgia",
                    TaskbarFontSize = 14.0f,
                    TaskbarFontBold = true,
                    TaskbarCustomStyle = true,
                    TaskbarColorLabel = "#223344",
                    TaskbarColorSafe = "#334455",
                    TaskbarColorWarn = "#445566",
                    TaskbarColorCrit = "#556677",
                };
                TaskbarRenderer.ReloadStyle(prodCfg);
                var s0 = TaskbarRenderer.CaptureStyleState();

                var theme = CreateTestTheme("DisposeTheme");
                try
                {
                    // 2. 创建 TaskbarPreviewControl 并执行渲染
                    var control = new TaskbarPreviewControl();
                    control.SetTheme(theme);

                    using (var bmp = new Bitmap(320, 32))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        control.RenderPreview(g);
                    }

                    // 3. 显式释放预览控件（其自持的 TaskbarStyleContext 和 Font 应被安全回收）
                    control.Dispose();

                    // 4. 断言生产快照未被污染
                    var s1 = TaskbarRenderer.CaptureStyleState();
                    AssertSnapshotsEqual(s0, s1);

                    // 5. 验证生产静态环境的 Font 实例完好可用
                    // 若生产 Font 被误释放，调用依赖静态 _cachedFont 的 Render 将抛出 ArgumentException / ObjectDisposedException
                    using var prodBmp = new Bitmap(320, 32);
                    using var prodG = Graphics.FromImage(prodBmp);

                    var cols = ThemePreviewScene.BuildColumns(ThemePreviewScene.CreateMetrics(), false);
                    var ex = Record.Exception(() =>
                    {
                        // 不传 context，直接走生产静态 _cachedFont 路径
                        TaskbarRenderer.Render(prodG, cols, false);
                    });
                    Assert.Null(ex);
                }
                finally
                {
                    theme.DisposeFonts();
                }
            });
        }

        // ====================================================================
        // 用例 5（N07 菜单骨架预埋断言）
        // ====================================================================
        [Fact(DisplayName = "用例5_N07菜单骨架预埋断言_导出按钮与三个子菜单项完整就绪")]
        public void 用例5_N07菜单骨架预埋断言_导出按钮与三个子菜单项完整就绪()
        {
            RunSta(() =>
            {
                using var form = new ThemeStudioForm();
                var exportBtn = form.ExportButtonForTesting;

                // 1. 断言 ExportButton 不为 null 且标题正确
                Assert.NotNull(exportBtn);
                Assert.Equal("导出", exportBtn.Text);

                // 2. 断言下拉菜单包含：“导出主题 JSON”、“导出效果图”、“准备投稿资料” 3 个菜单项
                var items = exportBtn.DropDownItems.OfType<ToolStripMenuItem>().ToList();
                Assert.Equal(3, items.Count);

                Assert.Equal("导出主题 JSON", items[0].Text);
                Assert.Equal("导出效果图", items[1].Text);
                Assert.Equal("准备投稿资料", items[2].Text);

                // 3. 验证触发各菜单项点击时安全响应且不抛出异常
                foreach (var item in items)
                {
                    var ex = Record.Exception(() => item.PerformClick());
                    Assert.Null(ex);
                }
            });
        }
    }
}
