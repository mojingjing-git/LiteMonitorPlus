using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    /// <summary>
    /// 「渲染路线」设置页：把 GPU（DirectX 11）路线与 GDI+ 旧路线的设置项**按路线归位**。
    /// <para>★ 路线总览卡只陈述事实：主题声明路线、实际生效路线、真实原因码
    /// （不做"已回退"这种无因表述，R3 §4.1-1.3）；GPU 路线卡自 MainPanelPage 迁入，
    /// 行为不变（含 WDA 首次政策弹窗与 GpuStartupRetryRequested 重试申请）。</para>
    /// <para>★ 卡片创建沿用 MainPanelPage 的 OnShow 惰性模式：构造期 <c>Config</c> 尚未注入
    /// （SetContext 在页面构造之后才调用），构造期建卡会拿到空配置。</para>
    /// </summary>
    public class RenderingRoutePage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded = false;

        /// <summary>GPU 开关被显式打开后请求一次启动重试（Apply 时由 SettingsForm 消费）。</summary>
        public bool GpuStartupRetryRequested { get; private set; }

        public void AcknowledgeGpuStartupRetryRequest() => GpuStartupRetryRequested = false;

        private Label? _themeRouteLabel;
        private Label? _statusSummaryLabel;
        private FullWidthDetailRow? _statusDetailRow;

        private bool Chinese => (Config.Language ?? string.Empty)
            .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        // ★ 原名是 Text(...)，它把基类 Control.Text **属性**藏了起来（CS0108 警告），
        //   而且名字和「控件标题」完全撞车，调用点里 `Text("启用", "Enable")` 很容易看错。
        //   改名为 GetText(...)，语义更清楚，也不再隐藏基类成员。
        //   ★ 纯重命名：方法体与全部调用点同步修改，运行期行为不变。
        private string GetText(string zh, string en) => Chinese ? zh : en;

        public RenderingRoutePage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) };
            this.Controls.Add(_container);
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null || _isLoaded) return;

            _container.SuspendLayout();

            CreateRouteOverviewCard();
            CreateGpuRouteCard();

            _container.ResumeLayout();
            _isLoaded = true;
        }

        // ================================================================
        // 卡 1：路线总览（只读）
        // ================================================================
        private void CreateRouteOverviewCard()
        {
            var group = new LiteSettingsGroup(GetText("路线总览", "Route overview"));

            // ★ 布局约束（初版错位的根因）：LiteSettingsItem 是**固定 40px 单行、控件右对齐**的
            //   两列交替布局（AddItem 按 _colTracker 左右轮流）；多行标签会竖直溢出压到相邻行，
            //   两个连续 AddItem 会并排成一行。因此本卡所有行一律 AddFullItem（整行独占），
            //   值标签单行 + AutoEllipsis，多行细节放独立的全行详情块。
            _themeRouteLabel = MakeValueLabel();
            _themeRouteLabel.Text = ThemeRouteText();
            group.AddFullItem(new LiteSettingsItem(
                GetText("主题声明路线", "Theme declared route"),
                _themeRouteLabel));

            _statusSummaryLabel = MakeValueLabel();
            _statusSummaryLabel.Text = ComposeStatusSummary();
            group.AddFullItem(new LiteSettingsItem(
                GetText("实际状态", "Actual state"),
                _statusSummaryLabel));

            _statusDetailRow = new FullWidthDetailRow(ComposeStatusDetail());
            group.AddFullItem(_statusDetailRow);

            group.AddHint(GetText(
                "方括号内是真实原因码（如 safe_mode / gpu_effects_opt_in_required），用于排障；不显示原因的\"已回退\"一律不出现。",
                "Bracketed codes are real reason codes (e.g. safe_mode / gpu_effects_opt_in_required) for diagnostics; regressions are never shown without a reason."));

            RegisterRefresh(() =>
            {
                if (_themeRouteLabel is not null) _themeRouteLabel.Text = ThemeRouteText();
                if (_statusSummaryLabel is not null) _statusSummaryLabel.Text = ComposeStatusSummary();
                if (_statusDetailRow is not null) _statusDetailRow.DetailText = ComposeStatusDetail();
            });

            AddGroupToPage(group);
        }

        // ================================================================
        // 卡 2：GPU 路线（自 MainPanelPage.CreateGpuMaterialCard 迁入，行为不变）
        // ================================================================
        private void CreateGpuRouteCard()
        {
            var group = new LiteSettingsGroup(GetText("GPU 路线（DirectX 11）", "GPU route (DirectX 11)"));

            group.AddToggle(
                this,
                GetText("GPU 材质渲染", "GPU material rendering"),
                () => Config.GpuThemeEffectsEnabled,
                value =>
                {
                    Config.GpuThemeEffectsEnabled = value;
                    if (value)
                    {
                        GpuStartupRetryRequested = true;
                    }
                });
            group.AddHint(GetText(
                "只对带有 rendering.backend=gpu 的新主题生效；旧主题继续使用原 GDI+ 路径。",
                "Only new themes with rendering.backend=gpu are affected; legacy themes keep the original GDI+ path."));

            var liveBackground = new LiteCheck(
                Config.GpuLiveBackgroundEnabled,
                GetText("启用", "Enable"));
            liveBackground.CheckedChanged += (_, _) =>
            {
                if (IsWriteBackSuppressed)
                {
                    return;
                }

                bool want = liveBackground.Checked;
                if (want && !Config.GpuWdaCapturePolicyAccepted)
                {
                    DialogResult accepted = MessageBox.Show(
                        FindForm(),
                        GetText(
                            "实时玻璃需要使用 Desktop Duplication，并通过 WDA_EXCLUDEFROMCAPTURE 排除整个顶层窗口。\n\n开启后，受支持的系统截图和录屏会看不到 LiteMonitor++。旧主题和固定输入模式不受影响。\n\n是否接受并开启实时背景？",
                            "Live glass uses Desktop Duplication and excludes the entire top-level window with WDA_EXCLUDEFROMCAPTURE.\n\nWhile active, supported screenshots and recordings will not show LiteMonitor++. Legacy themes and fixed-input mode are unaffected.\n\nAccept and enable live background?"),
                        GetText("实时玻璃捕获说明", "Live glass capture policy"),
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Warning);
                    if (accepted != DialogResult.OK)
                    {
                        using (BeginSuppressWriteBack())
                        {
                            liveBackground.Checked = false;
                        }
                        return;
                    }

                    Config.GpuWdaCapturePolicyAccepted = true;
                }

                Config.GpuLiveBackgroundEnabled = want;
                if (want)
                {
                    GpuStartupRetryRequested = true;
                }
            };
            group.AddItem(new LiteSettingsItem(
                GetText("实时桌面背景", "Live desktop background"),
                liveBackground));
            RegisterRefresh(() => liveBackground.Checked =
                Config.GpuLiveBackgroundEnabled);
            group.AddHint(GetText(
                "实时背景与旧 LiveGlassEnabled 完全独立；赛博朋克主题始终为零抓屏。",
                "Live background is independent from legacy LiveGlassEnabled; cyberpunk always performs zero desktop capture."));

            AddGroupToPage(group);
        }

        // ================================================================
        // 状态聚合（纯逻辑在 RenderingRouteStatusBuilder，便于单测）
        // ================================================================
        private static bool IsGpuTheme(ThemeRenderingConfig? rendering) =>
            rendering is not null &&
            string.Equals(rendering.Backend?.Trim(),
                ThemeRenderingConfig.GpuBackend,
                StringComparison.OrdinalIgnoreCase);

        private string ThemeRouteText()
        {
            ThemeRenderingConfig? rendering = ThemeManager.Current.Rendering;
            if (!IsGpuTheme(rendering))
            {
                return GetText("GDI+ 旧路线（主题未声明 GPU 材质）",
                    "GDI+ legacy (theme declares no GPU material)");
            }

            string material = rendering!.Material ?? string.Empty;
            return GetText($"GPU 材质（{material}）", $"GPU material ({material})");
        }

        private IReadOnlyList<string> ComposeStatusLines()
        {
            var snapshot = new RenderingRouteStatusSnapshot(
                ThemeUsesGpuMaterial: IsGpuTheme(ThemeManager.Current.Rendering),
                GpuEffectsEnabled: Config.GpuThemeEffectsEnabled,
                PresentationActive: UI?.IsGpuPresentationActive == true,
                LiveBackgroundActive: UI?.IsGpuLiveBackgroundActive == true,
                LiveBackgroundRequested: UI?.IsGpuLiveBackgroundRequested == true,
                RuntimeFailure: UI?.GpuRuntimeFailure,
                FallbackCode: UI?.GpuFallbackCode,
                WindowIsLayered: MainForm is { IsHandleCreated: true } && MainForm.IsWindowLayered());

            var lines = new List<string>();
            foreach (RenderingRouteStatusLine line in RenderingRouteStatusBuilder.Build(snapshot))
            {
                string key = line.Kind switch
                {
                    RenderingRouteStatusKind.ThemeDeclaresLegacy => "Menu.RouteStatus.LegacyTheme",
                    RenderingRouteStatusKind.GpuRunning => "Menu.RouteStatus.GpuRunning",
                    RenderingRouteStatusKind.LiveBackgroundOn => "Menu.RouteStatus.LiveBackgroundOn",
                    RenderingRouteStatusKind.LiveBackgroundOff => "Menu.RouteStatus.LiveBackgroundOff",
                    RenderingRouteStatusKind.LiveBackgroundUnavailable => "Menu.RouteStatus.LiveBackgroundUnavailable",
                    RenderingRouteStatusKind.NotStarted => "Menu.RouteStatus.NotStarted",
                    RenderingRouteStatusKind.LayeredDiagnostic => "Menu.RouteStatus.LayeredNote",
                    _ => "Menu.RouteStatus.NotStarted"
                };
                string text = LanguageManager.T(key);
                lines.Add(string.IsNullOrWhiteSpace(line.Detail) ? text : $"{text} [{line.Detail}]");
            }

            return lines;
        }

        /// <summary>单行摘要（表格行用）：取第一条状态行。</summary>
        private string ComposeStatusSummary() =>
            ComposeStatusLines().FirstOrDefault() ?? GetText("（无状态）", "(no status)");

        /// <summary>多行详情（全行块用）：全部状态行。</summary>
        private string ComposeStatusDetail()
        {
            var lines = ComposeStatusLines();
            return lines.Count == 0
                ? GetText("（无状态）", "(no status)")
                : string.Join(Environment.NewLine, lines);
        }

        // ================================================================
        // 公共小件
        // ================================================================

        /// <summary>表格行右侧的**单行**值标签：固定宽度 + AutoEllipsis，
        /// 配合 LiteSettingsItem 的"控件右对齐、行内垂直居中"布局（不可多行）。</summary>
        private Label MakeValueLabel()
        {
            return new Label
            {
                AutoSize = false,
                Width = UIUtils.S(440),
                Height = UIUtils.S(22),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                ForeColor = UIColors.TextSub,
                Font = UIFonts.Regular(9F),
                Text = string.Empty,
            };
        }

        /// <summary>全行多行详情块（文本可在 RegisterRefresh 里更新）。
        /// 形制与 LiteHintRow 相同，但 LiteHintRow 的文本是构造期固定的。</summary>
        private sealed class FullWidthDetailRow : Panel
        {
            private readonly Label _lbl;

            public string DetailText
            {
                get => _lbl.Text;
                set => _lbl.Text = value;
            }

            public FullWidthDetailRow(string text)
            {
                Dock = DockStyle.Top;
                Margin = new Padding(0);
                Padding = UIUtils.S(new Padding(0, 5, 5, 5));
                AutoSize = true;

                _lbl = new Label
                {
                    Text = text,
                    AutoSize = true,
                    MaximumSize = new Size(UIUtils.S(500), 0),
                    Font = UIFonts.Regular(8F),
                    ForeColor = UIColors.TextSub,
                    Dock = DockStyle.Fill,
                };
                Controls.Add(_lbl);
                Resize += (_, _) => _lbl.MaximumSize = new Size(Width - Padding.Horizontal, 0);
            }
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }
    }
}
