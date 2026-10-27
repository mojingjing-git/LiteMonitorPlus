using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace LiteMonitor
{
    public class TrendChartPoint
    {
        public DateTime Time { get; set; }
        public float Avg { get; set; }
        public float Max { get; set; }
        public float Min { get; set; }
        public int Count { get; set; }
    }

    public class TrendChartSeries
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public string Unit { get; set; } = "";
        public Color Color { get; set; }
        public List<TrendChartPoint> Points { get; set; } = new List<TrendChartPoint>();
    }

    public class TrendChartControl : Control
    {
        private Color _gridColor = Color.FromArgb(58, 58, 58);
        private Color _axisColor = Color.FromArgb(95, 95, 95);
        private Color _textColor = Color.FromArgb(220, 220, 220);
        private Color _dimTextColor = Color.FromArgb(145, 145, 145);
        private Color _tooltipBack = Color.FromArgb(235, 38, 38, 38);
        private readonly List<TrendChartSeries> _series = new List<TrendChartSeries>();
        private readonly HashSet<string> _hiddenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<LegendHit> _legendHits = new List<LegendHit>();

        private DateTime? _hoverTime;
        private string? _highlightKey;
        private int _legendRows = 1;

        public float? Threshold { get; set; }
        public string Title { get; set; } = "";
        public string EmptyText { get; set; } = "暂无历史数据";
        public string? HighlightKey => _highlightKey;

        public TrendChartControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(38, 38, 38);
            ForeColor = _textColor;
            Font = new Font("Microsoft YaHei UI", 9F);
        }

        /// <summary>T04：跟随设置窗体/应用主题切换图表配色；不改变系列颜色。</summary>
        public void ApplyPalette(Color background, Color grid, Color text, Color dim)
        {
            BackColor = background;
            _gridColor = grid;
            _axisColor = Color.FromArgb(
                Math.Min(255, (grid.R + 37 + text.R) / 3),
                Math.Min(255, (grid.G + 37 + text.G) / 3),
                Math.Min(255, (grid.B + 37 + text.B) / 3));
            _textColor = text;
            _dimTextColor = dim;
            _tooltipBack = Color.FromArgb(235, background);
            Invalidate();
        }

        public void SetSeries(IEnumerable<TrendChartSeries> series)
        {
            _series.Clear();
            _series.AddRange(series.Where(s => s.Points.Count > 0));
            _hiddenKeys.RemoveWhere(key => !_series.Any(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));

            if (!string.IsNullOrEmpty(_highlightKey) && !_series.Any(s => s.Key.Equals(_highlightKey, StringComparison.OrdinalIgnoreCase)))
            {
                _highlightKey = null;
            }

            _hoverTime = null;
            Invalidate();
        }

        public void SetHighlightedSeries(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                if (_highlightKey == null) return;
                _highlightKey = null;
                Invalidate();
                return;
            }

            _hiddenKeys.Remove(key);
            if (_highlightKey != null && _highlightKey.Equals(key, StringComparison.OrdinalIgnoreCase)) return;

            _highlightKey = key;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;

            string? legendKey = HitTestLegend(e.Location);
            if (legendKey == null) return;

            bool isHidden = _hiddenKeys.Contains(legendKey);
            if (!isHidden && GetVisibleSeries().Count <= 1) return;

            if (isHidden)
            {
                _hiddenKeys.Remove(legendKey);
            }
            else
            {
                _hiddenKeys.Add(legendKey);
                if (_highlightKey != null && _highlightKey.Equals(legendKey, StringComparison.OrdinalIgnoreCase))
                    _highlightKey = null;
            }

            _hoverTime = null;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (HitTestLegend(e.Location) != null)
            {
                Cursor = Cursors.Hand;
                if (_hoverTime.HasValue)
                {
                    _hoverTime = null;
                    Invalidate();
                }
                return;
            }

            Cursor = Cursors.Default;
            var visibleSeries = GetVisibleSeries();
            var plot = GetPlotRect();
            if (!plot.Contains(e.Location) || visibleSeries.Count == 0)
            {
                if (_hoverTime.HasValue)
                {
                    _hoverTime = null;
                    Invalidate();
                }
                return;
            }

            var times = GetAllTimes();
            if (times.Count == 0) return;

            DateTime minTime = times.First();
            DateTime maxTime = times.Last();
            double span = Math.Max(1, (maxTime - minTime).TotalSeconds);
            double cursorSeconds = ((double)(e.X - plot.Left) / Math.Max(1, plot.Width)) * span;
            DateTime cursorTime = minTime.AddSeconds(cursorSeconds);
            DateTime nearest = times.OrderBy(t => Math.Abs((t - cursorTime).TotalSeconds)).First();

            if (_hoverTime != nearest)
            {
                _hoverTime = nearest;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Cursor = Cursors.Default;
            if (_hoverTime.HasValue)
            {
                _hoverTime = null;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var back = new SolidBrush(BackColor))
                g.FillRectangle(back, ClientRectangle);

            if (_series.Count == 0)
            {
                DrawEmpty(g);
                return;
            }

            DrawTitleAndLegend(g);

            var visibleSeries = GetVisibleSeries();
            if (visibleSeries.Count == 0)
            {
                DrawEmptyText(g);
                return;
            }

            var plot = GetPlotRect();
            var range = GetValueRange();
            var times = GetAllTimes();
            if (times.Count == 0)
            {
                DrawEmptyText(g);
                return;
            }

            DateTime minTime = times.First();
            DateTime maxTime = times.Last();
            if (minTime == maxTime) maxTime = minTime.AddMinutes(1);

            DrawGrid(g, plot, range.Min, range.Max, minTime, maxTime);
            DrawThreshold(g, plot, range.Min, range.Max);
            DrawLines(g, plot, range.Min, range.Max, minTime, maxTime);
            DrawHover(g, plot, range.Min, range.Max, minTime, maxTime);
        }

        private Rectangle GetPlotRect()
        {
            int left = Width < S(520) ? S(46) : S(54);
            int right = Width < S(520) ? S(12) : S(22);
            int bottom = Height < S(280) ? S(32) : S(42);
            int top = S(48 + Math.Max(0, _legendRows - 1) * 22);

            return new Rectangle(
                left,
                top,
                Math.Max(S(80), Width - left - right),
                Math.Max(S(80), Height - top - bottom));
        }

        private void DrawEmpty(Graphics g)
        {
            DrawTitleAndLegend(g);
            DrawEmptyText(g);
        }

        private void DrawEmptyText(Graphics g)
        {
            using var dimBrush = new SolidBrush(_dimTextColor);
            var size = g.MeasureString(EmptyText, Font);
            g.DrawString(EmptyText, Font, dimBrush, (Width - size.Width) / 2, (Height - size.Height) / 2);
        }

        private void DrawTitleAndLegend(Graphics g)
        {
            _legendHits.Clear();

            using var titleBrush = new SolidBrush(_textColor);
            using var dimBrush = new SolidBrush(_dimTextColor);
            using var titleFont = new Font(Font, FontStyle.Bold);

            if (!string.IsNullOrEmpty(Title))
                g.DrawString(Title, titleFont, titleBrush, S(16), S(12));

            int maxRight = Math.Max(S(180), Width - S(16));
            int x = Math.Max(S(160), (int)g.MeasureString(Title, titleFont).Width + S(30));
            int y = S(13);
            int rowHeight = S(22);
            int rows = 1;

            foreach (var s in _series)
            {
                int textWidth = (int)g.MeasureString(s.Label, Font).Width;
                int itemWidth = Math.Min(S(170), Math.Max(S(82), textWidth + S(48)));

                if (x + itemWidth > maxRight && x > S(16))
                {
                    x = S(16);
                    y += rowHeight;
                    rows++;
                }

                bool hidden = _hiddenKeys.Contains(s.Key);
                bool highlighted = IsHighlighted(s);
                Color lineColor = hidden ? Color.FromArgb(80, s.Color) : s.Color;
                Color textColor = hidden ? Color.FromArgb(105, _dimTextColor) : (highlighted ? _textColor : _dimTextColor);

                using var pen = new Pen(lineColor, hidden ? Math.Max(1f, SF(1)) : Math.Max(1f, SF(2)));
                if (hidden) pen.DashStyle = DashStyle.Dot;
                g.DrawLine(pen, x, y + S(9), x + S(18), y + S(9));

                var textRect = new RectangleF(x + S(24), y - S(4), itemWidth - S(28), rowHeight);
                using var brush = new SolidBrush(textColor);
                using var format = new StringFormat
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(s.Label, Font, brush, textRect, format);

                if (highlighted)
                {
                    using var border = new Pen(Color.FromArgb(120, s.Color), 1);
                    g.DrawRectangle(border, new Rectangle(x - S(4), y - S(3), itemWidth, rowHeight - S(2)));
                }

                _legendHits.Add(new LegendHit { Key = s.Key, Bounds = new Rectangle(x - S(6), y - S(5), itemWidth + S(4), rowHeight) });
                x += itemWidth + S(6);
            }

            _legendRows = Math.Max(1, rows);
        }

        private void DrawGrid(Graphics g, Rectangle plot, float minValue, float maxValue, DateTime minTime, DateTime maxTime)
        {
            using var gridPen = new Pen(_gridColor, 1);
            using var axisPen = new Pen(_axisColor, 1);
            using var textBrush = new SolidBrush(_dimTextColor);

            g.DrawRectangle(axisPen, plot);

            int ySteps = plot.Height < S(180) ? 3 : 4;
            for (int i = 0; i <= ySteps; i++)
            {
                float y = plot.Top + plot.Height * i / (float)ySteps;
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);

                float value = maxValue - (maxValue - minValue) * i / ySteps;
                string text = FormatAxisValue(value);
                var size = g.MeasureString(text, Font);
                g.DrawString(text, Font, textBrush, plot.Left - size.Width - S(6), y - size.Height / 2);
            }

            int xSteps = plot.Width < S(360) ? 2 : 4;
            for (int i = 0; i <= xSteps; i++)
            {
                float x = plot.Left + plot.Width * i / (float)xSteps;
                g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);

                DateTime t = minTime.AddSeconds((maxTime - minTime).TotalSeconds * i / xSteps);
                string label = FormatTimeLabel(t, maxTime - minTime);
                var size = g.MeasureString(label, Font);
                g.DrawString(label, Font, textBrush, x - size.Width / 2, plot.Bottom + S(8));
            }
        }

        private void DrawThreshold(Graphics g, Rectangle plot, float minValue, float maxValue)
        {
            if (!Threshold.HasValue || Threshold.Value < minValue || Threshold.Value > maxValue) return;

            float y = ValueToY(Threshold.Value, plot, minValue, maxValue);
            using var pen = new Pen(Color.FromArgb(210, 210, 75, 75), Math.Max(1f, SF(1))) { DashStyle = DashStyle.Dash };
            g.DrawLine(pen, plot.Left, y, plot.Right, y);
        }

        private void DrawLines(Graphics g, Rectangle plot, float minValue, float maxValue, DateTime minTime, DateTime maxTime)
        {
            bool hasHighlight = !string.IsNullOrEmpty(_highlightKey);
            var orderedSeries = GetVisibleSeries()
                .OrderBy(s => IsHighlighted(s) ? 1 : 0)
                .ToList();

            foreach (var s in orderedSeries)
            {
                if (s.Points.Count == 0) continue;

                var points = s.Points
                    .OrderBy(p => p.Time)
                    .Select(p => new PointF(TimeToX(p.Time, plot, minTime, maxTime), ValueToY(p.Avg, plot, minValue, maxValue)))
                    .ToArray();

                bool highlighted = IsHighlighted(s);
                Color color = hasHighlight && !highlighted ? Color.FromArgb(95, s.Color) : s.Color;
                float width = highlighted ? Math.Max(2f, SF(3)) : Math.Max(1f, SF(2));

                if (points.Length == 1)
                {
                    using var brush = new SolidBrush(color);
                    float r = highlighted ? SF(4) : SF(3);
                    g.FillEllipse(brush, points[0].X - r, points[0].Y - r, r * 2, r * 2);
                }
                else
                {
                    using var pen = new Pen(color, width);
                    g.DrawLines(pen, points);
                }
            }
        }

        private void DrawHover(Graphics g, Rectangle plot, float minValue, float maxValue, DateTime minTime, DateTime maxTime)
        {
            if (!_hoverTime.HasValue) return;

            float x = TimeToX(_hoverTime.Value, plot, minTime, maxTime);
            using var linePen = new Pen(Color.FromArgb(130, 220, 220, 220), 1);
            g.DrawLine(linePen, x, plot.Top, x, plot.Bottom);

            var rows = new List<string> { _hoverTime.Value.ToString("MM-dd HH:mm") };
            foreach (var s in GetVisibleSeries())
            {
                var p = s.Points.FirstOrDefault(item => item.Time == _hoverTime.Value);
                if (p == null) continue;
                rows.Add($"{s.Label}: {FormatValue(p.Avg, s.Unit)}");
            }
            if (rows.Count <= 1) return;

            int rowHeight = Font.Height + S(6);
            float width = rows.Max(r => g.MeasureString(r, Font).Width) + S(18);
            float height = rows.Count * rowHeight + S(10);
            float left = Math.Min(Math.Max(plot.Left + S(8), x + S(12)), plot.Right - width - S(8));
            float top = plot.Top + S(8);

            using var path = RoundedRect(new RectangleF(left, top, width, height), S(5));
            using var bg = new SolidBrush(_tooltipBack);
            using var border = new Pen(Color.FromArgb(95, 95, 95), 1);
            g.FillPath(bg, path);
            g.DrawPath(border, path);

            using var brush = new SolidBrush(_textColor);
            for (int i = 0; i < rows.Count; i++)
            {
                g.DrawString(rows[i], Font, brush, left + S(9), top + S(6) + i * rowHeight);
            }
        }

        private (float Min, float Max) GetValueRange()
        {
            var values = GetVisibleSeries().SelectMany(s => s.Points.SelectMany(p => new[] { p.Min, p.Avg, p.Max })).ToList();
            if (Threshold.HasValue) values.Add(Threshold.Value);
            if (values.Count == 0) return (0, 100);

            float min = values.Min();
            float max = values.Max();
            if (Math.Abs(max - min) < 0.01f)
            {
                max += 1;
                min -= 1;
            }

            float padding = Math.Max(1, (max - min) * 0.12f);
            min = Math.Max(0, min - padding);
            max += padding;
            return (min, max);
        }

        private List<DateTime> GetAllTimes()
        {
            return GetVisibleSeries()
                .SelectMany(s => s.Points.Select(p => p.Time))
                .Distinct()
                .OrderBy(t => t)
                .ToList();
        }

        private List<TrendChartSeries> GetVisibleSeries()
        {
            return _series.Where(s => !_hiddenKeys.Contains(s.Key)).ToList();
        }

        private bool IsHighlighted(TrendChartSeries s)
        {
            return _highlightKey != null && s.Key.Equals(_highlightKey, StringComparison.OrdinalIgnoreCase);
        }

        private string? HitTestLegend(Point location)
        {
            foreach (var hit in _legendHits)
            {
                if (hit.Bounds.Contains(location)) return hit.Key;
            }
            return null;
        }

        private static float TimeToX(DateTime time, Rectangle plot, DateTime minTime, DateTime maxTime)
        {
            double span = Math.Max(1, (maxTime - minTime).TotalSeconds);
            double pos = (time - minTime).TotalSeconds / span;
            return plot.Left + (float)(plot.Width * pos);
        }

        private static float ValueToY(float value, Rectangle plot, float minValue, float maxValue)
        {
            float span = Math.Max(0.0001f, maxValue - minValue);
            float pos = (value - minValue) / span;
            return plot.Bottom - plot.Height * pos;
        }

        private static string FormatAxisValue(float value)
        {
            if (value >= 100) return value.ToString("0");
            if (value >= 10) return value.ToString("0.#");
            return value.ToString("0.##");
        }

        private static string FormatValue(float value, string unit)
        {
            string val = value >= 100 ? value.ToString("0") : value.ToString("0.#");
            return string.IsNullOrEmpty(unit) ? val : val + unit;
        }

        private static string FormatTimeLabel(DateTime time, TimeSpan span)
        {
            return span.TotalDays >= 2 ? time.ToString("MM-dd HH:mm") : time.ToString("HH:mm");
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private int S(int pixel)
        {
            return Math.Max(1, (int)Math.Round(pixel * (DeviceDpi > 0 ? DeviceDpi : 96) / 96f));
        }

        private float SF(float pixel)
        {
            return pixel * (DeviceDpi > 0 ? DeviceDpi : 96) / 96f;
        }

        private sealed class LegendHit
        {
            public string Key { get; set; } = "";
            public Rectangle Bounds { get; set; }
        }
    }
}
