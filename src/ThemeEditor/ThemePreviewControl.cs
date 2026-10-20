using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;
using LiteMonitor.src.UI.Decor;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// 实时预览控件（独立于 LiteMonitor++ 主程序）
    /// - 使用 Mock 数据
    /// - 使用 UILayout + UIRenderer 渲染
    /// - 背景 + 边框 + DPI 处理
    ///
    /// ★ 预览副本合同（外观改造计划 v4 §3.2 / A10）：
    /// 1. 布局使用 <b>独立副本</b>：<c>original.Layout.CreatePreviewCopy(dpiScale)</c>，
    ///    复制全部配置字段与扩展数据，并且<b>只缩放一次</b>
    ///    （源已缩放过时不再重复缩放，LayoutScale 语义保持正确）。
    ///    <b>禁止</b>手写 <c>new LayoutConfig { ... }</c> 逐字段复制。
    /// 2. 颜色 / 阈值 / 背景配置<b>只读借用</b>，预览不会修改它们。
    /// 3. 字体<b>只借用，不释放</b>原主题字体（预览主题绝不调用 DisposeFonts）。
    /// 4. 预览<b>不启动桌面捕获</b>：玻璃使用固定样例图，并在画面上注明"预览"。
    /// 5. 实际 DPI 取当前控件的 <see cref="Control.DeviceDpi"/>，不用全局 UIUtils.ScaleFactor。
    /// 6. 归一化只发生在运行时副本（ThemeAppearanceOptions），绝不回写用户 JSON。
    /// </summary>
    public class ThemePreviewControl : Panel
    {
        private Theme? _theme;
        private UILayout? _layout;
        private readonly List<GroupLayoutInfo> _groups = new();
        private float _dpiScale = 1.0f;

        // ★★★ 新增：复用 Bitmap，避免每次 OnPaint 都创建 ★★★
        private Bitmap? _cachedBitmap;
        private Size _lastSize;

        /// <summary>背景层渲染器：<b>不注册任何实时后端</b>，玻璃只会用固定样例图。</summary>
        private readonly BackdropRenderer _backdrop = new();

        /// <summary>预览专用的固定样例图（静态图案，绝不来自桌面捕获）。</summary>
        private Bitmap? _previewSample;

        public ThemePreviewControl()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;

            // DPI 缩放支持 - 手动计算内边距
            _dpiScale = GetDpiScale();
            Padding = new Padding((int)(5 * _dpiScale));
        }

        /// <summary>
        /// 当前控件的实际 DPI 比例。
        /// 取当前窗口的 <see cref="Control.DeviceDpi"/>（所在显示器的真实 DPI），
        /// <b>不</b>使用全局 <c>UIUtils.ScaleFactor</c>（进程级值，跨屏不可靠）。
        /// </summary>
        public float DpiScale => GetDpiScale();

        /// <summary>
        /// 编辑器传入主题
        /// </summary>
        public void SetTheme(Theme theme)
        {
            if (theme == null) throw new ArgumentNullException(nameof(theme));

            _theme = theme;
            _dpiScale = GetDpiScale();

            // 创建 DPI 缩放后的主题副本用于预览（独立布局副本，见类注释合同）
            _layout = new UILayout(CreatePreviewTheme(theme, _dpiScale));

            BuildMockData();
            Invalidate();
        }

        // ================================================================
        // 预览副本（静态、可测试）
        // ================================================================

        /// <summary>
        /// 创建预览主题：
        /// - Layout：<b>独立副本</b>（<c>CreatePreviewCopy</c>，含全部字段与扩展数据，只缩放一次）；
        /// - Color / Thresholds / Backdrop：只读借用；
        /// - 字体：只借用（原主题负责释放，预览绝不释放）。
        /// </summary>
        /// <param name="original">原主题（不会被修改）。</param>
        /// <param name="dpiScale">预览使用的 DPI 比例。</param>
        public static Theme CreatePreviewTheme(Theme original, float dpiScale)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));

            float scale = dpiScale <= 0f ? 1.0f : dpiScale;

            var preview = new Theme
            {
                Name = original.Name,
                Version = original.Version,

                // ★ 独立布局副本：复制所有配置字段与扩展数据；已缩放的源不会被二次缩放
                Layout = original.Layout != null
                    ? original.Layout.CreatePreviewCopy(scale)
                    : new LayoutConfig(),

                // 只读借用（预览不会修改原主题）
                Font = original.Font,
                Thresholds = original.Thresholds,
                Color = original.Color,
                Backdrop = original.Backdrop,

                // 字体：只借用，不释放
                FontTitle = original.FontTitle,
                FontGroup = original.FontGroup,
                FontItem = original.FontItem,
                FontValue = original.FontValue,
                FontTaskbar = original.FontTaskbar
            };

            // 主题级扩展数据一并复制（未知键不丢）
            if (original.ExtensionData != null && original.ExtensionData.Count > 0)
            {
                foreach (KeyValuePair<string, System.Text.Json.JsonElement> kv in original.ExtensionData)
                    preview.ExtensionData[kv.Key] = kv.Value;
            }

            return preview;
        }

        /// <summary>
        /// 构造 Mock 数据用于预览（静态版本，供测试直接使用）。
        /// </summary>
        public static List<GroupLayoutInfo> CreateMockGroups()
        {
            var groups = new List<GroupLayoutInfo>();

            groups.Add(new GroupLayoutInfo("CPU", new()
            {
                new MetricItem { Key = "CPU.Load", Value = 23, DisplayValue = 23, Label = "CPU 使用率" },
                new MetricItem { Key = "CPU.Temp", Value = 65, DisplayValue = 65, Label = "CPU 温度" }
            }));

            groups.Add(new GroupLayoutInfo("GPU", new()
            {
                new MetricItem { Key = "GPU.Load", Value = 90, DisplayValue = 90, Label = "GPU 使用率" },
                new MetricItem { Key = "GPU.Temp", Value = 65, DisplayValue = 65, Label = "GPU 温度" },
                new MetricItem { Key = "GPU.VRAM", Value = 41, DisplayValue = 41, Label = "VRAM 占用" }
            }));

            groups.Add(new GroupLayoutInfo("MEM", new()
            {
                new MetricItem { Key = "MEM.Load", Value = 68, DisplayValue = 68, Label = "内存占用" }
            }));

            groups.Add(new GroupLayoutInfo("DISK", new()
            {
                new MetricItem { Key = "DISK.Read", Value = 1024 * 500, Label = "磁盘读取" },
                new MetricItem { Key = "DISK.Write", Value = 1024 * 3320, Label = "磁盘写入" }
            }));

            groups.Add(new GroupLayoutInfo("NET", new()
            {
                new MetricItem { Key = "NET.Up", Value = 1024 * 80, Label = "上传速度" },
                new MetricItem { Key = "NET.Down", Value = 1024 * 38000, Label = "下载速度" }
            }));

            return groups;
        }

        /// <summary>
        /// 把预览主题渲染到指定画布（静态、可测试）。
        ///
        /// ★ 层次（v4 §3.3）：背景层 → 底纹（扫描线/网格）→ 内容 → 向内的窗口外框。
        /// ★ 预览只画"假底纹"与外框；辉光与新进度条样式要等 A07/A13 的正式渲染器接入。
        /// </summary>
        /// <param name="g">目标画布。</param>
        /// <param name="groups">要渲染的分组（Mock 数据）。</param>
        /// <param name="previewTheme">预览主题（由 <see cref="CreatePreviewTheme"/> 生成）。</param>
        /// <param name="bounds">完整客户区矩形（物理像素）。</param>
        /// <param name="backdrop">背景层渲染器（可空 = 不画背景层）。</param>
        /// <param name="previewSource">预览固定样例图（玻璃用；可空）。</param>
        /// <param name="dpiScale">实际 DPI 比例（用于新尺寸的唯一一次换算）。</param>
        public static void RenderPreview(
            Graphics g,
            List<GroupLayoutInfo> groups,
            Theme previewTheme,
            Rectangle bounds,
            BackdropRenderer? backdrop = null,
            Image? previewSource = null,
            float dpiScale = 1.0f)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (previewTheme == null) throw new ArgumentNullException(nameof(previewTheme));
            if (groups == null) throw new ArgumentNullException(nameof(groups));

            // 运行时副本：归一化只发生在这里，绝不回写用户 JSON
            ThemeAppearanceOptions options = ThemeAppearanceOptions.Create(
                previewTheme,
                dpiScale <= 0f ? 1.0f : dpiScale,
                1.0f,
                bounds,
                0,
                null,
                previewSource);

            bool backdropActive = IsBackdropActive(options, previewSource);

            // 1) 背景层（玻璃 = 固定样例图；绝不启动捕获）
            if (backdropActive && backdrop != null)
            {
                backdrop.Render(g, options);
            }

            // 2) 底纹（扫描线 / 网格）：预览用假绘制，
            //    正式实现由 A07 的 DecorRenderer 接管（A13 之后完整验收）。
            DrawPreviewTexture(g, options);

            // 3) 内容：UIRenderer 会用背景色铺满整块画布，
            //    启用外观效果时改用"透明背景"变体，让背景层 / 底纹透出来。
            bool needTransparentBackground = backdropActive || options.ScanlineEnabled || options.GridEnabled;
            Theme contentTheme = needTransparentBackground
                ? BuildContentTheme(previewTheme, transparentBackground: true)
                : previewTheme;

            UIRenderer.Render(g, groups, contentTheme);

            // 4) 向内的窗口外框（最后一层）
            if (options.FrameEnabled)
            {
                DecorPainter.FillInnerFrame(
                    g,
                    bounds,
                    options.FrameStyle,
                    options.FrameWidthPx,
                    options.FrameLengthPx,
                    previewTheme.Layout.CornerRadius,
                    options.BorderColor);
            }

            // 5) 预览提示（玻璃用的是固定样例图，不是实时桌面）
            if (options.GlassRequested)
            {
                DrawPreviewNotice(g, bounds);
            }
        }

        /// <summary>背景层是否真的会产生画面（solid / 未知模式 = 零副作用）。</summary>
        private static bool IsBackdropActive(ThemeAppearanceOptions options, Image? previewSource)
        {
            string mode = options.BackdropMode ?? ThemeAppearanceOptions.BackdropModeSolid;

            if (string.Equals(mode, ThemeAppearanceOptions.BackdropModeGradient, StringComparison.Ordinal)) return true;
            if (string.Equals(mode, ThemeAppearanceOptions.BackdropModeGlass, StringComparison.Ordinal)) return previewSource != null;

            return false;
        }

        /// <summary>
        /// 预览用"透明背景"变体：把背景色清空，
        /// UIUtils.GetBrush("") 会返回透明画刷 → UIRenderer 的背景铺底变成空操作。
        /// </summary>
        private static Theme BuildContentTheme(Theme previewTheme, bool transparentBackground)
        {
            if (!transparentBackground) return previewTheme;

            ColorConfig color = AppearanceThemeConfig.CloneColor(previewTheme.Color ?? new ColorConfig());
            color.Background = string.Empty;

            return new Theme
            {
                Name = previewTheme.Name,
                Version = previewTheme.Version,
                Layout = previewTheme.Layout,          // 借用（只读）
                Font = previewTheme.Font,
                Thresholds = previewTheme.Thresholds,
                Color = color,
                Backdrop = previewTheme.Backdrop,
                // 字体：只借用，不释放
                FontTitle = previewTheme.FontTitle,
                FontGroup = previewTheme.FontGroup,
                FontItem = previewTheme.FontItem,
                FontValue = previewTheme.FontValue,
                FontTaskbar = previewTheme.FontTaskbar
            };
        }

        /// <summary>
        /// 假底纹：扫描线（横线）与网格（横竖线）。
        /// ★ 只是为了让编辑器的外观字段"看得见"，
        ///   正式实现由 A07 的 DecorRenderer 负责（A13 之后完整验收）。
        /// </summary>
        private static void DrawPreviewTexture(Graphics g, ThemeAppearanceOptions options)
        {
            Rectangle b = options.ClientBounds;
            if (b.Width <= 0 || b.Height <= 0) return;

            if (options.ScanlineEnabled)
            {
                int alpha = (int)Math.Round(options.Scanline * 255d / 100d, MidpointRounding.AwayFromZero);
                if (alpha > 0)
                {
                    using var pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 1);
                    for (int y = b.Top; y < b.Bottom; y += 3)
                        g.DrawLine(pen, b.Left, y, b.Right, y);
                }
            }

            if (options.GridEnabled)
            {
                int alpha = (int)Math.Round(options.Grid * 255d / 100d, MidpointRounding.AwayFromZero);
                if (alpha > 0)
                {
                    using var pen = new Pen(Color.FromArgb(alpha, 120, 200, 255), 1);
                    for (int x = b.Left; x < b.Right; x += 24)
                        g.DrawLine(pen, x, b.Top, x, b.Bottom);
                    for (int y = b.Top; y < b.Height + b.Top; y += 24)
                        g.DrawLine(pen, b.Left, y, b.Right, y);
                }
            }
        }

        /// <summary>预览提示文字：玻璃用的是固定样例图。</summary>
        private static void DrawPreviewNotice(Graphics g, Rectangle bounds)
        {
            const string notice = "预览样例图 / Preview sample（非实时桌面捕获）";
            using var brush = new SolidBrush(Color.FromArgb(220, 255, 214, 0));
            using var font = new Font("Microsoft YaHei UI", 8f);
            g.DrawString(notice, font, brush, new PointF(bounds.Left + 4f, bounds.Top + 2f));
        }

        /// <summary>
        /// 构造 Mock 数据用于预览
        /// </summary>
        private void BuildMockData()
        {
            _groups.Clear();
            _groups.AddRange(CreateMockGroups());

            if (_layout != null)
                _layout.Build(_groups);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (_theme == null || _layout == null)
                return;

            e.Graphics.Clear(Color.FromArgb(245, 245, 245));

            Rectangle content = new Rectangle(
                Padding.Left,
                Padding.Top,
                Width - Padding.Left - Padding.Right,
                Height - Padding.Top - Padding.Bottom
            );

            if (content.Width <= 0 || content.Height <= 0) return;

            try
            {
                // 实际 DPI（窗口可能跨屏移动，每次绘制都重新取）
                float dpiScale = GetDpiScale();
                _dpiScale = dpiScale;

                var previewTheme = CreatePreviewTheme(_theme, dpiScale);
                var previewLayout = new UILayout(previewTheme);
                int h = previewLayout.Build(_groups);

                int bmpH = Math.Max(1, h);
                var requiredSize = new Size(Math.Max(1, previewTheme.Layout.Width), bmpH);

                // ★★★ 核心优化：复用 Bitmap 而非每次新建 ★★★
                if (_cachedBitmap == null || _lastSize != requiredSize)
                {
                    _cachedBitmap?.Dispose();
                    _cachedBitmap = new Bitmap(requiredSize.Width, requiredSize.Height);
                    _lastSize = requiredSize;
                }

                using (Graphics gBmp = Graphics.FromImage(_cachedBitmap))
                {
                    gBmp.Clear(Color.Transparent); // 清空上次内容
                    gBmp.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    gBmp.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    gBmp.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                    RenderPreview(
                        gBmp,
                        _groups,
                        previewTheme,
                        new Rectangle(0, 0, requiredSize.Width, requiredSize.Height),
                        _backdrop,
                        GetPreviewSample(),
                        dpiScale);
                }

                e.Graphics.DrawImageUnscaled(_cachedBitmap, content.X, content.Y);
            }
            catch (Exception ex)
            {
                e.Graphics.DrawString(
                    $"Render Error:\n{ex.Message}",
                    this.Font,
                    Brushes.Red,
                    new PointF(5, 5)
                );
            }
        }

        /// <summary>
        /// 预览固定样例图（静态图案）。
        /// ★ 预览路径<b>绝不</b>抓取桌面：这里只画一张固定图案。
        /// </summary>
        private Image? GetPreviewSample()
        {
            if (_previewSample == null)
                _previewSample = CreatePreviewSample();

            return _previewSample;
        }

        /// <summary>生成预览固定样例图（96×96 的静态图案）。</summary>
        public static Bitmap CreatePreviewSample()
        {
            var bmp = new Bitmap(96, 96);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(38, 42, 52));
                using var brush1 = new SolidBrush(Color.FromArgb(150, 90, 200, 255));
                using var brush2 = new SolidBrush(Color.FromArgb(150, 255, 120, 200));
                using var brush3 = new SolidBrush(Color.FromArgb(120, 120, 255, 160));
                g.FillEllipse(brush1, 6, 6, 44, 44);
                g.FillRectangle(brush2, 46, 40, 44, 44);
                g.FillEllipse(brush3, 30, 30, 40, 40);
            }
            return bmp;
        }

        /// <summary>
        /// 添加释放方法
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cachedBitmap?.Dispose();
                _cachedBitmap = null;

                // 只释放预览自己创建的位图；原主题的字体是借用的，绝不释放
                _previewSample?.Dispose();
                _previewSample = null;

                _backdrop.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            if (_layout != null)
            {
                _layout.Build(_groups);
                Invalidate();
            }
        }

        /// <summary>
        /// 取当前控件的真实 DPI 比例。
        /// </summary>
        private float GetDpiScale()
        {
            try
            {
                int dpi = DeviceDpi;
                if (dpi > 0) return dpi / 96f;
            }
            catch
            {
                // 句柄尚未创建时 DeviceDpi 可能不可用，退回 Graphics 查询
            }

            using (Graphics g = CreateGraphics())
            {
                if (g != null) return g.DpiX / 96f;
            }

            return 1.0f;
        }
    }
}
