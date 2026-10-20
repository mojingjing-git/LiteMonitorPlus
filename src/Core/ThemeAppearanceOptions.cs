using System;
using System.Drawing;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 外观渲染上下文快照（不可变）。
    ///
    /// 职责：把"主题里写的逻辑值"翻译成"这一次绘制可以直接用的运行时值"，
    /// 并且**只在这里做一次归一化与一次缩放**。
    ///
    /// ★ 合同要点：
    /// 1. 归一化只作用于运行时副本：越界/未知值在这里被夹紧，
    ///    **绝不回写用户 JSON**（原 Theme 对象在 Create 前后完全不变）。
    /// 2. 每个新尺寸只缩放一次：FrameWidth / FrameLength / Blur 在 Theme 里保留
    ///    96 DPI 逻辑值（LayoutConfig.Scale 不碰它们），由本类换算成物理像素
    ///    （FrameWidthPx / FrameLengthPx / BlurPx）。
    /// 3. 实际 DPI 必须由调用方传入当前窗口的 DeviceDpi，
    ///    **禁止**用全局 UIUtils.ScaleFactor 代替（那是进程级全局值，跨屏/多窗口不可靠）。
    /// 4. 本对象不持有整个 Theme / Font / Brush / Pen / Control，
    ///    背景帧与预览源只是"借用"的引用，调用方不得在此释放。
    /// </summary>
    public sealed class ThemeAppearanceOptions
    {
        // ============================================================
        // 归一化范围（v4 §3.1）
        // ============================================================
        public const int FrameWidthMin = 0;
        public const int FrameWidthMax = 8;
        public const int FrameLengthMin = 0;
        public const int FrameLengthMax = 64;
        public const int IntensityMin = 0;
        public const int IntensityMax = 100;
        public const double OpacityMin = 0.0;
        public const double OpacityMax = 1.0;
        public const double BlurMin = 0.0;
        public const double BlurMax = 32.0;
        public const int FpsMin = 1;
        public const int FpsMax = 6;

        /// <summary>背景模式：旧背景路径。</summary>
        public const string BackdropModeSolid = "solid";
        /// <summary>背景模式：静态渐变。</summary>
        public const string BackdropModeGradient = "gradient";
        /// <summary>背景模式：请求实时材质（后端不可用时由背景线回退）。</summary>
        public const string BackdropModeGlass = "glass";

        // 外框样式（与 LayoutConfig.FrameStyle 语义一致）
        public const int FrameStyleNone = 0;
        public const int FrameStyleChamfer = 1;
        public const int FrameStyleRoundedStroke = 2;
        public const int FrameStyleHolographic = 3;

        // 进度条样式（与 LayoutConfig.BarStyle 语义一致）
        public const int BarStyleLegacy = 0;
        public const int BarStyleGradient = 1;
        public const int BarStyleTexture = 2;

        // ============================================================
        // 主题代次与来源
        // ============================================================

        /// <summary>主题代次：由主题 owner 在换肤时自增，供绘制缓存失效判断使用。</summary>
        public int ThemeGeneration { get; }

        /// <summary>主题文件里的 version 字段（原样保留，不做修改）。</summary>
        public int ThemeVersion { get; }

        /// <summary>主题名称（仅用于诊断）。</summary>
        public string ThemeName { get; }

        // ============================================================
        // DPI / 用户缩放
        // ============================================================

        /// <summary>当前窗口的实际 DPI（96 = 100%）。</summary>
        public int DeviceDpi { get; }

        /// <summary>当前窗口的 DPI 比例（DeviceDpi / 96）。</summary>
        public float DpiScale { get; }

        /// <summary>用户缩放（主题设置里的倍率）。</summary>
        public float UserScale { get; }

        /// <summary>合成比例 = DpiScale * UserScale，用于换算新尺寸的物理像素。</summary>
        public float EffectiveScale { get; }

        // ============================================================
        // 客户区
        // ============================================================

        /// <summary>完整客户区矩形（布局完成后的 ClientRectangle，不是局部可见区）。</summary>
        public Rectangle ClientBounds { get; }

        /// <summary>客户区是否有有效宽高；为 false 时应跳过新几何绘制。</summary>
        public bool HasValidBounds => ClientBounds.Width > 0 && ClientBounds.Height > 0;

        /// <summary>
        /// 取用于**铺底 / 铺渐变 / 上色**的矩形：在客户区基础上四边各外扩
        /// <paramref name="padding"/> 个像素。
        /// <para>
        /// ★ <b>为什么必须外扩</b>：绘制层为了文字与圆角开了
        ///   <c>SmoothingMode.AntiAlias</c>，而 <c>PixelOffsetMode</c> 保持默认；
        ///   对位图而言默认等价于 <c>Half</c>（坐标偏移半像素）。
        ///   于是抗锯齿的填充**只覆盖目标矩形边界像素的约一半**（实测 alpha 仅 64~139/255）。
        ///   若严格按 <see cref="ClientBounds"/> 填充，客户区最外一圈会留下半透明像素 ——
        ///   背景"漏色"，局部重绘时底板也补不上。
        /// <para>
        /// ★ 旧的实心底板一直用 <c>(-5,-5, w+10, h+10)</c> 的溢出矩形正是为了躲开这个死角；
        ///   新背景层改用精确客户区后丢掉了该保护，这里补回来。
        /// <para>
        /// ★ 外扩出来的像素会被 Graphics 的裁剪自动丢弃，不会画到窗口之外。
        /// </para>
        /// </summary>
        /// <param name="clientBounds">完整客户区矩形。</param>
        /// <param name="padding">四边外扩的像素数（默认 2，足够覆盖抗锯齿过渡带）。</param>
        public static Rectangle GetEdgeCoveredBounds(Rectangle clientBounds, int padding = 2)
        {
            if (clientBounds.Width <= 0 || clientBounds.Height <= 0) return clientBounds;
            return new Rectangle(
                clientBounds.X - padding,
                clientBounds.Y - padding,
                clientBounds.Width + padding * 2,
                clientBounds.Height + padding * 2);
        }

        // ============================================================
        // 新外观标量（归一化后）
        // ============================================================

        /// <summary>外框样式：0=旧路径 1=切角 2=圆角描边 3=静态全息边框（未知值按 0）。</summary>
        public int FrameStyle { get; }

        /// <summary>描边宽度（96 DPI 逻辑值，0–8）。</summary>
        public int FrameWidth { get; }

        /// <summary>切角边长（96 DPI 逻辑值，0–64）。</summary>
        public int FrameLength { get; }

        /// <summary>扫描线强度（0–100）。</summary>
        public int Scanline { get; }

        /// <summary>辉光强度（0–100）。</summary>
        public int Glow { get; }

        /// <summary>网格强度（0–100）。</summary>
        public int Grid { get; }

        /// <summary>进度条样式：0=旧条 1=渐变 2=纹理（未知值按 0）。</summary>
        public int BarStyle { get; }

        // ============================================================
        // 物理像素（只在这里缩放一次）
        // ============================================================

        /// <summary>描边宽度（物理像素，已按当前 bounds 夹紧）。</summary>
        public int FrameWidthPx { get; }

        /// <summary>切角边长（物理像素，已按当前 bounds 夹紧）。</summary>
        public int FrameLengthPx { get; }

        /// <summary>模糊半径（物理像素）。</summary>
        public double BlurPx { get; }

        // ============================================================
        // 效果开关（消费颜色前的必要判断）
        // ============================================================

        /// <summary>是否绘制外框描边。只有为 true 时才允许消费 <see cref="BorderColor"/>。</summary>
        public bool FrameEnabled => FrameStyle != FrameStyleNone;

        /// <summary>是否绘制辉光。只有为 true 时才允许消费 <see cref="ShadowColor"/>。</summary>
        public bool GlowEnabled => Glow > 0;

        /// <summary>是否绘制扫描线。</summary>
        public bool ScanlineEnabled => Scanline > 0;

        /// <summary>是否绘制网格。</summary>
        public bool GridEnabled => Grid > 0;

        /// <summary>是否使用新的进度条样式。</summary>
        public bool BarStyleEnabled => BarStyle != BarStyleLegacy;

        /// <summary>是否请求实时玻璃材质（后端不可用时由背景线回退到渐变/纯色）。</summary>
        public bool GlassRequested => string.Equals(BackdropMode, BackdropModeGlass, StringComparison.Ordinal);

        // ============================================================
        // 颜色（已解析；无来源时为全透明）
        // ============================================================

        /// <summary>描边色。仅当 <see cref="FrameEnabled"/> 为 true 时才消费。</summary>
        public Color BorderColor { get; }

        /// <summary>辉光/阴影色。仅当 <see cref="GlowEnabled"/> 为 true 时才消费。</summary>
        public Color ShadowColor { get; }

        /// <summary>次级文字色。</summary>
        public Color TextSecondaryColor { get; }

        /// <summary>主背景色。</summary>
        public Color BackgroundColor { get; }

        /// <summary>组块背景色。</summary>
        public Color GroupBackgroundColor { get; }

        /// <summary>背景染色色（缺省取背景色）。</summary>
        public Color TintColor { get; }

        /// <summary>渐变起始色（缺省取背景色）。</summary>
        public Color GradientStartColor { get; }

        /// <summary>渐变结束色（缺省取组块背景色）。</summary>
        public Color GradientEndColor { get; }

        // ============================================================
        // 背景参数（归一化后）
        // ============================================================

        /// <summary>背景模式：solid / gradient / glass（未知值退回 solid）。</summary>
        public string BackdropMode { get; }

        /// <summary>背景 tint 层的额外 alpha 系数（0–1）。</summary>
        public double BackdropOpacity { get; }

        /// <summary>模糊半径（96 DPI 逻辑值，0–32）。</summary>
        public double BackdropBlur { get; }

        /// <summary>渐变角度（度）。</summary>
        public double GradientAngle { get; }

        /// <summary>噪点强度（0–100）。</summary>
        public int BackdropNoise { get; }

        /// <summary>边缘辉光强度（0–100）。</summary>
        public int BackdropEdgeGlow { get; }

        /// <summary>捕获帧率（1–6）。</summary>
        public int BackdropFps { get; }

        // ============================================================
        // 背景帧 / 预览源（仅借用，调用方负责释放）
        // ============================================================

        /// <summary>实时背景帧（可空）。本对象不释放它。</summary>
        public Image? BackgroundFrame { get; }

        /// <summary>预览用的固定样例源（可空）。本对象不释放它。</summary>
        public Image? PreviewSource { get; }

        // ============================================================
        // 构造
        // ============================================================

        private ThemeAppearanceOptions(
            int themeGeneration,
            int themeVersion,
            string themeName,
            int deviceDpi,
            float dpiScale,
            float userScale,
            Rectangle clientBounds,
            int frameStyle,
            int frameWidth,
            int frameLength,
            int scanline,
            int glow,
            int grid,
            int barStyle,
            int frameWidthPx,
            int frameLengthPx,
            double blurPx,
            Color borderColor,
            Color shadowColor,
            Color textSecondaryColor,
            Color backgroundColor,
            Color groupBackgroundColor,
            Color tintColor,
            Color gradientStartColor,
            Color gradientEndColor,
            string backdropMode,
            double backdropOpacity,
            double backdropBlur,
            double gradientAngle,
            int backdropNoise,
            int backdropEdgeGlow,
            int backdropFps,
            Image? backgroundFrame,
            Image? previewSource)
        {
            ThemeGeneration = themeGeneration;
            ThemeVersion = themeVersion;
            ThemeName = themeName ?? string.Empty;
            DeviceDpi = deviceDpi;
            DpiScale = dpiScale;
            UserScale = userScale;
            EffectiveScale = dpiScale * userScale;
            ClientBounds = clientBounds;

            FrameStyle = frameStyle;
            FrameWidth = frameWidth;
            FrameLength = frameLength;
            Scanline = scanline;
            Glow = glow;
            Grid = grid;
            BarStyle = barStyle;

            FrameWidthPx = frameWidthPx;
            FrameLengthPx = frameLengthPx;
            BlurPx = blurPx;

            BorderColor = borderColor;
            ShadowColor = shadowColor;
            TextSecondaryColor = textSecondaryColor;
            BackgroundColor = backgroundColor;
            GroupBackgroundColor = groupBackgroundColor;
            TintColor = tintColor;
            GradientStartColor = gradientStartColor;
            GradientEndColor = gradientEndColor;

            BackdropMode = backdropMode;
            BackdropOpacity = backdropOpacity;
            BackdropBlur = backdropBlur;
            GradientAngle = gradientAngle;
            BackdropNoise = backdropNoise;
            BackdropEdgeGlow = backdropEdgeGlow;
            BackdropFps = backdropFps;

            BackgroundFrame = backgroundFrame;
            PreviewSource = previewSource;
        }

        /// <summary>
        /// 生成外观快照。
        /// </summary>
        /// <param name="theme">主题对象（只读，不会被修改）。</param>
        /// <param name="dpiScale">当前窗口的实际 DPI 比例（DeviceDpi / 96）。<b>不要传全局 UIUtils.ScaleFactor。</b></param>
        /// <param name="userScale">用户缩放倍率。</param>
        /// <param name="clientBounds">完整客户区矩形。</param>
        /// <param name="themeGeneration">主题代次（换肤时自增）。</param>
        /// <param name="backgroundFrame">实时背景帧（可空，仅借用）。</param>
        /// <param name="previewSource">预览用固定样例源（可空，仅借用）。</param>
        public static ThemeAppearanceOptions Create(
            Theme theme,
            float dpiScale,
            float userScale,
            Rectangle clientBounds,
            int themeGeneration = 0,
            Image? backgroundFrame = null,
            Image? previewSource = null)
        {
            if (theme == null) throw new ArgumentNullException(nameof(theme));

            float dpi = NormalizeScale(dpiScale);
            float user = NormalizeScale(userScale);
            float effective = dpi * user;

            LayoutConfig layout = theme.Layout ?? new LayoutConfig();
            ColorConfig color = theme.Color ?? new ColorConfig();
            BackdropConfig? backdrop = theme.Backdrop;

            // ---- 归一化标量（未知样式 → 关闭该效果） ----
            int frameStyle = NormalizeFrameStyle(layout.FrameStyle);
            int frameWidth = Clamp(layout.FrameWidth, FrameWidthMin, FrameWidthMax);
            int frameLength = Clamp(layout.FrameLength, FrameLengthMin, FrameLengthMax);
            int scanline = Clamp(layout.Scanline, IntensityMin, IntensityMax);
            int glow = Clamp(layout.Glow, IntensityMin, IntensityMax);
            int grid = Clamp(layout.Grid, IntensityMin, IntensityMax);
            int barStyle = NormalizeBarStyle(layout.BarStyle);

            // ---- 新尺寸：逻辑值 → 物理像素（只缩放这一次） ----
            int frameWidthPx = ScaleToPixels(frameWidth, effective);
            int frameLengthPx = ScaleToPixels(frameLength, effective);

            // ---- 路径几何还要夹紧到当前 bounds ----
            if (clientBounds.Width > 0) frameWidthPx = Math.Min(frameWidthPx, clientBounds.Width / 2);
            if (clientBounds.Height > 0) frameLengthPx = Math.Min(frameLengthPx, clientBounds.Height / 2);
            if (frameWidthPx < 0) frameWidthPx = 0;
            if (frameLengthPx < 0) frameLengthPx = 0;

            // ---- 背景参数 ----
            string mode = NormalizeBackdropMode(backdrop?.Mode);
            double opacity = Clamp(backdrop?.Opacity ?? 0.75, OpacityMin, OpacityMax);
            double blur = Clamp(backdrop?.Blur ?? 12.0, BlurMin, BlurMax);
            double blurPx = blur * effective;
            double angle = backdrop?.GradientAngle ?? 90.0;
            int noise = Clamp(backdrop?.Noise ?? 0, IntensityMin, IntensityMax);
            int edgeGlow = Clamp(backdrop?.EdgeGlow ?? 0, IntensityMin, IntensityMax);
            int fps = Clamp(backdrop?.Fps ?? 2, FpsMin, FpsMax);

            // ---- 颜色 ----
            Color background = ThemeManager.ParseColor(color.Background);
            Color groupBackground = ThemeManager.ParseColor(color.GroupBackground);
            Color border = ThemeManager.ParseColor(color.Border);
            Color shadow = ThemeManager.ParseColor(color.Shadow);
            Color textSecondary = ThemeManager.ParseColor(color.TextSecondary);

            Color tint = string.IsNullOrWhiteSpace(backdrop?.Tint)
                ? background
                : ThemeManager.ParseColor(backdrop!.Tint);
            Color gradientStart = string.IsNullOrWhiteSpace(backdrop?.GradientStart)
                ? background
                : ThemeManager.ParseColor(backdrop!.GradientStart);
            Color gradientEnd = string.IsNullOrWhiteSpace(backdrop?.GradientEnd)
                ? groupBackground
                : ThemeManager.ParseColor(backdrop!.GradientEnd);

            int deviceDpi = (int)Math.Round(dpi * 96f, MidpointRounding.AwayFromZero);

            return new ThemeAppearanceOptions(
                themeGeneration,
                theme.Version,
                theme.Name,
                deviceDpi,
                dpi,
                user,
                clientBounds,
                frameStyle,
                frameWidth,
                frameLength,
                scanline,
                glow,
                grid,
                barStyle,
                frameWidthPx,
                frameLengthPx,
                blurPx,
                border,
                shadow,
                textSecondary,
                background,
                groupBackground,
                tint,
                gradientStart,
                gradientEnd,
                mode,
                opacity,
                blur,
                angle,
                noise,
                edgeGlow,
                fps,
                backgroundFrame,
                previewSource);
        }

        // ============================================================
        // 归一化辅助
        // ============================================================

        private static float NormalizeScale(float s)
        {
            if (float.IsNaN(s) || float.IsInfinity(s) || s <= 0f) return 1.0f;
            return s;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value)) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static int NormalizeFrameStyle(int style)
        {
            return style switch
            {
                FrameStyleNone => FrameStyleNone,
                FrameStyleChamfer => FrameStyleChamfer,
                FrameStyleRoundedStroke => FrameStyleRoundedStroke,
                FrameStyleHolographic => FrameStyleHolographic,
                _ => FrameStyleNone
            };
        }

        private static int NormalizeBarStyle(int style)
        {
            return style switch
            {
                BarStyleLegacy => BarStyleLegacy,
                BarStyleGradient => BarStyleGradient,
                BarStyleTexture => BarStyleTexture,
                _ => BarStyleLegacy
            };
        }

        private static string NormalizeBackdropMode(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return BackdropModeSolid;

            switch (mode!.Trim().ToLowerInvariant())
            {
                case BackdropModeGradient: return BackdropModeGradient;
                case BackdropModeGlass: return BackdropModeGlass;
                case BackdropModeSolid: return BackdropModeSolid;
                default: return BackdropModeSolid;   // 未知背景模式退回 solid
            }
        }

        private static int ScaleToPixels(int logical, float scale)
        {
            if (logical <= 0) return 0;
            double px = logical * scale;
            if (double.IsNaN(px) || double.IsInfinity(px)) return logical;
            return (int)Math.Round(px, MidpointRounding.AwayFromZero);
        }
    }
}
