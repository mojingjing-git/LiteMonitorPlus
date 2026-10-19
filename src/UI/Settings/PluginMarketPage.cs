using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    /// <summary>
    /// 插件市场：从社区插件索引仓库拉取插件列表，一键下载安装。
    /// 索引由 awesome-litemonitor-plugins 仓库自动生成（index.json）。
    /// </summary>
    public class PluginMarketPage : SettingsPageBase
    {
        private Panel _container;
        private LiteUnderlineInput _srcInput;
        private MarketIndex _index;
        private string _sourceUrl = PluginMarket.DefaultIndexUrl;
        private string _statusText = "";
        private bool _statusIsError;
        private bool _busy;

        public PluginMarketPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);

            _container = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = UIColors.MainBg,
                Padding = new Padding(20, 5, 20, 20)
            };
            this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            base.OnShow();
            RebuildUI();

            // 第一次进这个页面才自动拉一次，之后由用户手动刷新
            if (_index == null && !_busy)
            {
                _ = LoadIndexAsync();
            }
        }

        private void RebuildUI()
        {
            if (_container == null || _container.IsDisposed) return;

            _container.SuspendLayout();
            while (_container.Controls.Count > 0)
            {
                var c = _container.Controls[0];
                _container.Controls.RemoveAt(0);
                c.Dispose();
            }
            _container.AutoScrollMinSize = new Size(0, 0);

            // ---------- 1. 插件源 + 刷新 ----------
            var srcInput = new LiteUnderlineInput(_sourceUrl, "", "", 420, null, HorizontalAlignment.Left);
            _srcInput = srcInput;

            var btnRefresh = new LiteButton(LanguageManager.T("Menu.PluginMarketRefresh"), true);
            btnRefresh.Size = new Size(UIUtils.S(90), UIUtils.S(26));
            btnRefresh.Margin = new Padding(UIUtils.S(8), 0, 0, 0);
            btnRefresh.Click += async (s, e) => await LoadIndexAsync();

            var rightBox = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            rightBox.Controls.Add(srcInput);
            rightBox.Controls.Add(btnRefresh);

            var srcRow = new LiteActionRow(LanguageManager.T("Menu.PluginMarketSource"), rightBox);
            srcRow.Dock = DockStyle.Top;
            _container.Controls.Add(srcRow);

            // ---------- 2. 状态 ----------
            if (!string.IsNullOrEmpty(_statusText))
            {
                var lblStatus = new Label
                {
                    Text = _statusText,
                    AutoSize = true,
                    ForeColor = _statusIsError ? UIColors.TextCrit : UIColors.TextSub,
                    Font = new Font("Microsoft YaHei UI", 9F),
                    Padding = new Padding(0, 4, 0, 4)
                };
                lblStatus.Dock = DockStyle.Top;
                _container.Controls.Add(lblStatus);
            }

            // ---------- 3. 插件列表 ----------
            if (_index == null)
            {
                var lbl = new Label
                {
                    Text = LanguageManager.T("Menu.PluginMarketLoading"),
                    AutoSize = true,
                    ForeColor = UIColors.TextSub,
                    Padding = new Padding(0, 10, 0, 10)
                };
                lbl.Dock = DockStyle.Top;
                _container.Controls.Add(lbl);
            }
            else if (_index.Plugins.Count == 0)
            {
                var lbl = new Label
                {
                    Text = LanguageManager.T("Menu.PluginMarketEmpty"),
                    AutoSize = true,
                    ForeColor = UIColors.TextSub,
                    Padding = new Padding(0, 10, 0, 10)
                };
                lbl.Dock = DockStyle.Top;
                _container.Controls.Add(lbl);
            }
            else
            {
                var hint = new LiteHintRow(LanguageManager.T("Menu.PluginMarketHint"));
                _container.Controls.Add(hint);

                foreach (var entry in _index.Plugins)
                {
                    AddPluginGroup(entry);
                }
            }

            var spacer = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.Transparent };
            _container.Controls.Add(spacer);
            _container.Controls.SetChildIndex(spacer, 0);

            _container.ResumeLayout(true);
            _container.PerformLayout();
        }

        private void AddPluginGroup(MarketPluginEntry entry)
        {
            bool installed = PluginMarket.IsInstalled(entry);
            var group = new LiteSettingsGroup($"{entry.Name}   v{entry.Version}   (ID: {entry.Id})");

            // 头部按钮：装了就显示卸载，没装就显示安装
            var btn = new LiteHeaderBtn(installed
                ? LanguageManager.T("Menu.PluginMarketUninstall")
                : LanguageManager.T("Menu.PluginMarketInstall"));
            btn.SetColor(installed ? Color.IndianRed : UIColors.Primary);
            btn.Click += async (s, e) =>
            {
                if (installed) await UninstallAsync(entry);
                else await InstallAsync(entry);
            };
            group.AddHeaderAction(btn);

            if (!string.IsNullOrEmpty(entry.Description))
            {
                group.AddHint(entry.Description);
            }

            // 状态徽标
            var badge = new Label
            {
                Text = entry.StatusLabel + (installed ? "　·　" + LanguageManager.T("Menu.PluginMarketInstalled") : ""),
                AutoSize = true,
                ForeColor = entry.Status == "ok" ? UIColors.TextSub : UIColors.TextWarn,
                Font = new Font("Microsoft YaHei UI", 9F),
                Padding = new Padding(0, 2, 0, 6)
            };
            group.AddFullItem(badge);

            // 需要的额外准备
            if (entry.Requires != null && entry.Requires.Count > 0)
            {
                group.AddHint("📌 " + LanguageManager.T("Menu.PluginMarketRequires") + "：" + string.Join("；", entry.Requires));
            }

            if (!string.IsNullOrEmpty(entry.Note))
            {
                group.AddHint("💬 " + entry.Note);
            }

            if (entry.Renamed && !string.IsNullOrEmpty(entry.OriginalId))
            {
                group.AddHint("🏷 " + LanguageManager.T("Menu.PluginMarketRenamed") + $"：{entry.OriginalId} → {entry.Id}");
            }

            if (!string.IsNullOrEmpty(entry.Source))
            {
                var link = new LiteLink("🔗 " + LanguageManager.T("Menu.PluginMarketSourceLink"), () => OpenUrl(entry.Source));
                group.AddFullItem(link);
            }

            var spacer = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent };
            _container.Controls.Add(spacer);
            _container.Controls.SetChildIndex(spacer, 0);
            _container.Controls.Add(group);
            _container.Controls.SetChildIndex(group, 0);
        }

        private static void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        private async Task LoadIndexAsync()
        {
            if (_busy) return;
            _busy = true;

            string url = _srcInput?.Inner?.Text?.Trim();
            if (string.IsNullOrEmpty(url)) url = PluginMarket.DefaultIndexUrl;
            _sourceUrl = url;

            _statusText = LanguageManager.T("Menu.PluginMarketLoading");
            _statusIsError = false;
            RebuildUI();

            try
            {
                var index = await PluginMarket.FetchIndexAsync(url);
                _index = index;
                _statusText = LanguageManager.T("Menu.PluginMarketLoaded") + $"（{index.Plugins.Count}）";
                _statusIsError = false;
            }
            catch (Exception ex)
            {
                _index = null;
                _statusText = LanguageManager.T("Menu.PluginMarketFailed") + " " + ex.Message;
                _statusIsError = true;
            }
            finally
            {
                _busy = false;
                RebuildUI();
            }
        }

        private async Task InstallAsync(MarketPluginEntry entry)
        {
            if (_busy) return;
            if (!PluginMarket.IsSafeFileName(entry.File))
            {
                _statusText = LanguageManager.T("Menu.PluginMarketFailed") + " " + entry.Name + "（文件名不安全）";
                _statusIsError = true;
                RebuildUI();
                return;
            }

            _busy = true;
            _statusText = LanguageManager.T("Menu.PluginMarketInstalling") + entry.Name + " ...";
            _statusIsError = false;
            RebuildUI();

            try
            {
                await PluginMarket.InstallAsync(entry);

                // 重新加载插件模板，新实例会自动出现在「插件管理」页（默认不启用）
                PluginManager.Instance.LoadPlugins(PluginMarket.PluginsDir);

                _statusText = LanguageManager.T("Menu.PluginMarketInstallOk") + entry.Name +
                              " — " + LanguageManager.T("Menu.PluginMarketGoEnable");
                _statusIsError = false;
            }
            catch (UnauthorizedAccessException)
            {
                _statusText = LanguageManager.T("Menu.PluginMarketFailed") + entry.Name + "（没有写入权限，请把程序装在可写目录）";
                _statusIsError = true;
            }
            catch (Exception ex)
            {
                _statusText = LanguageManager.T("Menu.PluginMarketFailed") + entry.Name + " " + ex.Message;
                _statusIsError = true;
            }
            finally
            {
                _busy = false;
                RebuildUI();
            }
        }

        private Task UninstallAsync(MarketPluginEntry entry)
        {
            try
            {
                PluginMarket.Uninstall(entry);
                PluginManager.Instance.LoadPlugins(PluginMarket.PluginsDir);
                _statusText = LanguageManager.T("Menu.PluginMarketUninstallOk") + entry.Name;
                _statusIsError = false;
            }
            catch (Exception ex)
            {
                _statusText = LanguageManager.T("Menu.PluginMarketFailed") + entry.Name + " " + ex.Message;
                _statusIsError = true;
            }
            RebuildUI();
            return Task.CompletedTask;
        }
    }
}
