using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using LiteMonitor.Core.Actions.Services;
using LiteMonitor.src.Core.Scenarios;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.SystemServices; // B3:HardwareGateway
using LiteMonitor.src.Core.Actions;
using LiteMonitor.src.UI.Controls;
using LiteMonitor.src.UI.SettingsPage;
using LiteMonitor.Rendering.Configuration;

namespace LiteMonitor.src.UI
{
    public class SettingsForm : Form
    {
        private Settings _cfg; // Live Settings
        private Settings _draftCfg; // Draft Settings
        private UIController _ui;
        private MainForm _mainForm;
        
        // ★ 这两个面板都在构造函数里（InitializeComponent）创建并挂到窗体上 ⇒ 构造完成时一定已赋值，
        //   用 null! 占位声明（仅编译期用，不会真的为 null）。
        private FlowLayoutPanel _pnlNavContainer = null!; 
        private BufferedPanel _pnlContent = null!; // 使用现有的 BufferedPanel
        
        // 缓存所有页面实例
        private Dictionary<string, SettingsPageBase> _pages = new Dictionary<string, SettingsPageBase>();
        private string _currentKey = "";
        private SettingsEditState _editState = null!;
        private LiteButton _applyButton = null!;
        private Label _editStatus = null!;
        private bool _refreshingEditor = true;
        private bool _closeAccepted;
        private bool _lastApplyComplete = true;
        private bool _refreshQueued;
        private readonly HashSet<Control> _trackedControls = new();

        private string EditText(string zh, string en) => (_cfg.Language ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;

        // 恢复 WS_EX_COMPOSITED 以防止闪烁，同时配合页面卸载机制解决卡顿
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
                return cp;
            }
        }

        protected override void OnResizeBegin(EventArgs e)
        {
            this.SuspendLayout();
            base.OnResizeBegin(e);
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            this.ResumeLayout(true);
        }

        /// <summary>
        /// 释放页面缓存。
        ///
        /// <c>_pages</c> 里的页面只有**当前挂载**的那一个在 <c>_pnlContent</c> 的控件树里，
        /// 窗体 Dispose 顺着控件树只能碰到它。其余页面从未被挂载过
        /// （<see cref="SwitchPage"/> 里的 <c>_pnlContent.Controls.Clear()</c> 是「摘下来留着复用」，
        /// 不是「销毁」），窗体关掉后没有任何引用链能确定性地释放它们，
        /// 只能等 <see cref="Control"/> 的终结器回收 —— 在那之前它们的子控件窗口句柄与
        /// GDI 对象一直占着。
        ///
        /// 因此这里显式释放全部页面。对已经在控件树里的那个页面重复 Dispose 是安全的
        /// （<see cref="Control.Dispose()"/> 幂等），不必区分当前页。
        ///
        /// 覆盖范围（全仓库 5 个打开点都会走到这里）：
        /// 三处 <c>using { ShowDialog(); }</c>（MenuManager 设置项、MenuMonitorHelper ×2）由 using 调 Dispose；
        /// 两处 <c>new SettingsForm(...).Show()</c>（MainFormBizHelper.OpenSettings、TaskbarBizHelper 双击）
        /// 由 <see cref="Form.Close()"/> 在非模态路径上自己 Dispose —— 不是只触发 FormClosed 就结束。
        /// 已用 WinForms 探针确认：主窗体持有消息循环时，次级非模态窗体 Close() 返回时
        /// IsDisposed 已为 true、句柄已销毁、已从 Application.OpenForms 移除。
        /// 因此不需要额外挂 FormClosed 做兜底；挂了也只是多一次幂等调用。
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCachedPages();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// 释放 <c>_pages</c> 里的所有页面并清空缓存。可重复调用。
        ///
        /// 释放一个仍是容器子控件的页面时，<see cref="Control.Dispose()"/> 会顺手把它从父容器的
        /// <c>Controls</c> 里摘掉（实测 <c>Count</c> 1 → 0、<c>Contains</c> 为 false），
        /// 所以不存在「已释放却仍挂在 <c>_pnlContent</c> 里」的悬挂页，
        /// 调用前不必先手工 <c>Controls.Clear()</c>。
        /// </summary>
        private void DisposeCachedPages()
        {
            if (_pages.Count == 0) return;

            // 同一实例可能注册到多个导航键（插件市场/主题市场共用 PluginMarketPage）。
            // 必须按引用去重，否则第二次 Dispose 会释放已释放的 WinForms 控件。
            var disposed = new HashSet<SettingsPageBase>(ReferenceEqualityComparer.Instance);
            foreach (var page in _pages.Values)
            {
                if (disposed.Add(page))
                    page.Dispose();
            }

            _pages.Clear();
        }

        public SettingsForm(Settings cfg, UIController ui, MainForm mainForm)
        { 
            _cfg = cfg; 
            _ui = ui; 
            _mainForm = mainForm;
            
            // ★★★ Draft 机制核心：创建深拷贝 ★★★
            _draftCfg = _cfg.DeepClone();

            // E05：构建 UI **之前**初始化主题状态机——窗体从第一帧起就是正确的明暗，
            // 不做"先浅色再翻深"的闪烁。订阅 ThemeChanged 与系统配色变更（退出时注销）。
            SettingsThemeManager.Initialize(_cfg.SettingThemeMode);
            SettingsThemeManager.ThemeChanged += OnSettingsThemeChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            InitializeComponent(); 
            
            // ★★★ 关键点 1：构造时就初始化所有页面 ★★★
            InitPages(); 
            _editState = new SettingsEditState(_draftCfg);
            TrackEditor(_pnlContent);
            _refreshingEditor = false;
            RefreshEditStatus();
        }

        private void InitializeComponent()
        {
            UIUtils.ScaleFactor = this.DeviceDpi / 96f;

            this.Size = new Size(UIUtils.S(820), UIUtils.S(680));
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = true;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Text = LanguageManager.T("Menu.SettingsPanel");
            this.Font = new Font("Microsoft YaHei UI", 9F);
            this.BackColor = UIColors.MainBg;
            this.ShowInTaskbar = true;

            // 侧边栏
            var pnlSidebar = new Panel { Dock = DockStyle.Left, Width = UIUtils.S(160), BackColor = UIColors.SidebarBg };
            
            _pnlNavContainer = new FlowLayoutPanel 
            { 
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, 
                Padding = UIUtils.S(new Padding(0, 20, 0, 0)), BackColor = UIColors.SidebarBg
            };
            
            var line = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = UIColors.Border };
            pnlSidebar.Controls.Add(_pnlNavContainer);
            pnlSidebar.Controls.Add(line);
            this.Controls.Add(pnlSidebar);

            // 底部按钮
            var pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = UIUtils.S(60), BackColor = UIColors.MainBg };
            // Paint 里每次重绘都新建 Pen 且从未释放：底部栏每次被覆盖/重绘就漏一个 GDI 对象。
            // 用 using 收口；不缓存成静态字段是为了让主题换色时自动生效（缓存会留下旧颜色的 Pen）。
            pnlBottom.Paint += (s, e) =>
            {
                using var pen = new Pen(UIColors.Border);
                e.Graphics.DrawLine(pen, 0, 0, Width, 0);
            };

            var flowBtns = new FlowLayoutPanel 
            { 
                Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, 
                Padding = UIUtils.S(new Padding(0, 14, 20, 0)), WrapContents = false, BackColor = Color.Transparent 
            };
            
            var btnOk = new LiteButton(LanguageManager.T("Menu.OK"), true);
            var btnCancel = new LiteButton(LanguageManager.T("Menu.Cancel"), false);
            var btnApply = new LiteButton(LanguageManager.T("Menu.Apply"), false);
            var btnReset = new LiteButton(LanguageManager.T("Menu.Reset"), false) { ForeColor = UIColors.TextWarn };

            btnOk.Click += (s, e) => { if (ApplySettings()) { this.DialogResult = DialogResult.OK; this.Close(); } };
            btnCancel.Click += (s, e) => { this.Close(); };
            btnApply.Click += (s, e) => { ApplySettings(); };
            _applyButton = btnApply;
            _editStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = UIUtils.S(new Padding(16, 0, 0, 0)), ForeColor = UIColors.TextSub };
            pnlBottom.Controls.Add(_editStatus);
            
            btnReset.Click += (s, e) => 
            {
                if (MessageBox.Show(LanguageManager.T("Menu.ResetConfirm"), LanguageManager.T("Menu.Reset"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    try 
                    {
                        Settings.GlobalBlockSave = true;
                        SettingsHelper.DeleteStoredSettings();
                        Application.Restart();
                        Environment.Exit(0);
                    }
                    catch (Exception ex) { Settings.GlobalBlockSave = false; MessageBox.Show(ex.Message); }
                }
            };

            flowBtns.Controls.Add(btnOk); flowBtns.Controls.Add(btnCancel); flowBtns.Controls.Add(btnApply); flowBtns.Controls.Add(btnReset);
            pnlBottom.Controls.Add(flowBtns);
            this.Controls.Add(pnlBottom);

            // 内容区 - 使用 LiteUI.cs 中定义的 BufferedPanel
            _pnlContent = new BufferedPanel { Dock = DockStyle.Fill, Padding = new Padding(0) };
            this.Controls.Add(_pnlContent);
            
            pnlSidebar.BringToFront(); 
            pnlBottom.SendToBack(); 
            _pnlContent.BringToFront();
        }

        private void InitPages()
        {
            // 先释放上一批页面再清缓存：_pages.Clear() 只是丢引用，
            // 被丢掉的页面（及其子控件的窗口句柄）要等终结器才回收。
            // 当前只在构造时调用一次，这里是防止将来重调时退化成泄漏。
            DisposeCachedPages();

            while (_pnlNavContainer.Controls.Count > 0) _pnlNavContainer.Controls[0].Dispose();
            
            // 注册所有页面
            AddNav("MainPanel", LanguageManager.T("Menu.MainFormSettings"), new MainPanelPage());
            // ★「渲染路线」页：只读状态与诊断（开关在主面板页）。放主面板之后，
            //   让"改开关"与"看原因码"在同一处可达，不必在两页之间猜。
            AddNav("Rendering", LanguageManager.T("Menu.RenderingRoute"), new RenderingRoutePage());
            AddNav("Taskbar", LanguageManager.T("Menu.TaskbarSettings"), new TaskbarPage());
            AddNav("Monitor", LanguageManager.T("Menu.MonitorItemDisplay"), new MonitorPage());
            AddNav("Threshold", LanguageManager.T("Menu.Thresholds"), new ThresholdPage());
            AddNav("System", LanguageManager.T("Menu.SystemHardwar"), new SystemHardwarPage());
            AddNav("Plugins", LanguageManager.T("Menu.Plugins"), new PluginPage());
            var marketPage = new PluginMarketPage();
            marketPage.ImmediateChangeCompleted = field =>
            {
                if (IsDisposed) return;
                _refreshingEditor = true;
                try
                {
                    if (field == "Skin") _draftCfg.Skin = _cfg.Skin;
                    var live = _cfg.DeepClone();
                    _draftCfg.Themes = live.Themes;
                    _draftCfg.MarketPlugins = live.MarketPlugins;
                    _editState.AcceptField(_draftCfg, field);
                    _editState.AcceptField(_draftCfg, "Themes");
                    _editState.AcceptField(_draftCfg, "MarketPlugins");
                    if (_pages.TryGetValue("MainPanel", out var panel)) panel.OnShow();
                }
                finally { _refreshingEditor = false; }
                RefreshEditStatus();
            };
            AddNav("PluginMarket", LanguageManager.T("Menu.PluginMarket"), marketPage);
            AddNav("ThemeMarket", LanguageManager.T("Menu.ThemeMarket"), marketPage, MarketCategory.Themes);
            var scenarioPage = new ScenarioPage();
            scenarioPage.ApplyHandler = ApplyScenarioFromPage;
            AddNav("Scenarios", LanguageManager.T("Menu.Scenarios"), scenarioPage);
            AddNav("Diagnostics", LanguageManager.T("Menu.Diagnostics"), new DiagnosticPage());

            _pnlNavContainer.PerformLayout();
            SwitchPage("MainPanel");
        }

        private void AddNav(string key, string text, SettingsPageBase page, MarketCategory? marketCategory = null)
        {
            // ★★★ 关键点 2：将 Draft 传递给页面，而不是 Live Settings ★★★
            page.SetContext(_draftCfg, _mainForm, _ui);
            _pages[key] = page;
            var btn = new LiteNavBtn(text) { Tag = key };
            btn.Click += (s, e) => SwitchPage(key, marketCategory);
            _pnlNavContainer.Controls.Add(btn);
        }

        internal void SwitchPage(string key, MarketCategory? marketCategory = null)
        {
            bool wasRefreshing = _refreshingEditor;
            _refreshingEditor = true;
            try
            {
            // 同一市场页实例可挂两个入口；进入前先声明类别，不能依赖上次页签。
            if (marketCategory.HasValue && _pages.TryGetValue(key, out SettingsPageBase? target) &&
                target is PluginMarketPage marketPage)
            {
                marketPage.RequestCategory(marketCategory.Value);
            }

            if (_currentKey == key) return;
            _currentKey = key;

            // 更新导航按钮状态
            _pnlNavContainer.SuspendLayout();
            foreach (Control c in _pnlNavContainer.Controls)
                // ★ Tag 是 object?，这里只做字符串比较 ⇒ 先转成 string? 再比较，
                //   Tag 不是字符串时结果是 false（与原来 (string)b.Tag 抛 InvalidCastException 相比更安全，
                //   而正常运行路径下 Tag 一定是 string）。
                if (c is LiteNavBtn b) b.IsActive = ((string?)b.Tag == key);
            _pnlNavContainer.ResumeLayout();
            _pnlNavContainer.Refresh(); 
            Application.DoEvents();

            if (_pages.ContainsKey(key))
            {
                // ★★★ 核心修复开始 ★★★
                
                // 1. 挂起父容器布局：告诉系统“在我操作完之前，千万不要重绘”
                _pnlContent.SuspendLayout(); 
                
                try 
                {
                    // Controls.Clear 只是把旧页 Parent 置空，不会触发 OnVisibleChanged；
                    // 必须显式通知离页，页面才能停掉计时器/订阅。
                    foreach (Control child in _pnlContent.Controls)
                    {
                        if (child is SettingsPageBase oldPage)
                        {
                            oldPage.OnHide();
                            break;
                        }
                    }
                    _pnlContent.Controls.Clear();
                    var targetPage = _pages[key];
                    
                    // 2. 关键技：手动预设尺寸
                    // 在 Dock 生效前，先强制把它设为和父容器一样大。
                    // 这样即使 Layout 有微小延迟，肉眼看到的也是填满的状态。
                    targetPage.Size = _pnlContent.ClientSize; 
                    targetPage.Dock = DockStyle.Fill; // 双保险

                    _pnlContent.Controls.Add(targetPage);
                    targetPage.OnShow();
                }
                finally
                {
                    // 3. 恢复布局：此时控件大小已正确，系统一次性绘制最终画面
                    _pnlContent.ResumeLayout(); 
                }
                // ★★★ 核心修复结束 ★★★
            }
            }
            finally { _refreshingEditor = wasRefreshing; }
        }

        private string? ApplyScenarioFromPage(ScenarioProfile profile)
        {
            if (!ResolvePendingEdits()) return EditText("已取消切换情景。", "Scenario switch cancelled.");
            var service = new ScenarioService(
                new ScenarioProfileStore(),
                candidate => SettingsHelper.TrySave(candidate),
                (live, candidate) =>
                {
                    var before = live.CloneForDiff();
                    SettingsChanger.Merge(live, candidate);
                    return AppActions.ApplySettingsSelectivelyWithResults(before, live, _mainForm, _ui, false);
                },
                name => File.Exists(Path.Combine(ThemeManager.ThemeDir, name + ".json")));

            var outcome = service.Apply(profile, _cfg);
            if (outcome.Operation.OverallStatus is ApplyOperationStatus.Applied or ApplyOperationStatus.Partial)
            {
                SettingsChanger.Merge(_draftCfg, _cfg);
                SettingsThemeManager.SyncFromSettings(_cfg);
                ReloadEditor("Scenarios");
            }

            var failures = string.Join("; ", outcome.Operation.Modules
                .Where(module => !module.Applied)
                .Select(module => module.Module + ": " + (module.FailureReason ?? "failed")));
            string? error = outcome.Operation.OverallStatus == ApplyOperationStatus.Applied ? null : string.IsNullOrWhiteSpace(failures)
                ? "Scenario apply failed: " + outcome.Operation.OverallStatus
                : failures;
            if (_pages.TryGetValue("Scenarios", out var scenario) && scenario is ScenarioPage current)
                current.ReportApplication(error ?? EditText("已应用：", "Applied: ") + profile.Name, error != null);
            if (error != null) MessageBox.Show(this, error, EditText("情景未完全生效", "Scenario not fully applied"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return error;
        }
        private bool ApplySettings()
        {
            if (!HasPendingEdits()) return true;
            CollectDraftEdits();
            bool oldAutoStart = _cfg.AutoStart;

            // 保存逻辑顺序优化；同一实例可注册多个导航键（插件市场/主题市场），按引用只保存一次。
            var savedPages = new HashSet<SettingsPageBase>(ReferenceEqualityComparer.Instance);
            foreach (var kv in _pages)
            {
                if (kv.Key != "Monitor" && savedPages.Add(kv.Value))
                    kv.Value.CollectDraftEdits();
            }

            if (_pages.TryGetValue("Monitor", out SettingsPageBase? monitorPage) && savedPages.Add(monitorPage))
            {
                monitorPage.CollectDraftEdits();
            }
            
            // ★★★ Draft 机制核心：提交事务 ★★★
            // 1. 全局校验 (防止全隐藏死锁)
            // 2. 先在草稿副本上完成落盘，成功后才提交到 Live Settings。
            //    这样保存失败不会把内存中的 Live 配置改成“未落盘”的状态。
            Settings before = _cfg.CloneForDiff();
            Settings candidate = _draftCfg.DeepClone();
            candidate = _editState.CreateCandidate(_cfg, candidate);
            if (SettingsEditState.EnsureInteractiveEntry(candidate))
                MessageBox.Show(this, EditText("已保留托盘图标，以便重新打开设置。", "The tray icon has been kept visible so settings remain accessible."), "LiteMonitor++", MessageBoxButtons.OK, MessageBoxIcon.Information);


            // 2.5 ★★★ [多显卡] 落盘前先同步显卡监控项 ★★★
            //     必须在保存之前 —— ApplyAllSettings 里那一次 Ensure
            //     发生在保存之后, 磁盘上的 MonitorItems 会比内存晚一轮。
            int detectedGpuCount = HardwareGateway.DetectedGpuCount;
            int gpuCap = detectedGpuCount > 0
                ? detectedGpuCount
                : Math.Max(1, candidate.MonitoredGpuCount);
            GpuItemSync.Ensure(candidate, availableGpuCount: gpuCap);

            // 3. 持久化保存；失败时保持对话框打开、Live 配置不变。
            //    S01：凭据 DPAPI 保护失败单独提示（未保存、磁盘未动、哪个参数没写入），
            //    与一般写入失败（ConfigWriteBlockedShort）区分开，绝不谎报成功。
            SettingsSaveOutcome saveOutcome = candidate.TrySaveWithOutcome();
            if (saveOutcome != SettingsSaveOutcome.Saved)
            {
                string saveErrorText = saveOutcome == SettingsSaveOutcome.CredentialProtectionFailed
                    ? LanguageManager.T("ConfigCredentialProtectFailed") +
                      (string.IsNullOrEmpty(SettingsHelper.LastCredentialProtectionError)
                          ? ""
                          : "\n\n" + SettingsHelper.LastCredentialProtectionError)
                    : LanguageManager.T("ConfigWriteBlockedShort");
                MessageBox.Show(
                    saveErrorText,
                    "LiteMonitor++",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }

            SettingsChanger.Merge(_cfg, candidate);
            bool autoStartChanged = oldAutoStart != _cfg.AutoStart;

            // 4. 应用副作用 (刷新界面)
            // GPU 开关卡自「渲染路线」页迁回主面板页（渲染路线交互优化）⇒ 重试申请从 MainPanelPage 读取。
            if (_pages.TryGetValue("MainPanel", out SettingsPageBase? panelPage) &&
                panelPage is MainPanelPage mainPanel &&
                mainPanel.GpuStartupRetryRequested)
            {
                // 【S06】重试可能被守卫拒绝（存储不可用 / 状态异常）——必须如实提示，
                // 不能让用户以为"点了应用=已重试"。拒绝时守卫状态不变，可再次手动申请。
                bool retryAllowed = _ui.AllowGpuStartupRetryAfterExplicitSettingsApply();
                mainPanel.AcknowledgeGpuStartupRetryRequest();
                if (!retryAllowed)
                {
                    string? guardMessage = GpuStartupGuard.Shared.StatusMessage;
                    MessageBox.Show(
                        LanguageManager.T("ConfigGpuRetryBlocked") +
                        (string.IsNullOrWhiteSpace(guardMessage) ? "" : "\n\n" + guardMessage),
                        "LiteMonitor++",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            var applyResults = AppActions.ApplySettingsSelectivelyWithResults(before, _cfg, _mainForm, _ui, autoStartChanged);
            var pageErrors = new List<string>();
            foreach (var page in _pages.Values.Distinct())
            {
                if (page is MonitorPage) continue;
                try { page.ApplyCommittedSettings(_cfg); }
                catch (Exception ex) { pageErrors.Add(page.GetType().Name + ": " + ex.Message); }
            }
            var failedModules = applyResults.Where(r => !r.Applied).ToList();
            _lastApplyComplete = failedModules.Count == 0 && pageErrors.Count == 0;
            if (!_lastApplyComplete)
            {
                string detail = string.Join("\n", failedModules.Select(r =>
                    string.IsNullOrWhiteSpace(r.FailureReason) ? r.Module : $"{r.Module}: {r.FailureReason}").Concat(pageErrors));
                MessageBox.Show(
                    LanguageManager.T("SettingsApplyPartial") + "\n\n" + detail,
                    "LiteMonitor++",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            // 5. [Fix] Rebase Draft to match Live
            // 将 Live 环境中由插件生成的最新监控项同步回 Draft，并保留动态显示属性
            SettingsChanger.RebaseDraftMonitorItems(_cfg, _draftCfg);

            // E05：保存成功 ⇒ 主题状态机提交到新模式（预览态落成为已提交；无变化时为幂等空操作）
            SettingsThemeManager.SyncFromSettings(_cfg);
            ReloadEditor(_currentKey);
            _editStatus.Text = _lastApplyComplete ? EditText("已保存并应用", "Saved and applied") : EditText("已保存，部分功能未生效", "Saved; some changes could not be applied");
            return _lastApplyComplete;
        }

        private void CollectDraftEdits()
        {
            foreach (var page in _pages.Values.Distinct()) page.CollectDraftEdits();
        }

        private bool HasPendingEdits()
        {
            CollectDraftEdits();
            return _editState.HasChanges(_draftCfg) || _pages.Values.OfType<MainPanelPage>().Any(p => p.GpuStartupRetryRequested);
        }

        private void RefreshEditStatus()
        {
            if (_editState == null || IsDisposed) return;
            bool pending = HasPendingEdits();
            _applyButton.Enabled = pending;
            _editStatus.Text = pending ? EditText("有未保存的更改", "Unsaved changes") : EditText("没有未保存的更改", "No unsaved changes");
        }

        private void TrackEditor(Control control)
        {
            if (!_trackedControls.Add(control)) return;
            control.ControlAdded += (_, e) => { if (e.Control != null) TrackEditor(e.Control); };
            void Changed(object? sender, EventArgs e)
            {
                if (_refreshingEditor || !control.ContainsFocus && control is not Button and not LiteColorPicker) return;
                var page = FindPage(control);
                if (page?.IsWriteBackSuppressed == true) return;
                if (page is MonitorPage monitor) monitor.NoteUserEdit(control);
                if (_refreshQueued || !IsHandleCreated) return;
                _refreshQueued = true;
                BeginInvoke((Action)(() => { _refreshQueued = false; if (!IsDisposed) RefreshEditStatus(); }));
            }
            if (control is TextBoxBase) control.TextChanged += Changed;
            if (control is CheckBox check) check.CheckedChanged += Changed;
            if (control is ComboBox combo) combo.SelectedIndexChanged += Changed;
            if (control is NumericUpDown number) number.ValueChanged += Changed;
            if (control is TrackBar slider) slider.ValueChanged += Changed;
            if (control is LiteColorPicker picker) picker.ColorChanged += Changed;
            if (control is Button button) button.Click += Changed;
            foreach (Control child in control.Controls) TrackEditor(child);
        }

        private static SettingsPageBase? FindPage(Control control)
        {
            for (Control? parent = control; parent != null; parent = parent.Parent)
                if (parent is SettingsPageBase page) return page;
            return null;
        }

        private bool ResolvePendingEdits()
        {
            if (!HasPendingEdits()) return true;
            var choice = MessageBox.Show(this,
                EditText("有未保存的更改。\n是：保存并应用后继续\n否：放弃更改后继续\n取消：返回编辑", "There are unsaved changes.\nYes: save and apply, then continue\nNo: discard and continue\nCancel: return to editing"),
                EditText("未保存的更改", "Unsaved changes"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel) return false;
            if (choice == DialogResult.Yes) return ApplySettings();
            ReloadEditor(_currentKey);
            return true;
        }

        private void ReloadEditor(string page)
        {
            _refreshingEditor = true;
            try
            {
                foreach (var old in _pages.Values.Distinct()) old.OnHide();
                _pnlContent.Controls.Clear();
                _draftCfg = _cfg.DeepClone();
                _currentKey = "";
                InitPages();
                _trackedControls.RemoveWhere(c => c.IsDisposed);
                _editState.Accept(_draftCfg);
                if (_pages.ContainsKey(page)) SwitchPage(page);
                TrackEditor(_pnlContent);
            }
            finally { _refreshingEditor = false; }
            RefreshEditStatus();
        }

        // ── E05：明暗主题接线 ──────────────────────────────────────────────────
        private void OnSettingsThemeChanged(object? sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke(OnSettingsThemeChanged); return; }
            // 双层重绘：全局取色表推送（LiteUI 控件按 IThemeRefreshable 自刷）+ 槽位重映射兜底历史控件
            SettingsThemeManager.ApplyTo(this);
            SettingsThemeApplier.Apply(this);
        }

        private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return; // 应用配色变更走 General
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke(OnSystemColorChanged); return; }
            OnSystemColorChanged();
        }

        private void OnSystemColorChanged()
        {
            if (IsDisposed) return;
            bool? light = SettingsThemeManager.TryReadSystemUsesLightTheme();
            if (light.HasValue) SettingsThemeManager.ApplySystemColorChange(light.Value);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_closeAccepted && _editState != null)
            {
                if (!ResolvePendingEdits()) { e.Cancel = true; return; }
                _closeAccepted = true;
            }
            // E05：取消/关闭（含保存后关闭——此时已提交，为幂等空操作）丢弃未保存预览
            SettingsThemeManager.CancelPreview();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SettingsThemeManager.ThemeChanged -= OnSettingsThemeChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            base.OnFormClosed(e);
        }
    }
}
