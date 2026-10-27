using System;
using System.Drawing;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// R2 回归：F04 主效果 GDI 兜底必须使用兼容派生配色；
    /// F06 任务栏预览必须接收设置快照；F09 任务栏导出必须是独立形态。
    /// </summary>
    public class R2PreviewExportRegressionTests : IDisposable
    {
        private readonly string _dir;

        public R2PreviewExportRegressionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "litemonitor-r2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static Theme CreateGpuTheme()
        {
            var theme = new Theme
            {
                Name = "R2-Gpu",
                Version = 1,
                Color = new ColorConfig
                {
                    Background = "#101010",
                    GroupBackground = "#202020",
                    Border = "#303030",
                    TextPrimary = "#FFFFFF",
                    TextSecondary = "#AAAAAA",
                    TextTitle = "#FF0000"
                },
                Rendering = new ThemeRenderingConfig
                {
                    Backend = ThemeRenderingConfig.GpuBackend,
                    Material = ThemeRenderingConfig.LiquidGlassMaterial,
                    FallbackStyle = new ThemeFallbackStyle
                    {
                        Background = "#010203",
                        PanelBackground = "#040506",
                        Border = "#070809",
                        Text = "#0A0B0C",
                        MutedText = "#0D0E0F",
                        Accent = "#112233"
                    }
                }
            };
            theme.BuildFonts();
            return theme;
        }

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
            if (captured != null) ExceptionDispatchInfo.Capture(captured).Throw();
        }

        [Fact]
        public void F04_主效果目标_GPU关闭时_GDI兜底使用兼容派生配色()
        {
            RunSta(() =>
            {
                Settings settings = Settings.Load();
                bool previous = settings.GpuThemeEffectsEnabled;
                settings.GpuThemeEffectsEnabled = false;
                try
                {
                    using var control = new ThemePreviewControl();
                    using var form = new Form();
                    form.Controls.Add(control);
                    control.Size = new Size(300, 400);
                    form.Show();

                    Theme theme = CreateGpuTheme();
                    control.SetTheme(theme, ThemeEditTarget.GpuMain, PreviewShape.Vertical);

                    Assert.False(control.IsGpuPreviewActive, "GPU 关闭时不应激活 GPU 预览");
                    Assert.NotNull(control.RenderedTheme);
                    Assert.Equal("#010203", control.RenderedTheme!.Color.Background);
                    Assert.NotEqual("#101010", control.RenderedTheme.Color.Background);

                    form.Close();
                }
                finally
                {
                    settings.GpuThemeEffectsEnabled = previous;
                }
            });
        }

        [Fact]
        public void F05_横向形态_GPU关闭时仍保持Horizontal且使用兼容派生主题()
        {
            RunSta(() =>
            {
                Settings settings = Settings.Load();
                bool previous = settings.GpuThemeEffectsEnabled;
                settings.GpuThemeEffectsEnabled = false;
                try
                {
                    using var control = new ThemePreviewControl();
                    using var form = new Form();
                    form.Controls.Add(control);
                    control.Size = new Size(400, 300);
                    form.Show();

                    control.SetTheme(CreateGpuTheme(), ThemeEditTarget.GpuMain, PreviewShape.Horizontal);

                    Assert.Equal(PreviewShape.Horizontal, control.CurrentShape);
                    Assert.False(control.IsGpuPreviewActive);
                    Assert.Equal("#010203", control.RenderedTheme!.Color.Background);

                    form.Close();
                }
                finally
                {
                    settings.GpuThemeEffectsEnabled = previous;
                }
            });
        }

        [Fact]
        public void F06_任务栏预览_应用设置快照后使用单行与颜色来源()
        {
            RunSta(() =>
            {
                using var control = new TaskbarPreviewControl();
                using var form = new Form();
                form.Controls.Add(control);
                control.Size = new Size(600, 80);
                form.Show();
                control.SetTheme(CreateGpuTheme());
                var settings = new Settings
                {
                    TaskbarSingleLine = true,
                    TaskbarColorBg = "#123456",
                    TaskbarColorLabel = "#ABCDEF",
                    TaskbarFontSize = 13
                };

                control.ApplySettings(settings);

                Assert.True(control.SingleLine, "ApplySettings 应把 TaskbarSingleLine 带入预览");
                Assert.True(control.ContentWidth > 0);
                Assert.True(control.ContentHeight > 0);
                Assert.Contains("设置", control.ColorSourceNote);
                form.Close();
            });
        }

        [Fact]
        public void F09_任务栏形态导出_固定400x32且不落入竖向()
        {
            RunSta(() =>
            {
                string themePath = Path.Combine(_dir, "theme.json");
                File.WriteAllText(themePath, @"{
  ""version"": 3,
  ""name"": ""R2-Taskbar"",
  ""layout"": { ""rowHeight"": 40, ""padding"": 12 },
  ""font"": { ""family"": ""Microsoft YaHei UI"", ""value"": 14.0 },
  ""color"": { ""background"": ""#202020"", ""textTitle"": ""#FFFFFF"" }
}", new UTF8Encoding(false));

                ThemeDocumentSession session = ThemeDocumentSession.FromSourceBytes(
                    "R2-Taskbar", themePath, File.ReadAllBytes(themePath));
                string png = Path.Combine(_dir, "taskbar.png");

                ThemeImageExportResult result = ThemeExportService.ExportEffectImage(
                    session, png, ImageRoutePreference.Auto, ThemeExportImageFormFactor.Taskbar,
                    new Settings { TaskbarSingleLine = true }, taskbarLightMode: false);

                Assert.True(result.Success, result.Error);
                using var bitmap = new Bitmap(png);
                Assert.Equal(ThemeExportService.TaskbarImageWidth, bitmap.Width);
                Assert.Equal(ThemeExportService.TaskbarImageHeight, bitmap.Height);
            });
        }

        [Fact]
        public void F09_任务栏形态_Gpu路线必须明确失败而不是静默竖向()
        {
            RunSta(() =>
            {
                string themePath = Path.Combine(_dir, "theme-gpu.json");
                File.WriteAllText(themePath, @"{
  ""version"": 3,
  ""name"": ""R2-Taskbar-Gpu"",
  ""layout"": { ""rowHeight"": 40, ""padding"": 12 },
  ""font"": { ""family"": ""Microsoft YaHei UI"", ""value"": 14.0 },
  ""color"": { ""background"": ""#202020"", ""textTitle"": ""#FFFFFF"" }
}", new UTF8Encoding(false));

                ThemeDocumentSession session = ThemeDocumentSession.FromSourceBytes(
                    "R2-Taskbar-Gpu", themePath, File.ReadAllBytes(themePath));
                string png = Path.Combine(_dir, "taskbar-gpu.png");

                ThemeImageExportResult result = ThemeExportService.ExportEffectImage(
                    session, png, ImageRoutePreference.Gpu, ThemeExportImageFormFactor.Taskbar);

                Assert.False(result.Success);
                Assert.Contains("任务栏", result.Error);
                Assert.False(File.Exists(png));
            });
        }
    }
}
