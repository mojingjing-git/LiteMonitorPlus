using System.Drawing;
using BackdropModeResolver = LiteMonitor.src.SystemServices.Backdrop.BackdropModeResolver;
using ThemeAppearanceOptions = LiteMonitor.src.Core.ThemeAppearanceOptions;

namespace LiteMonitor.AppearanceThemeCheck
{
    /// <summary>
    /// 可读性判据模型：按渲染管线复刻"承载面"的合成结果，再算 WCAG 对比度。
    ///
    /// ★ 模型来源（全部来自生产代码，本工程只读引用、不修改）：
    /// - 相对亮度与对比度公式：WCAG 2.x 定义（sRGB 线性化后加权）。
    /// - 染色层 alpha：src/System/Backdrop/BackdropModeResolver.cs → BackdropModeResolver → ComputeTintAlpha
    ///   （= Tint 自身 alpha × Opacity）。
    /// - 噪点层 alpha：src/System/Backdrop/BackdropNoiseCache.cs → BackdropNoiseCache → CreateEntry
    ///   （= 255 × 强度/100 × 0.35，黑白噪点，最坏取白像素）。
    /// - 底纹层 alpha：src/UI/Decor/DecorRenderer.cs → DecorRenderer → ScaleIntensityAlpha
    ///   （= 上限 × 强度/100 × 取色自身 alpha/255；扫描线上限 80、网格上限 60）。
    /// - 底纹取色：src/UI/Decor/DecorRenderer.cs → DecorRenderer → ResolvePatternBase
    ///   （优先外框色，其次次级文字色，最后兜底白色）。
    /// - 边缘辉光 alpha：src/System/Backdrop/BackdropEdgeGlow.cs → BackdropEdgeGlow → Draw
    ///   （= 255 × 强度/100 × 染色色 alpha/255，取色为染色色）。
    ///
    /// ★ 本模型是**保守**的：假设所有图层在同一像素上完全重叠。
    ///   实际渲染中底纹被排除在不透明组块之外、边缘辉光只在窗口最外一圈，
    ///   因此真实对比度只会高于本模型给出的值。
    /// </summary>
    internal static class ContrastModel
    {
        /// <summary>扫描线强度映射的 alpha 上限（对应 src/UI/Decor/DecorRenderer.cs → DecorRenderer → ScanlineMaxAlpha）。</summary>
        private const int ScanlineMaxAlpha = 80;

        /// <summary>网格强度映射的 alpha 上限（对应 src/UI/Decor/DecorRenderer.cs → DecorRenderer → GridMaxAlpha）。</summary>
        private const int GridMaxAlpha = 60;

        /// <summary>噪点像素的 alpha 比例上限（对应 src/System/Backdrop/BackdropNoiseCache.cs → BackdropNoiseCache → MaxAlphaRatio）。</summary>
        private const double NoiseMaxAlphaRatio = 0.35;

        /// <summary>sRGB 分量线性化（WCAG 定义）。</summary>
        private static double Linearize(double channel)
        {
            double c = channel / 255.0;
            if (c <= 0.03928) return c / 12.92;
            return Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        /// <summary>WCAG 相对亮度。</summary>
        public static double RelativeLuminance(Color c)
        {
            return 0.2126 * Linearize(c.R)
                 + 0.7152 * Linearize(c.G)
                 + 0.0722 * Linearize(c.B);
        }

        /// <summary>WCAG 对比度（1.0 ~ 21.0）。</summary>
        public static double ContrastRatio(Color a, Color b)
        {
            double la = RelativeLuminance(a);
            double lb = RelativeLuminance(b);
            if (la < lb) { double t = la; la = lb; lb = t; }
            return (la + 0.05) / (lb + 0.05);
        }

        /// <summary>source-over 合成：前景色以给定 alpha 叠在背景色上（结果不透明）。</summary>
        public static Color Composite(Color foreground, Color background, double alpha)
        {
            if (alpha <= 0) return background;
            if (alpha >= 1) return foreground;
            double a = alpha;
            return Color.FromArgb(
                (int)Math.Round(foreground.R * a + background.R * (1.0 - a), MidpointRounding.AwayFromZero),
                (int)Math.Round(foreground.G * a + background.G * (1.0 - a), MidpointRounding.AwayFromZero),
                (int)Math.Round(foreground.B * a + background.B * (1.0 - a), MidpointRounding.AwayFromZero));
        }

        /// <summary>强度 → alpha：线性映射到上限，再乘以颜色自身 alpha（对应 src/UI/Decor/DecorRenderer.cs → DecorRenderer → ScaleIntensityAlpha）。</summary>
        private static int ScaleIntensityAlpha(int intensity, int maxAlpha, int baseAlpha)
        {
            if (intensity <= 0 || maxAlpha <= 0 || baseAlpha <= 0) return 0;
            double value = maxAlpha * (intensity / 100.0) * (baseAlpha / 255.0);
            int alpha = (int)Math.Round(value, MidpointRounding.AwayFromZero);
            if (alpha < 1) alpha = 1;
            if (alpha > 255) alpha = 255;
            return alpha;
        }

        /// <summary>底纹取色：外框色优先，其次次级文字色，最后兜底白色（对应 src/UI/Decor/DecorRenderer.cs → DecorRenderer → ResolvePatternBase）。</summary>
        public static Color ResolvePatternBase(ThemeAppearanceOptions options)
        {
            if (options.FrameEnabled && options.BorderColor.A > 0) return options.BorderColor;
            if (options.TextSecondaryColor.A > 0) return options.TextSecondaryColor;
            return Color.White;
        }

        /// <summary>
        /// 复刻"背景承载面"的最坏像素：渐变端色 → 染色层 → 噪点层 → 底纹层 → 边缘辉光层。
        /// </summary>
        public static Color BuildWorstCaseSurface(ThemeAppearanceOptions options, Color gradientEndColor)
        {
            // 渐变两端都是不透明色，渐变直接覆盖底板
            Color surface = gradientEndColor;

            // 染色层
            int tintAlpha = BackdropModeResolver.ComputeTintAlpha(options);
            if (tintAlpha > 0)
                surface = Composite(options.TintColor, surface, tintAlpha / 255.0);

            // 噪点层：黑白噪点，最坏取白像素
            int noiseAlpha = (int)Math.Round(
                255.0 * (options.BackdropNoise / 100.0) * NoiseMaxAlphaRatio,
                MidpointRounding.AwayFromZero);
            if (noiseAlpha > 0)
                surface = Composite(Color.White, surface, noiseAlpha / 255.0);

            // 底纹层：扫描线 + 网格，两层各自 source-over
            Color pattern = ResolvePatternBase(options);
            int scanAlpha = ScaleIntensityAlpha(options.Scanline, ScanlineMaxAlpha, pattern.A);
            if (scanAlpha > 0)
                surface = Composite(pattern, surface, scanAlpha / 255.0);
            int gridAlpha = ScaleIntensityAlpha(options.Grid, GridMaxAlpha, pattern.A);
            if (gridAlpha > 0)
                surface = Composite(pattern, surface, gridAlpha / 255.0);

            // 边缘辉光层：取色为染色色，最外缘最亮
            int glowAlpha = (int)Math.Round(
                255.0 * (options.BackdropEdgeGlow / 100.0) * (options.TintColor.A / 255.0),
                MidpointRounding.AwayFromZero);
            if (glowAlpha > 0)
                surface = Composite(options.TintColor, surface, glowAlpha / 255.0);

            return surface;
        }

        /// <summary>
        /// 在渐变两端中挑出"对给定文字色更不利"的那一端，返回它的最坏承载面。
        /// （暗底主题：更亮的端更不利；亮底主题：更暗的端更不利。）
        /// </summary>
        public static Color SelectWorstSurface(ThemeAppearanceOptions options, Color textColor)
        {
            Color fromStart = BuildWorstCaseSurface(options, options.GradientStartColor);
            Color fromEnd = BuildWorstCaseSurface(options, options.GradientEndColor);
            double crStart = ContrastRatio(textColor, fromStart);
            double crEnd = ContrastRatio(textColor, fromEnd);
            return crStart <= crEnd ? fromStart : fromEnd;
        }

        /// <summary>把颜色转成 #RRGGBB，便于在报告里引用。</summary>
        public static string ToHex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }
    }
}
