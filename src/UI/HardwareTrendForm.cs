using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor
{
    public class HardwareTrendForm : Form
    {
        private enum TrendCategory { Temperature, Load, Power, Frequency, Fps }
        private enum TrendRange { Hour1, Day1, Day7 }

        private readonly Settings _cfg;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Dictionary<TrendCategory, Button> _categoryButtons = new Dictionary<TrendCategory, Button>();
        private readonly Dictionary<TrendRange, Button> _rangeButtons = new Dictionary<TrendRange, Button>();
        private readonly TrendChartControl _chart;
        private readonly FlowLayoutPanel _summaryPanel;
        private readonly DataGridView _grid;

        private TrendCategory _category = TrendCategory.Temperature;
        private TrendRange _range = TrendRange.Hour1;
        private List<TrendChartSeries> _currentSeries = new List<TrendChartSeries>();
        private string? _selectedSeriesKey;
        private bool _fillingGrid;
        private float _scale = 1.0f;

        private readonly Color C_Back = Color.FromArgb(32, 32, 32);
        private readonly Color C_Panel = Color.FromArgb(45, 45, 45);
        private readonly Color C_GridBack = Color.FromArgb(38, 38, 38);
        private readonly Color C_GridLine = Color.FromArgb(60, 60, 60);
        private readonly Color C_TextMain = Color.FromArgb(230, 230, 230);
        private readonly Color C_TextDim = Color.FromArgb(160, 160, 160);
        private readonly Color C_Header = Color.FromArgb(50, 50, 50);
        private readonly Color C_Accent = Color.FromArgb(0, 122, 204);

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED，减少复杂窗口首次绘制闪烁
                return cp;
            }
        }

        protected override void OnResizeBegin(EventArgs e)
        {
            SuspendLayout();
            base.OnResizeBegin(e);
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            ResumeLayout(true);
        }

        public HardwareTrendForm(Settings cfg)
        {
            _cfg = cfg;
            DoubleBuffered = true;

            using (Graphics g = CreateGraphics())
            {
                _scale = g.DpiX / 96.0f;
            }

            Text = LanguageManager.T("Menu.MonitorHistory");
            Size = new Size(S(980), S(680));
            MinimumSize = new Size(S(900), S(620));
            StartPosition = FormStartPosition.Manual;
            BackColor = C_Back;
            ForeColor = C_TextMain;
            Font = new Font("Microsoft YaHei UI", 9F);

            SuspendLayout();

            var toolbar = CreateToolbar();
            Controls.Add(toolbar);

            _summaryPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = S(62),
                BackColor = C_Panel,
                Padding = new Padding(S(16), S(8), S(12), S(6)),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            Controls.Add(_summaryPanel);

            _grid = CreateGrid();
            _grid.SelectionChanged += Grid_SelectionChanged;
            Controls.Add(_grid);

            _chart = new TrendChartControl
            {
                Dock = DockStyle.Fill,
                BackColor = C_GridBack,
                Font = Font,
                EmptyText = T("暂无历史数据", "No history")
            };
            Controls.Add(_chart);
            _chart.BringToFront();

            ResumeLayout(true);

            LoadData();

            _timer = new System.Windows.Forms.Timer { Interval = 15000 };
            _timer.Tick += (_, __) => LoadData();
            _timer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            CenterOnDesktop();
        }

        private void CenterOnDesktop()
        {
            // 使用桌面工作区居中，避开任务栏，同时避免默认 CenterScreen 在部分 DPI/多屏场景下偏移。
            Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
            int x = workingArea.Left + (workingArea.Width - Width) / 2;
            int y = workingArea.Top + (workingArea.Height - Height) / 2;
            Location = new Point(Math.Max(workingArea.Left, x), Math.Max(workingArea.Top, y));
        }

        private Control CreateToolbar()
        {
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = S(58),
                BackColor = C_Back,
                Padding = new Padding(S(16), S(12), S(12), S(8)),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };

            AddLabel(toolbar, T("分类", "Group"));
            AddCategoryButton(toolbar, TrendCategory.Temperature, T("温度", "Temp"));
            AddCategoryButton(toolbar, TrendCategory.Load, T("负载", "Load"));
            AddCategoryButton(toolbar, TrendCategory.Power, T("功耗", "Power"));
            AddCategoryButton(toolbar, TrendCategory.Frequency, T("频率", "Clock"));
            AddCategoryButton(toolbar, TrendCategory.Fps, "FPS");

            AddSpacer(toolbar, S(18));
            AddLabel(toolbar, T("范围", "Range"));
            AddRangeButton(toolbar, TrendRange.Hour1, T("1小时", "1h"));
            AddRangeButton(toolbar, TrendRange.Day1, T("24小时", "24h"));
            AddRangeButton(toolbar, TrendRange.Day7, T("7天", "7d"));

            AddSpacer(toolbar, S(18));
            string exportText = T("导出 CSV", "Export CSV");
            var export = CreateButton(exportText, GetTextButtonWidth(exportText, 96));
            export.Click += (_, __) => ExportCsv();
            toolbar.Controls.Add(export);

            return toolbar;
        }

        private DataGridView CreateGrid()
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Bottom,
                Height = S(170),
                BackgroundColor = C_GridBack,
                BorderStyle = BorderStyle.None,
                EnableHeadersVisualStyles = false,
                GridColor = C_GridLine,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ColumnHeadersHeight = S(38),
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                AutoGenerateColumns = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };

            grid.ColumnHeadersDefaultCellStyle.BackColor = C_Header;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Gainsboro;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9, FontStyle.Regular);
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.DefaultCellStyle.BackColor = C_GridBack;
            grid.DefaultCellStyle.ForeColor = C_TextMain;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(70, 70, 70);
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.DefaultCellStyle.Font = new Font("Consolas", 10);
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.RowTemplate.Height = S(32);

            AddTextColumn(grid, "Name", T("指标", "Metric"), 140, DataGridViewContentAlignment.MiddleLeft);
            AddTextColumn(grid, "Current", T("最新", "Latest"), 85);
            AddTextColumn(grid, "Avg", T("平均", "Average"), 85);
            AddTextColumn(grid, "Max", T("最高", "Max"), 85);
            AddTextColumn(grid, "Min", T("最低", "Min"), 85);
            AddTextColumn(grid, "Count", T("样本", "Samples"), 70);

            return grid;
        }

        private void LoadData()
        {
            SetActiveButtons();

            DateTime from = DateTime.Now - GetRangeSpan();
            int bucketMinutes = GetBucketMinutes();
            var raw = HardwareHistoryLogger.GetPoints(from);
            string[] keys = GetCurrentKeys();

            _currentSeries = keys
                .Select((key, index) => BuildSeries(raw, key, bucketMinutes, index))
                .Where(s => s.Points.Count > 0)
                .ToList();

            _chart.Title = GetCategoryTitle();
            _chart.Threshold = _category == TrendCategory.Temperature ? _cfg.AlertTempThreshold : null;
            _chart.EmptyText = HasEnabledMetricInCurrentCategory()
                ? T("暂无历史数据", "No history")
                : T("当前分类没有开启的监控项", "No enabled metrics in this group");
            _chart.SetSeries(_currentSeries);

            FillSummary();
            FillGrid();
        }

        private TrendChartSeries BuildSeries(List<HardwareHistoryPoint> raw, string key, int bucketMinutes, int index)
        {
            var points = raw
                .Where(p => p.Values.ContainsKey(key))
                .Select(p => new { p.Time, Value = p.Values[key] })
                .GroupBy(x => FloorBucket(x.Time, bucketMinutes))
                .Select(g =>
                {
                    int count = g.Sum(x => Math.Max(1, x.Value.Count));
                    float avg = count > 0
                        ? g.Sum(x => NormalizeValue(key, x.Value.Avg) * Math.Max(1, x.Value.Count)) / count
                        : 0;

                    return new TrendChartPoint
                    {
                        Time = g.Key,
                        Avg = avg,
                        Max = g.Max(x => NormalizeValue(key, x.Value.Max)),
                        Min = g.Min(x => NormalizeValue(key, x.Value.Min)),
                        Count = count
                    };
                })
                .OrderBy(p => p.Time)
                .ToList();

            return new TrendChartSeries
            {
                Key = key,
                Label = GetMetricLabel(key),
                Unit = GetUnit(key),
                Color = GetSeriesColor(index),
                Points = points
            };
        }

        private void FillSummary()
        {
            _summaryPanel.SuspendLayout();
            _summaryPanel.Controls.Clear();

            if (_currentSeries.Count == 0)
            {
                AddSummaryText(_chart.EmptyText);
                _summaryPanel.ResumeLayout();
                return;
            }

            foreach (var s in _currentSeries)
            {
                float latest = s.Points.Last().Avg;
                float max = s.Points.Max(p => p.Max);
                AddSummaryText($"{s.Label}  {FormatMetric(latest, s.Unit)} / {FormatMetric(max, s.Unit)}", s.Color);
            }

            _summaryPanel.ResumeLayout();
        }

        private void FillGrid()
        {
            _fillingGrid = true;
            string? keyToRestore = _selectedSeriesKey;
            int selectedRowIndex = -1;

            try
            {
                _grid.Rows.Clear();

                foreach (var s in _currentSeries)
                {
                    int count = s.Points.Sum(p => Math.Max(1, p.Count));
                    float avg = count > 0 ? s.Points.Sum(p => p.Avg * Math.Max(1, p.Count)) / count : 0;
                    int idx = _grid.Rows.Add(
                        s.Label,
                        FormatMetric(s.Points.Last().Avg, s.Unit),
                        FormatMetric(avg, s.Unit),
                        FormatMetric(s.Points.Max(p => p.Max), s.Unit),
                        FormatMetric(s.Points.Min(p => p.Min), s.Unit),
                        count.ToString());

                    _grid.Rows[idx].Tag = s.Key;
                    _grid.Rows[idx].Cells["Name"].Style.ForeColor = s.Color;

                    if (!string.IsNullOrEmpty(keyToRestore) && s.Key.Equals(keyToRestore, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedRowIndex = idx;
                    }
                }

                _grid.ClearSelection();
                if (selectedRowIndex >= 0)
                {
                    _grid.Rows[selectedRowIndex].Selected = true;
                    _grid.CurrentCell = _grid.Rows[selectedRowIndex].Cells["Name"];
                }
                else
                {
                    _selectedSeriesKey = null;
                }
            }
            finally
            {
                _fillingGrid = false;
            }

            _chart.SetHighlightedSeries(selectedRowIndex >= 0 ? keyToRestore : null);
        }

        private void Grid_SelectionChanged(object? sender, EventArgs e)
        {
            if (_fillingGrid) return;

            var row = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0] : _grid.CurrentRow;
            if (row?.Tag is string key)
            {
                _selectedSeriesKey = key;
                _chart.SetHighlightedSeries(key);
            }
            else
            {
                _selectedSeriesKey = null;
                _chart.SetHighlightedSeries(null);
            }
        }

        private void ExportCsv()
        {
            if (_currentSeries.Count == 0) return;

            using var dialog = new SaveFileDialog
            {
                Title = T("导出硬件趋势", "Export Hardware Trends"),
                Filter = "CSV (*.csv)|*.csv",
                FileName = $"HardwareTrend_{DateTime.Now:yyyyMMdd_HHmm}.csv"
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            var sb = new StringBuilder();
            sb.AppendLine("Time,Key,Name,Average,Max,Min,Unit,Samples");
            foreach (var s in _currentSeries)
            {
                foreach (var p in s.Points)
                {
                    sb.Append(p.Time.ToString("yyyy-MM-dd HH:mm")).Append(',')
                        .Append(EscapeCsv(s.Key)).Append(',')
                        .Append(EscapeCsv(s.Label)).Append(',')
                        .Append(p.Avg.ToString("0.###")).Append(',')
                        .Append(p.Max.ToString("0.###")).Append(',')
                        .Append(p.Min.ToString("0.###")).Append(',')
                        .Append(EscapeCsv(s.Unit)).Append(',')
                        .Append(p.Count)
                        .AppendLine();
                }
            }

            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        private string[] GetCurrentKeys()
        {
            string[] baseKeys = _category switch
            {
                TrendCategory.Temperature => HardwareHistoryLogger.TemperatureKeys,
                TrendCategory.Load => HardwareHistoryLogger.LoadKeys,
                TrendCategory.Power => HardwareHistoryLogger.PowerKeys,
                TrendCategory.Frequency => HardwareHistoryLogger.FrequencyKeys,
                TrendCategory.Fps => HardwareHistoryLogger.FpsKeys,
                _ => HardwareHistoryLogger.TemperatureKeys
            };

            // ★★★ [多显卡] 补上带序号的显卡 Key ("GPU.0.Temp" / "GPU.1.Temp") ★★★
            // 只加当前分类下、且用户确实开启了的项。
            var indexed = new List<string>();
            foreach (var item in _cfg.MonitorItems)
            {
                if (!(item.VisibleInPanel || item.VisibleInTaskbar)) continue;
                if (!GpuKey.TryParse(item.Key, out _, out string metric)) continue;

                string baseKey = GpuKey.BaseKey(metric);
                bool inCategory = baseKeys.Any(k =>
                    string.Equals(k, baseKey, StringComparison.OrdinalIgnoreCase));
                if (!inCategory) continue;

                if (!indexed.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
                    indexed.Add(item.Key);
            }

            // ★★★ [多显卡] 旧格式的显卡 Key ("GPU.Temp") 在迁移后已没有对应的监控项,
            // 继续显示只会留下一条"冻结在升级前最后一个点"的旧曲线, 这里剔除。 ★★★
            // 注意: 不要写成 .Where(i => GpuKey.TryParse(i.Key, out _, out string m)).Select(i => GpuKey.BaseKey(m))
            // —— `m` 声明在 Where 的 lambda 里, 下一个 lambda 根本看不到它 (CS0103 编译错误)。
            var indexedBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _cfg.MonitorItems)
            {
                if (GpuKey.TryParse(item.Key, out _, out string metric))
                    indexedBases.Add(GpuKey.BaseKey(metric));
            }


            var result = new List<string>();
            foreach (var k in baseKeys)
            {
                if (k.StartsWith("GPU.", StringComparison.OrdinalIgnoreCase) &&
                    indexedBases.Contains(k)) continue;
                result.Add(k);
            }
            result.AddRange(indexed);

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private bool HasEnabledMetricInCurrentCategory()
        {
            var keys = new HashSet<string>(GetCurrentKeys(), StringComparer.OrdinalIgnoreCase);
            return _cfg.MonitorItems.Any(item =>
                keys.Contains(item.Key) &&
                (item.VisibleInPanel || item.VisibleInTaskbar));
        }

        private string GetCategoryTitle()
        {
            return _category switch
            {
                TrendCategory.Temperature => T("温度趋势", "Temperature Trend"),
                TrendCategory.Load => T("负载趋势", "Load Trend"),
                TrendCategory.Power => T("功耗趋势", "Power Trend"),
                TrendCategory.Frequency => T("频率趋势", "Clock Trend"),
                TrendCategory.Fps => T("帧率趋势", "FPS Trend"),
                _ => T("硬件趋势", "Hardware Trend")
            };
        }

        private TimeSpan GetRangeSpan()
        {
            return _range switch
            {
                TrendRange.Hour1 => TimeSpan.FromHours(1),
                TrendRange.Day1 => TimeSpan.FromDays(1),
                TrendRange.Day7 => TimeSpan.FromDays(7),
                _ => TimeSpan.FromHours(1)
            };
        }

        private int GetBucketMinutes()
        {
            return _range switch
            {
                TrendRange.Hour1 => 1,
                TrendRange.Day1 => 5,
                TrendRange.Day7 => 60,
                _ => 1
            };
        }

        private string GetMetricLabel(string key)
        {
            var item = _cfg.MonitorItems.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            string label = item != null ? MetricLabelResolver.ResolveLabel(item) : "";
            if (string.IsNullOrWhiteSpace(label)) label = LanguageManager.T("Items." + GpuKey.ToBaseKey(key));
            return string.IsNullOrWhiteSpace(label) ? key : label;
        }

        private string GetUnit(string key)
        {
            var type = MetricUtils.GetType(key);
            return type switch
            {
                MetricType.Temperature => "°C",
                MetricType.Percent => "%",
                MetricType.Memory => "%",
                MetricType.Frequency => "GHz",
                MetricType.Power => "W",
                MetricType.FPS => "FPS",
                _ => ""
            };
        }

        private static float NormalizeValue(string key, float value)
        {
            return MetricUtils.GetType(key) == MetricType.Frequency ? value / 1000f : value;
        }

        private static DateTime FloorBucket(DateTime time, int bucketMinutes)
        {
            long ticks = TimeSpan.FromMinutes(bucketMinutes).Ticks;
            return new DateTime(time.Ticks / ticks * ticks);
        }

        private string FormatMetric(float value, string unit)
        {
            string text = unit == "GHz"
                ? value.ToString("0.0")
                : value >= 100 ? value.ToString("0") : value.ToString("0.#");

            return unit == "FPS" ? $"{text} FPS" : text + unit;
        }

        private Button CreateButton(string text, int width)
        {
            var btn = new Button
            {
                Text = text,
                Size = new Size(width, S(30)),
                FlatStyle = FlatStyle.Flat,
                BackColor = C_Back,
                ForeColor = C_TextDim,
                Cursor = Cursors.Hand,
                Font = new Font("Microsoft YaHei UI", 9)
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private void AddCategoryButton(FlowLayoutPanel panel, TrendCategory category, string text)
        {
            var btn = CreateButton(text, GetTextButtonWidth(text, category == TrendCategory.Temperature ? 70 : 64));
            btn.Click += (_, __) =>
            {
                _category = category;
                LoadData();
            };
            _categoryButtons[category] = btn;
            panel.Controls.Add(btn);
        }

        private void AddRangeButton(FlowLayoutPanel panel, TrendRange range, string text)
        {
            var btn = CreateButton(text, GetTextButtonWidth(text, 64));
            btn.Click += (_, __) =>
            {
                _range = range;
                LoadData();
            };
            _rangeButtons[range] = btn;
            panel.Controls.Add(btn);
        }

        private void SetActiveButtons()
        {
            foreach (var kv in _categoryButtons)
            {
                bool active = kv.Key == _category;
                kv.Value.BackColor = active ? C_Accent : C_Back;
                kv.Value.ForeColor = active ? Color.White : C_TextDim;
            }

            foreach (var kv in _rangeButtons)
            {
                bool active = kv.Key == _range;
                kv.Value.BackColor = active ? C_Accent : C_Back;
                kv.Value.ForeColor = active ? Color.White : C_TextDim;
            }
        }

        private void AddSummaryText(string text, Color? accent = null)
        {
            var box = new Panel
            {
                Width = GetSummaryBoxWidth(),
                Height = S(38),
                BackColor = C_Panel,
                Margin = new Padding(0, 0, S(10), 0)
            };

            var line = new Panel
            {
                Width = S(3),
                Dock = DockStyle.Left,
                BackColor = accent ?? C_Accent
            };

            var label = new Label
            {
                Dock = DockStyle.Fill,
                Text = text,
                ForeColor = C_TextMain,
                Font = new Font("Microsoft YaHei UI", 8.5F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(S(8), 0, S(4), 0),
                AutoEllipsis = true
            };

            box.Controls.Add(label);
            box.Controls.Add(line);
            _summaryPanel.Controls.Add(box);
        }

        private void AddLabel(FlowLayoutPanel panel, string text)
        {
            int width = Math.Max(S(42), TextRenderer.MeasureText(text, Font).Width + S(8));
            panel.Controls.Add(new Label
            {
                Text = text,
                AutoSize = false,
                Size = new Size(width, S(30)),
                ForeColor = C_TextDim,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, S(4), 0)
            });
        }

        private int GetTextButtonWidth(string text, int minWidth)
        {
            return Math.Max(S(minWidth), TextRenderer.MeasureText(text, Font).Width + S(24));
        }

        private int GetSummaryBoxWidth()
        {
            int count = Math.Max(1, _currentSeries.Count);
            int available = Math.Max(S(240), _summaryPanel.ClientSize.Width - _summaryPanel.Padding.Horizontal - S(10) * Math.Max(0, count - 1));
            int width = available / count;
            return Math.Max(S(150), Math.Min(S(230), width));
        }

        private void AddSpacer(FlowLayoutPanel panel, int width)
        {
            panel.Controls.Add(new Label { AutoSize = false, Width = width, Height = S(30) });
        }

        private static void AddTextColumn(DataGridView grid, string name, string header, float weight, DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleRight)
        {
            var col = new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                FillWeight = weight,
                SortMode = DataGridViewColumnSortMode.Automatic
            };
            col.DefaultCellStyle.Alignment = align;
            grid.Columns.Add(col);
        }

        private static string EscapeCsv(string text)
        {
            if (text.Contains('"') || text.Contains(',') || text.Contains('\n'))
            {
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            }
            return text;
        }

        private Color GetSeriesColor(int index)
        {
            Color[] colors =
            {
                Color.FromArgb(80, 160, 255),
                Color.FromArgb(80, 220, 120),
                Color.FromArgb(245, 196, 80),
                Color.FromArgb(230, 95, 95),
                Color.FromArgb(180, 140, 255)
            };
            return colors[index % colors.Length];
        }

        private string T(string zh, string en)
        {
            return (_cfg.Language ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
        }

        private int S(int pixel) => (int)(pixel * _scale);

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            _timer.Dispose();
            base.OnFormClosed(e);
        }
    }
}
