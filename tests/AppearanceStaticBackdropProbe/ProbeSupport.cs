using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;

namespace LiteMonitor.AppearanceStaticBackdropProbe
{
    // ================================================================
    // 自证工程的公共设施（画布、哈希、选项构造、假后端）
    // ================================================================

    /// <summary>单个用例的结果。</summary>
    internal sealed class CaseResult
    {
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public bool Passed { get; set; }
        public List<string> Failures { get; set; } = new();
        public Dictionary<string, object?> Details { get; set; } = new();
    }

    /// <summary>假的后端（只用于自证；不是捕获实现，也不含任何抓屏 API）。</summary>
    internal sealed class FakeFrameSource : IBackdropFrameSource
    {
        public bool IsReady { get; set; } = true;
        public string UnavailableReason { get; set; } = "假后端未就绪";
        public Image? Frame { get; set; }
        public int TryGetFrameCalls { get; private set; }
        public double LastBlurPx { get; private set; } = -1;

        public bool TryGetFrame(double blurPx, out Image? frame)
        {
            TryGetFrameCalls++;
            LastBlurPx = blurPx;
            frame = Frame;
            return Frame != null;
        }
    }

    internal static class ProbeSupport
    {
        /// <summary>创建一个 32bpp 离屏画布。</summary>
        public static Bitmap NewSurface(int width, int height)
        {
            return new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }

        /// <summary>
        /// 复刻旧背景路径：UIRenderer 用 (-5,-5) 起的矩形铺满可见区。
        /// 这里只用于"旧路径像素基准"，不代表新组件的定位方式。
        /// </summary>
        public static void FillLegacyBackground(Bitmap surface, Color background)
        {
            using Graphics g = Graphics.FromImage(surface);
            using var brush = new SolidBrush(background);
            g.FillRectangle(brush, new Rectangle(-5, -5, surface.Width + 10, surface.Height + 10));
        }

        /// <summary>画布像素的 SHA256（原始字节，BGRA 顺序）。</summary>
        public static string Hash(Bitmap surface)
        {
            Rectangle rect = new Rectangle(0, 0, surface.Width, surface.Height);
            BitmapData data = surface.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int length = Math.Abs(data.Stride) * data.Height;
                byte[] buffer = new byte[length];
                Marshal.Copy(data.Scan0, buffer, 0, length);
                using SHA256 sha = SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(buffer));
            }
            finally
            {
                surface.UnlockBits(data);
            }
        }

        /// <summary>构造外观快照（不修改任何用户 JSON，只在内存里造运行时副本）。</summary>
        public static ThemeAppearanceOptions MakeOptions(
            BackdropConfig? backdrop,
            ColorConfig? color = null,
            Rectangle? bounds = null,
            Image? previewSource = null,
            Image? backgroundFrame = null,
            float dpiScale = 1f,
            float userScale = 1f)
        {
            var theme = new Theme
            {
                Name = "A08Probe",
                Version = 4,
                Color = color ?? new ColorConfig(),
                Backdrop = backdrop
            };
            return ThemeAppearanceOptions.Create(
                theme,
                dpiScale,
                userScale,
                bounds ?? new Rectangle(0, 0, 64, 48),
                1,
                backgroundFrame,
                previewSource);
        }

        /// <summary>把整幅画布平均成单个颜色（用于方向性判定）。</summary>
        public static Color AverageColor(Bitmap surface, Rectangle area)
        {
            long r = 0, g = 0, b = 0;
            long count = 0;
            for (int y = area.Top; y < area.Bottom; y++)
            {
                for (int x = area.Left; x < area.Right; x++)
                {
                    Color c = surface.GetPixel(x, y);
                    r += c.R;
                    g += c.G;
                    b += c.B;
                    count++;
                }
            }
            if (count == 0) return Color.Black;
            return Color.FromArgb((int)(r / count), (int)(g / count), (int)(b / count));
        }

        /// <summary>两个颜色的最大通道差。</summary>
        public static int ChannelDistance(Color a, Color b)
        {
            return Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
        }

        /// <summary>
        /// 测量一次渲染的线程分配字节数（先预热，避免 JIT 噪声）。
        /// </summary>
        public static long MeasureAlloc(Action action, int warmup = 3)
        {
            for (int i = 0; i < warmup; i++) action();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            long after = GC.GetAllocatedBytesForCurrentThread();
            return after - before;
        }

        /// <summary>把字节数格式化成便于阅读的字符串。</summary>
        public static string FormatBytes(long bytes)
        {
            return bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " B";
        }
    }
}
