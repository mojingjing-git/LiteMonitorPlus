using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Decor;

namespace LiteMonitor.AppearanceDecorRendererProbe
{
    /// <summary>
    /// 帧渲染器：**模拟 A13 的接线**（本线不接线到主面板）。
    ///
    /// 两条路径：
    /// - <see cref="RenderProductionLegacy"/>：直接调用生产渲染器（<c>UIRenderer</c> / <c>HorizontalRenderer</c>），
    ///   代表"改造前"的画面；
    /// - <see cref="RenderPortraitNewPath"/> / <see cref="RenderHorizontalNewPath"/>：
    ///   按 v4 §3.3 的固定层次重排，并在对应位置插入 A07 的装饰层。
    ///
    /// ★ 关键自检：探针会断言"重排路径在新能力全关时与生产旧路径<b>逐像素一致</b>"，
    ///   这同时证明"重排没有偷偷改变既有绘制"与"关闭态零副作用"。
    /// </summary>
    internal static class ProbeFrameRenderer
    {
        /// <summary>生产旧路径（竖屏 = UIRenderer，横屏 = HorizontalRenderer）。</summary>
        public static void RenderProductionLegacy(Graphics g, ProbeFrame frame)
        {
            if (frame.IsHorizontal)
            {
                HorizontalRenderer.Render(g, frame.Theme, frame.Columns, frame.Width);
            }
            else
            {
                UIRenderer.Render(g, frame.Groups, frame.Theme);
            }
        }

        /// <summary>
        /// 新路径（竖屏）：底板 → 窗口底纹 → 组块填充 → 组底纹/组框 → 标题/指标/条 → 向内的窗口边框/高光。
        /// </summary>
        /// <returns>实际画出的进度条数量。</returns>
        public static int RenderPortraitNewPath(
            Graphics g,
            ProbeFrame frame,
            ThemeAppearanceOptions options,
            DecorRenderer decor,
            BarRenderer bars,
            bool drawTexture = true,
            bool drawText = true,
            bool drawFrame = true)
        {
            Theme t = frame.Theme;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 1. 底板（与 UIRenderer.Render 的第 1 步逐字一致）
            g.FillRectangle(UIUtils.GetBrush(t.Color.Background),
                new Rectangle(-5, -5, (int)g.VisibleClipBounds.Width + 10, (int)g.VisibleClipBounds.Height + 10));

            // 2. 窗口底纹（A07）
            if (drawTexture)
            {
                decor.RenderWindowTexture(g, options, frame.GroupRegions);
            }

            int barsDrawn = 0;
            foreach (GroupLayoutInfo gr in frame.Groups)
            {
                int gp = t.Layout.GroupPadding;

                // 3. 组块填充（既有）
                UIUtils.FillRoundRect(g, gr.Bounds, t.Layout.GroupRadius, ThemeManager.ParseColor(t.Color.GroupBackground));

                // 4. 组底纹 / 组框（A07）
                if (drawTexture)
                {
                    decor.RenderGroupDecor(g, options, new DecorGroupRegion(gr.Bounds, t.Layout.GroupRadius));
                }

                if (!drawText) continue;

                // 5. 组标题（既有，逐字复制 UIRenderer.DrawGroupBackground 的标题部分）
                string label = string.IsNullOrEmpty(gr.Label) ? gr.GroupName : gr.Label;
                int titleH = t.FontGroup.Height;
                int titleY = gr.Bounds.Y - t.Layout.GroupTitleOffset - titleH;
                int extraH = (int)(8 * t.Layout.LayoutScale);
                var rectTitle = new Rectangle(gr.Bounds.X + gp,
                    Math.Max(0, titleY - extraH / 2),
                    gr.Bounds.Width - gp * 2,
                    titleH + extraH);
                TextRenderer.DrawText(g, label, t.FontGroup, rectTitle,
                    ThemeManager.ParseColor(t.Color.TextGroup),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                // 6. 指标（既有）+ 条（A07）
                foreach (MetricItem it in gr.Items)
                {
                    if (it.Bounds == Rectangle.Empty) continue;
                    if (it.Style != MetricRenderStyle.StandardBar) continue;

                    string itemLabel = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;
                    TextRenderer.DrawText(g, itemLabel, t.FontItem, it.LabelRect,
                        ThemeManager.ParseColor(t.Color.TextPrimary),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                    string valText = it.GetFormattedText(false);
                    Color valColor = it.GetTextColor(t);
                    TextRenderer.DrawText(g, valText, t.FontValue, it.ValueRect,
                        valColor,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                    if (bars.RenderBar(g, options, it, t)) barsDrawn++;
                }
            }

            // 7. 向内的窗口边框 / 高光（A07，层次最末）
            if (drawFrame)
            {
                decor.RenderWindowFrame(g, options, t.Layout.CornerRadius);
            }

            return barsDrawn;
        }

        /// <summary>
        /// 新路径（横屏）：<b>复用窗口底板 / 底纹 / 外框</b>，
        /// <b>不新增组框分组</b>（横屏现有模型是列，M1 不改造横屏分组布局），
        /// <b>不凭空出现条</b>（横屏本来就没有进度条）。
        /// </summary>
        /// <returns>实际画出的进度条数量（横屏恒为 0）。</returns>
        public static int RenderHorizontalNewPath(
            Graphics g,
            ProbeFrame frame,
            ThemeAppearanceOptions options,
            DecorRenderer decor,
            BarRenderer bars,
            bool drawTexture = true,
            bool drawFrame = true)
        {
            Theme t = frame.Theme;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 1. 底板（与 HorizontalRenderer.Render 一致）
            int panelHeight = (int)g.VisibleClipBounds.Height;
            using (SolidBrush bg = new SolidBrush(ThemeManager.ParseColor(t.Color.Background)))
            {
                g.FillRectangle(bg, new Rectangle(0, 0, frame.Width, panelHeight));
            }

            // 2. 窗口底纹（横屏没有组块 → 不做排除）
            if (drawTexture)
            {
                decor.RenderWindowTexture(g, options, null);
            }

            // 3. 列内容（既有，逐字复制 HorizontalRenderer.DrawItem）
            foreach (Column col in frame.Columns)
            {
                if (col.Bounds == Rectangle.Empty) continue;
                if (col.BoundsTop != Rectangle.Empty && col.Top != null)
                {
                    DrawHorizontalItem(g, col.Top, col.BoundsTop, t);
                }
                if (col.BoundsBottom != Rectangle.Empty && col.Bottom != null)
                {
                    DrawHorizontalItem(g, col.Bottom, col.BoundsBottom, t);
                }
            }

            // 4. 向内的窗口边框 / 高光（复用窗口外框）
            if (drawFrame)
            {
                decor.RenderWindowFrame(g, options, t.Layout.CornerRadius);
            }

            // 横屏没有条：不调用 bars.RenderBar
            return 0;
        }

        public static void DrawHorizontalItem(Graphics g, MetricItem it, Rectangle rc, Theme t)
        {
            string value = it.GetFormattedText(true);
            Color valColor = it.GetTextColor(t);

            bool hideLabel = string.IsNullOrEmpty(it.ShortLabel) || it.ShortLabel == " ";
            if (hideLabel)
            {
                TextRenderer.DrawText(g, value, t.FontItem, rc, valColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }

            string label = !string.IsNullOrEmpty(it.ShortLabel) ? it.ShortLabel : it.Label;
            if (string.IsNullOrEmpty(label)) label = it.Key;
            TextRenderer.DrawText(g, label, t.FontItem, rc,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            TextRenderer.DrawText(g, value, t.FontValue, rc, valColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
