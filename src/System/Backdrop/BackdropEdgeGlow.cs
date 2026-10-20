using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LiteMonitor.src.SystemServices.Backdrop
{
    // ================================================================
    // 边缘辉光（背景层最上层的装饰，不覆盖文字）
    // ================================================================

    /// <summary>
    /// 背景边缘辉光。
    ///
    /// ★ 合同要点：
    /// 1. 强度 0 时**完全跳过**：不创建画刷、不绘制、不分配位图。
    /// 2. 辉光颜色取背景染色色（<c>options.TintColor</c>），alpha 随强度线性变化；
    ///    本组件不消费 <c>BorderColor</c> / <c>ShadowColor</c>（那属于外框/窗口辉光）。
    /// 3. 四条边各画一条由外向内淡出的渐变带；左右两条**避开上下角**，
    ///    避免角点重复叠加导致亮度不一致。
    /// 4. 不使用位图，纯画刷绘制。
    /// </summary>
    internal static class BackdropEdgeGlow
    {
        /// <summary>强度 100 时的辉光带宽度（96 DPI 逻辑像素，实际按当前比例缩放一次）。</summary>
        private const int MaxWidthAt96Dpi = 24;

        /// <summary>
        /// 绘制边缘辉光。
        /// </summary>
        /// <param name="g">目标画布（调用方已做 Save/Restore 隔离）。</param>
        /// <param name="bounds">完整客户区。</param>
        /// <param name="glowColor">辉光颜色（取自背景染色色）。</param>
        /// <param name="intensity">强度 0–100（已归一化）。</param>
        /// <param name="scale">当前窗口的合成比例（DPI × 用户缩放），来自 options。</param>
        public static void Draw(Graphics g, Rectangle bounds, Color glowColor, int intensity, float scale)
        {
            if (intensity <= 0) return;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            int alpha = (int)Math.Round(255.0 * (intensity / 100.0) * (glowColor.A / 255.0), MidpointRounding.AwayFromZero);
            if (alpha <= 0) return;

            double width = Math.Round(MaxWidthAt96Dpi * (intensity / 100.0) * (scale <= 0 ? 1f : scale), MidpointRounding.AwayFromZero);
            int band = (int)width;
            if (band < 1) band = 1;
            if (band > bounds.Width) band = bounds.Width;
            if (band > bounds.Height) band = bounds.Height;
            if (band < 1) return;

            Color edge = Color.FromArgb(alpha, glowColor.R, glowColor.G, glowColor.B);
            Color inner = Color.FromArgb(0, glowColor.R, glowColor.G, glowColor.B);

            // 上边：由上向下淡出
            using (var brush = new LinearGradientBrush(
                new PointF(bounds.Left, bounds.Top),
                new PointF(bounds.Left, bounds.Top + band),
                edge, inner))
            {
                g.FillRectangle(brush, new Rectangle(bounds.Left, bounds.Top, bounds.Width, band));
            }

            // 下边：由下向上淡出
            using (var brush = new LinearGradientBrush(
                new PointF(bounds.Left, bounds.Bottom),
                new PointF(bounds.Left, bounds.Bottom - band),
                edge, inner))
            {
                g.FillRectangle(brush, new Rectangle(bounds.Left, bounds.Bottom - band, bounds.Width, band));
            }

            int sideHeight = bounds.Height - band * 2;
            if (sideHeight <= 0) return;

            // 左边（避开上下角）
            using (var brush = new LinearGradientBrush(
                new PointF(bounds.Left, bounds.Top + band),
                new PointF(bounds.Left + band, bounds.Top + band),
                edge, inner))
            {
                g.FillRectangle(brush, new Rectangle(bounds.Left, bounds.Top + band, band, sideHeight));
            }

            // 右边（避开上下角）
            using (var brush = new LinearGradientBrush(
                new PointF(bounds.Right, bounds.Top + band),
                new PointF(bounds.Right - band, bounds.Top + band),
                edge, inner))
            {
                g.FillRectangle(brush, new Rectangle(bounds.Right - band, bounds.Top + band, band, sideHeight));
            }
        }
    }
}
