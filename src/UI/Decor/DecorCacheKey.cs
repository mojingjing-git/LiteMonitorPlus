using System;
using System.Drawing;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.Decor
{
    /// <summary>
    /// 装饰绘图缓存的键。
    ///
    /// ★ 合同要点（v4 §3.3）：缓存键必须覆盖
    /// <b>主题代次、颜色、稳定槽位尺寸、方向、DPI / 用户缩放、噪点种子</b>，
    /// 缺一项就会出现"换了主题还是旧颜色""换了 DPI 还是旧纹理"这类串色问题。
    ///
    /// 本结构是 <c>readonly struct</c>，值语义；不持有任何绘图资源。
    /// </summary>
    public readonly struct DecorCacheKey : IEquatable<DecorCacheKey>
    {
        // ============================================================
        // 资源类别
        // ============================================================

        /// <summary>完整条槽渐变位图（按整条槽缓存，再裁剪到当前填充值）。</summary>
        public const int KindGradientSlot = 1;
        /// <summary>进度条纹理（可平铺图案）。</summary>
        public const int KindBarTexture = 2;
        /// <summary>噪点瓦片。</summary>
        public const int KindNoiseTile = 3;
        /// <summary>扫描线瓦片。</summary>
        public const int KindScanlineTile = 4;
        /// <summary>网格瓦片。</summary>
        public const int KindGridTile = 5;
        /// <summary>局部坐标的外框轮廓路径。</summary>
        public const int KindFramePath = 6;
        /// <summary>局部坐标的内描边带路径。</summary>
        public const int KindBandPath = 7;

        // ============================================================
        // 方向
        // ============================================================

        /// <summary>水平方向（左 → 右）。</summary>
        public const int DirectionHorizontal = 0;
        /// <summary>垂直方向（上 → 下）。</summary>
        public const int DirectionVertical = 1;
        /// <summary>径向 / 无方向。</summary>
        public const int DirectionRadial = 2;

        // ============================================================
        // 键字段
        // ============================================================

        /// <summary>资源类别（见 <see cref="Kind*"/> 常量）。</summary>
        public int Kind { get; }
        /// <summary>主题代次（换肤自增）。</summary>
        public int ThemeGeneration { get; }
        /// <summary>主色 ARGB。</summary>
        public int PrimaryArgb { get; }
        /// <summary>次色 ARGB（渐变结束色 / 纹理前景色）。</summary>
        public int SecondaryArgb { get; }
        /// <summary>稳定槽位宽度（物理像素）。</summary>
        public int WidthPx { get; }
        /// <summary>稳定槽位高度（物理像素）。</summary>
        public int HeightPx { get; }
        /// <summary>方向（见 <see cref="Direction*"/> 常量）。</summary>
        public int Direction { get; }
        /// <summary>当前窗口实际 DPI。</summary>
        public int DeviceDpi { get; }
        /// <summary>用户缩放（量化成千分比，避免浮点抖动导致键不命中）。</summary>
        public int UserScaleKey { get; }
        /// <summary>噪点 / 图案随机种子（决定图案内容）。</summary>
        public int Seed { get; }
        /// <summary>
        /// 类别相关的附加参数摘要（如渐变角度、扫描线间距 / 线宽、噪点强度）。
        /// 不放进键里会导致"角度不同的渐变共用同一条缓存"这类错误。
        /// </summary>
        public long Variant { get; }

        /// <summary>
        /// 构造缓存键。
        /// </summary>
        /// <param name="kind">资源类别。</param>
        /// <param name="themeGeneration">主题代次。</param>
        /// <param name="primaryArgb">主色 ARGB。</param>
        /// <param name="secondaryArgb">次色 ARGB。</param>
        /// <param name="widthPx">稳定槽位宽度（物理像素）。</param>
        /// <param name="heightPx">稳定槽位高度（物理像素）。</param>
        /// <param name="direction">方向。</param>
        /// <param name="deviceDpi">当前窗口实际 DPI。</param>
        /// <param name="userScaleKey">用户缩放（千分比）。</param>
        /// <param name="seed">噪点 / 图案种子。</param>
        /// <param name="variant">类别相关的附加参数摘要（渐变角度、间距 / 线宽、强度等）。</param>
        public DecorCacheKey(
            int kind,
            int themeGeneration,
            int primaryArgb,
            int secondaryArgb,
            int widthPx,
            int heightPx,
            int direction,
            int deviceDpi,
            int userScaleKey,
            int seed,
            long variant = 0L)
        {
            Kind = kind;
            ThemeGeneration = themeGeneration;
            PrimaryArgb = primaryArgb;
            SecondaryArgb = secondaryArgb;
            WidthPx = widthPx;
            HeightPx = heightPx;
            Direction = direction;
            DeviceDpi = deviceDpi;
            UserScaleKey = userScaleKey;
            Seed = seed;
            Variant = variant;
        }

        /// <summary>
        /// 从 <see cref="ThemeAppearanceOptions"/> 构造缓存键。
        ///
        /// ★ 这里只"读" options，<b>绝不回写</b>；主题代次 / DPI / 用户缩放
        /// 直接取 options 的运行时值（已经缩放过一次，本方法不再乘缩放系数）。
        /// </summary>
        /// <param name="kind">资源类别。</param>
        /// <param name="options">外观运行时快照（只读）。</param>
        /// <param name="primary">主色。</param>
        /// <param name="secondary">次色。</param>
        /// <param name="widthPx">稳定槽位宽度（物理像素）。</param>
        /// <param name="heightPx">稳定槽位高度（物理像素）。</param>
        /// <param name="direction">方向。</param>
        /// <param name="seed">噪点 / 图案种子。</param>
        /// <param name="variant">类别相关的附加参数摘要（默认 0）。</param>
        public static DecorCacheKey Create(
            int kind,
            ThemeAppearanceOptions options,
            Color primary,
            Color secondary,
            int widthPx,
            int heightPx,
            int direction,
            int seed,
            long variant = 0L)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            return new DecorCacheKey(
                kind,
                options.ThemeGeneration,
                primary.ToArgb(),
                secondary.ToArgb(),
                widthPx,
                heightPx,
                direction,
                options.DeviceDpi,
                QuantizeScale(options.UserScale),
                seed,
                variant);
        }

        /// <summary>
        /// 把角度量化成整数（0.1° 精度），用于放进缓存键。
        /// </summary>
        /// <param name="angleDegrees">角度（度）。</param>
        public static int QuantizeAngle(double angleDegrees)
        {
            if (double.IsNaN(angleDegrees) || double.IsInfinity(angleDegrees)) return 0;
            double v = angleDegrees * 10.0;
            if (v > int.MaxValue / 2) return int.MaxValue / 2;
            if (v < int.MinValue / 2) return int.MinValue / 2;
            return (int)Math.Round(v, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// 把用户缩放量化成千分比整数（1.0 → 1000，1.25 → 1250），
        /// 避免浮点误差让等价的缩放产生不同的缓存键。
        /// </summary>
        /// <param name="scale">用户缩放倍率。</param>
        public static int QuantizeScale(float scale)
        {
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f) return 1000;
            return (int)Math.Round(scale * 1000f, MidpointRounding.AwayFromZero);
        }

        /// <inheritdoc />
        public bool Equals(DecorCacheKey other)
        {
            return Kind == other.Kind
                && ThemeGeneration == other.ThemeGeneration
                && PrimaryArgb == other.PrimaryArgb
                && SecondaryArgb == other.SecondaryArgb
                && WidthPx == other.WidthPx
                && HeightPx == other.HeightPx
                && Direction == other.Direction
                && DeviceDpi == other.DeviceDpi
                && UserScaleKey == other.UserScaleKey
                && Seed == other.Seed
                && Variant == other.Variant;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is DecorCacheKey other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            HashCode hc = new HashCode();
            hc.Add(Kind);
            hc.Add(ThemeGeneration);
            hc.Add(PrimaryArgb);
            hc.Add(SecondaryArgb);
            hc.Add(WidthPx);
            hc.Add(HeightPx);
            hc.Add(Direction);
            hc.Add(DeviceDpi);
            hc.Add(UserScaleKey);
            hc.Add(Seed);
            hc.Add(Variant);
            return hc.ToHashCode();
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"K{Kind}/G{ThemeGeneration}/C{PrimaryArgb:X8}+{SecondaryArgb:X8}/" +
                   $"{WidthPx}x{HeightPx}/D{Direction}/Dpi{DeviceDpi}/S{UserScaleKey}/N{Seed}/V{Variant}";
        }
    }
}
