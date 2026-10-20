using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;

namespace LiteMonitor
{
    /// <summary>
    /// 横版渲染器（基于列结构绘制）
    /// 完全保留原版布局，不做任何功能添加。
    /// 修复内容：
    /// 1. Render 方法签名修复（支持 panelWidth）
    /// 2. value/颜色 使用 UIUtils 统一入口 -> 升级为 MetricItem 缓存入口
    /// 3. 删除文件内重复工具函数
    /// </summary>
    public static class HorizontalRenderer
    {
        /// <summary>
        /// 渲染横版面板（**旧入口，保留原有公开签名**）。
        /// <para>走的是完全不含新外观的旧路径，保留它是为了让外观探针仍能渲染"改造前的画面"。</para>
        /// </summary>
        public static void Render(Graphics g, Theme t, List<Column> cols, int panelWidth)
        {
            Render(g, t, cols, panelWidth, null);
        }

        /// <summary>
        /// 渲染横版面板（含新外观层）。
        /// </summary>
        /// <param name="appearance">
        /// 外观运行时（可空）。<b>为 null 时逐像素等同于旧路径</b>。
        /// </param>
        public static void Render(Graphics g, Theme t, List<Column> cols, int panelWidth, AppearanceRuntime? appearance)
        {
            int panelHeight = (int)g.VisibleClipBounds.Height;

            ThemeAppearanceOptions? options = appearance?.Options;
            bool hasAppearance = appearance != null && options != null;

            // 0. 背景层（solid / gradient / glass 降级）—— 最底层，在底板之前
            bool backdropOwnsBasePlate = false;
            if (hasAppearance)
            {
                BackdropStatus backdrop = appearance!.Backdrop.Render(g, options!);
                // ★ 同竖屏：非 solid 模式下背景层自己铺了**不透明底板**，
                //   旧的实心底板填充必须让位 —— 否则渐变 / 玻璃帧会被整个盖掉。
                backdropOwnsBasePlate = backdrop.EffectiveMode != BackdropRenderMode.Solid;
            }

            if (!backdropOwnsBasePlate)
            {
                if (hasAppearance)
                {
                    // ★ 新入口：底板锚定**完整客户区**，由 Graphics 裁剪限制实际写入范围。
                    //   旧实现用 (0,0,panelWidth,panelHeight)，其中 panelHeight 取自
                    //   g.VisibleClipBounds —— 局部失效时它就是那块裁剪区的高度，
                    //   底板画不满整块，失效区里补不上底色（独立验收 R5）。
                    //
                    // ★ 这里**不外扩**：横屏旧入口画的就是 (0,0,panelWidth,panelHeight)，
                    //   抗锯齿下两者的边缘半透明带必须一致，否则"旧主题新旧入口逐像素一致"会失败。
                    //   （竖屏旧入口本来就是溢出填充，新入口才需要外扩来等价。）
                    using (var bg = new SolidBrush(ThemeManager.ParseColor(t.Color.Background)))
                        g.FillRectangle(bg, options!.ClientBounds);
                }
                else
                {
                    // ★ 旧入口：保持原样（无 options，拿不到完整客户区）。
                    using (var bg = new SolidBrush(ThemeManager.ParseColor(t.Color.Background)))
                        g.FillRectangle(bg, new Rectangle(0, 0, panelWidth, panelHeight));
                }
            }

            // 1. 窗口底纹（扫描线 / 网格）
            //    ★ 横屏的模型是**列**，没有组块分组，因此窗口底纹铺满整个客户区，
            //      并且**不**画组底纹/组框（v4 §3.3：M1 不为了"组框一致"改造横屏分组布局）。
            if (hasAppearance)
                appearance!.Decor.RenderWindowTexture(g, options!, null);

            foreach (var col in cols)
                DrawColumn(g, col, t);

            // 2. 向内的窗口边框 / 高光 —— 最后画，压在最上层
            if (hasAppearance)
                appearance!.Decor.RenderWindowFrame(g, options!, t.Layout.CornerRadius);
        }

        private static void DrawColumn(Graphics g, Column col, Theme t)
        {
            if (col.Bounds == Rectangle.Empty) return;

            // ★★★ 优化：优先使用 Layout 预计算好的 Bounds，不再重复计算 ★★★
            // 这样可以同时兼容双行模式、任务栏单行模式、以及横条单行模式

            // 1. 绘制 Top
            if (col.BoundsTop != Rectangle.Empty && col.Top != null)
            {
                DrawItem(g, col.Top, col.BoundsTop, t);
            }

            // 2. 绘制 Bottom
            if (col.BoundsBottom != Rectangle.Empty && col.Bottom != null)
            {
                DrawItem(g, col.Bottom, col.BoundsBottom, t);
            }
        }

        private static void DrawItem(Graphics g, MetricItem it, Rectangle rc, Theme t)
        {
            // 使用 MetricItem 统一格式化 (横屏模式=true)
            string value = it.GetFormattedText(true);
            Color valColor = it.GetTextColor(t);

            // ★★★ 策略 A: 纯文本模式 (隐藏标签) ★★★
            // 适用于 IP、Dashboard 文本，直接居左显示
            // 逻辑：如果 ShortLabel 被显式设为空格或空，则视为隐藏标签
            bool hideLabel = string.IsNullOrEmpty(it.ShortLabel) || it.ShortLabel == " ";

            if (hideLabel)
            {
                // 可以根据偏好选择 Left 或 Center，这里选用 Left 比较稳妥
                TextRenderer.DrawText(
                    g,
                    value,
                    t.FontItem,
                    rc,
                    valColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                );
                return;
            }

            // ★★★ 策略 B: 标准标签模式 ★★★
            // Label (左对齐)
            // 优化：直接使用缓存的 ShortLabel
            string label = !string.IsNullOrEmpty(it.ShortLabel) ? it.ShortLabel : it.Label;
            if (string.IsNullOrEmpty(label)) label = it.Key;

            TextRenderer.DrawText(
                g,
                label,
                t.FontItem,
                rc,
                ThemeManager.ParseColor(t.Color.TextPrimary),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            );

            // Value (右对齐)
            // ★★★ 修复：统一使用 Item 字体 (即标签字体)，与任务栏保持一致 ★★★
            TextRenderer.DrawText(
                g,
                value,
                t.FontValue,
                rc,
                valColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            );
        }
    }
}