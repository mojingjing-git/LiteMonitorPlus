using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.Decor
{
    /// <summary>
    /// 进度条渲染器：旧条（完全复用既有逻辑）/ 渐变条 / 纹理条。
    ///
    /// ★ 样式语义（与 <see cref="ThemeAppearanceOptions.BarStyle"/> 一致）：
    /// <list type="bullet">
    /// <item><b>0 = 旧条</b>：<b>完全走旧逻辑</b>（转调既有 <see cref="UIUtils.DrawBar"/>），
    ///   一个像素都不改变，也不建任何新画刷 / 位图 / 缓存条目；</item>
    /// <item><b>1 = 渐变条</b>：渐变按<b>完整条槽</b>缓存，再裁剪到当前填充值
    ///   （复用 A05 的 <see cref="DecorResourceHost.GetOrAddGradientSlot"/> +
    ///   <see cref="DecorPainter.FillGradientSlot"/>），<b>不会为每个百分比建缓存</b>；</item>
    /// <item><b>2 = 纹理条</b>：斜纹瓦片平铺（复用 A05 的
    ///   <see cref="DecorResourceHost.GetOrAddTiledBrush"/> + <see cref="DecorPainter.FillTextureSlot"/>）。</item>
    /// </list>
    ///
    /// ★ 不得改变的东西：
    /// - <b>阈值取色不变</b>：条的颜色仍然来自既有 <see cref="UIUtils.GetStateColor"/>（含旧 <c>BarHigh</c> / <c>BarMid</c>），
    ///   新样式只在这一个颜色内部做明度渐变 / 加斜纹，<b>不引入第二种阈值颜色，也不改安全值颜色</b>；
    /// - <b>填充宽度不变</b>：新样式的填充宽度与旧条<b>逐像素一致</b>（同一套取整 + 最小 1 像素规则）；
    /// - <b>不凭空加条</b>：只有本来就有进度条的指标才会调用本渲染器，
    ///   <see cref="MetricItem.BarRect"/> 为空时直接返回，不画任何东西
    ///   （横屏的列模型本来就没有条，M1 不为它改造分组布局）。
    ///
    /// ★ 层次：条属于"原有标题 / 指标 / 条"层，位于组底纹 / 组框之后、
    ///   向内的窗口边框 / 高光之前。
    /// </summary>
    public sealed class BarRenderer : IDisposable
    {
        // 渐变条终点色相对起点色的提亮比例（只在同一个阈值颜色内部做明度变化）
        private const double GradientLightenRatio = 0.30;

        // 纹理条：底色相对阈值颜色的透明度比例（让斜纹能透出来）
        private const int TextureBaseAlphaDivisor = 2;

        // 纹理瓦片边长 = 条高 × 2（至少 4 物理像素）
        private const int TextureTileHeightFactor = 2;
        private const int TextureTileMin = 4;

        private readonly DecorResourceHost _host;
        private readonly bool _ownsHost;
        private bool _disposed;

        /// <summary>
        /// 创建进度条渲染器（自建资源宿主，生命周期与本对象一致）。
        /// </summary>
        public BarRenderer() : this(new DecorResourceHost(), ownsHost: true)
        {
        }

        /// <summary>
        /// 创建进度条渲染器，<b>借用</b>外部资源宿主。
        /// 借用的宿主由其所有者负责释放；本对象的 <see cref="Dispose"/> 不会释放它。
        /// </summary>
        /// <param name="host">资源宿主（不得为 null）。</param>
        public BarRenderer(DecorResourceHost host)
            : this(host ?? throw new ArgumentNullException(nameof(host)), ownsHost: false)
        {
        }

        private BarRenderer(DecorResourceHost host, bool ownsHost)
        {
            _host = host;
            _ownsHost = ownsHost;
        }

        /// <summary>资源宿主（A05 的缓存所有者）。</summary>
        public DecorResourceHost Host => _host;

        // ============================================================
        // 主入口
        // ============================================================

        /// <summary>
        /// 绘制一个指标进度条。
        ///
        /// ★ 样式 0（旧条）时<b>只转调既有 <see cref="UIUtils.DrawBar"/></b>，
        ///   与改造前<b>逐像素一致</b>（连取整规则、连颜色来源都一样）。
        ///
        /// @return 是否真的画了。
        /// </summary>
        /// <param name="g">目标画布（不得为 null）。</param>
        /// <param name="options">外观运行时快照（只读）。</param>
        /// <param name="item">指标项（提供条矩形、百分比与阈值状态；不得为 null）。</param>
        /// <param name="theme">当前主题（提供条底色与阈值色；不得为 null）。</param>
        public bool RenderBar(Graphics g, ThemeAppearanceOptions options, MetricItem item, Theme theme)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (theme == null) throw new ArgumentNullException(nameof(theme));
            ObjectDisposedException.ThrowIf(_disposed, this);

            // 没有任何条矩形 → 不凭空加条
            if (item.BarRect.Width <= 0 || item.BarRect.Height <= 0) return false;

            // 旧条：完全走旧逻辑，不做任何改变
            if (options.BarStyle == ThemeAppearanceOptions.BarStyleLegacy)
            {
                UIUtils.DrawBar(g, item, theme);
                return true;
            }

            // 阈值取色：复用既有逻辑（旧 BarHigh / BarMid / 安全值颜色都不动）
            Color barColor = UIUtils.GetStateColor(item.CachedColorState, theme, false);
            if (barColor.A <= 0) return false;

            // 条底（轨道）：与旧条完全一致的画法
            DrawTrack(g, item.BarRect, theme);

            int fillWidth = ComputeFillWidth(item.BarRect.Width, item.CachedPercent);
            if (fillWidth <= 0) return true;   // 轨道已画，填充为空

            return options.BarStyle == ThemeAppearanceOptions.BarStyleGradient
                ? DrawGradientBar(g, options, item.BarRect, fillWidth, barColor)
                : options.BarStyle == ThemeAppearanceOptions.BarStyleTexture
                    ? DrawTextureBar(g, options, item.BarRect, fillWidth, barColor)
                    : false;
        }

        /// <summary>
        /// 低层入口：只画"填充部分"，条底由调用方自己画。
        /// 用于调用方已经有自己的条底 / 条槽的场景。
        ///
        /// @return 是否真的画了。
        /// </summary>
        /// <param name="g">目标画布（不得为 null）。</param>
        /// <param name="options">外观运行时快照（只读）。</param>
        /// <param name="barRect">条槽矩形（物理像素）。</param>
        /// <param name="percent">填充比例（0–1，内部会夹紧）。</param>
        /// <param name="barColor">填充色（调用方负责按既有阈值逻辑取色）。</param>
        public bool RenderBarSlot(Graphics g, ThemeAppearanceOptions options, Rectangle barRect, double percent, Color barColor)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (options == null) throw new ArgumentNullException(nameof(options));
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (options.BarStyle == ThemeAppearanceOptions.BarStyleLegacy) return false;
            if (barRect.Width <= 0 || barRect.Height <= 0) return false;
            if (barColor.A <= 0) return false;

            int fillWidth = ComputeFillWidth(barRect.Width, percent);
            if (fillWidth <= 0) return false;

            return options.BarStyle == ThemeAppearanceOptions.BarStyleGradient
                ? DrawGradientBar(g, options, barRect, fillWidth, barColor)
                : options.BarStyle == ThemeAppearanceOptions.BarStyleTexture
                    ? DrawTextureBar(g, options, barRect, fillWidth, barColor)
                    : false;
        }

        // ============================================================
        // 失效与释放
        // ============================================================

        /// <summary>
        /// 同步渲染上下文（主题代次 / DPI / 用户缩放变化时自动失效相关缓存）。
        /// </summary>
        /// <param name="options">外观运行时快照（只读）。</param>
        /// <returns>被移除的缓存条目数。</returns>
        public int UpdateContext(ThemeAppearanceOptions options) => _host.UpdateContext(options);

        /// <summary>窗口尺寸变化：全量失效（槽位尺寸不再复用）。</summary>
        /// <returns>被移除的缓存条目数。</returns>
        public int OnSizeChanged() => _host.OnSizeChanged();

        /// <summary>换肤：按新的主题代次失效。</summary>
        /// <param name="themeGeneration">新的主题代次。</param>
        /// <returns>被移除的缓存条目数。</returns>
        public int OnThemeChanged(int themeGeneration) => _host.OnThemeChanged(themeGeneration);

        /// <summary>
        /// 释放本对象持有的资源。只有自建宿主时才会释放宿主（借用的宿主由其所有者负责）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsHost) _host.Dispose();
        }

        // ============================================================
        // 内部实现
        // ============================================================

        /// <summary>
        /// 填充宽度：<b>与旧条完全相同的取整规则</b>
        /// （<c>(int)(宽 × 百分比)</c> 截断；百分比 &gt; 0 时至少 1 像素）。
        /// </summary>
        private static int ComputeFillWidth(int barWidth, double percent)
        {
            if (barWidth <= 0) return 0;
            if (percent <= 0) return 0;
            if (percent > 1.0) percent = 1.0;

            int width = (int)(barWidth * percent);
            if (width < 1) width = 1;
            if (width > barWidth) width = barWidth;
            return width;
        }

        /// <summary>条底（轨道）：与旧条逐像素一致。</summary>
        private static void DrawTrack(Graphics g, Rectangle barRect, Theme theme)
        {
            using GraphicsPath bgPath = UIUtils.RoundRect(barRect, barRect.Height / 2);
            g.FillPath(UIUtils.GetBrush(theme.Color.BarBackground), bgPath);
        }

        /// <summary>
        /// 渐变条：渐变按<b>完整条槽</b>缓存（不是每个百分比一条），
        /// 绘制时按填充比例裁剪；填充部分裁剪成与旧条一致的圆角形状。
        /// </summary>
        private bool DrawGradientBar(Graphics g, ThemeAppearanceOptions options, Rectangle barRect, int fillWidth, Color barColor)
        {
            int slotWidth = barRect.Width;
            int slotHeight = barRect.Height;

            // 0° = 左 → 右（A05 已实测取证的约定）
            Bitmap? slot = _host.GetOrAddGradientSlot(
                options,
                barColor,
                Lighten(barColor, GradientLightenRatio),
                0.0,
                slotWidth,
                slotHeight);
            if (slot == null) return false;

            double ratio = (double)fillWidth / slotWidth;
            if (ratio <= 0) return false;

            GraphicsState state = g.Save();
            GraphicsPath? shape = null;
            try
            {
                shape = UIUtils.RoundRect(
                    new Rectangle(barRect.X, barRect.Y, fillWidth, barRect.Height),
                    barRect.Height / 2);
                g.SetClip(shape);
                return DecorPainter.FillGradientSlot(g, slot, barRect, ratio);
            }
            finally
            {
                g.Restore(state);
                shape?.Dispose();
            }
        }

        /// <summary>
        /// 纹理条：同色半透明底 + 45° 斜纹（瓦片来自 A05 缓存）。
        /// </summary>
        private bool DrawTextureBar(Graphics g, ThemeAppearanceOptions options, Rectangle barRect, int fillWidth, Color barColor)
        {
            int tile = Math.Max(TextureTileMin, barRect.Height * TextureTileHeightFactor);

            TextureBrush? brush = _host.GetOrAddTiledBrush(
                options,
                DecorCacheKey.KindBarTexture,
                barColor,
                tile,
                tile);
            if (brush == null) return false;

            double ratio = (double)fillWidth / barRect.Width;
            if (ratio <= 0) return false;

            GraphicsState state = g.Save();
            GraphicsPath? shape = null;
            try
            {
                shape = UIUtils.RoundRect(
                    new Rectangle(barRect.X, barRect.Y, fillWidth, barRect.Height),
                    barRect.Height / 2);
                g.SetClip(shape);

                Rectangle filled = new Rectangle(barRect.X, barRect.Y, fillWidth, barRect.Height);
                int baseAlpha = Math.Max(1, barColor.A / TextureBaseAlphaDivisor);
                using (SolidBrush baseBrush = new SolidBrush(
                    Color.FromArgb(baseAlpha, barColor.R, barColor.G, barColor.B)))
                {
                    g.FillRectangle(baseBrush, filled);
                }

                return DecorPainter.FillTextureSlot(g, brush, barRect, ratio);
            }
            finally
            {
                g.Restore(state);
                shape?.Dispose();
            }
        }

        /// <summary>
        /// 在<b>同一个颜色内部</b>做明度渐变（向白色靠拢）。
        /// 不引入第二种颜色，因此不会改变阈值取色语义。
        /// </summary>
        private static Color Lighten(Color color, double ratio)
        {
            double amount = ratio < 0 ? 0 : (ratio > 1 ? 1 : ratio);
            int r = (int)Math.Round(color.R + (255 - color.R) * amount, MidpointRounding.AwayFromZero);
            int g = (int)Math.Round(color.G + (255 - color.G) * amount, MidpointRounding.AwayFromZero);
            int b = (int)Math.Round(color.B + (255 - color.B) * amount, MidpointRounding.AwayFromZero);
            return Color.FromArgb(color.A, r, g, b);
        }
    }
}
