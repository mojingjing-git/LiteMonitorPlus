using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Actions;
using LiteMonitor.src.UI.Controls;
using LiteMonitor.src.UI.SettingsPage;

namespace LiteMonitor.src.UI
{
    public class SettingsForm : Form
    {
        private Settings _cfg; // Live Settings
        private Settings _draftCfg; // Draft Settings
        private UIController _ui;
        private MainForm _mainForm;
        
        private FlowLayoutPanel _pnlNavContainer; 
        private BufferedPanel _pnlContent; // 使用现有的 BufferedPanel
        
        // 缓存所有页面实例
        private Dictionary<string, SettingsPageBase> _pages = new Dictionary<string, SettingsPageBase>();
        private string _currentKey = "";

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

            foreach (var kv in _pages)
            {
                kv.Value.Dispose();
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
            
            InitializeComponent(); 
            
            // ★★★ 关键点 1：构造时就初始化所有页面 ★★★
            InitPages(); 
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

            btnOk.Click += (s, e) => { ApplySettings(); this.DialogResult = DialogResult.OK; this.Close(); };
            btnCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };
            btnApply.Click += (s, e) => { ApplySettings(); };
            
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

            _pnlNavContainer.Controls.Clear();
            
            // 注册所有页面
            AddNav("MainPanel", LanguageManager.T("Menu.MainFormSettings"), new MainPanelPage());
            AddNav("Taskbar", LanguageManager.T("Menu.TaskbarSettings"), new TaskbarPage());
            AddNav("Monitor", LanguageManager.T("Menu.MonitorItemDisplay"), new MonitorPage());
            AddNav("Threshold", LanguageManager.T("Menu.Thresholds"), new ThresholdPage());
            AddNav("System", LanguageManager.T("Menu.SystemHardwar"), new SystemHardwarPage());
            AddNav("Plugins", LanguageManager.T("Menu.Plugins"), new PluginPage());
            AddNav("PluginMarket", LanguageManager.T("Menu.PluginMarket"), new PluginMarketPage());

            _pnlNavContainer.PerformLayout();
            SwitchPage("MainPanel");
        }

        private void AddNav(string key, string text, SettingsPageBase page)
        {
            // ★★★ 关键点 2：将 Draft 传递给页面，而不是 Live Settings ★★★
            page.SetContext(_draftCfg, _mainForm, _ui);
            _pages[key] = page;
            var btn = new LiteNavBtn(text) { Tag = key };
            btn.Click += (s, e) => SwitchPage(key);
            _pnlNavContainer.Controls.Add(btn);
        }

        public void SwitchPage(string key)
        {
            if (_currentKey == key) return;
            _currentKey = key;

            // 更新导航按钮状态
            _pnlNavContainer.SuspendLayout();
            foreach (Control c in _pnlNavContainer.Controls)
                if (c is LiteNavBtn b) b.IsActive = ((string)b.Tag == key);
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

        private void ApplySettings()
        {
            bool oldAutoStart = _cfg.AutoStart;

            // 保存逻辑顺序优化
            foreach (var kv in _pages) 
            {
                if (kv.Key != "Monitor") kv.Value.Save(); 
            }
            
            if (_pages.ContainsKey("Monitor")) 
            {
                _pages["Monitor"].Save();
            }
            
            // ★★★ Draft 机制核心：提交事务 ★★★
            // 1. 全局校验 (防止全隐藏死锁)
            bool noInteractiveWindow = 
                (_draftCfg.HideMainForm || _draftCfg.ClickThrough) && 
                (!_draftCfg.ShowTaskbar || _draftCfg.TaskbarClickThrough) &&
                _draftCfg.HideTrayIcon;

            if (noInteractiveWindow)
            {
                // 自动纠正：如果所有可交互入口都被封死（隐藏或穿透+隐藏托盘），强制显示托盘
                _draftCfg.HideTrayIcon = false;
                MessageBox.Show("为了防止所有可交互入口都被死锁（隐藏或穿透+隐藏托盘），已强制显示托盘图标。", "LiteMonitor++", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // 2. 合并变更到 Live Settings
            SettingsChanger.Merge(_cfg, _draftCfg);
            bool autoStartChanged = oldAutoStart != _cfg.AutoStart;

            // 2.5 ★★★ [多显卡] 落盘前先同步显卡监控项 ★★★
            //     必须在 _cfg.Save() 之前 —— ApplyAllSettings 里那一次 Ensure
            //     发生在保存之后, 磁盘上的 MonitorItems 会比内存晚一轮。
            int detectedGpuCount = LiteMonitor.src.SystemServices.HardwareMonitor.TryGetDetectedGpuCount();
            int gpuCap = detectedGpuCount > 0
                ? detectedGpuCount
                : Math.Max(1, _cfg.MonitoredGpuCount);
            GpuItemSync.Ensure(_cfg, availableGpuCount: gpuCap);

            // 3. 持久化保存
            _cfg.Save();
            
            // 4. 应用副作用 (刷新界面)
            AppActions.ApplyAllSettings(_cfg, _mainForm, _ui, autoStartChanged);

            // 5. [Fix] Rebase Draft to match Live
            // 将 Live 环境中由插件生成的最新监控项同步回 Draft，并保留动态显示属性
            SettingsChanger.RebaseDraftMonitorItems(_cfg, _draftCfg);
        }
    }
}
