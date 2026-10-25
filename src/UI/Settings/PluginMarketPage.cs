using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.ThemeEditor;
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
        /// <summary>市场页分类（M4）：同一份索引的插件段 / themes 附加段（D1=A/D3）</summary>

        private Panel _container;
        private MarketIndex? _index;
        private string _statusText = "";
        private bool _statusIsError;
        private bool _busy;
        private MarketCategory _category = MarketCategory.Plugins;

        /// <summary>
        /// 测试接缝：安装状态解析器（id, 是否主题）→ 安装状态。
        /// <para>默认走产品实现（查市场安装记录 + 目标文件是否存在）；测试用它避免动
        /// settings.json 全局单例。<b>被测的是界面刷新逻辑，不是状态解析逻辑。</b></para>
        /// </summary>
        internal Func<string, bool, MarketInstallState>? InstallStateResolverOverride { get; set; }

        /// <summary>索引来源（默认走网络；测试可注入固定索引）。</summary>
        private readonly IMarketIndexProvider _indexProvider;

        // ---- 主题市场：单活跃实时预览窗格（D5：同一时刻全市场只有一个 GPU 呈现表面）----
        private Panel? _previewHost;
        private ThemePreviewControl? _livePreview;
        private string? _selectedThemeId;

        public PluginMarketPage() : this(new NetworkMarketIndexProvider())
        {
        }

        /// <summary>测试接缝：注入固定索引来源（避免验收用例依赖网络）。</summary>
        internal PluginMarketPage(IMarketIndexProvider indexProvider)
        {
            _indexProvider = indexProvider ?? throw new ArgumentNullException(nameof(indexProvider));

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
            // 预览窗格引用先摘掉（控件本体在下面统一 Dispose，重建时按选中项重建）
            _livePreview = null;
            _previewHost = null;
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

            // ---------- 1.5 分类切换：插件 / 主题（M4，同一份索引的两个附加段） ----------
            var btnPlugins = new LiteButton(LanguageManager.T("Menu.PluginMarketTab"), _category == MarketCategory.Plugins);
            btnPlugins.Size = new Size(UIUtils.S(76), UIUtils.S(24));
            btnPlugins.Margin = new Padding(UIUtils.S(8), 0, 0, 0);
            btnPlugins.Click += (s, e) => SwitchCategory(MarketCategory.Plugins);

            var btnThemes = new LiteButton(LanguageManager.T("Menu.ThemeMarketTab"), _category == MarketCategory.Themes);
            btnThemes.Size = new Size(UIUtils.S(76), UIUtils.S(24));
            btnThemes.Margin = new Padding(UIUtils.S(8), 0, 0, 0);
            btnThemes.Click += (s, e) => SwitchCategory(MarketCategory.Themes);

            var catBox = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            catBox.Controls.Add(btnPlugins);
            catBox.Controls.Add(btnThemes);

            var catRow = new LiteActionRow(LanguageManager.T("Menu.PluginMarketCategory"), catBox);
            catRow.Dock = DockStyle.Top;
            _container.Controls.Add(catRow);

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

            // ---------- 4. 列表（插件 / 主题） ----------
            if (_category == MarketCategory.Themes)
            {
                BuildThemeSection();
            }
            else if (_index == null)
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

            // 主题分类 + 有选中 ⇒ 重建单活跃实时预览窗格
            if (_category == MarketCategory.Themes && _selectedThemeId != null)
            {
                BuildPreviewPane();
            }

            _container.ResumeLayout(true);
            _container.PerformLayout();
        }

        /// <summary>判定某插件在市场页上的安装状态</summary>
        private static MarketInstallState GetState(MarketPluginEntry entry, out MarketPluginRecord? record)
        {
            record = PluginMarketState.Find(entry.Id);

            string path = Path.Combine(PluginMarket.PluginsDir, PluginMarket.GetInstallFileName(entry.Id));
            bool fileExists = File.Exists(path);

            if (record == null) return MarketInstallState.NotInstalled;

            // 记录在但文件没了 → 用户手动删了
            if (!fileExists) return MarketInstallState.NeedReinstall;

            // 索引里的 sha256 变了 → 可更新（老索引没有 sha256 时退化为"已安装"）
            if (!string.IsNullOrEmpty(entry.Sha256) &&
                !string.Equals(record.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                return MarketInstallState.Updatable;

            return MarketInstallState.Installed;
        }

        /// <summary>解析插件的安装状态（测试接缝可替换数据来源）。</summary>
        private MarketInstallState ResolvePluginState(MarketPluginEntry entry)
        {
            if (InstallStateResolverOverride != null)
            {
                return InstallStateResolverOverride(entry.Id, false);
            }
            return GetState(entry, out _);
        }

        /// <summary>解析主题的安装状态（测试接缝可替换数据来源）。</summary>
        private MarketInstallState ResolveThemeState(MarketThemeEntry entry)
        {
            if (InstallStateResolverOverride != null)
            {
                return InstallStateResolverOverride(entry.Id, true);
            }
            return GetThemeState(entry, out _);
        }

        private void AddPluginGroup(MarketPluginEntry entry)
        {
            var state = ResolvePluginState(entry);

            string displayName = PluginI18n.Pick(entry.Name, entry.NameEn);
            var group = new LiteSettingsGroup($"{displayName}   v{entry.Version}   (ID: {entry.Id})");

            // 头部按钮：按状态给出对应动作
            string btnText;
            Color btnColor;
            switch (state)
            {
                case MarketInstallState.Installed:
                    btnText = LanguageManager.T("Menu.PluginMarketUninstall");
                    btnColor = Color.IndianRed;
                    break;
                case MarketInstallState.Updatable:
                    btnText = LanguageManager.T("Menu.PluginMarketUpdate");
                    btnColor = UIColors.Primary;
                    break;
                case MarketInstallState.NeedReinstall:
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
                if (state == MarketInstallState.Installed) await UninstallAsync(entry);
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

        private static string GetStateLabel(MarketInstallState state)
        {
            switch (state)
            {
                case MarketInstallState.Installed: return LanguageManager.T("Menu.PluginMarketInstalled");
                case MarketInstallState.Updatable: return LanguageManager.T("Menu.PluginMarketUpdatable");
                case MarketInstallState.NeedReinstall: return LanguageManager.T("Menu.PluginMarketReinstall");
                default: return "";
            }
        }

        // ================================================================
        // 主题市场（M4/D5）
        // ================================================================

        /// <summary>测试接缝：切到指定页签（等价于点击分类按钮）。</summary>
        internal void SwitchCategory(MarketCategory category)
        {
            if (_category == category) return;
            _category = category;
            _selectedThemeId = null;
            RebuildUI();

            // 首次切到主题分类时索引可能还没拉过
            if (_index == null && !_busy)
            {
                _ = LoadIndexAsync();
            }
        }

        /// <summary>判定某主题在市场页上的安装状态（口径与插件一致；主题目录 = ThemesDir）</summary>
        private static MarketInstallState GetThemeState(MarketThemeEntry entry, out MarketPluginRecord? record)
        {
            record = ThemeMarketState.Find(entry.Id);

            string path = Path.Combine(ThemeMarket.ThemesDir, entry.Id + ".json");
            bool fileExists = File.Exists(path);

            if (record == null) return MarketInstallState.NotInstalled;
            if (!fileExists) return MarketInstallState.NeedReinstall;
            if (!string.IsNullOrEmpty(entry.Sha256) &&
                !string.Equals(record.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                return MarketInstallState.Updatable;
            return MarketInstallState.Installed;
        }

        private void BuildThemeSection()
        {
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
                return;
            }

            if (_index.Themes.Count == 0)
            {
                var empty = new Label
                {
                    Text = LanguageManager.T("Menu.ThemeMarketEmpty"),
                    AutoSize = true,
                    ForeColor = UIColors.TextSub,
                    Padding = new Padding(0, 10, 0, 10)
                };
                empty.Dock = DockStyle.Top;
                _container.Controls.Add(empty);
                return;
            }

            // 诚实性说明（D5）：预览一律固定背景 + 演示数据，不抓真实桌面
            var hint = new LiteHintRow(LanguageManager.T("Menu.ThemeMarketPreviewFixed"));
            _container.Controls.Add(hint);

            foreach (var entry in _index.Themes)
            {
                AddThemeGroup(entry);
            }
        }

        private void AddThemeGroup(MarketThemeEntry entry)
        {
            var state = ResolveThemeState(entry);

            string displayName = PluginI18n.Pick(entry.Name, entry.NameEn);
            bool isGpu = string.Equals(entry.Route, ThemeRenderingConfig.GpuBackend, StringComparison.OrdinalIgnoreCase);
            string routeBadge = isGpu ? "GPU" : "Legacy";
            var group = new LiteSettingsGroup($"{displayName}   v{entry.Version}   [{routeBadge}]   (ID: {entry.Id})");

            // 头部动作：预览 + 安装/卸载
            var btnPreview = new LiteHeaderBtn(LanguageManager.T("Menu.ThemeMarketPreviewAction"));
            btnPreview.Click += (s, e) => SelectThemeForPreview(entry);
            group.AddHeaderAction(btnPreview);

            string btnText;
            Color btnColor;
            switch (state)
            {
                case MarketInstallState.Installed:
                    btnText = LanguageManager.T("Menu.PluginMarketUninstall");
                    btnColor = Color.IndianRed;
                    break;
                case MarketInstallState.Updatable:
                    btnText = LanguageManager.T("Menu.PluginMarketUpdate");
                    btnColor = UIColors.Primary;
                    break;
                case MarketInstallState.NeedReinstall:
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
                if (state == MarketInstallState.Installed) UninstallThemeAsync(entry);
                else await InstallThemeAsync(entry);
            };
            group.AddHeaderAction(btn);

            string description = PluginI18n.Pick(entry.Description, entry.DescriptionEn);
            if (!string.IsNullOrEmpty(description))
            {
                group.AddHint(description);
            }

            // 状态徽标：路线 · 材质 · 安装状态
            var badge = new Label
            {
                Text = routeBadge + " · " + entry.Material + "　·　" + GetStateLabel(state),
                AutoSize = true,
                ForeColor = UIColors.TextSub,
                Font = UIFonts.Regular(9F),
                Padding = new Padding(0, 2, 0, 6)
            };
            group.AddFullItem(badge);

            // ★ 未经验证徽标：仓库侧审核机制在客户端的落点（与插件一致）
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

            // ★ 卡片快照（D5）：懒渲染 + 磁盘缓存；点击快照 = 选中 → 实时预览
            var snap = new PictureBox
            {
                Size = new Size(UIUtils.S(360), UIUtils.S(200)),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(16, 20, 24),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 0, 6)
            };
            snap.Click += (s, e) => SelectThemeForPreview(entry);
            group.AddFullItem(snap);
            ThemePreviewService.RequestSnapshot(entry, pngPath =>
            {
                if (snap.IsDisposed) return;
                if (pngPath != null)
                {
                    try { snap.Image = Image.FromFile(pngPath); } catch { }
                }
            });

            if (!string.IsNullOrEmpty(entry.PageUrl))
            {
                var pageLink = new LiteLink(LanguageManager.T("Menu.PluginMarketPageLink"), () => OpenUrl(entry.PageUrl));
                group.AddFullItem(pageLink);
            }

            var spacer = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent };
            _container.Controls.Add(spacer);
            _container.Controls.SetChildIndex(spacer, 0);
            _container.Controls.Add(group);
            _container.Controls.SetChildIndex(group, 0);
        }

        private async Task InstallThemeAsync(MarketThemeEntry entry)
        {
            if (_busy) return;
            _busy = true;
            _statusText = LanguageManager.T("Menu.PluginMarketInstalling") + PluginI18n.Pick(entry.Name, entry.NameEn) + " ...";
            _statusIsError = false;
            RebuildUI();

            try
            {
                await ThemeMarket.InstallAsync(entry);
                _statusText = LanguageManager.T("Menu.PluginMarketInstallOk") + PluginI18n.Pick(entry.Name, entry.NameEn);
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

        /// <summary>
        /// 卸载市场主题（D7 守卫）：目标主题是当前 Skin 时，先经
        /// AppActions.ApplyThemeAndLayout 切到出厂默认主题，切换成功才删文件；失败如实报错。
        /// </summary>
        private Task UninstallThemeAsync(MarketThemeEntry entry)
        {
            try
            {
                var cfg = Settings.Load();
                var switchToDefault = new Action(() =>
                {
                    cfg.Skin = "DarkFlat_Classic";   // Settings.Skin 的出厂默认（Settings.cs）
                    cfg.Save();
                    LiteMonitor.src.Core.Actions.AppActions.ApplyThemeAndLayout(cfg, UI, MainForm);
                });

                if (!ThemeMarket.TryUninstall(entry, switchToDefault, out var reason))
                {
                    _statusText = LanguageManager.T("Menu.PluginMarketFailed") + " " + DescribeReason(reason);
                    _statusIsError = true;
                    RebuildUI();
                    return Task.CompletedTask;
                }

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

        /// <summary>
        /// 选中主题 → 单活跃实时预览窗格（D5 选中层）：复用 ThemePreviewControl，
        /// 内部按用户的 GPU 开关自动内嵌 GPU 实时预览或走 legacy 管线
        /// （F14：市场主题不能替用户打开 GPU/WDA 开关）。
        /// </summary>
        internal void SelectThemeForPreview(MarketThemeEntry entry)
        {
            _selectedThemeId = entry.Id;
            BuildPreviewPane();
        }

        private void BuildPreviewPane()
        {
            ClosePreviewPane();

            var entry = _index?.Themes.FirstOrDefault(t =>
                string.Equals(t.Id, _selectedThemeId, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;

            var host = new Panel
            {
                BackColor = UIColors.MainBg,
                Padding = new Padding(UIUtils.S(8)),
                Height = UIUtils.S(240),
                Dock = DockStyle.Top
            };
            var lblNotice = new Label
            {
                Text = LanguageManager.T("Menu.ThemeMarketPreviewFixed"),
                AutoSize = true,
                ForeColor = UIColors.TextSub,
                Font = UIFonts.Regular(9F),
                Dock = DockStyle.Top,
                Padding = new Padding(0, 2, 0, 4)
            };
            var preview = new ThemePreviewControl { Dock = DockStyle.Fill };
            host.Controls.Add(preview);
            host.Controls.Add(lblNotice);
            preview.BringToFront();

            _previewHost = host;
            _livePreview = preview;
            _container.Controls.Add(host);
            _container.Controls.SetChildIndex(host, 0);

            // 下载载荷（后台线程）→ 回 UI 线程 SetTheme
            _ = Task.Run(async () =>
            {
                try
                {
                    byte[] raw = await ThemeMarket.DownloadThemePayloadAsync(entry);
                    var theme = JsonSerializer.Deserialize<Theme>(Encoding.UTF8.GetString(raw), ThemeMarket.ThemeJsonOpts);
                    if (theme == null) return;
                    var target = _previewHost;
                    if (target == null || target.IsDisposed || !target.IsHandleCreated) return;
                    target.BeginInvoke(new Action(() =>
                    {
                        if (_livePreview == preview && !preview.IsDisposed)
                        {
                            preview.SetTheme(theme);
                        }
                    }));
                }
                catch { }
            });
        }

        private void ClosePreviewPane()
        {
            _livePreview = null;
            if (_previewHost != null)
            {
                var host = _previewHost;
                _previewHost = null;
                try
                {
                    _container.Controls.Remove(host);
                    host.Dispose();
                }
                catch { }
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
                var index = await _indexProvider.FetchAsync();
                if (index == null) throw new InvalidOperationException("插件市场索引为空");
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
                // ★ 以下四个分支此前缺失，导致 9 个语言包里**已经存在**的对应文案永远不可达
                //   （用户只会看到通用的"插件市场加载失败"）。本轮补齐，使文案与实际失败原因相符。
                case MarketErrorReason.HashMismatch: return LanguageManager.T("Menu.PluginMarketHashMismatch");
                case MarketErrorReason.IdMismatch: return LanguageManager.T("Menu.PluginMarketIdMismatch");
                case MarketErrorReason.BadSource: return LanguageManager.T("Menu.PluginMarketBadSource");
                case MarketErrorReason.SchemaMismatch: return LanguageManager.T("Menu.PluginMarketSchemaMismatch");
                // LM-008：索引条目没有提供可用的 SHA-256 摘要 ⇒ 无法校验内容完整性 ⇒ 拒绝安装。
                case MarketErrorReason.MissingHash: return LanguageManager.T("Menu.PluginMarketMissingHash");
                // 主题市场：主题内容未通过互验/Validate/无远程引用契约
                case MarketErrorReason.ThemeContentRejected: return LanguageManager.T("Menu.ThemeMarketThemeContentRejected");
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
