using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    // ========================================================================
    // 颜色文本 / alpha（透明度）语义
    // ========================================================================

    /// <summary>
    /// 颜色文本与 alpha（透明度）语义（外观改造计划 v4 §3.1）。
    ///
    /// ★ 冻结语义：
    /// 1. <c>rgba(r,g,b,a)</c> 的 r/g/b 是 <b>0–255 整数</b>，<b>a 统一为 0–1</b>（1 = 不透明）；
    ///    字节 alpha 继续用已有的 <c>#AARRGGBB</c>。
    /// 2. alpha×255 按 <see cref="MidpointRounding.AwayFromZero"/> 舍入（0.5 → 128）。
    /// 3. 越界 / NaN / Infinity 的 alpha 一律<b>拒绝</b>：不写出半合法的颜色，回退到原值。
    /// 4. 解析与格式化一律使用 <see cref="CultureInfo.InvariantCulture"/>，不受系统区域设置影响。
    /// 5. 只认识的写法：<c>#RRGGBB</c>、<c>#AARRGGBB</c>、<c>rgba(r,g,b,a)</c>、系统颜色名。
    /// </summary>
    public static class AppearanceColorText
    {
        /// <summary>alpha 下限（0 = 全透明）。</summary>
        public const double MinAlpha = 0.0;

        /// <summary>alpha 上限（1 = 不透明）。</summary>
        public const double MaxAlpha = 1.0;

        /// <summary>
        /// 解析颜色文本 → (r, g, b, alpha)。
        /// </summary>
        /// <param name="text">颜色文本（可空）。</param>
        /// <param name="r">红色分量 0–255。</param>
        /// <param name="g">绿色分量 0–255。</param>
        /// <param name="b">蓝色分量 0–255。</param>
        /// <param name="alpha">透明度 0–1（1 = 不透明）。</param>
        /// <returns>能否解析。</returns>
        public static bool TryParse(string? text, out int r, out int g, out int b, out double alpha)
        {
            r = 0; g = 0; b = 0; alpha = 1.0;

            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text!.Trim();

            if (s.StartsWith('#'))
            {
                string hex = s.Substring(1);
                if (hex.Length == 6)
                {
                    if (!TryHexByte(hex, 0, out r)) return false;
                    if (!TryHexByte(hex, 2, out g)) return false;
                    if (!TryHexByte(hex, 4, out b)) return false;
                    alpha = 1.0;
                    return true;
                }
                if (hex.Length == 8)
                {
                    int a;
                    if (!TryHexByte(hex, 0, out a)) return false;
                    if (!TryHexByte(hex, 2, out r)) return false;
                    if (!TryHexByte(hex, 4, out g)) return false;
                    if (!TryHexByte(hex, 6, out b)) return false;
                    alpha = a / 255.0;
                    return true;
                }
                return false;
            }

            if (s.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase)
                && s.EndsWith(")", StringComparison.Ordinal))
            {
                int open = s.IndexOf('(');
                int close = s.LastIndexOf(')');
                if (open < 0 || close <= open) return false;

                string[] parts = s.Substring(open + 1, close - open - 1).Split(',');
                if (parts.Length != 4) return false;

                if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return false;
                if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out g)) return false;
                if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out b)) return false;
                if (r < 0 || r > 255 || g < 0 || g > 255 || b < 0 || b > 255) return false;

                if (!double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out alpha)) return false;
                if (double.IsNaN(alpha) || double.IsInfinity(alpha)) return false;
                if (alpha < MinAlpha || alpha > MaxAlpha) return false;
                return true;
            }

            // 颜色名：与 ThemeManager.ParseColor 走的 Color.FromName 行为保持一致
            Color named = Color.FromName(s);
            if (!named.IsNamedColor) return false;

            r = named.R; g = named.G; b = named.B;
            alpha = named.A / 255.0;
            return true;
        }

        /// <summary>
        /// 读取颜色文本当前的 alpha（0–1）。无法解析时返回 false。
        /// </summary>
        public static bool TryGetAlpha(string? text, out double alpha)
        {
            return TryParse(text, out _, out _, out _, out alpha);
        }

        /// <summary>
        /// 在保持 RGB 不变的前提下改写 alpha。
        ///
        /// ★ 拒绝语义：alpha 越界 / NaN / Infinity，或原颜色文本无法解析时，
        /// 返回 false 且 <b>不产生任何新文本</b>（调用方保持原值，即"拒绝并回退"）。
        /// </summary>
        /// <param name="currentText">当前颜色文本。</param>
        /// <param name="alpha">目标 alpha（0–1）。</param>
        /// <param name="newText">改写后的颜色文本（失败时为原文本）。</param>
        /// <returns>是否成功改写。</returns>
        public static bool TrySetAlpha(string? currentText, double alpha, out string newText)
        {
            newText = currentText ?? string.Empty;

            if (double.IsNaN(alpha) || double.IsInfinity(alpha)) return false;
            if (alpha < MinAlpha || alpha > MaxAlpha) return false;
            if (!TryParse(currentText, out int r, out int g, out int b, out _)) return false;

            newText = Format(r, g, b, alpha);
            return true;
        }

        /// <summary>
        /// 生成颜色文本：alpha = 1 用 <c>#RRGGBB</c>（与现有编辑器一致），
        /// alpha &lt; 1 用 <c>rgba(r,g,b,a)</c>（a 为 0–1 的不变文化小数）。
        /// </summary>
        public static string Format(int r, int g, int b, double alpha)
        {
            if (r < 0) r = 0; if (r > 255) r = 255;
            if (g < 0) g = 0; if (g > 255) g = 255;
            if (b < 0) b = 0; if (b > 255) b = 255;

            if (alpha >= MaxAlpha)
            {
                return "#" + r.ToString("X2", CultureInfo.InvariantCulture)
                          + g.ToString("X2", CultureInfo.InvariantCulture)
                          + b.ToString("X2", CultureInfo.InvariantCulture);
            }

            if (alpha < MinAlpha) alpha = MinAlpha;
            return string.Format(CultureInfo.InvariantCulture, "rgba({0},{1},{2},{3})",
                r, g, b, alpha.ToString("0.###", CultureInfo.InvariantCulture));
        }

        /// <summary>alpha（0–1）→ 字节（0–255），AwayFromZero 舍入（0.5 → 128）。</summary>
        public static int ToAlphaByte(double alpha)
        {
            if (double.IsNaN(alpha) || double.IsInfinity(alpha)) return 0;
            if (alpha < 0) alpha = 0;
            if (alpha > 1) alpha = 1;
            return (int)Math.Round(alpha * 255d, MidpointRounding.AwayFromZero);
        }

        private static bool TryHexByte(string hex, int offset, out int value)
        {
            value = 0;
            if (offset + 2 > hex.Length) return false;
            return int.TryParse(hex.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
    }

    // ========================================================================
    // 主题配置图（深拷贝 / 就地恢复）
    // ========================================================================

    /// <summary>
    /// 主题配置图的深拷贝与就地恢复。
    ///
    /// ★ 为什么需要它：
    /// - "取消"要求主题回到进入编辑器前的状态 → 需要一份进入前的快照；
    /// - 预览需要"独立副本"，避免改动原主题。
    ///
    /// ★ 合同：
    /// 1. 只拷贝<b>配置属性</b>（Layout / Font / Thresholds / Color / Backdrop / Name / Version /
    ///    各层扩展数据），<b>不碰运行期字体字段</b>（FontTitle 等）——预览与快照都是"借用"原字体。
    /// 2. 用反射逐属性拷贝，<b>不手写字段清单</b>：以后新增配置属性不会漏拷（v4 §7.1）。
    /// 3. 扩展数据（未知键）整份复制，键与值都不丢。
    /// </summary>
    public static class AppearanceThemeConfig
    {
        /// <summary>
        /// 深拷贝主题的配置图（不含运行期字体）。
        /// </summary>
        public static Theme CloneConfig(Theme source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            return new Theme
            {
                Name = source.Name,
                Version = source.Version,
                Layout = source.Layout != null ? (LayoutConfig)CloneObject(source.Layout) : new LayoutConfig(),
                Font = source.Font != null ? (FontConfig)CloneObject(source.Font) : new FontConfig(),
                Thresholds = source.Thresholds != null ? (ThresholdConfig)CloneObject(source.Thresholds) : new ThresholdConfig(),
                Color = source.Color != null ? (ColorConfig)CloneObject(source.Color) : new ColorConfig(),
                Backdrop = source.Backdrop != null ? (BackdropConfig)CloneObject(source.Backdrop) : null,
                ExtensionData = CloneDictionary(source.ExtensionData)
            };
        }

        /// <summary>
        /// 就地把快照的配置图写回目标主题（对象引用保持不变）。
        /// 用于"取消"：编辑器里正在编辑的那个 Theme 对象恢复原状。
        /// </summary>
        public static void RestoreConfig(Theme target, Theme snapshot)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            target.Name = snapshot.Name;
            target.Version = snapshot.Version;

            if (snapshot.Layout == null) target.Layout = new LayoutConfig();
            else if (target.Layout == null) target.Layout = (LayoutConfig)CloneObject(snapshot.Layout);
            else CopyInto(target.Layout, snapshot.Layout);

            if (snapshot.Font == null) target.Font = new FontConfig();
            else if (target.Font == null) target.Font = (FontConfig)CloneObject(snapshot.Font);
            else CopyInto(target.Font, snapshot.Font);

            if (snapshot.Thresholds == null) target.Thresholds = new ThresholdConfig();
            else if (target.Thresholds == null) target.Thresholds = (ThresholdConfig)CloneObject(snapshot.Thresholds);
            else CopyInto(target.Thresholds, snapshot.Thresholds);

            if (snapshot.Color == null) target.Color = new ColorConfig();
            else if (target.Color == null) target.Color = (ColorConfig)CloneObject(snapshot.Color);
            else CopyInto(target.Color, snapshot.Color);

            if (snapshot.Backdrop == null) target.Backdrop = null;
            else if (target.Backdrop == null) target.Backdrop = (BackdropConfig)CloneObject(snapshot.Backdrop);
            else CopyInto(target.Backdrop, snapshot.Backdrop);

            target.ExtensionData = CloneDictionary(snapshot.ExtensionData);
        }

        /// <summary>深拷贝颜色配置。</summary>
        public static ColorConfig CloneColor(ColorConfig source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return (ColorConfig)CloneObject(source);
        }

        // ------------------------------------------------------------
        // 反射实现
        // ------------------------------------------------------------

        private static object CloneObject(object source)
        {
            object clone = Activator.CreateInstance(source.GetType())!;
            CopyInto(clone, source);
            return clone;
        }

        /// <summary>把源对象的全部可写属性（逐属性深拷贝）写回目标对象。</summary>
        private static void CopyInto(object target, object source)
        {
            Type type = source.GetType();
            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.SetMethod == null || !p.SetMethod.IsPublic) continue;
                if (p.GetIndexParameters().Length > 0) continue;
                p.SetValue(target, CloneValue(p.GetValue(source)));
            }
        }

        private static object? CloneValue(object? value)
        {
            if (value == null) return null;

            Type t = value.GetType();
            if (value is string || t.IsPrimitive || t.IsEnum || t.IsValueType) return value;

            if (value is IDictionary dict)
            {
                var cloneDict = (IDictionary)Activator.CreateInstance(t)!;
                foreach (DictionaryEntry entry in dict)
                    cloneDict[entry.Key] = CloneValue(entry.Value);
                return cloneDict;
            }

            if (value is IList list)
            {
                var cloneList = (IList)Activator.CreateInstance(t)!;
                foreach (object? item in list)
                    cloneList.Add(CloneValue(item));
                return cloneList;
            }

            if (t.IsClass) return CloneObject(value);

            return value;
        }

        private static Dictionary<string, JsonElement> CloneDictionary(Dictionary<string, JsonElement>? source)
        {
            var clone = new Dictionary<string, JsonElement>();
            if (source == null) return clone;
            foreach (KeyValuePair<string, JsonElement> kv in source) clone[kv.Key] = kv.Value;
            return clone;
        }
    }

    // ========================================================================
    // alpha 编辑控件
    // ========================================================================

    /// <summary>
    /// alpha（透明度）输入控件：0–1 的小数输入框。
    ///
    /// ★ 只负责"输入与显示"，颜色文本的解析/格式化由
    /// <see cref="AppearanceColorText"/> 负责（拒绝非法 alpha 的逻辑集中在一处）。
    /// </summary>
    public sealed class AppearanceAlphaEditor : Panel
    {
        private readonly Label _caption;
        private readonly NumericUpDown _value;
        private readonly ToolTip _toolTip = new();
        private bool _binding;

        public AppearanceAlphaEditor()
        {
            Height = 26;
            Width = 120;

            _caption = new Label
            {
                Text = "α",
                AutoSize = false,
                Width = 18,
                Height = 22,
                Location = new Point(0, 3),
                TextAlign = ContentAlignment.MiddleCenter
            };

            _value = new NumericUpDown
            {
                Location = new Point(20, 0),
                Width = 86,
                DecimalPlaces = 3,
                Increment = 0.05m,
                // 范围放宽：避免"打开编辑器就把超范围的旧值夹紧并回写"
                // （归一化只作用于运行时副本，绝不回写用户 JSON，v4 §3.1）
                Minimum = -1000000m,
                Maximum = 1000000m
            };
            _value.ValueChanged += (_, __) =>
            {
                if (_binding) return;
                AlphaChanged?.Invoke(this, EventArgs.Empty);
            };

            Controls.Add(_caption);
            Controls.Add(_value);
        }

        /// <summary>用户改动了 alpha。</summary>
        public event EventHandler? AlphaChanged;

        /// <summary>当前 alpha 值（0–1 语义；控件本身允许越界，由上层校验）。</summary>
        public double Alpha
        {
            get => (double)_value.Value;
            set
            {
                _binding = true;
                try { _value.Value = (decimal)value; }
                finally { _binding = false; }
            }
        }

        /// <summary>当前颜色文本是否可解析（false = 该颜色写坏了，alpha 无法计算）。</summary>
        public bool HasValidColor { get; private set; }

        /// <summary>
        /// 同步显示某个颜色文本的 alpha（外部更新，不触发事件）。
        /// 文本无法解析时置灰并显示 0（用户需用颜色对话框重新选取）。
        /// </summary>
        public void SetColorText(string? text)
        {
            _binding = true;
            try
            {
                if (AppearanceColorText.TryGetAlpha(text, out double a))
                {
                    HasValidColor = true;
                    _value.Value = (decimal)a;
                    _caption.ForeColor = SystemColors.ControlText;
                    _caption.Text = "α";
                    _toolTip.SetToolTip(_value, null);
                }
                else
                {
                    HasValidColor = false;
                    _value.Value = 0m;
                    _caption.ForeColor = Color.Gray;
                    _caption.Text = "α?";
                    _toolTip.SetToolTip(_value, "颜色文本无法解析，alpha 暂不可用");
                }
            }
            finally
            {
                _binding = false;
            }
        }

        /// <summary>释放本控件自己创建的 ToolTip（颜色对话框等外部资源不在此释放）。</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _toolTip.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>组合项：显示文本 + 内部值（int 或可空 string）。</summary>
    internal sealed class AppearanceComboItem
    {
        public AppearanceComboItem(string text, int value)
        {
            Text = text;
            IntValue = value;
            StringValue = value.ToString(CultureInfo.InvariantCulture);
        }

        public AppearanceComboItem(string text, string? value)
        {
            Text = text;
            StringValue = value;
            IntValue = 0;
        }

        public string Text { get; }
        public int IntValue { get; }
        public string? StringValue { get; }

        public override string ToString() => Text;
    }

    // ========================================================================
    // 外观字段编辑面板
    // ========================================================================

    /// <summary>
    /// 外观字段编辑面板（外观改造计划 A10）。
    ///
    /// ★ 覆盖的字段（v4 §3.1）：
    /// - Layout.FrameStyle / FrameWidth / FrameLength / Scanline / Glow / Grid / BarStyle
    /// - Color.Border / Shadow / TextSecondary（可空，缺省不写出）
    /// - Backdrop（可空对象）：Mode / Opacity / Blur / Tint / GradientStart / GradientEnd /
    ///   GradientAngle / Noise / EdgeGlow / Fps
    ///
    /// ★ 合同：
    /// 1. <b>取值 / 写回</b>：控件值 → 主题（内存）由 <see cref="ApplyToTheme"/> 统一完成；
    ///    任何一次改动都会写回并触发 <see cref="ValueChanged"/>（编辑器据此刷新预览）。
    /// 2. <b>恢复缺省</b>：每一行都有"缺省"按钮；恢复缺省后写回的值与 v4 §3.1 的缺省一致
    ///    （7 个整数开关全为 0，Backdrop 整体缺省 = null，颜色类缺省 = null）。
    ///    由于模型层用了 <c>WhenWritingDefault</c> / <c>WhenWritingNull</c>，
    ///    恢复缺省在文件层面的表现是"这些键不再出现"，这正是正确行为。
    /// 3. <b>绝不写盘</b>：本面板只改内存中的主题对象，落盘只由编辑器的"保存"按钮触发。
    /// 4. <b>不归一化回写</b>：控件范围刻意放宽，主题里超出规范的值原样保留，
    ///    归一化只发生在运行时副本（ThemeAppearanceOptions）里。
    /// </summary>
    public sealed class AppearanceFields : Panel
    {
        // ---- 缺省键名（公开常量，便于测试与外部调用） ----
        public const string KeyFrameStyle = "frameStyle";
        public const string KeyFrameWidth = "frameWidth";
        public const string KeyFrameLength = "frameLength";
        public const string KeyScanline = "scanline";
        public const string KeyGlow = "glow";
        public const string KeyGrid = "grid";
        public const string KeyBarStyle = "barStyle";
        public const string KeyBackdropMode = "backdropMode";
        public const string KeyBackdropOpacity = "backdropOpacity";
        public const string KeyBackdropBlur = "backdropBlur";
        public const string KeyBackdropAngle = "backdropAngle";
        public const string KeyBackdropNoise = "backdropNoise";
        public const string KeyBackdropEdgeGlow = "backdropEdgeGlow";
        public const string KeyBackdropFps = "backdropFps";
        public const string KeyTint = "tint";
        public const string KeyGradientStart = "gradientStart";
        public const string KeyGradientEnd = "gradientEnd";
        public const string KeyBorder = "border";
        public const string KeyShadow = "shadow";
        public const string KeyTextSecondary = "textSecondary";

        // ---- 缺省值（v4 §3.1） ----
        public const int DefaultFrameStyle = 0;
        public const int DefaultFrameWidth = 0;
        public const int DefaultFrameLength = 0;
        public const int DefaultScanline = 0;
        public const int DefaultGlow = 0;
        public const int DefaultGrid = 0;
        public const int DefaultBarStyle = 0;
        public const double DefaultBackdropOpacity = 0.75;
        public const double DefaultBackdropBlur = 12.0;
        public const double DefaultBackdropAngle = 90.0;
        public const int DefaultBackdropNoise = 0;
        public const int DefaultBackdropEdgeGlow = 0;
        public const int DefaultBackdropFps = 2;

        private readonly float _dpi;
        private readonly Panel _rows;
        private readonly Dictionary<string, Action> _resetters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _colorValues = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AppearanceAlphaEditor> _alphaEditors = new(StringComparer.Ordinal);

        private Theme? _theme;
        private bool _binding;
        private int _y;

        // 控件
        private readonly ComboBox _cmbFrameStyle = new();
        private readonly NumericUpDown _numFrameWidth = new();
        private readonly NumericUpDown _numFrameLength = new();
        private readonly NumericUpDown _numScanline = new();
        private readonly NumericUpDown _numGlow = new();
        private readonly NumericUpDown _numGrid = new();
        private readonly ComboBox _cmbBarStyle = new();
        private readonly ComboBox _cmbBackdropMode = new();
        private readonly NumericUpDown _numOpacity = new();
        private readonly NumericUpDown _numBlur = new();
        private readonly NumericUpDown _numAngle = new();
        private readonly NumericUpDown _numNoise = new();
        private readonly NumericUpDown _numEdgeGlow = new();
        private readonly NumericUpDown _numFps = new();

        /// <summary>构造外观字段面板。</summary>
        /// <param name="dpiScale">当前窗口的 DPI 比例（用于控件定位）。</param>
        public AppearanceFields(float dpiScale = 1.0f)
        {
            _dpi = dpiScale <= 0f ? 1.0f : dpiScale;

            Dock = DockStyle.Fill;
            AutoScroll = true;
            BackColor = SystemColors.Window;

            _rows = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            Controls.Add(_rows);

            BuildRows();
            HookEvents();
        }

        /// <summary>任一字段被改动（写回主题之后触发）。</summary>
        public event EventHandler? ValueChanged;

        // ================================================================
        // 构建界面
        // ================================================================

        private void BuildRows()
        {
            _y = (int)(10 * _dpi);

            // ---------------- 外框 ----------------
            AddSection("窗口外框 / Frame");
            _cmbFrameStyle.DropDownStyle = ComboBoxStyle.DropDownList;
            AddRow("外框样式 / FrameStyle (0–3)", _cmbFrameStyle, KeyFrameStyle, 200);
            _resetters[KeyFrameStyle] = () => SelectComboInt(_cmbFrameStyle, DefaultFrameStyle);

            AddInt(_numFrameWidth, "描边宽度 / FrameWidth (0–8, 逻辑px)", KeyFrameWidth);
            _resetters[KeyFrameWidth] = () => SetNumeric(_numFrameWidth, DefaultFrameWidth);

            AddInt(_numFrameLength, "切角边长 / FrameLength (0–64, 逻辑px)", KeyFrameLength);
            _resetters[KeyFrameLength] = () => SetNumeric(_numFrameLength, DefaultFrameLength);

            // ---------------- 纹理与强度 ----------------
            AddSection("纹理与强度 / Texture & Intensity");
            AddInt(_numScanline, "扫描线 / Scanline (0–100)", KeyScanline);
            _resetters[KeyScanline] = () => SetNumeric(_numScanline, DefaultScanline);

            AddInt(_numGlow, "辉光 / Glow (0–100)", KeyGlow);
            _resetters[KeyGlow] = () => SetNumeric(_numGlow, DefaultGlow);

            AddInt(_numGrid, "网格 / Grid (0–100)", KeyGrid);
            _resetters[KeyGrid] = () => SetNumeric(_numGrid, DefaultGrid);

            _cmbBarStyle.DropDownStyle = ComboBoxStyle.DropDownList;
            AddRow("进度条样式 / BarStyle (0–2)", _cmbBarStyle, KeyBarStyle, 200);
            _resetters[KeyBarStyle] = () => SelectComboInt(_cmbBarStyle, DefaultBarStyle);

            // ---------------- 新用途色 ----------------
            AddSection("描边 / 辉光 / 次级文字色 / Effect Colors");
            AddColorRow("描边色 / Border", KeyBorder);
            AddColorRow("辉光色 / Shadow", KeyShadow);
            AddColorRow("次级文字色 / TextSecondary", KeyTextSecondary);

            // ---------------- 背景 ----------------
            AddSection("背景 / Backdrop");
            _cmbBackdropMode.DropDownStyle = ComboBoxStyle.DropDownList;
            AddRow("背景模式 / Backdrop Mode", _cmbBackdropMode, KeyBackdropMode, 200);
            _resetters[KeyBackdropMode] = () => SelectComboValue(_cmbBackdropMode, null);

            AddDouble(_numOpacity, "背景不透明度 / Opacity (0–1)", KeyBackdropOpacity, 3);
            _resetters[KeyBackdropOpacity] = () => SetNumeric(_numOpacity, (decimal)DefaultBackdropOpacity);

            AddDouble(_numBlur, "模糊半径 / Blur (0–32, 逻辑px)", KeyBackdropBlur, 2);
            _resetters[KeyBackdropBlur] = () => SetNumeric(_numBlur, (decimal)DefaultBackdropBlur);

            AddDouble(_numAngle, "渐变角度 / GradientAngle (°)", KeyBackdropAngle, 2);
            _resetters[KeyBackdropAngle] = () => SetNumeric(_numAngle, (decimal)DefaultBackdropAngle);

            AddInt(_numNoise, "噪点 / Noise (0–100)", KeyBackdropNoise);
            _resetters[KeyBackdropNoise] = () => SetNumeric(_numNoise, DefaultBackdropNoise);

            AddInt(_numEdgeGlow, "边缘辉光 / EdgeGlow (0–100)", KeyBackdropEdgeGlow);
            _resetters[KeyBackdropEdgeGlow] = () => SetNumeric(_numEdgeGlow, DefaultBackdropEdgeGlow);

            AddInt(_numFps, "捕获帧率 / Fps (1–6)", KeyBackdropFps);
            _resetters[KeyBackdropFps] = () => SetNumeric(_numFps, DefaultBackdropFps);

            AddColorRow("背景染色 / Tint", KeyTint);
            AddColorRow("渐变起始 / GradientStart", KeyGradientStart);
            AddColorRow("渐变结束 / GradientEnd", KeyGradientEnd);

            // ---------------- 说明 ----------------
            var note = new Label
            {
                Text = "说明：以上字段缺省即关闭对应效果；保存时缺省值不会写进 JSON。\r\n"
                     + "玻璃（glass）预览使用固定样例图，不会捕获桌面。",
                Location = new Point((int)(10 * _dpi), _y),
                Width = (int)(620 * _dpi),
                Height = (int)(40 * _dpi),
                ForeColor = Color.Gray
            };
            _rows.Controls.Add(note);
            _y += (int)(46 * _dpi);
        }

        private void AddSection(string title)
        {
            var lbl = new Label
            {
                Text = title,
                Location = new Point((int)(10 * _dpi), _y),
                Width = (int)(620 * _dpi),
                Height = (int)(24 * _dpi),
                Font = new Font(Font, FontStyle.Bold)
            };
            _rows.Controls.Add(lbl);
            _y += (int)(28 * _dpi);
        }

        private void AddRow(string label, Control editor, string resetKey, int editorWidth)
        {
            _rows.Controls.Add(new Label
            {
                Text = label,
                Location = new Point((int)(10 * _dpi), _y + (int)(4 * _dpi)),
                Width = (int)(240 * _dpi),
                Height = (int)(22 * _dpi)
            });

            editor.Location = new Point((int)(260 * _dpi), _y);
            editor.Width = (int)(editorWidth * _dpi);
            _rows.Controls.Add(editor);

            var btnReset = new Button
            {
                Text = "缺省 / Default",
                Location = new Point((int)((260 + editorWidth + 10) * _dpi), _y),
                Width = (int)(110 * _dpi),
                Height = (int)(26 * _dpi)
            };
            string key = resetKey;
            btnReset.Click += (_, __) => ResetField(key);
            _rows.Controls.Add(btnReset);

            _y += (int)(34 * _dpi);
        }

        private void AddInt(NumericUpDown num, string label, string resetKey)
        {
            ConfigureNumeric(num, 0);
            AddRow(label, num, resetKey, 120);
        }

        private void AddDouble(NumericUpDown num, string label, string resetKey, int decimals)
        {
            ConfigureNumeric(num, decimals);
            AddRow(label, num, resetKey, 120);
        }

        private void ConfigureNumeric(NumericUpDown num, int decimals)
        {
            num.DecimalPlaces = decimals;
            num.Increment = decimals > 0 ? 0.05m : 1m;
            // 范围放宽：不把用户 JSON 里的越界值夹紧后回写
            num.Minimum = -1000000m;
            num.Maximum = 1000000m;
        }

        private void AddColorRow(string label, string key)
        {
            var editor = new AppearanceAlphaEditor();
            _alphaEditors[key] = editor;

            AddRow(label, editor, key, 140);

            // 颜色缺省 = null（不写出该键）
            _resetters[key] = () => { _colorValues[key] = null; editor.SetColorText(null); };
        }

        // ================================================================
        // 绑定 / 写回
        // ================================================================

        /// <summary>
        /// 把主题里的值读进控件（不写回主题、不触发 ValueChanged）。
        /// </summary>
        public void Bind(Theme theme)
        {
            if (theme == null) throw new ArgumentNullException(nameof(theme));

            _theme = theme;
            _binding = true;
            try
            {
                LayoutConfig layout = theme.Layout ?? new LayoutConfig();
                SelectComboInt(_cmbFrameStyle, layout.FrameStyle);
                SetNumeric(_numFrameWidth, layout.FrameWidth);
                SetNumeric(_numFrameLength, layout.FrameLength);
                SetNumeric(_numScanline, layout.Scanline);
                SetNumeric(_numGlow, layout.Glow);
                SetNumeric(_numGrid, layout.Grid);
                SelectComboInt(_cmbBarStyle, layout.BarStyle);

                BackdropConfig? backdrop = theme.Backdrop;
                // 主题没有 Backdrop → null（= 不启用）；有但 mode 为空串 → 当作未知值原样保留
                SelectComboValue(_cmbBackdropMode, backdrop == null ? null : backdrop.Mode);
                SetNumeric(_numOpacity, (decimal)(backdrop?.Opacity ?? DefaultBackdropOpacity));
                SetNumeric(_numBlur, (decimal)(backdrop?.Blur ?? DefaultBackdropBlur));
                SetNumeric(_numAngle, (decimal)(backdrop?.GradientAngle ?? DefaultBackdropAngle));
                SetNumeric(_numNoise, backdrop?.Noise ?? DefaultBackdropNoise);
                SetNumeric(_numEdgeGlow, backdrop?.EdgeGlow ?? DefaultBackdropEdgeGlow);
                SetNumeric(_numFps, backdrop?.Fps ?? DefaultBackdropFps);

                ColorConfig color = theme.Color ?? new ColorConfig();

                _colorValues[KeyBorder] = color.Border;
                _colorValues[KeyShadow] = color.Shadow;
                _colorValues[KeyTextSecondary] = color.TextSecondary;
                _colorValues[KeyTint] = backdrop?.Tint;
                _colorValues[KeyGradientStart] = backdrop?.GradientStart;
                _colorValues[KeyGradientEnd] = backdrop?.GradientEnd;

                foreach (KeyValuePair<string, AppearanceAlphaEditor> kv in _alphaEditors)
                    kv.Value.SetColorText(_colorValues.TryGetValue(kv.Key, out string? v) ? v : null);
            }
            finally
            {
                _binding = false;
            }

            UpdateBackdropEnabled();
        }

        /// <summary>
        /// 把控件值写回主题（<b>只改内存</b>，不落盘）。
        /// </summary>
        public void ApplyToTheme()
        {
            if (_theme == null) return;

            LayoutConfig layout = _theme.Layout ??= new LayoutConfig();
            layout.FrameStyle = GetComboInt(_cmbFrameStyle);
            layout.FrameWidth = (int)_numFrameWidth.Value;
            layout.FrameLength = (int)_numFrameLength.Value;
            layout.Scanline = (int)_numScanline.Value;
            layout.Glow = (int)_numGlow.Value;
            layout.Grid = (int)_numGrid.Value;
            layout.BarStyle = GetComboInt(_cmbBarStyle);

            ColorConfig color = _theme.Color ??= new ColorConfig();
            color.Border = GetColorValue(KeyBorder);
            color.Shadow = GetColorValue(KeyShadow);
            color.TextSecondary = GetColorValue(KeyTextSecondary);

            string? mode = GetComboValue(_cmbBackdropMode);
            if (mode == null)
            {
                // 缺省 = 不启用背景对象（可选对象缺失不自动保存一个默认实例）
                _theme.Backdrop = null;
            }
            else
            {
                BackdropConfig backdrop = _theme.Backdrop ??= new BackdropConfig();
                backdrop.Mode = mode;
                backdrop.Opacity = (double)_numOpacity.Value;
                backdrop.Blur = (double)_numBlur.Value;
                backdrop.GradientAngle = (double)_numAngle.Value;
                backdrop.Noise = (int)_numNoise.Value;
                backdrop.EdgeGlow = (int)_numEdgeGlow.Value;
                backdrop.Fps = (int)_numFps.Value;
                backdrop.Tint = GetColorValue(KeyTint);
                backdrop.GradientStart = GetColorValue(KeyGradientStart);
                backdrop.GradientEnd = GetColorValue(KeyGradientEnd);
            }
        }

        /// <summary>
        /// 恢复单个字段的缺省值（写回主题并刷新控件）。
        /// </summary>
        /// <param name="key">字段键（见 <see cref="KeyFrameStyle"/> 等常量）。</param>
        public void ResetField(string key)
        {
            if (_theme == null) return;
            if (!_resetters.TryGetValue(key, out Action? reset)) return;

            _binding = true;
            try { reset(); }
            finally { _binding = false; }

            ApplyToTheme();
            Bind(_theme);
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 恢复所有字段的缺省值（7 个整数开关归 0、Backdrop 归 null、三个新用途色归 null）。
        /// </summary>
        public void ResetAllToDefaults()
        {
            if (_theme == null) return;

            _binding = true;
            try
            {
                foreach (KeyValuePair<string, Action> kv in _resetters) kv.Value();
            }
            finally
            {
                _binding = false;
            }

            ApplyToTheme();
            Bind(_theme);
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>读取某个颜色字段当前的颜色文本。</summary>
        public string? GetColorValue(string key)
        {
            return _colorValues.TryGetValue(key, out string? v) ? v : null;
        }

        /// <summary>设置某个颜色字段的颜色文本（内存），并刷新对应控件。</summary>
        public void SetColorValue(string key, string? value)
        {
            _colorValues[key] = value;
            if (_alphaEditors.TryGetValue(key, out AppearanceAlphaEditor? editor)) editor.SetColorText(value);
        }

        // ================================================================
        // 控件事件
        // ================================================================

        /// <summary>把控件的改动接到"写回主题 + 通知外部"。</summary>
        private void HookEvents()
        {
            _cmbFrameStyle.SelectedIndexChanged += (_, __) => OnControlChanged();
            _cmbBarStyle.SelectedIndexChanged += (_, __) => OnControlChanged();
            _cmbBackdropMode.SelectedIndexChanged += (_, __) => { UpdateBackdropEnabled(); OnControlChanged(); };

            _numFrameWidth.ValueChanged += (_, __) => OnControlChanged();
            _numFrameLength.ValueChanged += (_, __) => OnControlChanged();
            _numScanline.ValueChanged += (_, __) => OnControlChanged();
            _numGlow.ValueChanged += (_, __) => OnControlChanged();
            _numGrid.ValueChanged += (_, __) => OnControlChanged();
            _numOpacity.ValueChanged += (_, __) => OnControlChanged();
            _numBlur.ValueChanged += (_, __) => OnControlChanged();
            _numAngle.ValueChanged += (_, __) => OnControlChanged();
            _numNoise.ValueChanged += (_, __) => OnControlChanged();
            _numEdgeGlow.ValueChanged += (_, __) => OnControlChanged();
            _numFps.ValueChanged += (_, __) => OnControlChanged();

            foreach (KeyValuePair<string, AppearanceAlphaEditor> kv in _alphaEditors)
            {
                string key = kv.Key;
                kv.Value.AlphaChanged += (_, __) => OnAlphaChanged(key);
            }
        }

        private void OnControlChanged()
        {
            if (_binding) return;
            ApplyToTheme();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// alpha 改动：合法则写回颜色文本，非法（越界 / NaN / Infinity / 颜色本身写坏了）
        /// 则<b>拒绝并回退</b>，保持原颜色不变。
        /// </summary>
        private void OnAlphaChanged(string key)
        {
            if (_binding) return;
            if (!_alphaEditors.TryGetValue(key, out AppearanceAlphaEditor? editor)) return;

            string? current = GetColorValue(key);

            if (!AppearanceColorText.TrySetAlpha(current, editor.Alpha, out string newText))
            {
                // 拒绝并回退：控件恢复显示原值，颜色文本不变
                editor.SetColorText(current);
                return;
            }

            _colorValues[key] = newText;
            editor.SetColorText(newText);

            ApplyToTheme();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateBackdropEnabled()
        {
            bool hasBackdrop = GetComboValue(_cmbBackdropMode) != null;
            _numOpacity.Enabled = hasBackdrop;
            _numBlur.Enabled = hasBackdrop;
            _numAngle.Enabled = hasBackdrop;
            _numNoise.Enabled = hasBackdrop;
            _numEdgeGlow.Enabled = hasBackdrop;
            _numFps.Enabled = hasBackdrop;
            if (_alphaEditors.TryGetValue(KeyTint, out var tint)) tint.Enabled = hasBackdrop;
            if (_alphaEditors.TryGetValue(KeyGradientStart, out var gs)) gs.Enabled = hasBackdrop;
            if (_alphaEditors.TryGetValue(KeyGradientEnd, out var ge)) ge.Enabled = hasBackdrop;
        }

        // ================================================================
        // 小工具
        // ================================================================

        private void SetNumeric(NumericUpDown num, decimal value)
        {
            bool prev = _binding;
            _binding = true;
            try { num.Value = value; }
            finally { _binding = prev; }
        }

        /// <summary>按整数值选中组合项；未知值额外加一项（保留原值，不静默改写）。</summary>
        private void SelectComboInt(ComboBox combo, int value)
        {
            bool prev = _binding;
            _binding = true;
            try
            {
                EnsureComboIntItems(combo);
                int index = FindComboInt(combo, value);
                if (index < 0)
                {
                    combo.Items.Add(new AppearanceComboItem("未知 / Unknown (" + value + ")", value));
                    index = combo.Items.Count - 1;
                }
                combo.SelectedIndex = index;
            }
            finally { _binding = prev; }
        }

        private int GetComboInt(ComboBox combo)
        {
            return combo.SelectedItem is AppearanceComboItem item ? item.IntValue : 0;
        }

        /// <summary>按字符串值选中组合项；未知值额外加一项（保留原值）。</summary>
        private void SelectComboValue(ComboBox combo, string? value)
        {
            bool prev = _binding;
            _binding = true;
            try
            {
                EnsureComboValueItems(combo);
                int index = FindComboString(combo, value);
                if (index < 0)
                {
                    combo.Items.Add(new AppearanceComboItem("未知 / Unknown (" + (value ?? "") + ")", value));
                    index = combo.Items.Count - 1;
                }
                combo.SelectedIndex = index;
            }
            finally { _binding = prev; }
        }

        private string? GetComboValue(ComboBox combo)
        {
            return combo.SelectedItem is AppearanceComboItem item ? item.StringValue : null;
        }

        private static int FindComboInt(ComboBox combo, int value)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is AppearanceComboItem item && item.IntValue == value) return i;
            }
            return -1;
        }

        private static int FindComboString(ComboBox combo, string? value)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is AppearanceComboItem item && string.Equals(item.StringValue, value, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        private static void EnsureComboIntItems(ComboBox combo)
        {
            if (combo.Items.Count > 0) return;
            combo.Items.Add(new AppearanceComboItem("无 / None (0)", 0));
            combo.Items.Add(new AppearanceComboItem("切角 / Chamfer (1)", 1));
            combo.Items.Add(new AppearanceComboItem("圆角描边 / Rounded (2)", 2));
            combo.Items.Add(new AppearanceComboItem("静态全息边框 / Holographic (3)", 3));
        }

        private static void EnsureComboValueItems(ComboBox combo)
        {
            if (combo.Items.Count > 0) return;
            combo.Items.Add(new AppearanceComboItem("无 / None", (string?)null));
            combo.Items.Add(new AppearanceComboItem("solid（纯色旧路径）", "solid"));
            combo.Items.Add(new AppearanceComboItem("gradient（静态渐变）", "gradient"));
            combo.Items.Add(new AppearanceComboItem("glass（实时材质 / 预览用样例图）", "glass"));
        }
    }
}
