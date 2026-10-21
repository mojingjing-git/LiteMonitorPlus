using LiteMonitor.src.Core;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LiteMonitor
{
    public enum LayoutMode
    {
        Horizontal,
        Taskbar
    }

    public class HorizontalLayout
    {
        private readonly Theme _t;
        private readonly LayoutMode _mode;
        private readonly Settings _settings;

        private readonly int _padding;
        private int _rowH;

        // DPI
        private readonly float _dpiScale;

        public int PanelWidth { get; private set; }

        /// <summary>
        /// 空态下的**最小面板高度**（物理像素）。
        /// <para>★ 全部可见项关闭时列集合为空，<see cref="Build"/> 过去直接返回 0，
        ///   调用方据此把客户区高度写成 0 ⇒ 面板整个从屏幕上消失。
        ///   用户既看不到任何指标，也分不清是"没有勾选任何项"还是"程序崩了"。
        ///   这里给一个最小高度，让它退化成一块**空面板**而不是凭空消失。</para>
        /// <para>★ 任务栏模式不使用这个值：任务栏高度由任务栏自己的 Placement 决定，
        ///   <c>Build</c> 的返回值在任务栏链路上不被消费（TaskbarForm 只取 PanelWidth）。</para>
        /// </summary>
        public int MinPanelHeight => Math.Max(_rowH + _padding, 24);

        public HorizontalLayout(Theme t, int initialWidth, LayoutMode mode, Settings? settings = null)
        {
            _t = t;
            _mode = mode;
            _settings = settings ?? Settings.Load();

            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                _dpiScale = g.DpiX / 96f;
            }

            _padding = t.Layout.Padding;

            if (mode == LayoutMode.Horizontal)
                _rowH = Math.Max(t.FontItem.Height, t.FontValue.Height);
            else
                _rowH = 0; // 任务栏模式稍后根据 taskbarHeight 决定

            PanelWidth = initialWidth;
        }

        /// <summary>
        /// Build：横屏/任务栏共用布局
        /// </summary>
        public int Build(List<Column> cols, int taskbarHeight = 32)
        {
            // ★ D10：列集合为空（全部可见项关闭）时**不能返回 0** ——
            //   调用方会把客户区高度写成 0，面板直接消失，用户无从判断原因。
            //   改为返回最小高度，配合 UI 层的空态提示说明"没有可见项"。
            if (cols == null || cols.Count == 0) return MinPanelHeight;
            
            var s = _settings.GetStyle();
            int pad = _padding;
            int padV = _padding / 2;
            bool isTaskbarSingle = (_mode == LayoutMode.Taskbar && _settings.TaskbarSingleLine);
            bool isHorizontalSingle = (_mode == LayoutMode.Horizontal && _settings.HorizontalSingleLine);

            if (_mode == LayoutMode.Taskbar)
            {
                padV = 0;
                _rowH = isTaskbarSingle ? taskbarHeight : taskbarHeight / 2;
            }

            int totalWidth = pad * 2;
            float dpi = _dpiScale;

            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                foreach (var col in cols)
                {
                    // 分别计算 Top 和 Bottom 的所需宽度，然后取最大值
                    int widthTop = 0;
                    int widthBottom = 0;



                    // 执行测量
                    widthTop = MeasureMetricItem(g, col.Top, s);
                    widthBottom = MeasureMetricItem(g, col.Bottom, s);

                    // ★★★ 核心修复：列宽取上下两者的最大值 ★★★
                    // 这样即使 IP 在下面，列宽也会被 IP 撑大；
                    // 同时上面的普通项也能利用这个宽度正常显示（虽然左右会有空余，但不会重叠）
                    col.ColumnWidth = Math.Max(widthTop, widthBottom);
                    
                    totalWidth += col.ColumnWidth;
                }
            }


             // 组间距逻辑
            int gapBase = (_mode == LayoutMode.Taskbar) ? s.Gap : _settings.HorizontalItemSpacing; 
            int gap = (int)Math.Round(gapBase * dpi); 

            if (cols.Count > 1) totalWidth += (cols.Count - 1) * gap;
            PanelWidth = totalWidth;
            
            // ===== 设置列 Bounds =====
            int x = pad;

            foreach (var col in cols)
            {
                int colHeight;
                if (isTaskbarSingle || isHorizontalSingle)
                    colHeight = _rowH;
                else
                    colHeight = _rowH * 2;
                
                col.Bounds = new Rectangle(x, padV, col.ColumnWidth, colHeight);

                if (_mode == LayoutMode.Taskbar)
                {
                    int fixOffset = 1; 
                    
                    if (isTaskbarSingle) {
                        col.BoundsTop = new Rectangle(x, col.Bounds.Y + fixOffset, col.ColumnWidth, colHeight);
                        col.BoundsBottom = Rectangle.Empty;
                    } else {
                        // 双行模式
                        col.BoundsTop = new Rectangle(x, col.Bounds.Y + s.VOff + fixOffset, col.ColumnWidth, _rowH - s.VOff);
                        col.BoundsBottom = new Rectangle(x, col.Bounds.Y + _rowH - s.VOff + fixOffset, col.ColumnWidth, _rowH);
                    }
                }
                else
                {
                    // 横屏模式
                    if (isHorizontalSingle)
                    {
                        // 单行模式：Top 居中显示，隐藏 Bottom
                        col.BoundsTop = new Rectangle(col.Bounds.X, col.Bounds.Y, col.Bounds.Width, _rowH);
                        col.BoundsBottom = Rectangle.Empty;
                    }
                    else
                    {
                        // 默认双行模式
                        col.BoundsTop = new Rectangle(col.Bounds.X, col.Bounds.Y, col.Bounds.Width, _rowH);
                        col.BoundsBottom = new Rectangle(col.Bounds.X, col.Bounds.Y + _rowH, col.Bounds.Width, _rowH);
                    }
                }
                
                // [补充修正] 如果是 NET.IP 混合列，我们需要告诉 Renderer 不要画 Label 区域，而是全宽显示
                // 但由于 Renderer 是根据 (LabelRect, ValueRect) 绘图的，而 HorizontalLayout 不负责计算具体的 LabelRect
                // 所以我们依赖 TaskbarRenderer 的逻辑：它会看 Label 是否为空。
                // 只要列宽足够（ColumnWidth 够大），TaskbarRenderer 右对齐 Value 时就不会出问题。

                x += col.ColumnWidth + gap;
            }

            return padV * 2 + ((isTaskbarSingle || isHorizontalSingle) ? _rowH : _rowH * 2);
        }

        private int MeasureMetricItem(Graphics g, MetricItem item, Settings.TBStyle s)
        {
            if (item == null) return 0;

            float dpi = _dpiScale;

            // [通用逻辑] 如果隐藏标签 (ShortLabel 为空 或 " ")，则只计算文本宽
            if (string.IsNullOrEmpty(item.ShortLabel) || item.ShortLabel == " ")
            {
                // 对于 Dashboard/IP 类，直接使用当前文本作为测量依据
                string valText = item.TextValue ?? item.GetFormattedText(true);
                if (string.IsNullOrEmpty(valText)) return 0;

                Font valFont;
                bool disposeFont = false;

                if (_mode == LayoutMode.Taskbar)
                {
                    valFont = new Font(s.Font, s.Size, s.Bold ? FontStyle.Bold : FontStyle.Regular);
                    disposeFont = true;
                }
                else
                {
                    valFont = _t.FontItem;
                }

                try
                {
                    int w = TextRenderer.MeasureText(g, valText, valFont,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding).Width;
                    
                    // 纯文本项建议稍微加一点点左右 padding，防止紧贴
                    return w + 4;
                }
                finally
                {
                    if (disposeFont) valFont.Dispose();
                }
            }
            else
            {
                // [普通逻辑] 标签 + 数值 + 间距
                // 1. Label
                string label = item.ShortLabel;
                Font labelFont, valueFont;
                bool disposeFont = false;

                if (_mode == LayoutMode.Taskbar)
                {
                    var fs = s.Bold ? FontStyle.Bold : FontStyle.Regular;
                    var f = new Font(s.Font, s.Size, fs);
                    labelFont = f; valueFont = f;
                    disposeFont = true;
                }
                else
                {
                    labelFont = _t.FontItem;
                    valueFont = _t.FontValue;
                }

                try
                {
                    int wLabel = TextRenderer.MeasureText(g, label, labelFont,
                        new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;

                    // 2. Value (使用样本值估算 或 真实值)
                    string sample = GenerateSampleText(item);

                    int wValue = TextRenderer.MeasureText(g, sample, valueFont,
                        new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;

                    // 3. Padding
                    int paddingX;
                    if (_mode == LayoutMode.Taskbar || _settings.HorizontalFollowsTaskbar)
                        paddingX = (int)Math.Round(s.Inner * dpi);
                    else
                        paddingX = (int)Math.Round(_settings.HorizontalInnerSpacing * dpi);

                    return wLabel + wValue + paddingX;
                }
                finally
                {
                    if (disposeFont)
                    {
                        labelFont.Dispose();
                        // valueFont is same reference as labelFont in Taskbar mode
                    }
                }
            }
        }

        private string GenerateSampleText(MetricItem item)
        {
            // 1. 优先获取 TextValue
            string val = item.TextValue ?? "";
            
            // ★★★ 优化：直接调用 GetUnitStr 获取默认单位 ★★★
            // 无论是硬件(°C) 还是 插件(从InfoService查)，这里都能统一拿到了
            string rawUnit = MetricUtils.GetUnitStr(item.Key, 0, MetricUtils.UnitContext.Taskbar);

            // 2. 硬件监控项的特殊处理（如果没有 TextValue 且不是插件项，则使用硬件估算逻辑）
            if (string.IsNullOrEmpty(val) && !item.Key.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase))
            {
                val = MetricUtils.GetSampleValueStr(item.Key);

                // [特殊保留] 网速/硬盘仍需强制 "MB"，防止 GetUnitStr 返回 "KB" 导致宽度不够出现抖动
                var type = MetricUtils.GetType(item.Key);
                if (type == MetricType.DataSpeed || type == MetricType.DataSize)
                {
                    rawUnit = "MB"; 
                }
            }

            // 2. 处理显示单位 (叠加用户配置)
            string userFmt = item.BoundConfig?.UnitTaskbar;
            string unit = MetricUtils.GetDisplayUnit(item.Key, rawUnit, userFmt);

            // 3. 拼接并生成样本 (将所有数字替换为 '0')
            // [Optimization] 使用 string.Create 避免中间数组分配 (Net 8.0+)
            bool appendUnit = !string.IsNullOrEmpty(unit) && !val.EndsWith(unit);
            int totalLen = val.Length + (appendUnit ? unit.Length : 0);
            
            return string.Create(totalLen, (val, unit, appendUnit), (span, state) =>
            {
                var (v, u, append) = state;
                int pos = 0;
                
                // 写入数值部分 (数字转0)
                foreach (char c in v)
                {
                    span[pos++] = char.IsDigit(c) ? '0' : c;
                }
                
                // 写入单位部分
                if (append)
                {
                    foreach (char c in u)
                    {
                        span[pos++] = char.IsDigit(c) ? '0' : c; 
                    }
                }
            });
        }

        // [通用方案] 获取当前布局的签名是否变化 (用于检测是否需要重绘)
        public string GetLayoutSignature(List<Column> cols)
        {
            if (cols == null || cols.Count == 0) return "";
            
            unchecked
            {
                int hash = 17;
                foreach (var col in cols)
                {
                    // 直接计算 Top 和 Bottom 的特征哈希，不再生成中间样本字符串
                    void AddItemToHash(MetricItem? item)
                    {
                        if (item == null) return;
                        string text = item.TextValue ?? item.GetFormattedText(true);
                        hash = hash * 31 + text.Length;
                        // 关键：数字位宽一致，所以只对非数字字符（单位、小数点）做哈希
                        foreach (char c in text) if (!char.IsDigit(c)) hash = (hash << 5) - hash + c;
                    }

                    AddItemToHash(col.Top);
                    AddItemToHash(col.Bottom);
                }
                return hash.ToString();
            }
        }
    }

    public class Column
    {
        public MetricItem? Top;
        public MetricItem? Bottom;

        public int ColumnWidth;
        public Rectangle Bounds = Rectangle.Empty;

        // ★★ B 方案新增：上下行布局由 Layout 计算，不再由 Renderer 处理
        public Rectangle BoundsTop = Rectangle.Empty;
        public Rectangle BoundsBottom = Rectangle.Empty;
    }
}