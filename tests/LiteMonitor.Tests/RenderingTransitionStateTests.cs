using System.Text.Json;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Configuration;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【T02 合同 §12.4 序 1 / 序 2 / 序 3 · owner A 物化】
/// 覆盖：纯状态类型（§3.2）、句柄代次唯一真相源（§7.2）、
/// <c>RenderingRouteRequest.WindowIsLayered</c> 新分支（§3.1 / GPU-003）。
/// </summary>
public sealed class RenderingTransitionStateTests
{
    // ---------------------------------------------------------------- §3.1

    [Fact]
    public void WindowIsLayeredDefaultsToFalseAndKeepsDecisionByteIdentical()
    {
        ThemeRenderingConfig configuration = CreateGpuConfiguration(
            ThemeRenderingConfig.LiquidGlassMaterial,
            liveBackground: true);

        RenderingRouteDecision omitted = RenderingRoutePolicy.Decide(new(
            configuration,
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: true,
            WdaCapturePolicyAccepted: true,
            SafeMode: false));

        RenderingRouteDecision explicitFalse = RenderingRoutePolicy.Decide(new(
            configuration,
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
        Assert.Equal(RenderingRoute.GpuLiveBackground, omitted.Route);
        Assert.True(omitted.CaptureRequested);
    }

    [Fact]
    public void LayeredWindowNeverRequestsWdaExclusion()
    {
        RenderingRouteDecision decision = RenderingRoutePolicy.Decide(new(
            CreateGpuConfiguration(ThemeRenderingConfig.LiquidGlassMaterial, liveBackground: true),
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: true,
            WdaCapturePolicyAccepted: true,
            SafeMode: false,
            WindowIsLayered: true));

        Assert.Equal(RenderingRoute.GpuFixedInput, decision.Route);
        Assert.Equal(RenderBackendKind.Gpu, decision.EffectiveBackend);
        Assert.False(decision.CaptureRequested);
        Assert.Equal("wda_capture_policy_unsupported_on_layered_window", decision.FallbackCode);
    }

    [Theory]
    [InlineData(true, "safe_mode")]
    [InlineData(false, "gpu_effects_opt_in_required")]
    public void LayeredWindowDoesNotChangeExistingFallbackCodePriority(bool safeMode, string expectedCode)
    {
        RenderingRouteDecision decision = RenderingRoutePolicy.Decide(new(
            CreateGpuConfiguration(ThemeRenderingConfig.LiquidGlassMaterial, liveBackground: true),
            GpuEffectsEnabled: false,
            LiveBackgroundEnabled: true,
            WdaCapturePolicyAccepted: true,
            SafeMode: safeMode,
            WindowIsLayered: true));

        Assert.Equal(RenderingRoute.Legacy, decision.Route);
        Assert.Equal(expectedCode, decision.FallbackCode);
    }

    [Fact]
    public void LayeredWindowDoesNotOverrideWdaNotAcceptedReason()
    {
        // 新分支位于既有 WdaCapturePolicyAccepted 判定**之后** ⇒ 未接受 WDA 策略时原因码不变。
        RenderingRouteDecision decision = RenderingRoutePolicy.Decide(new(
            CreateGpuConfiguration(ThemeRenderingConfig.LiquidGlassMaterial, liveBackground: true),
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: true,
            WdaCapturePolicyAccepted: false,
            SafeMode: false,
            WindowIsLayered: true));

        Assert.Equal(RenderingRoute.GpuFixedInput, decision.Route);
        Assert.False(decision.CaptureRequested);
        Assert.Equal("wda_capture_policy_not_accepted", decision.FallbackCode);
    }

    // ---------------------------------------------------------------- §7.2

    [Fact]
    public void HandleGenerationAdvancesMonotonicallyFromOne()
    {
        WindowHandleGeneration generation = new();

        Assert.Equal(1, generation.Current);
        Assert.True(generation.IsCurrent(1));
        Assert.False(generation.IsOlder(1));

        long first = generation.Advance();
        long second = generation.Advance();

        Assert.Equal(2, first);
        Assert.Equal(3, second);
        Assert.Equal(3, generation.Current);
        Assert.True(generation.IsCurrent(3));
        Assert.True(generation.IsOlder(2));
        Assert.True(generation.IsOlder(1));
        Assert.False(generation.IsOlder(3));
        Assert.False(generation.IsCurrent(2));
    }

    [Fact]
    public void TwoGenerationsAreIndependentSources()
    {
        WindowHandleGeneration first = new();
        WindowHandleGeneration second = new();

        first.Advance();

        Assert.Equal(2, first.Current);
        Assert.Equal(1, second.Current);
    }

    [Fact]
    public void ProcessWideInstanceIsSharedAndNotASecondCounter()
    {
        // 【主审裁决 D-G】唯一实例由类型自身持有 ⇒ 全进程只有一个代次序列。
        // 生产路径（owner B 的协调器、owner C 的宿主）必须都消费这**同一个**实例，
        // 否则两侧会读到不同的代次 ⇒ 合同 §7.4 的跨代次丢弃判据失效。
        Assert.Same(WindowHandleGeneration.Instance, WindowHandleGeneration.Instance);

        long before = WindowHandleGeneration.Instance.Current;
        long advanced = WindowHandleGeneration.Instance.Advance();

        Assert.Equal(before + 1, advanced);
        Assert.Equal(advanced, WindowHandleGeneration.Instance.Current);
        Assert.True(WindowHandleGeneration.Instance.IsCurrent(advanced));
        Assert.True(WindowHandleGeneration.Instance.IsOlder(before));
    }

    // ---------------------------------------------------------------- §3.2 / §6

    [Fact]
    public void NewCleanupRecordStartsPendingWithZeroAttempts()
    {
        WindowCapturePolicyCleanup cleanup = new()
        {
            WindowHandle = new IntPtr(1234),
            Generation = 7,
            OriginalAffinity = 0x00000011,
            AffinityChangedBySession = true
        };

        Assert.Equal(CapturePolicyCleanupState.Pending, cleanup.State);
        Assert.False(cleanup.IsTerminal);
        Assert.Equal(0, cleanup.Attempts);
        Assert.Equal(new IntPtr(1234), cleanup.WindowHandle);
        Assert.Equal(7, cleanup.Generation);
        Assert.Equal(0x00000011u, cleanup.OriginalAffinity);
        Assert.True(cleanup.AffinityChangedBySession);
    }

    [Fact]
    public void FailedAttemptCountsUpAndKeepsLastError()
    {
        WindowCapturePolicyCleanup cleanup = new() { Generation = 1 };
        DateTimeOffset next = new(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8));

        Assert.True(cleanup.TryRecordFailedAttempt(
            "gpu_capture_policy_restore_failed", "boom", next));
        Assert.True(cleanup.TryRecordFailedAttempt(
            "gpu_capture_policy_restore_failed", "boom again", next.AddSeconds(1)));

        Assert.Equal(2, cleanup.Attempts);
        Assert.Equal("gpu_capture_policy_restore_failed", cleanup.LastErrorCode);
        Assert.Equal("boom again", cleanup.LastErrorMessage);
        Assert.Equal(next.AddSeconds(1), cleanup.NextAttemptAt);
        Assert.Equal(CapturePolicyCleanupState.Pending, cleanup.State);
        Assert.False(cleanup.IsTerminal);
    }

    [Fact]
    public void TerminalStateIsFinalAndIdempotent()
    {
        WindowCapturePolicyCleanup cleanup = new() { Generation = 1 };
        cleanup.TryRecordFailedAttempt("code", "msg", DateTimeOffset.MinValue);

        Assert.True(cleanup.TryMarkCompleted("window_destroyed"));
        Assert.Equal(CapturePolicyCleanupState.Completed, cleanup.State);
        Assert.Equal("window_destroyed", cleanup.TerminalReason);
        Assert.True(cleanup.IsTerminal);

        // 终态不可回退：后续迁移必须是无副作用的 no-op。
        Assert.False(cleanup.TryRecordFailedAttempt("late", "late", DateTimeOffset.MaxValue));
        Assert.False(cleanup.TryMarkFailed("exhausted"));
        Assert.False(cleanup.TryMarkCompleted("superseded"));
        Assert.False(cleanup.TryMarkSupersededByNewGeneration());

        Assert.Equal(CapturePolicyCleanupState.Completed, cleanup.State);
        Assert.Equal("window_destroyed", cleanup.TerminalReason);
        Assert.Equal(1, cleanup.Attempts);
    }

    [Fact]
    public void SupersededByNewGenerationCarriesWarningCode()
    {
        WindowCapturePolicyCleanup cleanup = new() { Generation = 1 };

        Assert.True(cleanup.TryMarkSupersededByNewGeneration());

        Assert.Equal(CapturePolicyCleanupState.Completed, cleanup.State);
        Assert.Equal("superseded_by_new_generation", cleanup.TerminalReason);
        Assert.Equal("gpu_capture_policy_cleanup_superseded", cleanup.WarningCode);
        Assert.True(cleanup.IsTerminal);
    }

    [Fact]
    public void FailedTerminalKeepsErrorCodeAndReason()
    {
        WindowCapturePolicyCleanup cleanup = new() { Generation = 1 };

        Assert.True(cleanup.TryMarkFailed(
            "exhausted", "gpu_capture_policy_restore_retry_exhausted", "budget gone"));

        Assert.Equal(CapturePolicyCleanupState.Failed, cleanup.State);
        Assert.Equal("exhausted", cleanup.TerminalReason);
        Assert.Equal("gpu_capture_policy_restore_retry_exhausted", cleanup.LastErrorCode);
        Assert.Equal("budget gone", cleanup.LastErrorMessage);
        Assert.True(cleanup.IsTerminal);
    }

    // ---------------------------------------------------------------- §5.3 规则 2

    [Fact]
    public void DiagnosticFieldsNeverOverwriteEachOther()
    {
        RenderingTransitionState state = new(
            TransitionId: 3,
            HandleGeneration: 2,
            HostGeneration: 1,
            LastAppliedRequestVersion: 5,
            ExpectedRoute: RenderingRoute.GpuLiveBackground,
            EffectiveRoute: RenderingRoute.Legacy,
            Phase: RenderingTransitionPhase.Failed,
            BindingState: PresentationBindingState.Presented,
            CleanupState: CapturePolicyCleanupState.Failed,
            Trigger: RenderingTransitionTrigger.RuntimeFailure,
            FirstFrameOutcome: FirstFrameOutcome.Unknown,
            PendingFirstFrame: false,
            FailureCode: "gpu_transition_layout_restore_failed",
            CleanupErrorCode: "gpu_capture_policy_restore_failed",
            CleanupWarningCode: "gpu_capture_policy_cleanup_superseded");

        Assert.Equal("gpu_transition_layout_restore_failed", state.FailureCode);
        Assert.Equal("gpu_capture_policy_restore_failed", state.CleanupErrorCode);
        Assert.Equal("gpu_capture_policy_cleanup_superseded", state.CleanupWarningCode);
        Assert.Equal(RenderingTransitionPhase.Failed, state.Phase);
        Assert.Equal(FirstFrameOutcome.Unknown, state.FirstFrameOutcome);
        Assert.Equal(CapturePolicyCleanupState.Failed, state.CleanupState);
    }

    private static ThemeRenderingConfig CreateGpuConfiguration(
        string material,
        bool liveBackground = false) =>
        new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = material,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(liveBackground),
                ["cornerRadius"] = JsonSerializer.SerializeToElement(24),
                ["primaryCut"] = JsonSerializer.SerializeToElement(14)
            }
        };
}
