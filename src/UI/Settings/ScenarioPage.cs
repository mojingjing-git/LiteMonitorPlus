using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Scenarios;

namespace LiteMonitor.src.UI.SettingsPage
{
    /// <summary>
    /// S05 场景管理页：列表、从当前创建、重命名、删除、差异预览与手动应用。
    /// 应用动作由 SettingsForm 注入，页面不直接改 live 设置。
    /// </summary>
    public sealed class ScenarioPage : SettingsPageBase
    {
        private readonly ListView _list;
        private readonly Label _status;
        private readonly Button _apply;
        private readonly Button _rename;
        private readonly Button _delete;
        private readonly ScenarioService _service = new();

        public Func<ScenarioProfile, string?>? ApplyHandler { get; set; }

        public ScenarioPage()
        {
            BackColor = GlobalBackColor;
            Padding = new Padding(12);

            var top = new Panel { Dock = DockStyle.Top, Height = 40 };
            var title = new Label
            {
                Text = "Scenarios",
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
            var refresh = MakeButton("Refresh", 86);
            var create = MakeButton("New", 76);
            var preview = MakeButton("Preview", 86);
            _apply = MakeButton("Apply", 76);
            _rename = MakeButton("Rename", 84);
            _delete = MakeButton("Delete", 80);
            refresh.Click += (_, _) => Reload();
            create.Click += (_, _) => CreateProfile();
            preview.Click += (_, _) => PreviewProfile();
            _apply.Click += (_, _) => ApplyProfile();
            _rename.Click += (_, _) => RenameProfile();
            _delete.Click += (_, _) => DeleteProfile();
            top.Controls.Add(_status);
            top.Controls.Add(_delete);
            top.Controls.Add(_rename);
            top.Controls.Add(_apply);
            top.Controls.Add(preview);
            top.Controls.Add(create);
            top.Controls.Add(refresh);
            top.Controls.Add(title);
            Controls.Add(top);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
            };
            _list.Columns.Add("Name", 260);
            _list.Columns.Add("Revision", 80, HorizontalAlignment.Right);
            _list.Columns.Add("Updated (UTC)", 180);
            _list.SelectedIndexChanged += (_, _) => UpdateButtons();
            _list.DoubleClick += (_, _) => ApplyProfile();
            Controls.Add(_list);
            _list.BringToFront();
        }

        public override void OnShow()
        {
            base.OnShow();
            Reload();
        }

        private static Button MakeButton(string text, int width)
            => new Button { Text = text, Dock = DockStyle.Right, Width = width };

        private ScenarioProfile? Selected =>
            _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as ScenarioProfile : null;

        private void Reload()
        {
            var selectedId = Selected?.Id;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var profile in _service.List().OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var item = new ListViewItem(profile.Name) { Tag = profile };
                    item.SubItems.Add(profile.DefinitionRevision.ToString());
                    item.SubItems.Add(profile.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                    _list.Items.Add(item);
                    if (string.Equals(selectedId, profile.Id, StringComparison.Ordinal))
                        item.Selected = true;
                }
            }
            finally
            {
                _list.EndUpdate();
            }

            _status.Text = _service.Store.LastLoadWarning ?? $"{_list.Items.Count} scene(s)";
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool has = Selected != null;
            _apply.Enabled = has && ApplyHandler != null;
            _rename.Enabled = has;
            _delete.Enabled = has;
        }

        private void CreateProfile()
        {
            string? name = PromptName("New scenario", "Name");
            if (name == null) return;
            var result = _service.CreateFromCurrent(name, Config);
            if (!result.Success) { ShowError(result.Error); return; }
            Reload();
            _status.Text = "Created: " + result.Profile!.Name;
        }

        private void RenameProfile()
        {
            var profile = Selected;
            if (profile == null) return;
            string? name = PromptName("Rename scenario", profile.Name);
            if (name == null) return;
            var result = _service.Rename(profile, name);
            if (!result.Success) { ShowError(result.Error); return; }
            Reload();
        }

        private void DeleteProfile()
        {
            var profile = Selected;
            if (profile == null) return;
            if (MessageBox.Show(this, $"Delete scenario '{profile.Name}'?", "Confirm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            var result = _service.Delete(profile);
            if (!result.Success) { ShowError(result.Error); return; }
            Reload();
        }

        private void PreviewProfile()
        {
            var profile = Selected;
            if (profile == null) return;
            var preflight = ScenarioPreflight.Inspect(profile, Config, ThemeExists);
            if (!preflight.CanApply)
            {
                ShowError("Cannot apply:\n" + string.Join("\n", preflight.BlockingIssues.Select(i => i.Code + ": " + i.Message)));
                return;
            }

            string text = preflight.Changes.Count == 0
                ? "No changes."
                : string.Join("\n", preflight.Changes.Select(c => $"{c.Field}: {c.CurrentValue} → {c.TargetValue}"));
            MessageBox.Show(this, text, "Scenario preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ApplyProfile()
        {
            var profile = Selected;
            if (profile == null || ApplyHandler == null) return;
            string? error = ApplyHandler(profile);
            if (error == null)
            {
                Reload();
                _status.Text = "Applied: " + profile.Name;
            }
            else
            {
                ShowError(error);
            }
        }

        private static bool ThemeExists(string name)
            => !string.IsNullOrWhiteSpace(name)
               && File.Exists(Path.Combine(ThemeManager.ThemeDir, name + ".json"));

        private void ShowError(string? message)
            => MessageBox.Show(this, message ?? "Operation failed.", "Scenarios",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

        private string? PromptName(string title, string initial)
        {
            using var dialog = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(360, 120),
                ShowInTaskbar = false,
            };
            var label = new Label { Text = "Name", Left = 12, Top = 14, Width = 60 };
            var text = new TextBox { Left = 76, Top = 10, Width = 268, Text = initial };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 188, Top = 54, Width = 74 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 270, Top = 54, Width = 74 };
            dialog.Controls.AddRange(new Control[] { label, text, ok, cancel });
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            return dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(text.Text)
                ? text.Text.Trim()
                : null;
        }
    }
}

