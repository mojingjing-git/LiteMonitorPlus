using System;
using System.Buffers;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// 实时预览控件（独立于 LiteMonitor++ 主程序）
    /// - 使用 Mock 数据
    /// - 使用 UILayout + UIRenderer 渲染
    /// - 背景 + 边框 + DPI 处理
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

        public ThemePreviewControl()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;
            
            // DPI 缩放支持 - 手动计算内边距
            using (Graphics g = CreateGraphics())
            {
                _dpiScale = g.DpiX / 96f;
                Padding = new Padding((int)(5 * _dpiScale));
            }
        }

        /// <summary>
        /// 编辑器传入主题
        /// </summary>
        public void SetTheme(Theme theme)
        {
            _theme = theme;
            
            // 创建DPI缩放后的主题副本用于预览
            var scaledTheme = CreateScaledTheme(theme);
            _layout = new UILayout(scaledTheme);

            BuildMockData();
            Invalidate();
        }

        /// <summary>
        /// 创建DPI缩放后的主题副本
        /// </summary>
        private Theme CreateScaledTheme(Theme original)
        {
            var scaled = new Theme
            {
                Name = original.Name,
                FontTitle = original.FontTitle,
                FontGroup = original.FontGroup,
                FontItem = original.FontItem,
                FontValue = original.FontValue,
                Color = original.Color,
                Layout = new LayoutConfig
                {
                    Width = (int)(original.Layout.Width * _dpiScale),
                    Padding = (int)(original.Layout.Padding * _dpiScale),
                    RowHeight = (int)(original.Layout.RowHeight * _dpiScale),
                    ItemGap = (int)(original.Layout.ItemGap * _dpiScale),
                    GroupPadding = (int)(original.Layout.GroupPadding * _dpiScale),
                    GroupSpacing = (int)(original.Layout.GroupSpacing * _dpiScale),
                    GroupBottom = (int)(original.Layout.GroupBottom * _dpiScale),
                    GroupRadius = (int)(original.Layout.GroupRadius * _dpiScale),
                    GroupTitleOffset = (int)(original.Layout.GroupTitleOffset * _dpiScale),
                    CornerRadius = (int)(original.Layout.CornerRadius * _dpiScale)
                }
            };
            
            return scaled;
        }

        /// <summary>
        /// 构造 Mock 数据用于预览
        /// </summary>
        private void BuildMockData()
        {
            _groups.Clear();

            _groups.Add(new GroupLayoutInfo("CPU", new()
            {
                new MetricItem { Key = "CPU.Load", Value = 23, DisplayValue = 23, Label = "CPU 使用率" },
                new MetricItem { Key = "CPU.Temp", Value = 65, DisplayValue = 65, Label = "CPU 温度" }
            }));

            _groups.Add(new GroupLayoutInfo("GPU", new()
            {
                new MetricItem { Key = "GPU.Load", Value = 90, DisplayValue = 90, Label = "GPU 使用率" },
                new MetricItem { Key = "GPU.Temp", Value = 65, DisplayValue = 65, Label = "GPU 温度" },
                new MetricItem { Key = "GPU.VRAM", Value = 41, DisplayValue = 41, Label = "VRAM 占用" }
            }));

            _groups.Add(new GroupLayoutInfo("MEM", new()
            {
                new MetricItem { Key = "MEM.Load", Value = 68, DisplayValue = 68, Label = "内存占用" }
            }));

            _groups.Add(new GroupLayoutInfo("DISK", new()
            {
                new MetricItem { Key = "DISK.Read", Value = 1024 * 500, Label = "磁盘读取" },
                new MetricItem { Key = "DISK.Write", Value = 1024 * 3320, Label = "磁盘写入" }
            }));

            _groups.Add(new GroupLayoutInfo("NET", new()
            {
                new MetricItem { Key = "NET.Up", Value = 1024 * 80, Label = "上传速度" },
                new MetricItem { Key = "NET.Down", Value = 1024 * 38000, Label = "下载速度" }
            }));

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
                var previewTheme = CreatePreviewTheme(_theme, content.Width);
                var previewLayout = new UILayout(previewTheme);
                int h = previewLayout.Build(_groups);
                
                int bmpH = Math.Max(1, h);
                var requiredSize = new Size(previewTheme.Layout.Width, bmpH);

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

                    UIRenderer.Render(gBmp, _groups, previewTheme);
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
        /// 创建适应预览控件宽度的主题副本
        /// </summary>
        private Theme CreatePreviewTheme(Theme original, int previewWidth)
        {
            var preview = new Theme
            {
                Name = original.Name,
                FontTitle = original.FontTitle,
                FontGroup = original.FontGroup,
                FontItem = original.FontItem,
                FontValue = original.FontValue,
                Color = original.Color,
                Layout = new LayoutConfig
                {
                    Width = (int)(240 * _dpiScale), // 固定为240像素（考虑DPI缩放）
                    Padding = (int)(original.Layout.Padding * _dpiScale),
                    RowHeight = (int)(original.Layout.RowHeight * _dpiScale),
                    ItemGap = (int)(original.Layout.ItemGap * _dpiScale),
                    GroupPadding = (int)(original.Layout.GroupPadding * _dpiScale),
                    GroupSpacing = (int)(original.Layout.GroupSpacing * _dpiScale),
                    GroupBottom = (int)(original.Layout.GroupBottom * _dpiScale),
                    GroupRadius = (int)(original.Layout.GroupRadius * _dpiScale),
                    GroupTitleOffset = (int)(original.Layout.GroupTitleOffset * _dpiScale),
                    CornerRadius = (int)(original.Layout.CornerRadius * _dpiScale)
                }
            };
            
            return preview;
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
    }
}