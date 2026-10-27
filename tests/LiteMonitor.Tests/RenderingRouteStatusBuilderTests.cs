using System.Collections.Generic;
using LiteMonitor.src.UI.SettingsPage;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 「渲染路线」页状态聚合器（<see cref="RenderingRouteStatusBuilder"/>）的行为回归。
    /// <para>★ 语义锚点：2026-10-24 步 0 实测结论（R3 §10.1）——分层窗口不再阻断 GPU
    /// 实时背景；状态行必须携带**真实原因码**（R3 §4.1-1.3），不得出现无因"已回退"。</para>
    /// </summary>
    public class RenderingRouteStatusBuilderTests
    {
        private static RenderingRouteStatusSnapshot Snap(
            bool themeUsesGpuMaterial = true,
            bool gpuEffects = true,
            bool presenting = true,
            bool liveActive = false,
            bool liveRequested = false,
            string? runtimeFailure = null,
            string? fallbackCode = null,
            bool layered = false,
            bool guardConservative = false,
            string? guardCode = null) => new(
                ThemeUsesGpuMaterial: themeUsesGpuMaterial,
                GpuEffectsEnabled: gpuEffects,
                PresentationActive: presenting,
                LiveBackgroundActive: liveActive,
                LiveBackgroundRequested: liveRequested,
                RuntimeFailure: runtimeFailure,
                FallbackCode: fallbackCode,
                WindowIsLayered: layered,
                GpuGuardConservative: guardConservative,
                GpuGuardStatusCode: guardCode);

        [Fact]
        public void LegacyThemeWithoutGpuSwitchReportsLegacyOnly()
        {
            var lines = RenderingRouteStatusBuilder.Build(Snap(themeUsesGpuMaterial: false, gpuEffects: false));

            Assert.Single(lines);
            Assert.Equal(RenderingRouteStatusKind.ThemeDeclaresLegacy, lines[0].Kind);
        }

        [Fact]
        public void LegacyThemeWithGpuSwitchOnExplainsSwitchIsIgnored()
        {
            var lines = RenderingRouteStatusBuilder.Build(Snap(themeUsesGpuMaterial: false, gpuEffects: true));

            Assert.Equal(2, lines.Count);
            Assert.Equal(RenderingRouteStatusKind.ThemeDeclaresLegacy, lines[0].Kind);
            Assert.Equal(RenderingRouteStatusKind.NotStarted, lines[1].Kind);
            Assert.Equal("legacy_theme_ignores_gpu_switch", lines[1].Detail);
        }

        [Fact]
        public void GpuThemeWithEffectsOffReportsOptInReason()
        {
            var lines = RenderingRouteStatusBuilder.Build(Snap(gpuEffects: false));

            Assert.Single(lines);
            Assert.Equal(RenderingRouteStatusKind.NotStarted, lines[0].Kind);
            Assert.Equal("gpu_effects_opt_in_required", lines[0].Detail);
        }

        [Fact]
        public void NotPresentingReportsFailureOrFallbackCode()
        {
            var lines = RenderingRouteStatusBuilder.Build(
                Snap(presenting: false, runtimeFailure: "gpu_present_failed", fallbackCode: "safe_mode"));

            Assert.Single(lines);
            Assert.Equal(RenderingRouteStatusKind.NotStarted, lines[0].Kind);
            // 运行失败码优先于回退原因码（R3 §4.1-1.3：用户看到的应当是"原因"）。
            Assert.Equal("gpu_present_failed", lines[0].Detail);
        }

        [Fact]
        public void NotPresentingWithoutAnyCodeStillReportsNotStarted()
        {
            var lines = RenderingRouteStatusBuilder.Build(Snap(presenting: false));

            Assert.Single(lines);
            Assert.Equal(RenderingRouteStatusKind.NotStarted, lines[0].Kind);
            Assert.Null(lines[0].Detail);
        }

        [Fact]
        public void PresentingWithLiveBackgroundOnReportsRunningAndLive()
        {
            var lines = RenderingRouteStatusBuilder.Build(
                Snap(presenting: true, liveActive: true, liveRequested: true));

            Assert.Equal(2, lines.Count);
            Assert.Equal(RenderingRouteStatusKind.GpuRunning, lines[0].Kind);
            Assert.Equal(RenderingRouteStatusKind.LiveBackgroundOn, lines[1].Kind);
        }

        [Fact]
        public void PresentingWithLiveRequestedButInactiveReportsUnavailableWithReason()
        {
            var lines = RenderingRouteStatusBuilder.Build(
                Snap(presenting: true, liveActive: false, liveRequested: true, fallbackCode: "gpu_capture_failed"));

            Assert.Equal(2, lines.Count);
            Assert.Equal(RenderingRouteStatusKind.GpuRunning, lines[0].Kind);
            Assert.Equal(RenderingRouteStatusKind.LiveBackgroundUnavailable, lines[1].Kind);
            Assert.Equal("gpu_capture_failed", lines[1].Detail);
        }

        [Fact]
        public void PresentingWithLiveOffReportsFixedBackground()
        {
            var lines = RenderingRouteStatusBuilder.Build(
                Snap(presenting: true, liveActive: false, liveRequested: false));

            Assert.Equal(2, lines.Count);
            Assert.Equal(RenderingRouteStatusKind.GpuRunning, lines[0].Kind);
            Assert.Equal(RenderingRouteStatusKind.LiveBackgroundOff, lines[1].Kind);
        }

        [Fact]
        public void LayeredWindowAddsDiagnosticButNeverChangesRouteLines()
        {
            // 方案 A：分层是**诊断**不是回退（2026-10-24 步 0 实测可达）。
            var layered = RenderingRouteStatusBuilder.Build(
                Snap(presenting: true, liveActive: true, liveRequested: true, layered: true));
            var notLayered = RenderingRouteStatusBuilder.Build(
                Snap(presenting: true, liveActive: true, liveRequested: true, layered: false));

            Assert.Equal(notLayered.Count + 1, layered.Count);
            Assert.Equal(RenderingRouteStatusKind.LayeredDiagnostic, layered[^1].Kind);
            Assert.DoesNotContain(layered, l => l.Kind == RenderingRouteStatusKind.NotStarted);
        }

        [Fact]
        public void RuntimeFailurePreferredOverFallbackCodeForLiveUnavailable()
        {
            var lines = RenderingRouteStatusBuilder.Build(
                Snap(presenting: true, liveActive: false, liveRequested: true,
                     runtimeFailure: "gpu_capture_session_faulted", fallbackCode: "wda_capture_policy_not_accepted"));

            Assert.Equal(RenderingRouteStatusKind.LiveBackgroundUnavailable, lines[1].Kind);
            Assert.Equal("gpu_capture_session_faulted", lines[1].Detail);
        }

        // ================================================================
        // 【S06】启动守卫保守状态行：与运行原因并列，原码保留在 Detail。
        // ================================================================

        [Fact]
        public void GuardConservativeAddsGuardLineNextToRunReason()
        {
            var lines = RenderingRouteStatusBuilder.Build(
                Snap(presenting: false, fallbackCode: "safe_mode",
                     guardConservative: true, guardCode: "gpu_startup_guard_previous_attempt_unclean"));

            Assert.Equal(2, lines.Count);
            Assert.Equal(RenderingRouteStatusKind.NotStarted, lines[0].Kind);
            Assert.Equal("safe_mode", lines[0].Detail);
            Assert.Equal(RenderingRouteStatusKind.GpuGuardConservative, lines[1].Kind);
            Assert.Equal("gpu_startup_guard_previous_attempt_unclean", lines[1].Detail);
        }

        [Fact]
        public void GuardStorageCodeIsPreservedVerbatim()
        {
            var lines = RenderingRouteStatusBuilder.Build(
                Snap(presenting: false, guardConservative: true,
                     guardCode: "gpu_startup_guard_storage_unavailable"));

            Assert.Contains(lines, l =>
                l.Kind == RenderingRouteStatusKind.GpuGuardConservative &&
                l.Detail == "gpu_startup_guard_storage_unavailable");
        }

        [Fact]
        public void GuardLineDefaultsToUncleanCodeWhenMissing()
        {
            var lines = RenderingRouteStatusBuilder.Build(Snap(presenting: false, guardConservative: true));

            Assert.Contains(lines, l =>
                l.Kind == RenderingRouteStatusKind.GpuGuardConservative &&
                l.Detail == "gpu_startup_guard_previous_attempt_unclean");
        }

        [Fact]
        public void GuardLineAbsentWhenGuardIsClean()
        {
            var lines = RenderingRouteStatusBuilder.Build(Snap(presenting: false));

            Assert.DoesNotContain(lines, l => l.Kind == RenderingRouteStatusKind.GpuGuardConservative);
        }

        [Fact]
        public void GuardLineAbsentWhilePresenting()
        {
            // GPU 已在实际出帧 ⇒ 守卫状态不再是用户需要行动的信号。
            var lines = RenderingRouteStatusBuilder.Build(
                Snap(presenting: true, guardConservative: true,
                     guardCode: "gpu_startup_guard_previous_attempt_unclean"));

            Assert.DoesNotContain(lines, l => l.Kind == RenderingRouteStatusKind.GpuGuardConservative);
        }

        [Fact]
        public void GuardLineAbsentWhenEffectsAreOff()
        {
            // 普通未启用 GPU（opt-in）是独立原因，守卫行不与之混叠。
            var lines = RenderingRouteStatusBuilder.Build(
                Snap(presenting: false, gpuEffects: false, guardConservative: true));

            var line = Assert.Single(lines);
            Assert.Equal(RenderingRouteStatusKind.NotStarted, line.Kind);
            Assert.Equal("gpu_effects_opt_in_required", line.Detail);
        }
    }
}
