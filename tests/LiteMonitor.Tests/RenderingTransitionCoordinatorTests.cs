using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【GPU T03 · owner B 物化】切换协调器 + 清理责任 ledger 的确定性单测。
/// <para>覆盖合同 §4（GPU → legacy 的 9 步）、§8（<c>HandleCreated</c> 判别位）、
/// §7.2（句柄代次唯一真相源）、§8.4（Tick 启动守卫）、§6.2/§6.4/§7.4（清理责任）、
/// 以及 U02 / U04 / U05 / U06 / U06b / U10 / U11 / U13 / U14。</para>
/// <para><b>不依赖真实桌面 / GPU / 窗口</b>：协调器与 ledger 都是纯 CLR，
/// 时钟与窗口存活探针都由外部注入 ⇒ 完全确定性。</para>
/// </summary>
public sealed class RenderingTransitionCoordinatorTests
{
    // ================================================================
    // 测试替身
    // ================================================================

    /// <summary>假时钟（注入协调器 ⇒ 超时兜底用例零等待、零 flake）。</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        internal FakeTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan delta) => _now += delta;
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8));

    private sealed class Harness
    {
        internal WindowHandleGeneration Generation { get; } = new();
        internal FakeTimeProvider Clock { get; } = new(T0);
        internal RenderingTransitionCoordinator Coordinator { get; }

        internal Harness()
        {
            Coordinator = new RenderingTransitionCoordinator(Generation, Clock);
        }
    }

    // ================================================================
    // §8 判别位（HandleCreated 的一次性重建令牌）
    // ================================================================

    [Fact]
    public void 判别位_置位后计数为一且带非零请求号()
    {
        Harness h = new();

        long requestId = h.Coordinator.RequestControlledRecreate("gpu_to_legacy");

        Assert.Equal(1, h.Coordinator.PendingControlledRecreateCount);
        Assert.True(requestId > 0);
        Assert.Equal(requestId, h.Coordinator.PendingControlledRecreateRequestId);
    }

    [Fact]
    public void 判别位_正常消费一次后计数归零且不可再消费()
    {
        Harness h = new();
        long requestId = h.Coordinator.RequestControlledRecreate("gpu_to_legacy");

        bool consumed = h.Coordinator.TryConsumeControlledRecreate(out long consumedId);

        Assert.True(consumed);
        Assert.Equal(requestId, consumedId);
        Assert.Equal(0, h.Coordinator.PendingControlledRecreateCount);
        Assert.Equal(requestId, h.Coordinator.LastConsumedControlledRecreateRequestId);

        // 规则 ①：计数已归零 ⇒ 第二次消费必须落空（构造期首次创建不得被误判为重建）。
        Assert.False(h.Coordinator.TryConsumeControlledRecreate(out long secondId));
        Assert.Equal(0, secondId);
    }

    [Fact]
    public void 判别位_取消指定请求后计数归零()
    {
        Harness h = new();
        long requestId = h.Coordinator.RequestControlledRecreate("gpu_to_legacy");

        // 规则 ②：RecreateHandle() 抛异常 ⇒ 立即取消并归零。
        Assert.True(h.Coordinator.CancelControlledRecreate(requestId));
        Assert.Equal(0, h.Coordinator.PendingControlledRecreateCount);
        Assert.Equal(0, h.Coordinator.PendingControlledRecreateRequestId);
    }

    [Fact]
    public void 判别位_取消别的请求不得误清当前请求()
    {
        Harness h = new();
        h.Coordinator.RequestControlledRecreate("gpu_to_legacy");

        Assert.False(h.Coordinator.CancelControlledRecreate(9999));
        Assert.Equal(1, h.Coordinator.PendingControlledRecreateCount);
    }

    [Fact]
    public void 判别位_陈旧兜底_再次请求前先归零并记告警且不清空呈现根因()
    {
        Harness h = new();
        h.Coordinator.RequestControlledRecreate("first");
        // 先制造一个呈现根因，验证告警**不得**覆盖它（合同 §5.3 规则 2）。
        h.Coordinator.FailHandleRecreate();
        Assert.Equal("gpu_presentation_handle_recreate_failed", h.Coordinator.State.FailureCode);

        long secondId = h.Coordinator.RequestControlledRecreate("second");

        // 规则 ③：残留计数先归零，再置位新请求。
        Assert.Equal(1, h.Coordinator.PendingControlledRecreateCount);
        Assert.Equal(secondId, h.Coordinator.PendingControlledRecreateRequestId);
        Assert.Equal(
            "gpu_presentation_handle_recreate_stale_flag",
            h.Coordinator.State.CleanupWarningCode);
        // 呈现根因**不得**被清理告警清空。
        Assert.Equal("gpu_presentation_handle_recreate_failed", h.Coordinator.State.FailureCode);
    }

    [Fact]
    public void 判别位_超时兜底_五秒内不归零超时后归零并记告警()
    {
        Harness h = new();
        h.Coordinator.RequestControlledRecreate("gpu_to_legacy");

        // 规则 ④：5 秒**未到** ⇒ 不归零。
        h.Clock.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.False(h.Coordinator.EnforceControlledRecreateTimeout());
        Assert.Equal(1, h.Coordinator.PendingControlledRecreateCount);

        // 5 秒**到** ⇒ 归零并留痕。
        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(h.Coordinator.EnforceControlledRecreateTimeout());
        Assert.Equal(0, h.Coordinator.PendingControlledRecreateCount);
        Assert.Equal(
            "gpu_presentation_handle_recreate_not_observed",
            h.Coordinator.State.CleanupWarningCode);
    }

    [Fact]
    public void 判别位_关闭过程中不得置位()
    {
        Harness h = new();
        h.Coordinator.NotifyFormClosing();

        long requestId = h.Coordinator.RequestControlledRecreate("gpu_to_legacy");

        Assert.Equal(0, requestId);
        Assert.Equal(0, h.Coordinator.PendingControlledRecreateCount);
    }

    // ================================================================
    // §7.2 句柄代次唯一真相源
    // ================================================================

    [Fact]
    public void 代次_协调器只读消费注入的真相源()
    {
        Harness h = new();

        Assert.Equal(1, h.Coordinator.HandleGeneration);

        long advanced = h.Generation.Advance();

        Assert.Equal(advanced, h.Coordinator.HandleGeneration);
    }

    [Fact]
    public void 代次_受控重建后创建推进阶段到恢复中()
    {
        Harness h = new();
        h.Coordinator.TryBeginTransition(
            CreateGpuRouteRequest(),
            RenderingTransitionTrigger.UserThemeChange,
            out _);

        h.Coordinator.OnHandleCreated(controlled: true, newHandleGeneration: 42);

        Assert.Equal(42, h.Coordinator.State.HandleGeneration);
        Assert.Equal(RenderingTransitionPhase.Restoring, h.Coordinator.Phase);
    }

    [Fact]
    public void 代次_构造期首次创建不得进入GPU重建路径()
    {
        Harness h = new();
        h.Coordinator.TryBeginTransition(
            CreateGpuRouteRequest(),
            RenderingTransitionTrigger.UserThemeChange,
            out _);

        // controlled == false ⇒ 构造期首次创建 / 非受控创建（合同 §8.2 规则 3）。
        h.Coordinator.OnHandleCreated(controlled: false, newHandleGeneration: 42);

        Assert.Equal(RenderingTransitionPhase.Stopping, h.Coordinator.Phase);
    }

    // ================================================================
    // §8.4 Tick 启动守卫
    // ================================================================

    [Fact]
    public void 守卫_稳定态且期望路由为GPU时允许拉起()
    {
        Harness h = new();
        h.Coordinator.TryBeginTransition(
            CreateGpuRouteRequest(),
            RenderingTransitionTrigger.UserThemeChange,
            out _);
        h.Coordinator.PublishStable(RenderingRoute.GpuLiveBackground);

        Assert.True(h.Coordinator.CanStartGpuPresentation);
    }

    [Fact]
    public void 守卫_过渡阶段禁止拉起GPU()
    {
        Harness h = new();
        h.Coordinator.TryBeginTransition(
            CreateGpuRouteRequest(),
            RenderingTransitionTrigger.UserThemeChange,
            out _);

        Assert.Equal(RenderingTransitionPhase.Stopping, h.Coordinator.Phase);
        Assert.False(h.Coordinator.CanStartGpuPresentation);
    }

    [Fact]
    public void 守卫_期望路由为legacy时禁止拉起GPU()
    {
        Harness h = new();
        h.Coordinator.TryBeginTransition(
            CreateLegacyRouteRequest(),
            RenderingTransitionTrigger.UserThemeChange,
            out _);
        h.Coordinator.PublishStable(RenderingRoute.Legacy);

        Assert.False(h.Coordinator.CanStartGpuPresentation);
    }

    [Fact]
    public void 守卫_恢复预算耗尽后普通Tick不得再自动拉起GPU()
    {
        Harness h = new();
        h.Coordinator.TryBeginTransition(
            CreateGpuRouteRequest(),
            RenderingTransitionTrigger.UserThemeChange,
            out _);
        h.Coordinator.PublishStable(RenderingRoute.GpuLiveBackground);
        Assert.True(h.Coordinator.CanStartGpuPresentation);

        h.Coordinator.MarkGpuRecoveryExhausted();

        Assert.False(h.Coordinator.CanStartGpuPresentation);
        Assert.Equal("gpu_presentation_recovery_exhausted", h.Coordinator.State.FailureCode);
    }

    // ================================================================
    // §4 GPU → legacy 的 9 步
    // ================================================================

    [Fact]
    public void 步1_进入保护并推进请求版本与切换号()
    {
        Harness h = new();

        bool entered = h.Coordinator.TryBeginTransition(
            CreateGpuRouteRequest(),
            RenderingTransitionTrigger.UserThemeChange,
            out RenderingRouteDecision? decision);

        Assert.True(entered);
        Assert.NotNull(decision);
        Assert.Equal(RenderingRoute.GpuLiveBackground, h.Coordinator.ExpectedRoute);
        Assert.Equal(RenderingTransitionPhase.Stopping, h.Coordinator.Phase);
        Assert.Equal(1, h.Coordinator.State.TransitionId);
        Assert.Equal(1, h.Coordinator.State.LastAppliedRequestVersion);
    }

    [Fact]
    public void 步1_判定失败保持原状态且不进入停止阶段()
    {
        Harness h = new();
        long transitionIdBefore = h.Coordinator.State.TransitionId;

        // Decide(null) 抛 ArgumentNullException ⇒ 合同 §4 步 1 的失败语义。
        bool entered = h.Coordinator.TryBeginTransition(
            null!,
            RenderingTransitionTrigger.UserThemeChange,
            out RenderingRouteDecision? decision);

        Assert.False(entered);
        Assert.Null(decision);
        Assert.Equal("gpu_transition_decide_failed", h.Coordinator.State.FailureCode);
        Assert.Equal(RenderingTransitionPhase.Failed, h.Coordinator.Phase);
        Assert.Equal(transitionIdBefore, h.Coordinator.State.TransitionId);
    }

    [Fact]
    public void 步2_快照失败记码且不重建()
    {
        Harness h = new();

        bool captured = h.Coordinator.TryCaptureWindowState(null);

        Assert.False(captured);
        Assert.Equal(
            "gpu_transition_window_state_snapshot_failed",
            h.Coordinator.State.FailureCode);
        Assert.Equal(RenderingTransitionPhase.Failed, h.Coordinator.Phase);
    }

    [Fact]
    public void 步3_清理诊断不得覆盖呈现根因()
    {
        Harness h = new();
        h.Coordinator.FailHandleRecreate();

        h.Coordinator.RecordCleanupState(
            CapturePolicyCleanupState.Pending,
            "gpu_capture_policy_restore_failed");

        Assert.Equal(CapturePolicyCleanupState.Pending, h.Coordinator.CleanupState);
        Assert.Equal("gpu_capture_policy_restore_failed", h.Coordinator.State.CleanupErrorCode);
        // 合同 §5.3 规则 2：写清理诊断**不得**清空呈现根因。
        Assert.Equal("gpu_presentation_handle_recreate_failed", h.Coordinator.State.FailureCode);
    }

    [Fact]
    public void 步4_进入重建阶段且失败时保留失败码()
    {
        Harness h = new();

        h.Coordinator.EnterRecreatingHandle();
        Assert.Equal(RenderingTransitionPhase.RecreatingHandle, h.Coordinator.Phase);

        h.Coordinator.FailHandleRecreate();
        Assert.Equal(RenderingTransitionPhase.Failed, h.Coordinator.Phase);
        Assert.Equal(
            "gpu_presentation_handle_recreate_failed",
            h.Coordinator.State.FailureCode);
    }

    [Fact]
    public void 步6_布局恢复失败记码()
    {
        Harness h = new();

        h.Coordinator.RecordLayoutRestore(false);

        Assert.Equal("gpu_transition_layout_restore_failed", h.Coordinator.State.FailureCode);
        Assert.Equal(RenderingTransitionPhase.Failed, h.Coordinator.Phase);
    }

    [Fact]
    public void 步7_窗口状态恢复失败记码()
    {
        Harness h = new();

        h.Coordinator.RecordWindowStateRestore(false);

        Assert.Equal("gpu_transition_window_state_restore_failed", h.Coordinator.State.FailureCode);
    }

    [Fact]
    public void 步8_隐藏窗口记待首帧且不报成功()
    {
        Harness h = new();

        h.Coordinator.RecordFirstFrame(windowVisible: false, legacyFrameObserved: false);

        Assert.True(h.Coordinator.State.PendingFirstFrame);
        Assert.Equal(FirstFrameOutcome.NotAttempted, h.Coordinator.State.FirstFrameOutcome);
    }

    [Fact]
    public void 步8_可见窗口拿到首帧证据才落成功()
    {
        Harness h = new();

        h.Coordinator.RecordFirstFrame(windowVisible: true, legacyFrameObserved: true);

        Assert.False(h.Coordinator.State.PendingFirstFrame);
        Assert.Equal(FirstFrameOutcome.Succeeded, h.Coordinator.State.FirstFrameOutcome);
    }

    [Fact]
    public void 步8_首帧超时记码()
    {
        Harness h = new();

        h.Coordinator.FailLegacyFirstFrame();

        Assert.Equal("gpu_transition_legacy_first_frame_timeout", h.Coordinator.State.FailureCode);
    }

    [Fact]
    public void 步9_发布稳定状态且有效路由为legacy()
    {
        Harness h = new();

        h.Coordinator.PublishStable(RenderingRoute.Legacy);

        Assert.Equal(RenderingTransitionPhase.Stable, h.Coordinator.Phase);
        Assert.Equal(RenderingRoute.Legacy, h.Coordinator.EffectiveRoute);
    }

    // ================================================================
    // U04 幂等短路 / U05 三重丢弃 / U10 关闭优先
    // ================================================================

    [Fact]
    public void U04_同一请求重复到达不再重建且状态不变()
    {
        Harness h = new();
        RenderingRouteRequest request = CreateLegacyRouteRequest();

        Assert.True(h.Coordinator.TryBeginTransition(
            request, RenderingTransitionTrigger.UserThemeChange, out _));
        h.Coordinator.PublishStable(RenderingRoute.Legacy);
        long transitionId = h.Coordinator.State.TransitionId;
        long requestVersion = h.Coordinator.State.LastAppliedRequestVersion;

        // 同一请求再次到达 ⇒ 幂等短路：不进入切换、不推进切换号与请求版本。
        Assert.False(h.Coordinator.TryBeginTransition(
            request, RenderingTransitionTrigger.UserThemeChange, out _));

        Assert.Equal(transitionId, h.Coordinator.State.TransitionId);
        Assert.Equal(requestVersion, h.Coordinator.State.LastAppliedRequestVersion);
        Assert.Equal(RenderingTransitionPhase.Stable, h.Coordinator.Phase);
    }

    [Fact]
    public void U05_三重丢弃判据缺一不可()
    {
        Harness h = new();
        h.Coordinator.TryBeginTransition(
            CreateGpuRouteRequest(),
            RenderingTransitionTrigger.UserThemeChange,
            out _);
        h.Coordinator.OnHandleCreated(controlled: true, newHandleGeneration: 7);
        long hostGeneration = h.Coordinator.NextHostGeneration();

        long transitionId = h.Coordinator.State.TransitionId;
        long handleGeneration = h.Coordinator.State.HandleGeneration;

        Assert.False(h.Coordinator.ShouldDiscardCallback(
            transitionId, handleGeneration, hostGeneration));

        Assert.True(h.Coordinator.ShouldDiscardCallback(
            transitionId - 1, handleGeneration, hostGeneration));
        Assert.True(h.Coordinator.ShouldDiscardCallback(
            transitionId, handleGeneration - 1, hostGeneration));
        Assert.True(h.Coordinator.ShouldDiscardCallback(
            transitionId, handleGeneration, hostGeneration - 1));
    }

    [Fact]
    public void U10_关闭过程中不再启动新的切换()
    {
        Harness h = new();
        h.Coordinator.NotifyFormClosing();

        bool entered = h.Coordinator.TryBeginTransition(
            CreateGpuRouteRequest(),
            RenderingTransitionTrigger.UserThemeChange,
            out _);

        Assert.False(entered);
        Assert.False(h.Coordinator.AllowsWindowOperations);
    }

    // ================================================================
    // §6.2 / §6.4 / §7.4 清理责任 ledger
    // ================================================================

    private sealed class RestoreProbe
    {
        private int _calls;

        internal int Calls => _calls;
        internal List<IntPtr> Handles { get; } = new();
        internal bool Succeed { get; set; }

        internal void RecordCall(IntPtr handle)
        {
            _calls++;
            Handles.Add(handle);
        }
    }

    private static CapturePolicyCleanupLedger CreateLedger(
        long currentGeneration,
        RestoreProbe probe,
        Func<IntPtr, bool>? isWindowAlive = null)
    {
        return new CapturePolicyCleanupLedger(
            () => currentGeneration,
            isWindowAlive ?? (_ => true),
            cleanup =>
            {
                probe.RecordCall(cleanup.WindowHandle);
                return probe.Succeed
                    ? new CapturePolicyRestoreResult(true, null, null)
                    : new CapturePolicyRestoreResult(
                        false,
                        CapturePolicyCleanupLedger.RestoreFailedErrorCode,
                        "boom");
            });
    }

    [Fact]
    public void INV4_同一代次至多一条待处理清理责任()
    {
        RestoreProbe probe = new();
        CapturePolicyCleanupLedger ledger = CreateLedger(3, probe);

        Assert.True(ledger.Add(new WindowCapturePolicyCleanup { Generation = 3 }));
        Assert.False(ledger.Add(new WindowCapturePolicyCleanup { Generation = 3 }));
        Assert.Single(ledger.Entries);
    }

    [Fact]
    public void INV6_同代次存在未完成清理时拒绝叠加新的所有权()
    {
        RestoreProbe probe = new();
        CapturePolicyCleanupLedger ledger = CreateLedger(3, probe);

        Assert.False(ledger.HasPendingForGeneration(3));
        ledger.Add(new WindowCapturePolicyCleanup { Generation = 3 });
        Assert.True(ledger.HasPendingForGeneration(3));
        Assert.False(ledger.HasPendingForGeneration(4));
    }

    [Fact]
    public void 判据优先于窗口存活探针_旧代次不得触碰任何窗口调用()
    {
        RestoreProbe probe = new();
        bool aliveProbeCalled = false;
        CapturePolicyCleanupLedger ledger = CreateLedger(
            5,
            probe,
            _ =>
            {
                aliveProbeCalled = true;
                return true;
            });

        ledger.Add(new WindowCapturePolicyCleanup
        {
            Generation = 3,
            WindowHandle = new IntPtr(0x1234),
            AffinityChangedBySession = true
        });

        ledger.Pump(T0);

        // 合同 §7.4 第 1 条：代次落后 ⇒ 禁止任何 Win32 调用（含 IsWindow）⇒ 直接终结。
        Assert.False(aliveProbeCalled);
        Assert.Equal(0, probe.Calls);
        WindowCapturePolicyCleanup entry = ledger.Entries[0];
        Assert.Equal(CapturePolicyCleanupState.Completed, entry.State);
        Assert.Equal("superseded_by_new_generation", entry.TerminalReason);
        Assert.Equal("gpu_capture_policy_cleanup_superseded", ledger.CleanupWarningCode);
    }

    [Fact]
    public void 同代次但窗口已销毁则同步终结且不发起恢复()
    {
        RestoreProbe probe = new();
        CapturePolicyCleanupLedger ledger = CreateLedger(5, probe, _ => false);

        ledger.Add(new WindowCapturePolicyCleanup
        {
            Generation = 5,
            WindowHandle = new IntPtr(0x1234),
            AffinityChangedBySession = true
        });

        ledger.Pump(T0);

        Assert.Equal(0, probe.Calls);
        WindowCapturePolicyCleanup entry = ledger.Entries[0];
        Assert.Equal(CapturePolicyCleanupState.Completed, entry.State);
        Assert.Equal("window_destroyed", entry.TerminalReason);
    }

    [Fact]
    public void 退避重试_未到点不重试到点才重试且六次后终结()
    {
        RestoreProbe probe = new() { Succeed = false };
        CapturePolicyCleanupLedger ledger = CreateLedger(5, probe);

        ledger.Add(new WindowCapturePolicyCleanup
        {
            Generation = 5,
            WindowHandle = new IntPtr(0x1234),
            AffinityChangedBySession = true
        });

        DateTimeOffset now = T0;

        // 到期 ⇒ 尝试一次。★ 合同 §6.4：尝试 1、2 属于宿主停止流程，
        //   ledger 的退避重试是尝试 3–6 ⇒ 本次 ledger 尝试后 Attempts = 3。
        Assert.Equal(1, ledger.Pump(now));
        Assert.Equal(3, ledger.Entries[0].Attempts);

        // 退避 500 ms 未到 ⇒ **不重试**（合同 §6.4"禁止每帧重试"）。
        Assert.Equal(0, ledger.Pump(now.AddMilliseconds(499)));
        Assert.Equal(3, ledger.Entries[0].Attempts);

        // 退避到点 ⇒ 再试一次（尝试 4）。
        Assert.Equal(1, ledger.Pump(now.AddMilliseconds(500)));
        Assert.Equal(4, ledger.Entries[0].Attempts);

        // 继续推进到预算耗尽（总上限 6 次）。
        DateTimeOffset cursor = now.AddMilliseconds(500);
        for (int i = 0; i < 10 && !ledger.Entries[0].IsTerminal; i++)
        {
            cursor = cursor.AddSeconds(5);
            ledger.Pump(cursor);
        }

        Assert.Equal(6, ledger.Entries[0].Attempts);
        Assert.Equal(CapturePolicyCleanupState.Failed, ledger.Entries[0].State);
        Assert.Equal("exhausted", ledger.Entries[0].TerminalReason);
        Assert.Equal(
            "gpu_capture_policy_restore_retry_exhausted",
            ledger.CleanupErrorCode);
    }

    [Fact]
    public void INV5_关闭时终结清理责任且幂等()
    {
        RestoreProbe probe = new() { Succeed = false };
        CapturePolicyCleanupLedger ledger = CreateLedger(5, probe);

        ledger.Add(new WindowCapturePolicyCleanup
        {
            Generation = 5,
            WindowHandle = new IntPtr(0x1234),
            AffinityChangedBySession = true
        });

        ledger.FinalizeOnFormClosed(T0);

        WindowCapturePolicyCleanup entry = ledger.Entries[0];
        Assert.Equal(CapturePolicyCleanupState.Failed, entry.State);
        Assert.Equal("form_closed", entry.TerminalReason);

        // 幂等：重复 Dispose 安全（重复终结不得改变任何状态）。
        ledger.FinalizeOnFormClosed(T0.AddSeconds(1));
        Assert.Equal(CapturePolicyCleanupState.Failed, entry.State);
        Assert.Equal("form_closed", entry.TerminalReason);
        Assert.Equal(0, entry.Attempts);
    }

    // ================================================================
    // 结构护栏（IL 断言）：把"接线"本身钉死
    // ================================================================

    [Fact]
    public void 护栏_句柄代次推进必须早于两侧消费()
    {
        MethodBase handler = FindMethodCalling(typeof(WindowHandleGeneration), "Advance");
        Assert.NotNull(handler);

        List<IlInspector.IlInstruction> instructions = IlInspector.Disassemble(handler).Instructions;
        Assert.NotEmpty(instructions);

        int advanceOffset = FindCallOffset(instructions, "Advance", typeof(WindowHandleGeneration));
        int liveOffset = FindCallOffset(instructions, "NotifyLiveGlassHandleChanged", typeof(UIController));
        int gpuOffset = FindCallOffset(instructions, "NotifyGpuHandleChanged", typeof(UIController));

        Assert.True(advanceOffset >= 0, "HandleCreated 处理器必须调用句柄代次推进");
        Assert.True(liveOffset >= 0, "HandleCreated 处理器必须通知 live 捕获链");
        Assert.True(gpuOffset >= 0, "HandleCreated 处理器必须通知 GPU 链");
        Assert.True(
            advanceOffset < liveOffset,
            $"句柄代次推进（IL 偏移 {advanceOffset}）必须早于 live 捕获链通知（{liveOffset}）");
        Assert.True(
            advanceOffset < gpuOffset,
            $"句柄代次推进（IL 偏移 {advanceOffset}）必须早于 GPU 链通知（{gpuOffset}）");
    }

    [Fact]
    public void 护栏_Tick兜底必须先过启动守卫再拉起GPU()
    {
        // ★ 精确定位到 **Tick 自己的** 状态机（async 方法体编译进 <Tick>d__NN.MoveNext）。
        //   不能用"同时调用了守卫与 StartGpuPresentation 的任意方法"来定位 ——
        //   NotifyGpuHandleChanged 也同时调用这两者，会把断言挪到错误的方法上。
        MethodBase tickBody = FindAsyncStateMachineMoveNext(typeof(UIController), "Tick");
        Assert.NotNull(tickBody);

        List<IlInspector.IlInstruction> instructions = IlInspector.Disassemble(tickBody).Instructions;
        int guardOffset = FindCallOffset(
            instructions, "get_CanStartGpuPresentation", typeof(RenderingTransitionCoordinator));
        int startOffset = FindCallOffset(instructions, "StartGpuPresentation", typeof(UIController));

        Assert.True(guardOffset >= 0,
            "Tick 兜底必须读启动守卫（合同 §8.4：Phase == Stable 且 ExpectedRoute.IsGpu）");
        Assert.True(startOffset >= 0, "Tick 兜底必须调用启动路径");
        Assert.True(
            guardOffset < startOffset,
            $"启动守卫（IL 偏移 {guardOffset}）必须早于 GPU 启动（{startOffset}）");
    }

    [Fact]
    public void 护栏_受控重建必须直接调用受保护方法且不得用反射()
    {
        MethodInfo entry = typeof(MainForm).GetMethod(
            "TryRecreateHandleForTransition",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(entry);

        List<IlInspector.IlInstruction> instructions = IlInspector.Disassemble(entry).Instructions;
        Assert.NotEmpty(instructions);

        bool callsRecreateHandle = false;
        bool usesReflection = false;
        foreach (IlInspector.IlInstruction instruction in instructions)
        {
            MethodBase target = instruction.Target;
            if (target == null) continue;
            if (target.DeclaringType == typeof(System.Windows.Forms.Control) &&
                target.Name == "RecreateHandle")
            {
                callsRecreateHandle = true;
            }

            if (target.Name is "GetMethod" or "Invoke" or "GetMethodInfo")
            {
                usesReflection = true;
            }
        }

        Assert.True(callsRecreateHandle,
            "受控重建入口必须**直接调用** Control.RecreateHandle（合同 §4 步 4 禁止反射）");
        Assert.False(usesReflection, "受控重建入口不得使用反射调用受保护方法");
    }

    [Fact]
    public void 护栏_生产代码不得自行创建第二个句柄代次实例()
    {
        // 【主审裁决 D-G】唯一实例由类型自身持有 ⇒ 生产代码里除
        // WindowHandleGeneration 自身的静态构造外，**不得**再出现 new WindowHandleGeneration()。
        Assembly production = typeof(UIController).Assembly;
        List<string> offenders = new List<string>();

        foreach (Type type in EnumerateTypes(production))
        {
            foreach (MethodBase method in EnumerateMethods(type))
            {
                foreach (IlInspector.IlInstruction instruction in IlInspector.Disassemble(method).Instructions)
                {
                    if (instruction.OpCode != OpCodes.Newobj) continue;
                    var ctor = instruction.Target as ConstructorInfo;
                    if (ctor == null) continue;
                    if (ctor.DeclaringType != typeof(WindowHandleGeneration)) continue;
                    if (type == typeof(WindowHandleGeneration)) continue;   // 唯一合法的那个
                    offenders.Add($"{type.FullName}.{method.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "生产代码不得 new WindowHandleGeneration()（主审裁决 D-G）；违例：" +
            string.Join(", ", offenders));
    }

    [Fact]
    public void 护栏_仓库不得再存在SharedWindowHandleGeneration这个类型名()
    {
        Assembly production = typeof(UIController).Assembly;

        List<string> hits = EnumerateTypes(production)
            .Where(t => string.Equals(t.Name, "SharedWindowHandleGeneration", StringComparison.Ordinal))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        Assert.True(hits.Count == 0,
            "主审裁决 D-G：句柄代次唯一实例由类型自身持有，不得再有第二份 holder；违例：" +
            string.Join(", ", hits));
    }

    // ================================================================
    // 缺口 1（清理责任移交出口）的结构护栏
    // ================================================================

    [Fact]
    public void 护栏_宿主创建点必须订阅清理责任移交出口并把记录收进ledger()
    {
        // ★ 合同 §6.2：宿主在停止时把未完成的 affinity 清理责任**移交**给 ledger。
        //   没有订阅 ⇒ ledger 永远不持有责任 ⇒ 旧 HWND 永远保持 WDA 排除（比 GPU-001 更糟）。
        MethodBase attach = FindMethodCalling(
            typeof(GpuPresentationHostController), "add_CapturePolicyCleanupRequired");
        Assert.NotNull(attach);

        List<IlInspector.IlInstruction> instructions = IlInspector.Disassemble(attach).Instructions;
        int subscribeOffset = FindCallOffset(
            instructions, "add_CapturePolicyCleanupRequired", typeof(GpuPresentationHostController));
        int probeOffset = FindCallOffset(
            instructions, "set_CapturePolicyCleanupPendingProbe", typeof(GpuPresentationHostController));

        Assert.True(subscribeOffset >= 0, "必须订阅 CapturePolicyCleanupRequired");
        Assert.True(probeOffset >= 0,
            "必须注入 INV-6 探针（CapturePolicyCleanupPendingProbe）");

        // ★ 订阅的**处理函数**必须把清理记录收进 ledger（否则订阅形同虚设）。
        MethodBase? handler = FindDelegateTarget(instructions, typeof(UIController));
        Assert.NotNull(handler);

        List<IlInspector.IlInstruction> handlerInstructions =
            IlInspector.Disassemble(handler).Instructions;
        int addOffset = FindCallOffset(
            handlerInstructions, "Add", typeof(CapturePolicyCleanupLedger));
        Assert.True(addOffset >= 0,
            "移交处理函数必须调用 CapturePolicyCleanupLedger.Add（否则清理责任不会进 ledger）");
    }

    [Fact]
    public void 护栏_停止宿主必须无条件释放宿主引用()
    {
        // ★ 合同 §6.2 INV-2：停止之后 `_gpuPresentation` 无条件为 null。
        //   判据：方法里**不得有任何条件分支**跳过最后一次置 null
        //   （"保留引用"分支会让清理责任重新由宿主引用表达 ⇒ GPU-001 的根因复活）。
        //   ★ 只排除无条件 br（"宿主本就为 null ⇒ 早退"那条路径，字段本来就是 null）。
        MethodInfo stop = typeof(UIController).GetMethod(
            "StopGpuPresentation", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(stop);

        List<IlInspector.IlInstruction> instructions = IlInspector.Disassemble(stop).Instructions;
        List<int> stores = new();
        foreach (IlInspector.IlInstruction instruction in instructions)
        {
            if (instruction.OpCode != OpCodes.Stfld) continue;
            if (instruction.Field == null) continue;
            if (instruction.Field.Name != "_gpuPresentation") continue;
            stores.Add(instruction.Offset);
        }

        Assert.True(stores.Count > 0, "StopGpuPresentation 必须把 _gpuPresentation 置 null");

        int lastStore = stores[stores.Count - 1];
        byte[] il = stop.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
        foreach (IlInspector.IlInstruction instruction in instructions)
        {
            // ★ 只看**条件分支**；leave / endfinally 是异常处理表的边，不是"跳过路径"；
            //   无条件 br 只是控制流转移（"宿主本就为 null ⇒ 早退"那条路径），
            //   不是"条件性地跳过置 null"。
            //   ★ 必须同时覆盖短格式（.s）变体：C# 编译器对近距离跳转发射的是
            //     brtrue.s / brfalse.s —— 只认长格式会让本护栏漏判
            //     （第一轮变异证明 M9「恢复保留引用分支」正是这样漏过去的）。
            if (!IsConditionalBranch(instruction.OpCode))
            {
                continue;
            }

            int? target = ReadBranchTarget(stop, instruction, il);
            if (target == null) continue;

            Assert.True(
                target <= lastStore,
                $"StopGpuPresentation 里有一条**条件**分支跳到 IL 偏移 {target}，" +
                $"越过了最后一次置 null（偏移 {lastStore}）⇒ \"保留引用\"分支复活，" +
                "清理责任会重新由宿主引用表达（合同 §6.2 INV-2）");
        }
    }

    // ================================================================
    // 缺口 3（legacy 回退样式派生）
    // ================================================================

    [Fact]
    public void 回退样式_派生副本生效且源主题与全局主题都不被修改()
    {
        Theme source = new()
        {
            Name = "T03FallbackProbe",
            Color = new ColorConfig
            {
                Background = "#111111",
                GroupBackground = "#222222",
                Border = "#333333",
                TextPrimary = "#444444",
                TextSecondary = "#555555",
                ValueSafe = "#00FF00",
            }
        };

        string originalBackground = source.Color.Background;
        string originalGroup = source.Color.GroupBackground;
        string originalBorder = source.Color.Border;
        string originalText = source.Color.TextPrimary;
        string originalMuted = source.Color.TextSecondary;
        string originalValueSafe = source.Color.ValueSafe;

        LegacyFallbackStyle style = new(
            Background: "#AABBCC",
            PanelBackground: "#DDEEFF",
            Border: "#112233",
            Text: "#FFEEDD",
            MutedText: "#AABBCC",
            Accent: "#8FB4D9",
            Diagnostics: Array.Empty<string>());

        Theme derived = LegacyFallbackStyleBridge.BuildDerivedTheme(source, style);

        // 派生副本承载回退样式。
        Assert.Equal("#AABBCC", derived.Color.Background);
        Assert.Equal("#DDEEFF", derived.Color.GroupBackground);
        Assert.Equal("#112233", derived.Color.Border);
        Assert.Equal("#FFEEDD", derived.Color.TextPrimary);
        Assert.Equal("#AABBCC", derived.Color.TextSecondary);
        // ★ 告警语义色**不得**被 accent 覆盖（合同 §4.6"保留告警语义和进度值"）。
        Assert.Equal("#00FF00", derived.Color.ValueSafe);

        // ★ 源主题**不被修改**（合同 §4.6"不改主题"）。
        Assert.Equal(originalBackground, source.Color.Background);
        Assert.Equal(originalGroup, source.Color.GroupBackground);
        Assert.Equal(originalBorder, source.Color.Border);
        Assert.Equal(originalText, source.Color.TextPrimary);
        Assert.Equal(originalMuted, source.Color.TextSecondary);
        Assert.Equal(originalValueSafe, source.Color.ValueSafe);
    }

    [Fact]
    public void 回退样式_accent按裁决DH落到标题色且不侵占告警色与进度条色()
    {
        // ★ 主审裁决 D-H（计划 §4.5.11）：`LegacyFallbackStyle.Accent` 在 `ColorConfig` 里
        //   没有同名列 ⇒ 裁决落到 `TextTitle`（标题 / 强调前景色）。
        // ★ **不得**占用告警语义色（ValueSafe/ValueWarn/ValueCrit）与进度条分段色
        //   （BarLow/BarMid/BarHigh）—— 合同 §4.6 明文"保留告警语义和进度值"。
        // ★ 变体（把 `colors.TextTitle = style.Accent;` 删掉）必须让本用例变红。
        Theme source = new()
        {
            Name = "T03AccentProbe",
            Color = new ColorConfig
            {
                Background = "#111111",
                GroupBackground = "#222222",
                Border = "#333333",
                TextPrimary = "#444444",
                TextSecondary = "#555555",
                TextTitle = "#666666",
                TextGroup = "#777777",
                ValueSafe = "#00FF00",
                ValueWarn = "#FFAA00",
                ValueCrit = "#FF0000",
                BarLow = "#001122",
                BarMid = "#334455",
                BarHigh = "#667788",
            }
        };

        LegacyFallbackStyle style = new(
            Background: "#AABBCC",
            PanelBackground: "#DDEEFF",
            Border: "#112233",
            Text: "#FFEEDD",
            MutedText: "#AABBCC",
            Accent: "#8FB4D9",
            Diagnostics: Array.Empty<string>());

        Theme derived = LegacyFallbackStyleBridge.BuildDerivedTheme(source, style);

        // ★ 裁决落点：accent ⇒ 标题前景色。
        Assert.Equal("#8FB4D9", derived.Color.TextTitle);

        // ★ 告警语义色**不得**被 accent 覆盖。
        Assert.Equal("#00FF00", derived.Color.ValueSafe);
        Assert.Equal("#FFAA00", derived.Color.ValueWarn);
        Assert.Equal("#FF0000", derived.Color.ValueCrit);

        // ★ 进度条分段色**不得**被 accent 覆盖。
        Assert.Equal("#001122", derived.Color.BarLow);
        Assert.Equal("#334455", derived.Color.BarMid);
        Assert.Equal("#667788", derived.Color.BarHigh);

        // ★ 分组标题色不在裁决范围内 ⇒ 保持源值（不得与 MutedText 塌陷成同一层）。
        Assert.Equal("#777777", derived.Color.TextGroup);

        // ★ 源主题**不被修改**。
        Assert.Equal("#666666", source.Color.TextTitle);
        Assert.Equal("#00FF00", source.Color.ValueSafe);
        Assert.Equal("#001122", source.Color.BarLow);
    }

    [Fact]
    public void 回退样式_解析器缺省与非法值都给出可诊断结果且不抛异常()
    {
        LegacyFallbackStyle defaults = LegacyFallbackStyleBridge.Resolve(null, out IReadOnlyList<string> none);
        Assert.Empty(none);
        Assert.False(string.IsNullOrWhiteSpace(defaults.Background));
        Assert.False(string.IsNullOrWhiteSpace(defaults.Text));

        ThemeRenderingConfig configuration = new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.LiquidGlassMaterial,
        };
        LegacyFallbackStyle resolved = LegacyFallbackStyleBridge.Resolve(
            configuration, out IReadOnlyList<string> diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(defaults.Background, resolved.Background);
    }

    [Fact]
    public void 护栏_过渡流程必须经由派生样式桥生成legacy外观()
    {
        MethodBase caller = FindMethodCalling(
            typeof(LegacyFallbackStyleBridge), "BuildDerivedTheme");
        Assert.NotNull(caller);

        // 桥必须被**过渡流程**调用（而不是别处）。
        Assert.Equal(typeof(UIController), caller.DeclaringType);
    }

    // ================================================================
    // IL 辅助
    // ================================================================

    private static IEnumerable<Type> EnumerateTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }

    private static IEnumerable<MethodBase> EnumerateMethods(Type type)
    {
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (MethodInfo method in type.GetMethods(flags))
        {
            yield return method;
        }

        ConstructorInfo? cctor = type.TypeInitializer;
        if (cctor != null) yield return cctor;
    }

    private static MethodBase? FindMethodCalling(Type declaringType, string methodName)
    {
        foreach (Type type in EnumerateTypes(typeof(UIController).Assembly))
        {
            foreach (MethodBase method in EnumerateMethods(type))
            {
                foreach (IlInspector.IlInstruction instruction in IlInspector.Disassemble(method).Instructions)
                {
                    MethodBase? target = instruction.Target;
                    if (target == null) continue;
                    if (target.DeclaringType != declaringType) continue;
                    if (!string.Equals(target.Name, methodName, StringComparison.Ordinal)) continue;
                    return method;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 判断一条指令是否是"**条件**分支"（可跳过一段代码的那种）。
    /// <para>★ 必须**同时**覆盖长格式与短格式（<c>.s</c>）变体：
    /// C# 编译器对近距离跳转发射的是短格式（如 <c>brtrue.s</c>），
    /// 只认长格式会让"是否存在跳过置 null 的条件分支"这一判据失效
    /// （第一轮变异证明 M9 就是这样漏过去的）。</para>
    /// <para>★ **不含**无条件 <c>br</c> / <c>br.s</c>：它们不是"条件路径"，
    /// 只是控制流转移。本方法里唯一出现的无条件 <c>br.s</c> 是
    /// "宿主本就为 <c>null</c> ⇒ 直接返回"的早退路径
    /// （<c>IL_0010: br.s IL_0068</c>）—— 那条路径上字段本来就是 <c>null</c>，
    /// 跳过置 null 是**良性**的。</para>
    /// <para>★ 不含 <c>leave</c> / <c>leave.s</c> / <c>endfinally</c>：
    /// 它们是异常处理表的边，不是"跳过路径"。</para>
    /// </summary>
    private static bool IsConditionalBranch(OpCode opCode) =>
        opCode == OpCodes.Brfalse || opCode == OpCodes.Brfalse_S ||
        opCode == OpCodes.Brtrue || opCode == OpCodes.Brtrue_S ||
        opCode == OpCodes.Beq || opCode == OpCodes.Beq_S ||
        opCode == OpCodes.Bge || opCode == OpCodes.Bge_S ||
        opCode == OpCodes.Bgt || opCode == OpCodes.Bgt_S ||
        opCode == OpCodes.Ble || opCode == OpCodes.Ble_S ||
        opCode == OpCodes.Blt || opCode == OpCodes.Blt_S ||
        opCode == OpCodes.Bne_Un || opCode == OpCodes.Bne_Un_S ||
        opCode == OpCodes.Bge_Un || opCode == OpCodes.Bge_Un_S ||
        opCode == OpCodes.Bgt_Un || opCode == OpCodes.Bgt_Un_S ||
        opCode == OpCodes.Ble_Un || opCode == OpCodes.Ble_Un_S ||
        opCode == OpCodes.Blt_Un || opCode == OpCodes.Blt_Un_S;

    /// <summary>
    /// 读一条分支指令的目标 IL 偏移；不是分支或读不出时返回 <c>null</c>。
    /// <para>★ <c>IlInspector</c> 只解析方法/字段/字符串操作数，分支目标需要自己再读一次 IL ——
    /// 而 <c>IlInspector.cs</c> 属其它 owner 的文件，**不**改它，这里就地解码。</para>
    /// </summary>
    private static int? ReadBranchTarget(
        MethodBase method,
        IlInspector.IlInstruction instruction,
        byte[] il)
    {
        int size = instruction.OpCode.OperandType switch
        {
            OperandType.ShortInlineBrTarget => 1,
            OperandType.InlineBrTarget => 4,
            _ => -1,
        };
        if (size < 0) return null;

        // 这些分支 opcode 全是单字节（br/brfalse/brtrue/beq/bne.un 及其 .s 变体）。
        int operandOffset = instruction.Offset + 1;
        if (operandOffset + size > il.Length) return null;

        int delta = size == 1
            ? (sbyte)il[operandOffset]
            : BitConverter.ToInt32(il, operandOffset);

        return operandOffset + size + delta;
    }

    /// <summary>
    /// 找出方法体里作为**委托目标**被引用的方法（<c>ldftn</c> / <c>ldvirtftn</c>）。
    /// 用于"事件订阅的处理函数是谁"这类断言。
    /// </summary>
    private static MethodBase? FindDelegateTarget(
        IList<IlInspector.IlInstruction> instructions,
        Type declaringType)
    {
        foreach (IlInspector.IlInstruction instruction in instructions)
        {
            if (instruction.OpCode != OpCodes.Ldftn && instruction.OpCode != OpCodes.Ldvirtftn)
            {
                continue;
            }

            MethodBase? target = instruction.Target;
            if (target == null) continue;
            if (target.DeclaringType != declaringType) continue;
            return target;
        }

        return null;
    }

    private static MethodBase? FindAsyncStateMachineMoveNext(Type owner, string methodName)
    {
        foreach (Type nested in owner.GetNestedTypes(BindingFlags.NonPublic))
        {
            if (!nested.Name.StartsWith("<" + methodName + ">", StringComparison.Ordinal)) continue;
            MethodInfo? moveNext = nested.GetMethod(
                "MoveNext", BindingFlags.NonPublic | BindingFlags.Instance);
            if (moveNext != null) return moveNext;
        }

        return null;
    }

    private static int FindCallOffset(
        IList<IlInspector.IlInstruction> instructions,
        string methodName,
        Type declaringType)
    {
        foreach (IlInspector.IlInstruction instruction in instructions)
        {
            MethodBase? target = instruction.Target;
            if (target == null) continue;
            if (target.DeclaringType != declaringType) continue;
            if (!string.Equals(target.Name, methodName, StringComparison.Ordinal)) continue;
            return instruction.Offset;
        }

        return -1;
    }

    // ================================================================
    // 夹具
    // ================================================================

    private static RenderingRouteRequest CreateGpuRouteRequest() =>
        new(
            CreateGpuConfiguration(),
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: true,
            WdaCapturePolicyAccepted: true,
            SafeMode: false);

    private static RenderingRouteRequest CreateLegacyRouteRequest() =>
        new(
            new ThemeRenderingConfig
            {
                Backend = ThemeRenderingConfig.LegacyBackend,
                Material = ThemeRenderingConfig.LegacyMaterial
            },
            GpuEffectsEnabled: false,
            LiveBackgroundEnabled: false,
            WdaCapturePolicyAccepted: false,
            SafeMode: false);

    private static ThemeRenderingConfig CreateGpuConfiguration() =>
        new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.LiquidGlassMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(true),
                ["cornerRadius"] = JsonSerializer.SerializeToElement(24),
                ["primaryCut"] = JsonSerializer.SerializeToElement(14)
            }
        };
}
