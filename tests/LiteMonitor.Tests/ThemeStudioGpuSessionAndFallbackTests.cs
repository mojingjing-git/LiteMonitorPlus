using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    public class ThemeStudioGpuSessionAndFallbackTests
    {
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
            if (!thread.Join(TimeSpan.FromMinutes(2)))
            {
                throw new TimeoutException("STA 测试线程未在 2 分钟内完成。");
            }

            if (captured != null)
            {
                throw captured;
            }
        }

        private static Theme CreateTestTheme(bool isGpu = true, string material = ThemeRenderingConfig.LiquidGlassMaterial)
        {
            var theme = new Theme
            {
                Name = "GpuTestTheme",
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
                Rendering = isGpu ? new ThemeRenderingConfig
                {
                    Backend = ThemeRenderingConfig.GpuBackend,
                    Material = material,
                    FallbackStyle = new ThemeFallbackStyle
                    {
                        Background = "#010203",
                        PanelBackground = "#040506",
                        Border = "#070809",
                        Text = "#0A0B0C",
                        MutedText = "#0D0E0F",
                        Accent = "#112233"
                    }
                } : null
            };
            theme.BuildFonts();
            return theme;
        }

        [Fact]
        public void N03_BuildDerivedTheme_生成独立副本且不修改源主题()
        {
            Theme source = CreateTestTheme(isGpu: true);
            string originalBg = source.Color.Background;
            string originalGroupBg = source.Color.GroupBackground;

            Theme derived = LegacyFallbackStyleResolver.BuildDerivedTheme(source);

            // 源主题未被修改
            Assert.Equal(originalBg, source.Color.Background);
            Assert.Equal(originalGroupBg, source.Color.GroupBackground);

            // 派生副本与源主题不是同一个引用
            Assert.NotSame(source, derived);
            Assert.NotSame(source.Color, derived.Color);

            // 派生配色落入正确槽位
            Assert.Equal("#010203", derived.Color.Background);
            Assert.Equal("#040506", derived.Color.GroupBackground);
            Assert.Equal("#070809", derived.Color.Border);
            Assert.Equal("#0A0B0C", derived.Color.TextPrimary);
            Assert.Equal("#0D0E0F", derived.Color.TextSecondary);
            Assert.Equal("#112233", derived.Color.TextTitle); // accent -> textTitle
        }

        [Fact]
        public void N03_BuildDerivedTheme_共享字体实例且不二次调用BuildFonts()
        {
            Theme source = CreateTestTheme(isGpu: true);
            Font titleFont = source.FontTitle;
            Font groupFont = source.FontGroup;
            Font itemFont = source.FontItem;
            Font valueFont = source.FontValue;
            Font taskbarFont = source.FontTaskbar;

            Assert.NotNull(titleFont);

            Theme derived = LegacyFallbackStyleResolver.BuildDerivedTheme(source);

            // 字体实例严格引用共享
            Assert.Same(titleFont, derived.FontTitle);
            Assert.Same(groupFont, derived.FontGroup);
            Assert.Same(itemFont, derived.FontItem);
            Assert.Same(valueFont, derived.FontValue);
            Assert.Same(taskbarFont, derived.FontTaskbar);
        }

        [Fact]
        public void N04_HorizontalPreviewMock_正确构建模拟数据与列布局()
        {
            var metrics = HorizontalPreviewMock.BuildMockMetrics();
            Assert.NotEmpty(metrics);
            Assert.Contains(metrics, m => m.Key == "CPU.Load");

            Theme theme = CreateTestTheme(isGpu: false);
            var cols = HorizontalPreviewMock.BuildColumns(theme: theme, singleLine: false, width: 400);
            Assert.NotEmpty(cols);
            Assert.True(cols[0].Bounds.Width > 0, "BuildColumns 应当计算并填充列边界");
        }

        [Fact]
        public void N02_N03_ThemePreviewControl_接入兼容外观与主效果()
        {
            RunSta(() =>
            {
                using var control = new ThemePreviewControl();
                using var form = new Form();
                form.Controls.Add(control);
                control.Size = new Size(300, 400);
                form.Show();

                Theme theme = CreateTestTheme(isGpu: true);

                // 1. 设置兼容外观：GPU 预览应被隐藏，画面走 GDI+ 派生绘制
                control.SetTheme(theme, ThemeEditTarget.GpuFallback);
                Assert.False(control.IsGpuPreviewActive);

                // 2. 诊断穿透属性存在且可读
                Assert.True(control.DeviceCreationCount >= 0);
                Assert.True(control.SessionUpdateCount >= 0);
                Assert.True(control.CurrentGeneration >= 0);

                // 3. 经典模式
                Theme classicTheme = CreateTestTheme(isGpu: false);
                control.SetTheme(classicTheme, ThemeEditTarget.Classic);
                Assert.False(control.IsGpuPreviewActive);

                form.Close();
            });
        }
    }
}
