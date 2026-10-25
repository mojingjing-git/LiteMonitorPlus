using System;
using System.Collections.Generic;
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
    ///
    /// ★★★ F14a（#437 卡顿半项）—— 本页同时承载**插件**与**主题**两类资产：
    /// 进入页面不再"全量销毁 + 全量重建"，而是按**内容签名**做差量更新：
    /// <list type="bullet">
    /// <item>**固定控件**（源行 / 页签行 / 刷新按钮 / 提示行 / 状态标签）：只创建一次，
    ///       之后只就地改文字与配色；</item>
    /// <item>**列表数据**（每个资产一行）：按行缓存，安装/卸载**只更新受影响的那一行**，
    ///       索引内容变化也只重建内容变了的行；</item>
    /// <item>**进度/错误状态**：独立状态位，标签就地刷新，不牵动列表；</item>
    /// <item>**预览生命周期**：独立于列表 —— 离开页面释放唯一活跃的 GPU 预览，
    ///       返回时恢复；旧异步回调不得写入新页面代次。</item>
    /// </list>
    /// </summary>
    public class PluginMarketPage : SettingsPageBase
    {
        // ================================================================
        // 固定控件（只创建一次；之后只就地更新）
        // ================================================================
        private readonly Panel _container;        // 页面滚动容器
        private readonly Panel _listHost;         // 列表区（行数据）
        private readonly Panel _previewHost;      // 预览窗格宿主（预览生命周期）
        private readonly Panel _spacer;           // 列表与预览之间的间隔
        private readonly LiteActionRow _sourceRow;
        private readonly LiteActionRow _categoryRow;
        private readonly Label _statusLabel;      // 进度/错误状态
        private readonly Label _warnLabel;        // 启动自检提示
        private readonly Label _listStateLabel;   // 列表区状态（加载中 / 空）
        private readonly Label _previewNotice;    // 预览诚实性说明
        private readonly LiteButton _btnRefresh;
        private readonly LiteButton _btnPlugins;
        private readonly LiteButton _btnThemes;
        /// <summary>列表提示行（文案变了要重建：内部标签不可达）。</summary>
        private LiteHintRow? _hintRow;
        private string _hintText = "";

        // ================================================================
        // 列表数据（按资产缓存行）
        // ================================================================
        private readonly Dictionary<string, MarketRow> _rows =
            new Dictionary<string, MarketRow>(StringComparer.OrdinalIgnoreCase);

        // ================================================================
        // 进度/错误状态
        // ================================================================
        private string _statusText = "";
        private bool _statusIsError;

        // ================================================================
        // 预览生命周期
        // ================================================================
        private ThemePreviewControl? _livePreview;
        private string? _previewThemeId;
        private string? _selectedThemeId;

        // ================================================================
        // 签名 / 代次
        // ================================================================
        private MarketContentSignature _appliedSignature;
        private bool _hasAppliedSignature;
        private int _loadGeneration;      // 索引请求代次（过期回调不得写入）
        private int _contentGeneration;   // 已呈现内容的代次（签名维度）
        private bool _disposed;

        /// <summary>创建页面的线程（= UI 线程）：后台线程不得直接碰控件。</summary>
        private readonly int _uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

        // ================================================================
        // 数据源
        // ================================================================
        private readonly IMarketIndexProvider _indexProvider;
        private MarketIndex? _index;
        private string _pluginIndexDigest = "";
        private string _themeIndexDigest = "";
        private bool _busy;
        private MarketCategory _category = MarketCategory.Plugins;

        /// <summary>
        /// 测试接缝：安装状态解析器（id, 是否主题）→ 安装状态。
        /// <para>默认走产品实现（查市场安装记录 + 目标文件是否存在）；测试用它避免动
        /// settings.json 全局单例。<b>被测的是界面刷新逻辑，不是状态解析逻辑。</b></para>
        /// </summary>
        internal Func<string, bool, MarketInstallState>? InstallStateResolverOverride { get; set; }

        /// <summary>本次页面生命周期内**创建**的控件数（F14a 验收：缓存命中时必须为 0）。</summary>
        internal int ControlsCreated => _controlsCreated;

        /// <summary>本次页面生命周期内**销毁**的控件数（F14a 验收：缓存命中时必须为 0）。</summary>
        internal int ControlsDisposed => _controlsDisposed;

        private int _controlsCreated;
        private int _controlsDisposed;

        /// <summary>当前是否持有活跃的实时预览窗格（F14a 验收：离开页面后必须为 false）。</summary>
        public bool HasActiveLivePreview => _livePreview != null && !_livePreview.IsDisposed;

        /// <summary>当前实时预览控件实例（验收用；离开页面后应为已释放实例）。</summary>
        internal ThemePreviewControl? LivePreviewControl => _livePreview;

        /// <summary>因代次过期而被丢弃的异步回调次数（F14a 验收：旧回调不得写入新代次）。</summary>
        internal int StaleCallbacksDropped { get; private set; }

        /// <summary>当前页面代次（每次 OnShow 自增）。</summary>
        internal int CurrentPageGeneration => _pageGeneration;

        /// <summary>当前已呈现内容的代次（签名维度之一）。</summary>
        internal int ContentGeneration => _contentGeneration;

        /// <summary>当前索引请求代次。</summary>
        internal int LoadGeneration => _loadGeneration;

        /// <summary>当前内容签名（验收/诊断用）。</summary>
        internal MarketContentSignature CurrentSignature => ComputeSignature();

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

            // ---------- 预览窗格宿主（预览生命周期与列表数据解耦）----------
            _previewHost = new Panel
            {
                Dock = DockStyle.Top,
                BackColor = UIColors.MainBg,
                Padding = new Padding(UIUtils.S(8)),
                Height = UIUtils.S(240),
                Visible = false
            };
            _previewNotice = new Label
            {
                Text = LanguageManager.T("Menu.ThemeMarketPreviewFixed"),
                AutoSize = true,
                ForeColor = UIColors.TextSub,
                Font = UIFonts.Regular(9F),
                Dock = DockStyle.Top,
                Padding = new Padding(0, 2, 0, 4)
            };
            _previewHost.Controls.Add(_previewNotice);

            // ---------- 列表与预览之间的间隔 ----------
            _spacer = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.Transparent };

            // ---------- 列表区（行数据）----------
            _listHost = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            _listStateLabel = new Label
            {
                Text = LanguageManager.T("Menu.PluginMarketLoading"),
                AutoSize = true,
                ForeColor = UIColors.TextSub,
                Padding = new Padding(0, 10, 0, 10),
                Dock = DockStyle.Top
            };
            _listHost.Controls.Add(_listStateLabel);

            // ---------- 插件源（只读，锁死）+ 刷新 ----------
            _btnRefresh = new LiteButton(LanguageManager.T("Menu.PluginMarketRefresh"), true);
            _btnRefresh.Size = new Size(UIUtils.S(90), UIUtils.S(26));
            _btnRefresh.Margin = new Padding(UIUtils.S(8), 0, 0, 0);
            _btnRefresh.Click += async (s, e) => await LoadIndexAsync(showLoading: false);

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
            rightBox.Controls.Add(_btnRefresh);

            _sourceRow = new LiteActionRow(LanguageManager.T("Menu.PluginMarketSource"), rightBox);
            _sourceRow.Dock = DockStyle.Top;

            // ---------- 分类切换：插件 / 主题（M4，同一份索引的两个附加段） ----------
            _btnPlugins = new LiteButton(LanguageManager.T("Menu.PluginMarketTab"), true);
            _btnPlugins.Size = new Size(UIUtils.S(76), UIUtils.S(24));
            _btnPlugins.Margin = new Padding(UIUtils.S(8), 0, 0, 0);
            _btnPlugins.Click += (s, e) => SwitchCategory(MarketCategory.Plugins);

            _btnThemes = new LiteButton(LanguageManager.T("Menu.ThemeMarketTab"), false);
            _btnThemes.Size = new Size(UIUtils.S(76), UIUtils.S(24));
            _btnThemes.Margin = new Padding(UIUtils.S(8), 0, 0, 0);
            _btnThemes.Click += (s, e) => SwitchCategory(MarketCategory.Themes);

            var catBox = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            catBox.Controls.Add(_btnPlugins);
            catBox.Controls.Add(_btnThemes);

            _categoryRow = new LiteActionRow(LanguageManager.T("Menu.PluginMarketCategory"), catBox);
            _categoryRow.Dock = DockStyle.Top;

            // ---------- 状态（进度/错误）----------
            _statusLabel = new Label
            {
                Text = "",
                AutoSize = true,
                ForeColor = UIColors.TextSub,
                Font = UIFonts.Regular(9F),
                Padding = new Padding(0, 4, 0, 4),
                Dock = DockStyle.Top,
                Visible = false
            };

            // ---------- 启动自检提示 ----------
            _warnLabel = new Label
            {
                Text = "",
                AutoSize = true,
                ForeColor = UIColors.TextWarn,
                Font = UIFonts.Regular(9F),
                Padding = new Padding(0, 4, 0, 4),
                Dock = DockStyle.Top,
                Visible = false
            };

            // ---------- 列表提示行（文案随页签/语言变化）----------
            _hintRow = new LiteHintRow(LanguageManager.T("Menu.PluginMarketHint"));
            _hintRow.Dock = DockStyle.Top;

            // ★ 停靠顺序：WinForms 的 Dock 按 z 序**逆序**处理（索引越大越先停靠 ⇒ 越靠上）。
            //   这里按"视觉顺序的逆序"加入，得到的视觉顺序与改造前逐项一致：
            //   提示行 → 自检 → 状态 → 页签行 → 源行 → 列表 → 间隔 → 预览窗格。
            _container.Controls.Add(_previewHost);
            _container.Controls.Add(_spacer);
            _container.Controls.Add(_listHost);
            _container.Controls.Add(_sourceRow);
            _container.Controls.Add(_categoryRow);
            _container.Controls.Add(_statusLabel);
            _container.Controls.Add(_warnLabel);
            _container.Controls.Add(_hintRow);

            _controlsCreated += 1;   // _container
            _controlsCreated += 1;   // _previewHost
            _controlsCreated += 1;   // _spacer
            _controlsCreated += 1;   // _listHost
            _controlsCreated += 1;   // _listStateLabel
            _controlsCreated += 1;   // _previewNotice
            _controlsCreated += 1;   // _sourceRow
            _controlsCreated += 1;   // _categoryRow
            _controlsCreated += 1;   // _statusLabel
            _controlsCreated += 1;   // _warnLabel
            _controlsCreated += 1;   // _hintRow
            _controlsCreated += 1;   // _btnRefresh
            _controlsCreated += 1;   // _repoLink
            _controlsCreated += 1;   // rightBox
            _controlsCreated += 1;   // _btnPlugins
            _controlsCreated += 1;   // _btnThemes
            _controlsCreated += 1;   // catBox
        }

        // ================================================================
        // 生命周期
        // ================================================================

        public override void OnShow()
        {
            base.OnShow();

            // ★ 页面代次自增：旧代次的异步回调（预览载荷、索引结果）一律不得写入。
            _pageGeneration++;

            SyncUi();

            // 每次进入页面都后台刷新一次（更新检测要拿到索引里的最新 sha256）。
            // ★ 刷新**不**清空当前画面：有旧数据就继续显示，等新数据到了再差量更新。
            if (!_busy)
            {
                _ = LoadIndexAsync(showLoading: _index == null);
            }
        }

        /// <summary>页面代次：每次 OnShow 自增；异步回调据此判断自己是否已经过期。</summary>
        private int _pageGeneration;

        /// <summary>
        /// 离开页面（从设置窗体的内容区被摘掉）⇒ 释放唯一活跃的 GPU 预览。
        /// <para>★ 缓存**不能**把隐藏预览永久留在后台跑。</para>
        /// </summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null)
            {
                SuspendPreview();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                ReleaseLivePreview();
                foreach (MarketRow row in _rows.Values.ToList())
                {
                    DisposeRow(row);
                }
                _rows.Clear();
            }
            base.Dispose(disposing);
        }

        /// <summary>离开页面：释放唯一活跃的实时预览（连带其 GPU 呈现表面与计时器）。</summary>
        private void SuspendPreview()
        {
            ReleaseLivePreview();
            if (!_disposed && _previewHost != null && !_previewHost.IsDisposed)
            {
                _previewHost.Visible = false;
            }
        }

        // ================================================================
        // 同步（签名驱动的差量更新）
        // ================================================================

        /// <summary>
        /// 把界面同步到当前内容。
        /// <para>★ 签名相同 ⇒ **一个控件都不创建、不销毁**（缓存命中，#437 卡顿半项的正解）。</para>
        /// </summary>
        private void SyncUi()
        {
            if (_disposed) return;
            if (_container == null || _container.IsDisposed) return;

            UpdateStatusUi();
            UpdateWarnUi();
            SyncPreviewPane();

            MarketContentSignature signature = ComputeSignature();
            if (_hasAppliedSignature && signature.Equals(_appliedSignature))
            {
                return;     // ★ 缓存命中：控件零创建、零销毁
            }

            SyncChrome();
            SyncListState();
            SyncRows();
            SyncHintRow();

            _appliedSignature = signature;
            _hasAppliedSignature = true;

            _container.AutoScrollMinSize = new Size(0, 0);
            _container.PerformLayout();
        }

        /// <summary>内容签名：八个维度（资产类型/索引摘要/安装状态/选中项/语言/DPI/配色/请求代次）。</summary>
        private MarketContentSignature ComputeSignature()
        {
            string indexDigest = _category == MarketCategory.Themes ? _themeIndexDigest : _pluginIndexDigest;
            return new MarketContentSignature(
                _category,
                indexDigest,
                ComputeInstallStateDigest(),
                _selectedThemeId ?? string.Empty,
                LanguageManager.CurrentLang ?? string.Empty,
                MarketDigest.DpiKey(this),
                MarketDigest.Palette(),
                _contentGeneration);
        }

        /// <summary>安装状态摘要：当前页签全部资产的安装状态。</summary>
        private string ComputeInstallStateDigest()
        {
            var sb = new StringBuilder();

            // ★ "没有索引"与"索引里一条都没有"是两种不同状态，摘要必须能区分
            //   （否则一次失败的刷新会被当成"内容没变"，列表就不更新了）。
            if (_index == null) return MarketDigest.Of("no-index");

            sb.Append('|');
            if (_category == MarketCategory.Themes)
            {
                foreach (MarketThemeEntry entry in _index.Themes)
                {
                    sb.Append(MarketDigest.Join(entry.Id, ResolveThemeState(entry).ToString()));
                }
            }
            else
            {
                foreach (MarketPluginEntry entry in _index.Plugins)
                {
                    sb.Append(MarketDigest.Join(entry.Id, ResolvePluginState(entry).ToString()));
                }
            }
            return MarketDigest.Of(sb.ToString());
        }

        /// <summary>固定控件就地刷新：语言 / 配色 / DPI 变了要改文字与配色，但不重建控件。</summary>
        private void SyncChrome()
        {
            _sourceRow.Label.Text = LanguageManager.T("Menu.PluginMarketSource");
            _sourceRow.Label.ForeColor = UIColors.TextSub;
            _categoryRow.Label.Text = LanguageManager.T("Menu.PluginMarketCategory");
            _categoryRow.Label.ForeColor = UIColors.TextSub;

            _btnRefresh.Text = LanguageManager.T("Menu.PluginMarketRefresh");
            _btnPlugins.Text = LanguageManager.T("Menu.PluginMarketTab");
            _btnThemes.Text = LanguageManager.T("Menu.ThemeMarketTab");
            ApplyTabStyle(_btnPlugins, _category == MarketCategory.Plugins);
            ApplyTabStyle(_btnThemes, _category == MarketCategory.Themes);

            _previewNotice.Text = LanguageManager.T("Menu.ThemeMarketPreviewFixed");
            _previewNotice.ForeColor = UIColors.TextSub;
            _previewHost.BackColor = UIColors.MainBg;
            _container.BackColor = UIColors.MainBg;
            this.BackColor = UIColors.MainBg;

            SyncChromeMetrics();
        }

        /// <summary>
        /// 固定控件的**尺寸/边距**按当前缩放因子重算。
        /// <para>★ DPI 变了必须重算：控件不重建，但排版尺寸要跟上。</para>
        /// </summary>
        private void SyncChromeMetrics()
        {
            _btnRefresh.Size = new Size(UIUtils.S(90), UIUtils.S(26));
            _btnRefresh.Margin = new Padding(UIUtils.S(8), 0, 0, 0);
            _btnPlugins.Size = new Size(UIUtils.S(76), UIUtils.S(24));
            _btnPlugins.Margin = new Padding(UIUtils.S(8), 0, 0, 0);
            _btnThemes.Size = new Size(UIUtils.S(76), UIUtils.S(24));
            _btnThemes.Margin = new Padding(UIUtils.S(8), 0, 0, 0);

            _sourceRow.Height = UIUtils.S(40);
            _categoryRow.Height = UIUtils.S(40);

            _statusLabel.Padding = new Padding(0, 4, 0, 4);
            _warnLabel.Padding = new Padding(0, 4, 0, 4);
            _listStateLabel.Padding = new Padding(0, 10, 0, 10);

            _previewHost.Height = UIUtils.S(240);
            _previewHost.Padding = new Padding(UIUtils.S(8));
        }

        private static void ApplyTabStyle(LiteButton button, bool active)
        {
            if (active)
            {
                button.BackColor = UIColors.Primary;
                button.ForeColor = Color.White;
            }
            else
            {
                button.BackColor = Color.White;
                button.ForeColor = UIColors.TextMain;
            }
            button.FlatAppearance.BorderColor = UIColors.Border;
        }

        /// <summary>列表区状态：加载中 / 空 / 有数据。三种状态互不冒充。</summary>
        private void SyncListState()
        {
            string stateText;
            bool hasItems;

            if (_index == null)
            {
                stateText = LanguageManager.T("Menu.PluginMarketLoading");
                hasItems = false;
            }
            else if (_category == MarketCategory.Themes)
            {
                hasItems = _index.Themes.Count > 0;
                stateText = hasItems ? string.Empty : LanguageManager.T("Menu.ThemeMarketEmpty");
            }
            else
            {
                hasItems = _index.Plugins.Count > 0;
                stateText = hasItems ? string.Empty : LanguageManager.T("Menu.PluginMarketEmpty");
            }

            _listStateLabel.Text = stateText;
            _listStateLabel.Visible = !hasItems;
            _listHost.Visible = true;
        }

        /// <summary>列表提示行：插件页签与主题页签文案不同；文案变了才重建这一行。</summary>
        private void SyncHintRow()
        {
            string text = _category == MarketCategory.Themes
                ? LanguageManager.T("Menu.ThemeMarketPreviewFixed")
                : LanguageManager.T("Menu.PluginMarketHint");

            bool hasItems = _index != null &&
                            (_category == MarketCategory.Themes ? _index.Themes.Count > 0 : _index.Plugins.Count > 0);

            if (_hintRow != null && string.Equals(_hintText, text, StringComparison.Ordinal))
            {
                _hintRow.Visible = hasItems;
                return;
            }

            if (_hintRow != null)
            {
                _container.Controls.Remove(_hintRow);
                _hintRow.Dispose();
                _controlsDisposed += 1;
            }

            _hintRow = new LiteHintRow(text) { Dock = DockStyle.Top };
            _hintText = text;
            _controlsCreated += 1;
            _container.Controls.Add(_hintRow);
            _container.Controls.SetChildIndex(_hintRow, _container.Controls.Count - 1);
            _hintRow.Visible = hasItems;
        }

        /// <summary>进度/错误状态：独立状态位，就地刷新标签（不牵动列表）。</summary>
        private void UpdateStatusUi()
        {
            bool has = !string.IsNullOrEmpty(_statusText);
            _statusLabel.Visible = has;
            if (!has) return;

            Color color = _statusIsError ? UIColors.TextCrit : UIColors.TextSub;
            if (!string.Equals(_statusLabel.Text, _statusText, StringComparison.Ordinal) ||
                _statusLabel.ForeColor.ToArgb() != color.ToArgb())
            {
                _statusLabel.Text = _statusText;
                _statusLabel.ForeColor = color;
            }
        }

        /// <summary>启动自检提示：独立状态位，就地刷新标签。</summary>
        private void UpdateWarnUi()
        {
            if (PluginSelfCheck.SuspiciousFiles.Count == 0)
            {
                _warnLabel.Visible = false;
                return;
            }

            string names = string.Join("、", PluginSelfCheck.SuspiciousFiles.Select(Path.GetFileName));
            string text = string.Format(LanguageManager.T("Menu.PluginMarketSelfCheck"), names);
            if (!string.Equals(_warnLabel.Text, text, StringComparison.Ordinal))
            {
                _warnLabel.Text = text;
            }
            _warnLabel.Visible = true;
        }

        /// <summary>
        /// 行级差量同步：
        /// · 索引里没了的资产 ⇒ 销毁对应行；
        /// · 内容变了的资产 ⇒ 只重建这一行；
        /// · 安装状态变了的资产 ⇒ **就地**改按钮与徽标（控件零创建、零销毁）。
        /// </summary>
        private void SyncRows()
        {
            var wanted = new List<RowSpec>();

            if (_index != null)
            {
                if (_category == MarketCategory.Themes)
                {
                    foreach (MarketThemeEntry entry in _index.Themes)
                    {
                        wanted.Add(new RowSpec(entry.Id, true, BuildThemeRowKey(entry)));
                    }
                }
                else
                {
                    foreach (MarketPluginEntry entry in _index.Plugins)
                    {
                        wanted.Add(new RowSpec(entry.Id, false, BuildPluginRowKey(entry)));
                    }
                }
            }

            var wantedIds = new HashSet<string>(wanted.Select(w => w.Id), StringComparer.OrdinalIgnoreCase);
            foreach (string id in _rows.Keys.ToList())
            {
                if (!wantedIds.Contains(id))
                {
                    DisposeRow(_rows[id]);
                    _rows.Remove(id);
                }
            }

            foreach (RowSpec spec in wanted)
            {
                MarketInstallState state = spec.IsTheme
                    ? ResolveThemeState(FindThemeEntry(spec.Id))
                    : ResolvePluginState(FindPluginEntry(spec.Id));

                if (!_rows.TryGetValue(spec.Id, out MarketRow? row) || row.ContentKey != spec.ContentKey)
                {
                    if (row != null)
                    {
                        DisposeRow(row);
                        _rows.Remove(spec.Id);
                    }
                    row = CreateRow(spec.Id, spec.IsTheme);
                    row.ContentKey = spec.ContentKey;
                    _rows[spec.Id] = row;
                    _listHost.Controls.Add(row.Spacer);
                    _listHost.Controls.Add(row.Group);
                    _controlsCreated += 2;
                }

                if (row.State != state)
                {
                    row.State = state;
                    ApplyRowState(row, state);
                }
            }

            ReorderRows(wanted);
        }

        /// <summary>按索引顺序重排（视觉顺序 = 集合顺序的逆序，WinForms Dock 语义）。</summary>
        private void ReorderRows(List<RowSpec> wanted)
        {
            foreach (RowSpec spec in wanted)
            {
                if (!_rows.TryGetValue(spec.Id, out MarketRow? row)) continue;
                _listHost.Controls.SetChildIndex(row.Group, 0);
                _listHost.Controls.SetChildIndex(row.Spacer, 1);
            }
        }

        private MarketPluginEntry? FindPluginEntry(string id)
        {
            if (_index == null) return null;
            return _index.Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private MarketThemeEntry? FindThemeEntry(string id)
        {
            if (_index == null) return null;
            return _index.Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>行的内容键：资产内容 + 语言/DPI/配色。安装状态**不在**里面（否则会整行重建）。</summary>
        private string BuildPluginRowKey(MarketPluginEntry entry)
        {
            return MarketDigest.Join(
                "plugin", entry.Id, entry.Version, entry.Sha256, entry.Name, entry.NameEn,
                entry.Description, entry.DescriptionEn, entry.Author,
                entry.Status, entry.Verified ? "1" : "0",
                entry.Renamed ? "1" : "0", entry.OriginalId, entry.Note,
                entry.PageUrl, entry.Source,
                string.Join(",", entry.Requires ?? new List<string>()),
                LanguageManager.CurrentLang,
                MarketDigest.DpiKey(this).ToString(System.Globalization.CultureInfo.InvariantCulture),
                MarketDigest.Palette());
        }

        private string BuildThemeRowKey(MarketThemeEntry entry)
        {
            return MarketDigest.Join(
                "theme", entry.Id, entry.Version, entry.Sha256, entry.Name, entry.NameEn,
                entry.Description, entry.DescriptionEn, entry.Author,
                entry.Verified ? "1" : "0", entry.Route, entry.Material, entry.Status,
                entry.PageUrl,
                LanguageManager.CurrentLang,
                MarketDigest.DpiKey(this).ToString(System.Globalization.CultureInfo.InvariantCulture),
                MarketDigest.Palette());
        }

        // ================================================================
        // 行（每个资产一行）
        // ================================================================

        private sealed class RowSpec
        {
            public RowSpec(string id, bool isTheme, string contentKey)
            {
                Id = id;
                IsTheme = isTheme;
                ContentKey = contentKey;
            }

            public string Id { get; }
            public bool IsTheme { get; }
            public string ContentKey { get; }
        }

        /// <summary>一行资产：卡片 + 间隔 + 头部动作按钮 + 状态徽标。</summary>
        private sealed class MarketRow
        {
            public Panel Spacer = null!;
            public LiteSettingsGroup Group = null!;
            public LiteHeaderBtn ActionButton = null!;
            public Label Badge = null!;
            public LiteHeaderBtn? PreviewButton;
            public PictureBox? Snapshot;
            public MarketInstallState State = MarketInstallState.NotInstalled;
            public string ContentKey = string.Empty;
        }

        private MarketRow CreateRow(string id, bool isTheme)
        {
            var row = new MarketRow();
            row.Spacer = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent };

            if (isTheme)
            {
                MarketThemeEntry? entry = FindThemeEntry(id);
                if (entry == null) return row;
                BuildThemeGroup(row, entry);
            }
            else
            {
                MarketPluginEntry? entry = FindPluginEntry(id);
                if (entry == null) return row;
                BuildPluginGroup(row, entry);
            }
            return row;
        }

        private void DisposeRow(MarketRow row)
        {
            if (row.Spacer != null && !row.Spacer.IsDisposed)
            {
                _listHost.Controls.Remove(row.Spacer);
                row.Spacer.Dispose();
                _controlsDisposed += 1;
            }
            if (row.Group != null && !row.Group.IsDisposed)
            {
                _listHost.Controls.Remove(row.Group);
                row.Group.Dispose();
                _controlsDisposed += 1;
            }
        }

        /// <summary>插件卡片（原 AddPluginGroup，改为往已有行里填控件）。</summary>
        private void BuildPluginGroup(MarketRow row, MarketPluginEntry entry)
        {
            MarketInstallState state = ResolvePluginState(entry);
            row.State = state;

            string displayName = PluginI18n.Pick(entry.Name, entry.NameEn);
            var group = new LiteSettingsGroup($"{displayName}   v{entry.Version}   (ID: {entry.Id})");
            row.Group = group;

            var btn = new LiteHeaderBtn(ActionText(state));
            btn.SetColor(ActionColor(state));
            btn.Click += async (s, e) =>
            {
                if (state == MarketInstallState.Installed) await UninstallAsync(entry);
                else await InstallAsync(entry);
            };
            group.AddHeaderAction(btn);
            row.ActionButton = btn;
            _controlsCreated += 1;

            string description = PluginI18n.Pick(entry.Description, entry.DescriptionEn);
            if (!string.IsNullOrEmpty(description))
            {
                group.AddHint(description);
            }

            // 状态徽标：状态说明 + 安装状态
            var badge = new Label
            {
                Text = PluginBadgeText(entry, state),
                AutoSize = true,
                ForeColor = entry.Status == "ok" ? UIColors.TextSub : UIColors.TextWarn,
                Font = UIFonts.Regular(9F),
                Padding = new Padding(0, 2, 0, 6)
            };
            group.AddFullItem(badge);
            row.Badge = badge;
            _controlsCreated += 1;

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
                _controlsCreated += 1;
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
                _controlsCreated += 1;
            }

            if (!string.IsNullOrEmpty(entry.Source))
            {
                var link = new LiteLink(LanguageManager.T("Menu.PluginMarketSourceLink"), () => OpenUrl(entry.Source));
                group.AddFullItem(link);
                _controlsCreated += 1;
            }
        }

        /// <summary>主题卡片（原 AddThemeGroup，改为往已有行里填控件）。</summary>
        private void BuildThemeGroup(MarketRow row, MarketThemeEntry entry)
        {
            MarketInstallState state = ResolveThemeState(entry);
            row.State = state;

            string displayName = PluginI18n.Pick(entry.Name, entry.NameEn);
            bool isGpu = string.Equals(entry.Route, ThemeRenderingConfig.GpuBackend, StringComparison.OrdinalIgnoreCase);
            string routeBadge = isGpu ? "GPU" : "Legacy";
            var group = new LiteSettingsGroup($"{displayName}   v{entry.Version}   [{routeBadge}]   (ID: {entry.Id})");
            row.Group = group;

            // 头部动作：预览 + 安装/卸载
            var btnPreview = new LiteHeaderBtn(LanguageManager.T("Menu.ThemeMarketPreviewAction"));
            btnPreview.Click += (s, e) => SelectThemeForPreview(entry);
            group.AddHeaderAction(btnPreview);
            row.PreviewButton = btnPreview;
            _controlsCreated += 1;

            var btn = new LiteHeaderBtn(ActionText(state));
            btn.SetColor(ActionColor(state));
            btn.Click += async (s, e) =>
            {
                if (state == MarketInstallState.Installed) UninstallThemeAsync(entry);
                else await InstallThemeAsync(entry);
            };
            group.AddHeaderAction(btn);
            row.ActionButton = btn;
            _controlsCreated += 1;

            string description = PluginI18n.Pick(entry.Description, entry.DescriptionEn);
            if (!string.IsNullOrEmpty(description))
            {
                group.AddHint(description);
            }

            // 状态徽标：路线 · 材质 · 安装状态
            var badge = new Label
            {
                Text = ThemeBadgeText(entry, state),
                AutoSize = true,
                ForeColor = UIColors.TextSub,
                Font = UIFonts.Regular(9F),
                Padding = new Padding(0, 2, 0, 6)
            };
            group.AddFullItem(badge);
            row.Badge = badge;
            _controlsCreated += 1;

            // ★ 未经验证徽标（与插件一致）
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
                _controlsCreated += 1;
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
            row.Snapshot = snap;
            _controlsCreated += 1;

            RequestSnapshot(row, entry, snap);

            if (!string.IsNullOrEmpty(entry.PageUrl))
            {
                var pageLink = new LiteLink(LanguageManager.T("Menu.PluginMarketPageLink"), () => OpenUrl(entry.PageUrl));
                group.AddFullItem(pageLink);
                _controlsCreated += 1;
            }
        }

        /// <summary>
        /// 请求卡片快照。
        /// <para>★ 回调守卫：**这一行仍是当前行**才允许写入 —— 行被重建/销毁后，
        /// 旧回调不得把上一代的快照画到新一代的行上。</para>
        /// </summary>
        private void RequestSnapshot(MarketRow row, MarketThemeEntry entry, PictureBox snap)
        {
            ThemePreviewService.RequestSnapshot(entry, pngPath =>
            {
                if (!_rows.TryGetValue(entry.Id, out MarketRow? current) || current.Snapshot != snap)
                {
                    StaleCallbacksDropped++;
                    return;
                }
                if (snap.IsDisposed) return;
                if (pngPath != null)
                {
                    try { snap.Image = Image.FromFile(pngPath); } catch { }
                }
            });
        }

        /// <summary>安装状态变了 ⇒ **就地**改这一行的动作按钮与徽标（控件零创建、零销毁）。</summary>
        private void ApplyRowState(MarketRow row, MarketInstallState state)
        {
            if (row.ActionButton != null && !row.ActionButton.IsDisposed)
            {
                row.ActionButton.Text = ActionText(state);
                row.ActionButton.SetColor(ActionColor(state));
            }

            if (row.Badge != null && !row.Badge.IsDisposed)
            {
                if (_category == MarketCategory.Themes)
                {
                    MarketThemeEntry? entry = FindThemeEntry(FindRowId(row));
                    if (entry != null) row.Badge.Text = ThemeBadgeText(entry, state);
                }
                else
                {
                    MarketPluginEntry? entry = FindPluginEntry(FindRowId(row));
                    if (entry != null) row.Badge.Text = PluginBadgeText(entry, state);
                }
            }
        }

        private string FindRowId(MarketRow row)
        {
            foreach (KeyValuePair<string, MarketRow> kv in _rows)
            {
                if (ReferenceEquals(kv.Value, row)) return kv.Key;
            }
            return string.Empty;
        }

        private static string PluginBadgeText(MarketPluginEntry entry, MarketInstallState state)
        {
            return entry.StatusLabel + "　·　" + GetStateLabel(state);
        }

        private static string ThemeBadgeText(MarketThemeEntry entry, MarketInstallState state)
        {
            bool isGpu = string.Equals(entry.Route, ThemeRenderingConfig.GpuBackend, StringComparison.OrdinalIgnoreCase);
            string routeBadge = isGpu ? "GPU" : "Legacy";
            return routeBadge + " · " + entry.Material + "　·　" + GetStateLabel(state);
        }

        private static string ActionText(MarketInstallState state)
        {
            switch (state)
            {
                case MarketInstallState.Installed: return LanguageManager.T("Menu.PluginMarketUninstall");
                case MarketInstallState.Updatable: return LanguageManager.T("Menu.PluginMarketUpdate");
                case MarketInstallState.NeedReinstall: return LanguageManager.T("Menu.PluginMarketReinstallAction");
                default: return LanguageManager.T("Menu.PluginMarketInstall");
            }
        }

        private static Color ActionColor(MarketInstallState state)
        {
            switch (state)
            {
                case MarketInstallState.Installed: return Color.IndianRed;
                case MarketInstallState.Updatable: return UIColors.Primary;
                case MarketInstallState.NeedReinstall: return UIColors.Primary;
                default: return UIColors.Primary;
            }
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
        // 安装状态判定
        // ================================================================

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

        private MarketInstallState ResolvePluginState(MarketPluginEntry? entry)
        {
            if (InstallStateResolverOverride != null)
            {
                return InstallStateResolverOverride(entry?.Id ?? string.Empty, false);
            }
            if (entry == null) return MarketInstallState.NotInstalled;
            return GetState(entry, out _);
        }

        private MarketInstallState ResolveThemeState(MarketThemeEntry? entry)
        {
            if (InstallStateResolverOverride != null)
            {
                return InstallStateResolverOverride(entry?.Id ?? string.Empty, true);
            }
            if (entry == null) return MarketInstallState.NotInstalled;
            return GetThemeState(entry, out _);
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

            // 预览生命周期独立：换页签 ⇒ 释放旧预览。
            ReleaseLivePreview();

            SyncUi();

            // 首次切到主题分类时索引可能还没拉过
            if (_index == null && !_busy)
            {
                _ = LoadIndexAsync(showLoading: true);
            }
        }

        /// <summary>
        /// 选中主题 → 单活跃实时预览窗格（D5 选中层）：复用 ThemePreviewControl，
        /// 内部按用户的 GPU 开关自动内嵌 GPU 实时预览或走 legacy 管线
        /// （F14：市场主题不能替用户打开 GPU/WDA 开关）。
        /// </summary>
        /// <summary>测试接缝：选中主题进入实时预览（等价于点击预览按钮）。</summary>
        internal void SelectThemeForPreview(MarketThemeEntry entry)
        {
            _selectedThemeId = entry.Id;
            SyncUi();
        }

        /// <summary>预览窗格与当前选中项对齐（幂等）。</summary>
        private void SyncPreviewPane()
        {
            bool wanted = _category == MarketCategory.Themes && _selectedThemeId != null;
            if (!wanted)
            {
                ReleaseLivePreview();
                return;
            }

            if (_livePreview != null && !_livePreview.IsDisposed &&
                string.Equals(_previewThemeId, _selectedThemeId, StringComparison.OrdinalIgnoreCase))
            {
                return;     // 已经是当前选中项的预览
            }

            BuildPreviewPane();
        }

        private void BuildPreviewPane()
        {
            ReleaseLivePreview();

            MarketThemeEntry? entry = _index?.Themes.FirstOrDefault(t =>
                string.Equals(t.Id, _selectedThemeId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                _previewHost.Visible = false;
                return;
            }

            var preview = new ThemePreviewControl { Dock = DockStyle.Fill };
            _controlsCreated += 1;

            _previewHost.Controls.Add(preview);
            _previewHost.Controls.SetChildIndex(preview, 0);
            _previewHost.Visible = true;

            _livePreview = preview;
            _previewThemeId = entry.Id;

            int generation = _pageGeneration;

            // 下载载荷（后台线程）→ 回 UI 线程 SetTheme
            _ = Task.Run(async () =>
            {
                try
                {
                    byte[] raw = await ThemeMarket.DownloadThemePayloadAsync(entry);
                    var theme = JsonSerializer.Deserialize<Theme>(Encoding.UTF8.GetString(raw), ThemeMarket.ThemeJsonOpts);
                    if (theme == null) return;
                    RunOnUi(() => TryApplyPreviewPayload(generation, preview, theme));
                }
                catch { }
            });
        }

        /// <summary>
        /// 把下载好的主题载荷写进实时预览。
        /// <para>★ 代次守卫：旧页面代次 / 非当前预览 ⇒ 一律丢弃，绝不写入新页面代次。</para>
        /// </summary>
        internal bool TryApplyPreviewPayload(int generation, ThemePreviewControl preview, Theme theme)
        {
            if (generation != _pageGeneration)
            {
                StaleCallbacksDropped++;
                return false;
            }
            if (_livePreview != preview || preview.IsDisposed)
            {
                StaleCallbacksDropped++;
                return false;
            }
            preview.SetTheme(theme);
            return true;
        }

        /// <summary>释放唯一活跃的实时预览（连带其 GPU 呈现表面与计时器）。</summary>
        private void ReleaseLivePreview()
        {
            ThemePreviewControl? preview = _livePreview;
            _livePreview = null;
            _previewThemeId = null;
            if (preview == null) return;

            try
            {
                _previewHost.Controls.Remove(preview);
                preview.Dispose();
                _controlsDisposed += 1;
            }
            catch { }
        }

        /// <summary>把动作投递到 UI 线程（UI 线程只投递恢复请求，不在后台线程碰控件）。</summary>
        private void RunOnUi(Action action)
        {
            if (_disposed) return;
            try
            {
                if (IsHandleCreated)
                {
                    if (InvokeRequired)
                    {
                        BeginInvoke(action);
                        return;
                    }
                    action();
                    return;
                }

                // 句柄还没建起来 ⇒ 只有 UI 线程本身可以安全地碰控件；
                // 后台线程来的回调直接丢弃（页面再次显示时会重建预览）。
                if (Thread.CurrentThread.ManagedThreadId == _uiThreadId)
                {
                    action();
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        // ================================================================
        // 数据加载
        // ================================================================

        private async Task LoadIndexAsync(bool showLoading)
        {
            if (_busy) return;
            _busy = true;

            int generation = ++_loadGeneration;

            if (showLoading)
            {
                _statusText = LanguageManager.T("Menu.PluginMarketLoading");
                _statusIsError = false;
                SyncUi();
            }

            try
            {
                MarketIndex? index = await _indexProvider.FetchAsync();

                // ★ 过期回调不得写入新代次
                if (generation != _loadGeneration) return;

                if (index == null)
                {
                    _index = null;
                    _statusText = LanguageManager.T("Menu.PluginMarketFailed");
                    _statusIsError = true;
                }
                else
                {
                    ApplyIndex(index);
                    _statusText = LanguageManager.T("Menu.PluginMarketLoaded") + $"（{index.Plugins.Count}）";
                    _statusIsError = false;
                }
            }
            catch (Exception ex)
            {
                if (generation != _loadGeneration) return;
                _index = null;
                _statusText = LanguageManager.T("Menu.PluginMarketFailed") + " " + DescribeError(ex);
                _statusIsError = true;
            }
            finally
            {
                if (generation == _loadGeneration)
                {
                    _busy = false;
                    SyncUi();
                }
            }
        }

        /// <summary>应用一份索引快照：内容摘要变了才推进内容代次。</summary>
        private void ApplyIndex(MarketIndex index)
        {
            string pluginDigest = MarketDigest.Plugins(index.Plugins);
            string themeDigest = MarketDigest.Themes(index.Themes);

            bool changed = !string.Equals(pluginDigest, _pluginIndexDigest, StringComparison.Ordinal)
                           || !string.Equals(themeDigest, _themeIndexDigest, StringComparison.Ordinal);

            _pluginIndexDigest = pluginDigest;
            _themeIndexDigest = themeDigest;
            _index = index;

            if (changed) _contentGeneration++;
        }

        private async Task InstallThemeAsync(MarketThemeEntry entry)
        {
            if (_busy) return;
            _busy = true;
            _statusText = LanguageManager.T("Menu.PluginMarketInstalling") + PluginI18n.Pick(entry.Name, entry.NameEn) + " ...";
            _statusIsError = false;
            SyncUi();

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
                SyncUi();
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
                    SyncUi();
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
            SyncUi();
            return Task.CompletedTask;
        }

        private async Task InstallAsync(MarketPluginEntry entry)
        {
            if (_busy) return;

            _busy = true;
            _statusText = LanguageManager.T("Menu.PluginMarketInstalling") + PluginI18n.Pick(entry.Name, entry.NameEn) + " ...";
            _statusIsError = false;
            SyncUi();

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
                SyncUi();
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
                    SyncUi();
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
            SyncUi();
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
