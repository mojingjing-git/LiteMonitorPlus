using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.Decor
{
    /// <summary>
    /// 装饰资源宿主：<see cref="DecorResourceCache"/> 的所有者，
    /// 对外提供<b>明确的失效入口</b>（尺寸 / 主题 / DPI 变化）与开箱即用的取缓存方法。
    ///
    /// ★ 接线建议（A13）：
    /// <code>
    /// // 构造（主窗体 / 渲染器持有，生命周期与窗口一致）
    /// _decor = new DecorResourceHost();
    ///
    /// // 每次 Paint 前同步上下文（主题代次 / DPI / 用户缩放）
    /// _decor.UpdateContext(options);
    ///
    /// // 窗口尺寸变化
    /// _decor.OnSizeChanged();
    ///
    /// // 窗口销毁
    /// _decor.Dispose();
    /// </code>
    ///
    /// ★ 关闭新能力时（FrameEnabled=false 等）不会创建任何位图 / 计时器：
    ///   本类没有任何计时器，位图只在真正请求缓存资源时才创建。
    /// </summary>
    public sealed class DecorResourceHost : IDisposable
    {
        private int _themeGeneration;
        private int _deviceDpi = 96;
        private int _userScaleKey = 1000;
        private bool _disposed;

        /// <summary>
        /// 创建宿主。
        /// </summary>
        /// <param name="maxEntries">缓存条目数上限（≤0 时用默认值 128）。</param>
        /// <param name="maxBitmapBytes">位图总字节上限（≤0 时用默认值 16 MiB）。</param>
        public DecorResourceHost(int maxEntries = DecorResourceCache.DefaultMaxEntries, long maxBitmapBytes = DecorResourceCache.DefaultMaxBitmapBytes)
        {
            Cache = new DecorResourceCache(maxEntries, maxBitmapBytes);
        }

        /// <summary>底层缓存（资源的所有者）。</summary>
        public DecorResourceCache Cache { get; }

        /// <summary>当前主题代次。</summary>
        public int ThemeGeneration => _themeGeneration;

        /// <summary>当前窗口实际 DPI。</summary>
        public int DeviceDpi => _deviceDpi;

        /// <summary>当前用户缩放（千分比）。</summary>
        public int UserScaleKey => _userScaleKey;

        // ============================================================
        // 失效入口
        // ============================================================

        /// <summary>
        /// <b>同步渲染上下文</b>：主题代次 / DPI / 用户缩放发生变化时，
        /// 自动失效与之相关的缓存条目。<b>不需要猜测，该调用是显式的。</b>
        ///
        /// @return 被移除的条目数。
        /// </summary>
        /// <param name="options">当前外观运行时快照（只读，不会被修改）。</param>
        public int UpdateContext(ThemeAppearanceOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            ObjectDisposedException.ThrowIf(_disposed, this);

            int gen = options.ThemeGeneration;
            int dpi = options.DeviceDpi > 0 ? options.DeviceDpi : 96;
            int scale = DecorCacheKey.QuantizeScale(options.UserScale);

            int removed = 0;
            if (gen != _themeGeneration)
            {
                removed += Cache.InvalidateTheme(gen);
            }
            if (dpi != _deviceDpi || scale != _userScaleKey)
            {
                removed += Cache.InvalidateStale(gen, dpi, scale);
            }

            _themeGeneration = gen;
            _deviceDpi = dpi;
            _userScaleKey = scale;
            return removed;
        }

        /// <summary>
        /// <b>尺寸变化</b>：槽位尺寸不再复用，全量失效。
        /// </summary>
        /// <returns>被移除的条目数。</returns>
        public int OnSizeChanged() => Cache.InvalidateAll();

        /// <summary>
        /// <b>换肤</b>：按新的主题代次失效（等价于 <see cref="UpdateContext"/> 里的主题分支）。
        /// </summary>
        /// <param name="themeGeneration">新的主题代次。</param>
        /// <returns>被移除的条目数。</returns>
        public int OnThemeChanged(int themeGeneration) => Cache.InvalidateTheme(themeGeneration);

        // ============================================================
        // 开箱即用的取缓存方法
        // ============================================================

        /// <summary>
        /// 取（或建并缓存）一条<b>局部坐标</b>的<b>内描边带</b>路径。
        ///
        /// ★ 坐标约定（必须与 <see cref="DecorPainter.FillInnerFrame"/> 一致）：
        ///   传入的是<b>客户区</b>尺寸与<b>外轮廓缩进</b>；
        ///   本方法内部按缩进算出实际区域尺寸，路径以该区域左上角为原点。
        ///   绘制时由 <see cref="DecorPainter.FillInnerFrame"/> 负责平移到位。
        /// ★ 缓存拥有路径，<b>借用者不要 Dispose，也不要修改它</b>。
        /// </summary>
        /// <param name="options">外观运行时快照（只读）。</param>
        /// <param name="clientWidthPx">客户区宽度（物理像素）。</param>
        /// <param name="clientHeightPx">客户区高度（物理像素）。</param>
        /// <param name="frameStyle">外框样式。</param>
        /// <param name="cornerLengthPx">切角边长（物理像素）。</param>
        /// <param name="radiusPx">圆角半径（物理像素）。</param>
        /// <param name="outerInsetPx">外轮廓向内缩进（物理像素，最小 1）。</param>
        /// <param name="bandWidthPx">带宽（物理像素）。</param>
        /// <returns>借用引用（不要 Dispose）。</returns>
        public GraphicsPath GetOrAddBandPath(
            ThemeAppearanceOptions options,
            int clientWidthPx,
            int clientHeightPx,
            int frameStyle,
            int cornerLengthPx,
            int radiusPx,
            int outerInsetPx,
            int bandWidthPx)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            int inset = Math.Max(1, outerInsetPx);
            int w = clientWidthPx - inset * 2;
            int h = clientHeightPx - inset * 2;
            if (w <= 0 || h <= 0)
            {
                // 空间不足：返回一条空路径（画不出东西，也不缓存）
                return new GraphicsPath();
            }

            DecorCacheKey key = DecorCacheKey.Create(
                DecorCacheKey.KindBandPath,
                options,
                Color.Empty,
                Color.Empty,
                w,
                h,
                DecorCacheKey.DirectionRadial,
                0,
                PackPathParams(frameStyle, cornerLengthPx, radiusPx, 0, bandWidthPx));

            return Cache.GetOrAddPath(key, k => DecorGeometry.BuildLocalBandPath(
                k.WidthPx, k.HeightPx, frameStyle, cornerLengthPx, radiusPx, 0, bandWidthPx));
        }

        /// <summary>
        /// 取（或建并缓存）一条<b>局部坐标</b>的外框轮廓路径（不含带宽）。
        ///
        /// ★ 路径是<b>局部坐标</b>（左上角在 0,0），绘制时平移到目标位置。
        /// ★ 缓存拥有路径，<b>借用者不要 Dispose，也不要修改它</b>。
        /// </summary>
        /// <param name="options">外观运行时快照（只读）。</param>
        /// <param name="widthPx">宽度（物理像素）。</param>
        /// <param name="heightPx">高度（物理像素）。</param>
        /// <param name="frameStyle">外框样式。</param>
        /// <param name="cornerLengthPx">切角边长（物理像素）。</param>
        /// <param name="radiusPx">圆角半径（物理像素）。</param>
        /// <param name="insetPx">相对矩形向内缩进（物理像素）。</param>
        /// <returns>借用引用（不要 Dispose）。</returns>
        public GraphicsPath GetOrAddFramePath(
            ThemeAppearanceOptions options,
            int widthPx,
            int heightPx,
            int frameStyle,
            int cornerLengthPx,
            int radiusPx,
            int insetPx = 0)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            DecorCacheKey key = DecorCacheKey.Create(
                DecorCacheKey.KindFramePath,
                options,
                Color.Empty,
                Color.Empty,
                widthPx,
                heightPx,
                DecorCacheKey.DirectionRadial,
                0,
                PackPathParams(frameStyle, cornerLengthPx, radiusPx, insetPx, 0));

            return Cache.GetOrAddPath(key, k => DecorGeometry.BuildLocalFramePath(
                k.WidthPx, k.HeightPx, frameStyle, cornerLengthPx, radiusPx, insetPx));
        }

        /// <summary>
        /// 把路径参数打包进一个 long（样式 4 位、边长 10 位、半径 10 位、缩进 10 位、带宽 10 位）。
        /// 超出范围的参数会被夹紧，保证打包结果唯一。
        /// </summary>
        /// <param name="frameStyle">外框样式。</param>
        /// <param name="cornerLengthPx">切角边长。</param>
        /// <param name="radiusPx">圆角半径。</param>
        /// <param name="insetPx">向内缩进。</param>
        /// <param name="bandWidthPx">带宽（0 表示只要轮廓不要带）。</param>
        public static long PackPathParams(int frameStyle, int cornerLengthPx, int radiusPx, int insetPx, int bandWidthPx)
        {
            long style = Math.Clamp(frameStyle, 0, 15);
            long corner = Math.Clamp(cornerLengthPx, 0, 1023);
            long radius = Math.Clamp(radiusPx, 0, 1023);
            long inset = Math.Clamp(insetPx, 0, 1023);
            long band = Math.Clamp(bandWidthPx, 0, 1023);
            return style | (corner << 4) | (radius << 14) | (inset << 24) | (band << 34);
        }

        /// <summary>
        /// 取（或建并缓存）一个<b>完整条槽</b>的渐变位图。
        ///
        /// ★ 渐变按整条槽缓存，绘制时再按填充比例裁剪（<see cref="DecorPainter.FillGradientSlot"/>），
        ///   <b>不会为每个百分比单独建缓存</b>。
        /// </summary>
        /// <param name="options">外观运行时快照（只读）。</param>
        /// <param name="startColor">起始色。</param>
        /// <param name="endColor">结束色。</param>
        /// <param name="angleDegrees">渐变角度（屏幕坐标顺时针为正）。</param>
        /// <param name="widthPx">槽位宽度（物理像素）。</param>
        /// <param name="heightPx">槽位高度（物理像素）。</param>
        /// <param name="seed">图案种子（0 表示不参与区分）。</param>
        /// <returns>借用引用（缓存拥有它，<b>不要 Dispose</b>）；尺寸非法时返回 null。</returns>
        public Bitmap? GetOrAddGradientSlot(
            ThemeAppearanceOptions options,
            Color startColor,
            Color endColor,
            double angleDegrees,
            int widthPx,
            int heightPx,
            int seed = 0)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (widthPx <= 0 || heightPx <= 0) return null;

            DecorCacheKey key = DecorCacheKey.Create(
                DecorCacheKey.KindGradientSlot,
                options,
                startColor,
                endColor,
                widthPx,
                heightPx,
                DecorCacheKey.DirectionHorizontal,
                seed,
                DecorCacheKey.QuantizeAngle(angleDegrees));

            return Cache.GetOrAddBitmap(key, k => DecorTextureFactory.CreateGradientSlot(
                k.WidthPx, k.HeightPx, startColor, endColor, angleDegrees)!);
        }

        /// <summary>
        /// 取（或建并缓存）一个可平铺的纹理画刷（扫描线 / 网格 / 噪点 / 进度条纹理）。
        ///
        /// ★ <b>图案内容完全由缓存键决定</b>：类别 + 颜色 + 瓦片尺寸 + 种子 + 变体参数，
        ///   所以同样的输入一定命中同一条缓存，不会出现"改了参数还是旧纹理"。
        /// ★ 缓存拥有画刷，并负责连同它引用的位图一起释放；
        ///   <b>借用者不要 Dispose，也不要改 Transform</b>（需要变换请用
        ///   <see cref="DecorPainter.CloneTextureBrush"/>）。
        /// </summary>
        /// <param name="options">外观运行时快照（只读）。</param>
        /// <param name="kind">资源类别（见 <see cref="DecorCacheKey.Kind*"/>）。</param>
        /// <param name="color">前景色（alpha 表示强度上限）。</param>
        /// <param name="tileWidthPx">瓦片宽度（物理像素；扫描线 = 线间距方向宽度，网格 = 间距）。</param>
        /// <param name="tileHeightPx">瓦片高度（物理像素；扫描线 = 线间距，网格 = 间距）。</param>
        /// <param name="seed">图案种子。</param>
        /// <param name="variant">类别相关的附加参数（噪点强度等）；默认 0。</param>
        /// <returns>借用引用（不要 Dispose）；类别不支持或尺寸非法时返回 null。</returns>
        public TextureBrush? GetOrAddTiledBrush(
            ThemeAppearanceOptions options,
            int kind,
            Color color,
            int tileWidthPx,
            int tileHeightPx,
            int seed = 0,
            int variant = 0)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (tileWidthPx <= 0 || tileHeightPx <= 0) return null;
            if (kind != DecorCacheKey.KindScanlineTile
                && kind != DecorCacheKey.KindGridTile
                && kind != DecorCacheKey.KindNoiseTile
                && kind != DecorCacheKey.KindBarTexture)
            {
                return null;
            }

            DecorCacheKey key = DecorCacheKey.Create(
                kind,
                options,
                color,
                Color.Empty,
                tileWidthPx,
                tileHeightPx,
                DecorCacheKey.DirectionRadial,
                seed,
                variant);

            return Cache.GetOrAddTextureBrush(key, k =>
            {
                Bitmap? tile = kind switch
                {
                    DecorCacheKey.KindScanlineTile => DecorTextureFactory.CreateScanlineTile(k.WidthPx, k.HeightPx, color),
                    DecorCacheKey.KindGridTile => DecorTextureFactory.CreateGridTile(k.WidthPx, k.HeightPx, color),
                    DecorCacheKey.KindNoiseTile => DecorTextureFactory.CreateNoiseTile(k.WidthPx, k.HeightPx, k.Seed, color, (int)k.Variant),
                    DecorCacheKey.KindBarTexture => DecorTextureFactory.CreateBarTextureTile(k.WidthPx, k.HeightPx, color),
                    _ => null
                };
                if (tile == null) throw new InvalidOperationException($"装饰纹理工厂无法生成类别 {kind} 的瓦片。");
                return new TextureBrush(tile);
            });
        }

        // ============================================================
        // 释放
        // ============================================================

        /// <summary>
        /// 释放全部缓存资源。本对象释放后不可再用。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cache.Dispose();
        }
    }
}
