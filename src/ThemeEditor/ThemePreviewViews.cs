using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// 预览用**固定模拟场景**（TE04 交付定义）。
    /// <para>
    /// ★ 直接构造 <see cref="MetricItem"/> / <see cref="Column"/>：<b>不建采集源</b>、
    ///   <b>不读实时硬件</b>、<b>不写磁盘</b>。数值是写死的常量 ⇒ 证据图与断言
    ///   不依赖运行机器的实时状态。
    /// </para>
    /// <para>
    /// ★ <see cref="MetricItem.BoundConfig"/> 一律留空：生产路径靠它解析用户自定义标签与单位格式，
    ///   预览没有会话上下文，留空时 <c>MetricLabelResolver</c> 直接返回空串，
    ///   <see cref="MetricItem.Label"/> / <see cref="MetricItem.ShortLabel"/> 回落到自己设置的值，
    ///   <b>静态解析器（DefaultConfigLookup / DefaultPluginColorResolver）为空也是安全的</b>。
    /// </para>
    /// </summary>
    public static class ThemePreviewScene
    {
        // ★ 固定模拟数值（交付定义里的场景常量）
        public const float CpuLoad = 23f;          // CPU 使用率 23%
        public const float CpuTemp = 65f;          // CPU 温度 65°C
        public const float GpuLoad = 90f;          // GPU 使用率 90%
        public const float GpuTemp = 65f;          // GPU 温度 65°C
        public const float GpuVram = 41f;          // 显存占用 41%
        public const float MemoryUsage = 68f;      // 内存占用 68%
        public const float NetUpBytesPerSecond = 80f * 1024f;          // 上行 80 KB/s
        public const float NetDownBytesPerSecond = 38000f * 1024f;     // 下行 38000 KB/s

        /// <summary>
        /// 构造固定模拟场景的指标列表（顺序即配对顺序）：
        /// CPU.Load / CPU.Temp / GPU.Load / GPU.Temp / GPU.VRAM / MEM.Load / NET.Up / NET.Down。
        /// </summary>
        public static List<MetricItem> CreateMetrics()
        {
            return new List<MetricItem>
            {
                CreateMetric("CPU.Load",   "CPU 使用率", "CPU",  CpuLoad),
                CreateMetric("CPU.Temp",   "CPU 温度",   "CPU",  CpuTemp),
                CreateMetric("GPU.Load",   "GPU 使用率", "GPU",  GpuLoad),
                CreateMetric("GPU.Temp",   "GPU 温度",   "GPU",  GpuTemp),
                CreateMetric("GPU.VRAM",   "显存占用",   "显存", GpuVram),
                CreateMetric("MEM.Load",   "内存占用",   "内存", MemoryUsage),
                CreateMetric("NET.Up",     "上传速度",   "↑",    NetUpBytesPerSecond),
                CreateMetric("NET.Down",   "下载速度",   "↓",    NetDownBytesPerSecond),
            };
        }

        /// <summary>
        /// 构造一个预览指标：<see cref="MetricItem.Value"/> 与
        /// <see cref="MetricItem.DisplayValue"/> 一起给（与生产 <c>InitMetricValue</c> 一致）。
        /// </summary>
        public static MetricItem CreateMetric(string key, string label, string shortLabel, float value)
        {
            return new MetricItem
            {
                Key = key,
                Label = label,
                ShortLabel = shortLabel,
                Value = value,
                DisplayValue = value,
                // ★ BoundConfig 留空（预览没有会话上下文，见类注释）。
            };
        }

        /// <summary>
        /// 按行模式把指标配成列（与生产 <c>UIController.BuildColumnsCore</c> 同规则）：
        /// 单行一行一个指标，双行一行两个指标（上下各一）。
        /// </summary>
        public static List<Column> BuildColumns(IReadOnlyList<MetricItem> metrics, bool singleLine)
        {
            var cols = new List<Column>();
            if (metrics is null) return cols;

            int step = singleLine ? 1 : 2;
            for (int i = 0; i < metrics.Count; i += step)
            {
                var col = new Column { Top = metrics[i] };
                if (!singleLine && i + 1 < metrics.Count)
                {
                    col.Bottom = metrics[i + 1];
                }
                cols.Add(col);
            }
            return cols;
        }
    }

    /// <summary>
    /// 横向（横条）预览控件：与生产横排路径**同源**。
    /// <para>
    /// ★ 复用链：<c>ThemePreviewControl.CreatePreviewTheme(theme, dpiScale)</c>（布局副本，只缩放一次）
    ///   → <c>HorizontalLayout(theme, width, LayoutMode.Horizontal, new Settings(), null)</c>
    ///   → <c>HorizontalLayout.Build(cols)</c> → <c>HorizontalRenderer.Render(g, theme, cols, layout.PanelWidth, null)</c>。
    /// </para>
    /// <para>
    /// ★ 确定性：<c>HorizontalLayout</c> 的 <c>Settings</c> 一律<b>显式 new</b>，
    ///   <b>禁止</b> <c>Settings.Load()</c> 读用户磁盘配置；测量控件传 null ⇒
    ///   行高按 96 DPI 基线确定性测量。
    /// </para>
    /// <para>
    /// ★ 边界：<b>不建采集源</b>、<b>不读实时硬件</b>、<b>不写磁盘</b>、<b>不做 GPU</b>；
    ///   <see cref="SetTheme"/> 只缓存预览主题副本（字体只借用，绝不释放原主题字体）。
    /// </para>
    /// </summary>
    public class HorizontalPreviewControl : Panel
    {
        /// <summary>空列兜底时使用的初始宽度（物理像素）。</summary>
        private const int DefaultInitialWidth = 320;

        private Theme? _sourceTheme;
        private Theme? _previewTheme;

        private float _dpiScale = 1.0f;
        private bool _singleLine;

        private readonly List<MetricItem> _metrics = new();
        private readonly List<Column> _columns = new();

        private HorizontalLayout? _layout;
        private int _contentWidth;
        private int _contentHeight;

        private Bitmap? _cachedBitmap;
        private Size _cachedSize;

        private bool _disposed;

        public HorizontalPreviewControl()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = SystemColors.Control;

            _metrics.AddRange(ThemePreviewScene.CreateMetrics());
        }

        /// <summary>预览主题副本（<c>SetTheme</c> 之后可用；与源主题是不同对象）。</summary>
        public Theme? PreviewTheme => _previewTheme;

        /// <summary>源主题（只读引用，预览从不修改它）。</summary>
        public Theme? SourceTheme => _sourceTheme;

        /// <summary>当前预览使用的 DPI 比例（设备 DPI / 96）。</summary>
        public float DpiScale => _dpiScale;

        /// <summary>
        /// 单行 / 双行显示（对应生产的 <c>HorizontalSingleLine</c>）。
        /// <para>切换后立即重建列（配对规则随行模式变化）并重排布局。</para>
        /// </summary>
        public bool SingleLine
        {
            get => _singleLine;
            set
            {
                if (_singleLine == value) return;
                _singleLine = value;
                Rebuild();
                Invalidate();
            }
        }

        /// <summary>预览内容宽度（物理像素，与 <c>HorizontalLayout.PanelWidth</c> 一致）。</summary>
        public int ContentWidth => _contentWidth;

        /// <summary>预览内容高度（物理像素；空列时由 <c>HorizontalLayout.MinPanelHeight</c> 兜底）。</summary>
        public int ContentHeight => _contentHeight;

        /// <summary>空态最小高度（<c>HorizontalLayout.MinPanelHeight</c>；无布局时为 0）。</summary>
        public int MinPanelHeight => _layout?.MinPanelHeight ?? 0;

        /// <summary>当前参与渲染的列集合（只读）。</summary>
        public IReadOnlyList<Column> PreviewColumns => _columns;

        /// <summary>当前模拟场景的指标集合（只读）。</summary>
        public IReadOnlyList<MetricItem> PreviewMetrics => _metrics;

        /// <summary>
        /// 载入主题：<b>只缓存预览主题副本</b>（布局独立副本、字体只借用），
        /// 不做 GPU、不建采集源、不写磁盘。
        /// </summary>
        public void SetTheme(Theme theme)
        {
            if (theme is null) throw new ArgumentNullException(nameof(theme));

            _sourceTheme = theme;
            _dpiScale = GetDpiScale();

            // ★ 布局副本只缩放一次（CreatePreviewCopy 内部保证），颜色/阈值只读借用。
            _previewTheme = ThemePreviewControl.CreatePreviewTheme(theme, _dpiScale);

            Rebuild();
            Invalidate();
        }

        /// <summary>
        /// 替换模拟场景的指标集合（默认是固定模拟场景）。
        /// <para>用于预览其它布局（例如空列表、超长标签）；列配对随 <see cref="SingleLine"/> 变化。</para>
        /// </summary>
        public void SetMetrics(IEnumerable<MetricItem> metrics)
        {
            _metrics.Clear();
            if (metrics != null)
            {
                foreach (var m in metrics)
                {
                    if (m != null) _metrics.Add(m);
                }
            }
            Rebuild();
            Invalidate();
        }

        /// <summary>
        /// 把预览渲染到指定画布。
        /// <para>★ 画布尺寸应等于 <see cref="ContentWidth"/> × <see cref="ContentHeight"/>：
        ///   渲染器按可见裁剪区高度铺底板，与生产"客户区尺寸 = 布局尺寸"的口径一致。</para>
        /// </summary>
        public void RenderPreview(Graphics g)
        {
            if (g is null) throw new ArgumentNullException(nameof(g));

            Theme? theme = _previewTheme;
            HorizontalLayout? layout = _layout;
            if (theme is null || layout is null) return;

            // ★ 与生产同源：appearance = null ⇒ 走旧路径（无外观层），逐像素等同旧横排。
            HorizontalRenderer.Render(g, theme, _columns, layout.PanelWidth, appearance: null);
        }

        /// <summary>重建列与布局（主题 / 指标 / 行模式变化后调用）。</summary>
        private void Rebuild()
        {
            _columns.Clear();
            _columns.AddRange(ThemePreviewScene.BuildColumns(_metrics, _singleLine));

            Theme? theme = _previewTheme;
            if (theme is null)
            {
                _layout = null;
                _contentWidth = 0;
                _contentHeight = 0;
                return;
            }

            // ★ 显式 new Settings()：绝不 Settings.Load()（不读用户磁盘配置）。
            var settings = new Settings { HorizontalSingleLine = _singleLine };

            // ★ 测量控件传 null ⇒ 无显示上下文 ⇒ 行高按 96 DPI 基线确定性测量。
            var layout = new HorizontalLayout(theme, InitialWidth, LayoutMode.Horizontal, settings, null);

            int height = layout.Build(_columns);
            // ★ 空列 / 失败兜底：列集合为空时 Build 返回 MinPanelHeight，这里再兜一层。
            height = Math.Max(height, layout.MinPanelHeight);

            _layout = layout;
            _contentWidth = Math.Max(1, layout.PanelWidth);
            _contentHeight = Math.Max(1, height);
        }

        private int InitialWidth => Math.Max(1, Width > 0 ? Width : DefaultInitialWidth);

        private float GetDpiScale()
        {
            try
            {
                int dpi = DeviceDpi;
                if (dpi > 0) return dpi / 96f;
            }
            catch
            {
                // 句柄尚未创建时 DeviceDpi 可能不可用，退回基线 DPI。
            }
            return 1.0f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (_disposed || _previewTheme is null || _layout is null) return;

            try
            {
                var required = new Size(_contentWidth, _contentHeight);
                if (_cachedBitmap is null || _cachedSize != required)
                {
                    _cachedBitmap?.Dispose();
                    _cachedBitmap = new Bitmap(required.Width, required.Height, PixelFormat.Format32bppArgb);
                    _cachedSize = required;
                }

                using (Graphics gBmp = Graphics.FromImage(_cachedBitmap))
                {
                    gBmp.Clear(Color.Transparent);
                    RenderPreview(gBmp);
                }

                e.Graphics.DrawImageUnscaled(_cachedBitmap, Padding.Left, Padding.Top);
            }
            catch (Exception ex)
            {
                e.Graphics.DrawString(
                    $"Preview Error: {ex.Message}",
                    this.Font,
                    Brushes.Red,
                    new PointF(4f, 4f));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _cachedBitmap?.Dispose();
                _cachedBitmap = null;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 任务栏预览控件：与生产任务栏**同源**。
    /// <para>
    /// ★ 复用链：<c>HorizontalLayout(theme, width, LayoutMode.Taskbar, new Settings())</c>
    ///   → <c>Build(cols, taskbarHeight)</c> → <c>TaskbarRenderer.TaskbarStyleContext</c>
    ///   → <c>TaskbarRenderer.Render(g, cols, light, _styleContext)</c>。
    /// </para>
    /// <para>
    /// ★ <b>事实边界（不得夸大）</b>：任务栏的**颜色由设置驱动</b>（<c>Settings</c>，
    ///   默认标签灰黑 / 正常淡绿 / 警告金黄 / 严重橙红），**不随主题变化**；主题只影响
    ///   <b>布局</b>（列配对、内边距、行模式）。见 <see cref="ColorSourceNotice"/>。
    /// </para>
    /// <para>
    /// ★ 隔离性：渲染使用独立 <c>TaskbarRenderer.TaskbarStyleContext</c>，
    ///   严禁写入生产静态样式，彻底杜绝串扰生产任务栏。
    /// </para>
    /// </summary>
    public class TaskbarPreviewControl : Panel
    {
        /// <summary>
        /// 颜色来源的**事实标注**：任务栏颜色由设置驱动、布局随主题。
        /// <para>★ 严禁在任何文案里宣称"任务栏颜色随主题变化"（与生产实现不符）。</para>
        /// </summary>
        public const string ColorSourceNotice =
            "任务栏颜色由设置驱动（Settings），布局随主题（Layout.Padding / 行模式）；颜色不随主题变化。";

        /// <summary>任务栏参考高度（物理像素；与生产 TaskbarForm 的默认任务栏高度一致）。</summary>
        public const int ReferenceTaskbarHeight = 32;

        private Theme? _sourceTheme;
        private Theme? _previewTheme;

        private float _dpiScale = 1.0f;
        private bool _singleLine;
        private bool _lightMode;

        private readonly List<MetricItem> _metrics = new();
        private readonly List<Column> _columns = new();

        /// <summary>本控件自持的设置实例（<b>显式 new</b>，绝不 Settings.Load()）。</summary>
        private readonly Settings _settings = new();

        /// <summary>自持的独立任务栏样式上下文（隔离生产静态字段，谁创建谁释放）。</summary>
        private TaskbarRenderer.TaskbarStyleContext? _styleContext;

        private HorizontalLayout? _layout;
        private int _contentWidth;
        private int _contentHeight;

        private Bitmap? _cachedBitmap;
        private Size _cachedSize;

        private bool _disposed;

        public TaskbarPreviewControl()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = SystemColors.Control;

            _metrics.AddRange(ThemePreviewScene.CreateMetrics());
            UpdateStyleContext();
        }

        /// <summary>
        /// 更新任务栏独立样式上下文（安全释放旧上下文，按当前设置创建新上下文）。
        /// </summary>
        public void UpdateStyleContext()
        {
            _styleContext?.Dispose();
            _styleContext = new TaskbarRenderer.TaskbarStyleContext(_settings);
        }

        /// <summary>
        /// 应用自定义设置并更新样式上下文。
        /// </summary>
        public void ApplySettings(Settings settings)
        {
            if (settings is null) throw new ArgumentNullException(nameof(settings));
            _settings.TaskbarCustomStyle = settings.TaskbarCustomStyle;
            _settings.TaskbarColorLabel = settings.TaskbarColorLabel;
            _settings.TaskbarColorSafe = settings.TaskbarColorSafe;
            _settings.TaskbarColorWarn = settings.TaskbarColorWarn;
            _settings.TaskbarColorCrit = settings.TaskbarColorCrit;
            _settings.TaskbarColorBg = settings.TaskbarColorBg;
            _settings.TaskbarCustomLayout = settings.TaskbarCustomLayout;
            _settings.TaskbarFontFamily = settings.TaskbarFontFamily;
            _settings.TaskbarFontSize = settings.TaskbarFontSize;
            _settings.TaskbarFontBold = settings.TaskbarFontBold;
            _settings.TaskbarPresetStyle = settings.TaskbarPresetStyle;
            _settings.TaskbarItemSpacing = settings.TaskbarItemSpacing;
            _settings.TaskbarInnerSpacing = settings.TaskbarInnerSpacing;
            _settings.TaskbarVerticalPadding = settings.TaskbarVerticalPadding;
            _settings.TaskbarSingleLine = settings.TaskbarSingleLine;
            _singleLine = settings.TaskbarSingleLine;

            UpdateStyleContext();
            Rebuild();
            Invalidate();
        }

        /// <summary>预览主题副本（<c>SetTheme</c> 之后可用；与源主题是不同对象）。</summary>
        public Theme? PreviewTheme => _previewTheme;

        /// <summary>源主题（只读引用，预览从不修改它）。</summary>
        public Theme? SourceTheme => _sourceTheme;

        /// <summary>当前预览使用的 DPI 比例（设备 DPI / 96）。</summary>
        public float DpiScale => _dpiScale;

        /// <summary>
        /// 浅色 / 深色任务栏（对应生产的 <c>LastIsLightTheme</c>）。
        /// <para>★ 只影响任务栏渲染器的明暗配色分支，<b>与主题无关</b>。</para>
        /// </summary>
        public bool LightMode
        {
            get => _lightMode;
            set
            {
                if (_lightMode == value) return;
                _lightMode = value;
                Invalidate();
            }
        }

        /// <summary>单行 / 双行显示（对应生产的 <c>TaskbarSingleLine</c>）。</summary>
        public bool SingleLine
        {
            get => _singleLine;
            set
            {
                if (_singleLine == value) return;
                _singleLine = value;
                Rebuild();
                Invalidate();
            }
        }

        /// <summary>颜色来源的事实标注（同 <see cref="ColorSourceNotice"/>）。</summary>
        public string ColorSourceNote => ColorSourceNotice;

        /// <summary>预览内容宽度（物理像素）。</summary>
        public int ContentWidth => _contentWidth;

        /// <summary>预览内容高度（物理像素）。</summary>
        public int ContentHeight => _contentHeight;

        /// <summary>空态最小高度（<c>HorizontalLayout.MinPanelHeight</c>；无布局时为 0）。</summary>
        public int MinPanelHeight => _layout?.MinPanelHeight ?? 0;

        /// <summary>当前参与渲染的列集合（只读）。</summary>
        public IReadOnlyList<Column> PreviewColumns => _columns;

        /// <summary>当前模拟场景的指标集合（只读）。</summary>
        public IReadOnlyList<MetricItem> PreviewMetrics => _metrics;

        /// <summary>
        /// 载入主题：<b>只缓存预览主题副本</b>（布局独立副本、字体只借用），
        /// 不做 GPU、不建采集源、不写磁盘。
        /// </summary>
        public void SetTheme(Theme theme)
        {
            if (theme is null) throw new ArgumentNullException(nameof(theme));

            _sourceTheme = theme;
            _dpiScale = GetDpiScale();

            _previewTheme = ThemePreviewControl.CreatePreviewTheme(theme, _dpiScale);

            // 颜色来源标注同步到无障碍描述（供工作室界面直接引用，避免文案夸大）。
            AccessibleDescription = ColorSourceNotice;

            Rebuild();
            Invalidate();
        }

        /// <summary>
        /// 替换模拟场景的指标集合（默认是固定模拟场景）。
        /// </summary>
        public void SetMetrics(IEnumerable<MetricItem> metrics)
        {
            _metrics.Clear();
            if (metrics != null)
            {
                foreach (var m in metrics)
                {
                    if (m != null) _metrics.Add(m);
                }
            }
            Rebuild();
            Invalidate();
        }

        /// <summary>
        /// 把任务栏预览渲染到指定画布。
        /// <para>★ 严格使用自持的 <see cref="TaskbarRenderer.TaskbarStyleContext"/> 进行隔离绘制，
        ///   绝不修改生产任务栏静态样式，不串扰生产任务栏静态样式。</para>
        /// </summary>
        public void RenderPreview(Graphics g)
        {
            if (g is null) throw new ArgumentNullException(nameof(g));

            HorizontalLayout? layout = _layout;
            if (layout is null) return;

            if (_styleContext is null)
            {
                UpdateStyleContext();
            }

            TaskbarRenderer.Render(g, _columns, _lightMode, _styleContext);
        }

        /// <summary>重建列与布局。</summary>
        private void Rebuild()
        {
            _columns.Clear();
            _columns.AddRange(ThemePreviewScene.BuildColumns(_metrics, _singleLine));

            Theme? theme = _previewTheme;
            if (theme is null)
            {
                _layout = null;
                _contentWidth = 0;
                _contentHeight = 0;
                return;
            }

            // ★ 显式 new Settings()：绝不 Settings.Load()（不读用户磁盘配置）。
            _settings.TaskbarSingleLine = _singleLine;

            // ★ 任务栏模式：列宽 / 行高由任务栏样式（Settings）决定，主题只贡献内边距。
            var layout = new HorizontalLayout(theme, InitialWidth, LayoutMode.Taskbar, _settings);

            int height = layout.Build(_columns, ReferenceTaskbarHeight);
            height = Math.Max(height, layout.MinPanelHeight);

            _layout = layout;
            _contentWidth = Math.Max(1, layout.PanelWidth);
            _contentHeight = Math.Max(1, height);
        }

        private int InitialWidth => Math.Max(1, Width > 0 ? Width : 320);

        private float GetDpiScale()
        {
            try
            {
                int dpi = DeviceDpi;
                if (dpi > 0) return dpi / 96f;
            }
            catch
            {
                // 句柄尚未创建时 DeviceDpi 可能不可用，退回基线 DPI。
            }
            return 1.0f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (_disposed || _previewTheme is null || _layout is null) return;

            try
            {
                var required = new Size(_contentWidth, _contentHeight);
                if (_cachedBitmap is null || _cachedSize != required)
                {
                    _cachedBitmap?.Dispose();
                    _cachedBitmap = new Bitmap(required.Width, required.Height, PixelFormat.Format32bppArgb);
                    _cachedSize = required;
                }

                using (Graphics gBmp = Graphics.FromImage(_cachedBitmap))
                {
                    // ★ 任务栏渲染器只画文字、不铺底板 ⇒ 离屏画布先铺一层不透明底色，
                    //   证据图才有可读内容（GDI 文字在透明底上会被当成"无底"渲染）。
                    using (var bg = new SolidBrush(PreviewBackgroundColor))
                    {
                        gBmp.FillRectangle(bg, 0, 0, required.Width, required.Height);
                    }
                    RenderPreview(gBmp);
                }

                e.Graphics.DrawImageUnscaled(_cachedBitmap, Padding.Left, Padding.Top);
            }
            catch (Exception ex)
            {
                e.Graphics.DrawString(
                    $"Preview Error: {ex.Message}",
                    this.Font,
                    Brushes.Red,
                    new PointF(4f, 4f));
            }
        }

        /// <summary>
        /// 任务栏预览的底色：取自本控件自持的设置实例（<c>TaskbarColorBg</c>），
        /// 不读用户磁盘配置。
        /// </summary>
        public Color PreviewBackgroundColor
        {
            get
            {
                try
                {
                    return ColorTranslator.FromHtml(_settings.TaskbarColorBg);
                }
                catch
                {
                    return Color.LightGray;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _cachedBitmap?.Dispose();
                _cachedBitmap = null;
                _styleContext?.Dispose();
                _styleContext = null;
            }
            base.Dispose(disposing);
        }
    }
}
