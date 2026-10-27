using System.Collections.Generic;

namespace LiteMonitor.src.UI.SettingsPage
{
    /// <summary>
    /// 「渲染路线」页状态行的**种类**。每类对应一条可本地化的状态文案 key
    /// （<c>Menu.RouteStatus.*</c>，见 resources/lang/*.json）；
    /// <see cref="RenderingRouteStatusLine.Detail"/> 携带原样原因码/诊断码，供排障追加显示。
    /// </summary>
    public enum RenderingRouteStatusKind
    {
        /// <summary>当前主题未声明 GPU 材质 ⇒ 渲染走 GDI+ 旧路线。</summary>
        ThemeDeclaresLegacy,

        /// <summary>GPU 呈现运行中。</summary>
        GpuRunning,

        /// <summary>实时桌面背景运行中（WDA 排除生效）。</summary>
        LiveBackgroundOn,

        /// <summary>GPU 运行中但使用固定背景（实时背景未开启）。</summary>
        LiveBackgroundOff,

        /// <summary>实时背景已开启但暂不可用（捕获失败/回退），Detail = 原因码。</summary>
        LiveBackgroundUnavailable,

        /// <summary>GPU 呈现未运行。Detail = 原因码（safe_mode / gpu_effects_opt_in_required / 运行失败码等），可为 null。</summary>
        NotStarted,

        /// <summary>
        /// 【S06】GPU 启动守卫处于保守状态（上次尝试未确认首帧 / 守卫存储不可用），
        /// 需要用户显式重试。Detail = 守卫原因码（gpu_startup_guard_*），与
        /// <see cref="NotStarted"/> 的运行原因并列展示——守卫状态与运行失败是两个事实。
        /// </summary>
        GpuGuardConservative,

        /// <summary>当前窗口为分层窗口（透明度&lt;100%）——实测不影响实时背景，仅诊断展示。</summary>
        LayeredDiagnostic
    }

    public sealed record RenderingRouteStatusLine(RenderingRouteStatusKind Kind, string? Detail);

    /// <summary>
    /// 构建状态行所需的**纯数据快照**（无 WinForms/无 UIController 依赖 ⇒ 可直接单测）。
    /// </summary>
    public sealed record RenderingRouteStatusSnapshot(
        bool ThemeUsesGpuMaterial,
        bool GpuEffectsEnabled,
        bool PresentationActive,
        bool LiveBackgroundActive,
        bool LiveBackgroundRequested,
        string? RuntimeFailure,
        string? FallbackCode,
        bool WindowIsLayered,
        bool GpuGuardConservative = false,
        string? GpuGuardStatusCode = null)
    {
        /// <summary>Detail 的取值优先级：运行失败码优先于回退原因码。</summary>
        public string? FailureOrFallbackCode => RuntimeFailure ?? FallbackCode;
    }

    /// <summary>
    /// 「渲染路线」页**路线总览卡**的状态行聚合器（纯函数）。
    /// <para>★ 语义对齐 <c>MainFormWinHelper</c> 合同 §10 与 2026-10-24 步 0 实测结论：
    /// 分层窗口（透明度&lt;100%）**不再**阻断 GPU 实时背景——它是诊断信息，不是回退原因。
    /// 状态行只陈述事实与真实原因码，不做"已回退"这种无因表述（R3 §4.1-1.3）。</para>
    /// </summary>
    public static class RenderingRouteStatusBuilder
    {
        public static IReadOnlyList<RenderingRouteStatusLine> Build(RenderingRouteStatusSnapshot s)
        {
            var lines = new List<RenderingRouteStatusLine>();

            if (!s.ThemeUsesGpuMaterial)
            {
                lines.Add(new(RenderingRouteStatusKind.ThemeDeclaresLegacy, null));
                if (s.GpuEffectsEnabled)
                {
                    lines.Add(new(
                        RenderingRouteStatusKind.NotStarted,
                        "legacy_theme_ignores_gpu_switch"));
                }
                return lines;
            }

            if (!s.GpuEffectsEnabled)
            {
                lines.Add(new(RenderingRouteStatusKind.NotStarted, "gpu_effects_opt_in_required"));
                return lines;
            }

            if (!s.PresentationActive)
            {
                // GPU 已开但宿主未运行：原因码来自期望路由的回退判定或宿主运行失败。
                lines.Add(new(RenderingRouteStatusKind.NotStarted, s.FailureOrFallbackCode));
                // 【S06】启动守卫保守状态与运行失败并列展示：守卫说的是"上一次尝试
                // 没有以首帧收尾 / 状态写不进磁盘"，运行失败说的是"这次为什么没跑起来"。
                // 两者都是真实原因，缺一不可（R3 §4.1-1.3）。
                if (s.GpuGuardConservative)
                {
                    lines.Add(new(
                        RenderingRouteStatusKind.GpuGuardConservative,
                        s.GpuGuardStatusCode ?? "gpu_startup_guard_previous_attempt_unclean"));
                }
            }
            else
            {
                lines.Add(new(RenderingRouteStatusKind.GpuRunning, null));
                if (s.LiveBackgroundActive)
                {
                    lines.Add(new(RenderingRouteStatusKind.LiveBackgroundOn, null));
                }
                else if (s.LiveBackgroundRequested)
                {
                    lines.Add(new(
                        RenderingRouteStatusKind.LiveBackgroundUnavailable,
                        s.FailureOrFallbackCode));
                }
                else
                {
                    lines.Add(new(RenderingRouteStatusKind.LiveBackgroundOff, null));
                }
            }

            if (s.WindowIsLayered)
            {
                lines.Add(new(RenderingRouteStatusKind.LayeredDiagnostic, null));
            }

            return lines;
        }
    }
}
