using LiteMonitor.src.Core;
using System.Drawing.Text;

namespace LiteMonitor
{
    /// <summary>
    /// 任务栏渲染器（仅负责绘制，不再负责布局）
    /// </summary>
    public static class TaskbarRenderer
    {
        //private static readonly Settings _settings = Settings.Load();

        /// <summary>
        /// 任务栏渲染的独立样式上下文（用于预览等隔离场景，避免串扰生产静态样式）。
        /// 遵循“谁创建谁释放”原则，实现 IDisposable 安全释放自有的 Font。
        /// </summary>
        public sealed class TaskbarStyleContext : IDisposable
        {
            private bool _disposed;

            public Font Font { get; }
            public bool UseCustom { get; }
            public Color LabelColor { get; }
            public Color SafeColor { get; }
            public Color WarnColor { get; }
            public Color CritColor { get; }

            public TaskbarStyleContext(Settings cfg)
            {
                if (cfg == null) throw new ArgumentNullException(nameof(cfg));

                var s = cfg.GetStyle();
                try
                {
                    Font = new Font(s.Font, s.Size, s.Bold ? FontStyle.Bold : FontStyle.Regular);
                }
                catch
                {
                    // 字体族不存在或参数越界时退到系统默认族，避免整条任务栏渲染路径抛异常
                    Font = new Font(SystemFonts.DefaultFont.FontFamily, s.Size,
                                           s.Bold ? FontStyle.Bold : FontStyle.Regular);
                }

                bool useCustom = cfg.TaskbarCustomStyle;
                Color cLabel = Color.Empty;
                Color cSafe = Color.Empty;
                Color cWarn = Color.Empty;
                Color cCrit = Color.Empty;

                if (useCustom)
                {
                    try
                    {
                        cLabel = ColorTranslator.FromHtml(cfg.TaskbarColorLabel);
                        cSafe = ColorTranslator.FromHtml(cfg.TaskbarColorSafe);
                        cWarn = ColorTranslator.FromHtml(cfg.TaskbarColorWarn);
                        cCrit = ColorTranslator.FromHtml(cfg.TaskbarColorCrit);
                    }
                    catch
                    {
                        // 容错：如果解析失败，回退到默认
                        useCustom = false;
                    }
                }

                UseCustom = useCustom;
                LabelColor = cLabel;
                SafeColor = cSafe;
                WarnColor = cWarn;
                CritColor = cCrit;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    try { Font.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// 供测试专用的内部样式快照记录，反映生产静态样式字段的当前状态。
        /// </summary>
        internal record TaskbarStyleSnapshot(
            string? FontFamilyName,
            float? FontSize,
            FontStyle? FontStyle,
            bool UseCustom,
            Color LabelColor,
            Color SafeColor,
            Color WarnColor,
            Color CritColor);

        /// <summary>
        /// 测试专用：捕获当前生产静态样式字段的只读快照。
        /// </summary>
        internal static TaskbarStyleSnapshot CaptureStyleState()
        {
            return new TaskbarStyleSnapshot(
                _cachedFont?.FontFamily.Name,
                _cachedFont?.Size,
                _cachedFont?.Style,
                _useCustom,
                _cLabel,
                _cSafe,
                _cWarn,
                _cCrit);
        }

        // 字体缓存 - 直接初始化，避免每次渲染都创建字体
        private static Font? _cachedFont = null;

        // 浅色主题
        private static readonly Color LABEL_LIGHT = Color.FromArgb(20, 20, 20);
        private static readonly Color SAFE_LIGHT = Color.FromArgb(0x00, 0x80, 0x40);
        private static readonly Color WARN_LIGHT = Color.FromArgb(0xB5, 0x75, 0x00);
        private static readonly Color CRIT_LIGHT = Color.FromArgb(0xC0, 0x30, 0x30);

        // 深色主题
        private static readonly Color LABEL_DARK = Color.White;
        private static readonly Color SAFE_DARK = Color.FromArgb(0x66, 0xFF, 0x99);
        private static readonly Color WARN_DARK = Color.FromArgb(0xFF, 0xD6, 0x66);
        private static readonly Color CRIT_DARK = Color.FromArgb(0xFF, 0x66, 0x66);

        // ★★★ [新增] 自定义颜色缓存 ★★★
        private static bool _useCustom = false;
        private static Color _cLabel, _cSafe, _cWarn, _cCrit;

        // ★★★ [新增] 极简的核心：手动刷新缓存 ★★★
        // 在 UIController 初始化或配置变更时调用它
        public static void ReloadStyle(Settings cfg)
        {     
            var s = cfg.GetStyle();

            // ★★★ 这个字体由本类独占，所以「先释放旧的、再建新的」才是正确的所有权闭环 ★★★
            //
            // 为什么不能用 UIUtils.GetFont(...)：
            //   它返回的是 UIUtils._fontCache 里的**共享实例**（同 key 永远是同一个 Font 对象）。
            //   在这里 Dispose 它，缓存里就留下一个已释放的 Font，后续同 key 取值会把坏对象原样取回；
            //   而 TextRenderer.DrawText 用已释放字体并不抛异常、只是静默降级，
            //   于是症状是「应用设置 / 换肤后任务栏字体不再生效」，且没有任何异常可查。
            //   反过来若不 Dispose、只丢弃引用，则每次换样式漏一个 GDI 字体。
            //   两个方向都错 —— 所以改成自己 new 一份：谁建谁释放。
            //
            // 为什么必须自己持有而不能借缓存的实例：
            //   UIController.ApplyTheme 与 MainFormBizHelper.RebuildMenus 都会调
            //   UIUtils.ClearBrushCache()（换语言 / 改监控布局 / 主题编辑器确认都会走到 RebuildMenus），
            //   那一刻共享实例会被 Dispose 并清空，而之后并没有任何地方刷新本字段 ——
            //   借来的引用会变成悬空对象。本类自有的实例不来自那个缓存，天然不受影响。
            if (_cachedFont != null)
            {
                try { _cachedFont.Dispose(); } catch { }
                _cachedFont = null;
            }

            try
            {
                _cachedFont = new Font(s.Font, s.Size, s.Bold ? FontStyle.Bold : FontStyle.Regular);
            }
            catch
            {
                // 字体族不存在或参数越界时退到系统默认族，避免整条任务栏渲染路径抛异常
                _cachedFont = new Font(SystemFonts.DefaultFont.FontFamily, s.Size,
                                       s.Bold ? FontStyle.Bold : FontStyle.Regular);
            }

            // 颜色依然允许自定义
            _useCustom = cfg.TaskbarCustomStyle;
            if (_useCustom)
            {
                try {
                    _cLabel = ColorTranslator.FromHtml(cfg.TaskbarColorLabel);
                    _cSafe = ColorTranslator.FromHtml(cfg.TaskbarColorSafe);
                    _cWarn = ColorTranslator.FromHtml(cfg.TaskbarColorWarn);
                    _cCrit = ColorTranslator.FromHtml(cfg.TaskbarColorCrit);
                } catch {
                    // 容错：如果解析失败，回退到默认
                    _useCustom = false; 
                }
            }
        }

        public static void Render(Graphics g, List<Column> cols, bool light, TaskbarStyleContext? context = null)
        {
            if (context == null)
            {
                // [防空策略] 万一还没人调用 ReloadStyle，就自己兜底初始化一次
                if (_cachedFont == null)
                {
                    // 兜底：读磁盘配置（仅第一次）
                    ReloadStyle(Settings.Load());
                }
            }
            
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            foreach (var col in cols)
            {
                // ★★★ [新增]：如果只有 Top 没有 Bottom，强制使用全高绘制（居中）
                if (col.Top != null && col.Bottom == null && col.Bounds != Rectangle.Empty)
                {
                    DrawItem(g, col.Top, col.Bounds, light, context);
                    continue; // 处理完这个特殊情况直接跳过本次循环
                }

                if (col.BoundsTop != Rectangle.Empty && col.Top != null)
                    DrawItem(g, col.Top, col.BoundsTop, light, context);

                if (col.BoundsBottom != Rectangle.Empty && col.Bottom != null)
                    DrawItem(g, col.Bottom, col.BoundsBottom, light, context);
            }
        }

        private static void DrawItem(Graphics g, MetricItem item, Rectangle rc, bool light, TaskbarStyleContext? context)
        {
            // ★★★ 优化：直接使用缓存的 ShortLabel，避免每帧生成 Key 和查询字典 ★★★
            string label = item.ShortLabel;

            // ★★★ 修复：如果 ShortLabel 被显式设为空格或空，则视为隐藏标签 ★★★
            // 适用于 IP/Dashboard 文本，直接绘制 Value (左对齐)
            bool hideLabel = (string.IsNullOrEmpty(label) || label == " ");

            // 如果不是隐藏，且为空，则回退到 Label 或 Key
            if (!hideLabel)
            {
                if (string.IsNullOrEmpty(label)) label = item.Label;
                if (string.IsNullOrEmpty(label)) label = item.Key;
            }

            string value = item.GetFormattedText(true);

            // 直接使用上下文中的字体或静态缓存字体，不再 new Font
            Font font = context != null ? context.Font : _cachedFont!;

            Color labelColor, valueColor;

            // ★★★ [修改] 颜色选择逻辑 ★★★
            bool useCustom = context != null ? context.UseCustom : _useCustom;
            if (useCustom)
            {
                // 自定义模式：忽略系统明暗，强制使用自定义色
                labelColor = context != null ? context.LabelColor : _cLabel;
                valueColor = GetCustomStateColor(item.CachedColorState, context);
            }
            else
            {
                // 原有模式
                labelColor = light ? LABEL_LIGHT : LABEL_DARK;
                valueColor = GetStateColor(item.CachedColorState, light);
            }

            // ★★★ 修复：如果开启了隐藏标签 (如 IP/Dashboard)，则仅绘制 Value (左对齐) ★★★
            if (hideLabel)
            {
                TextRenderer.DrawText(
                    g, value, font, rc, valueColor,
                    TextFormatFlags.Left |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.NoClipping
                );
                return;
            }

            // Label 左对齐
            TextRenderer.DrawText(
                g, label, font, rc, labelColor,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoClipping
            );

            // Value 右对齐
            TextRenderer.DrawText(
                g, value, font, rc, valueColor,
                TextFormatFlags.Right |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoClipping
            );
        }

        // [新增] 辅助：根据状态快速获取颜色 (替代原来的 PickColor)
        private static Color GetStateColor(int state, bool light)
        {
            if (state == 2) return light ? CRIT_LIGHT : CRIT_DARK;
            if (state == 1) return light ? WARN_LIGHT : WARN_DARK;
            return light ? SAFE_LIGHT : SAFE_DARK;
        }

        // [新增] 辅助：自定义模式
        private static Color GetCustomStateColor(int state, TaskbarStyleContext? context = null)
        {
            if (context != null)
            {
                if (state == 2) return context.CritColor;
                if (state == 1) return context.WarnColor;
                return context.SafeColor;
            }
            if (state == 2) return _cCrit;
            if (state == 1) return _cWarn;
            return _cSafe;
        }
    }
}
