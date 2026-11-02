using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.SettingsPage;

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
        private static readonly Font s_groupFont = new("Segoe UI", 9F, FontStyle.Bold);
        private static readonly Font s_smallFont = new("Segoe UI", 7.5F);
        private static readonly Font s_monoFont = new("Consolas", 9F);

        private ThemeDocumentSession? _session;
        private IReadOnlyList<ThemePropertyDescriptor> _properties = Array.Empty<ThemePropertyDescriptor>();
        private readonly Dictionary<string, List<Control>> _rowsByPath = new(StringComparer.Ordinal);
        private readonly ToolTip _toolTip = new();

        /// <summary>★ C-2（R8）：检查器配色来源（既有明暗引擎的当前配色）。</summary>
        private SettingsThemePalette _palette = SettingsThemePalette.Light;

        /// <summary>★ E-1（G1）：当前设备 DPI（用于把固定像素布局换算到新 DPI）。</summary>
        private int _dpi = 96;

        private int Scale(int value) => (int)Math.Round(value * _dpi / 96.0);

        /// <summary>[S05] 阈值说明里的数值摘要格式（整数或两位小数）。</summary>
        private static string FormatNumber(double value) => value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);

        /// <summary>
        /// ★ E-1（G1）：DPI 变化后重算检查器内部固定像素布局。
        /// <para>
        /// 检查器的行是**绝对定位**的（行首显示名固定 96 像素列宽、组标题 24 像素高等），
        /// DPI 变化后必须整体重算，否则会与旁栏宽度错位。
        /// </para>
        /// </summary>
        public void ApplyDpi(int deviceDpi)
        {
            if (deviceDpi <= 0) return;

            _dpi = deviceDpi;
            Padding = new Padding(Scale(12), Scale(8), Scale(12), Scale(8));
            Rebuild();
        }

        /// <summary>字段被用户编辑（已提交到会话），参数为失效级别。</summary>
        public event EventHandler<ThemePropertyInvalidation>? PropertyEdited;
        public event EventHandler? PropertyStructureEdited;

        /// <summary>当前会话的草稿对象。</summary>
        public Theme? Draft => _session?.Draft;

        public PropertyInspector()
        {
            AutoScroll = true;
            BackColor = Color.White;
            Padding = new Padding(12, 8, 12, 8);
        }

        /// <summary>
        /// ★ C-2（R8）：检查器配色跟随既有明暗引擎。
        /// <para>
        /// 编辑器控件（TextBox/CheckBox/ComboBox）**保持原生样式** ——
        /// 计划 §5.3 C-2 明确「高对比度走原生控件样式」，强行自绘会破坏系统高对比度。
        /// 这里只统一检查器自身底色与行内文字色。
        /// </para>
        /// </summary>
        public void ApplyPalette(SettingsThemePalette palette)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));

            _palette = palette;
            BackColor = palette.CardBg;
            ApplyRowColors();
        }

        private void ApplyRowColors()
        {
            foreach (Control child in Controls)
            {
                switch (child)
                {
                    case LinkLabel link:
                        link.ForeColor = _palette.Primary;
                        break;
                    case Label label when label.Tag as string == "group":
                        label.ForeColor = _palette.TextSub;
                        break;
                    case Label label when label.Tag as string == "shared" || label.Tag as string == "thresholds-note":
                        label.ForeColor = label.Tag as string == "shared" ? _palette.Primary : _palette.TextSub;
                        break;
                    case Label label when label.Tag as string == "name":
                        label.ForeColor = _palette.TextMain;
                        break;
                }
            }
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

        internal Control? EditorForTesting(string jsonPath)
        {
            if (!_rowsByPath.TryGetValue(jsonPath, out List<Control>? rows))
            {
                return null;
            }

            return rows.FirstOrDefault(row => row.Tag is RowState);
        }

        internal bool ResetPropertyForTesting(string jsonPath)
        {
            if (!_rowsByPath.TryGetValue(jsonPath, out List<Control>? rows))
            {
                return false;
            }

            LinkLabel? reset = rows
                .OfType<LinkLabel>()
                .FirstOrDefault(link => link.Tag is ThemePropertyDescriptor descriptor &&
                    string.Equals(descriptor.JsonPath, jsonPath, StringComparison.Ordinal));
            if (reset is null)
            {
                return false;
            }

            if (reset.Tag is not ThemePropertyDescriptor descriptor)
            {
                return false;
            }

            ResetToDefault(descriptor);
            return true;
        }

        /// <summary>按属性路径聚焦并滚动对应行到可见区域（N05 点选定位）。</summary>
        public bool FocusProperty(string jsonPath)
        {
            if (string.IsNullOrWhiteSpace(jsonPath)) return false;

            if (_rowsByPath.TryGetValue(jsonPath, out List<Control>? rows) && rows.Count > 0)
            {
                // ★ C-2 整改（计划 §5.3 C-2）：焦点必须落在**可编辑控件**上。
                //   旧实现取 rows[0] —— 那是行首的显示名 Label，Label 不可聚焦，
                //   于是"点选定位"返回 true 但焦点根本没进编辑框（实测 ActiveControl = Label）。
                Control? editor = FindEditableTarget(rows);
                if (editor is null) return false;

                ScrollControlIntoView(editor);
                editor.Focus();
                LastFocusedEditor = editor;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 行内可编辑控件（排除行首显示名 Label 与"恢复默认"LinkLabel —— 后者继承自 Label）。
        /// <para>
        /// ★ 不用 <see cref="Control.CanFocus"/> 作判据：CanFocus 依赖父链的可聚焦性，
        ///   窗体未 Show 时整条父链返回 false，会把真正的编辑框误判成"没有可编辑控件"
        ///   （N05 回归：窗体未显示时点选定位直接失败）。
        ///   这里只排除展示型控件（Label 家族），编辑控件本身由构建器保证。
        /// </para>
        /// </summary>
        private static Control? FindEditableTarget(List<Control> rows)
        {
            foreach (Control row in rows)
            {
                // Label 家族 = 行首显示名 + "恢复默认"链接（LinkLabel 继承自 Label），都不是编辑控件。
                if (row is Label) continue;
                return row;
            }

            return null;
        }

        /// <summary>最近一次点选定位聚焦到的可编辑控件（测试可观测；未定位过则为 null）。</summary>
        internal Control? LastFocusedEditor { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _toolTip.Dispose();
                while (Controls.Count > 0)
                {
                    Control child = Controls[Controls.Count - 1];
                    Controls.RemoveAt(Controls.Count - 1);
                    child.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void Rebuild()
        {
            SuspendLayout();
            try
            {
                _toolTip.RemoveAll();
                while (Controls.Count > 0)
                {
                    Control child = Controls[Controls.Count - 1];
                    Controls.RemoveAt(Controls.Count - 1);
                    child.Dispose();
                }

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
                            Size = new Size(Width - Padding.Horizontal, Scale(24)),
                            ForeColor = _palette.TextSub,
                            Font = s_groupFont,
                            Tag = "group",
                        };
                        groupLabel.Margin = new Padding(0, y == Padding.Top ? 0 : Scale(10), 0, Scale(2));
                        Controls.Add(groupLabel);
                        _rowsByPath["#group:" + p.Group] = new List<Control> { groupLabel };
                        y += Scale(30);
                        lastGroup = p.Group;
                    }

                    int rowY = y;
                    var nameLabel = new Label
                    {
                        Text = p.DisplayName,
                        Location = new Point(Padding.Left, rowY + Scale(3)),
                        Size = new Size(Scale(92), Scale(22)),
                        TextAlign = ContentAlignment.MiddleLeft,
                        ForeColor = _palette.TextMain,
                        Tag = "name",
                    };
                    Controls.Add(nameLabel);
                    var rows = new List<Control> { nameLabel };

                    Control editor = BuildEditor(p);
                    editor.Location = new Point(Padding.Left + Scale(96), rowY);
                    editor.Width = Math.Max(Scale(80), Width - Padding.Horizontal - Scale(96));
                    editor.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                    Controls.Add(editor);
                    rows.Add(editor);
                    y += editor.Height + Scale(4);

                    // 恢复默认按钮（有显式覆盖时才显示）
                    var resetLink = new LinkLabel
                    {
                        Text = "恢复默认",
                        Location = new Point(Padding.Left + Scale(96), y),
                        Size = new Size(Scale(64), Scale(18)),
                        AutoSize = false,
                        Font = s_smallFont,
                        Tag = p,
                        Visible = _session!.HasOverride(p.JsonPath),
                    };
                    resetLink.LinkClicked += (_, _) => ResetToDefault(p);
                    Controls.Add(resetLink);
                    rows.Add(resetLink);
                    _rowsByPath[p.JsonPath] = rows;
                    y += resetLink.Height + Scale(6);

                    // GPU 目标下的共享设置显式标注（不假装独立）——TE00 §2.5
                    if (Target != ThemeEditTarget.Classic && p.Scope.HasFlag(ThemePropertyScope.Shared))
                    {
                        var sharedLabel = new Label
                        {
                            Text = "共享设置 — 同时影响主效果与兼容外观",
                            Location = new Point(Padding.Left + Scale(96), y),
                            Size = new Size(Width - Padding.Horizontal - Scale(96), Scale(16)),
                            ForeColor = _palette.Primary,
                            Font = s_smallFont,
                            Tag = "shared",
                        };
                        Controls.Add(sharedLabel);
                        rows.Add(sharedLabel);
                        y += sharedLabel.Height + Scale(4);
                    }
                }

                // [S05] 阈值只读说明：主题不再提供阈值编辑入口，状态阈值来自当前生效设置；
                // 显示实时摘要，避免用户在旧主题 JSON 里找不到阈值字段时产生误解。
                double loadWarn = 0, loadCrit = 0, tempWarn = 0, tempCrit = 0;
                try
                {
                    (loadWarn, loadCrit) = MetricUtils.GetThresholds("CPU.Load");
                    (tempWarn, tempCrit) = MetricUtils.GetThresholds("CPU.Temp");
                }
                catch
                {
                    // 设置不可读时只显示说明文字本身，不拼摘要数值。
                }

                string thresholdDetail = tempWarn > 0 || loadWarn > 0
                    ? $"\n当前生效：负载 {FormatNumber(loadWarn)}/{FormatNumber(loadCrit)}，温度 {FormatNumber(tempWarn)}/{FormatNumber(tempCrit)} ℃"
                    : "";
                var thresholdNote = new Label
                {
                    Text = "状态阈值（负载/温度等）来自当前生效设置，主题只负责颜色。" + thresholdDetail,
                    Location = new Point(Padding.Left, y + Scale(6)),
                    Size = new Size(Width - Padding.Horizontal, Scale(44)),
                    ForeColor = _palette.TextSub,
                    Font = s_smallFont,
                    Tag = "thresholds-note",
                };
                Controls.Add(thresholdNote);
                y += thresholdNote.Height + Scale(10);

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
                        Font = s_monoFont,
                    };
                    var state = new RowState(p, box, _session, OnRowCommitted);
                    box.Tag = state;
                    box.TextChanged += (_, _) => state.CommitText();
                    return box;
                }
                case ThemePropertyEditorKind.Number:
                {
                    var box = new TextBox { Font = s_monoFont };
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
            // 草稿回写模型默认值，与解析器语义一致（重做计划 §4.2）；
            // 副作用编辑按缺省值计算并入同一撤销事务。
            _session.ApplyPropertyReset(p);
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
            if (ChangesPropertyStructure(p))
            {
                // 结构变更会让宿主整体重绑检查器（销毁并重建全部行控件）。
                // 若在触发本事件的 ComboBox 事件栈内同步执行，等于在事件处理中
                // Dispose 事件源控件，是 WinForms 已知雷区；有句柄时延迟到消息循环。
                if (IsHandleCreated)
                {
                    BeginInvoke(() => PropertyStructureEdited?.Invoke(this, EventArgs.Empty));
                }
                else
                {
                    PropertyStructureEdited?.Invoke(this, EventArgs.Empty);
                }
            }
            PropertyEdited?.Invoke(this, p.Invalidation);
        }

        private static bool ChangesPropertyStructure(ThemePropertyDescriptor p) =>
            string.Equals(
                p.JsonPath,
                "rendering.parameters.visualPreset",
                StringComparison.Ordinal) ||
            string.Equals(
                p.JsonPath,
                "rendering.parameters.glassStyle",
                StringComparison.Ordinal);

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
                            string? text = Convert.ToString(
                                value,
                                System.Globalization.CultureInfo.InvariantCulture);
                            int index = text is null ? -1 : FindChoiceIndex(combo, text);
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
                Submit(parsed);
            }

            public void CommitCheck()
            {
                if (_refreshing || _checkBox is null) return;
                Submit(_checkBox.Checked);
            }

            public void CommitChoice()
            {
                if (_refreshing || _combo is null || _combo.SelectedIndex < 0) return;
                string? value = _combo.SelectedItem as string;
                if (value is null) return;
                // 结构变更会整体重建检查器（Dispose 全部行控件）。下拉展开中的键盘
                // 导航同样触发本提交——先收起下拉、释放控件的消息状态，再进入异步
                // 重建，避免稍后的 Dispose 命中仍处于展开/捕获状态的 ComboBox
                //（原生访问冲突会让整个进程闪退）。
                if (ChangesPropertyStructure(_p) && _combo.DroppedDown)
                {
                    _combo.DroppedDown = false;
                }
                Submit(value);
            }

            private void Submit(object? value)
            {
                ThemeDocumentSession? session = _session;
                if (session is null) return;

                // 主编辑与描述符声明的副作用编辑（如预设联动 liveBackground/glassStyle）
                // 由会话统一提交进同一撤销事务。
                session.ApplyPropertyEdit(_p, value);
                _onCommitted(_p);
            }

            private static object? ParseNumberOrText(string text) => text;

            private static int FindChoiceIndex(ComboBox combo, string text)
            {
                for (int index = 0; index < combo.Items.Count; index++)
                {
                    if (string.Equals(
                        combo.Items[index] as string,
                        text,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return index;
                    }
                }

                return -1;
            }
        }
    }

}
