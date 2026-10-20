using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LiteMonitor.src.SystemServices.Backdrop
{
    // ================================================================
    // 静态渐变：支持任意角度
    // ================================================================

    /// <summary>
    /// 静态渐变的画刷构造。
    ///
    /// ★ 角度语义（本组件统一定义，主题里的 GradientAngle 按此解释）：
    /// - 0°   = 从左到右
    /// - 90°  = 从上到下（缺省值）
    /// - 180° = 从右到左
    /// - 270° = 从下到上
    /// - 其它角度 = 沿该方向线性插值
    ///
    /// 0/90/180/270 与 GDI+ 的 <c>LinearGradientMode</c>（Horizontal/Vertical）方向一致，
    /// 任意角度用"矩形中心 + 方向向量"自行求轴，避免方向反转或退化。
    /// </summary>
    public static class BackdropGradient
    {
        /// <summary>
        /// 计算渐变轴的起点与终点。
        /// </summary>
        /// <param name="bounds">填充矩形（完整客户区）。</param>
        /// <param name="angleDegrees">角度（度），按本类的角度语义解释。</param>
        /// <param name="start">渐变起点（起始色所在位置）。</param>
        /// <param name="end">渐变终点（结束色所在位置）。</param>
        /// <returns>矩形没有有效宽高或轴退化时返回 false。</returns>
        public static bool TryComputeAxis(Rectangle bounds, double angleDegrees, out PointF start, out PointF end)
        {
            start = PointF.Empty;
            end = PointF.Empty;

            if (bounds.Width <= 0 || bounds.Height <= 0) return false;
            if (double.IsNaN(angleDegrees) || double.IsInfinity(angleDegrees)) return false;

            // 角度归一化到 [0, 360)
            double angle = angleDegrees % 360.0;
            if (angle < 0) angle += 360.0;

            double rad = angle * Math.PI / 180.0;
            double dx = Math.Cos(rad);
            double dy = Math.Sin(rad);

            double cx = bounds.Left + bounds.Width / 2.0;
            double cy = bounds.Top + bounds.Height / 2.0;

            // 轴长取矩形在方向上的投影长度，保证矩形四角都被渐变覆盖
            double length = Math.Abs(bounds.Width * dx) + Math.Abs(bounds.Height * dy);
            if (length <= 0) return false;

            start = new PointF((float)(cx - dx * length / 2.0), (float)(cy - dy * length / 2.0));
            end = new PointF((float)(cx + dx * length / 2.0), (float)(cy + dy * length / 2.0));
            return true;
        }

        /// <summary>
        /// 创建渐变画刷。矩形无效时返回 null（调用方跳过绘制，不抛异常）。
        /// </summary>
        public static LinearGradientBrush? CreateBrush(Rectangle bounds, double angleDegrees, Color startColor, Color endColor)
        {
            if (!TryComputeAxis(bounds, angleDegrees, out PointF start, out PointF end)) return null;
            return new LinearGradientBrush(start, end, startColor, endColor);
        }
    }
}
