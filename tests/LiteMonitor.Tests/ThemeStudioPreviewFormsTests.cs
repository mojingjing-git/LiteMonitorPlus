using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// TE04（Lane A）：横向 / 任务栏**同源预览控件**的离屏证据图与行为回归。
    /// <para>
    /// ★ 断言原则：<b>不基于颜色像素</b>（机器 / 用户 / 主题相关）。
    ///   只断言"文件非空 + 尺寸符合预期 + 像素多样性（≥N 种颜色）或非全透明"。
    /// </para>
    /// ★ 两个控件都<b>不建捕获源</b>、<b>不读实时硬件</b>、<b>不写磁盘</b>；
    ///   证据图写到 <c>%TEMP%/LiteMonitor.Tests/Milestone-A/</c>（临时目录，不是用户配置目录）。
    /// </para>
    /// </summary>
    public class ThemeStudioPreviewFormsTests
    {
        // ================================================================
        // 0. 基础设施
        // ================================================================

        /// <summary>证据图目录（临时目录）。</summary>
        private static string EvidenceDir =>
            Path.Combine(Path.GetTempPath(), "LiteMonitor.Tests", "Milestone-A");

        private static Theme CreateTheme(string name, string background = "#202830")
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

        /// <summary>STA 线程执行（WinForms 控件创建的既有范式）。</summary>
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
            thread.Join(TimeSpan.FromMinutes(2));

            if (captured != null)
                ExceptionDispatchInfo.Capture(captured).Throw();
        }

        /// <summary>一次离屏渲染的证据。</summary>
        private sealed class RenderEvidence
        {
            public string Path = "";
            public int Width;
            public int Height;
            public int DistinctColors;
            public int OpaquePixels;
            public int TotalPixels;

            public override string ToString() =>
                $"{Path} {Width}x{Height} colors={DistinctColors} opaque={OpaquePixels}/{TotalPixels}";
        }

        /// <summary>
        /// 离屏渲染 → 存 PNG → 统计像素多样性。
        /// <para>★ 只用"颜色种类数 / 非全透明"做断言，绝不断言具体颜色值。</para>
        /// </summary>
        private static RenderEvidence Capture(string fileName, int width, int height, Action<Graphics> render, Color? prefill = null)
        {
            Directory.CreateDirectory(EvidenceDir);
            string png = Path.Combine(EvidenceDir, fileName);

            using var bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                if (prefill.HasValue)
                {
                    using var bg = new SolidBrush(prefill.Value);
                    g.FillRectangle(bg, 0, 0, bmp.Width, bmp.Height);
                }
                render(g);
            }

            bmp.Save(png, ImageFormat.Png);

            var (distinct, opaque, total) = Analyze(bmp);
            return new RenderEvidence
            {
                Path = png,
                Width = bmp.Width,
                Height = bmp.Height,
                DistinctColors = distinct,
                OpaquePixels = opaque,
                TotalPixels = total
            };
        }

        /// <summary>统计颜色种类数与非全透明像素数（不依赖具体颜色值）。</summary>
        private static (int distinct, int opaque, int total) Analyze(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] buf = new byte[stride * bmp.Height];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);

                var seen = new HashSet<int>();
                int opaque = 0;
                for (int y = 0; y < bmp.Height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        int i = row + x * 4;
                        seen.Add(buf[i] | (buf[i + 1] << 8) | (buf[i + 2] << 16) | (buf[i + 3] << 24));
                        if (buf[i + 3] != 0) opaque++;
                    }
                }
                return (seen.Count, opaque, bmp.Width * bmp.Height);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        // ================================================================
        // 1. 固定模拟场景（交付定义）
        // ================================================================

        [Fact]
        public void 固定模拟场景_指标与数值符合交付定义()
        {
            var metrics = ThemePreviewScene.CreateMetrics();

            Assert.Equal(8, metrics.Count);

            Assert.Equal("CPU.Load", metrics[0].Key);
            Assert.Equal(23f, metrics[0].Value.Value);
            Assert.Equal("CPU.Temp", metrics[1].Key);
            Assert.Equal(65f, metrics[1].Value.Value);
            Assert.Equal("GPU.Load", metrics[2].Key);
            Assert.Equal(90f, metrics[2].Value.Value);
            Assert.Equal("GPU.Temp", metrics[3].Key);
            Assert.Equal(65f, metrics[3].Value.Value);
            Assert.Equal("GPU.VRAM", metrics[4].Key);
            Assert.Equal(41f, metrics[4].Value.Value);
            Assert.Equal("MEM.Load", metrics[5].Key);
            Assert.Equal(68f, metrics[5].Value.Value);
            Assert.Equal("NET.Up", metrics[6].Key);
            Assert.Equal(80f * 1024f, metrics[6].Value.Value);
            Assert.Equal("NET.Down", metrics[7].Key);
            Assert.Equal(38000f * 1024f, metrics[7].Value.Value);

            // 标签按生产用法设置（BoundConfig 留空 ⇒ 静态解析器为空也是安全的）
            Assert.All(metrics, m =>
            {
                Assert.False(string.IsNullOrEmpty(m.Label));
                Assert.False(string.IsNullOrEmpty(m.ShortLabel));
                Assert.Null(m.BoundConfig);
                Assert.NotNull(m.Value);
            });

            // 双行：8 个指标配成 4 列；单行：8 列
            Assert.Equal(4, ThemePreviewScene.BuildColumns(metrics, singleLine: false).Count);
            Assert.Equal(8, ThemePreviewScene.BuildColumns(metrics, singleLine: true).Count);
        }

        // ================================================================
        // 2. 横向预览
        // ================================================================

        [Fact]
        public void 横向预览_离屏渲染_产出非空PNG且宽大于高()
        {
            Theme theme = CreateTheme("LaneA-Horizontal");
            try
            {
                RunSta(() =>
                {
                    using var control = new HorizontalPreviewControl();
                    control.SetTheme(theme);

                    Assert.True(control.ContentWidth > 0, "横向预览宽度必须为正");
                    Assert.True(control.ContentHeight > 0, "横向预览高度必须为正");
                    Assert.True(
                        control.ContentWidth > control.ContentHeight,
                        $"横向预览应当宽大于高：{control.ContentWidth}x{control.ContentHeight}");

                    var ev = Capture(
                        "horizontal-default.png",
                        control.ContentWidth,
                        control.ContentHeight,
                        control.RenderPreview);

                    Assert.True(new FileInfo(ev.Path).Length > 0, "证据图文件必须非空");
                    Assert.Equal(control.ContentWidth, ev.Width);
                    Assert.Equal(control.ContentHeight, ev.Height);

                    // ★ 不看颜色值：只看"有内容"（颜色种类 ≥ 2 或存在非全透明像素）
                    Assert.True(
                        ev.DistinctColors >= 2 || ev.OpaquePixels > 0,
                        $"横向预览画面没有任何内容：{ev}");

                    Console.WriteLine("[LaneA] 横向证据图：" + ev);
                });
            }
            finally
            {
                theme.DisposeFonts();
            }
        }

        [Fact]
        public void 横向预览_空列不抛异常_以最小高度兜底()
        {
            Theme theme = CreateTheme("LaneA-Horizontal-Empty");
            try
            {
                RunSta(() =>
                {
                    using var control = new HorizontalPreviewControl();
                    control.SetTheme(theme);

                    control.SetMetrics(Array.Empty<MetricItem>());

                    Assert.Empty(control.PreviewColumns);
                    Assert.True(control.MinPanelHeight > 0, "最小面板高度必须为正");
                    Assert.Equal(control.MinPanelHeight, control.ContentHeight);
                    Assert.True(control.ContentWidth > 0, "空列时宽度仍要有值（不能塌成 0）");

                    var ev = Capture(
                        "horizontal-empty-columns.png",
                        control.ContentWidth,
                        control.ContentHeight,
                        control.RenderPreview);

                    Assert.True(new FileInfo(ev.Path).Length > 0);
                    Assert.True(ev.OpaquePixels > 0, "空列预览仍应有底板");
                    Console.WriteLine("[LaneA] 空列证据图：" + ev);
                });
            }
            finally
            {
                theme.DisposeFonts();
            }
        }

        [Fact]
        public void 横向预览_超长标签_不抛异常且列宽随之变宽()
        {
            Theme theme = CreateTheme("LaneA-Horizontal-LongLabel");
            try
            {
                RunSta(() =>
                {
                    using var control = new HorizontalPreviewControl();
                    control.SetTheme(theme);

                    int narrowWidth = control.ContentWidth;

                    var longLabelMetric = ThemePreviewScene.CreateMetric(
                        "CPU.Load",
                        new string('长', 120),
                        new string('X', 60),
                        23f);
                    control.SetMetrics(new List<MetricItem> { longLabelMetric });

                    Assert.Single(control.PreviewColumns);
                    Assert.True(
                        control.ContentWidth > narrowWidth,
                        $"超长标签应当撑宽列：{narrowWidth} -> {control.ContentWidth}");

                    var ev = Capture(
                        "horizontal-long-label.png",
                        control.ContentWidth,
                        control.ContentHeight,
                        control.RenderPreview);

                    Assert.True(new FileInfo(ev.Path).Length > 0);
                    Console.WriteLine("[LaneA] 长标签证据图：" + ev);
                });
            }
            finally
            {
                theme.DisposeFonts();
            }
        }

        [Fact]
        public void 横向预览_单行与双行_列数与高度都不同()
        {
            Theme theme = CreateTheme("LaneA-Horizontal-Lines");
            try
            {
                RunSta(() =>
                {
                    using var control = new HorizontalPreviewControl();
                    control.SetTheme(theme);

                    Assert.False(control.SingleLine);
                    int twoLineColumns = control.PreviewColumns.Count;
                    int twoLineHeight = control.ContentHeight;

                    control.SingleLine = true;

                    Assert.True(control.SingleLine);
                    int oneLineColumns = control.PreviewColumns.Count;
                    int oneLineHeight = control.ContentHeight;

                    Assert.Equal(4, twoLineColumns);
                    Assert.Equal(8, oneLineColumns);
                    Assert.True(
                        oneLineHeight < twoLineHeight,
                        $"单行应当比双行矮：{oneLineHeight} vs {twoLineHeight}");

                    var ev = Capture(
                        "horizontal-single-line.png",
                        control.ContentWidth,
                        control.ContentHeight,
                        control.RenderPreview);
                    Assert.True(new FileInfo(ev.Path).Length > 0);
                    Console.WriteLine("[LaneA] 单行证据图：" + ev);
                });
            }
            finally
            {
                theme.DisposeFonts();
            }
        }

        // ================================================================
        // 3. 任务栏预览
        // ================================================================

        [Fact]
        public void 任务栏预览_浅色与深色_都产出非空PNG()
        {
            Theme theme = CreateTheme("LaneA-Taskbar");
            try
            {
                RunSta(() =>
                {
                    using var control = new TaskbarPreviewControl();
                    control.SetTheme(theme);

                    control.LightMode = true;
                    var light = Capture(
                        "taskbar-light.png",
                        control.ContentWidth,
                        control.ContentHeight,
                        control.RenderPreview,
                        control.PreviewBackgroundColor);

                    control.LightMode = false;
                    var dark = Capture(
                        "taskbar-dark.png",
                        control.ContentWidth,
                        control.ContentHeight,
                        control.RenderPreview,
                        control.PreviewBackgroundColor);

                    foreach (var ev in new[] { light, dark })
                    {
                        Assert.True(new FileInfo(ev.Path).Length > 0, "证据图文件必须非空");
                        Assert.True(ev.OpaquePixels > 0, "任务栏预览必须不是全透明");
                        Assert.True(
                            ev.DistinctColors >= 2,
                            $"任务栏预览没有画出内容（颜色种类 {ev.DistinctColors}）：{ev}");
                        Console.WriteLine("[LaneA] 任务栏证据图：" + ev);
                    }
                });
            }
            finally
            {
                theme.DisposeFonts();
            }
        }

        [Fact]
        public void 任务栏预览_高度不超过64倍缩放()
        {
            Theme theme = CreateTheme("LaneA-Taskbar-Size");
            try
            {
                RunSta(() =>
                {
                    using var control = new TaskbarPreviewControl();
                    control.SetTheme(theme);

                    float scale = control.DpiScale;
                    Assert.True(scale > 0f, "DPI 比例必须为正");

                    Assert.True(control.ContentHeight > 0);
                    Assert.True(
                        control.ContentHeight <= 64 * scale,
                        $"任务栏预览高度 {control.ContentHeight} 超过 64*{scale}");
                });
            }
            finally
            {
                theme.DisposeFonts();
            }
        }

        [Fact]
        public void 任务栏预览_颜色来源标注_不得宣称颜色随主题变化()
        {
            // ★ 事实：任务栏颜色由设置驱动（TaskbarRenderer.ReloadStyle 取 Settings），
            //   与主题无关；主题只影响布局。
            string notice = TaskbarPreviewControl.ColorSourceNotice;

            Assert.False(string.IsNullOrWhiteSpace(notice));
            Assert.Contains("设置", notice);
            Assert.Contains("布局", notice);
            Assert.DoesNotContain("颜色随主题变化", notice.Replace("颜色不随主题变化", ""));
        }

        // ================================================================
        // 4. 换主题 / 副本 / 副作用边界
        // ================================================================

        [Fact]
        public void 预览控件_SetTheme两次_换主题后仍可渲染()
        {
            Theme first = CreateTheme("LaneA-First", background: "#101820");
            Theme second = CreateTheme("LaneA-Second", background: "#303840");
            try
            {
                RunSta(() =>
                {
                    using var horizontal = new HorizontalPreviewControl();
                    using var taskbar = new TaskbarPreviewControl();

                    horizontal.SetTheme(first);
                    taskbar.SetTheme(first);

                    var ev1 = Capture(
                        "horizontal-first-theme.png",
                        horizontal.ContentWidth,
                        horizontal.ContentHeight,
                        horizontal.RenderPreview);
                    Assert.True(new FileInfo(ev1.Path).Length > 0);

                    // 换主题：控件必须重建预览副本并仍可渲染
                    horizontal.SetTheme(second);
                    taskbar.SetTheme(second);

                    Assert.NotSame(second, horizontal.PreviewTheme);
                    Assert.Equal(second.Name, horizontal.PreviewTheme!.Name);
                    Assert.Equal(second.Name, taskbar.PreviewTheme!.Name);

                    var ev2 = Capture(
                        "horizontal-second-theme.png",
                        horizontal.ContentWidth,
                        horizontal.ContentHeight,
                        horizontal.RenderPreview);
                    Assert.True(new FileInfo(ev2.Path).Length > 0);
                    Assert.True(ev2.OpaquePixels > 0);

                    var ev3 = Capture(
                        "taskbar-second-theme.png",
                        taskbar.ContentWidth,
                        taskbar.ContentHeight,
                        taskbar.RenderPreview,
                        taskbar.PreviewBackgroundColor);
                    Assert.True(new FileInfo(ev3.Path).Length > 0);
                    Assert.True(ev3.OpaquePixels > 0);

                    Console.WriteLine("[LaneA] 换主题证据图：" + ev2 + " / " + ev3);
                });
            }
            finally
            {
                first.DisposeFonts();
                second.DisposeFonts();
            }
        }

        [Fact]
        public void 预览控件_只缓存主题副本_不修改源主题也不挂载GPU子控件()
        {
            Theme theme = CreateTheme("LaneA-Copy");
            try
            {
                int sourcePadding = theme.Layout.Padding;

                RunSta(() =>
                {
                    using var horizontal = new HorizontalPreviewControl();
                    using var taskbar = new TaskbarPreviewControl();

                    horizontal.SetTheme(theme);
                    taskbar.SetTheme(theme);

                    // 预览副本是**独立对象**：布局独立、字体只借用
                    Assert.NotSame(theme, horizontal.PreviewTheme);
                    Assert.NotSame(theme.Layout, horizontal.PreviewTheme!.Layout);
                    Assert.Same(theme.FontItem, horizontal.PreviewTheme.FontItem);
                    Assert.Same(theme.FontValue, horizontal.PreviewTheme.FontValue);

                    // 源主题不被预览改动
                    Assert.Equal(sourcePadding, theme.Layout.Padding);

                    // ★ 不做 GPU：不挂任何子控件（GPU 预览控件是子控件形态）
                    Assert.Empty(horizontal.Controls);
                    Assert.Empty(taskbar.Controls);
                });
            }
            finally
            {
                theme.DisposeFonts();
            }
        }

        [Fact]
        public void 预览控件_未设置主题时渲染不抛异常()
        {
            RunSta(() =>
            {
                using var horizontal = new HorizontalPreviewControl();
                using var taskbar = new TaskbarPreviewControl();

                // 没有主题 ⇒ 没有布局 ⇒ 渲染应当"什么都不画"，而不是抛异常
                using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    horizontal.RenderPreview(g);
                    taskbar.RenderPreview(g);
                }

                Assert.Equal(0, horizontal.ContentWidth);
                Assert.Equal(0, taskbar.ContentWidth);
            });
        }
    }
}
