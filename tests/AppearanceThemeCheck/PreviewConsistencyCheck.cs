using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;

namespace LiteMonitor.AppearanceThemeCheck
{
    /// <summary>
    /// A13 收口 · R2：主题编辑器预览与主渲染入口的**效果一致性检查**。
    ///
    /// ★ 背景（独立验收 R2）：预览曾经自绘"假扫描线 / 假网格"，并走三参数
    ///   <c>UIRenderer.Render</c>（没有 <see cref="AppearanceRuntime"/>），因此
    ///   辉光（Glow）与新进度条样式（BarStyle）在预览里**完全不生效** ——
    ///   实测切换后预览变化像素 = 0，而主渲染入口 = 4,256 / 9,390。
    ///
    /// ★ 本检查的判据（全部离屏渲染，不抓屏、不动窗口）：
    /// 1. 同一份主题场景，**只**切换 Glow（0 → 100）：预览像素必须发生变化（&gt; 0），
    ///    且变化区域必须落在外框附近（紧贴客户区边缘）。
    /// 2. 同一份主题场景，**只**切换 BarStyle（0 → 1）：预览像素必须发生变化（&gt; 0），
    ///    且变化区域必须落在进度条附近。
    /// 3. 预览与主渲染入口（<c>UIRenderer.Render</c> 四参重载）**逐像素一致**：
    ///    证明预览确实走的是正式渲染层，而不是另画一套。
    /// </summary>
    internal static class PreviewConsistencyCheck
    {
        /// <summary>Glow 变化后，变化像素到客户区边缘的最大允许距离（物理像素）。</summary>
        private const int MaxGlowEdgeDistance = 8;

        /// <summary>BarStyle 变化后，变化像素到条槽的最大允许距离（物理像素；抗锯齿留 2 像素余量）。</summary>
        private const int MaxBarDistance = 2;

        /// <summary>执行预览一致性检查。</summary>
        /// <param name="repoRoot">仓库根目录（用于定位内置主题）。</param>
        public static void Run(string repoRoot)
        {
            Program.Section("A13-R2：编辑器预览与主渲染入口的效果一致性");

            string themeDir = Path.Combine(repoRoot, "resources", "themes");

            Theme theme;
            try
            {
                theme = ThemeFileService.LoadTheme(themeDir, "DarkNeo_Modern");
            }
            catch (Exception ex)
            {
                Program.Fail("加载 DarkNeo_Modern 失败：" + ex.Message);
                return;
            }

            try
            {
                RunScene(theme);
            }
            catch (Exception ex)
            {
                Program.Fail("预览一致性检查执行异常：" + ex);
            }
            finally
            {
                // 主题自己创建的字体由主题负责释放
                theme.DisposeFonts();
            }
        }

        /// <summary>跑一遍"同一场景、每次只切一个选项"的对比。</summary>
        private static void RunScene(Theme theme)
        {
            // 明确设置可见的边框 / 辉光颜色；其余外观项保持主题原值
            theme.Layout.FrameStyle = 1;   // 切角外框
            theme.Layout.FrameWidth = 1;
            theme.Color.Border = "#00FFFF";
            theme.Color.Shadow = "#00FFFF";

            PreviewFrame baseFrame = Render(theme, glow: 0, barStyle: 0);
            PreviewFrame glowFrame = Render(theme, glow: 100, barStyle: 0);
            PreviewFrame barFrame = Render(theme, glow: 0, barStyle: 1);

            Console.WriteLine($"    客户区：{baseFrame.Bounds.Width}×{baseFrame.Bounds.Height}；带条指标：{baseFrame.BarRects.Count} 条");

            // ---- 1. Glow：预览必须跟着变（修复前恒为 0）----
            List<Point> glowDiff = DiffPixels(baseFrame.Bitmap, glowFrame.Bitmap);
            Program.Check(glowDiff.Count > 0,
                "预览：Glow 0 → 100 会改变画面像素",
                $"变化像素 {glowDiff.Count} 个（期望 > 0）");

            int maxEdge = 0;
            foreach (Point p in glowDiff)
            {
                int d = EdgeDistance(p, baseFrame.Bounds);
                if (d > maxEdge) maxEdge = d;
            }
            Program.Check(glowDiff.Count == 0 || maxEdge <= MaxGlowEdgeDistance,
                "预览：Glow 变化区域落在外框附近",
                $"变化像素距客户区边缘最远 {maxEdge} px（判据 ≤ {MaxGlowEdgeDistance}）");

            // ---- 2. BarStyle：预览必须跟着变（修复前恒为 0）----
            List<Point> barDiff = DiffPixels(baseFrame.Bitmap, barFrame.Bitmap);
            Program.Check(barDiff.Count > 0,
                "预览：BarStyle 0 → 1 会改变画面像素",
                $"变化像素 {barDiff.Count} 个（期望 > 0）");

            int outside = 0;
            foreach (Point p in barDiff)
            {
                if (!NearAnyBar(p, baseFrame.BarRects, MaxBarDistance)) outside++;
            }
            Program.Check(outside == 0,
                "预览：BarStyle 变化区域落在进度条附近",
                $"落在所有进度条之外的像素 {outside} 个（判据 = 0）");

            // ---- 3. 预览与主渲染入口逐像素一致 ----
            Bitmap mainFrame = RenderMainEntry(theme, glow: 0, barStyle: 0);
            int mainDiff = DiffPixels(baseFrame.Bitmap, mainFrame).Count;
            Program.Check(mainDiff == 0,
                "预览与主渲染入口逐像素一致",
                $"差异像素 {mainDiff} 个（期望 0）");
        }

        // ================================================================
        // 渲染
        // ================================================================

        /// <summary>走预览入口渲染一帧（正式外观运行时 + 正式渲染器）。</summary>
        private static PreviewFrame Render(Theme theme, int glow, int barStyle)
        {
            theme.Layout.Glow = glow;
            theme.Layout.BarStyle = barStyle;

            Theme preview = ThemePreviewControl.CreatePreviewTheme(theme, 1.0f);
            List<GroupLayoutInfo> groups = ThemePreviewControl.CreateMockGroups();
            int height = new UILayout(preview).Build(groups);

            var bounds = new Rectangle(0, 0, preview.Layout.Width, Math.Max(1, height));
            var frame = new PreviewFrame { Bounds = bounds };

            foreach (GroupLayoutInfo gr in groups)
            {
                foreach (MetricItem it in gr.Items)
                {
                    if (it.BarRect.Width > 0 && it.BarRect.Height > 0)
                        frame.BarRects.Add(it.BarRect);
                }
            }

            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                // 正式外观运行时：与主窗口同一套渲染器与资源生命周期
                using var runtime = new AppearanceRuntime();
                runtime.Update(preview, 1.0f, 1.0f, bounds);

                ThemePreviewControl.RenderPreview(g, groups, preview, bounds, runtime);
            }

            frame.Bitmap = bmp;
            return frame;
        }

        /// <summary>走主渲染入口渲染一帧（UIRenderer 的四参重载）。</summary>
        private static Bitmap RenderMainEntry(Theme theme, int glow, int barStyle)
        {
            theme.Layout.Glow = glow;
            theme.Layout.BarStyle = barStyle;

            Theme preview = ThemePreviewControl.CreatePreviewTheme(theme, 1.0f);
            List<GroupLayoutInfo> groups = ThemePreviewControl.CreateMockGroups();
            int height = new UILayout(preview).Build(groups);
            var bounds = new Rectangle(0, 0, preview.Layout.Width, Math.Max(1, height));

            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                using var runtime = new AppearanceRuntime();
                runtime.Update(preview, 1.0f, 1.0f, bounds);

                UIRenderer.Render(g, groups, preview, runtime);
            }
            return bmp;
        }

        // ================================================================
        // 像素比较
        // ================================================================

        /// <summary>取两张位图里**不一致**的像素坐标。</summary>
        private static List<Point> DiffPixels(Bitmap a, Bitmap b)
        {
            var points = new List<Point>();
            int w = Math.Min(a.Width, b.Width);
            int h = Math.Min(a.Height, b.Height);

            byte[] pa = Capture(a, out int strideA);
            byte[] pb = Capture(b, out int strideB);

            for (int y = 0; y < h; y++)
            {
                int rowA = y * strideA;
                int rowB = y * strideB;
                for (int x = 0; x < w; x++)
                {
                    int ia = rowA + x * 4;
                    int ib = rowB + x * 4;
                    if (pa[ia] == pb[ib]
                        && pa[ia + 1] == pb[ib + 1]
                        && pa[ia + 2] == pb[ib + 2]
                        && pa[ia + 3] == pb[ib + 3])
                    {
                        continue;
                    }
                    points.Add(new Point(x, y));
                }
            }
            return points;
        }

        /// <summary>抓取位图的 32bpp 原始像素（自上而下）。</summary>
        private static byte[] Capture(Bitmap bmp, out int stride)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                stride = Math.Abs(data.Stride);
                var buffer = new byte[stride * bmp.Height];
                for (int y = 0; y < bmp.Height; y++)
                {
                    IntPtr row = data.Stride >= 0
                        ? IntPtr.Add(data.Scan0, y * stride)
                        : IntPtr.Add(data.Scan0, (bmp.Height - 1 - y) * stride);
                    Marshal.Copy(row, buffer, y * stride, stride);
                }
                return buffer;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        // ================================================================
        // 几何判定
        // ================================================================

        /// <summary>像素到客户区最近边缘的距离（物理像素）。</summary>
        private static int EdgeDistance(Point p, Rectangle bounds)
        {
            int d = p.X - bounds.Left;
            if (p.Y - bounds.Top < d) d = p.Y - bounds.Top;
            if (bounds.Right - 1 - p.X < d) d = bounds.Right - 1 - p.X;
            if (bounds.Bottom - 1 - p.Y < d) d = bounds.Bottom - 1 - p.Y;
            return d < 0 ? 0 : d;
        }

        /// <summary>像素是否落在某条进度条附近（允许 tolerance 像素余量）。</summary>
        private static bool NearAnyBar(Point p, List<Rectangle> bars, int tolerance)
        {
            foreach (Rectangle bar in bars)
            {
                Rectangle area = Rectangle.Inflate(bar, tolerance, tolerance);
                if (area.Contains(p)) return true;
            }
            return false;
        }

        /// <summary>一帧预览的结果（位图 + 客户区 + 条槽矩形）。</summary>
        private sealed class PreviewFrame
        {
            public Bitmap Bitmap = null!;
            public Rectangle Bounds;
            public List<Rectangle> BarRects = new();
        }
    }
}
