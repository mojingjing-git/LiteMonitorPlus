using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace LiteMonitor
{
    /// <summary>
    /// 显示 DPI 来源：提供"当前用于布局测量的设备 DPI"。
    /// <para>★ 句柄不存在 / 正在销毁 ⇒ <see cref="TryGetDpi"/> 返回 <c>false</c>，
    ///   调用方**推迟测量**（既不新建 HWND，也不碰任何可能已失效的 GDI 对象）。</para>
    /// </summary>
    internal interface IDisplayDpiSource
    {
        /// <summary>取当前设备 DPI；不可用时返回 <c>false</c>（<paramref name="dpi"/> 置 0）。</summary>
        bool TryGetDpi(out int dpi);
    }

    /// <summary>
    /// 固定 DPI 来源（无控件场景 / 测试注入）。
    /// </summary>
    internal sealed class FixedDisplayDpiSource : IDisplayDpiSource
    {
        private readonly int _dpi;

        internal FixedDisplayDpiSource(int dpi)
        {
            _dpi = dpi;
        }

        public bool TryGetDpi(out int dpi)
        {
            dpi = _dpi;
            return _dpi > 0;
        }
    }

    /// <summary>
    /// 基于 <see cref="Control"/> 的 DPI 来源。
    /// <para>★ **绝不**为了取 DPI 而强制创建句柄 —— 句柄不存在就报"不可用"，
    ///   由调用方推迟测量。这同时满足"关闭过程不新建 HWND"。</para>
    /// </summary>
    internal sealed class ControlDisplayDpiSource : IDisplayDpiSource
    {
        private readonly Control? _control;

        internal ControlDisplayDpiSource(Control? control)
        {
            _control = control;
        }

        public bool TryGetDpi(out int dpi)
        {
            dpi = 0;
            Control? control = _control;
            if (control is null) return false;

            try
            {
                if (control.IsDisposed) return false;
                // ★ 句柄不存在 ⇒ 推迟测量（不调用 CreateGraphics / DeviceDpi 去"顺带"建句柄）。
                if (!control.IsHandleCreated) return false;
                dpi = control.DeviceDpi;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                // 句柄在"检查存在"与"读 DPI"之间被销毁（关窗/重建句柄）。
                return false;
            }
            catch (Win32Exception)
            {
                return false;
            }

            return dpi > 0;
        }
    }

    /// <summary>
    /// 字体度量缓存键：字体族 + 字号 + 单位 + 样式 + DPI + 主题代次。
    /// <para>★ 换肤 / 重缩放 / 跨屏（DPI 变化）都会自然落到**不同的键**上，
    ///   因此绝不会命中过期度量；键里**不持有** <see cref="Font"/> 实例，
    ///   避免把已释放字体留在缓存里。</para>
    /// </summary>
    internal readonly struct FontMetricsKey : IEquatable<FontMetricsKey>
    {
        internal readonly string Family;
        internal readonly float Size;
        internal readonly GraphicsUnit Unit;
        internal readonly FontStyle Style;
        internal readonly int Dpi;
        internal readonly int ThemeGeneration;

        internal FontMetricsKey(string family, float size, GraphicsUnit unit, FontStyle style, int dpi, int themeGeneration)
        {
            Family = family;
            Size = size;
            Unit = unit;
            Style = style;
            Dpi = dpi;
            ThemeGeneration = themeGeneration;
        }

        public bool Equals(FontMetricsKey other)
        {
            return Dpi == other.Dpi
                && ThemeGeneration == other.ThemeGeneration
                && Unit == other.Unit
                && Style == other.Style
                && Size.Equals(other.Size)
                && string.Equals(Family, other.Family, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => obj is FontMetricsKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Family, Size, Unit, (int)Style, Dpi, ThemeGeneration);

        public override string ToString() =>
            $"{Family}/{Size}{Unit}/{Style}@{Dpi}#g{ThemeGeneration}";
    }

    /// <summary>
    /// 一次**受控**字体度量的产物。
    /// <para>★ <see cref="IsMeasured"/> 为 <c>false</c> 表示"句柄不存在 / 正在销毁 ⇒ 本次未测量"，
    ///   调用方应**推迟测量**并保留最小布局。</para>
    /// </summary>
    internal readonly struct FontMetrics
    {
        internal bool IsMeasured { get; }
        internal int Dpi { get; }
        internal int ItemHeight { get; }
        internal int ValueHeight { get; }

        /// <summary>行高（像素）：item / value 两个字体度量里的较大者。</summary>
        internal int RowHeight => Math.Max(ItemHeight, ValueHeight);

        internal FontMetrics(bool isMeasured, int dpi, int itemHeight, int valueHeight)
        {
            IsMeasured = isMeasured;
            Dpi = dpi;
            ItemHeight = itemHeight;
            ValueHeight = valueHeight;
        }

        internal static FontMetrics Unmeasured => new FontMetrics(false, 0, 0, 0);
    }

    /// <summary>
    /// 字体度量缓存 —— **受控布局阶段**唯一的字体度量入口。
    /// <para>
    /// ★★ 为什么不用 <c>Font.GetHeight(Graphics)</c>：
    ///   该重载把 <see cref="Graphics"/> 交给 GDI+，当 Graphics 已释放时它抛
    ///   <see cref="ArgumentException"/>（ParamName = "graphics"）；
    ///   而 <c>Font.Height</c> / <c>Font.GetHeight(float)</c> 在**字体已被释放**时抛
    ///   <see cref="ArgumentException"/>（"Parameter is not valid."）—— 这正是 #574 的崩溃形态。
    ///   这里统一改用 <c>Font.GetHeight(float dpi)</c>：**纯 DPI 参数、不需要任何 GDI 句柄**，
    ///   且在同一 DPI 下与 <c>GetHeight(Graphics)</c> **完全等价**（实测逐位相等）。
    /// </para>
    /// <para>
    /// ★★ DPI 语义：<c>GetHeight(Graphics)</c> 取的是 <c>graphics.DpiY</c>；
    ///   <c>GetHeight(float)</c> 直接吃 DPI 数值。主题字体是 **Point 单位**
    ///   （<c>new Font(family, size)</c> 的默认单位），GDI+/GDI 都会按设备 DPI 放大，
    ///   所以必须喂**设备 DPI**；喂 96 DPI 的 <c>Font.Height</c> 会在 150%/200% 下**裁切行高**。
    /// </para>
    /// </summary>
    internal sealed class FontMetricsCache
    {
        /// <summary>基线 DPI（没有任何显示上下文时的兜底测量分辨率）。</summary>
        internal const int BaselineDpi = 96;

        /// <summary>诊断记录上限（有界：避免布局测量在高频路径上刷屏）。</summary>
        internal const int MaxDiagnostics = 20;

        private readonly Dictionary<FontMetricsKey, int> _heights = new Dictionary<FontMetricsKey, int>();
        private readonly HashSet<FontMetricsKey> _failed = new HashSet<FontMetricsKey>();
        private readonly List<string> _diagnostics = new List<string>();

        /// <summary>主题代次：换肤 / 重缩放后自增，令旧度量立即失效。</summary>
        internal int ThemeGeneration { get; private set; }

        /// <summary>缓存条目数（测试可观测）。</summary>
        internal int CacheCount => _heights.Count;

        /// <summary>已记录的诊断（测试可观测）。</summary>
        internal IReadOnlyList<string> Diagnostics => _diagnostics;

        /// <summary>主题代次自增并清空缓存。</summary>
        internal void AdvanceThemeGeneration()
        {
            ThemeGeneration++;
            _heights.Clear();
            _failed.Clear();
        }

        /// <summary>
        /// 受控测量：一次拿到 item / value 两个字体在给定 DPI 下的行高。
        /// </summary>
        internal FontMetrics Measure(Font? item, Font? value, int dpi)
        {
            if (dpi <= 0) dpi = BaselineDpi;
            return new FontMetrics(true, dpi, MeasureFont(item, dpi), MeasureFont(value, dpi));
        }

        /// <summary>
        /// 受控测量（带 DPI 来源）：来源不可用（句柄不存在 / 销毁中）⇒ **推迟测量**
        /// （返回 <c>false</c>，<paramref name="metrics"/> 为未测量态）。
        /// </summary>
        internal bool TryMeasure(Font? item, Font? value, IDisplayDpiSource? source, out FontMetrics metrics)
        {
            if (source is null || !source.TryGetDpi(out int dpi) || dpi <= 0)
            {
                metrics = FontMetrics.Unmeasured;
                return false;
            }

            metrics = Measure(item, value, dpi);
            return true;
        }

        /// <summary>
        /// 单个字体的行高（像素），带缓存。
        /// <para>★ 字体已被释放时 GDI+ 抛 <see cref="ArgumentException"/>
        ///   （"Parameter is not valid."）—— 这里**不让它冒泡**：记录诊断并返回 0，
        ///   由调用方保留最小布局。真正的修复是"不测量已释放字体"（见调用方的主题代次失效）。</para>
        /// </summary>
        internal int MeasureFont(Font? font, int dpi)
        {
            if (font is null) return 0;
            if (dpi <= 0) dpi = BaselineDpi;

            FontMetricsKey key;
            try
            {
                key = CreateKey(font, dpi);
            }
            catch (ArgumentException ex)
            {
                // 已释放字体：连"读字体描述"都会抛 ArgumentException("Parameter is not valid.")。
                RecordDiagnostic("FontMetricsCache.CreateKey", ex);
                return 0;
            }

            if (_heights.TryGetValue(key, out int cached)) return cached;

            // ★ 负缓存：已释放字体的度量**每次都会抛**，而布局测量在高频路径上重复调用。
            //   记一次失败 ⇒ 本主题代次内不再重试（有界，不刷屏、不反复抛）。
            if (_failed.Contains(key)) return 0;

            int height;
            try
            {
                // ★ Graphics 无关的度量：不需要任何 GDI 句柄 ⇒ 不会碰到"已释放 Graphics"。
                height = (int)Math.Ceiling(font.GetHeight(dpi));
            }
            catch (ArgumentException ex)
            {
                _failed.Add(key);
                RecordDiagnostic("FontMetricsCache.MeasureFont", ex);
                return 0;
            }

            _heights[key] = height;
            return height;
        }

        private FontMetricsKey CreateKey(Font font, int dpi)
        {
            return new FontMetricsKey(
                font.FontFamily.Name,
                font.Size,
                font.Unit,
                font.Style,
                dpi,
                ThemeGeneration);
        }

        /// <summary>
        /// 记录一条**诊断**（应用层只补诊断，不冒充原生边界修复）。
        /// <para>★ 有界：最多 <see cref="MaxDiagnostics"/> 条，超出后只计数不再写内容。</para>
        /// </summary>
        internal void RecordDiagnostic(string source, Exception? ex = null)
        {
            if (_diagnostics.Count >= MaxDiagnostics) return;

            string message = ex is null
                ? source
                : $"{source}: {ex.GetType().Name}: {ex.Message}";
            _diagnostics.Add(message);

            Debug.WriteLine($"[FontMetrics] {message}");
            Console.Error.WriteLine($"[FontMetrics] {message}");
        }
    }
}
