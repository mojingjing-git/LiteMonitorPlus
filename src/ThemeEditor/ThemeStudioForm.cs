using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using LiteMonitor.Rendering;
using LiteMonitor.Core.Actions.Services;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.SettingsPage;

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
        private static readonly Color AccentBlue = Color.FromArgb(0x2F, 0x6F, 0xED);
        private static readonly Color CanvasBack = Color.FromArgb(0xFA, 0xFA, 0xF8);

        /// <summary>
        /// 预览画布底色 —— ★ **主题产物**，不参与工作区明暗（计划 §5.3 C-2 排除项）：
        /// 画布显示的是"主题在该主题下的真实观感"，被工作区明暗染色就等于伪造预览。
        /// </summary>
        private static readonly Color PreviewBack = Color.FromArgb(0xFF, 0xFF, 0xFF);
        private static readonly Color TextMuted = Color.FromArgb(0x6B, 0x72, 0x80);

        /// <summary>
        /// ★ C-2（R8）：工作区配色来自**既有明暗引擎**（<see cref="SettingsThemeManager"/>），
        /// 不再是文件内的硬编码浅色常量。
        /// <para>
        /// 旧实现把 8 个浅色值写死成 static readonly Color（文件内 <c>IsDark</c>/<c>Palette</c> 零命中），
        /// 于是暗色模式下工作区仍是浅色（实测 actualBack=FFFAFAF8、actualInspectorBack=FFFFFFFF）。
        /// 现在统一读 <c>SettingsThemeManager.CurrentPalette</c>，并订阅 <c>ThemeChanged</c>
        /// 以便窗口已打开时跟随系统主题切换同步。
        /// </para>
        /// </summary>
        private SettingsThemePalette _palette = SettingsThemePalette.Light;

        /// <summary>
        /// 资料库副标题字体（进程内单实例）。
        /// ★ Lane C 基线发现：逐帧 <c>new Font</c> 会让 GDI 计数在 GC 之间振荡（376→868→376）；
        ///   字体只在终结器运行后释放，所以按"谁建谁释放"改为窗体持有、Dispose 时释放。
        /// </summary>
        private readonly Font _librarySubtitleFont = new("Segoe UI", 7.5F);

        private Func<int, int> _dip = v => v;
        private int Dip(int value) => _dip(value);

        /// <summary>★ E-1（G1）：DPI 变化后需要按新 DeviceDpi 重算的固定几何清单。</summary>
        private readonly List<Action<int>> _dpiResetters = new();

        /// <summary>★ E-1（G1）：最近一次生效的设备 DPI（用于算缩放比）。</summary>
        private int _lastDeviceDpi = 96;

        private static int ScaleDip(int value, int deviceDpi) =>
            (int)Math.Round(value * deviceDpi / 96.0);

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
        private ThemePreviewControl? _preview;
        private System.Windows.Forms.Timer? _previewDebounce;
        private ToolStripComboBox _targetCombo = null!;
        private ToolStripLabel _targetLabel = null!;
        private ToolStripComboBox _shapeCombo = null!;
        private ToolStripLabel _shapeNotice = null!;
        private bool _suppressTargetChange;
        private PropertyInspector _inspector = null!;
        private HorizontalPreviewControl? _horizontalPreview;
        private TaskbarPreviewControl? _taskbarPreview;
        private PreviewShape _previewShape = PreviewShape.Vertical;
        private ToolStripDropDownButton _exportButton = null!;
        private readonly Settings? _appSettings;

        private ThemeDocumentSession? _session;
        private string _selectedTheme = string.Empty;
        private readonly System.Windows.Forms.Timer _recoveryTimer;
        private Panel _libraryPanel = null!;
        private Panel _libraryButtons = null!;
        private Label _libraryTitle = null!;
        private Panel _canvasPanel = null!;
        private ToolStrip _canvasToolbar = null!;
        private bool _suppressSelection;

        /// <summary>应用执行阶段（N06 三阶段模型）。</summary>
        public enum ThemeApplyStage
        {
            ThemeSaved,
            SettingsPersisted,
            ThemeApplied
        }

        /// <summary>应用执行结果细分（N06）。</summary>
        public sealed record ThemeApplyResult(
            bool Success,
            ThemeApplyStage Stage,
            string? Error = null,
            string? EffectiveTheme = null,
            string? EffectiveRoute = null,
            long Revision = 0,
            bool Partial = false,
            IReadOnlyList<ModuleApplyResult>? Modules = null
        );

        /// <summary>应用依赖注入（重做计划 §8 / N06）：保存完成后的应用动作由宿主提供，编辑器不反射。</summary>
        public interface IThemeApplyGateway
        {
            /// <summary>应用已保存的主题；返回细分阶段与生效路线信息。</summary>
            ThemeApplyResult ApplySavedTheme(string themeName, long revision);
        }

        private IThemeApplyGateway? _applyGateway;

        public ThemeStudioForm(IThemeApplyGateway? applyGateway = null, Settings? appSettings = null)
        {
            _applyGateway = applyGateway;
            _appSettings = appSettings;

            Text = "LiteMonitor 主题工作室";
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9F);
            BackColor = CanvasBack;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;

            _ = Handle; // 取 DeviceDpi 前先建句柄
            _dip = value => (int)Math.Round(value * DeviceDpi / 96.0);
            _lastDeviceDpi = DeviceDpi;
            ClientSize = new Size(Dip(DefaultWidthDip), Dip(DefaultHeightDip));
            MinimumSize = new Size(Dip(720), Dip(480));

            BuildUi();
            // ★ E-1（G1）：检查器内部固定像素布局同样按当前 DeviceDpi 换算（计划 §7 DPI 合同）。
            _inspector.ApplyDpi(DeviceDpi);
            ReloadThemeList();
            RegisterShortcuts();

            // 恢复草稿：空闲 2 秒持久化（重做计划 §5.1）
            _recoveryTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _recoveryTimer.Tick += (_, _) =>
            {
                _recoveryTimer.Stop();
                PersistRecovery();
            };

            // 关闭 / Alt+F4：有未保存修改时提供保存／放弃／取消
            FormClosing += (_, e) =>
            {
                if (!ConfirmCloseCurrentDocument())
                {
                    e.Cancel = true;
                }
            };

        }

        private void PersistRecovery()
        {
            if (_session is null || !_session.IsDirty) return;
            try
            {
                ThemeDocumentStore.PersistRecovery(_session.CaptureSnapshot());
            }
            catch
            {
                // 恢复草稿写失败不阻塞编辑（正式保存仍是唯一可靠路径）
            }
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
            _libraryPanel = library;
            var libraryTitle = new Label
            {
                Text = "资料库",
                Dock = DockStyle.Top,
                Height = Dip(28),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            };
            _libraryTitle = libraryTitle;
            var libraryButtons = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = Dip(34),
                BackColor = LibraryBack,
            };
            _libraryButtons = libraryButtons;
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
            _inspector.PropertyEdited += (_, invalidation) =>
            {
                OnSessionChanged();
                if (invalidation == ThemePropertyInvalidation.Font)
                {
                    // 字体对象已重建：预览必须同步重新借用新 Font 实例
                    RefreshPreviewNow();
                }
            };

            // === 画布（中栏）===
            var canvas = new Panel { Dock = DockStyle.Fill, BackColor = CanvasBack, Padding = new Padding(Dip(16)) };
            _canvasPanel = canvas;
            _previewHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = PreviewBack,
                Padding = new Padding(1),
            };
            _previewHost.Paint += (_, e) =>
            {
                using var pen = new Pen(_palette.Border);
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

            // 画布工具栏：编辑目标（类型/目标/实际路线三者同时可辨认，计划 §3.2）
            var canvasToolbar = new ToolStrip
            {
                Dock = DockStyle.Top,
                GripStyle = ToolStripGripStyle.Hidden,
                BackColor = CanvasBack,
                RenderMode = ToolStripRenderMode.System,
            };
            _targetLabel = new ToolStripLabel("编辑目标：") { ForeColor = TextMuted };
            _targetCombo = new ToolStripComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = Dip(180),
                Enabled = false,
            };
            _targetCombo.SelectedIndexChanged += (_, _) => OnTargetChanged();
            var routeHint = new ToolStripLabel("切换目标不修改 rendering.backend")
            {
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 7.5F),
            };
            _shapeCombo = new ToolStripComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = Dip(150),
            };
            _shapeCombo.Items.AddRange(["竖向", "横向", "任务栏"]);
            _shapeCombo.SelectedIndex = 0;
            _shapeCombo.SelectedIndexChanged += (_, _) => OnPreviewShapeChanged();
            _shapeNotice = new ToolStripLabel(string.Empty)
            {
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 7.5F),
            };
            canvasToolbar.Items.AddRange(
                [_targetLabel, _targetCombo, new ToolStripSeparator(), routeHint,
                 new ToolStripSeparator(), new ToolStripLabel("预览形态：") { ForeColor = TextMuted },
                 _shapeCombo, _shapeNotice]);
            _canvasToolbar = canvasToolbar;
            canvas.Controls.Add(canvasToolbar);

            // 同源预览（TE04 首批）：复用生产渲染路径的经典/GPU 预览控件。
            // 每次编辑经 150ms 防抖刷新，避免连续输入触发 GPU 控件反复重建。
            _previewDebounce = new System.Windows.Forms.Timer { Interval = 150 };
            _previewDebounce.Tick += (_, _) =>
            {
                _previewDebounce!.Stop();
                RefreshPreviewNow();
            };
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
            // N07 导出与投稿准备菜单（计划 §11 / 波次 W2）
            _exportButton = new ToolStripDropDownButton("导出");
            var exportJsonItem = new ToolStripMenuItem("导出主题 JSON");
            exportJsonItem.Click += (_, _) => ExportThemeJsonAction();
            var exportPreviewItem = new ToolStripMenuItem("导出效果图");
            exportPreviewItem.Click += (_, _) => ExportEffectImageAction();
            var exportSubmissionItem = new ToolStripMenuItem("准备投稿资料");
            exportSubmissionItem.Click += (_, _) => PrepareSubmissionMaterialsAction();
            _exportButton.DropDownItems.AddRange(
                [exportJsonItem, exportPreviewItem, exportSubmissionItem]);

            var libraryToggle = new ToolStripButton("资料库");
            libraryToggle.Click += (_, _) => _libraryPanel.Visible = !_libraryPanel.Visible;
            _docToolbar.Items.AddRange(
                [_docName, _dirtyDot, new ToolStripSeparator(), _undoButton, _redoButton,
                 new ToolStripSeparator(), _saveButton, saveAsButton, applyButton,
                 new ToolStripSeparator(), _exportButton,
                 new ToolStripSeparator(), libraryToggle]);

            // Dock 优先级与探针一致：后加入者优先。
            Controls.Add(canvas);
            Controls.Add(_inspector);
            Controls.Add(library);
            Controls.Add(_docToolbar);

            // ★ E-1（G1）：登记 DPI 重算清单。
            //   本窗体 AutoScaleMode.None（计划 §7 合同），所有固定几何都经 Dip() 换算，
            //   但换算只在构造时做了一次 —— 窗口跨不同 DPI 的显示器后不会重算。
            //   这里把所有固定尺寸集中登记，DPI 变化时按新 DeviceDpi 统一重算。
            RegisterDpiGeometry(library, libraryTitle, libraryButtons, newButton, duplicateButton, deleteButton, canvas);

            // ★ C-2（R8）：工作区配色跟随既有明暗引擎；窗口已打开时随系统主题切换同步。
            ApplyWorkspacePalette();
            SettingsThemeManager.ThemeChanged += OnWorkspaceThemeChanged;
        }

        /// <summary>★ E-1（G1）：登记全部固定尺寸几何（分栏宽度、列表行高、工具栏控件宽、画布内边距）。</summary>
        private void RegisterDpiGeometry(
            Panel library,
            Label libraryTitle,
            Panel libraryButtons,
            Button newButton,
            Button duplicateButton,
            Button deleteButton,
            Panel canvas)
        {
            _dpiResetters.Clear();
            _dpiResetters.Add(dpi => library.Width = ScaleDip(220, dpi));
            _dpiResetters.Add(dpi => library.Padding = new Padding(ScaleDip(8, dpi), ScaleDip(8, dpi), ScaleDip(4, dpi), ScaleDip(4, dpi)));
            _dpiResetters.Add(dpi => libraryTitle.Height = ScaleDip(28, dpi));
            _dpiResetters.Add(dpi => libraryButtons.Height = ScaleDip(34, dpi));
            _dpiResetters.Add(dpi =>
            {
                newButton.Width = ScaleDip(56, dpi);
                duplicateButton.Width = ScaleDip(56, dpi);
                deleteButton.Width = ScaleDip(56, dpi);
            });
            _dpiResetters.Add(dpi => _libraryList.ItemHeight = ScaleDip(40, dpi));
            _dpiResetters.Add(dpi => _inspector.Width = ScaleDip(300, dpi));
            _dpiResetters.Add(dpi => canvas.Padding = new Padding(ScaleDip(16, dpi)));
            _dpiResetters.Add(dpi => _targetCombo.Width = ScaleDip(180, dpi));
            _dpiResetters.Add(dpi => _shapeCombo.Width = ScaleDip(150, dpi));
            _dpiResetters.Add(dpi => _docToolbar.Padding = new Padding(ScaleDip(6, dpi), ScaleDip(3, dpi), ScaleDip(6, dpi), ScaleDip(3, dpi)));
        }

        /// <summary>
        /// ★ E-1（G1）：DPI 变化后重算固定几何。
        /// <para>
        /// 触发源：<c>WM_DPICHANGED</c>（跨显示器）与 <c>DpiChangedAfterParent</c>。
        /// 两者都会先让 WinForms 更新 <see cref="DeviceDpi"/>，再回调到这里重排。
        /// </para>
        /// </summary>
        internal void ApplyDpiScale(int deviceDpi)
        {
            if (deviceDpi <= 0) return;
            if (_lastDeviceDpi <= 0) _lastDeviceDpi = deviceDpi;
            if (deviceDpi == _lastDeviceDpi) return;

            _dip = value => ScaleDip(value, deviceDpi);
            foreach (Action<int> reset in _dpiResetters)
            {
                reset(deviceDpi);
            }

            _inspector.ApplyDpi(deviceDpi);
            MinimumSize = new Size(ScaleDip(720, deviceDpi), ScaleDip(480, deviceDpi));
            _lastDeviceDpi = deviceDpi;
            Invalidate(true);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyDpiScale(DeviceDpi);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // WM_DPICHANGED = 0x02E0：base.WndProc 已更新 DeviceDpi，这里补做几何重算。
            if (m.Msg == 0x02E0)
            {
                ApplyDpiScale(DeviceDpi);
            }
        }

        /// <summary>系统/用户主题切换 ⇒ 重刷工作区配色（预览画布不受影响，见 C-2 排除项）。</summary>
        private void OnWorkspaceThemeChanged(object? sender, EventArgs e)
        {
            if (IsDisposed) return;
            ApplyWorkspacePalette();
        }

        private void DrawLibraryItem(object? sender, DrawItemEventArgs e)
        {
            if (sender is not ListBox list || e.Index < 0 || e.Index >= _themes.Count)
            {
                return;
            }

            (string name, string subtitle) = _themes[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var backBrush = new SolidBrush(selected ? _palette.NavSelected : list.BackColor);
            e.Graphics.FillRectangle(backBrush, e.Bounds);
            if (selected)
            {
                using var accentBrush = new SolidBrush(_palette.Primary);
                e.Graphics.FillRectangle(accentBrush, e.Bounds.Left, e.Bounds.Top, Dip(3), e.Bounds.Height);
            }

            var nameRect = new Rectangle(e.Bounds.Left + Dip(12), e.Bounds.Top + Dip(5), e.Bounds.Width - Dip(16), Dip(16));
            var subRect = new Rectangle(e.Bounds.Left + Dip(12), e.Bounds.Top + Dip(22), e.Bounds.Width - Dip(16), Dip(14));
            TextRenderer.DrawText(e.Graphics, name, Font, nameRect, _palette.TextMain);
            TextRenderer.DrawText(e.Graphics, subtitle, _librarySubtitleFont, subRect, _palette.TextSub);
            e.DrawFocusRectangle();
        }

        /// <summary>
        /// ★ C-2（R8）：把**工作区**配色对齐到既有明暗引擎的当前配色
        /// （<see cref="SettingsThemeManager.CurrentPalette"/>）。
        /// <para>
        /// 排除项：预览画布（<c>_previewHost</c> 与各预览控件）保持主题产物本色 ——
        /// 画布显示的是主题真实观感，被工作区明暗染色等于伪造预览。
        /// </para>
        /// </summary>
        private void ApplyWorkspacePalette()
        {
            SettingsThemePalette palette = SettingsThemeManager.CurrentPalette;
            _palette = palette;

            BackColor = palette.MainBg;
            _canvasPanel.BackColor = palette.MainBg;
            _canvasToolbar.BackColor = palette.MainBg;
            _libraryPanel.BackColor = palette.SidebarBg;
            _libraryButtons.BackColor = palette.SidebarBg;
            _libraryTitle.ForeColor = palette.TextSub;
            _libraryList.BackColor = palette.SidebarBg;
            _status.BackColor = palette.SidebarBg;
            _statusSaved.ForeColor = palette.TextMain;
            _statusRoute.ForeColor = palette.TextSub;
            _inspector.BackColor = palette.CardBg;
            _inspector.ApplyPalette(palette);
            _docToolbar.BackColor = palette.CardBg;
            _previewPlaceholder.BackColor = palette.SurfaceBg;
            _previewPlaceholder.ForeColor = palette.TextSub;
            _targetLabel.ForeColor = palette.TextSub;
            _shapeNotice.ForeColor = palette.TextSub;

            // 工具栏里的说明文字统一走次要文字色；强调点（未保存标记）单独用强调色。
            foreach (ToolStripItem canvasItem in _canvasToolbar.Items)
            {
                if (canvasItem is ToolStripLabel canvasLabel) canvasLabel.ForeColor = palette.TextSub;
            }
            foreach (ToolStripItem docItem in _docToolbar.Items)
            {
                if (docItem is ToolStripLabel docLabel) docLabel.ForeColor = palette.TextSub;
            }
            _docName.ForeColor = palette.TextMain;
            _dirtyDot.ForeColor = palette.Primary;

            // 预览画布保持主题产物本色（排除项）。
            _previewHost.BackColor = PreviewBack;

            _libraryList.Invalidate();
            _previewHost.Invalidate();
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SettingsThemeManager.ThemeChanged -= OnWorkspaceThemeChanged;
                _librarySubtitleFont.Dispose();
                _previewDebounce?.Dispose();
                _recoveryTimer?.Dispose();
            }

            base.Dispose(disposing);
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

            // 重建列表项会触发 SelectedIndexChanged：抑制它，避免误开/误提示
            _suppressSelection = true;
            try
            {
                _libraryList.BeginUpdate();
                _libraryList.Items.Clear();
                foreach (var t in _themes) _libraryList.Items.Add(t.Name);
                _libraryList.EndUpdate();
                SyncSelectionToList();
            }
            finally
            {
                _suppressSelection = false;
            }
        }

        /// <summary>把当前文档的选中状态同步到列表（不触发打开流程）。</summary>
        private void SyncSelectionToList()
        {
            int index = _themes.FindIndex(t => string.Equals(t.Name, _selectedTheme, StringComparison.Ordinal));
            if (index >= 0)
            {
                _libraryList.SelectedIndex = index;
            }
            else if (_libraryList.SelectedIndex >= 0)
            {
                _libraryList.ClearSelected();
            }
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
            if (_suppressSelection) return;
            if (_libraryList.SelectedIndex < 0 || _libraryList.SelectedIndex >= _themes.Count)
            {
                return;
            }

            string name = _themes[_libraryList.SelectedIndex].Name;
            if (string.Equals(name, _selectedTheme, StringComparison.Ordinal)) return;

            // 未保存修改：保存／放弃继续，取消则恢复列表选中项
            if (!ConfirmCloseCurrentDocument())
            {
                ReselectList(_selectedTheme);
                return;
            }

            OpenTheme(name);
        }

        private void ReselectList(string name)
        {
            _suppressSelection = true;
            try
            {
                int index = _themes.FindIndex(t => string.Equals(t.Name, name, StringComparison.Ordinal));
                _libraryList.SelectedIndex = index >= 0 ? index : -1;
            }
            finally
            {
                _suppressSelection = false;
            }
        }

        private void OpenTheme(string name)
        {
            ThemeDocumentSession session;
            try
            {
                if (ThemeDocumentStore.TryLoadRecovery(name, out ThemeDraftSnapshot? snapshot) &&
                    snapshot is not null && snapshot.Revision > snapshot.SavedRevision)
                {
                    DialogResult choice = MessageBox.Show(
                        this,
                        $"检测到主题“{name}”的未保存恢复草稿。\n\n" +
                        "“是”= 恢复草稿；“否”= 丢弃草稿并打开磁盘版本。\n" +
                        "若源文件已被外部修改，恢复会打开独立副本，保存前需另存为新主题。\n" +
                        "恢复的草稿中如有旧版阈值编辑，会被自动清理（状态阈值来自当前生效设置，主题只负责颜色）。",
                        "恢复草稿",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (choice == DialogResult.Yes)
                    {
                        (session, bool conflictCopied) = ThemeDocumentSession.Restore(snapshot);
                        if (conflictCopied)
                        {
                            _statusSaved.Text = "源文件已变化：已打开恢复副本，保存前请另存为新主题。";
                        }
                    }
                    else
                    {
                        ThemeDocumentStore.DeleteRecovery(name);
                        session = ThemeDocumentSession.Open(name);
                    }
                }
                else
                {
                    session = ThemeDocumentSession.Open(name);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开主题失败：" + ex.Message, "主题工作室",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _session = session;
            _selectedTheme = name;
            _inspector.Bind(_session);
            ConfigureTargetSelector();
            // 代码路径（新建/复制/打开）同步列表选中项，不触发打开流程
            _suppressSelection = true;
            try
            {
                SyncSelectionToList();
            }
            finally
            {
                _suppressSelection = false;
            }
            UpdateDocumentUi();
            RefreshPreviewNow();
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
                    ConfigureTargetSelector();
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
                _recoveryTimer.Stop();
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
                _recoveryTimer.Stop();
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
                _statusSaved.Text = "已保存；应用入口未接线。";
                return;
            }

            try
            {
                ThemeApplyResult result = _applyGateway.ApplySavedTheme(_session.ThemeName, _session.Revision);
                if (result.Success)
                {
                    _statusSaved.Text = $"已保存并应用（修订 r{result.Revision}，{result.EffectiveRoute ?? "正常生效"}）。";
                }
                else if (result.Partial)
                {
                    _statusSaved.Text = $"主题已生效（{result.EffectiveTheme ?? "当前主题"}），但部分模块失败：" +
                        (result.Error ?? "可在设置中重试") + "；不存在原子回滚。";
                }
                else
                {
                    string stageDesc = result.Stage switch
                    {
                        ThemeApplyStage.SettingsPersisted => "Settings 持久化失败",
                        ThemeApplyStage.ThemeApplied => "运行时应用失败",
                        _ => "保存阶段失败"
                    };
                    _statusSaved.Text = $"已保存；{stageDesc}：" + (result.Error ?? "可在设置中重试");
                }
            }
            catch (Exception ex)
            {
                _statusSaved.Text = "已保存；应用异常：" + ex.Message;
            }
        }

        private void ExportThemeJsonAction()
        {
            if (_session is null)
            {
                _statusSaved.Text = "未打开主题，无法导出。";
                return;
            }

            using var sfd = new SaveFileDialog
            {
                Title = "导出主题 JSON",
                Filter = "JSON 文件 (*.json)|*.json",
                FileName = $"{_session.ThemeName}.json"
            };
            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                ThemeExportResult res = ThemeExportService.ExportJson(_session, sfd.FileName);
                _statusSaved.Text = res.Success
                    ? $"导出 JSON 成功（修订 r{_session.Revision}，{Path.GetFileName(sfd.FileName)}）。"
                    : "导出 JSON 失败：" + res.Error;
            }
        }

        private void ExportEffectImageAction()
        {
            if (_session is null)
            {
                _statusSaved.Text = "未打开主题，无法导出。";
                return;
            }

            using var sfd = new SaveFileDialog
            {
                Title = "导出主题效果图",
                Filter = "PNG 图片 (*.png)|*.png",
                FileName = _previewShape == PreviewShape.Taskbar
                    ? $"{_session.ThemeName}_taskbar.png"
                    : $"{_session.ThemeName}_preview.png"
            };
            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                var pref = _inspector.Target == ThemeEditTarget.GpuFallback
                    ? ImageRoutePreference.Classic
                    : ImageRoutePreference.Auto;
                ThemeExportImageFormFactor formFactor = _previewShape switch
                {
                    PreviewShape.Horizontal => ThemeExportImageFormFactor.Horizontal,
                    PreviewShape.Taskbar => ThemeExportImageFormFactor.Taskbar,
                    _ => ThemeExportImageFormFactor.Vertical
                };
                ThemeImageExportResult res = formFactor == ThemeExportImageFormFactor.Taskbar
                    ? ThemeExportService.ExportEffectImage(
                        _session, sfd.FileName, pref, formFactor,
                        _appSettings, ResolveLightMode(_appSettings))
                    : ThemeExportService.ExportEffectImage(_session, sfd.FileName, pref, formFactor);
                _statusSaved.Text = res.Success
                    ? $"导出效果图成功（路线：{res.Route}）。"
                    : "导出效果图失败：" + res.Error;
            }
        }

        private void PrepareSubmissionMaterialsAction()
        {
            if (_session is null)
            {
                _statusSaved.Text = "未打开主题，无法生成投稿资料。";
                return;
            }

            using var fbd = new FolderBrowserDialog
            {
                Description = "选择投稿资料输出文件夹",
                UseDescriptionForTitle = true
            };
            if (fbd.ShowDialog(this) == DialogResult.OK)
            {
                using var metadataDialog = new ThemeSubmissionDialog(_session.ThemeName);
                if (metadataDialog.ShowDialog(this) != DialogResult.OK) return;
                ThemeSubmissionResult res = ThemeExportService.PrepareSubmissionMaterials(
                    _session, fbd.SelectedPath, metadataDialog.Metadata);
                _statusSaved.Text = res.Success
                    ? $"投稿资料已生成（路径：{res.Directory}，待市场审核）。"
                    : "生成投稿资料失败：" + res.Error;
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
            SchedulePreviewRefresh();

            // 空闲 2 秒持久化恢复草稿
            _recoveryTimer.Stop();
            if (_session?.IsDirty == true)
            {
                _recoveryTimer.Start();
            }
        }

        /// <summary>防抖刷新预览（连续编辑合并到一次渲染）。</summary>
        private void SchedulePreviewRefresh()
        {
            _previewDebounce?.Stop();
            _previewDebounce?.Start();
        }

        /// <summary>立即把草稿交给同源预览控件（按预览形态路由）。</summary>
        private void RefreshPreviewNow()
        {
            if (_session is null)
            {
                _previewPlaceholder.Visible = true;
                _previewPlaceholder.BringToFront();
                return;
            }

            try
            {
                switch (_previewShape)
                {
                    case PreviewShape.Horizontal:
                        // GPU 横向预览接入：若主题请求 GPU 且当前为主效果目标，使用 _preview 的横向 GPU 能力
                        if (_session.Draft.Rendering?.IsGpuRequested == true && _inspector.Target == ThemeEditTarget.GpuMain)
                        {
                            EnsurePreviewControl();
                            _preview!.SetTheme(_session.Draft, ThemeEditTarget.GpuMain, PreviewShape.Horizontal);
                            if (_preview.IsGpuPreviewActive)
                            {
                                ShowPreviewControl(_preview);
                            }
                            else
                            {
                                // F05：GPU 不可用或运行期失败时，保持横向形态走 GDI 回退，
                                // 不能退成 ThemePreviewControl 的竖向 UILayout。
                                ShowHorizontalPreview();
                            }
                        }
                        else
                        {
                            ShowHorizontalPreview();
                        }
                        break;

                    case PreviewShape.Taskbar:
                        if (_taskbarPreview is null)
                        {
                            _taskbarPreview = new TaskbarPreviewControl
                            {
                                Dock = DockStyle.Fill,
                                BackColor = PreviewBack,
                            };
                            _previewHost.Controls.Add(_taskbarPreview);
                        }

                        // F06：任务栏预览必须使用工作室传入的当前设置快照，
                        // 不能只 SetTheme 而沿用控件构造时的空 Settings。
                        _taskbarPreview.ApplySettings(_appSettings?.DeepClone() ?? new Settings());
                        _taskbarPreview.LightMode = ResolveLightMode(_appSettings);
                        _taskbarPreview.SetTheme(_session.Draft);
                        ShowPreviewControl(_taskbarPreview);
                        break;

                    default:
                        EnsurePreviewControl();
                        _preview!.SetTheme(_session.Draft, _inspector.Target, PreviewShape.Vertical);
                        ShowPreviewControl(_preview);
                        break;
                }
            }
            catch (Exception ex)
            {
                _statusSaved.Text = "预览失败：" + ex.Message;
                return;
            }

            _previewPlaceholder.Visible = false;
            UpdateRouteStatus();
        }

        private void EnsurePreviewControl()
        {
            if (_preview is null)
            {
                _preview = new ThemePreviewControl
                {
                    Dock = DockStyle.Fill,
                    BackColor = PreviewBack,
                };
                _preview.GpuPreviewUnavailable += OnGpuPreviewUnavailable;
                _previewHost.Controls.Add(_preview);
            }
        }

        private void OnGpuPreviewUnavailable(object? sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || _session is null)
                    {
                        return;
                    }

                    // R02：失败通知只切换“现有画布的实际路线”，不能再走
                    // RefreshPreviewNow → SetTheme → UpdateTheme 的完整重建路径。
                    // 后者会在每次失败时重新从 raw theme 开始，形成无输入刷新循环。
                    if (_previewShape == PreviewShape.Horizontal)
                    {
                        ShowHorizontalPreview();
                    }
                    else if (_preview is not null)
                    {
                        ShowPreviewControl(_preview);
                    }

                    UpdateRouteStatus();
                }));
            }
            catch
            {
                // 关闭阶段忽略。
            }
        }

        private void ShowHorizontalPreview()
        {
            if (_horizontalPreview is null)
            {
                _horizontalPreview = new HorizontalPreviewControl
                {
                    Dock = DockStyle.Fill,
                    BackColor = PreviewBack,
                };
                _previewHost.Controls.Add(_horizontalPreview);
            }

            bool needsFallback = _inspector.Target == ThemeEditTarget.GpuFallback
                || (_inspector.Target == ThemeEditTarget.GpuMain && _preview?.IsGpuPreviewActive != true);
            Theme themeToRender = needsFallback
                ? LegacyFallbackStyleResolver.BuildDerivedTheme(_session!.Draft)
                : _session!.Draft;
            _horizontalPreview.SetTheme(themeToRender);
            ShowPreviewControl(_horizontalPreview);
        }

        private static bool ResolveLightMode(Settings? settings)
        {
            if (settings is null)
            {
                return false;
            }

            if (settings.SettingThemeMode == SettingThemeMode.Light)
            {
                return true;
            }

            if (settings.SettingThemeMode == SettingThemeMode.Dark)
            {
                return false;
            }

            return SettingsThemeManager.TryReadSystemUsesLightTheme() ?? false;
        }

        private void ShowPreviewControl(Control active)
        {
            if (_preview is not null) _preview.Visible = ReferenceEquals(_preview, active);
            if (_horizontalPreview is not null) _horizontalPreview.Visible = ReferenceEquals(_horizontalPreview, active);
            if (_taskbarPreview is not null) _taskbarPreview.Visible = ReferenceEquals(_taskbarPreview, active);
            active.Visible = true;
            active.BringToFront();
        }

        private void OnPreviewShapeChanged()
        {
            PreviewShape shape = _shapeCombo.SelectedIndex switch
            {
                1 => PreviewShape.Horizontal,
                2 => PreviewShape.Taskbar,
                _ => PreviewShape.Vertical
            };
            if (shape == _previewShape) return;
            _previewShape = shape;

            _shapeNotice.Text = shape switch
            {
                PreviewShape.Horizontal => "固定模拟数据 · 生产横排渲染",
                PreviewShape.Taskbar => "颜色由设置驱动，主题只影响布局",
                _ => string.Empty
            };

            RefreshPreviewNow();
        }

        private void UpdateRouteStatus()
        {
            if (_session is null)
            {
                _statusRoute.Text = "预览路线：—";
                return;
            }

            if (_previewShape == PreviewShape.Taskbar)
            {
                _statusRoute.Text = "预览形态：任务栏 · 常规绘制（颜色由设置驱动，布局随主题）";
                return;
            }

            bool gpuRequested = _session.Draft.Rendering?.IsGpuRequested == true;

            if (_previewShape == PreviewShape.Horizontal)
            {
                if (_inspector.Target == ThemeEditTarget.GpuFallback)
                {
                    _statusRoute.Text = "预览形态：横向 · 兼容外观（常规绘制）";
                    return;
                }

                if (gpuRequested && _preview?.IsGpuPreviewActive == true)
                {
                    _statusRoute.Text = "预览形态：横向 · GPU 场景（生产同源）";
                }
                else
                {
                    _statusRoute.Text = "预览形态：横向 · 常规绘制（生产同源）";
                }
                return;
            }

            // 竖向 Vertical
            if (_inspector.Target == ThemeEditTarget.GpuFallback)
            {
                _statusRoute.Text = "预览路线：兼容外观 · 常规绘制（生产同源）";
                return;
            }

            if (!gpuRequested)
            {
                _statusRoute.Text = "预览路线：经典渲染（同源）";
                return;
            }

            if (_preview?.IsGpuPreviewActive == true)
            {
                _statusRoute.Text = "预览路线：GPU 场景（同源） ｜ 目标：主效果";
            }
            else
            {
                string reason = _preview?.GpuPreviewError ?? "未启动";
                _statusRoute.Text = "预览路线：GPU 不可用 → 兼容外观（常规绘制）— " + reason;
            }
        }

        /// <summary>按语义角色点选定位到检查器对应的属性字段（N05）。</summary>
        public bool SelectPropertyByRole(ThemePropertyCatalog.SemanticElementRole role)
        {
            // ★ C-1a：按**当前草稿**解析 —— GPU 主效果的可编辑字段随材质不同，
            //   只有草稿感知的解析才会给出该目标下真实存在的描述符路径。
            Theme? draft = _session?.Draft;
            string path = ThemePropertyCatalog.ResolvePropertyPath(draft, role, _inspector.Target);
            if (path.Length == 0)
            {
                // 诚实告知"没有可编辑字段"，而不是指一个不存在的路径。
                _statusSaved.Text = "该编辑目标下没有与该语义元素对应的可编辑字段。";
                return false;
            }

            bool focused = _inspector.FocusProperty(path);
            if (focused)
            {
                _statusSaved.Text = $"已定位到属性：{path}";
            }
            return focused;
        }

        /// <summary>按文档类型配置编辑目标选择器（视图状态，不写主题、不制造脏状态）。</summary>
        private void ConfigureTargetSelector()
        {
            _suppressTargetChange = true;
            try
            {
                _targetCombo.Items.Clear();
                if (_session is null)
                {
                    _targetCombo.Items.Add("—");
                    _targetCombo.SelectedIndex = 0;
                    _targetCombo.Enabled = false;
                    return;
                }

                bool gpu = _session.Draft.Rendering?.IsGpuRequested == true;
                if (!gpu)
                {
                    _targetCombo.Items.Add("经典外观");
                    _targetCombo.SelectedIndex = 0;
                    _targetCombo.Enabled = false;
                }
                else
                {
                    _targetCombo.Items.Add("主效果（GPU）");
                    _targetCombo.Items.Add("兼容外观（回退）");
                    _targetCombo.SelectedIndex = _inspector.Target == ThemeEditTarget.GpuFallback ? 1 : 0;
                    _targetCombo.Enabled = true;
                }
            }
            finally
            {
                _suppressTargetChange = false;
            }
        }

        private void OnTargetChanged()
        {
            if (_suppressTargetChange || _session is null) return;
            if (_targetCombo.SelectedIndex < 0) return;

            ThemeEditTarget target = _targetCombo.SelectedIndex == 1
                ? ThemeEditTarget.GpuFallback
                : (_session.Draft.Rendering?.IsGpuRequested == true ? ThemeEditTarget.GpuMain : ThemeEditTarget.Classic);

            ApplyEditTarget(target, syncSelector: false);
        }

        /// <summary>
        /// 切换编辑目标的**唯一入口**（真实下拉与测试钩子共用）。
        /// <para>
        /// ★ R1 整改（计划 §5.1 A-2）：画布渲染目标由 <c>_inspector.Target</c> 决定
        /// （见 <see cref="RefreshPreviewNow"/> 的 default 分支），因此目标切换后
        /// **必须刷新画布** —— 旧实现只重绑检查器与状态栏，画布仍显示上一个目标的主题，
        /// 用户看到的是「改了没反应」。目标切换是低频离散操作，直接同步刷新（不做防抖）。
        /// </para>
        /// </summary>
        private void ApplyEditTarget(ThemeEditTarget target, bool syncSelector)
        {
            if (_session is null) return;

            // 只切换文档视图：不修改 rendering.backend，不触发换肤。
            _inspector.Bind(_session, target);
            if (syncSelector)
            {
                ConfigureTargetSelector();
            }
            UpdateRouteStatus();
            RefreshPreviewNow();
        }

        private void UpdateDocumentUi()
        {
            // ★ 局部变量单次读取：_session 是可空字段，而 hasDoc 只是布尔推断，
            //   编译器无法据此收窄 _session —— 原写法混用 `_session!` 与裸 `_session.ThemeName`，
            //   后者就是 1018 行的 CS8602。这里改为一次取值 + 派生只读量，语义逐条等价。
            ThemeDocumentSession? session = _session;
            bool hasDoc = session is not null;
            string docName = session?.ThemeName ?? "—";
            bool isDirty = session?.IsDirty ?? false;
            int undoCount = session?.UndoCount ?? 0;
            int redoCount = session?.RedoCount ?? 0;

            _docName.Text = docName;
            _dirtyDot.Visible = isDirty;
            _undoButton.Enabled = undoCount > 0;
            _redoButton.Enabled = redoCount > 0;
            _saveButton.Enabled = hasDoc;
            _statusSaved.Text = isDirty ? "未保存的更改" :
                hasDoc ? "已保存" : "就绪";
            UpdateRouteStatus();
            Text = "LiteMonitor 主题工作室" + (isDirty ? " — *" : string.Empty) +
                   (hasDoc ? " — " + docName : string.Empty);
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

        // ============================================================
        // 测试钩子（仅供单元测试驱动 UI，生产代码不调用）
        // ============================================================

        internal void OpenThemeForTesting(string themeName) => OpenTheme(themeName);

        /// <summary>★ E-1（G1）：模拟 DPI 变化，触发固定几何重算（测试可观测）。</summary>
        internal void ApplyDpiScaleForTesting(int deviceDpi) => ApplyDpiScale(deviceDpi);

        /// <summary>★ E-1（G1）：资料库分栏宽度（测试可观测）。</summary>
        internal int LibraryPanelWidthForTesting => _libraryPanel.Width;

        /// <summary>★ E-1（G1）：检查器分栏宽度（测试可观测）。</summary>
        internal int InspectorWidthForTesting => _inspector.Width;

        /// <summary>★ E-1（G1）：资料库列表行高（测试可观测）。</summary>
        internal int LibraryItemHeightForTesting => _libraryList.ItemHeight;

        /// <summary>★ E-2（G2）：真实编辑目标下拉（测试用它触发**真实**目标事件，而非测试钩子）。</summary>
        internal ToolStripComboBox TargetComboForTesting => _targetCombo;

        internal void RefreshPreviewForTesting() => RefreshPreviewNow();

        internal PropertyInspector InspectorForTesting => _inspector;

        /// <summary>
        /// 测试/诊断钩子：画布当前**实际渲染**的编辑目标（R1 整改新增可观测量）。
        /// 与检查器当前绑定的 Target 不同 —— R1 修复前两者会长期不一致
        /// （检查器切了，画布没切）。
        /// </summary>
        internal ThemeEditTarget? RenderedTargetForTesting => _preview?.RenderedTarget;

        /// <summary>
        /// 测试/诊断钩子：画布当前渲染主题的背景色（R1 整改新增可观测量）。
        /// 回退目标下画布渲染的是派生主题 ⇒ 此处是派生主题的背景色，
        /// 与草稿主题的背景色不同，可直接作为「目标切换是否真的作用到画布」的证据。
        /// </summary>
        internal string? RenderedBackgroundForTesting => _preview?.RenderedTheme?.Color.Background;

        /// <summary>测试/诊断钩子：GPU 预览会话代次（R6 整改新增可观测量）。</summary>
        internal long PreviewGenerationForTesting => _preview?.CurrentGeneration ?? 0;

        /// <summary>测试/诊断钩子：GPU 预览错误信息（R6 整改：UpdateTheme 失败必须可诊断）。</summary>
        internal string? PreviewErrorForTesting => _preview?.GpuPreviewError;

        /// <summary>测试钩子：按编辑目标重绑检查器（等价于画布工具栏切换）。</summary>
        internal void SelectTargetForTesting(ThemeEditTarget target)
        {
            // ★ R1 整改：测试钩子必须与真实下拉走同一条路径（含画布刷新），
            //   否则测试永远测不到「目标切换不刷新画布」这个缺陷。
            ApplyEditTarget(target, syncSelector: true);
        }

        /// <summary>测试钩子：切换预览形态（0=竖向, 1=横向, 2=任务栏）。</summary>
        internal void SelectPreviewShapeForTesting(int index)
        {
            _shapeCombo.SelectedIndex = index;
        }

        internal HorizontalPreviewControl? HorizontalPreviewForTesting => _horizontalPreview;

        internal TaskbarPreviewControl? TaskbarPreviewForTesting => _taskbarPreview;

        internal ToolStripDropDownButton ExportButtonForTesting => _exportButton;

        internal ThemePreviewControl? PreviewControlForTesting => _preview;

        // ★ ToolStripItem.Text 的可空标注使其读取结果为「可能为 null」，直接返回会触发 CS8603。
        //   WinForms 内部保证 Text 永不为 null（getter 返回 _text ?? string.Empty），此处显式兜底为 "",
        //   语义与原实现完全一致（空串而非 null），不改变任何调用方可见行为。
        internal string RouteStatusTextForTesting => _statusRoute.Text ?? "";

        internal string SavedStatusTextForTesting => _statusSaved.Text ?? "";

        internal void ApplyForTesting() => ApplySession();

        internal void ExportJsonForTesting(string path) => ThemeExportService.ExportJson(_session!, path);

        internal void ExportEffectImageForTesting(string path) => ThemeExportService.ExportEffectImage(_session!, path, ImageRoutePreference.Auto);

        internal void PrepareSubmissionForTesting(string dir) => ThemeExportService.PrepareSubmissionMaterials(_session!, dir);

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

}
