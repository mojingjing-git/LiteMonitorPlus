using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LiteMonitor.src.UI.Decor
{
    /// <summary>
    /// 装饰纹理 / 渐变位图工厂。
    ///
    /// ★ 所有权：本类返回的位图<b>由调用方拥有并负责释放</b>。
    ///   需要缓存复用时请通过 <see cref="DecorResourceCache.GetOrAddBitmap"/> 交给缓存托管。
    ///
    /// ★ 渐变约定：角度按<b>屏幕坐标顺时针为正</b>——
    ///   0° = 左 → 右（起始色在左，结束色在右），
    ///   90° = 上 → 下，180° = 右 → 左，270° = 下 → 上。
    ///   渐变始终按<b>完整条槽</b>计算（覆盖整个槽位），
    ///   绘制时再按当前填充比例裁剪，<b>不会为每个百分比单独建缓存</b>。
    /// </summary>
    public static class DecorTextureFactory
    {
        /// <summary>
        /// 创建"完整条槽"的渐变位图：尺寸 = 槽位物理像素尺寸，渐变覆盖整块槽位。
        /// 绘制时用 <see cref="DecorPainter.FillGradientSlot"/> 按填充比例裁剪。
        /// </summary>
        /// <param name="widthPx">槽位宽度（物理像素）。</param>
        /// <param name="heightPx">槽位高度（物理像素）。</param>
        /// <param name="startColor">起始色（角度 0° 时在左）。</param>
        /// <param name="endColor">结束色（角度 0° 时在右）。</param>
        /// <param name="angleDegrees">渐变角度（屏幕坐标顺时针为正）。</param>
        /// <returns>新建位图（调用方负责释放）；尺寸非法时返回 null。</returns>
        public static Bitmap? CreateGradientSlot(int widthPx, int heightPx, Color startColor, Color endColor, double angleDegrees)
        {
            if (widthPx <= 0 || heightPx <= 0) return null;

            Bitmap bmp = new Bitmap(widthPx, heightPx, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using (LinearGradientBrush brush = CreateGradientBrush(widthPx, heightPx, startColor, endColor, angleDegrees))
                {
                    g.FillRectangle(brush, new Rectangle(0, 0, widthPx, heightPx));
                }
            }
            return bmp;
        }

        /// <summary>
        /// 创建覆盖整块槽位的线性渐变画刷（角度同 <see cref="CreateGradientSlot"/>）。
        ///
        /// ★ 角度约定经实测确认（GDI+ 矩形 + 角度构造器）：
        ///   0° = 左 → 右（起始色在左）、90° = 上 → 下、180° = 右 → 左、270° = 下 → 上。
        /// </summary>
        /// <param name="widthPx">槽位宽度（物理像素）。</param>
        /// <param name="heightPx">槽位高度（物理像素）。</param>
        /// <param name="startColor">起始色。</param>
        /// <param name="endColor">结束色。</param>
        /// <param name="angleDegrees">渐变角度（屏幕坐标顺时针为正）。</param>
        public static LinearGradientBrush CreateGradientBrush(int widthPx, int heightPx, Color startColor, Color endColor, double angleDegrees)
        {
            Rectangle rect = new Rectangle(0, 0, Math.Max(1, widthPx), Math.Max(1, heightPx));

            if (double.IsNaN(angleDegrees) || double.IsInfinity(angleDegrees)) angleDegrees = 0.0;
            float angle = (float)NormalizeAngle(angleDegrees);

            // 注意：LinearGradientBrush 的 WrapMode 只能是 Tile（设置别的值会抛 ArgumentException）；
            // 渐变按"本槽位矩形"定义，绘制时也只填同一块矩形，因此不会平铺出错。
            return new LinearGradientBrush(rect, startColor, endColor, angle);
        }

        private static double NormalizeAngle(double angleDegrees)
        {
            double a = angleDegrees % 360.0;
            if (a < 0) a += 360.0;
            return a;
        }

        /// <summary>
        /// 创建噪点瓦片（可平铺）。同一 <paramref name="seed"/> 必定得到同一张图。
        /// 噪点透明度 = <paramref name="color"/> 的 alpha × <paramref name="strength"/> / 100。
        /// </summary>
        /// <param name="widthPx">瓦片宽度（物理像素）。</param>
        /// <param name="heightPx">瓦片高度（物理像素）。</param>
        /// <param name="seed">随机种子。</param>
        /// <param name="color">噪点颜色（alpha 是强度上限）。</param>
        /// <param name="strength">噪点强度（0–100）。</param>
        /// <returns>新建位图（调用方负责释放）；尺寸非法或强度为 0 时返回 null。</returns>
        public static Bitmap? CreateNoiseTile(int widthPx, int heightPx, int seed, Color color, int strength)
        {
            if (widthPx <= 0 || heightPx <= 0) return null;

            int level = ClampIntensity(strength);
            if (level <= 0) return null;

            byte alpha = (byte)Math.Min(255, Math.Round(color.A * level / 100.0, MidpointRounding.AwayFromZero));
            if (alpha == 0) return null;
            Color c = Color.FromArgb(alpha, color.R, color.G, color.B);

            Bitmap bmp = new Bitmap(widthPx, heightPx, PixelFormat.Format32bppPArgb);
            uint state = unchecked((uint)seed) | 1u;

            for (int y = 0; y < heightPx; y++)
            {
                for (int x = 0; x < widthPx; x++)
                {
                    // xorshift32：确定性的伪随机序列
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    byte v = (byte)(state & 0xFF);
                    if (v < 128) continue;   // 一半像素留空，避免噪点过密
                    bmp.SetPixel(x, y, c);
                }
            }
            return bmp;
        }

        /// <summary>
        /// 创建扫描线瓦片（水平细线，可平铺）。
        /// <b>瓦片高度就是线间距</b>，线宽固定 1 物理像素。
        /// </summary>
        /// <param name="widthPx">瓦片宽度（物理像素）。</param>
        /// <param name="heightPx">线间距（物理像素，至少 1）。</param>
        /// <param name="color">线颜色（alpha 自行给定）。</param>
        /// <returns>新建位图（调用方负责释放）；尺寸非法时返回 null。</returns>
        public static Bitmap? CreateScanlineTile(int widthPx, int heightPx, Color color)
        {
            if (widthPx <= 0 || heightPx <= 0) return null;
            int height = Math.Max(1, heightPx);

            Bitmap bmp = new Bitmap(widthPx, height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(color))
                {
                    g.FillRectangle(brush, new Rectangle(0, 0, widthPx, 1));
                }
            }
            return bmp;
        }

        /// <summary>
        /// 创建网格瓦片（正交细线，可平铺）。
        /// <b>瓦片宽高就是网格间距</b>，线宽固定 1 物理像素。
        /// </summary>
        /// <param name="widthPx">网格间距（物理像素）。</param>
        /// <param name="heightPx">网格间距（物理像素）。</param>
        /// <param name="color">线颜色（alpha 自行给定）。</param>
        /// <returns>新建位图（调用方负责释放）；尺寸非法时返回 null。</returns>
        public static Bitmap? CreateGridTile(int widthPx, int heightPx, Color color)
        {
            if (widthPx <= 0 || heightPx <= 0) return null;

            Bitmap bmp = new Bitmap(widthPx, heightPx, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(color))
                {
                    g.FillRectangle(brush, new Rectangle(0, 0, widthPx, 1));
                    g.FillRectangle(brush, new Rectangle(0, 0, 1, heightPx));
                }
            }
            return bmp;
        }

        /// <summary>
        /// 创建进度条纹理瓦片（45° 斜纹，可平铺）。
        /// 斜纹宽度取 min(宽, 高) / 8（至少 1 物理像素）。
        /// </summary>
        /// <param name="widthPx">瓦片宽度（物理像素）。</param>
        /// <param name="heightPx">瓦片高度（物理像素）。</param>
        /// <param name="color">斜纹颜色（alpha 自行给定）。</param>
        /// <returns>新建位图（调用方负责释放）；尺寸非法时返回 null。</returns>
        public static Bitmap? CreateBarTextureTile(int widthPx, int heightPx, Color color)
        {
            if (widthPx <= 0 || heightPx <= 0) return null;

            int thickness = Math.Max(1, Math.Min(widthPx, heightPx) / 8);
            int size = Math.Max(widthPx, heightPx);

            Bitmap bmp = new Bitmap(widthPx, heightPx, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush brush = new SolidBrush(color))
                using (Pen pen = new Pen(brush, thickness))
                {
                    // 45° 斜纹：两组平行线，间距 = 瓦片边长
                    g.DrawLine(pen, -size, size, size, -size);
                    g.DrawLine(pen, 0, size * 2, size * 2, 0);
                }
            }
            return bmp;
        }

        private static int ClampIntensity(int value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }
    }
}
