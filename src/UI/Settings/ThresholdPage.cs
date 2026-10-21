using System;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class ThresholdPage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;

        public ThresholdPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = UIUtils.S(new Padding(20)) }; 
            this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null || _isLoaded) return;

            _container.SuspendLayout();
            ClearAndDispose(_container.Controls);

            // === 1. Alert Temp ===
            var grpAlert = new LiteSettingsGroup(LanguageManager.T("Menu.AlertTemp"));
            
            grpAlert.AddToggle(this, "Menu.AlertTemp", 
                () => Config.AlertTempEnabled, 
                v => Config.AlertTempEnabled = v);

            grpAlert.AddInt(this, "Menu.AlertThreshold", "°C", 
                () => Config.AlertTempThreshold, 
                v => Config.AlertTempThreshold = v, 
                width: 80, color: UIColors.TextCrit);

            AddGroupToPage(grpAlert);

            // === 2. Hardware ===
            var grpHardware = new LiteSettingsGroup(LanguageManager.T("Menu.GeneralHardware"));
            grpHardware.AddHint(LanguageManager.T("Menu.ThresholdsTips"));
            
            // ★ 只有**硬件负载（%）**钳制到 0~100 —— 它是百分比，超出即无意义。
            //   其余指标一律**不设**上下限：
            //     · 温度可以为负（室外/冷藏场景是合法配置），不加 ">= 0"；
            //     · MB/s 与 MB 的上限是**场景相关**的，通用层猜一个只会误伤用户的合法配置。
            //   ★ 钳制不是静默改值 —— 会带行内提示，用户看得见自己的输入被调整过。
            grpHardware.AddThreshold(this, LanguageManager.T("Menu.HardwareLoad"), "%", Config.Thresholds.Load,
                clamp: raw =>
                {
                    double v = Math.Min(100.0, Math.Max(0.0, raw));
                    return (v, (string?)null);
                });
            grpHardware.AddThreshold(this, LanguageManager.T("Menu.HardwareTemp"), "°C", Config.Thresholds.Temp);

            AddGroupToPage(grpHardware);

            // === 3. Network & Disk ===
            var grpNet = new LiteSettingsGroup(LanguageManager.T("Menu.NetworkDiskSpeed"));
            
            grpNet.AddThreshold(this, LanguageManager.T("Menu.DiskIOSpeed"), "MB/s", Config.Thresholds.DiskIOMB);
            grpNet.AddThreshold(this, LanguageManager.T("Menu.UploadSpeed"), "MB/s", Config.Thresholds.NetUpMB);
            grpNet.AddThreshold(this, LanguageManager.T("Menu.DownloadSpeed"), "MB/s", Config.Thresholds.NetDownMB);

            AddGroupToPage(grpNet);

            // === 4. Data Usage ===
            var grpData = new LiteSettingsGroup(LanguageManager.T("Menu.DailyTraffic"));

            grpData.AddThreshold(this, LanguageManager.T("Items.DATA.DayUp"), "MB", Config.Thresholds.DataUpMB);
            grpData.AddThreshold(this, LanguageManager.T("Items.DATA.DayDown"), "MB", Config.Thresholds.DataDownMB);

            AddGroupToPage(grpData);

            _container.ResumeLayout();
            _isLoaded = true;
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = UIUtils.S(new Padding(0, 0, 0, 20)) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }
    }
}
