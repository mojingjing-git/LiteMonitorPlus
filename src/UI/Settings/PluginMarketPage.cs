using System;
using System.Drawing;
using System.IO;
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
    ///
    /// 插件源是锁死的（D1）—— 界面不提供修改入口，地址写死在 PluginMarket.DefaultIndexUrl。
    /// </summary>
    public class PluginMarketPage : SettingsPageBase
    {
        /// <summary>插件在市场页上的安装状态</summary>
        private enum InstallState
        {
            NotInstalled,  // 未安装
            NeedReinstall, // 记录还在但文件没了（用户手动删了）→ 需重装
            Updatable,     // 已安装且索引里的 sha256 变了 → 可更新
            Installed      // 已安装且是最新版
        }

        private Panel _container;
        private MarketIndex? _index;
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

            // 每次进入页面都刷新一次：更新检测要拿到索引里的最新 sha256
            if (!_busy)
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

            // ---------- 1. 插件源（只读，锁死）+ 刷新 ----------
            var btnRefresh = new LiteButton(LanguageManager.T("Menu.PluginMarketRefresh"), true);
            btnRefresh.Size = new Size(UIUtils.S(90), UIUtils.S(26));
            btnRefresh.Margin = new Padding(UIUtils.S(8), 0, 0, 0);
            btnRefresh.Click += async (s, e) => await LoadIndexAsync();

            var repoLink = new LiteLink(PluginMarket.DefaultIndexUrl, () => OpenUrl(PluginMarket.DefaultIndexUrl));
            repoLink.Margin = new Padding(0, 0, UIUtils.S(8), 0);
            repoLink.AutoSize = true;

            var rightBox = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            rightBox.Controls.Add(repoLink);
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
                    Font = UIFonts.Regular(9F),
                    Padding = new Padding(0, 4, 0, 4)
                };
                lblStatus.Dock = DockStyle.Top;
                _container.Controls.Add(lblStatus);
            }

            // ---------- 3. 启动自检提示 ----------
            if (PluginSelfCheck.SuspiciousFiles.Count > 0)
            {
                var names = string.Join("、", PluginSelfCheck.SuspiciousFiles.Select(Path.GetFileName));
                var lblWarn = new Label
                {
                    Text = string.Format(LanguageManager.T("Menu.PluginMarketSelfCheck"), names),
                    AutoSize = true,
                    ForeColor = UIColors.TextWarn,
                    Font = UIFonts.Regular(9F),
                    Padding = new Padding(0, 4, 0, 4)
                };
                lblWarn.Dock = DockStyle.Top;
                _container.Controls.Add(lblWarn);
            }

            // ---------- 4. 插件列表 ----------
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

        /// <summary>判定某插件在市场页上的安装状态</summary>
        private static InstallState GetState(MarketPluginEntry entry, out MarketPluginRecord? record)
        {
            record = PluginMarketState.Find(entry.Id);

            string path = Path.Combine(PluginMarket.PluginsDir, PluginMarket.GetInstallFileName(entry.Id));
            bool fileExists = File.Exists(path);

            if (record == null) return InstallState.NotInstalled;

            // 记录在但文件没了 → 用户手动删了
            if (!fileExists) return InstallState.NeedReinstall;

            // 索引里的 sha256 变了 → 可更新（老索引没有 sha256 时退化为"已安装"）
            if (!string.IsNullOrEmpty(entry.Sha256) &&
                !string.Equals(record.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                return InstallState.Updatable;

            return InstallState.Installed;
        }

        private void AddPluginGroup(MarketPluginEntry entry)
        {
            var state = GetState(entry, out var record);

            string displayName = PluginI18n.Pick(entry.Name, entry.NameEn);
            var group = new LiteSettingsGroup($"{displayName}   v{entry.Version}   (ID: {entry.Id})");

            // 头部按钮：按状态给出对应动作
            string btnText;
            Color btnColor;
            switch (state)
            {
                case InstallState.Installed:
                    btnText = LanguageManager.T("Menu.PluginMarketUninstall");
                    btnColor = Color.IndianRed;
                    break;
                case InstallState.Updatable:
                    btnText = LanguageManager.T("Menu.PluginMarketUpdate");
                    btnColor = UIColors.Primary;
                    break;
                case InstallState.NeedReinstall:
                    btnText = LanguageManager.T("Menu.PluginMarketReinstallAction");
                    btnColor = UIColors.Primary;
                    break;
                default:
                    btnText = LanguageManager.T("Menu.PluginMarketInstall");
                    btnColor = UIColors.Primary;
                    break;
            }

            var btn = new LiteHeaderBtn(btnText);
            btn.SetColor(btnColor);
            btn.Click += async (s, e) =>
            {
                if (state == InstallState.Installed) await UninstallAsync(entry);
                else await InstallAsync(entry);
            };
            group.AddHeaderAction(btn);

            string description = PluginI18n.Pick(entry.Description, entry.DescriptionEn);
            if (!string.IsNullOrEmpty(description))
            {
                group.AddHint(description);
            }

            // 状态徽标：状态说明 + 安装状态
            var badge = new Label
            {
                Text = entry.StatusLabel + "　·　" + GetStateLabel(state),
                AutoSize = true,
                ForeColor = entry.Status == "ok" ? UIColors.TextSub : UIColors.TextWarn,
                Font = UIFonts.Regular(9F),
                Padding = new Padding(0, 2, 0, 6)
            };
            group.AddFullItem(badge);

            // ★ 未经验证徽标：仓库侧的审核 Agent 机制在客户端的落点
            if (!entry.Verified)
            {
                var unverified = new Label
                {
                    Text = LanguageManager.T("Menu.PluginMarketUnverified") + "　—　" +
                           LanguageManager.T("Menu.PluginMarketUnverifiedHint"),
                    AutoSize = true,
                    ForeColor = UIColors.TextWarn,
                    Font = UIFonts.Regular(9F),
                    Padding = new Padding(0, 0, 0, 6)
                };
                group.AddFullItem(unverified);
            }

            // 需要的额外准备
            if (entry.Requires != null && entry.Requires.Count > 0)
            {
                group.AddHint(LanguageManager.T("Menu.PluginMarketRequires") + "：" + string.Join("；", entry.Requires));
            }

            if (!string.IsNullOrEmpty(entry.Note))
            {
                group.AddHint(entry.Note);
            }

            if (entry.Renamed && !string.IsNullOrEmpty(entry.OriginalId))
            {
                group.AddHint(LanguageManager.T("Menu.PluginMarketRenamed") + $"：{entry.OriginalId} → {entry.Id}");
            }

            // 双链接：插件介绍页（含 README 渲染）+ 源码
            if (!string.IsNullOrEmpty(entry.PageUrl))
            {
                var pageLink = new LiteLink(LanguageManager.T("Menu.PluginMarketPageLink"), () => OpenUrl(entry.PageUrl));
                group.AddFullItem(pageLink);
            }

            if (!string.IsNullOrEmpty(entry.Source))
            {
                var link = new LiteLink(LanguageManager.T("Menu.PluginMarketSourceLink"), () => OpenUrl(entry.Source));
                group.AddFullItem(link);
            }

            var spacer = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent };
            _container.Controls.Add(spacer);
            _container.Controls.SetChildIndex(spacer, 0);
            _container.Controls.Add(group);
            _container.Controls.SetChildIndex(group, 0);
        }

        private static string GetStateLabel(InstallState state)
        {
            switch (state)
            {
                case InstallState.Installed: return LanguageManager.T("Menu.PluginMarketInstalled");
                case InstallState.Updatable: return LanguageManager.T("Menu.PluginMarketUpdatable");
                case InstallState.NeedReinstall: return LanguageManager.T("Menu.PluginMarketReinstall");
                default: return "";
            }
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

            _statusText = LanguageManager.T("Menu.PluginMarketLoading");
            _statusIsError = false;
            RebuildUI();

            try
            {
                var index = await PluginMarket.FetchIndexAsync(PluginMarket.DefaultIndexUrl);
                _index = index;
                _statusText = LanguageManager.T("Menu.PluginMarketLoaded") + $"（{index.Plugins.Count}）";
                _statusIsError = false;
            }
            catch (Exception ex)
            {
                _index = null;
                _statusText = LanguageManager.T("Menu.PluginMarketFailed") + " " + DescribeError(ex);
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

            _busy = true;
            _statusText = LanguageManager.T("Menu.PluginMarketInstalling") + PluginI18n.Pick(entry.Name, entry.NameEn) + " ...";
            _statusIsError = false;
            RebuildUI();

            try
            {
                await PluginMarket.InstallAsync(entry);

                // 重新加载插件模板，新实例会自动出现在「插件管理」页（默认不启用）
                PluginManager.Instance.LoadPlugins(PluginMarket.PluginsDir);

                _statusText = LanguageManager.T("Menu.PluginMarketInstallOk") + PluginI18n.Pick(entry.Name, entry.NameEn) +
                              " — " + LanguageManager.T("Menu.PluginMarketGoEnable");
                _statusIsError = false;
            }
            catch (UnauthorizedAccessException)
            {
                _statusText = LanguageManager.T("Menu.PluginMarketNeedAdmin");
                _statusIsError = true;
            }
            catch (Exception ex)
            {
                _statusText = LanguageManager.T("Menu.PluginMarketFailed") + " " + DescribeError(ex);
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
                if (!PluginMarket.Uninstall(entry, out var reason))
                {
                    _statusText = LanguageManager.T("Menu.PluginMarketFailed") + " " + DescribeReason(reason);
                    _statusIsError = true;
                    RebuildUI();
                    return Task.CompletedTask;
                }

                PluginManager.Instance.LoadPlugins(PluginMarket.PluginsDir);
                _statusText = LanguageManager.T("Menu.PluginMarketUninstallOk") + PluginI18n.Pick(entry.Name, entry.NameEn);
                _statusIsError = false;
            }
            catch (Exception ex)
            {
                _statusText = LanguageManager.T("Menu.PluginMarketFailed") + " " + DescribeError(ex);
                _statusIsError = true;
            }
            RebuildUI();
            return Task.CompletedTask;
        }

        /// <summary>把市场操作失败原因映射成对应的本地化文案</summary>
        private static string DescribeReason(MarketErrorReason reason)
        {
            switch (reason)
            {
                case MarketErrorReason.NotManaged: return LanguageManager.T("Menu.PluginMarketNotManaged");
                case MarketErrorReason.BuiltinConflict: return LanguageManager.T("Menu.PluginMarketBuiltinProtected");
                case MarketErrorReason.TargetExists: return LanguageManager.T("Menu.PluginMarketTargetExists");
                case MarketErrorReason.RecordCorrupted: return LanguageManager.T("Menu.PluginMarketNotManaged");
                case MarketErrorReason.InvalidId: return LanguageManager.T("Menu.PluginMarketFailed");
                default: return LanguageManager.T("Menu.PluginMarketFailed");
            }
        }

        /// <summary>把异常转成可读的提示文案</summary>
        private static string DescribeError(Exception ex)
        {
            if (ex is PluginMarketException pm)
                return DescribeReason(pm.Reason) + " (" + pm.Message + ")";
            return ex.Message;
        }
    }
}
