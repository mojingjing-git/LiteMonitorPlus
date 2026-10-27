using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.Diagnostics;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    /// <summary>
    /// D03 诊断窗口：模块快照、事件时间线、详情、导出四个区域。
    /// 只读；1 Hz 刷新，不触发额外采样。
    /// </summary>
    public sealed class DiagnosticPage : SettingsPageBase
    {
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private readonly Label _status;
        private readonly TextBox _filter;
        private readonly ListView _modules;
        private readonly ListView _events;
        private readonly TextBox _details;
        private readonly DiagnosticPackageService _packageService = new();
        private bool _refreshing;

        public DiagnosticPage()
        {
            BackColor = GlobalBackColor;
            Padding = new Padding(12);

            var top = new Panel { Dock = DockStyle.Top, Height = 38 };
            var title = new Label
            {
                Text = "Diagnostics",
                Dock = DockStyle.Left,
                Width = 120,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font(Font, FontStyle.Bold),
            };
            _status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray,
            };
            var refresh = new Button { Text = "Refresh", Dock = DockStyle.Right, Width = 86 };
            var export = new Button { Text = "Export…", Dock = DockStyle.Right, Width = 92 };
            var rescan = new Button { Text = "Rescan", Dock = DockStyle.Right, Width = 86 };
            refresh.Click += (_, _) => RefreshView();
            export.Click += (_, _) => ExportPackage();
            rescan.Click += async (_, _) => await RescanHardwareAsync();
            top.Controls.Add(_status);
            top.Controls.Add(export);
            top.Controls.Add(rescan);
            top.Controls.Add(refresh);
            top.Controls.Add(title);
            Controls.Add(top);

            var filterBar = new Panel { Dock = DockStyle.Top, Height = 32 };
            var filterLabel = new Label
            {
                Text = "Filter",
                Dock = DockStyle.Left,
                Width = 52,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            _filter = new TextBox
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 3, 0, 3),
                PlaceholderText = "module, state, reason or summary",
            };
            _filter.TextChanged += (_, _) => RefreshView();
            _filter.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                _filter.Clear();
                e.SuppressKeyPress = true;
            };
            filterBar.Controls.Add(_filter);
            filterBar.Controls.Add(filterLabel);
            Controls.Add(filterBar);

            var main = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 250,
            };
            var upper = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 560,
            };

            _modules = CreateList(new[] { "Module", "State", "Reason", "Summary" });
            _modules.SelectedIndexChanged += (_, _) => ShowSelectedModule();
            _details = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = GlobalBackColor,
                ForeColor = Color.Black,
            };
            upper.Panel1.Controls.Add(_modules);
            upper.Panel2.Controls.Add(_details);

            _events = CreateList(new[] { "Time", "Severity", "Module", "Reason", "Count" });
            main.Panel1.Controls.Add(upper);
            main.Panel2.Controls.Add(_events);
            Controls.Add(main);

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _refreshTimer.Tick += (_, _) => RefreshView();
        }

        public override void OnShow()
        {
            base.OnShow();
            RefreshView();
            _refreshTimer.Start();
        }

        public override void OnHide()
        {
            _refreshTimer.Stop();
        }

        internal bool IsRefreshTimerRunning => _refreshTimer.Enabled;

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (IsDisposed) return;

            if (Visible)
            {
                RefreshView();
                _refreshTimer.Start();
            }
            else
            {
                _refreshTimer.Stop();
            }
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            // 真实设置页导航用 Controls.Clear 摘下页面，Parent 变 null
            // 而 Visible 可能仍为 true；Parent 为空即停止轮询。
            if (Parent is null && !IsDisposed)
            {
                _refreshTimer.Stop();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private static ListView CreateList(string[] columns)
        {
            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                MultiSelect = false,
                BackColor = GlobalBackColor,
                ForeColor = Color.Black,
            };
            foreach (string column in columns) list.Columns.Add(column, 120);
            return list;
        }

        private void RefreshView()
        {
            if (_refreshing || IsDisposed) return;
            _refreshing = true;
            try
            {
                DiagnosticSnapshot snapshot;
                try
                {
                    snapshot = DiagnosticHub.Current.SnapshotProvider();
                }
                catch (Exception ex)
                {
                    _status.Text = "Snapshot failed: " + ex.GetType().Name;
                    return;
                }

                string filter = _filter.Text.Trim();
                string? selectedModule = _modules.SelectedItems.Count > 0
                    ? _modules.SelectedItems[0].Text
                    : null;
                int moduleCount = 0;
                _modules.BeginUpdate();
                _modules.Items.Clear();
                foreach (DiagnosticModuleSnapshot module in snapshot.Modules)
                {
                    if (!MatchesFilter(filter, module.Module, module.State.ToString(),
                            module.ReasonCode, module.Summary)) continue;
                    var item = new ListViewItem(module.Module) { Tag = module };
                    item.SubItems.Add(module.State.ToString());
                    item.SubItems.Add(module.ReasonCode ?? string.Empty);
                    item.SubItems.Add(module.Summary ?? string.Empty);
                    _modules.Items.Add(item);
                    moduleCount++;
                }
                _modules.EndUpdate();

                if (selectedModule != null)
                {
                    ListViewItem? restored = _modules.Items
                        .Cast<ListViewItem>()
                        .FirstOrDefault(item => string.Equals(item.Text, selectedModule, StringComparison.Ordinal));
                    if (restored != null)
                    {
                        restored.Selected = true;
                        restored.Focused = true;
                        _modules.EnsureVisible(restored.Index);
                        ShowSelectedModule();
                    }
                    else
                    {
                        _details.Clear();
                    }
                }

                IReadOnlyList<DiagnosticEvent> events = DiagnosticHub.Current.Store.Snapshot();
                int eventCount = 0;
                _events.BeginUpdate();
                _events.Items.Clear();
                foreach (DiagnosticEvent evt in events)
                {
                    if (!MatchesFilter(filter, evt.Module, evt.Severity.ToString(),
                            evt.ReasonCode, evt.Context == null ? null : string.Join(" ", evt.Context.Keys))) continue;
                    var item = new ListViewItem(evt.UtcTime.ToLocalTime().ToString("HH:mm:ss"));
                    item.SubItems.Add(evt.Severity.ToString());
                    item.SubItems.Add(evt.Module);
                    item.SubItems.Add(evt.ReasonCode);
                    item.SubItems.Add(evt.RepeatCount.ToString());
                    _events.Items.Add(item);
                    eventCount++;
                }
                _events.EndUpdate();

                _status.Text = $"Captured {snapshot.CapturedAtUtc.ToLocalTime():HH:mm:ss} · " +
                    $"{moduleCount}/{snapshot.Modules.Count} modules · {eventCount}/{events.Count} events · " +
                    $"dropped {DiagnosticHub.Current.Store.DroppedCount}";
            }
            finally
            {
                _refreshing = false;
            }
        }

        private static bool MatchesFilter(string filter, params string?[] fields)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            foreach (string? field in fields)
            {
                if (field != null && field.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void ShowSelectedModule()
        {
            if (_modules.SelectedItems.Count == 0) return;
            if (_modules.SelectedItems[0].Tag is not DiagnosticModuleSnapshot module)
            {
                _details.Text = string.Empty;
                return;
            }

            var lines = new List<string>
            {
                "Module: " + module.Module,
                "State: " + module.State,
                "Reason: " + (module.ReasonCode ?? string.Empty),
                "Summary: " + (module.Summary ?? string.Empty),
                "Observed: " + module.ObservedAtUtc.ToLocalTime().ToString("O"),
                string.Empty,
                "Facts:",
            };
            foreach (KeyValuePair<string, string> pair in module.Facts)
                lines.Add($"  {pair.Key} = {pair.Value}");
            _details.Text = string.Join(Environment.NewLine, lines);
        }

        private async Task RescanHardwareAsync()
        {
            try
            {
                var result = await UI.HardwareClient.RequestFullRescanAsync();
                DiagnosticHub.Current.Record(
                    "sampling",
                    result.Accepted ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning,
                    result.Accepted ? "manual-rescan" : "manual-rescan-rejected",
                    context: new Dictionary<string, string>
                    {
                        ["operationId"] = result.OperationId,
                        ["state"] = result.State.ToString(),
                        ["failureReason"] = result.FailureReason ?? string.Empty,
                    });
                MessageBox.Show(
                    result.Accepted ? $"Rescan requested ({result.State})." : $"Rescan rejected: {result.FailureReason}",
                    "Diagnostics", MessageBoxButtons.OK,
                    result.Accepted ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                RefreshView();
            }
            catch (Exception ex)
            {
                DiagnosticHub.Current.Record("sampling", DiagnosticSeverity.Error, "manual-rescan-failed",
                    context: new Dictionary<string, string> { ["error"] = ex.GetType().Name });
                MessageBox.Show($"Rescan failed: {ex.GetType().Name}", "Diagnostics",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ExportPackage()
        {
            DiagnosticSnapshot snapshot;
            try
            {
                snapshot = DiagnosticHub.Current.SnapshotProvider();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Snapshot failed: " + ex.GetType().Name, "Diagnostics",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            IReadOnlyList<DiagnosticEvent> events = DiagnosticHub.Current.Store.Snapshot();
            var input = new DiagnosticPackageInput(
                snapshot,
                events,
                Application.ProductVersion,
                DiagnosticHub.Current.Store.DroppedCount,
                DiagnosticHub.Current.Store.RejectedCount);
            DiagnosticPackagePreview preview = _packageService.CreatePreview(input);

            using var dialog = new SaveFileDialog
            {
                Title = "Export diagnostics",
                FileName = preview.FileName,
                Filter = "ZIP package (*.zip)|*.zip",
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

            DiagnosticPackageExportResult result = _packageService.Export(dialog.FileName, input);
            MessageBox.Show(
                result.Success ? $"Saved {result.Bytes} bytes." : $"Export failed: {result.ErrorCode}",
                "Diagnostics",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }
}
