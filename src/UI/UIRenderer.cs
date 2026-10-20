using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;
using LiteMonitor.src.UI.Decor;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LiteMonitor
{
    public static class UIRenderer
    {
        // ★★★ 优化：移除本地画刷缓存，改为调用 UIUtils ★★★
        // private static readonly Dictionary<string, SolidBrush> _brushCache = new();
        // private static SolidBrush GetBrush(string color, Theme t) { ... }

        // [替换] ClearCache 方法
        public static void ClearCache() 
        {
            // 委托给 UIUtils 清理
            UIUtils.ClearBrushCache();
        }

        /// <summary>
        /// 渲染主面板（**旧入口，保留原有公开签名**）。
        /// <para>
        /// 走的是**完全不含新外观**的旧路径：没有背景层、没有装饰层、没有新进度条。
        /// 保留这个入口，是为了让既有调用方与外观探针仍能渲染"改造前的画面"，
        /// 从而让"新能力全关时逐像素一致"这一验收有可比的基准。
        /// </para>
        /// </summary>
        public static void Render(Graphics g, List<GroupLayoutInfo> groups, Theme t)
        {
            Render(g, groups, t, null);
        }

        /// <summary>
        /// 渲染主面板（含新外观层）。
        /// </summary>
        /// <param name="g">目标画布。</param>
        /// <param name="groups">分组布局。</param>
        /// <param name="t">当前主题。</param>
        /// <param name="appearance">
        /// 外观运行时（可空）。<b>为 null 时逐像素等同于旧路径</b> ——
        /// 这是"新能力全关时画面不变"的前提。
        /// </param>
        public static void Render(Graphics g, List<GroupLayoutInfo> groups, Theme t, AppearanceRuntime? appearance)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            ThemeAppearanceOptions? options = appearance?.Options;
            bool hasAppearance = appearance != null && options != null;

            // 0. 背景层（solid / gradient / glass 降级）—— 最底层，在底板之前
            bool backdropOwnsBasePlate = false;
            if (hasAppearance)
            {
                BackdropStatus backdrop = appearance!.Backdrop.Render(g, options!);
                // ★ 非 solid 模式下背景层自己铺了**不透明底板**（A08 的 DrawBasePlate），
                //   旧的实心底板填充必须让位 —— 否则渐变 / 玻璃帧会被整个盖掉，
                //   等于新背景白画。solid 模式背景层零绘制，底板仍走旧路径（旧主题像素不变）。
                backdropOwnsBasePlate = backdrop.EffectiveMode != BackdropRenderMode.Solid;
            }

            // 1. 绘制背景（底板）
            // ★★★ [核心修复] 扩大绘制区域，解决左侧和上侧漏黑边的问题 ★★★
            // 原理：从 (-5, -5) 开始画，确保绝对覆盖掉 (0,0) 处的物理像素死角。
            // 多余的部分会被系统自动裁剪，不会有副作用。
            // ★ 注意：这里的 (-5,-5) 溢出策略**只用于把底板填满客户区**，
            //   绝不能拿来定位外框/装饰 —— 装饰一律用 options.ClientBounds。
            if (!backdropOwnsBasePlate)
            {
                g.FillRectangle(UIUtils.GetBrush(t.Color.Background),
                    new Rectangle(-5, -5, (int)g.VisibleClipBounds.Width + 10, (int)g.VisibleClipBounds.Height + 10));
            }

            // 2. 窗口底纹（扫描线 / 网格）—— 铺在组块之外的区域，不覆盖数值文字
            if (hasAppearance)
                appearance!.Decor.RenderWindowTexture(g, options!, BuildGroupRegions(groups, t));

            // 3. 绘制主标题
            DrawMainTitle(g, t);

            // 4. 绘制各分组
            foreach (var gr in groups)
            {
                // 4.1 组块填充（不透明底板）
                DrawGroupFill(g, gr, t);

                // 4.2 组底纹 / 组框（在组块填充之后、组标题之前）
                if (hasAppearance)
                    appearance!.Decor.RenderGroupDecor(g, options!, new DecorGroupRegion(gr.Bounds, t.Layout.GroupRadius));

                // 4.3 组标题（画在组块上方，属于"原有标题"层，与组底纹不重叠）
                DrawGroupTitle(g, gr, t);

                // 遍历子项绘制 (不再区分 NET/DISK，统一由 Item.Style 决定)
                for (int i = 0; i < gr.Items.Count; i++)
                {
                    var it = gr.Items[i];
                    bool isLast = (i == gr.Items.Count - 1);

                    if (it.Style == MetricRenderStyle.TwoColumn)
                        DrawTwoColumnItem(g, it, t);
                    else if (it.Style == MetricRenderStyle.TextOnly) // [新增]
                        DrawTextItem(g, it, t, isLast);
                    else
                        DrawStandardItem(g, it, t, appearance);
                }
            }

            // 5. 向内的窗口边框 / 高光 —— 最后画，压在最上层
            if (hasAppearance)
                appearance!.Decor.RenderWindowFrame(g, options!, t.Layout.CornerRadius);
        }

        /// <summary>
        /// 把分组布局转成装饰层需要的组块区域列表（窗口底纹据此排除组块区域）。
        /// </summary>
        private static List<DecorGroupRegion> BuildGroupRegions(List<GroupLayoutInfo> groups, Theme t)
        {
            var list = new List<DecorGroupRegion>(groups?.Count ?? 0);
            if (groups == null) return list;
            foreach (var gr in groups)
            {
                if (gr.Bounds.Width <= 0 || gr.Bounds.Height <= 0) continue;
                list.Add(new DecorGroupRegion(gr.Bounds, t.Layout.GroupRadius));
            }
            return list;
        }

        private static void DrawMainTitle(Graphics g, Theme t)
        {
            string title = LanguageManager.T("Title");
            if (string.IsNullOrEmpty(title) || title == "Title") return;

            // 直接使用字体高度，不需要测量
            int titleH = t.FontTitle.Height;
            
            // ★★★ [优化] 标题下方的微调间距也要随 DPI 缩放 ★★★
            int titlePadding = (int)(4 * t.Layout.LayoutScale);
            
            var titleRect = new Rectangle(t.Layout.Padding, t.Layout.Padding, t.Layout.Width - t.Layout.Padding * 2, titleH + titlePadding);

            TextRenderer.DrawText(g, title, t.FontTitle, titleRect,
                ThemeManager.ParseColor(t.Color.TextTitle),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        /// <summary>
        /// 绘制组块的圆角填充（不透明底板）。
        /// <para>
        /// 与组标题拆成两个方法，是为了让装饰层能按 v4 固定的层次插入：
        /// <b>组块填充 → 组底纹/组框 → 组标题</b>。两者合并时组底纹就无处可插。
        /// </para>
        /// </summary>
        private static void DrawGroupFill(Graphics g, GroupLayoutInfo gr, Theme t)
        {
            // 绘制圆角背景
            UIUtils.FillRoundRect(g, gr.Bounds, t.Layout.GroupRadius, ThemeManager.ParseColor(t.Color.GroupBackground));
        }

        /// <summary>
        /// 绘制组标题 (CPU, GPU...)。
        /// <para>
        /// 标题画在组块**上方**（也就是背景层的位置），因此与组底纹/组框并不重叠，
        /// 两者不会互相覆盖。
        /// </para>
        /// </summary>
        private static void DrawGroupTitle(Graphics g, GroupLayoutInfo gr, Theme t)
        {
            int gp = t.Layout.GroupPadding;

            // ★★★ 优化：直接使用缓存的 gr.Label，不再每帧调用 LanguageManager.T ★★★
            string label = string.IsNullOrEmpty(gr.Label) ? gr.GroupName : gr.Label;

            int titleH = t.FontGroup.Height;
            int titleY = gr.Bounds.Y - t.Layout.GroupTitleOffset - titleH;

            // [Fix] 修复 Windows 10 下 Emoji 显示不全的问题
            // Emoji 字符高度通常大于普通字体高度，且 TextRenderer 的 VerticalCenter 可能导致上下被裁剪
            // 解决方案：扩大绘制区域的高度，并保持中心对齐
            int extraH = (int)(8 * t.Layout.LayoutScale);

            // Y 向上偏移一半 extraH，高度增加 extraH，这样中心点不变
            var rectTitle = new Rectangle(gr.Bounds.X + gp,
                System.Math.Max(0, titleY - extraH / 2),
                gr.Bounds.Width - gp * 2,
                titleH + extraH);

            TextRenderer.DrawText(g, label, t.FontGroup, rectTitle,
                ThemeManager.ParseColor(t.Color.TextGroup),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        /// <summary>
        /// 绘制标准项 (标签 + 数值 + 进度条)
        /// </summary>
        /// <param name="appearance">
        /// 外观运行时（可空）。仅用于进度条：<c>BarStyle=0</c>（旧条）时
        /// <c>BarRenderer</c> 内部<b>原样转调</b> <see cref="UIUtils.DrawBar"/>，
        /// 像素与旧路径完全一致；只有 1/2（渐变条 / 纹理条）才走新画法。
        /// </param>
        private static void DrawStandardItem(Graphics g, MetricItem it, Theme t, AppearanceRuntime? appearance)
        {
            if (it.Bounds == Rectangle.Empty) return;

            // Label (左对齐)
            // ★★★ 优化：直接使用缓存的 it.Label ★★★
            string label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;

            TextRenderer.DrawText(g, label, t.FontItem, it.LabelRect,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Value (右对齐)  传入 false 表示竖屏/普通模式
            string valText = it.GetFormattedText(false);
            
            
            Color valColor = it.GetTextColor(t);

            TextRenderer.DrawText(g, valText, t.FontValue, it.ValueRect,
                valColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Bar - 注意：这里调用的是 UIUtils.DrawBar，它现在已经使用了优化的画刷逻辑
            // ★ 走外观运行时：旧条（BarStyle=0）由 BarRenderer 原样转调 UIUtils.DrawBar，
            //   像素与改造前完全一致；渐变条 / 纹理条才走新画法。
            ThemeAppearanceOptions? options = appearance?.Options;
            if (appearance != null && options != null)
                appearance.Bars.RenderBar(g, options, it, t);
            else
                UIUtils.DrawBar(g, it, t);
        }

        /// <summary>
        /// 绘制双列项 (居中标签 + 居中数值)
        /// </summary>
        private static void DrawTwoColumnItem(Graphics g, MetricItem it, Theme t)
        {
            if (it.Bounds == Rectangle.Empty) return;

            // Label (居中顶部)
            string label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;
            
            TextRenderer.DrawText(g, label, t.FontItem, it.LabelRect,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);

            // Value (居中底部)
            // ★★★ [优化]：如果用户使用了自定义单位 (HasCustomUnit)，则跳过窄屏自动精简逻辑 ★★★
            // 原逻辑：if (narrow) valText = FormatHorizontalValue(...)
            bool narrow = t.Layout.Width < 240 * t.Layout.LayoutScale;
            string valText = it.GetFormattedText(narrow);
            


            Color valColor = it.GetTextColor(t);

            TextRenderer.DrawText(g, valText, t.FontValue, it.ValueRect,
                valColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPadding);
        }

        // [新增] 绘制纯文本项方法
        private static void DrawTextItem(Graphics g, MetricItem it, Theme t, bool isLast)
        {
            if (it.Bounds == Rectangle.Empty) return;

            // 1. 绘制左侧标签 (IP)
            string label = string.IsNullOrEmpty(it.Label) ? it.Key : it.Label;
            TextRenderer.DrawText(g, label, t.FontItem, it.LabelRect,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // 2. 绘制右侧数值 (192.168.x.x)
            // ★★★ 修复：直接使用布局计算好的 ValueRect (已修正为全宽) ★★★
            // 优先调用 GetFormattedText 以支持单位显示
            string text = it.GetFormattedText(false);
            
            // ★★★ 修改：字体使用 FontItem (与标签一致)，颜色保持 ValueSafe ★★★
            // [Fix] Use GetTextColor(t) to respect Plugin Color Override
            Color valColor = it.GetTextColor(t);
            TextRenderer.DrawText(g, text, t.FontItem, it.ValueRect,
                valColor, 
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // ★★★ 修复：补充分割线 (最后一行不画) ★★★
            if (!isLast)
            {
                // 使用半透明的 BarBackground 作为分割线
                Color divColor = Color.FromArgb(100, ThemeManager.ParseColor(t.Color.BarBackground));
                
                // [优化] 使用缓存的 Pen
                var pen = UIUtils.GetPen(divColor, 1);
                
                // 线条稍微缩进一点，更美观
                int lineY = it.Bounds.Bottom - 1;
                g.DrawLine(pen, it.Bounds.Left + 5, lineY, it.Bounds.Right - 5, lineY);
            }
        }

    }
}