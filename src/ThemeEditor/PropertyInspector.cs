using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// 属性检查器面板（重做计划 §8 PropertyInspector）：
    /// 按 <see cref="ThemePropertyCatalog"/> 描述符分组渲染编辑行；
    /// 每次编辑经 <see cref="ThemeDocumentSession.ApplyEdit"/> 提交，
    /// 不直接改运行时主题。显式值与模型缺省区分显示；"恢复默认"按描述符删除覆盖。
    /// </summary>
    public sealed class PropertyInspector : Panel
    {
        private ThemeDocumentSession? _session;
        private IReadOnlyList<ThemePropertyDescriptor> _properties = Array.Empty<ThemePropertyDescriptor>();
        private readonly Dictionary<string, List<Control>> _rowsByPath = new(StringComparer.Ordinal);
        private readonly ToolTip _toolTip = new();

        /// <summary>字段被用户编辑（已提交到会话），参数为失效级别。</summary>
        public event EventHandler<ThemePropertyInvalidation>? PropertyEdited;

        /// <summary>当前会话的草稿对象。</summary>
        public Theme? Draft => _session?.Draft;

        public PropertyInspector()
        {
            AutoScroll = true;
            BackColor = Color.White;
            Padding = new Padding(12, 8, 12, 8);
        }

        /// <summary>当前编辑目标（视图状态，不写主题、不制造脏状态）。</summary>
        public ThemeEditTarget Target { get; private set; } = ThemeEditTarget.Classic;

        /// <summary>绑定会话并重建检查器（目标默认按文档类型选择）。</summary>
        public void Bind(ThemeDocumentSession session) =>
            Bind(session, ThemePropertyCatalog.DefaultTarget(session.Draft));

        /// <summary>绑定会话与编辑目标；切换目标只换视图，不改 rendering.backend。</summary>
        public void Bind(ThemeDocumentSession session, ThemeEditTarget target)
        {
            _session = session;
            Target = target;
            _properties = ThemePropertyCatalog.GetProperties(session.Draft, target);
            Rebuild();
        }

        /// <summary>编辑器偏好（TE05 接偏好持久化前先给固定窄宽）。</summary>
        public int PreferredWidth { get; set; } = 300;

        /// <summary>全部行控件恢复描述符当前值（撤销／重做后调用）。</summary>
        public void RefreshValues()
        {
            if (_session is null) return;

            foreach (ThemePropertyDescriptor p in _properties)
            {
                if (!_rowsByPath.TryGetValue(p.JsonPath, out List<Control>? rows)) continue;
                foreach (Control row in rows)
                {
                    if (row.Tag is RowState state)
                    {
                        state.Refresh();
                    }
                }
            }

            RefreshOverrideBadges();
        }

        private void Rebuild()
        {
            SuspendLayout();
            try
            {
                foreach (Control child in Controls)
                {
                    child.Dispose();
                }

                Controls.Clear();
                _rowsByPath.Clear();

                int y = Padding.Top;
                string? lastGroup = null;

                foreach (ThemePropertyDescriptor p in _properties)
                {
                    if (!string.Equals(p.Group, lastGroup, StringComparison.Ordinal))
                    {
                        var groupLabel = new Label
                        {
                            Text = p.Group,
                            Location = new Point(Padding.Left, y),
                            Size = new Size(Width - Padding.Horizontal, 24),
                            ForeColor = Color.FromArgb(0x6B, 0x72, 0x80),
                            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                        };
                        groupLabel.Margin = new Padding(0, y == Padding.Top ? 0 : 10, 0, 2);
                        Controls.Add(groupLabel);
                        _rowsByPath["#group:" + p.Group] = new List<Control> { groupLabel };
                        y += 30;
                        lastGroup = p.Group;
                    }

                    int rowY = y;
                    var nameLabel = new Label
                    {
                        Text = p.DisplayName,
                        Location = new Point(Padding.Left, rowY + 3),
                        Size = new Size(92, 22),
                        TextAlign = ContentAlignment.MiddleLeft,
                    };
                    Controls.Add(nameLabel);
                    var rows = new List<Control> { nameLabel };

                    Control editor = BuildEditor(p);
                    editor.Location = new Point(Padding.Left + 96, rowY);
                    editor.Width = Math.Max(80, Width - Padding.Horizontal - 96);
                    editor.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                    Controls.Add(editor);
                    rows.Add(editor);
                    y += editor.Height + 4;

                    // 恢复默认按钮（有显式覆盖时才显示）
                    var resetLink = new LinkLabel
                    {
                        Text = "恢复默认",
                        Location = new Point(Padding.Left + 96, y),
                        Size = new Size(64, 18),
                        AutoSize = false,
                        Font = new Font("Segoe UI", 7.5F),
                        Tag = p,
                        Visible = _session!.HasOverride(p.JsonPath),
                    };
                    resetLink.LinkClicked += (_, _) => ResetToDefault(p);
                    Controls.Add(resetLink);
                    rows.Add(resetLink);
                    _rowsByPath[p.JsonPath] = rows;
                    y += resetLink.Height + 6;

                    // GPU 目标下的共享设置显式标注（不假装独立）——TE00 §2.5
                    if (Target != ThemeEditTarget.Classic && p.Scope.HasFlag(ThemePropertyScope.Shared))
                    {
                        var sharedLabel = new Label
                        {
                            Text = "共享设置 — 同时影响主效果与兼容外观",
                            Location = new Point(Padding.Left + 96, y),
                            Size = new Size(Width - Padding.Horizontal - 96, 16),
                            ForeColor = Color.FromArgb(0x2F, 0x6F, 0xED),
                            Font = new Font("Segoe UI", 7.5F),
                        };
                        Controls.Add(sharedLabel);
                        rows.Add(sharedLabel);
                        y += sharedLabel.Height + 4;
                    }
                }

                // 底部占位，保证滚动到底部时最后一行不被裁切
                Controls.Add(new Panel { Location = new Point(0, y + 8), Size = new Size(1, 1) });
            }
            finally
            {
                ResumeLayout(performLayout: true);
            }

            RefreshValues();
        }

        private Control BuildEditor(ThemePropertyDescriptor p)
        {
            switch (p.EditorKind)
            {
                case ThemePropertyEditorKind.Color:
                {
                    var box = new TextBox
                    {
                        Font = new Font("Consolas", 9F),
                    };
                    var state = new RowState(p, box, _session, OnRowCommitted);
                    box.Tag = state;
                    box.TextChanged += (_, _) => state.CommitText();
                    return box;
                }
                case ThemePropertyEditorKind.Number:
                {
                    var box = new TextBox { Font = new Font("Consolas", 9F) };
                    var state = new RowState(p, box, _session, OnRowCommitted);
                    if (p.Minimum is double min) _toolTip.SetToolTip(box, $"范围 [{min}, {p.Maximum}]");
                    box.Tag = state;
                    box.TextChanged += (_, _) => state.CommitText();
                    return box;
                }
                case ThemePropertyEditorKind.Boolean:
                {
                    var check = new CheckBox { Height = 24, Text = string.Empty };
                    var state = new RowState(p, check, _session, OnRowCommitted);
                    check.Tag = state;
                    check.CheckedChanged += (_, _) => state.CommitCheck();
                    return check;
                }
                case ThemePropertyEditorKind.Choice:
                {
                    var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Height = 24 };
                    if (p.Choices is not null)
                    {
                        foreach (string choice in p.Choices) combo.Items.Add(choice);
                    }

                    var state = new RowState(p, combo, _session, OnRowCommitted);
                    combo.Tag = state;
                    combo.SelectedIndexChanged += (_, _) => state.CommitChoice();
                    return combo;
                }
                default:
                {
                    var box = new TextBox();
                    var state = new RowState(p, box, _session, OnRowCommitted);
                    box.Tag = state;
                    box.TextChanged += (_, _) => state.CommitText();
                    return box;
                }
            }
        }

        private void ResetToDefault(ThemePropertyDescriptor p)
        {
            if (_session is null) return;

            // "恢复默认"= 删除覆盖（JSON 键消失，由模型缺省接管），
            // 草稿回写模型默认值，与解析器语义一致（重做计划 §4.2）。
            object? modelDefault = p.DefaultGetter();
            _session.ApplyEdit(new ThemeFieldEdit(
                p.JsonPath,
                (System.Text.Json.Nodes.JsonNode?)null,
                theme => p.Setter(theme, modelDefault)));
            RefreshValues();
            OnRowCommitted(p);
        }

        /// <summary>行提交统一出口：字体失效先重建运行期字体，再通知宿主刷新预览。</summary>
        private void OnRowCommitted(ThemePropertyDescriptor p)
        {
            if (_session is not null && p.Invalidation == ThemePropertyInvalidation.Font)
            {
                // 先释放旧运行期字体再重建，避免每帧/每次输入泄漏 GDI 字体句柄；
                // 预览只借用字体，宿主收到 Font 失效后会同步 SetTheme 重新借用新实例。
                _session.Draft.DisposeFonts();
                _session.Draft.BuildFonts();
            }

            RefreshOverrideBadges();
            PropertyEdited?.Invoke(this, p.Invalidation);
        }

        private void RefreshOverrideBadges()
        {
            if (_session is null) return;

            foreach (ThemePropertyDescriptor p in _properties)
            {
                if (!_rowsByPath.TryGetValue(p.JsonPath, out List<Control>? rows)) continue;
                foreach (Control row in rows)
                {
                    if (row is LinkLabel link && link.Tag is ThemePropertyDescriptor)
                    {
                        link.Visible = _session.HasOverride(p.JsonPath);
                    }
                }
            }
        }

        /// <summary>行编辑状态：从描述符读值刷新控件、从控件提交会话。</summary>
        private sealed class RowState
        {
            private readonly ThemePropertyDescriptor _p;
            private readonly ThemeDocumentSession? _session;
            private readonly Action<ThemePropertyDescriptor> _onCommitted;
            private readonly Control _editor;
            private readonly TextBox? _textBox;
            private readonly CheckBox? _checkBox;
            private readonly ComboBox? _combo;
            private bool _refreshing;

            public RowState(
                ThemePropertyDescriptor p,
                Control editor,
                ThemeDocumentSession? session,
                Action<ThemePropertyDescriptor> onCommitted)
            {
                _p = p;
                _session = session;
                _editor = editor;
                _onCommitted = onCommitted;
                _textBox = editor as TextBox;
                _checkBox = editor as CheckBox;
                _combo = editor as ComboBox;
                Refresh();
            }

            public void Refresh()
            {
                _refreshing = true;
                try
                {
                    object? value = _p.Getter(SessionDraft);
                    switch (_editor)
                    {
                        case TextBox box:
                            box.Text = value as string ?? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                            break;
                        case CheckBox check:
                            check.Checked = value is true;
                            break;
                        case ComboBox combo:
                        {
                            int index = value is int i
                                ? combo.Items.IndexOf(i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                                : -1;
                            combo.SelectedIndex = index;
                            break;
                        }
                    }
                }
                finally
                {
                    _refreshing = false;
                }
            }

            private Theme SessionDraft => _session?.Draft
                ?? throw new InvalidOperationException("Inspector session not bound.");

            public void CommitText()
            {
                if (_refreshing || _textBox is null) return;
                string text = _textBox.Text;
                object? parsed = ParseNumberOrText(text);
                Submit(parsed, _p.ToJson(parsed));
            }

            public void CommitCheck()
            {
                if (_refreshing || _checkBox is null) return;
                Submit(_checkBox.Checked, _p.ToJson(_checkBox.Checked));
            }

            public void CommitChoice()
            {
                if (_refreshing || _combo is null || _combo.SelectedIndex < 0) return;
                int value = _combo.SelectedIndex;
                Submit(value, _p.ToJson(value));
            }

            private void Submit(object? value, System.Text.Json.Nodes.JsonNode? json)
            {
                ThemeDocumentSession? session = _session;
                if (session is null) return;

                session.ApplyEdit(new ThemeFieldEdit(
                    _p.JsonPath,
                    json,
                    theme => _p.Setter(theme, value)));
                _onCommitted(_p);
            }

            private static object? ParseNumberOrText(string text) => text;
        }
    }

}
