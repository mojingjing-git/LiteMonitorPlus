using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LiteMonitor.src.UI.Decor
{
    /// <summary>
    /// 装饰绘制原语：内描边、高光、条槽渐变 / 纹理裁剪、平铺纹理。
    ///
    /// ★ 层次合同（v4 §3.3，固定顺序）：
    /// 底板 / 背景 → 窗口底纹 → 组块填充 → 组底纹 / 组框 →
    /// 原有标题 / 指标 / 条 → <b>向内的窗口边框 / 高光</b>。
    /// 扫描线与网格属于"底纹"，<b>必须早于文字绘制</b>，不能盖住数值。
    /// 本类只提供原语，层次由调用方（A07 的 DecorRenderer / A13 的接线）保证。
    ///
    /// ★ 状态隔离：所有绘制方法都用 <c>Graphics.Save()</c> / <c>Restore()</c> 包裹，
    ///   不污染调用方的画笔 / 变换 / 平滑模式。
    ///
    /// ★ 不做模糊：<see cref="SmoothingMode"/> 只是抗锯齿，<b>不是模糊算法</b>（v4 §4.4）。
    /// </summary>
    public static class DecorPainter
    {
        // ============================================================
        // 窗口边框 / 高光
        // ============================================================

        /// <summary>
        /// 绘制<b>向内</b>的窗口外框（内描边带）。
        ///
        /// ★ 实现方式是"填充一条带"而不是"描边"：GDI+ 的画笔沿路径居中，
        ///   描边必然有一半落到路径外（会画到客户区外）；填充这条带则严格在路径内。
        ///
        /// ★ 外轮廓默认向内缩 1 像素（<paramref name="outerInsetPx"/>）：
        ///   抗锯齿在整数边界上仍可能给相邻像素一点覆盖率，
        ///   缩进 1 像素后这点覆盖落在客户区<b>之内</b>，保证不会溢出。
        ///
        /// @return 是否真的画了（样式关闭 / 尺寸无效 / 颜色全透明时返回 false）。
        /// </summary>
        /// <param name="g">目标画布（不得为 null）。</param>
        /// <param name="bounds">完整客户区矩形（物理像素）。</param>
        /// <param name="frameStyle">外框样式（见 <see cref="DecorGeometry.FrameStyle*"/>）。</param>
        /// <param name="frameWidthPx">描边宽度（物理像素，options.FrameWidthPx）。</param>
        /// <param name="cornerLengthPx">切角边长（物理像素，options.FrameLengthPx）。</param>
        /// <param name="radiusPx">圆角半径（物理像素）。</param>
        /// <param name="color">描边颜色（调用方必须先判 FrameEnabled 再消费 BorderColor）。</param>
        /// <param name="localBandPath">
        /// 缓存里的<b>局部坐标</b>带路径（可空）。给了就用它（借用引用，不要 Dispose），
        /// 必须用<b>同样的</b> <paramref name="outerInsetPx"/> 构建；
        /// 没给就临时建一条并在本次调用结束时释放。
        /// </param>
        /// <param name="outerInsetPx">外轮廓向内缩进（物理像素，最小 1）。</param>
        public static bool FillInnerFrame(
            Graphics g,
            Rectangle bounds,
            int frameStyle,
            int frameWidthPx,
            int cornerLengthPx,
            int radiusPx,
            Color color,
            GraphicsPath? localBandPath = null,
            int outerInsetPx = 1)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (frameStyle == DecorGeometry.FrameStyleNone) return false;
            if (color.A <= 0) return false;
            if (bounds.Width <= 0 || bounds.Height <= 0) return false;

            int width = DecorGeometry.ClampFrameWidthPx(frameWidthPx, bounds);
            if (width <= 0) return false;

            int inset = Math.Max(1, outerInsetPx);
            Rectangle outer = DecorGeometry.Deflate(bounds, inset);
            if (outer.Width <= 0 || outer.Height <= 0) return false;

            GraphicsState state = g.Save();
            GraphicsPath? owned = null;
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;

                GraphicsPath path;
                if (localBandPath != null)
                {
                    path = localBandPath;
                }
                else
                {
                    owned = DecorGeometry.BuildLocalBandPath(outer.Width, outer.Height, frameStyle, cornerLengthPx, radiusPx, 0, width);
                    path = owned;
                }

                // 局部坐标 → 目标位置（两种分支都要平移，只平移不缩放）
                g.TranslateTransform(outer.X, outer.Y);

                if (path.PointCount == 0) return false;

                using (SolidBrush brush = new SolidBrush(color))
                {
                    g.FillPath(brush, path);
                }
                return true;
            }
            finally
            {
                owned?.Dispose();
                g.Restore(state);
            }
        }

        /// <summary>
        /// 绘制<b>内侧高光线 / 第二条轮廓线</b>（同样是"带"，不会溢出客户区）。
        ///
        /// @return 是否真的画了。
        /// </summary>
        /// <param name="g">目标画布（不得为 null）。</param>
        /// <param name="bounds">完整客户区矩形（物理像素）。</param>
        /// <param name="frameStyle">外框样式。</param>
        /// <param name="cornerLengthPx">切角边长（物理像素）。</param>
        /// <param name="radiusPx">圆角半径（物理像素）。</param>
        /// <param name="insetPx">相对客户区向内缩进（物理像素）。</param>
        /// <param name="bandWidthPx">线宽（物理像素，至少 1）。</param>
        /// <param name="color">线颜色。</param>
        /// <param name="localBandPath">缓存里的局部坐标带路径（可空，借用引用）。</param>
        public static bool FillInnerLine(
            Graphics g,
            Rectangle bounds,
            int frameStyle,
            int cornerLengthPx,
            int radiusPx,
            int insetPx,
            int bandWidthPx,
            Color color,
            GraphicsPath? localBandPath = null)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (frameStyle == DecorGeometry.FrameStyleNone) return false;
            if (color.A <= 0) return false;
            if (bandWidthPx <= 0) return false;
            if (bounds.Width <= 0 || bounds.Height <= 0) return false;

            Rectangle outer = DecorGeometry.Deflate(bounds, Math.Max(1, insetPx));
            if (outer.Width <= 0 || outer.Height <= 0) return false;

            GraphicsState state = g.Save();
            GraphicsPath? owned = null;
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;

                GraphicsPath path;
                if (localBandPath != null)
                {
                    path = localBandPath;
                }
                else
                {
                    owned = DecorGeometry.BuildLocalBandPath(outer.Width, outer.Height, frameStyle, cornerLengthPx, radiusPx, 0, bandWidthPx);
                    path = owned;
                }

                // 局部坐标 → 目标位置（两种分支都要平移，只平移不缩放）
                g.TranslateTransform(outer.X, outer.Y);

                if (path.PointCount == 0) return false;

                using (SolidBrush brush = new SolidBrush(color))
                {
                    g.FillPath(brush, path);
                }
                return true;
            }
            finally
            {
                owned?.Dispose();
                g.Restore(state);
            }
        }

        // ============================================================
        // 条槽绘制（渐变 / 纹理）
        // ============================================================

        /// <summary>
        /// 用<b>完整条槽</b>渐变位图绘制进度条，并按当前填充比例裁剪。
        ///
        /// ★ 关键点：渐变位图是按整条槽（满值）算好的，
        ///   这里只裁剪"取哪一段"，<b>不会为每个百分比单独建缓存</b>。
        ///
        /// @return 是否真的画了（槽位无效 / 填充值为 0 时返回 false）。
        /// </summary>
        /// <param name="g">目标画布（不得为 null）。</param>
        /// <param name="slot">缓存里的完整条槽渐变位图（<b>借用引用，不要 Dispose</b>）。</param>
        /// <param name="dest">进度条目标矩形（物理像素）。</param>
        /// <param name="fillRatio">填充比例（0–1，内部会夹紧）。</param>
        public static bool FillGradientSlot(Graphics g, Bitmap slot, Rectangle dest, double fillRatio)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (slot == null) return false;
            if (dest.Width <= 0 || dest.Height <= 0) return false;
            if (fillRatio <= 0) return false;
            if (fillRatio > 1.0) fillRatio = 1.0;

            int fillWidth = (int)Math.Round(dest.Width * fillRatio, MidpointRounding.AwayFromZero);
            if (fillWidth <= 0) return false;
            if (fillWidth > dest.Width) fillWidth = dest.Width;

            int srcWidth = (int)Math.Round(slot.Width * fillRatio, MidpointRounding.AwayFromZero);
            if (srcWidth <= 0) return false;
            if (srcWidth > slot.Width) srcWidth = slot.Width;

            Rectangle src = new Rectangle(0, 0, srcWidth, slot.Height);
            Rectangle dst = new Rectangle(dest.X, dest.Y, fillWidth, dest.Height);

            GraphicsState state = g.Save();
            try
            {
                g.InterpolationMode = slot.Width == dest.Width
                    ? InterpolationMode.NearestNeighbor
                    : InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(slot, dst, src, GraphicsUnit.Pixel);
            }
            finally
            {
                g.Restore(state);
            }
            return true;
        }

        /// <summary>
        /// 用纹理画刷绘制进度条，并按当前填充比例裁剪（只填左侧已填充部分）。
        ///
        /// @return 是否真的画了。
        /// </summary>
        /// <param name="g">目标画布（不得为 null）。</param>
        /// <param name="brush">缓存里的纹理画刷（<b>借用引用，不要 Dispose、不要改 Transform</b>）。</param>
        /// <param name="dest">进度条目标矩形（物理像素）。</param>
        /// <param name="fillRatio">填充比例（0–1）。</param>
        public static bool FillTextureSlot(Graphics g, TextureBrush brush, Rectangle dest, double fillRatio)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (brush == null) return false;
            if (dest.Width <= 0 || dest.Height <= 0) return false;
            if (fillRatio <= 0) return false;
            if (fillRatio > 1.0) fillRatio = 1.0;

            int fillWidth = (int)Math.Round(dest.Width * fillRatio, MidpointRounding.AwayFromZero);
            if (fillWidth <= 0) return false;
            if (fillWidth > dest.Width) fillWidth = dest.Width;

            GraphicsState state = g.Save();
            try
            {
                g.FillRectangle(brush, new Rectangle(dest.X, dest.Y, fillWidth, dest.Height));
            }
            finally
            {
                g.Restore(state);
            }
            return true;
        }

        /// <summary>
        /// 平铺纹理（扫描线 / 网格 / 噪点）。属于"底纹"层，
        /// <b>调用方必须保证它早于文字绘制</b>，不能盖住数值。
        /// </summary>
        /// <param name="g">目标画布（不得为 null）。</param>
        /// <param name="brush">纹理画刷（借用引用）。</param>
        /// <param name="dest">目标矩形（物理像素）。</param>
        /// <returns>是否真的画了。</returns>
        public static bool FillTiled(Graphics g, TextureBrush brush, Rectangle dest)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (brush == null) return false;
            if (dest.Width <= 0 || dest.Height <= 0) return false;

            GraphicsState state = g.Save();
            try
            {
                g.FillRectangle(brush, dest);
            }
            finally
            {
                g.Restore(state);
            }
            return true;
        }

        // ============================================================
        // 画刷辅助
        // ============================================================

        /// <summary>
        /// 克隆纹理画刷，得到<b>独占</b>对象（调用方负责释放）。
        /// 需要改 Transform 时必须用克隆件，<b>不能改缓存里的共享画刷</b>。
        ///
        /// ★ 克隆件与源画刷<b>共用同一张瓦片位图</b>；释放克隆件不会释放位图
        ///   （位图仍归缓存所有），调用方也不要去释放它。
        /// </summary>
        /// <param name="source">源画刷（借用引用，不会被修改）。</param>
        /// <returns>新的纹理画刷（调用方负责释放）。</returns>
        public static TextureBrush CloneTextureBrush(TextureBrush source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            Image image = source.Image ?? new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
            TextureBrush clone = new TextureBrush(image)
            {
                WrapMode = source.WrapMode
            };
            clone.Transform = source.Transform;
            return clone;
        }

        /// <summary>
        /// 用瓦片位图创建一个平铺纹理画刷（<b>调用方负责释放</b>，位图的生命周期仍归原主）。
        /// </summary>
        /// <param name="tile">瓦片位图（借用引用）。</param>
        /// <returns>新的纹理画刷（调用方负责释放）。</returns>
        public static TextureBrush CreateTiledBrush(Bitmap tile)
        {
            if (tile == null) throw new ArgumentNullException(nameof(tile));
            return new TextureBrush(tile);
        }
    }
}
