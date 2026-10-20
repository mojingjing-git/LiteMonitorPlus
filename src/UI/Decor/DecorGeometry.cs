using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LiteMonitor.src.UI.Decor
{
    /// <summary>
    /// 装饰几何：窗口外框 / 组框的路径构造与边界夹紧。
    ///
    /// ★ 合同要点（v4 §3.2 / §3.3）：
    /// 1. <b>只做物理像素几何。</b>调用方必须传入 <c>ThemeAppearanceOptions.FrameWidthPx</c> /
    ///    <c>FrameLengthPx</c>（已由 options 缩放过一次），<b>本类不再乘任何缩放系数</b>。
    /// 2. <b>几何层要再防御一次。</b>options 只把 FrameWidthPx 夹到宽度一半、
    ///    FrameLengthPx 夹到高度一半；圆角半径、同时受宽高约束的切角边长
    ///    在这里统一再夹一次，保证切角边长超过窗口半宽/半高时也不会越界。
    /// 3. <b>返回的 GraphicsPath 由调用方拥有并释放</b>；本类不缓存路径，
    ///    也不持有任何位图 / 画刷 / 画笔。
    /// </summary>
    public static class DecorGeometry
    {
        // ============================================================
        // 外框样式常量（与 ThemeAppearanceOptions.FrameStyle* 语义一致）
        // ============================================================

        /// <summary>旧路径（不画装饰外框）。</summary>
        public const int FrameStyleNone = 0;
        /// <summary>切角外框。</summary>
        public const int FrameStyleChamfer = 1;
        /// <summary>圆角描边外框。</summary>
        public const int FrameStyleRoundedStroke = 2;
        /// <summary>静态全息边框（外层切角 + 内层圆角双轮廓）。</summary>
        public const int FrameStyleHolographic = 3;

        // ============================================================
        // 尺寸夹紧
        // ============================================================

        /// <summary>
        /// 把切角边长夹紧到当前 bounds 内：切角边长不得超过 min(宽, 高) / 2，
        /// 否则两侧切角会交叉、路径自交。另外在"正好等于一半"时再收 1 像素，
        /// 保证竖直/水平方向至少留 1 像素直线段（否则路径出现重合顶点，
        /// 奇偶填充会出现难以预料的翻转）。
        /// 返回 0 表示不切角。
        /// </summary>
        /// <param name="lengthPx">切角边长（物理像素）。</param>
        /// <param name="bounds">目标矩形（物理像素）。</param>
        public static int ClampCornerLengthPx(int lengthPx, Rectangle bounds)
        {
            if (lengthPx <= 0) return 0;
            int limit = Math.Min(bounds.Width, bounds.Height);
            if (limit <= 0) return 0;
            int c = Math.Min(lengthPx, limit / 2);
            if (2 * c >= limit) c = Math.Max(0, (limit - 1) / 2);
            return c;
        }

        /// <summary>
        /// 把描边宽度夹紧到当前 bounds 内：不得超过 min(宽, 高) / 2，
        /// 否则向内描边会退化成一个点 / 互相吞噬。返回 0 表示不描边。
        /// </summary>
        /// <param name="widthPx">描边宽度（物理像素）。</param>
        /// <param name="bounds">目标矩形（物理像素）。</param>
        public static int ClampFrameWidthPx(int widthPx, Rectangle bounds)
        {
            if (widthPx <= 0) return 0;
            int max = Math.Min(bounds.Width, bounds.Height) / 2;
            if (max <= 0) return 0;
            return Math.Min(widthPx, max);
        }

        /// <summary>
        /// 把圆角半径夹紧到当前 bounds 内：不得超过 min(宽, 高) / 2。
        /// 返回 0 表示直角。
        /// </summary>
        /// <param name="radiusPx">圆角半径（物理像素）。</param>
        /// <param name="bounds">目标矩形（物理像素）。</param>
        public static int ClampRadiusPx(int radiusPx, Rectangle bounds)
        {
            if (radiusPx <= 0) return 0;
            int max = Math.Min(bounds.Width, bounds.Height) / 2;
            if (max <= 0) return 0;
            return Math.Min(radiusPx, max);
        }

        /// <summary>
        /// 把矩形向内缩进指定像素。缩进后宽高不再为正时返回 <see cref="Rectangle.Empty"/>。
        /// </summary>
        /// <param name="bounds">原始矩形（物理像素）。</param>
        /// <param name="insetPx">向内缩进的物理像素数。</param>
        public static Rectangle Deflate(Rectangle bounds, int insetPx)
        {
            if (insetPx <= 0) return bounds;
            int w = bounds.Width - insetPx * 2;
            int h = bounds.Height - insetPx * 2;
            if (w <= 0 || h <= 0) return Rectangle.Empty;
            return new Rectangle(bounds.X + insetPx, bounds.Y + insetPx, w, h);
        }

        // ============================================================
        // 路径构造
        // ============================================================

        /// <summary>
        /// 构造外框轮廓路径（切角 / 圆角）。<b>调用方负责 Dispose 返回的路径。</b>
        ///
        /// 样式语义：
        /// - <see cref="FrameStyleNone"/>：返回空路径（什么都不画）；
        /// - <see cref="FrameStyleChamfer"/>：四角切角；
        /// - <see cref="FrameStyleRoundedStroke"/>：四角圆角；
        /// - <see cref="FrameStyleHolographic"/>：外层切角（内层圆角用
        ///   <see cref="BuildInnerFramePath"/>）。
        ///
        /// 未知样式按"不画"处理，保证旧主题不会凭空出现描边。
        /// </summary>
        /// <param name="bounds">完整客户区矩形（物理像素）。</param>
        /// <param name="frameStyle">外框样式（见 <see cref="FrameStyle*"/> 常量）。</param>
        /// <param name="cornerLengthPx">切角边长（物理像素，options.FrameLengthPx）。</param>
        /// <param name="radiusPx">圆角半径（物理像素）。</param>
        /// <param name="insetPx">相对 bounds 向内缩进的物理像素数。</param>
        public static GraphicsPath BuildFramePath(Rectangle bounds, int frameStyle, int cornerLengthPx, int radiusPx, int insetPx)
        {
            GraphicsPath path = new GraphicsPath();
            Rectangle rect = Deflate(bounds, insetPx);
            if (rect.Width <= 0 || rect.Height <= 0) return path;

            switch (frameStyle)
            {
                case FrameStyleChamfer:
                    AddChamfer(path, rect, ClampCornerLengthPx(cornerLengthPx, rect));
                    break;
                case FrameStyleRoundedStroke:
                    AddRounded(path, rect, ClampRadiusPx(radiusPx, rect));
                    break;
                case FrameStyleHolographic:
                    AddChamfer(path, rect, ClampCornerLengthPx(cornerLengthPx, rect));
                    break;
                default:
                    break;   // 未知样式 = 不画
            }
            return path;
        }

        /// <summary>
        /// 构造<b>局部坐标</b>的外框轮廓路径（左上角固定在 0,0，尺寸为 widthPx × heightPx）。
        ///
        /// ★ 局部坐标版本用于<b>路径缓存</b>：缓存键里不含位置，
        ///   绘制时用 <c>Graphics.TranslateTransform</c> 平移到目标位置即可，
        ///   同一尺寸 / 同一参数只建一次路径。
        /// <b>调用方负责 Dispose 返回的路径。</b>
        /// </summary>
        /// <param name="widthPx">宽度（物理像素）。</param>
        /// <param name="heightPx">高度（物理像素）。</param>
        /// <param name="frameStyle">外框样式。</param>
        /// <param name="cornerLengthPx">切角边长（物理像素）。</param>
        /// <param name="radiusPx">圆角半径（物理像素）。</param>
        /// <param name="insetPx">相对矩形向内缩进的物理像素数。</param>
        public static GraphicsPath BuildLocalFramePath(int widthPx, int heightPx, int frameStyle, int cornerLengthPx, int radiusPx, int insetPx = 0)
        {
            GraphicsPath path = new GraphicsPath();
            Rectangle rect = Deflate(new Rectangle(0, 0, widthPx, heightPx), insetPx);
            if (rect.Width <= 0 || rect.Height <= 0) return path;

            switch (frameStyle)
            {
                case FrameStyleChamfer:
                    AddChamfer(path, rect, ClampCornerLengthPx(cornerLengthPx, rect));
                    break;
                case FrameStyleRoundedStroke:
                    AddRounded(path, rect, ClampRadiusPx(radiusPx, rect));
                    break;
                case FrameStyleHolographic:
                    AddChamfer(path, rect, ClampCornerLengthPx(cornerLengthPx, rect));
                    break;
                default:
                    break;
            }
            return path;
        }

        /// <summary>
        /// 构造<b>内描边带</b>路径（绝对坐标）：外轮廓与向内缩进 bandWidthPx 的内轮廓组成的一条带，
        /// 填充模式为 <see cref="FillMode.Alternate"/>（奇偶规则：内轮廓天然成为孔洞）。
        ///
        /// ★ 为什么用"带"而不是"描边"：GDI+ 的画笔是<b>沿路径居中</b>的，
        ///   描边一定会有一半像素落到路径之外 —— 那样就会画到客户区外。
        ///   填充这条带则严格落在路径之内，<b>天然不会溢出客户区</b>。
        /// <b>调用方负责 Dispose 返回的路径。</b>
        /// </summary>
        /// <param name="bounds">完整客户区矩形（物理像素）。</param>
        /// <param name="frameStyle">外框样式。</param>
        /// <param name="cornerLengthPx">切角边长（物理像素）。</param>
        /// <param name="radiusPx">圆角半径（物理像素）。</param>
        /// <param name="outerInsetPx">外轮廓相对客户区向内缩进（物理像素）。</param>
        /// <param name="bandWidthPx">带宽（物理像素）。</param>
        public static GraphicsPath BuildBandPath(Rectangle bounds, int frameStyle, int cornerLengthPx, int radiusPx, int outerInsetPx, int bandWidthPx)
        {
            return BuildLocalBandPath(bounds.Width, bounds.Height, frameStyle, cornerLengthPx, radiusPx, outerInsetPx, bandWidthPx);
        }

        /// <summary>
        /// 构造<b>局部坐标</b>的内描边带路径（供缓存使用）。
        /// 语义同 <see cref="BuildBandPath"/>，只是坐标从 0,0 开始。
        /// <b>调用方负责 Dispose 返回的路径。</b>
        /// </summary>
        /// <param name="widthPx">宽度（物理像素）。</param>
        /// <param name="heightPx">高度（物理像素）。</param>
        /// <param name="frameStyle">外框样式。</param>
        /// <param name="cornerLengthPx">切角边长（物理像素）。</param>
        /// <param name="radiusPx">圆角半径（物理像素）。</param>
        /// <param name="outerInsetPx">外轮廓向内缩进（物理像素）。</param>
        /// <param name="bandWidthPx">带宽（物理像素）。</param>
        public static GraphicsPath BuildLocalBandPath(int widthPx, int heightPx, int frameStyle, int cornerLengthPx, int radiusPx, int outerInsetPx, int bandWidthPx)
        {
            // FillMode.Alternate = 奇偶规则：内轮廓天然成为孔洞
            GraphicsPath path = new GraphicsPath { FillMode = FillMode.Alternate };

            Rectangle outer = Deflate(new Rectangle(0, 0, widthPx, heightPx), outerInsetPx);
            if (outer.Width <= 0 || outer.Height <= 0) return path;

            using (GraphicsPath outerPath = BuildLocalFramePath(widthPx, heightPx, frameStyle, cornerLengthPx, radiusPx, outerInsetPx))
            {
                if (outerPath.PointCount == 0) return path;
                path.AddPath(outerPath, false);

                Rectangle inner = Deflate(outer, bandWidthPx);
                if (inner.Width <= 0 || inner.Height <= 0) return path;   // 内轮廓退化：整块都是带

                using (GraphicsPath innerPath = BuildLocalFramePath(widthPx, heightPx, frameStyle, cornerLengthPx, radiusPx, outerInsetPx + bandWidthPx))
                {
                    if (innerPath.PointCount > 0) path.AddPath(innerPath, false);
                }
            }
            return path;
        }

        /// <summary>
        /// 构造内轮廓路径（用于全息双线 / 内侧高光）：在
        /// <see cref="BuildFramePath"/> 的基础上再向内缩进 <paramref name="extraInsetPx"/>。
        /// 全息样式（3）的内层用圆角，其余样式与外层一致。
        /// <b>调用方负责 Dispose 返回的路径。</b>
        /// </summary>
        /// <param name="bounds">完整客户区矩形（物理像素）。</param>
        /// <param name="frameStyle">外框样式（见 <see cref="FrameStyle*"/> 常量）。</param>
        /// <param name="cornerLengthPx">切角边长（物理像素）。</param>
        /// <param name="radiusPx">圆角半径（物理像素）。</param>
        /// <param name="insetPx">相对 bounds 向内缩进的物理像素数。</param>
        /// <param name="extraInsetPx">在外轮廓基础上的额外向内缩进（物理像素）。</param>
        public static GraphicsPath BuildInnerFramePath(Rectangle bounds, int frameStyle, int cornerLengthPx, int radiusPx, int insetPx, int extraInsetPx)
        {
            GraphicsPath path = new GraphicsPath();
            Rectangle rect = Deflate(bounds, insetPx + Math.Max(0, extraInsetPx));
            if (rect.Width <= 0 || rect.Height <= 0) return path;

            if (frameStyle == FrameStyleHolographic)
            {
                AddRounded(path, rect, ClampRadiusPx(radiusPx, rect));
            }
            else
            {
                switch (frameStyle)
                {
                    case FrameStyleChamfer:
                        AddChamfer(path, rect, ClampCornerLengthPx(cornerLengthPx, rect));
                        break;
                    case FrameStyleRoundedStroke:
                        AddRounded(path, rect, ClampRadiusPx(radiusPx, rect));
                        break;
                    default:
                        break;
                }
            }
            return path;
        }

        /// <summary>
        /// 构造切角矩形路径（四角各切掉一个等腰直角）。
        /// 切角边长会被夹紧到 min(宽, 高) / 2，<b>调用方负责 Dispose 返回的路径。</b>
        /// </summary>
        /// <param name="rect">目标矩形（物理像素）。</param>
        /// <param name="cornerPx">切角边长（物理像素）。</param>
        public static GraphicsPath BuildChamferPath(Rectangle rect, int cornerPx)
        {
            GraphicsPath path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0) return path;
            AddChamfer(path, rect, ClampCornerLengthPx(cornerPx, rect));
            return path;
        }

        /// <summary>
        /// 构造圆角矩形路径。半径会被夹紧到 min(宽, 高) / 2，
        /// <b>调用方负责 Dispose 返回的路径。</b>
        /// </summary>
        /// <param name="rect">目标矩形（物理像素）。</param>
        /// <param name="radiusPx">圆角半径（物理像素）。</param>
        public static GraphicsPath BuildRoundedPath(Rectangle rect, int radiusPx)
        {
            GraphicsPath path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0) return path;
            AddRounded(path, rect, ClampRadiusPx(radiusPx, rect));
            return path;
        }

        /// <summary>
        /// 向内描边用的矩形：先按 insetPx 缩进，再按描边宽度缩进。
        /// 宽高不再为正时返回 <see cref="Rectangle.Empty"/>。
        /// </summary>
        /// <param name="bounds">完整客户区矩形（物理像素）。</param>
        /// <param name="frameWidthPx">描边宽度（物理像素）。</param>
        /// <param name="insetPx">相对 bounds 向内缩进的物理像素数。</param>
        public static Rectangle BuildInnerStrokeBounds(Rectangle bounds, int frameWidthPx, int insetPx)
        {
            Rectangle outer = Deflate(bounds, insetPx);
            if (outer.Width <= 0 || outer.Height <= 0) return Rectangle.Empty;
            return Deflate(outer, Math.Max(0, frameWidthPx));
        }

        // ============================================================
        // 内部实现
        // ============================================================

        private static void AddChamfer(GraphicsPath path, Rectangle rect, int cornerPx)
        {
            float x = rect.X;
            float y = rect.Y;
            float w = rect.Width;
            float h = rect.Height;

            if (cornerPx <= 0)
            {
                path.AddRectangle(rect);
                return;
            }

            // 切角边长不能超过短边的一半；等于一半时会出现重合顶点（路径自交，
            // 填充时会出现难以预料的奇偶翻转），因此再收 1 像素，保证留有直线段。
            int c = ClampCornerLengthPx(cornerPx, rect);
            if (c <= 0)
            {
                path.AddRectangle(rect);
                return;
            }

            float f = c;
            path.AddPolygon(new[]
            {
                new PointF(x + f,         y),
                new PointF(x + w - f,     y),
                new PointF(x + w,         y + f),
                new PointF(x + w,         y + h - f),
                new PointF(x + w - f,     y + h),
                new PointF(x + f,         y + h),
                new PointF(x,             y + h - f),
                new PointF(x,             y + f)
            });
            path.CloseFigure();
        }

        private static void AddRounded(GraphicsPath path, Rectangle rect, int radiusPx)
        {
            if (radiusPx <= 0)
            {
                path.AddRectangle(rect);
                return;
            }

            float x = rect.X;
            float y = rect.Y;
            float w = rect.Width;
            float h = rect.Height;
            float r = Math.Min(radiusPx, Math.Min(rect.Width, rect.Height) / 2f);
            float d = r * 2f;

            // 四段圆弧 + 四条直线，缺一段直线路径就会断开，填充结果不可预期
            path.AddArc(x,         y,         d, d, 180f, 90f);   // 左上
            path.AddLine(x + r,     y,         x + w - r, y);       // 上边
            path.AddArc(x + w - d, y,         d, d, 270f, 90f);   // 右上
            path.AddLine(x + w,     y + r,     x + w, y + h - r);   // 右边
            path.AddArc(x + w - d, y + h - d, d, d,   0f, 90f);   // 右下
            path.AddLine(x + w - r, y + h,     x + r,     y + h);   // 下边
            path.AddArc(x,         y + h - d, d, d,  90f, 90f);   // 左下
            path.AddLine(x,         y + h - r, x,         y + r);   // 左边
            path.CloseFigure();
        }
    }
}
