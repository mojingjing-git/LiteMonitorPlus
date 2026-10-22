using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace AppearanceBackdropProbe.Analysis
{
    internal sealed class FrameStats
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public double MeanR { get; set; }
        public double MeanG { get; set; }
        public double MeanB { get; set; }
        public double Luminance { get; set; }
        /// <summary>画面中命中"自身标记色"的像素占比（&gt;0 表示把本窗口自己抓进来了）。</summary>
        public double SelfMarkerFraction { get; set; }
        /// <summary>画面中命中"场景静态纯色块"的像素占比（&gt;0 表示抓到了真实背景）。</summary>
        public double StaticPatchFraction { get; set; }
        /// <summary>与场景参考图的平均绝对差（越小越一致）。</summary>
        public double MeanAbsDiffVsReference { get; set; } = -1;
        /// <summary>色相循环区的平均色相（度）。用于判定背后内容是否随时间变化。</summary>
        public double HueDegrees { get; set; } = -1;
        /// <summary>移动扫描条在区域内的平均亮度（用于判定运动）。</summary>
        public double BarBrightness { get; set; } = -1;
    }

    /// <summary>像素级分析工具（全部为自动断言的判定依据）。</summary>
    internal static class PixelAnalyzer
    {
        public static int[] GetPixels(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var data = new int[w * h];
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, data, 0, data.Length); }
            finally { bmp.UnlockBits(bd); }
            return data;
        }

        public static Color MeanColor(int[] px, int w, Rectangle region)
        {
            long r = 0, g = 0, b = 0, n = 0;
            for (int y = region.Top; y < region.Bottom; y++)
            {
                int row = y * w;
                for (int x = region.Left; x < region.Right; x++)
                {
                    int c = px[row + x];
                    r += (c >> 16) & 0xFF;
                    g += (c >> 8) & 0xFF;
                    b += c & 0xFF;
                    n++;
                }
            }
            if (n == 0) return Color.Black;
            return Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
        }

        public static double Luminance(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

        /// <summary>区域内与目标色（逐通道容差）的像素占比。</summary>
        public static double FractionNear(int[] px, int w, Rectangle region, Color target, int tol)
            => CountNear(px, w, region, target, tol) / (double)Math.Max(1, region.Width * region.Height);

        /// <summary>
        /// 区域内与目标色（逐通道容差）的**绝对像素数**。
        /// 判定"窗口是否还能被外部观察者看到"必须用绝对数量：
        /// 占比会随窗口尺寸变化（同样的标记方块在 300×985 与 420×1379 窗口里占比差一倍），
        /// 用占比做阈值会在大窗口上产生假阴性。
        /// </summary>
        public static int CountNear(int[] px, int w, Rectangle region, Color target, int tol)
        {
            long hit = 0;
            for (int y = region.Top; y < region.Bottom; y++)
            {
                int row = y * w;
                for (int x = region.Left; x < region.Right; x++)
                {
                    int c = px[row + x];
                    int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                    if (Math.Abs(r - target.R) <= tol && Math.Abs(g - target.G) <= tol && Math.Abs(b - target.B) <= tol) hit++;
                }
            }
            return (int)hit;
        }

        /// <summary>两块同尺寸图的平均绝对差（RGB）。</summary>
        public static double MeanAbsDiff(int[] a, int[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            if (n == 0) return -1;
            long sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += Math.Abs(((a[i] >> 16) & 0xFF) - ((b[i] >> 16) & 0xFF));
                sum += Math.Abs(((a[i] >> 8) & 0xFF) - ((b[i] >> 8) & 0xFF));
                sum += Math.Abs((a[i] & 0xFF) - (b[i] & 0xFF));
            }
            return (double)sum / (n * 3);
        }

        /// <summary>色相（0..360），对灰度返回 -1。</summary>
        public static double HueOf(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            if (d < 1e-6) return -1;
            double h;
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = ((b - r) / d) + 2;
            else h = ((r - g) / d) + 4;
            h *= 60;
            if (h < 0) h += 360;
            return h;
        }

        /// <summary>
        /// 阶跃边缘的 10%–90% 上升沿宽度（像素）。
        /// 用于把"模糊半径"变成可自动断言的数值，而不是靠肉眼。
        /// </summary>
        public static double EdgeRiseWidth(int[] px, int w, int h, int y, int x0, int x1)
        {
            if (x1 <= x0 + 2 || y < 0 || y >= h) return -1;
            double min = double.MaxValue, max = double.MinValue;
            var lum = new double[x1 - x0];
            for (int x = x0; x < x1; x++)
            {
                int c = px[y * w + x];
                double l = 0.2126 * ((c >> 16) & 0xFF) + 0.7152 * ((c >> 8) & 0xFF) + 0.0722 * (c & 0xFF);
                lum[x - x0] = l;
                if (l < min) min = l;
                if (l > max) max = l;
            }
            double range = max - min;
            if (range < 5) return -1;
            int i10 = -1, i90 = -1;
            for (int i = 0; i < lum.Length; i++)
            {
                if (i10 < 0 && lum[i] >= min + 0.10 * range) i10 = i;
                if (lum[i] >= min + 0.90 * range) { i90 = i; break; }
            }
            if (i10 < 0 || i90 < 0) return -1;
            return i90 - i10;
        }

        /// <summary>线性回归斜率（用于检测"递归变暗"这类持续漂移）。</summary>
        public static double TrendSlope(IReadOnlyList<double> values)
        {
            int n = values.Count;
            if (n < 2) return 0;
            double sx = 0, sy = 0, sxy = 0, sxx = 0;
            for (int i = 0; i < n; i++)
            {
                double x = i, y = values[i];
                sx += x; sy += y; sxy += x * y; sxx += x * x;
            }
            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < 1e-9) return 0;
            return (n * sxy - sx * sy) / denom;
        }

        /// <summary>分析一帧捕获结果。</summary>
        public static FrameStats Analyze(
            Bitmap capture,
            Rectangle staticRegion,
            Rectangle hueRegion,
            Rectangle barRegion,
            Color selfMarker,
            int[]? referencePixels,
            Rectangle referenceRegion)
        {
            int w = capture.Width, h = capture.Height;
            int[] px = GetPixels(capture);
            var st = new FrameStats { Width = w, Height = h };

            var mean = MeanColor(px, w, new Rectangle(0, 0, w, h));
            st.MeanR = mean.R; st.MeanG = mean.G; st.MeanB = mean.B;
            st.Luminance = Luminance(mean);

            // 自身标记只在"静态纯色块"区域内统计：
            // 该区域在场景里恒为 RGB(24,92,168)，出现品红只可能是本窗口的标记；
            // 若对整幅图统计，场景的色相循环区转到品红色相时会造成假阳性。
            st.SelfMarkerFraction = FractionNear(px, w, staticRegion, selfMarker, 60);
            st.StaticPatchFraction = FractionNear(px, w, staticRegion, SceneRef.StaticPatch, 12);

            if (hueRegion.Width > 0 && hueRegion.Height > 0)
            {
                var hueMean = MeanColor(px, w, hueRegion);
                st.HueDegrees = HueOf(hueMean);
            }
            if (barRegion.Width > 0 && barRegion.Height > 0)
            {
                st.BarBrightness = Luminance(MeanColor(px, w, barRegion));
            }

            if (referencePixels != null)
            {
                // 参考图与捕获同尺寸，比较区域按 referenceRegion 映射
                int rx = referenceRegion.X, ry = referenceRegion.Y;
                int rw = referenceRegion.Width, rh = referenceRegion.Height;
                rx = Math.Max(0, Math.Min(rx, w - 1));
                ry = Math.Max(0, Math.Min(ry, h - 1));
                rw = Math.Min(rw, w - rx);
                rh = Math.Min(rh, h - ry);
                if (rw > 0 && rh > 0)
                {
                    long sum = 0, n = 0;
                    for (int y = 0; y < rh; y++)
                    {
                        for (int x = 0; x < rw; x++)
                        {
                            int c1 = px[(ry + y) * w + (rx + x)];
                            int c2 = referencePixels[(ry + y) * w + (rx + x)];
                            sum += Math.Abs(((c1 >> 16) & 0xFF) - ((c2 >> 16) & 0xFF));
                            sum += Math.Abs(((c1 >> 8) & 0xFF) - ((c2 >> 8) & 0xFF));
                            sum += Math.Abs((c1 & 0xFF) - (c2 & 0xFF));
                            n += 3;
                        }
                    }
                    st.MeanAbsDiffVsReference = n == 0 ? -1 : (double)sum / n;
                }
            }

            return st;
        }
    }

    /// <summary>分析阶段用到的场景常量（与 Probe.ScenePainter.StaticPatch 保持一致）。</summary>
    internal static class SceneRef
    {
        public static readonly Color StaticPatch = Color.FromArgb(255, 24, 92, 168);
    }
}
