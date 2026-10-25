using System;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// ★ 独立复核：owner A 物化的 T02 合同序 1/2/3 的行为核对。
    /// <para>与实现者的 <c>RenderingTransitionStateTests</c> **无共用断言**，逐条独立重写。</para>
    /// </summary>
    public sealed class VerifyLm045IndependentRenderingTests
    {
        // ---------------------------------------------------------------- §3.1

        [Fact]
        public void R1_分层窗口_必须降级为GpuFixedInput且不申请WDA排除()
        {
            var d = RenderingRoutePolicy.Decide(new(
                Gpu(ThemeRenderingConfig.LiquidGlassMaterial, true),
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: false,
                WindowIsLayered: true));

            Assert.Equal(RenderingRoute.GpuFixedInput, d.Route);
            Assert.Equal(RenderBackendKind.Gpu, d.EffectiveBackend);
            Assert.Equal(MaterialKind.LiquidGlass, d.EffectiveMaterial);
            Assert.False(d.CaptureRequested);
            Assert.Equal("wda_capture_policy_unsupported_on_layered_window", d.FallbackCode);
        }

        [Fact]
        public void R2_未传WindowIsLayered_与显式传false_输出逐位相同()
        {
            var omitted = RenderingRoutePolicy.Decide(new(
                Gpu(ThemeRenderingConfig.LiquidGlassMaterial, true),
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: false));

            var explicitFalse = RenderingRoutePolicy.Decide(new(
                Gpu(ThemeRenderingConfig.LiquidGlassMaterial, true),
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: false,
                WindowIsLayered: false));

            Assert.Equal(explicitFalse.Route, omitted.Route);
            Assert.Equal(explicitFalse.EffectiveBackend, omitted.EffectiveBackend);
            Assert.Equal(explicitFalse.EffectiveMaterial, omitted.EffectiveMaterial);
            Assert.Equal(explicitFalse.CaptureRequested, omitted.CaptureRequested);
            Assert.Equal(explicitFalse.FallbackCode, omitted.FallbackCode);
            Assert.Equal(explicitFalse.Errors, omitted.Errors);
        }

        [Theory]
        [InlineData(true, true, "safe_mode")]
        [InlineData(true, false, "safe_mode")]
        [InlineData(false, false, "gpu_effects_opt_in_required")]
        public void R3_既有原因码优先级不因分层窗口而改变(bool safeMode, bool gpuEffects, string expected)
        {
            var layered = RenderingRoutePolicy.Decide(new(
                Gpu(ThemeRenderingConfig.LiquidGlassMaterial, true),
                GpuEffectsEnabled: gpuEffects,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: safeMode,
                WindowIsLayered: true));

            var plain = RenderingRoutePolicy.Decide(new(
                Gpu(ThemeRenderingConfig.LiquidGlassMaterial, true),
                GpuEffectsEnabled: gpuEffects,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: safeMode,
                WindowIsLayered: false));

            // 分层窗口**不得**改变既有原因码（safe_mode / gpu_effects_opt_in_required）
            Assert.Equal(expected, layered.FallbackCode);
            Assert.Equal(expected, plain.FallbackCode);
            Assert.Equal(plain.Route, layered.Route);
            Assert.Equal(plain.EffectiveBackend, layered.EffectiveBackend);
            Assert.Equal(plain.EffectiveMaterial, layered.EffectiveMaterial);
            Assert.Equal(plain.CaptureRequested, layered.CaptureRequested);
        }

        [Fact]
        public void R3b_一切就绪时_分层窗口只降级路由不改其它字段()
        {
            var layered = RenderingRoutePolicy.Decide(new(
                Gpu(ThemeRenderingConfig.LiquidGlassMaterial, true),
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: false,
                WindowIsLayered: true));

            var plain = RenderingRoutePolicy.Decide(new(
                Gpu(ThemeRenderingConfig.LiquidGlassMaterial, true),
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: false,
                WindowIsLayered: false));

            Assert.Equal("wda_capture_policy_unsupported_on_layered_window", layered.FallbackCode);
            Assert.Null(plain.FallbackCode);
            Assert.Equal(RenderingRoute.GpuFixedInput, layered.Route);
            Assert.Equal(RenderingRoute.GpuLiveBackground, plain.Route);
            Assert.Equal(plain.EffectiveBackend, layered.EffectiveBackend);
            Assert.Equal(plain.EffectiveMaterial, layered.EffectiveMaterial);
            Assert.False(layered.CaptureRequested);
            Assert.True(plain.CaptureRequested);
        }
        [Fact]
        public void R4_未接受WDA策略时_原因码仍是既有那个()
        {
            var d = RenderingRoutePolicy.Decide(new(
                Gpu(ThemeRenderingConfig.LiquidGlassMaterial, true),
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: false,
                SafeMode: false,
                WindowIsLayered: true));

            Assert.Equal(RenderingRoute.GpuFixedInput, d.Route);
            Assert.False(d.CaptureRequested);
            Assert.Equal("wda_capture_policy_not_accepted", d.FallbackCode);
        }

        // ---------------------------------------------------------------- §6

        [Fact]
        public void R5_清理记录终态Completed后_所有迁移方法都返回false且逐字段不变()
        {
            var cleanup = new WindowCapturePolicyCleanup { Generation = 3, OriginalAffinity = 0x11 };
            DateTimeOffset next = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8));

            Assert.True(cleanup.TryRecordFailedAttempt("err-1", "boom", next));
            Assert.True(cleanup.TryMarkCompleted("window_destroyed"));
            Assert.True(cleanup.IsTerminal);

            // 终态快照
            int attempts = cleanup.Attempts;
            var state = cleanup.State;
            string? terminal = cleanup.TerminalReason;
            string? warning = cleanup.WarningCode;
            DateTimeOffset nextAt = cleanup.NextAttemptAt;
            string? lastCode = cleanup.LastErrorCode;
            string? lastMsg = cleanup.LastErrorMessage;

            Assert.False(cleanup.TryRecordFailedAttempt("late", "late", DateTimeOffset.MaxValue));
            Assert.False(cleanup.TryMarkCompleted("superseded"));
            Assert.False(cleanup.TryMarkFailed("exhausted"));
            Assert.False(cleanup.TryMarkSupersededByNewGeneration());

            Assert.Equal(attempts, cleanup.Attempts);
            Assert.Equal(state, cleanup.State);
            Assert.Equal(terminal, cleanup.TerminalReason);
            Assert.Equal(warning, cleanup.WarningCode);
            Assert.Equal(nextAt, cleanup.NextAttemptAt);
            Assert.Equal(lastCode, cleanup.LastErrorCode);
            Assert.Equal(lastMsg, cleanup.LastErrorMessage);
        }

        [Fact]
        public void R6_清理记录终态Failed后_所有迁移方法都返回false且逐字段不变()
        {
            var cleanup = new WindowCapturePolicyCleanup { Generation = 3 };
            DateTimeOffset next = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8));

            Assert.True(cleanup.TryRecordFailedAttempt("err-1", "boom", next));
            Assert.True(cleanup.TryMarkFailed("exhausted", "retry-exhausted", "budget gone"));
            Assert.True(cleanup.IsTerminal);

            int attempts = cleanup.Attempts;
            var state = cleanup.State;
            string? terminal = cleanup.TerminalReason;
            string? warning = cleanup.WarningCode;
            DateTimeOffset nextAt = cleanup.NextAttemptAt;
            string? lastCode = cleanup.LastErrorCode;
            string? lastMsg = cleanup.LastErrorMessage;

            Assert.False(cleanup.TryRecordFailedAttempt("late", "late", DateTimeOffset.MaxValue));
            Assert.False(cleanup.TryMarkCompleted("superseded"));
            Assert.False(cleanup.TryMarkFailed("again"));
            Assert.False(cleanup.TryMarkSupersededByNewGeneration());

            Assert.Equal(attempts, cleanup.Attempts);
            Assert.Equal(state, cleanup.State);
            Assert.Equal(terminal, cleanup.TerminalReason);
            Assert.Equal(warning, cleanup.WarningCode);
            Assert.Equal(nextAt, cleanup.NextAttemptAt);
            Assert.Equal(lastCode, cleanup.LastErrorCode);
            Assert.Equal(lastMsg, cleanup.LastErrorMessage);
        }

        [Fact]
        public void R7_跨代次被取代_终态与告警码()
        {
            var cleanup = new WindowCapturePolicyCleanup { Generation = 1 };

            Assert.True(cleanup.TryMarkSupersededByNewGeneration());

            Assert.Equal(CapturePolicyCleanupState.Completed, cleanup.State);
            Assert.Equal("superseded_by_new_generation", cleanup.TerminalReason);
            Assert.Equal("gpu_capture_policy_cleanup_superseded", cleanup.WarningCode);
            Assert.True(cleanup.IsTerminal);

            // 幂等：再次调用不得改变任何字段
            Assert.False(cleanup.TryMarkSupersededByNewGeneration());
            Assert.False(cleanup.TryMarkCompleted("other"));
            Assert.Equal("superseded_by_new_generation", cleanup.TerminalReason);
            Assert.Equal("gpu_capture_policy_cleanup_superseded", cleanup.WarningCode);
        }

        // ---------------------------------------------------------------- §7.2

        [Fact]
        public void R8_句柄代次单调推进且语义正确()
        {
            var gen = new WindowHandleGeneration();

            Assert.Equal(1, gen.Current);
            Assert.True(gen.IsCurrent(1));
            Assert.False(gen.IsOlder(1));
            Assert.False(gen.IsCurrent(0));
            Assert.True(gen.IsOlder(0));

            long g2 = gen.Advance();
            long g3 = gen.Advance();

            Assert.Equal(2, g2);
            Assert.Equal(3, g3);
            Assert.Equal(3, gen.Current);
            Assert.True(gen.IsCurrent(3));
            Assert.False(gen.IsCurrent(2));
            Assert.True(gen.IsOlder(2));
            Assert.True(gen.IsOlder(1));
            Assert.False(gen.IsOlder(3));
            Assert.False(gen.IsOlder(gen.Current + 1));
        }

        [Fact]
        public void R9_进程级唯一实例_是同一个对象()
        {
            Assert.Same(WindowHandleGeneration.Instance, WindowHandleGeneration.Instance);

            long before = WindowHandleGeneration.Instance.Current;
            long advanced = WindowHandleGeneration.Instance.Advance();

            Assert.Equal(before + 1, advanced);
            Assert.Equal(advanced, WindowHandleGeneration.Instance.Current);
            Assert.True(WindowHandleGeneration.Instance.IsCurrent(advanced));
            Assert.True(WindowHandleGeneration.Instance.IsOlder(before));
        }

        private static ThemeRenderingConfig Gpu(string material, bool liveBackground) =>
            new()
            {
                Backend = ThemeRenderingConfig.GpuBackend,
                Material = material,
                Parameters = new()
                {
                    ["liveBackground"] = System.Text.Json.JsonSerializer.SerializeToElement(liveBackground),
                    ["cornerRadius"] = System.Text.Json.JsonSerializer.SerializeToElement(24),
                    ["primaryCut"] = System.Text.Json.JsonSerializer.SerializeToElement(14)
                }
            };
    }
}