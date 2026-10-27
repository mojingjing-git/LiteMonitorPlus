using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    /// <summary>
    /// 渲染路线状态聚合的**共享视图件**（自「渲染路线」页迁出 —— 渲染路线交互优化）。
    /// <para>「主面板」页的路线状态行与「系统与硬件」页的路线诊断卡共用同一聚合逻辑；
    /// 纯聚合仍在 <see cref="RenderingRouteStatusBuilder"/>（有单测），本类只做取数与取词。
    /// MainForm/UI 允许为 null（探针与设计器场景），空传播不得 NRE。</para>
    /// </summary>
    internal static class RenderingRouteStatusView
    {
        private static bool IsGpuTheme(ThemeRenderingConfig? rendering) =>
            rendering is not null &&
            string.Equals(rendering.Backend?.Trim(),
                ThemeRenderingConfig.GpuBackend,
                StringComparison.OrdinalIgnoreCase);

        /// <summary>「主题声明路线」行文本。</summary>
        public static string ThemeRouteText(bool chinese)
        {
            ThemeRenderingConfig? rendering = ThemeManager.Current.Rendering;
            if (!IsGpuTheme(rendering))
            {
                return ZH(chinese,
                    "GDI+ 旧路线（主题未声明 GPU 材质）",
                    "GDI+ legacy (theme declares no GPU material)");
            }

            string material = rendering!.Material ?? string.Empty;
            return ZH(chinese, $"GPU 材质（{material}）", $"GPU material ({material})");
        }

        /// <summary>状态行聚合（取数自 Config / UI / MainForm；null 传播安全）。</summary>
        public static IReadOnlyList<string> ComposeStatusLines(
            Settings config, UIController? ui, MainForm? mainForm, bool chinese)
        {
            var snapshot = new RenderingRouteStatusSnapshot(
                ThemeUsesGpuMaterial: IsGpuTheme(ThemeManager.Current.Rendering),
                GpuEffectsEnabled: config.GpuThemeEffectsEnabled,
                PresentationActive: ui?.IsGpuPresentationActive == true,
                LiveBackgroundActive: ui?.IsGpuLiveBackgroundActive == true,
                LiveBackgroundRequested: ui?.IsGpuLiveBackgroundRequested == true,
                RuntimeFailure: ui?.GpuRuntimeFailure,
                FallbackCode: ui?.GpuFallbackCode,
                WindowIsLayered: mainForm is { IsHandleCreated: true } && mainForm.IsWindowLayered(),
                GpuGuardConservative: GpuStartupGuard.Shared.ConservativeForThisProcess,
                GpuGuardStatusCode: GpuStartupGuard.Shared.StatusCode);

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
                    // 【S06】守卫原因码转易懂文案；原码已由 Detail 追加展示，排障信息不丢。
                    RenderingRouteStatusKind.GpuGuardConservative => string.Equals(
                        line.Detail,
                        "gpu_startup_guard_storage_unavailable",
                        StringComparison.Ordinal)
                        ? "Menu.RouteStatus.GpuGuardStorage"
                        : "Menu.RouteStatus.GpuGuardUnclean",
                    RenderingRouteStatusKind.LayeredDiagnostic => "Menu.RouteStatus.LayeredNote",
                    _ => "Menu.RouteStatus.NotStarted"
                };
                string text = LanguageManager.T(key);
                lines.Add(string.IsNullOrWhiteSpace(line.Detail) ? text : $"{text} [{line.Detail}]");
            }

            return lines;
        }

        /// <summary>单行摘要（表格行用）：取第一条状态行。</summary>
        public static string ComposeStatusSummary(
            Settings config, UIController? ui, MainForm? mainForm, bool chinese) =>
            ComposeStatusLines(config, ui, mainForm, chinese).FirstOrDefault()
                ?? ZH(chinese, "（无状态）", "(no status)");

        /// <summary>多行详情（全行块用）：全部状态行。</summary>
        public static string ComposeStatusDetail(
            Settings config, UIController? ui, MainForm? mainForm, bool chinese)
        {
            var lines = ComposeStatusLines(config, ui, mainForm, chinese);
            return lines.Count == 0
                ? ZH(chinese, "（无状态）", "(no status)")
                : string.Join(Environment.NewLine, lines);
        }

        private static string ZH(bool chinese, string zh, string en) => chinese ? zh : en;

        /// <summary>表格行右侧的**单行**值标签：固定宽度 + AutoEllipsis，
        /// 配合 LiteSettingsItem 的"控件右对齐、行内垂直居中"布局（不可多行）。</summary>
        public static Label MakeValueLabel()
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
    }

    /// <summary>全行多行详情块（文本可在 RegisterRefresh 里更新）。
    /// 形制与 LiteHintRow 相同，但 LiteHintRow 的文本是构造期固定的。</summary>
    internal sealed class FullWidthDetailRow : Panel
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
}
