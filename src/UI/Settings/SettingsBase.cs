using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public interface ISettingsPage
    {
        void Save();
        void OnShow();
    }

    public class SettingsPageBase : UserControl, ISettingsPage
    {
        protected Settings Config;
        protected MainForm MainForm;
        protected UIController UI;
        
        // ★★★ Refresh Mechanism for Deferred Load ★★★
        protected List<Action> _refreshActions = new List<Action>();

        // ★★★ 回写抑制（W3）★★★
        //  设置页的控件是「立即绑定」：控件一变就把新值写回配置（draft）。
        //  但**程序化**改控件（加载时同步、异步刷新选项、清空后重填）也会触发同样的事件，
        //  于是「界面还没让用户做任何事」就把用户的配置改掉了。
        //  作用域的作用：在这段时间里，控件产生的回写被丢弃，配置保持原样。
        private int _suppressWriteBackDepth;

        // ★ 配置写入被阻止时的提示条（页面顶部）。
        private Panel? _configBlockedBanner;
        private Label? _configBlockedLabel;

        public static readonly Color GlobalBackColor = Color.FromArgb(249, 249, 249); 

        /// <summary>当前是否处于回写抑制作用域内。</summary>
        public bool IsWriteBackSuppressed => _suppressWriteBackDepth > 0;

        /// <summary>
        /// 进入回写抑制作用域；<c>Dispose</c> 时退出（支持嵌套）。
        /// <para>★ 用法：<c>using (page.BeginSuppressWriteBack()) { ...程序化改控件... }</c></para>
        /// </summary>
        public IDisposable BeginSuppressWriteBack()
        {
            _suppressWriteBackDepth++;
            return new SuppressWriteBackScope(this);
        }

        /// <summary>回写抑制作用域的句柄。</summary>
        private sealed class SuppressWriteBackScope : IDisposable
        {
            private readonly SettingsPageBase _page;
            public SuppressWriteBackScope(SettingsPageBase page) => _page = page;
            public void Dispose()
            {
                if (_page._suppressWriteBackDepth > 0) _page._suppressWriteBackDepth--;
            }
        }

        public SettingsPageBase() 
        {
            this.BackColor = GlobalBackColor; 
            this.Dock = DockStyle.Fill;
            this.DoubleBuffered = true;
        }

        public void SetContext(Settings cfg, MainForm form, UIController ui)
        {
            Config = cfg;
            MainForm = form;
            UI = ui;

            // ★★★ Fix: Ensure UI controls reflect the Config values immediately ★★★
            // This prevents controls from defaulting to false/0 and overwriting the config on Save()
            if (Config != null)
            {
                foreach (var action in _refreshActions)
                {
                    action.Invoke();
                }
            }
        }

        public void RegisterRefresh(Action action)
        {
            _refreshActions.Add(action);
        }

        public virtual void OnShow()
        {
            // Base implementation can be empty or used for common logic
            
            // Execute refresh actions to ensure UI reflects the latest Config
            if (Config != null)
            {
                foreach (var action in _refreshActions)
                {
                    action.Invoke();
                }
            }

            // ★ 配置写入被阻止时必须让用户看见（否则他改了半天气，退出后全没了）。
            UpdateConfigWriteBlockedNotice();
        }

        /// <summary>
        /// 显示/隐藏「配置暂时无法读取，本次修改不会保存」的提示条。
        /// <para>★ 放在基类里 ⇒ 七个设置页**全部**自动带上，不用逐页加代码。</para>
        /// <para>★ 条件：<see cref="SettingsHelper.GlobalBlockSave"/> 为真 —— 配置读不出来时，
        ///   本次会话的任何自动落盘都被阻止（<c>SettingsHelper.Save()</c> 里已有判空早退）。</para>
        /// </summary>
        private void UpdateConfigWriteBlockedNotice()
        {
            bool blocked = SettingsHelper.GlobalBlockSave;

            if (!blocked)
            {
                if (_configBlockedBanner != null) _configBlockedBanner.Visible = false;
                return;
            }

            if (_configBlockedBanner == null)
            {
                _configBlockedBanner = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = UIUtils.S(30),
                    BackColor = Color.FromArgb(255, 248, 230),
                    Padding = UIUtils.S(new Padding(12, 0, 12, 0)),
                };
                _configBlockedLabel = new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = UIColors.TextWarn,
                    Font = UIFonts.Regular(9F),
                    Text = LanguageManager.T("Menu.ConfigWriteBlocked"),
                };
                _configBlockedBanner.Controls.Add(_configBlockedLabel);

                // ★ 追加到**最后** ⇒ 在停靠顺序里最先处理 ⇒ 出现在页面最上方。
                //   （WinForms 的停靠按 z 序反向进行：越靠后的越先停靠。）
                this.Controls.Add(_configBlockedBanner);
            }
            else
            {
                _configBlockedBanner.Visible = true;
                if (_configBlockedLabel != null)
                    _configBlockedLabel.Text = LanguageManager.T("Menu.ConfigWriteBlocked");
            }
        }

        public virtual void Save()
        {
            // Immediate binding means we don't need to do anything here.
            // But we keep the method because ISettingsPage requires it.
            // Subclasses (like PluginPage) can override it for post-save logic.
        }

        protected void ClearAndDispose(Control.ControlCollection controls)
        {
            // Clear pending actions whenever we destroy the UI controls
            _refreshActions.Clear();

            while (controls.Count > 0)
            {
                var c = controls[0];
                controls.RemoveAt(0);
                c.Dispose();
            }
        }

        protected void EnsureSafeVisibility(LiteCheck chkHideMain, LiteCheck chkHideTray, LiteCheck chkShowTaskbar)
        {
            bool hideMain = chkHideMain != null ? chkHideMain.Checked : Config.HideMainForm;
            bool hideTray = chkHideTray != null ? chkHideTray.Checked : Config.HideTrayIcon;
            bool showBar  = chkShowTaskbar != null ? chkShowTaskbar.Checked : Config.ShowTaskbar;

            if (hideMain && hideTray && !showBar)
            {
                MessageBox.Show("为了防止程序无法唤出，不能同时隐藏 [主界面]、[托盘图标] 和 [任务栏]。", 
                                "安全警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                
                if (chkHideMain != null) chkHideMain.Checked = false;
                if (chkHideTray != null) chkHideTray.Checked = false;
                if (chkShowTaskbar != null) chkShowTaskbar.Checked = true;
            }
        }
    }
}
