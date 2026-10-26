using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LiteMonitor.Contracts; // B2：树节点 Tag 为平台无关的 HardwareCatalogEntry，不再持有 LHM 活对象
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.Controls
{
    public class LiteTreeView : TreeView
    {
        private static readonly Brush _selectBgBrush = new SolidBrush(Color.FromArgb(204, 232, 255)); 
        private static readonly Brush _hoverBrush = new SolidBrush(Color.FromArgb(250, 250, 250)); 
        private static readonly Pen _linePen = new Pen(Color.FromArgb(240, 240, 240)); 
        private static readonly Brush _chevronBrush = new SolidBrush(Color.Gray); 

        private Font _baseFont;
        private Font _boldFont;

        // ★★★ 新增：用于手动追踪悬停节点，解决“触角效果没生效”的问题 ★★★
        // ★ 可空：鼠标不在任何节点上时 GetNodeAt 返回 null（OnMouseMove 直接把它存进本字段），
        //   OnMouseLeave 又会把本字段清空 ⇒ 运行期确实可能为 null，如实声明为可空。
        private TreeNode? _hoverNode;

        // 布局参数
        public int ColValueWidth { get; set; } = 70;  
        public int ColMaxWidth { get; set; } = 70;
        public int RightMargin { get; set; } = 6;    
        public int IconWidth { get; set; } = 20;      

        public LiteTreeView()
        {
            // ★★★ 修复1：移除 AllPaintingInWmPaint，防止黑屏 ★★★
            // 只保留基本的双缓冲设置
            this.SetStyle(
                ControlStyles.OptimizedDoubleBuffer | 
                ControlStyles.ResizeRedraw, true);

            this.DrawMode = TreeViewDrawMode.OwnerDrawText; 
            this.ShowLines = false;
            this.ShowPlusMinus = false; 
            this.FullRowSelect = true;
            this.BorderStyle = BorderStyle.None;
            this.BackColor = Color.White;
            this.ItemHeight = UIUtils.S(28); 

            _baseFont = new Font("Microsoft YaHei UI", 9f);
            _boldFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            this.Font = _baseFont;
        }

        // ★★★ 修复2：启用 WS_EX_COMPOSITED (终极防闪烁) ★★★
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED (双缓冲合成)
                return cp;
            }
        }

        // ★★★ 修复3：彻底删除 WndProc 方法 (让系统正常擦除背景，解决底部黑屏) ★★★

        public void InvalidateSensorValue(TreeNode node)
        {
            if (node == null || node.Bounds.Height <= 0) return;
            
            // 计算需要重绘的宽度 (必须 >= OnDrawNode 中定义的 rightZoneWidth)
            // ColMaxWidth(70) + ColValueWidth(70) + RightMargin(6) + Spacing(25) + Padding(10) ≈ 181
            // 我们给它一个稍微宽裕一点的整数，确保覆盖所有数值，但绝对不要碰到文字
            int refreshWidth = UIUtils.S(ColMaxWidth + ColValueWidth + RightMargin + 40); 
            
            int safeWidth = this.ClientSize.Width;
            if (refreshWidth > safeWidth) refreshWidth = safeWidth;

            // 计算脏矩形
            Rectangle dirtyRect = new Rectangle(safeWidth - refreshWidth, node.Bounds.Y, refreshWidth, node.Bounds.Height);
            
            this.Invalidate(dirtyRect);
        }

        // ★★★ 新增：鼠标移动时手动触发重绘，确保悬停效果灵敏 ★★★
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            TreeNode currentNode = this.GetNodeAt(e.Location);
            if (currentNode != _hoverNode)
            {
                // 重绘旧的悬停节点（取消高亮）
                if (_hoverNode != null && _hoverNode.Bounds.Height > 0) 
                    this.Invalidate(new Rectangle(0, _hoverNode.Bounds.Y, this.Width, _hoverNode.Bounds.Height));
                
                _hoverNode = currentNode;

                // 重绘新的悬停节点（显示高亮）
                if (_hoverNode != null && _hoverNode.Bounds.Height > 0)
                    this.Invalidate(new Rectangle(0, _hoverNode.Bounds.Y, this.Width, _hoverNode.Bounds.Height));
            }
        }

        // ★★★ 新增：鼠标离开控件时清除悬停状态 ★★★
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverNode != null)
            {
                this.Invalidate(new Rectangle(0, _hoverNode.Bounds.Y, this.Width, _hoverNode.Bounds.Height));
                _hoverNode = null;
            }
        }

        // ★★★ 新增：右键点击自动选中节点 ★★★
        protected override void OnMouseDown(MouseEventArgs e)
        {
            // 如果是右键，先尝试找到鼠标位置的节点并选中它
            if (e.Button == MouseButtons.Right)
            {
                var node = this.GetNodeAt(e.X, e.Y);
                if (node != null)
                {
                    this.SelectedNode = node;
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnDrawNode(DrawTreeNodeEventArgs e)
        {
            // 1. 基础防呆检查
            if (e.Bounds.Height <= 0 || e.Bounds.Width <= 0) return;

            // ★ .NET 8 起 DrawTreeNodeEventArgs.Node 是 TreeNode?：
            //   画"树线"伪节点时（ShowLines=true）会收到 Node=null 的绘制事件。
            //   本控件构造函数里设了 ShowLines=false ⇒ 运行期拿到的都是真实节点；
            //   但这里仍然用"局部量 + 条件包裹"如实表达，不写 e.Node! 断言，
            //   也不在绘制回调里提前 return（那会跳过后续绘制语句）。
            var node = e.Node;
            if (node is not null)
            {
                DrawNodeContent(e, node);
            }
        }

        /// <summary>
        /// 绘制单个树节点。<paramref name="node"/> 已由 <see cref="OnDrawNode"/> 判定为非空。
        /// </summary>
        /// <remarks>
        /// ★ 方法名带 Content 后缀：TreeView 本身已有一个 DrawNode 事件，
        ///   同名方法会触发 CS0108（隐藏继承成员）警告。
        /// </remarks>
        private void DrawNodeContent(DrawTreeNodeEventArgs e, TreeNode node)
        {
            var g = e.Graphics;
            // 恢复高质量绘图设置
            g.SmoothingMode = SmoothingMode.HighQuality; 
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int w = this.ClientSize.Width; 
            Rectangle fullRow = new Rectangle(0, e.Bounds.Y, w, this.ItemHeight);

            // 2. 绘制背景 (处理选中/悬停)
            bool isSelected = (e.State & TreeNodeStates.Selected) != 0;
            bool isHot = (node == _hoverNode); 

            if (isSelected) g.FillRectangle(_selectBgBrush, fullRow);
            else if (isHot) g.FillRectangle(_hoverBrush, fullRow);
            else g.FillRectangle(Brushes.White, fullRow); // 显式擦除背景

            if (!isSelected)
                g.DrawLine(_linePen, 0, fullRow.Bottom - 1, w, fullRow.Bottom - 1);

            // 3. 计算关键坐标
            int baseIndent = node.Level * UIUtils.S(20);
            Rectangle chevronRect = new Rectangle(baseIndent + UIUtils.S(5), fullRow.Y, UIUtils.S(IconWidth), fullRow.Height);

            // --- 定义“右侧禁区” (数值列占用区域) ---
            // Max列 + Value列 + 间距 + 右边距
            int rightZoneWidth = UIUtils.S(ColMaxWidth + ColValueWidth + RightMargin + 25); 
            int rightZoneStart = w - rightZoneWidth;

            // 计算具体的列坐标
            int xMax = w - UIUtils.S(RightMargin) - UIUtils.S(25) - UIUtils.S(ColMaxWidth);
            Rectangle maxRect = new Rectangle(xMax, fullRow.Y, UIUtils.S(ColMaxWidth), fullRow.Height);
            
            int xValue = xMax - UIUtils.S(20) - UIUtils.S(ColValueWidth);
            Rectangle valRect = new Rectangle(xValue, fullRow.Y, UIUtils.S(ColValueWidth), fullRow.Height);

            // 4. 绘制折叠图标
            if (node.Nodes.Count > 0) DrawChevron(g, chevronRect, node.IsExpanded);

            // 5. 绘制右侧数值 (仅传感器)
            if (node.Tag is HardwareCatalogEntry { SensorType: not null } sensor)
            {
                string maxStr = FormatValue(sensor.Max, sensor.SensorType);
                TextRenderer.DrawText(g, maxStr, _baseFont, maxRect, Color.DimGray, 
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);

                string valStr = FormatValue(sensor.Value, sensor.SensorType);
                Color valColor = GetColorByType(sensor.SensorType);
                TextRenderer.DrawText(g, valStr, _baseFont, valRect, valColor, 
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);
            }

            // 6. 绘制左侧文本 (核心修复)
            Color txtColor;
            Font font;

            // 样式选择（设备条目加粗，传感器条目常规，分组节点加粗）
            if (node.Tag is HardwareCatalogEntry { SensorType: null }) { font = _boldFont; txtColor = Color.FromArgb(20, 20, 20); }
            else if (node.Tag is HardwareCatalogEntry) { font = _baseFont; txtColor = Color.Black; }
            else { font = _boldFont; txtColor = Color.FromArgb(30, 30, 30); }

            int textStartX = chevronRect.Right + UIUtils.S(5);

            // ★★★ 核心修复逻辑 ★★★
            // ★★★ 修复开始 ★★★
            Rectangle textRect;
            // 基础 Flag：垂直居中 | 左对齐 | 单行 | 不显示前缀符号(&)
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

            // 区分处理
            if (node.Tag is HardwareCatalogEntry { SensorType: not null })
            {
                // 传感器：必须避让右侧数值区，并在不够时显示省略号
                int maxTextWidth = rightZoneStart - textStartX - UIUtils.S(10);
                if (maxTextWidth < 10) maxTextWidth = 10;
                
                textRect = new Rectangle(textStartX, fullRow.Y, maxTextWidth, fullRow.Height);
                
                // ★ 关键：只有传感器才加省略号 ★
                flags |= TextFormatFlags.EndEllipsis; 
            }
            else
            {
                // 硬件标题：给它无限宽度
                // ★ 关键：这里绝对不能加 EndEllipsis，否则又会乱码 ★
                int maxTextWidth = w - UIUtils.S(RightMargin) - textStartX;
                textRect = new Rectangle(textStartX, fullRow.Y, maxTextWidth, fullRow.Height);
            }
            
            // 使用动态计算的 flags 绘制
            TextRenderer.DrawText(g, node.Text, font, textRect, txtColor, flags);
            // ★★★ 修复结束 ★★★
        }
        private void DrawChevron(Graphics g, Rectangle rect, bool expanded)
        {
            int cx = rect.X + rect.Width / 2;
            int cy = rect.Y + rect.Height / 2;
            int size = UIUtils.S(4);

            using (Pen p = new Pen(_chevronBrush, 1.5f))
            {
                if (expanded) // V
                {
                    g.DrawLine(p, cx - size, cy - 2, cx, cy + 3);
                    g.DrawLine(p, cx, cy + 3, cx + size, cy - 2);
                }
                else // >
                {
                    g.DrawLine(p, cx - 2, cy - size, cx + 2, cy);
                    g.DrawLine(p, cx + 2, cy, cx - 2, cy + size);
                }
            }
        }

        private Color GetColorByType(string? type)
        {
            switch (type) {
                case "Temperature": return Color.FromArgb(200, 60, 0);
                case "Load": return Color.FromArgb(0, 100, 0);
                case "Power": return Color.Purple;
                case "Clock": return Color.DarkBlue;
                // ★★★ 修改：默认颜色改为绿色 ★★★
                default: return Color.FromArgb(34, 139, 34); // ForestGreen
            }
        }

        private string FormatValue(float? val, string? type)
        {
            if (!val.HasValue) return "-";
            float v = val.Value;

            // 1. 数据类特殊处理 (LHM 单位转 Bytes)
            if (type == "Data") return MetricUtils.FormatDataSize((double)v * 1073741824.0);
            if (type == "SmallData") return MetricUtils.FormatDataSize((double)v * 1048576.0);

            if (type == "Throughput")
            {
                string dummyKey = "NET.Throughput";
                return MetricUtils.GetValueStr(dummyKey, v) + MetricUtils.GetUnitStr(dummyKey, v, MetricUtils.UnitContext.Panel);
            }

            // 2. 映射到 MetricUtils 的 Key（SensorType 字符串来自适配器的 sensor.SensorType.ToString()）
            string key = type switch
            {
                "Voltage" => "VOLTAGE",
                "Clock" => "CLOCK",
                "Temperature" => "TEMP",
                "Load" => "LOAD",
                "Level" => "LOAD",
                "Fan" => "FAN",
                "Power" => "POWER",
                _ => ""
            };

            if (string.IsNullOrEmpty(key)) return $"{v:F1}";

            // 3. 使用统一的格式化逻辑
            return MetricUtils.GetValueStr(key, v) + MetricUtils.GetUnitStr(key, v, MetricUtils.UnitContext.Panel);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var node = this.GetNodeAt(e.X, e.Y);
            if (node != null && node.Nodes.Count > 0)
            {
                // 计算左侧图标的有效点击区域
                int baseIndent = node.Level * UIUtils.S(20);
                // 给图标左右各加一点缓冲区域方便点击
                int clickAreaStart = baseIndent;
                int clickAreaEnd = baseIndent + UIUtils.S(IconWidth) + UIUtils.S(15);

                // 如果点击了左侧图标区域
                if (e.X >= clickAreaStart && e.X <= clickAreaEnd) 
                {
                    if (node.IsExpanded) node.Collapse(); else node.Expand();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _baseFont?.Dispose(); _boldFont?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}