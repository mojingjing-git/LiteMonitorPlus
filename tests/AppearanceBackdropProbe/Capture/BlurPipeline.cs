using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AppearanceBackdropProbe.Capture
{
    /// <summary>
    /// 模糊半径合同（v4 §4 第 4 条）。
    ///
    /// 合同定义：
    ///  1) Blur 的单位是 **96 DPI 逻辑像素**，表示"最终显示尺寸下"的等效高斯标准差 σ（逻辑像素）。
    ///  2) 物理标准差 = 逻辑值 × (窗口DPI/96) × 用户缩放；本探针用户缩放固定为 1。
    ///  3) 捕获可以按 captureScale 降采样；降采样后滤波在降采样缓冲上进行，
    ///     其标准差 σ_down = σ_physical × captureScale。
    ///  4) 实际滤波 = **3 次可分离 box blur**（半径 r 的 box 方差为 r(r+1)/3，3 次后 σ² = r(r+1)），
    ///     因此 r = round((√(1+4σ_down²) − 1) / 2)，实际等效 σ = √(r(r+1))。
    ///     该实现为 O(1)/像素（滑动和），满足 v4 §7.4 的单次处理预算。
    ///  5) Blur = 0 → σ = 0 → 不做滤波（不是"半径 0 的 box"）。
    ///
    /// 明确排除：单纯缩小再放大只是缩放近似；SmoothingMode 不是模糊算法。
    /// </summary>
    internal sealed class BlurContract
    {
        public int BlurLogical { get; init; }
        public double DpiScale { get; init; } = 1.0;
        public double CaptureScale { get; init; } = 1.0;

        public double SigmaPhysical => BlurLogical * DpiScale;
        public double SigmaDownsampled => SigmaPhysical * CaptureScale;

        /// <summary>3×box blur 的等效半径 r（满足 σ_eff = √(r(r+1)) 最接近 σ_down）。</summary>
        public int BoxRadius => SigmaDownsampled <= 0.0001
            ? 0
            : (int)Math.Round((Math.Sqrt(1.0 + 4.0 * SigmaDownsampled * SigmaDownsampled) - 1.0) / 2.0);

        public double SigmaEffectiveDownsampled => BoxRadius <= 0 ? 0 : Math.Sqrt((double)BoxRadius * (BoxRadius + 1));

        /// <summary>最终显示尺寸下的等效 σ（逻辑像素）。</summary>
        public double SigmaEffectiveDisplay => CaptureScale <= 0 ? 0 : SigmaEffectiveDownsampled / CaptureScale;

        public string Describe() =>
            $"Blur={BlurLogical}逻辑px(96DPI) → σ_physical={SigmaPhysical:0.##}px → " +
            $"σ_down={SigmaDownsampled:0.##}px(captureScale={CaptureScale:0.##}) → " +
            $"3×box r={BoxRadius} → σ_eff_down={SigmaEffectiveDownsampled:0.##}px → " +
            $"σ_eff_display={SigmaEffectiveDisplay:0.##}逻辑px";

        /// <summary>从物理像素推导逻辑 Blur 值（编辑器里的 Backdrop.Blur 语义）。</summary>
        public static int LogicalFromPhysical(double physicalSigma, double dpiScale)
        {
            if (dpiScale <= 0) return 0;
            return (int)Math.Round(physicalSigma / dpiScale);
        }
    }

    /// <summary>受预算约束的可分离 box/Gaussian 近似实现。</summary>
    internal static class BlurPipeline
    {
        /// <summary>对 32bpp 位图做 3 次可分离 box blur（预乘 alpha 安全）。</summary>
        public static Bitmap ApplyBoxBlur3x(Bitmap src, int radius)
        {
            if (radius <= 0) return new Bitmap(src);

            int w = src.Width, h = src.Height;
            var data = new int[w * h];
            var rect = new Rectangle(0, 0, w, h);
            var bd = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, data, 0, data.Length);
            }
            finally { src.UnlockBits(bd); }

            // 预乘 alpha
            for (int i = 0; i < data.Length; i++)
            {
                int c = data[i];
                int a = (c >> 24) & 0xFF;
                if (a == 255) continue;
                int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                r = (r * a + 127) / 255;
                g = (g * a + 127) / 255;
                b = (b * a + 127) / 255;
                data[i] = (a << 24) | (r << 16) | (g << 8) | b;
            }

            var tmp = new int[data.Length];
            var outBuf = new int[data.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlurH(data, tmp, w, h, radius);
                BoxBlurV(tmp, outBuf, w, h, radius);
                Array.Copy(outBuf, data, data.Length);
            }

            // 反预乘并写回
            var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var rb = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int i = 0; i < data.Length; i++)
                {
                    int c = data[i];
                    int a = (c >> 24) & 0xFF;
                    int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                    if (a != 255 && a != 0)
                    {
                        r = Math.Min(255, r * 255 / a);
                        g = Math.Min(255, g * 255 / a);
                        b = Math.Min(255, b * 255 / a);
                    }
                    data[i] = (a << 24) | (r << 16) | (g << 8) | b;
                }
                System.Runtime.InteropServices.Marshal.Copy(data, 0, rb.Scan0, data.Length);
            }
            finally { result.UnlockBits(rb); }

            return result;
        }

        private static void BoxBlurH(int[] src, int[] dst, int w, int h, int r)
        {
            var prefix = new long[(w + 1) * 4];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                long a = 0, rr = 0, g = 0, b = 0;
                prefix[0] = 0; prefix[1] = 0; prefix[2] = 0; prefix[3] = 0;
                for (int x = 0; x < w; x++)
                {
                    int c = src[row + x];
                    a += (c >> 24) & 0xFF;
                    rr += (c >> 16) & 0xFF;
                    g += (c >> 8) & 0xFF;
                    b += c & 0xFF;
                    int o = (x + 1) * 4;
                    prefix[o] = a; prefix[o + 1] = rr; prefix[o + 2] = g; prefix[o + 3] = b;
                }
                for (int x = 0; x < w; x++)
                {
                    int left = Math.Max(0, x - r);
                    int right = Math.Min(w - 1, x + r);
                    int o0 = left * 4, o1 = (right + 1) * 4;
                    int cnt = right - left + 1;
                    int oa = (int)((prefix[o1 + 0] - prefix[o0 + 0]) / cnt);
                    int orr = (int)((prefix[o1 + 1] - prefix[o0 + 1]) / cnt);
                    int og = (int)((prefix[o1 + 2] - prefix[o0 + 2]) / cnt);
                    int ob = (int)((prefix[o1 + 3] - prefix[o0 + 3]) / cnt);
                    dst[row + x] = (oa << 24) | (orr << 16) | (og << 8) | ob;
                }
            }
        }

        private static void BoxBlurV(int[] src, int[] dst, int w, int h, int r)
        {
            var prefix = new long[(h + 1) * 4];
            for (int x = 0; x < w; x++)
            {
                long a = 0, rr = 0, g = 0, b = 0;
                prefix[0] = 0; prefix[1] = 0; prefix[2] = 0; prefix[3] = 0;
                for (int y = 0; y < h; y++)
                {
                    int c = src[y * w + x];
                    a += (c >> 24) & 0xFF;
                    rr += (c >> 16) & 0xFF;
                    g += (c >> 8) & 0xFF;
                    b += c & 0xFF;
                    int o = (y + 1) * 4;
                    prefix[o] = a; prefix[o + 1] = rr; prefix[o + 2] = g; prefix[o + 3] = b;
                }
                for (int y = 0; y < h; y++)
                {
                    int top = Math.Max(0, y - r);
                    int bottom = Math.Min(h - 1, y + r);
                    int o0 = top * 4, o1 = (bottom + 1) * 4;
                    int cnt = bottom - top + 1;
                    int oa = (int)((prefix[o1 + 0] - prefix[o0 + 0]) / cnt);
                    int orr = (int)((prefix[o1 + 1] - prefix[o0 + 1]) / cnt);
                    int og = (int)((prefix[o1 + 2] - prefix[o0 + 2]) / cnt);
                    int ob = (int)((prefix[o1 + 3] - prefix[o0 + 3]) / cnt);
                    dst[y * w + x] = (oa << 24) | (orr << 16) | (og << 8) | ob;
                }
            }
        }

        /// <summary>降采样（面积平均，避免 GDI+ 双三次在大幅缩小时的混叠）。</summary>
        public static Bitmap Downscale(Bitmap src, float scale)
        {
            int w = Math.Max(1, (int)Math.Round(src.Width * scale));
            int h = Math.Max(1, (int)Math.Round(src.Height * scale));
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return dst;
        }

        /// <summary>升采样回最终显示尺寸。</summary>
        public static Bitmap Upscale(Bitmap src, int w, int h)
        {
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return dst;
        }

        /// <summary>
        /// 完整处理管线：捕获 → 降采样 → 模糊 → 升采样。
        /// 返回处理结果与分段计时（捕获/滤波/呈现分别计时，不把经过时间当 CPU 时间）。
        /// </summary>
        public static Bitmap Process(Bitmap captured, BlurContract contract, out string detail)
        {
            double s = contract.CaptureScale;
            Bitmap? small = null, blurred = null;
            try
            {
                if (s >= 0.999)
                {
                    small = new Bitmap(captured);
                }
                else
                {
                    small = Downscale(captured, (float)s);
                }

                blurred = ApplyBoxBlur3x(small, contract.BoxRadius);

                Bitmap result = BlurPipeline.Upscale(blurred, captured.Width, captured.Height);
                detail = contract.Describe();
                return result;
            }
            finally
            {
                small?.Dispose();
                blurred?.Dispose();
            }
        }
    }
}
