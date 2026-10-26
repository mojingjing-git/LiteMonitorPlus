using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// 主题工作室主窗体（重做计划 TE02 视觉壳 + TE01/TE03 接线，M1 批次）。
    ///
    /// ★ 窗口合同（计划 §3.1）：标准 WinForms Form，系统标题栏／边框／最小化／最大化／关闭，
    ///   不做任何非客户区自绘；窗口行为（拖动、贴靠、Alt+F4、双击最大化）全部走系统默认。
    /// ★ DPI 合同（计划 §7）：AutoScaleMode.None，句柄创建后取 DeviceDpi，
    ///   经唯一 <see cref="Dip"/> 入口换算全部几何（TE00 探针验证的模式）。
    /// ★ 编辑合同（计划 §5.1）：会话仅持有配置；保存／应用分离；
    ///   视图状态（缩放、背景、选中）不制造脏状态。
    /// </summary>
    public sealed class ThemeStudioForm : Form
    {
        private const int DefaultWidthDip = 1120;
        private const int DefaultHeightDip = 760;

        private static readonly Color LibraryBack = Color.FromArgb(0xF3, 0xF1, 0xEE);
        private static readonly Color LibraryItemSelected = Color.FromArgb(0xDC, 0xE7, 0xF8);
        private static readonly Color AccentBlue = Color.FromArgb(0x2F, 0x6F, 0xED);
        private static readonly Color CanvasBack = Color.FromArgb(0xFA, 0xFA, 0xF8);
        private static readonly Color PreviewBack = Color.FromArgb(0xFF, 0xFF, 0xFF);
        private static readonly Color Hairline = Color.FromArgb(0xE5, 0xE3, 0xDF);
        private static readonly Color TextMuted = Color.FromArgb(0x6B, 0x72, 0x80);
        private static readonly Color TextStrong = Color.FromArgb(0x2A, 0x2C, 0x30);

        private Func<int, int> _dip = v => v;
        private int Dip(int value) => _dip(value);

        private readonly List<(string Name, string Subtitle)> _themes = new();
        private ListBox _libraryList = null!;
        private ToolStrip _docToolbar = null!;
        private ToolStripLabel _docName = null!;
        private ToolStripLabel _dirtyDot = null!;
        private ToolStripButton _undoButton = null!;
        private ToolStripButton _redoButton = null!;
        private ToolStripButton _saveButton = null!;
        private StatusStrip _status = null!;
        private ToolStripStatusLabel _statusSaved = null!;
        private ToolStripStatusLabel _statusRoute = null!;
        private Panel _previewHost = null!;
        private Label _previewPlaceholder = null!;
        private PropertyInspector _inspector = null!;

        private ThemeDocumentSession? _session;
        private string _selectedTheme = string.Empty;

        /// <summary>应用依赖注入（重做计划 §8）：保存完成后的应用动作由宿主提供，编辑器不反射。</summary>
        public interface IThemeApplyGateway
        {
            /// <summary>应用已保存的主题；返回是否成功（结果由现有编排报告）。</summary>
            bool ApplySavedTheme(string themeName);
        }

        private IThemeApplyGateway? _applyGateway;

        public ThemeStudioForm(IThemeApplyGateway? applyGateway = null)
        {
            _applyGateway = applyGateway;

            Text = "LiteMonitor 主题工作室";
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9F);
            BackColor = CanvasBack;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;

            _ = Handle; // 取 DeviceDpi 前先建句柄
            _dip = value => (int)Math.Round(value * DeviceDpi / 96.0);
            ClientSize = new Size(Dip(DefaultWidthDip), Dip(DefaultHeightDip));
            MinimumSize = new Size(Dip(720), Dip(480));

            BuildUi();
            ReloadThemeList();
            RegisterShortcuts();
            FormClosed += (_, _) => PropertyInspectorSessionBridge.Release();
        }

        // ============================================================
        // UI 构建（三栏：资料库 / 画布 / 检查器；双工具栏 + 状态区）
        // ============================================================

        private void BuildUi()
        {
            // === 状态区（底部）===
            _status = new StatusStrip { SizingGrip = false, BackColor = LibraryBack };
            _statusSaved = new ToolStripStatusLabel("就绪");
            _statusRoute = new ToolStripStatusLabel("预览路线：—");
            _status.Items.AddRange(
                [_statusSaved, new ToolStripStatusLabel("      "), _statusRoute]);
            Controls.Add(_status);

            // === 资料库（左栏）===
            var library = new Panel
            {
                Dock = DockStyle.Left,
                Width = Dip(220),
                BackColor = LibraryBack,
                Padding = new Padding(Dip(8), Dip(8), Dip(4), Dip(4)),
            };
            var libraryTitle = new Label
            {
                Text = "资料库",
                Dock = DockStyle.Top,
                Height = Dip(28),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            };
            var libraryButtons = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = Dip(34),
                BackColor = LibraryBack,
            };
            var newButton = new Button
            {
                Text = "新建",
                Dock = DockStyle.Left,
                Width = Dip(56),
                FlatStyle = FlatStyle.Flat,
            };
            newButton.Click += (_, _) => CreateTheme();
            var duplicateButton = new Button
            {
                Text = "复制",
                Dock = DockStyle.Left,
                Width = Dip(56),
                FlatStyle = FlatStyle.Flat,
            };
            duplicateButton.Click += (_, _) => DuplicateSelected();
            var deleteButton = new Button
            {
                Text = "删除",
                Dock = DockStyle.Left,
                Width = Dip(56),
                FlatStyle = FlatStyle.Flat,
            };
            deleteButton.Click += (_, _) => DeleteSelected();
            libraryButtons.Controls.AddRange([newButton, duplicateButton, deleteButton]);

            _libraryList = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = Dip(40),
                BackColor = LibraryBack,
                IntegralHeight = false,
            };
            _libraryList.DrawItem += DrawLibraryItem;
            _libraryList.SelectedIndexChanged += (_, _) => OnThemeSelected();
            library.Controls.Add(_libraryList);
            library.Controls.Add(libraryButtons);
            library.Controls.Add(libraryTitle);

            // === 检查器（右栏）===
            _inspector = new PropertyInspector
            {
                Dock = DockStyle.Right,
                Width = Dip(300),
                BackColor = PreviewBack,
            };
            _inspector.PropertyEdited += (_, _) => OnSessionChanged();

            // === 画布（中栏）===
            var canvas = new Panel { Dock = DockStyle.Fill, BackColor = CanvasBack, Padding = new Padding(Dip(16)) };
            _previewHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = PreviewBack,
                Padding = new Padding(1),
            };
            _previewHost.Paint += (_, e) =>
            {
                using var pen = new Pen(Hairline);
                e.Graphics.DrawRectangle(pen, 0, 0, _previewHost.Width - 1, _previewHost.Height - 1);
            };
            _previewPlaceholder = new Label
            {
                Text = "选择左侧主题开始编辑。\n预览接入（TE04）前显示占位。",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = TextMuted,
                BackColor = PreviewBack,
            };
            _previewHost.Controls.Add(_previewPlaceholder);
            canvas.Controls.Add(_previewHost);

            // === 文档工具栏（顶部）===
            _docToolbar = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                BackColor = PreviewBack,
                RenderMode = ToolStripRenderMode.System,
                Padding = new Padding(Dip(6), Dip(3), Dip(6), Dip(3)),
            };
            _docName = new ToolStripLabel("—") { Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
            _dirtyDot = new ToolStripLabel("●") { ForeColor = AccentBlue, ToolTipText = "未保存的更改", Visible = false };
            _undoButton = new ToolStripButton("撤销") { Enabled = false };
            _undoButton.Click += (_, _) => Undo();
            _redoButton = new ToolStripButton("重做") { Enabled = false };
            _redoButton.Click += (_, _) => Redo();
            _saveButton = new ToolStripButton("保存") { Enabled = false };
            _saveButton.Click += (_, _) => SaveSession();
            var saveAsButton = new ToolStripButton("另存为…") { Enabled = false };
            saveAsButton.Click += (_, _) => SaveSessionAs();
            var applyButton = new ToolStripButton("应用") { Enabled = false };
            applyButton.Click += (_, _) => ApplySession();
            _docToolbar.Items.AddRange(
                [_docName, _dirtyDot, new ToolStripSeparator(), _undoButton, _redoButton,
                 new ToolStripSeparator(), _saveButton, saveAsButton, applyButton]);

            // Dock 优先级与探针一致：后加入者优先。
            Controls.Add(canvas);
            Controls.Add(_inspector);
            Controls.Add(library);
            Controls.Add(_docToolbar);
        }

        private void DrawLibraryItem(object? sender, DrawItemEventArgs e)
        {
            if (sender is not ListBox list || e.Index < 0 || e.Index >= _themes.Count)
            {
                return;
            }

            (string name, string subtitle) = _themes[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var backBrush = new SolidBrush(selected ? LibraryItemSelected : list.BackColor);
            e.Graphics.FillRectangle(backBrush, e.Bounds);
            if (selected)
            {
                using var accentBrush = new SolidBrush(AccentBlue);
                e.Graphics.FillRectangle(accentBrush, e.Bounds.Left, e.Bounds.Top, Dip(3), e.Bounds.Height);
            }

            var nameRect = new Rectangle(e.Bounds.Left + Dip(12), e.Bounds.Top + Dip(5), e.Bounds.Width - Dip(16), Dip(16));
            var subRect = new Rectangle(e.Bounds.Left + Dip(12), e.Bounds.Top + Dip(22), e.Bounds.Width - Dip(16), Dip(14));
            TextRenderer.DrawText(e.Graphics, name, Font, nameRect, TextStrong);
            TextRenderer.DrawText(e.Graphics, subtitle, new Font("Segoe UI", 7.5F), subRect, TextMuted);
            e.DrawFocusRectangle();
        }

        // ============================================================
        // 资料库
        // ============================================================

        private void ReloadThemeList()
        {
            _themes.Clear();
            foreach (string name in ThemeDocumentStore.ListThemes())
            {
                _themes.Add((name, DescribeTheme(name)));
            }

            _libraryList.BeginUpdate();
            _libraryList.Items.Clear();
            foreach (var t in _themes) _libraryList.Items.Add(t.Name);
            _libraryList.EndUpdate();
        }

        private static string DescribeTheme(string name)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(ThemeDocumentStore.PathOf(name));
                var node = JsonNode.Parse(bytes) as JsonObject;
                if (node?["rendering"] is JsonObject rendering &&
                    string.Equals(rendering["backend"]?.GetValue<string>(), "gpu", StringComparison.OrdinalIgnoreCase))
                {
                    string material = rendering["material"]?.GetValue<string>() ?? "gpu";
                    return "GPU · " + material;
                }

                return "经典";
            }
            catch
            {
                return "无法读取";
            }
        }

        private void OnThemeSelected()
        {
            if (_libraryList.SelectedIndex < 0 || _libraryList.SelectedIndex >= _themes.Count)
            {
                return;
            }

            string name = _themes[_libraryList.SelectedIndex].Name;
            if (string.Equals(name, _selectedTheme, StringComparison.Ordinal)) return;

            if (!ConfirmCloseCurrentDocument()) return;

            // 用户取消了切换：恢复选中项
            if (_session is not null && !string.Equals(name, _session.ThemeName, StringComparison.Ordinal))
            {
                ReselectList(_selectedTheme);
                return;
            }

            OpenTheme(name);
        }

        private void ReselectList(string name)
        {
            int index = _themes.FindIndex(t => string.Equals(t.Name, name, StringComparison.Ordinal));
            _selectedTheme = string.Empty; // 允许重新触发
            _libraryList.SelectedIndex = index;
        }

        private void OpenTheme(string name)
        {
            try
            {
                _session = ThemeDocumentSession.Open(name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开主题失败：" + ex.Message, "主题工作室",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _selectedTheme = name;
            _inspector.Bind(_session);
            PropertyInspectorSessionBridge.Set(_session);
            UpdateDocumentUi();
        }

        private bool ConfirmCloseCurrentDocument()
        {
            if (_session is null || !_session.IsDirty) return true;

            DialogResult choice = MessageBox.Show(
                this,
                $"主题“{_session.ThemeName}”有未保存的修改。保存吗？",
                "主题工作室",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);
            return choice switch
            {
                DialogResult.Yes => SaveSession().Success,
                DialogResult.No => true,
                _ => false
            };
        }

        private void CreateTheme()
        {
            string? name = PromptName("新建主题", SuggestNewName());
            if (name is null) return;

            try
            {
                ThemeFileService.CreateTheme(name);
                ReloadThemeList();
                OpenTheme(name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "新建失败：" + ex.Message, "主题工作室",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string SuggestNewName()
        {
            const string prefix = "新主题";
            for (int i = 1; i < 1000; i++)
            {
                string candidate = i == 1 ? prefix : $"{prefix}{i}";
                if (!ThemeDocumentStore.Exists(candidate)) return candidate;
            }

            return "新主题" + Guid.NewGuid().ToString("N")[..6];
        }

        private void DuplicateSelected()
        {
            if (_libraryList.SelectedIndex < 0) return;
            string src = _themes[_libraryList.SelectedIndex].Name;
            string? name = PromptName("复制主题", SuggestDuplicateName(src));
            if (name is null) return;

            try
            {
                ThemeFileService.DuplicateTheme(src, name);
                ReloadThemeList();
                OpenTheme(name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制失败：" + ex.Message, "主题工作室",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string SuggestDuplicateName(string src)
        {
            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{src} 副本{i}";
                if (!ThemeDocumentStore.Exists(candidate)) return candidate;
            }

            return src + " 副本";
        }

        private void DeleteSelected()
        {
            if (_libraryList.SelectedIndex < 0) return;
            string name = _themes[_libraryList.SelectedIndex].Name;

            if (_session is not null &&
                string.Equals(_session.ThemeName, name, StringComparison.Ordinal) &&
                !ConfirmCloseCurrentDocument())
            {
                return;
            }

            if (MessageBox.Show(this, $"删除主题“{name}”？此操作不可撤销。", "主题工作室",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            try
            {
                if (_session is not null && string.Equals(_session.ThemeName, name, StringComparison.Ordinal))
                {
                    _session = null;
                    _selectedTheme = string.Empty;
                    PropertyInspectorSessionBridge.Set(null);
                }

                ThemeFileService.DeleteTheme(name);
                ReloadThemeList();
                UpdateDocumentUi();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "删除失败：" + ex.Message, "主题工作室",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ============================================================
        // 文档操作
        // ============================================================

        private ThemeSaveResult SaveSession()
        {
            if (_session is null) return new ThemeSaveResult { Success = false, Error = "未打开主题。" };

            ThemeSaveResult result = _session.Save();
            ReportSaveResult(result, saveAction: "保存");
            if (result.Success)
            {
                ReloadThemeList();
                UpdateDocumentUi();
            }

            return result;
        }

        private ThemeSaveResult SaveSessionAs()
        {
            if (_session is null) return new ThemeSaveResult { Success = false, Error = "未打开主题。" };

            string? name = PromptName("另存为", SuggestDuplicateName(_session.ThemeName));
            if (name is null) return new ThemeSaveResult { Success = false, Error = "已取消。" };

            ThemeSaveResult result = _session.SaveAs(name);
            ReportSaveResult(result, saveAction: "另存为");
            if (result.Success)
            {
                ReloadThemeList();
                UpdateDocumentUi();
            }

            return result;
        }

        private void ApplySession()
        {
            if (_session is null) return;

            // 有修改时走"保存并应用"顺序；保存失败不进入应用。
            if (_session.IsDirty)
            {
                ThemeSaveResult saved = SaveSession();
                if (!saved.Success) return;
            }

            if (_applyGateway is null)
            {
                _statusSaved.Text = "已保存；应用入口未接线（TE06）。";
                return;
            }

            try
            {
                bool ok = _applyGateway.ApplySavedTheme(_session.ThemeName);
                _statusSaved.Text = ok ? "已保存并应用。" : "已保存；应用失败，可在设置中重试。";
            }
            catch (Exception ex)
            {
                _statusSaved.Text = "已保存；应用失败：" + ex.Message;
            }
        }

        private void ReportSaveResult(ThemeSaveResult result, string saveAction)
        {
            if (result.Success)
            {
                _statusSaved.Text = result.Diagnostics.Count > 0
                    ? $"{saveAction}成功（含 {result.Diagnostics.Count} 条诊断）。"
                    : $"{saveAction}成功。";
                return;
            }

            if (result.ExternalConflict)
            {
                DialogResult choice = MessageBox.Show(
                    this,
                    "主题文件已被外部修改。覆盖外部更改，还是另存副本？\n\n" +
                    "“是”= 覆盖外部更改；“否”= 另存副本；“取消”= 保留当前草稿。",
                    saveAction + "冲突",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning);
                if (choice == DialogResult.Yes && _session is not null)
                {
                    ThemeSaveResult forced = _session.Save(overwriteExternalChanges: true);
                    ReportSaveResult(forced, saveAction);
                    if (forced.Success)
                    {
                        ReloadThemeList();
                        UpdateDocumentUi();
                    }
                }
                else if (choice == DialogResult.No)
                {
                    SaveSessionAs();
                }

                return;
            }

            _statusSaved.Text = saveAction + "失败：" + result.Error;
            MessageBox.Show(this, saveAction + "失败：" + result.Error, "主题工作室",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void Undo()
        {
            if (_session is null) return;
            if (_session.Undo())
            {
                _inspector.RefreshValues();
                OnSessionChanged();
            }
        }

        private void Redo()
        {
            if (_session is null) return;
            if (_session.Redo())
            {
                _inspector.RefreshValues();
                OnSessionChanged();
            }
        }

        private void OnSessionChanged()
        {
            // 视图状态不制造脏状态：这里只反映会话事实。
            UpdateDocumentUi();
        }

        private void UpdateDocumentUi()
        {
            bool hasDoc = _session is not null;
            _docName.Text = hasDoc ? _session!.ThemeName : "—";
            _dirtyDot.Visible = hasDoc && _session!.IsDirty;
            _undoButton.Enabled = hasDoc && _session!.UndoCount > 0;
            _redoButton.Enabled = hasDoc && _session!.RedoCount > 0;
            _saveButton.Enabled = hasDoc;
            _statusSaved.Text = hasDoc && _session!.IsDirty ? "未保存的更改" :
                hasDoc ? "已保存" : "就绪";
            _statusRoute.Text = "预览路线：" + (hasDoc
                ? (_session!.Draft.Rendering?.IsGpuRequested == true ? "GPU 场景（TE04 接入）" : "经典渲染（TE04 接入）")
                : "—");
            Text = "LiteMonitor 主题工作室" + (hasDoc && _session!.IsDirty ? " — *" : string.Empty) +
                   (hasDoc ? " — " + _session.ThemeName : string.Empty);
        }

        private void RegisterShortcuts()
        {
            // 计划 §3.2：Ctrl+S 保存、Ctrl+Shift+S 另存为、Ctrl+Z 撤销、Ctrl+Y/Ctrl+Shift+Z 重做。
            KeyDown += (_, e) =>
            {
                if (e.Control && e.Shift && e.KeyCode == Keys.S)
                {
                    SaveSessionAs();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.S)
                {
                    SaveSession();
                    e.Handled = true;
                }
                else if (e.Control && e.Shift && e.KeyCode == Keys.Z)
                {
                    Redo();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.Y)
                {
                    Redo();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.Z)
                {
                    Undo();
                    e.Handled = true;
                }
            };
        }

        private string? PromptName(string title, string initial)
        {
            using var dialog = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(Dip(360), Dip(110)),
                MaximizeBox = false,
                MinimizeBox = false,
                Font = Font,
            };
            var label = new Label { Text = "主题名：", Location = new Point(Dip(12), Dip(12)), AutoSize = true };
            var box = new TextBox
            {
                Location = new Point(Dip(12), Dip(34)),
                Width = Dip(330),
                Text = initial,
            };
            var ok = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Location = new Point(Dip(190), Dip(70)),
                Width = Dip(72),
            };
            var cancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(Dip(270), Dip(70)),
                Width = Dip(72),
            };
            dialog.Controls.AddRange([label, box, ok, cancel]);
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;

            while (dialog.ShowDialog(this) == DialogResult.OK)
            {
                string name = box.Text.Trim();
                try
                {
                    ThemeFileService.ValidateThemeName(name);
                    return name;
                }
                catch (ArgumentException ex)
                {
                    MessageBox.Show(this, "名称无效：" + ex.Message, title,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            return null;
        }
    }

    /// <summary>空会话（占位绑定用）：未打开文档时保持检查器可显示空态。</summary>
    internal static class PropertyInspectorSessionBridge
    {
        private static ThemeDocumentSession? _current;

        public static void Set(ThemeDocumentSession? session) => _current = session;

        public static void Release() => _current = null;

        public static ThemeDocumentSession? Current => _current;
    }
}
