using System;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    /// <summary>
    /// 「渲染路线」页（只读状态与诊断）。
    /// <para>★ 职责边界：<b>开关在「主面板」页</b>（与主题选择同处一页，主题即渲染意图），
    /// 本页负责"当前到底走哪条路线、为什么"——主题声明路线、实际生效路线、真实原因码与
    /// GPU 选项快照。这样"看状态"与"改开关"各归其位，不需要用户在两页之间猜。</para>
    /// </summary>
    public class RenderingRoutePage : SettingsPageBase
    {
        private Panel _container;
        private bool _isLoaded;

        public RenderingRoutePage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) };
            this.Controls.Add(_container);
        }

        private bool Chinese => (Config.Language ?? string.Empty)
            .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        private string GetText(string zh, string en) => Chinese ? zh : en;

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null || _isLoaded) return;

            _container.SuspendLayout();
            ClearAndDispose(_container.Controls);

            CreateStatusCard();
            CreateGpuOptionsCard();
            CreatePresentationDiagnosticsCard();

            _container.ResumeLayout();
            _isLoaded = true;
        }

        /// <summary>状态总览卡：主题声明路线 / 实际生效路线 / 完整详情（含原因码）。</summary>
        private void CreateStatusCard()
        {
            var group = new LiteSettingsGroup(GetText("渲染路线", "Rendering route"));

            Label themeLabel = RenderingRouteStatusView.MakeValueLabel();
            group.AddFullItem(new LiteSettingsItem(
                GetText("主题声明路线", "Theme declared route"),
                themeLabel));

            Label statusLabel = RenderingRouteStatusView.MakeValueLabel();
            group.AddFullItem(new LiteSettingsItem(
                GetText("实际生效路线", "Effective route"),
                statusLabel));

            var detailRow = new FullWidthDetailRow(string.Empty);
            group.AddFullItem(detailRow);

            group.AddHint(GetText(
                "方括号内是真实原因码（如 safe_mode），用于排障；不显示原因的\"已回退\"一律不出现。",
                "Bracketed codes are real reason codes (e.g. safe_mode) for diagnostics; regressions are never shown without a reason."));

            RegisterRefresh(() =>
            {
                themeLabel.Text = RenderingRouteStatusView.ThemeRouteText(Chinese);
                statusLabel.Text = RenderingRouteStatusView.ComposeStatusSummary(Config, UI, MainForm, Chinese);
                detailRow.DetailText = RenderingRouteStatusView.ComposeStatusDetail(Config, UI, MainForm, Chinese);
            });

            AddGroupToPage(group);
        }

        /// <summary>GPU 选项快照卡：只读显示当前开关状态，并指向开关所在页。</summary>
        private void CreateGpuOptionsCard()
        {
            var group = new LiteSettingsGroup(GetText("GPU 渲染选项", "GPU rendering options"));

            Label effectsLabel = RenderingRouteStatusView.MakeValueLabel();
            group.AddFullItem(new LiteSettingsItem(
                GetText("GPU 材质渲染", "GPU material rendering"),
                effectsLabel));

            Label liveLabel = RenderingRouteStatusView.MakeValueLabel();
            group.AddFullItem(new LiteSettingsItem(
                GetText("实时桌面背景", "Live desktop background"),
                liveLabel));

            group.AddHint(GetText(
                "这两个开关在「主面板」页调整（主题选择下方）。",
                "These switches live on the Main panel page (below the theme picker)."));

            RegisterRefresh(() =>
            {
                effectsLabel.Text = Config.GpuThemeEffectsEnabled
                    ? GetText("已开启", "Enabled")
                    : GetText("已关闭", "Disabled");
                liveLabel.Text = Config.GpuLiveBackgroundEnabled
                    ? GetText("已开启", "Enabled")
                    : GetText("已关闭", "Disabled");
            });

            AddGroupToPage(group);
        }

        /// <summary>
        /// GPU 呈现诊断卡：把"画面出不来"的原因变成可核对的事实
        /// （渲染适配器 / 是否覆盖窗口所在屏 / 呈现帧数 / 捕获状态与原因码）。
        /// </summary>
        private void CreatePresentationDiagnosticsCard()
        {
            var group = new LiteSettingsGroup(GetText("GPU 呈现诊断", "GPU presentation diagnostics"));

            var detailRow = new FullWidthDetailRow(string.Empty);
            group.AddFullItem(detailRow);

            group.AddHint(GetText(
                "「呈现尝试 / 已呈现」为 0 表示 GPU 一帧都没画出来；适配器不覆盖窗口所在显示器时，"
                + "混合显卡机器上需要把窗口移到该显卡直接驱动的屏幕上，或在系统里调整显示接线。",
                "A '0 / 0' attempt/presented count means the GPU never drew a frame. "
                + "When the adapter does not cover the window's monitor (common on hybrid-graphics laptops), "
                + "move the window to a monitor driven by that adapter, or change the display cabling."));

            RegisterRefresh(() =>
            {
                detailRow.DetailText = UI?.GpuPresentationDiagnosticText()
                    ?? GetText("（未取到渲染控制器）", "(renderer unavailable)");
            });

            AddGroupToPage(group);
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
