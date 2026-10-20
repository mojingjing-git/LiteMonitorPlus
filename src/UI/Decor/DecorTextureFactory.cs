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
    ///   需要缓存复用时请通过 <see cref="DecorResourceCache.GetOrAddBitmap"/> 交给缓存托管；
    ///   纹理瓦片请用 <see cref="DecorResourceCache.GetOrAddTextureBrush"/>——
    ///   缓存会同时持有<b>源位图与画刷</b>并一起释放（画刷不复制源位图）。
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
        ///
        /// ★ 斜线族：<c>x + y = phase + k × period</c>（k 为整数，斜率恒为 −1）。
        ///   - <c>period</c> 必须是宽高的<b>公约数</b>，否则横向 / 纵向平铺后接缝处的斜线会错位；
        ///     取值规则见 <see cref="GetBarTexturePeriod"/>（取最接近 4 倍线宽的那一个公约数，
        ///     线间距 ≈ 3~4 倍线宽，斜纹密度合适）；
        ///   - <c>phase = period / 2</c> 让斜线落在瓦片<b>内部</b>。
        ///     旧实现把两条线正好画在角点上（x+y=0 与 x+y=2×size），
        ///     瓦片内部一个像素都没有，条里就只剩一层平底。
        ///
        /// ★ 逐像素按<b>面积覆盖率</b>算 alpha（不交给 GDI+ 抗锯齿）：
        ///   覆盖率<b>只取决于 x + y</b>，所以同一条斜线上的像素完全一致，
        ///   平铺后沿 (x+1, y−1) 方向<b>逐像素连续</b>，接缝处不会断链。
        ///   （旧实现用 GDI+ 画线，笔带在瓦片边界被裁断，
        ///   边界像素的覆盖率与斜线中段不一致，平铺后斜线在接缝处会断。）
        /// </summary>
        /// <param name="widthPx">瓦片宽度（物理像素）。</param>
        /// <param name="heightPx">瓦片高度（物理像素）。</param>
        /// <param name="color">斜纹颜色（alpha 自行给定）。</param>
        /// <returns>新建位图（调用方负责释放）；尺寸非法时返回 null。</returns>
        public static Bitmap? CreateBarTextureTile(int widthPx, int heightPx, Color color)
        {
            if (widthPx <= 0 || heightPx <= 0) return null;

            int thickness = GetBarTextureThickness(widthPx, heightPx);
            int period = GetBarTexturePeriod(widthPx, heightPx);
            int phase = period / 2;

            // 线的垂直半宽换算到 x+y 坐标（斜率 −1 ⇒ 垂直距离 = |x+y−c| / √2）
            double halfWidth = thickness / Math.Sqrt(2.0);

            Bitmap bmp = new Bitmap(widthPx, heightPx, PixelFormat.Format32bppPArgb);
            for (int y = 0; y < heightPx; y++)
            {
                for (int x = 0; x < widthPx; x++)
                {
                    double coverage = StripeCoverage(x + y, period, phase, halfWidth);
                    if (coverage <= 0) continue;

                    int alpha = (int)Math.Round(color.A * coverage, MidpointRounding.AwayFromZero);
                    if (alpha <= 0) continue;

                    bmp.SetPixel(x, y, Color.FromArgb(alpha, color.R, color.G, color.B));
                }
            }
            return bmp;
        }

        /// <summary>
        /// 进度条纹理瓦片的<b>斜纹线宽</b>（物理像素）：min(宽, 高) / 8，<b>至少 2 像素</b>。
        ///
        /// ★ 为什么下限是 2：1 像素宽的 45° 线在每个像素上的覆盖率最多只有 50%，
        ///   而纹理条的"半透明底"正好是阈值色的一半 alpha —— 两者一样，
        ///   斜纹就<b>完全看不见</b>（条里只剩一层平底）。
        /// </summary>
        /// <param name="widthPx">瓦片宽度（物理像素）。</param>
        /// <param name="heightPx">瓦片高度（物理像素）。</param>
        /// <returns>线宽（物理像素）；尺寸非法时返回 0。</returns>
        public static int GetBarTextureThickness(int widthPx, int heightPx)
        {
            if (widthPx <= 0 || heightPx <= 0) return 0;
            return Math.Max(2, Math.Min(widthPx, heightPx) / 8);
        }

        /// <summary>
        /// 进度条纹理瓦片的<b>斜纹周期</b>（物理像素，即斜线族 <c>x+y = phase + k×period</c> 的 period）。
        ///
        /// ★ 必须是宽高的<b>公约数</b>（只有这样，相邻瓦片的相位才不会错位，平铺才连续）；
        ///   这里取<b>最接近 4 倍线宽</b>的那一个公约数（线间距 ≈ 3~4 倍线宽）。
        ///   公开出来是为了让探针按"同一相位族"核对斜纹，不必复制实现细节。
        /// </summary>
        /// <param name="widthPx">瓦片宽度（物理像素）。</param>
        /// <param name="heightPx">瓦片高度（物理像素）。</param>
        /// <returns>周期（物理像素）；尺寸非法时返回 0。</returns>
        public static int GetBarTexturePeriod(int widthPx, int heightPx)
        {
            if (widthPx <= 0 || heightPx <= 0) return 0;

            int g = GreatestCommonDivisor(widthPx, heightPx);
            int target = GetBarTextureThickness(widthPx, heightPx) * 4;

            int best = 1;
            int bestDiff = int.MaxValue;
            for (int d = 1; d <= g; d++)
            {
                if (g % d != 0) continue;
                int diff = Math.Abs(d - target);
                if (diff < bestDiff || (diff == bestDiff && d > best))
                {
                    best = d;
                    bestDiff = diff;
                }
            }
            return best;
        }

        /// <summary>
        /// 像素的斜纹覆盖率。
        ///
        /// ★ 像素沿 45° 对角线的 <c>x+y</c> 范围是 [k, k+2]，其上"面积密度"是一个
        ///   底 2、高 1 的三角形（峰值在 k+1）；斜带在 x+y 坐标上的范围是 [c−h, c+h]。
        ///   覆盖率 = 密度在两者交集上的积分，<b>只取决于 k</b>——
        ///   这正是"平铺后斜线逐像素连续"的前提。
        /// </summary>
        private static double StripeCoverage(int k, int period, int phase, double halfWidth)
        {
            double s0 = k;
            double s1 = k + 2.0;
            double total = 0.0;

            // 可能与该像素相交的斜线：c = phase + n × period
            int first = (int)Math.Floor((s0 - halfWidth - phase) / period);
            int last = (int)Math.Ceiling((s1 + halfWidth - phase) / period);

            for (int n = first; n <= last; n++)
            {
                double c = phase + n * period;
                double lo = Math.Max(s0, c - halfWidth);
                double hi = Math.Min(s1, c + halfWidth);
                if (hi <= lo) continue;

                total += TriCdf(hi, s0, s1) - TriCdf(lo, s0, s1);
            }

            if (total < 0) return 0.0;
            return total > 1.0 ? 1.0 : total;
        }

        /// <summary>三角形密度（区间 [s0, s1]，峰值在中点）的累积分布。</summary>
        private static double TriCdf(double s, double s0, double s1)
        {
            if (s <= s0) return 0.0;
            if (s >= s1) return 1.0;

            double mid = (s0 + s1) / 2.0;
            double span = s1 - s0;
            if (s <= mid)
            {
                double d = s - s0;
                return d * d / span;
            }
            double e = s1 - s;
            return 1.0 - e * e / span;
        }

        /// <summary>最大公约数（平铺周期用）。</summary>
        private static int GreatestCommonDivisor(int a, int b)
        {
            while (b != 0)
            {
                int t = a % b;
                a = b;
                b = t;
            }
            return a <= 0 ? 1 : a;
        }

        private static int ClampIntensity(int value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }
    }
}
