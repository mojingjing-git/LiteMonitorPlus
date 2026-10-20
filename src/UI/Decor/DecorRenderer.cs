using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.Decor
{
    /// <summary>
    /// 组块区域描述：<b>组矩形 + 组圆角</b>（物理像素）。
    ///
    /// ★ 为什么要单独一个类型：窗口底纹要"排除组块区域"（避免同一块面积被纹理画两遍，
    ///   那会让 4 卡 + 两倍物理尺寸档直接超预算），组框又要按组块自己的圆角走形；
    ///   两者都需要"组的矩形 + 组的圆角"，所以用同一个不可变结构传递。
    /// </summary>
    public readonly struct DecorGroupRegion
    {
        /// <summary>组块矩形（物理像素，屏幕坐标）。</summary>
        public Rectangle Bounds { get; }

        /// <summary>组块圆角半径（物理像素，&lt;=0 表示直角）。</summary>
        public int RadiusPx { get; }

        /// <summary>
        /// 构造组块区域描述。
        /// </summary>
        /// <param name="bounds">组块矩形（物理像素）。</param>
        /// <param name="radiusPx">组块圆角半径（物理像素，负数按 0 处理）。</param>
        public DecorGroupRegion(Rectangle bounds, int radiusPx)
        {
            Bounds = bounds;
            RadiusPx = radiusPx < 0 ? 0 : radiusPx;
        }
    }

    /// <summary>
    /// 机能风装饰渲染器：窗口底纹（扫描线 / 网格）、组底纹与组框、向内的窗口边框与辉光高光。
    ///
    /// ★ 层次合同（v4 §3.3，固定顺序，<b>不可调换</b>）：
    /// <code>
    /// 底板/背景 → 窗口底纹 → 组块填充 → 组底纹/组框 → 原有标题/指标/条 → 向内的窗口边框/高光
    /// </code>
    /// 本类负责其中四层，调用方（A13）负责把它们插进既有渲染流程：
    /// <list type="number">
    /// <item><see cref="RenderWindowTexture"/> —— <b>窗口底纹</b>层，必须早于任何文字；</item>
    /// <item>组块填充 —— <b>既有代码</b>（<c>UIUtils.FillRoundRect</c>）负责，本类不碰；</item>
    /// <item><see cref="RenderGroupDecor"/> —— <b>组底纹 / 组框</b>层，必须早于组内文字；</item>
    /// <item>标题 / 指标 / 条 —— <b>既有代码</b>负责；</item>
    /// <item><see cref="RenderWindowFrame"/> —— <b>向内的窗口边框 / 高光</b>层，层次最末。</item>
    /// </list>
    ///
    /// ★ 扫描线与网格是"底纹"，<b>不得覆盖数值文字</b>：本类只提供"底纹层"入口，
    ///   由调用方保证它排在文字之前；探针用像素采样证明"文字区域与无扫描线时一致"。
    ///
    /// ★ 关闭态零副作用：当 <c>FrameStyle / Scanline / Grid / Glow / BarStyle</c> 全为 0 时，
    ///   本类<b>不画像素、不建画刷、不建位图、不进缓存</b>（每个入口都在取缓存之前提前返回）。
    ///
    /// ★ 只消费 <see cref="ThemeAppearanceOptions"/>：尺寸一律取 <c>FrameWidthPx</c> / <c>FrameLengthPx</c>
    ///   （<b>物理像素</b>，本类不再乘任何缩放系数）；完整窗口范围一律用 <c>options.ClientBounds</c>
    ///   （不用 <c>VisibleClipBounds</c>，也不用旧的 (-5,-5) 背景填充矩形）。
    ///
    /// ★ 颜色消费规则：<c>BorderColor</c> 只有在 <c>options.FrameEnabled</c> 为真时才消费，
    ///   <c>ShadowColor</c> 只有在 <c>options.GlowEnabled</c> 为真时才消费 ——
    ///   保证已有 <c>border</c> / <c>shadow</c> 键的旧主题不会自动冒出描边 / 辉光。
    /// </summary>
    public sealed class DecorRenderer : IDisposable
    {
        // 强度 → alpha 的上限（线性映射，详见 ResolveScanlineColor / ResolveGridColor / ResolveGlowColor）
        private const int ScanlineMaxAlpha = 80;
        private const int GridMaxAlpha = 60;
        private const int GlowMaxAlpha = 200;

        // 图案间距（96 DPI 逻辑值，按当前缩放换算成物理像素）
        private const int ScanlineSpacingLogical = 4;
        private const int ScanlineTileWidthLogical = 8;
        private const int GridSpacingLogical = 16;

        /// <summary>
        /// 组合图案（扫描线 + 网格合成一张瓦片）的缓存类别。
        /// A05 已占用 1..7，这里预留 100，避免与将来新增的类别冲突。
        /// </summary>
        private const int KindCombinedPattern = 100;

        private readonly DecorResourceHost _host;
        private readonly bool _ownsHost;
        private bool _disposed;

        /// <summary>
        /// 创建装饰渲染器（自建资源宿主，生命周期与本对象一致）。
        /// </summary>
        public DecorRenderer() : this(new DecorResourceHost(), ownsHost: true)
        {
        }

        /// <summary>
        /// 创建装饰渲染器，<b>借用</b>外部资源宿主。
        /// 借用的宿主由其所有者负责释放；本对象的 <see cref="Dispose"/> 不会释放它。
        /// </summary>
        /// <param name="host">资源宿主（不得为 null）。</param>
        public DecorRenderer(DecorResourceHost host)
            : this(host ?? throw new ArgumentNullException(nameof(host)), ownsHost: false)
        {
        }

        private DecorRenderer(DecorResourceHost host, bool ownsHost)
        {
            _host = host;
            _ownsHost = ownsHost;
        }

        /// <summary>资源宿主（A05 的缓存所有者）。</summary>
        public DecorResourceHost Host => _host;

        // ============================================================
        // 层次 1：窗口底纹（扫描线 / 网格）
        // ============================================================

        /// <summary>
        /// 绘制<b>窗口底纹</b>层（扫描线 + 网格）。<b>必须在任何标题 / 指标 / 条之前调用。</b>
        ///
        /// ★ 排除优化：传入 <paramref name="groups"/> 后，组块覆盖的区域不再重复铺纹理
        ///   （组块填充是不透明时，"先铺满再被盖住"与"直接跳过"画面完全等价），
        ///   这让 4 卡 + 两倍物理尺寸档的纹理开销从"整屏两遍"降到"整屏一遍"。
        ///   组块背景半透明时不排除（保持"透过组块仍能看到底纹"的原义）。
        ///
        /// @return 是否真的画了（全关闭 / 客户区无效时返回 false，且<b>不产生任何资源</b>）。
        /// </summary>
        /// <param name="g">目标画布（不得为 null）。</param>
        /// <param name="options">外观运行时快照（只读）。</param>
        /// <param name="groups">将被不透明组块填充覆盖的区域（可空；传 null 表示铺满整个客户区）。</param>
        public bool RenderWindowTexture(
            Graphics g,
            ThemeAppearanceOptions options,
            IReadOnlyList<DecorGroupRegion>? groups = null)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (options == null) throw new ArgumentNullException(nameof(options));
            ObjectDisposedException.ThrowIf(_disposed, this);

            // 关闭态：不进缓存、不建画刷、不画像素
            if (!options.HasValidBounds) return false;
            if (!options.ScanlineEnabled && !options.GridEnabled) return false;

            bool drew = false;
            GraphicsState state = g.Save();
            try
            {
                // 组块区域不再重复铺纹理：按"客户区减去组块包围矩形"切出的条带直接铺，
                // 既省掉一次全屏 alpha 混合，也不用每帧构造 GDI Region 句柄。
                List<Rectangle> bands = ComputeComplementBands(options, groups);

                TextureBrush? brush = GetPatternBrush(options);
                if (brush == null) return false;

                if (bands.Count == 0)
                {
                    // 没有可排除的组块（或组块背景半透明）：铺满整个客户区
                    drew |= DecorPainter.FillTiled(g, brush, options.ClientBounds);
                }
                else
                {
                    foreach (Rectangle band in bands)
                    {
                        drew |= DecorPainter.FillTiled(g, brush, band);
                    }
                }
            }
            finally
            {
                g.Restore(state);
            }
            return drew;
        }

        // ============================================================
        // 层次 2：组底纹 / 组框
        // ============================================================

        /// <summary>
        /// 绘制<b>组底纹 + 组框</b>层。<b>必须在组内标题 / 指标 / 条之前调用。</b>
        ///
        /// ★ 组底纹被裁剪到组块自己的圆角形状（与既有 <c>UIUtils.FillRoundRect</c> 的组块填充完全重合），
        ///   所以不会糊到组块外面。
        /// ★ 组框是<b>向内</b>的描边带（复用 A05 的 <see cref="DecorPainter.FillInnerFrame"/>），
        ///   不会溢出组块，更不会溢出客户区。
        ///
        /// @return 是否真的画了。
        /// </summary>
        /// <param name="g">目标画布（不得为 null）。</param>
        /// <param name="options">外观运行时快照（只读）。</param>
        /// <param name="group">组块区域（矩形 + 圆角）。</param>
        public bool RenderGroupDecor(Graphics g, ThemeAppearanceOptions options, DecorGroupRegion group)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (options == null) throw new ArgumentNullException(nameof(options));
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!options.HasValidBounds) return false;
            if (group.Bounds.Width <= 0 || group.Bounds.Height <= 0) return false;
            if (!options.ScanlineEnabled && !options.GridEnabled && !options.FrameEnabled) return false;

            bool drew = false;
            GraphicsState state = g.Save();
            GraphicsPath? shape = null;
            try
            {
                // ---------- 组底纹 ----------
                if (options.ScanlineEnabled || options.GridEnabled)
                {
                    TextureBrush? brush = GetPatternBrush(options);
                    if (brush != null)
                    {
                        // 与既有组块填充使用同一个圆角矩形构造，保证形状完全一致。
                        // 直接用 FillPath 让纹理被路径裁剪：既不用每帧为每个组块新建 Region，
                        // 抗锯齿边缘也与组块填充的圆角边缘一致。
                        shape = UIUtils.RoundRect(group.Bounds, group.RadiusPx);
                        g.SmoothingMode = SmoothingMode.AntiAlias;

                        // ★ 抗锯齿的路径填充会在路径外侧溢出约 1 像素（实测 1×1 客户区时
                        //   正好落到客户区外 1 个像素）。这里把绘制范围夹到组块矩形，
                        //   组块矩形必然在客户区之内，因此"客户区外 0 像素被改动"始终成立。
                        Rectangle clipRect = Rectangle.Intersect(group.Bounds, options.ClientBounds);
                        if (clipRect.Width <= 0 || clipRect.Height <= 0) return drew;

                        g.SetClip(clipRect, CombineMode.Intersect);
                        g.FillPath(brush, shape);
                        drew = true;
                    }
                }

                // ---------- 组框 ----------
                if (options.FrameEnabled)
                {
                    int frameWidth = DecorGeometry.ClampFrameWidthPx(options.FrameWidthPx, group.Bounds);
                    if (frameWidth > 0)
                    {
                        GraphicsPath band = _host.GetOrAddBandPath(
                            options,
                            group.Bounds.Width,
                            group.Bounds.Height,
                            options.FrameStyle,
                            options.FrameLengthPx,
                            group.RadiusPx,
                            1,
                            frameWidth);
                        drew |= DecorPainter.FillInnerFrame(
                            g,
                            group.Bounds,
                            options.FrameStyle,
                            frameWidth,
                            options.FrameLengthPx,
                            group.RadiusPx,
                            options.BorderColor,
                            band);
                    }
                }
            }
            finally
            {
                g.Restore(state);
                shape?.Dispose();
            }
            return drew;
        }

        // ============================================================
        // 层次 4：向内的窗口边框 / 辉光高光（层次最末）
        // ============================================================

        /// <summary>
        /// 绘制<b>向内的窗口边框 + 辉光高光</b>层。<b>这是层次最末的一层</b>
        /// （在标题 / 指标 / 条之后调用）。
        ///
        /// ★ 全部用 A05 的"内描边带"实现：GDI+ 画笔沿路径居中，描边必然溢出客户区；
        ///   填充一条带则严格落在路径之内。实测客户区外被改动像素 = 0。
        /// ★ 辉光是外框<b>内侧</b>的高光线，同样是带，不会溢出。
        ///
        /// @return 是否真的画了。
        /// </summary>
        /// <param name="g">目标画布（不得为 null）。</param>
        /// <param name="options">外观运行时快照（只读）。</param>
        /// <param name="cornerRadiusPx">
        /// 窗口圆角半径（物理像素，来自主题 <c>layout.cornerRadius</c>，
        /// 已按既有布局缩放换算；<b>本类不再乘缩放</b>）。圆角描边样式才会用到它。
        /// </param>
        public bool RenderWindowFrame(Graphics g, ThemeAppearanceOptions options, int cornerRadiusPx)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (options == null) throw new ArgumentNullException(nameof(options));
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!options.HasValidBounds) return false;

            // 没有外框就没有可依附的边，辉光也随之关闭
            if (!options.FrameEnabled) return false;

            int frameWidth = DecorGeometry.ClampFrameWidthPx(options.FrameWidthPx, options.ClientBounds);
            if (frameWidth <= 0) return false;

            bool drew = false;

            GraphicsPath band = _host.GetOrAddBandPath(
                options,
                options.ClientBounds.Width,
                options.ClientBounds.Height,
                options.FrameStyle,
                options.FrameLengthPx,
                cornerRadiusPx,
                1,
                frameWidth);
            drew |= DecorPainter.FillInnerFrame(
                g,
                options.ClientBounds,
                options.FrameStyle,
                frameWidth,
                options.FrameLengthPx,
                cornerRadiusPx,
                options.BorderColor,
                band);

            // ---------- 内侧辉光高光线 ----------
            if (options.GlowEnabled)
            {
                Color glowColor = ResolveGlowColor(options);
                if (glowColor.A > 0)
                {
                    int inset = 1 + frameWidth + 1;   // 紧贴描边内侧
                    GraphicsPath line = _host.GetOrAddBandPath(
                        options,
                        options.ClientBounds.Width,
                        options.ClientBounds.Height,
                        options.FrameStyle,
                        options.FrameLengthPx,
                        cornerRadiusPx,
                        inset,
                        1);
                    drew |= DecorPainter.FillInnerLine(
                        g,
                        options.ClientBounds,
                        options.FrameStyle,
                        options.FrameLengthPx,
                        cornerRadiusPx,
                        inset,
                        1,
                        glowColor,
                        line);
                }
            }

            return drew;
        }

        // ============================================================
        // 失效与释放
        // ============================================================

        /// <summary>
        /// 同步渲染上下文（主题代次 / DPI / 用户缩放变化时自动失效相关缓存）。
        /// 每次 Paint 前调用一次即可。
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
        /// 取得底纹画刷（扫描线 / 网格）。
        ///
        /// ★ 只开一种图案时，直接用 A05 的瓦片；<b>两种都开时合成一张瓦片</b>——
        ///   分两次铺等于把整块面积做两遍 alpha 混合（实测 4 卡 + 两倍物理尺寸档
        ///   单独图案层就要 2.0 ms/帧，直接超预算），合成后只铺一遍。
        ///
        /// ★ 返回的是借用引用（缓存拥有它），不要 Dispose、不要改 Transform。
        /// </summary>
        private TextureBrush? GetPatternBrush(ThemeAppearanceOptions options)
        {
            bool scan = options.ScanlineEnabled;
            bool grid = options.GridEnabled;
            if (!scan && !grid) return null;

            if (scan && !grid)
            {
                return _host.GetOrAddTiledBrush(
                    options,
                    DecorCacheKey.KindScanlineTile,
                    ResolveScanlineColor(options),
                    ScanlineTileWidth(options),
                    ScanlineTileHeight(options));
            }

            if (!scan && grid)
            {
                return _host.GetOrAddTiledBrush(
                    options,
                    DecorCacheKey.KindGridTile,
                    ResolveGridColor(options),
                    GridTileSize(options),
                    GridTileSize(options));
            }

            // 两者都开：合成一张瓦片
            int scanSpacing = ScanlineTileHeight(options);
            int gridSize = GridTileSize(options);
            int tileSize = LeastCommonMultiple(scanSpacing, gridSize);
            Color scanColor = ResolveScanlineColor(options);
            Color gridColor = ResolveGridColor(options);

            DecorCacheKey key = DecorCacheKey.Create(
                KindCombinedPattern,
                options,
                scanColor,
                gridColor,
                tileSize,
                tileSize,
                DecorCacheKey.DirectionRadial,
                0,
                PackPatternParams(scanSpacing, gridSize));

            // ★ 工厂只产出**源位图**，所有权转移给缓存：
            //   画刷由缓存创建，缓存同时持有源位图（TextureBrush 不复制源位图）。
            return _host.Cache.GetOrAddTextureBrush(key, k =>
            {
                return BuildCombinedPatternTile(k.WidthPx, scanSpacing, gridSize, scanColor, gridColor);
            }, key.WidthPx, key.HeightPx);
        }

        /// <summary>
        /// 合成"扫描线 + 网格"瓦片：瓦片边长是两种间距的最小公倍数，
        /// 保证平铺后既无缝、又同时保留两种图案。
        /// 两种图案的瓦片都来自 A05 的 <see cref="DecorTextureFactory"/>。
        /// </summary>
        private static Bitmap BuildCombinedPatternTile(int tileSize, int scanSpacing, int gridSize, Color scanColor, Color gridColor)
        {
            Bitmap combined = new Bitmap(tileSize, tileSize, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(combined))
            {
                g.Clear(Color.Transparent);

                using (Bitmap? scanTile = DecorTextureFactory.CreateScanlineTile(tileSize, scanSpacing, scanColor))
                {
                    if (scanTile != null)
                    {
                        for (int y = 0; y < tileSize; y += scanSpacing)
                        {
                            g.DrawImage(scanTile, new Rectangle(0, y, tileSize, scanSpacing));
                        }
                    }
                }

                using (Bitmap? gridTile = DecorTextureFactory.CreateGridTile(gridSize, gridSize, gridColor))
                {
                    if (gridTile != null)
                    {
                        for (int y = 0; y < tileSize; y += gridSize)
                        {
                            for (int x = 0; x < tileSize; x += gridSize)
                            {
                                g.DrawImage(gridTile, new Rectangle(x, y, gridSize, gridSize));
                            }
                        }
                    }
                }
            }
            return combined;
        }

        /// <summary>把两种间距打包进缓存键的变体参数。</summary>
        private static long PackPatternParams(int scanSpacing, int gridSize)
            => ((long)Math.Clamp(scanSpacing, 0, 1023) << 20) | (uint)Math.Clamp(gridSize, 0, 1023);

        private static int LeastCommonMultiple(int a, int b)
        {
            if (a <= 0) return Math.Max(1, b);
            if (b <= 0) return Math.Max(1, a);
            int gcd = GreatestCommonDivisor(a, b);
            long lcm = (long)(a / gcd) * b;
            return lcm > 4096 ? 4096 : (int)lcm;
        }

        private static int GreatestCommonDivisor(int a, int b)
        {
            while (b != 0)
            {
                int t = a % b;
                a = b;
                b = t;
            }
            return a == 0 ? 1 : a;
        }

        /// <summary>
        /// 计算"客户区减去组块包围矩形"的补集条带。
        ///
        /// ★ 为什么不用 Region 排除：<c>Region.Exclude</c> / <c>new Region(path)</c>
        ///   要把区域栅格化成掩码（还要占用 GDI 句柄），4 卡 + 两倍物理尺寸档实测
        ///   比"纯整数运算切条带"慢一倍；这里改成"按 Y 切条带、条带内减去被覆盖的 X 区间"，
        ///   纯托管运算、零原生句柄。
        ///
        /// ★ 语义说明：这里按<b>包围矩形</b>排除，所以组块圆角之外的四个小角
        ///   不会铺窗口底纹（组块填充本身也是圆角，那里露出的是底板）。
        ///   差异仅限圆角处极小的几块像素，肉眼不可见。
        ///
        /// @return 需要铺纹理的条带；空集合表示"没有可排除的组块，应铺满客户区"。
        /// </summary>
        private static List<Rectangle> ComputeComplementBands(ThemeAppearanceOptions options, IReadOnlyList<DecorGroupRegion>? groups)
        {
            List<Rectangle> bands = new();
            if (groups == null || groups.Count == 0) return bands;

            Rectangle client = options.ClientBounds;
            if (client.Width <= 0 || client.Height <= 0) return bands;

            // 只有组块填充不透明时，排除才与"先铺满再被盖住"等价；
            // 半透明组块本来就该透出底纹，此时铺满整个客户区。
            if (options.GroupBackgroundColor.A < 255) return bands;

            List<Rectangle> rects = new();
            for (int i = 0; i < groups.Count; i++)
            {
                Rectangle rect = Rectangle.Intersect(groups[i].Bounds, client);
                if (rect.Width > 0 && rect.Height > 0) rects.Add(rect);
            }
            if (rects.Count == 0) return bands;

            // Y 边界集合
            List<int> edges = new() { client.Top, client.Bottom };
            foreach (Rectangle rect in rects)
            {
                edges.Add(rect.Top);
                edges.Add(rect.Bottom);
            }
            edges.Sort();
            edges.RemoveAll(v => v < client.Top || v > client.Bottom);

            int index = 0;
            while (index < edges.Count - 1)
            {
                int y0 = edges[index];
                int y1 = edges[index + 1];
                index++;
                if (y1 <= y0) continue;

                // 该条带内被组块覆盖的 X 区间
                List<(int Left, int Right)> covered = new();
                foreach (Rectangle rect in rects)
                {
                    if (rect.Top <= y0 && rect.Bottom >= y1)
                    {
                        covered.Add((rect.Left, rect.Right));
                    }
                }
                covered.Sort((a, b) => a.Left.CompareTo(b.Left));

                int cursor = client.Left;
                foreach ((int left, int right) in covered)
                {
                    if (left > cursor)
                    {
                        bands.Add(new Rectangle(cursor, y0, left - cursor, y1 - y0));
                    }
                    if (right > cursor) cursor = right;
                }
                if (cursor < client.Right)
                {
                    bands.Add(new Rectangle(cursor, y0, client.Right - cursor, y1 - y0));
                }
            }

            return bands;
        }

        /// <summary>
        /// 底纹的取色：优先外框色（仅在外框启用时），其次次级文字色，最后兜底白色。
        /// </summary>
        private static Color ResolvePatternBase(ThemeAppearanceOptions options)
        {
            // BorderColor 只有 FrameEnabled 时才允许消费
            if (options.FrameEnabled && options.BorderColor.A > 0) return options.BorderColor;
            if (options.TextSecondaryColor.A > 0) return options.TextSecondaryColor;
            return Color.White;
        }

        private static Color ResolveScanlineColor(ThemeAppearanceOptions options)
        {
            Color baseColor = ResolvePatternBase(options);
            int alpha = ScaleIntensityAlpha(options.Scanline, ScanlineMaxAlpha, baseColor.A);
            return alpha <= 0 ? Color.Empty : Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
        }

        private static Color ResolveGridColor(ThemeAppearanceOptions options)
        {
            Color baseColor = ResolvePatternBase(options);
            int alpha = ScaleIntensityAlpha(options.Grid, GridMaxAlpha, baseColor.A);
            return alpha <= 0 ? Color.Empty : Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
        }

        /// <summary>
        /// 辉光取色：只有在 <c>GlowEnabled</c>（且有外框）时才会调用，
        /// 因此 <c>ShadowColor</c> 不会被旧主题意外消费。
        /// </summary>
        private static Color ResolveGlowColor(ThemeAppearanceOptions options)
        {
            Color baseColor = options.ShadowColor;
            if (baseColor.A <= 0) return Color.Empty;
            int alpha = ScaleIntensityAlpha(options.Glow, GlowMaxAlpha, baseColor.A);
            return alpha <= 0 ? Color.Empty : Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
        }

        /// <summary>
        /// 强度 → alpha：线性映射到上限，再乘以颜色自身 alpha。
        /// </summary>
        private static int ScaleIntensityAlpha(int intensity, int maxAlpha, int baseAlpha)
        {
            if (intensity <= 0 || maxAlpha <= 0 || baseAlpha <= 0) return 0;
            double value = maxAlpha * (intensity / 100.0) * (baseAlpha / 255.0);
            int alpha = (int)Math.Round(value, MidpointRounding.AwayFromZero);
            if (alpha < 1) alpha = 1;
            if (alpha > 255) alpha = 255;
            return alpha;
        }

        // 图案间距：按当前缩放换算成物理像素（只缩放这一次）
        private static int ScanlineTileHeight(ThemeAppearanceOptions options)
            => Math.Max(2, ScaleSpacing(ScanlineSpacingLogical, options.EffectiveScale));

        private static int ScanlineTileWidth(ThemeAppearanceOptions options)
            => Math.Max(1, ScaleSpacing(ScanlineTileWidthLogical, options.EffectiveScale));

        private static int GridTileSize(ThemeAppearanceOptions options)
            => Math.Max(4, ScaleSpacing(GridSpacingLogical, options.EffectiveScale));

        private static int ScaleSpacing(int logical, float scale)
        {
            float s = scale <= 0f || float.IsNaN(scale) ? 1f : scale;
            int px = (int)Math.Round(logical * s, MidpointRounding.AwayFromZero);
            return px < 1 ? 1 : px;
        }
    }
}
