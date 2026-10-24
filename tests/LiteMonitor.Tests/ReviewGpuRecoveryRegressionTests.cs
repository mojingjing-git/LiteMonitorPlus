using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// F01/F02：使用生产协调器、UI 故障调度入口和清理账本。
/// 不启动 UIController 的硬件/定时器构造路径，不读写 Settings 或共享配置目录。
/// 恢复时钟由 NextAttemptAt 推进，登记冲突在实例级登记边界同步注入，无 sleep。
/// </summary>
public sealed class ReviewGpuRecoveryRegressionTests
{
    [Theory]
    [InlineData(PresentationBindingState.Unbound, PresentationBindingState.Unbound, PresentationBindingState.Unbound)]
    [InlineData(PresentationBindingState.Unbound, PresentationBindingState.Bound, PresentationBindingState.Bound)]
    [InlineData(PresentationBindingState.Bound, PresentationBindingState.PresentAttempted, PresentationBindingState.PresentAttempted)]
    [InlineData(PresentationBindingState.PresentAttempted, PresentationBindingState.Presented, PresentationBindingState.Presented)]
    [InlineData(PresentationBindingState.Bound, PresentationBindingState.Unbound, PresentationBindingState.Bound)]
    [InlineData(PresentationBindingState.PresentAttempted, PresentationBindingState.Unbound, PresentationBindingState.PresentAttempted)]
    [InlineData(PresentationBindingState.Presented, PresentationBindingState.Unbound, PresentationBindingState.Presented)]
    [InlineData(PresentationBindingState.PresentAttempted, PresentationBindingState.Bound, PresentationBindingState.PresentAttempted)]
    [InlineData(PresentationBindingState.Presented, PresentationBindingState.Bound, PresentationBindingState.Presented)]
    [InlineData(PresentationBindingState.Presented, PresentationBindingState.PresentAttempted, PresentationBindingState.Presented)]
    public void F01_同一HWND历史按呈现语义单调推进(
        PresentationBindingState previous,
        PresentationBindingState incoming,
        PresentationBindingState expected)
    {
        WindowHandleGeneration generation = new();
        RenderingTransitionCoordinator coordinator = new(generation);
        coordinator.RecordBindingState(previous);

        coordinator.RecordBindingState(incoming);

        Assert.Equal(expected, coordinator.BindingState);
        Assert.Equal(generation.Current, coordinator.State.HandleGeneration);
        Assert.Equal(expected != PresentationBindingState.Unbound, coordinator.RequiresControlledRecreate());
    }

    [Theory]
    [InlineData(PresentationBindingState.Unbound, false)]
    [InlineData(PresentationBindingState.Bound, true)]
    [InlineData(PresentationBindingState.PresentAttempted, true)]
    [InlineData(PresentationBindingState.Presented, true)]
    public void F01_生产故障调度保留旧窗口历史且冷启动失败不强制重建(
        PresentationBindingState previous,
        bool requiresRecreate)
    {
        RunSta(() =>
        {
            using Form form = new();
            int handleCreations = 0;
            form.HandleCreated += (_, _) => handleCreations++;
            RenderingTransitionCoordinator coordinator = CreateGpuCoordinator(new(), out _);
            coordinator.NextHostGeneration();
            coordinator.RecordBindingState(previous);
            coordinator.RecordFirstFrame(windowVisible: false, legacyFrameObserved: false);
            coordinator.NextHostGeneration(); // 宿主替换，HWND 代次保持不变。
            GpuFailureFallbackScheduler scheduler = new();
            UIController controller = CreateController(form, coordinator, scheduler);
            GpuFailureReport report = StartupFailure();
            long generationBefore = coordinator.State.HandleGeneration;
            long transitionBefore = coordinator.TransitionId;

            controller.ScheduleGpuFailureFallback(report);

            Assert.Same(report, scheduler.Pending);
            Assert.Equal(PresentationBindingState.Unbound, scheduler.Pending.BindingState);
            Assert.Equal(previous, coordinator.BindingState);
            Assert.Equal(requiresRecreate, coordinator.RequiresControlledRecreate());
            Assert.Equal(generationBefore, coordinator.State.HandleGeneration);
            Assert.Equal(transitionBefore, coordinator.TransitionId);
            Assert.Equal(RenderingTransitionPhase.Stable, coordinator.Phase);
            Assert.Equal(RenderingRoute.GpuLiveBackground, coordinator.ExpectedRoute);
            Assert.Equal(report.FailureCode, coordinator.State.FailureCode);
            Assert.Equal(report.FailureCode, controller.GpuFallbackCode);
            Assert.True(coordinator.State.PendingFirstFrame);
            Assert.Equal(FirstFrameOutcome.NotAttempted, coordinator.State.FirstFrameOutcome);
            Assert.False(form.IsHandleCreated);
            Assert.False(form.Visible);
            Assert.Equal(0, handleCreations);
            Assert.Null(GpuHostField.GetValue(controller));
        });
    }

    [Fact]
    public void F01_生产运行故障仍先有限恢复且不提前入队回退()
    {
        RunSta(() =>
        {
            using Form form = new();
            RenderingTransitionCoordinator coordinator = new(new WindowHandleGeneration());
            coordinator.RecordBindingState(PresentationBindingState.Presented);
            GpuFailureFallbackScheduler scheduler = new();
            UIController controller = CreateController(form, coordinator, scheduler);

            controller.ScheduleGpuFailureFallback(new GpuFailureReport(
                GpuFailureStage.Runtime,
                "gpu_presentation_render_failed",
                null,
                PresentationBindingState.Unbound,
                FirstFrameOutcome.NotAttempted));

            Assert.False(scheduler.HasPending);
            Assert.Equal(PresentationBindingState.Presented, coordinator.BindingState);
            Assert.True(coordinator.RequiresControlledRecreate());
            Assert.Equal("gpu_presentation_render_failed", coordinator.State.FailureCode);
            Assert.False(form.IsHandleCreated);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F01_先推进真相源再通知新代次才清空历史且不会启动GPU(bool controlled)
    {
        RunSta(() =>
        {
            using Form form = new();
            WindowHandleGeneration generation = new();
            RenderingTransitionCoordinator coordinator = CreateGpuCoordinator(generation, out var route);
            coordinator.RecordBindingState(PresentationBindingState.Presented);
            coordinator.EnterRecreatingHandle();
            long previous = coordinator.State.HandleGeneration;
            UIController controller = CreateController(form, coordinator, new(), route);

            long current = generation.Advance();
            Assert.Equal(current, coordinator.HandleGeneration);
            Assert.Equal(previous, coordinator.State.HandleGeneration);
            controller.NotifyGpuHandleChanged(controlled, current);

            Assert.Equal(current, coordinator.State.HandleGeneration);
            Assert.Equal(PresentationBindingState.Unbound, coordinator.BindingState);
            Assert.False(coordinator.RequiresControlledRecreate());
            Assert.Equal(controlled ? RenderingTransitionPhase.Restoring : RenderingTransitionPhase.RecreatingHandle,
                coordinator.Phase);
            Assert.False(coordinator.CanStartGpuPresentation);
            Assert.Null(GpuHostField.GetValue(controller));
            Assert.False(form.IsHandleCreated);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F01_稳定GPU阶段的重复和旧通知不改变历史阶段或重启宿主(bool controlled)
    {
        RunSta(() =>
        {
            using Form form = new();
            _ = form.Handle; // 若通知错误地继续创建宿主，这个真实 HWND 会使路径继续执行。
            WindowHandleGeneration generation = new();
            RenderingTransitionCoordinator coordinator = CreateGpuCoordinator(generation, out var route);
            UIController controller = CreateController(form, coordinator, new(), route);
            long current = generation.Advance();
            controller.NotifyGpuHandleChanged(controlled: true, current);
            coordinator.RecordBindingState(PresentationBindingState.Presented);
            coordinator.PublishStable(RenderingRoute.GpuLiveBackground);
            RenderingTransitionState before = coordinator.State;

            controller.NotifyGpuHandleChanged(controlled, current);
            Assert.Same(before, coordinator.State);
            controller.NotifyGpuHandleChanged(controlled, current - 1);

            Assert.Same(before, coordinator.State);
            Assert.True(coordinator.RequiresControlledRecreate());
            Assert.Null(GpuHostField.GetValue(controller));
        });
    }

    [Fact]
    public void F01_真相源已越过尚未记账的旧回调时不接纳中间代次()
    {
        WindowHandleGeneration generation = new();
        RenderingTransitionCoordinator coordinator = new(generation);
        coordinator.RecordBindingState(PresentationBindingState.Presented);
        coordinator.EnterRecreatingHandle();
        RenderingTransitionState before = coordinator.State;
        long stale = generation.Advance();
        long current = generation.Advance();

        Assert.False(coordinator.OnHandleCreated(controlled: true, stale));
        Assert.Same(before, coordinator.State);
        Assert.True(coordinator.OnHandleCreated(controlled: true, current));
        Assert.Equal(current, coordinator.State.HandleGeneration);
        Assert.Equal(PresentationBindingState.Unbound, coordinator.BindingState);
        Assert.Equal(RenderingTransitionPhase.Restoring, coordinator.Phase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F01_保留已有直接传入42代次的测试接缝(bool controlled)
    {
        WindowHandleGeneration generation = new();
        RenderingTransitionCoordinator coordinator = new(generation);
        coordinator.RecordBindingState(PresentationBindingState.Presented);
        coordinator.EnterRecreatingHandle();

        Assert.True(coordinator.OnHandleCreated(controlled, newHandleGeneration: 42));

        Assert.Equal(1, generation.Current);
        Assert.Equal(42, coordinator.State.HandleGeneration);
        Assert.Equal(PresentationBindingState.Unbound, coordinator.BindingState);
        Assert.Equal(controlled ? RenderingTransitionPhase.Restoring : RenderingTransitionPhase.RecreatingHandle,
            coordinator.Phase);
    }

    [Fact]
    public void F01_首次HandleCreated与关闭中的通知不启动GPU或故障转换()
    {
        RunSta(() =>
        {
            using Form form = new();
            WindowHandleGeneration generation = new();
            RenderingTransitionCoordinator coordinator = new(generation);
            GpuFailureFallbackScheduler scheduler = new();
            UIController controller = CreateController(form, coordinator, scheduler);

            controller.NotifyGpuHandleChanged(controlled: false, generation.Advance());
            Assert.Equal(RenderingTransitionPhase.Stable, coordinator.Phase);
            Assert.False(coordinator.CanStartGpuPresentation);
            Assert.Null(GpuHostField.GetValue(controller));

            coordinator.RecordBindingState(PresentationBindingState.Presented);
            coordinator.NotifyFormClosing();
            RenderingTransitionState closing = coordinator.State;
            controller.NotifyGpuHandleChanged(controlled: true, generation.Advance());
            controller.ScheduleGpuFailureFallback(StartupFailure());

            Assert.Same(closing, coordinator.State);
            Assert.False(scheduler.HasPending);
            Assert.Equal(0, coordinator.RequestControlledRecreate("closing"));
            Assert.False(coordinator.AllowsWindowOperations);
            Assert.Null(GpuHostField.GetValue(controller));
            Assert.False(form.IsHandleCreated);
        });
    }

    [Fact]
    public void F01_受控重建失败后重复回调不清除失败阶段和原因()
    {
        WindowHandleGeneration generation = new();
        RenderingTransitionCoordinator coordinator = new(generation);
        coordinator.RecordBindingState(PresentationBindingState.Presented);
        long request = coordinator.RequestControlledRecreate("review_recreate_failure");
        coordinator.EnterRecreatingHandle();
        Assert.True(coordinator.CancelControlledRecreate(request));
        coordinator.FailHandleRecreate();
        RenderingTransitionState failed = coordinator.State;

        Assert.False(coordinator.OnHandleCreated(controlled: true, generation.Current));
        Assert.False(coordinator.OnHandleCreated(controlled: true, generation.Current - 1));

        Assert.Same(failed, coordinator.State);
        Assert.Equal(RenderingTransitionPhase.Failed, coordinator.Phase);
        Assert.Equal(RenderingTransitionCoordinator.HandleRecreateFailedCode, failed.FailureCode);
        Assert.True(coordinator.RequiresControlledRecreate());
        Assert.False(coordinator.CanStartGpuPresentation);
        Assert.Equal(0, coordinator.PendingControlledRecreateCount);
    }

    [Theory]
    [InlineData(1, 0x22u)] // A 耗尽 -> B 耗尽 -> C 成功。
    [InlineData(3, 0x22u)]
    [InlineData(3, 0u)]
    [InlineData(2, 1u)]
    public void F02_逐轮接管直到恢复成功并保留全部Failed审计(int failedRecoveryRounds, uint originalAffinity)
    {
        LedgerHarness h = new();
        IntPtr window = new(0x1234);
        WindowCapturePolicyCleanup current = h.Add(window, originalAffinity);
        h.Exhaust(current);
        List<(WindowCapturePolicyCleanup Entry, CleanupAudit Audit)> failures = [];

        for (int round = 0; round <= failedRecoveryRounds; round++)
        {
            failures.Add((current, Audit(current)));
            Assert.False(current.ReclaimedByExplicitRecovery);
            Assert.Equal(CapturePolicyCleanupLedger.UnresolvedBlockingErrorCode,
                h.Ledger.GetBlockingReasonForGeneration(h.Generation.Current));

            Assert.True(h.Ledger.TryScheduleExplicitRecoveryForRejectedCode(
                h.Ledger.GetBlockingReasonForGeneration(h.Generation.Current),
                h.Generation.Current,
                h.Now,
                out string? error));
            Assert.Null(error);

            WindowCapturePolicyCleanup rearmed = h.Ledger.Entries[^1];
            Assert.NotSame(current, rearmed);
            Assert.True(current.ReclaimedByExplicitRecovery);
            Assert.Equal(failures[^1].Audit, Audit(current));
            Assert.Equal(window, rearmed.WindowHandle);
            Assert.Equal(originalAffinity, rearmed.OriginalAffinity);
            Assert.Equal(h.Generation.Current, rearmed.Generation);
            Assert.True(rearmed.AffinityChangedBySession);
            Assert.True(rearmed.RebuiltByExplicitRecovery);
            Assert.Equal(0, rearmed.Attempts);
            Assert.Equal(h.Now, rearmed.NextAttemptAt);
            Assert.Single(h.Ledger.Entries.Where(entry => !entry.IsTerminal));
            Assert.Equal(CapturePolicyCleanupLedger.PendingBlockingErrorCode,
                h.Ledger.GetBlockingReasonForGeneration(h.Generation.Current));

            int count = h.Ledger.Entries.Count;
            Assert.True(h.Ledger.Add(rearmed)); // 同一实例重复登记仍幂等。
            Assert.False(h.Ledger.TryRearmForExplicitRecovery(h.Generation.Current, h.Now, out error));
            Assert.Equal(CapturePolicyCleanupLedger.AlreadyPendingErrorCode, error);
            Assert.Equal(count, h.Ledger.Entries.Count);
            Assert.Single(h.Ledger.Entries.Where(entry => !entry.IsTerminal));

            int callsBefore = h.Restores.Count;
            current = rearmed;
            if (round < failedRecoveryRounds)
            {
                h.Exhaust(current);
            }
            else
            {
                h.RestoreSucceeds = true;
                Assert.Equal(1, h.Ledger.Pump(h.Now));
                Assert.Equal(CapturePolicyCleanupState.Completed, current.State);
                Assert.Equal("restored", current.TerminalReason);
            }

            Assert.All(h.Restores.Skip(callsBefore), entry => Assert.Same(current, entry));
        }

        Assert.Null(h.Ledger.GetBlockingReasonForGeneration(h.Generation.Current));
        Assert.False(h.Ledger.HasUnresolvedForGeneration(h.Generation.Current));
        Assert.False(h.Ledger.HasPendingForGeneration(h.Generation.Current));
        Assert.All(h.Restores, entry =>
        {
            Assert.Equal(window, entry.WindowHandle);
            Assert.Equal(originalAffinity, entry.OriginalAffinity);
        });
        foreach (var failure in failures)
        {
            Assert.Equal(CapturePolicyCleanupState.Failed, failure.Entry.State);
            Assert.Equal("exhausted", failure.Entry.TerminalReason);
            Assert.True(failure.Entry.ReclaimedByExplicitRecovery);
            Assert.Equal(failure.Audit, Audit(failure.Entry));
            Assert.False(failure.Entry.TryMarkCompleted("late_success"));
        }

        int finalCount = h.Ledger.Entries.Count;
        int finalCalls = h.Restores.Count;
        Assert.False(h.Ledger.TryRearmForExplicitRecovery(h.Generation.Current, h.Now, out string? completedError));
        Assert.Equal(CapturePolicyCleanupLedger.NoUnresolvedEntryErrorCode, completedError);
        Assert.False(h.Ledger.TryScheduleExplicitRecoveryForRejectedCode(
            CapturePolicyCleanupLedger.UnresolvedBlockingErrorCode,
            h.Generation.Current,
            h.Now,
            out completedError));
        Assert.Equal(CapturePolicyCleanupLedger.NoUnresolvedEntryErrorCode, completedError);
        Assert.Equal(finalCount, h.Ledger.Entries.Count);
        Assert.Equal(0, h.Ledger.Pump(h.Now.AddDays(1)));
        Assert.Equal(finalCalls, h.Restores.Count);
    }

    [Fact]
    public void F02_同代次成功后再失败只接管新责任并沿用新的精确原值()
    {
        LedgerHarness h = new();
        IntPtr window = new(0x4567);
        WindowCapturePolicyCleanup first = h.Add(window, 0x22u);
        h.Exhaust(first);
        Assert.True(h.Ledger.TryRearmForExplicitRecovery(h.Generation.Current, h.Now, out _));
        h.RestoreSucceeds = true;
        Assert.Equal(1, h.Ledger.Pump(h.Now));
        Assert.Null(h.Ledger.GetBlockingReasonForGeneration(h.Generation.Current));

        h.RestoreSucceeds = false;
        WindowCapturePolicyCleanup second = h.Add(window, 1u);
        h.Exhaust(second);
        Assert.True(h.Ledger.TryRearmForExplicitRecovery(h.Generation.Current, h.Now, out _));
        WindowCapturePolicyCleanup recovery = h.Ledger.Entries[^1];

        Assert.True(second.ReclaimedByExplicitRecovery);
        Assert.Equal(window, recovery.WindowHandle);
        Assert.Equal(1u, recovery.OriginalAffinity);
        h.RestoreSucceeds = true;
        Assert.Equal(1, h.Ledger.Pump(h.Now));
        Assert.Same(recovery, h.Restores[^1]);
        Assert.Null(h.Ledger.GetBlockingReasonForGeneration(h.Generation.Current));
        Assert.Equal(CapturePolicyCleanupState.Failed, first.State);
        Assert.Equal(CapturePolicyCleanupState.Failed, second.State);
    }

    [Fact]
    public void F02_多个未解决责任只接管选中条目且不误解除其他责任的闸门()
    {
        LedgerHarness h = new();
        WindowCapturePolicyCleanup first = h.Add(new IntPtr(0x1234), 0x22u);
        h.Exhaust(first);
        CleanupAudit firstAudit = Audit(first);
        WindowCapturePolicyCleanup other = h.Add(new IntPtr(0x5678), 1u);
        h.Exhaust(other);
        CleanupAudit otherAudit = Audit(other);

        Assert.True(h.Ledger.TryRearmForExplicitRecovery(h.Generation.Current, h.Now, out _));
        WindowCapturePolicyCleanup firstRecovery = h.Ledger.Entries[^1];
        Assert.True(first.ReclaimedByExplicitRecovery);
        Assert.False(other.ReclaimedByExplicitRecovery);
        Assert.Equal(first.WindowHandle, firstRecovery.WindowHandle);
        Assert.Equal(first.OriginalAffinity, firstRecovery.OriginalAffinity);
        Assert.Single(h.Ledger.Entries.Where(entry => !entry.IsTerminal));
        h.RestoreSucceeds = true;
        Assert.Equal(1, h.Ledger.Pump(h.Now));
        Assert.Same(firstRecovery, h.Restores[^1]);
        Assert.Equal(CapturePolicyCleanupLedger.UnresolvedBlockingErrorCode,
            h.Ledger.GetBlockingReasonForGeneration(h.Generation.Current));
        Assert.Equal(otherAudit, Audit(other));

        Assert.True(h.Ledger.TryRearmForExplicitRecovery(h.Generation.Current, h.Now, out _));
        WindowCapturePolicyCleanup otherRecovery = h.Ledger.Entries[^1];
        Assert.True(other.ReclaimedByExplicitRecovery);
        Assert.Equal(other.WindowHandle, otherRecovery.WindowHandle);
        Assert.Equal(other.OriginalAffinity, otherRecovery.OriginalAffinity);
        Assert.Single(h.Ledger.Entries.Where(entry => !entry.IsTerminal));
        Assert.Equal(1, h.Ledger.Pump(h.Now));
        Assert.Same(otherRecovery, h.Restores[^1]);
        Assert.Null(h.Ledger.GetBlockingReasonForGeneration(h.Generation.Current));
        Assert.Equal(firstAudit, Audit(first));
        Assert.Equal(otherAudit, Audit(other));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void F02_登记边界成功后才接管且登记冲突保留旧责任(bool rejectRegistration)
    {
        LedgerHarness h = new();
        WindowCapturePolicyCleanup failed = h.Add(new IntPtr(0x1234), 0x22u);
        h.Exhaust(failed);
        CleanupAudit before = Audit(failed);
        WindowCapturePolicyCleanup? candidate = null;
        var previousHook = h.Ledger.BeforeExplicitRecoveryRegistration;
        try
        {
            h.Ledger.BeforeExplicitRecoveryRegistration = cleanup =>
            {
                candidate = cleanup;
                Assert.False(failed.ReclaimedByExplicitRecovery);
                Assert.Equal(before, Audit(failed));
                Assert.DoesNotContain(cleanup, h.Ledger.Entries);
                Assert.Equal(failed.WindowHandle, cleanup.WindowHandle);
                Assert.Equal(failed.Generation, cleanup.Generation);
                Assert.Equal(failed.OriginalAffinity, cleanup.OriginalAffinity);
                if (rejectRegistration)
                {
                    // 在真实 Add 前建立冲突，验证生产 Add 的拒绝分支及提交顺序。
                    h.Add(failed.WindowHandle, 0x11u);
                }
            };

            bool rearmed = h.Ledger.TryRearmForExplicitRecovery(h.Generation.Current, h.Now, out string? error);

            Assert.NotNull(candidate);
            Assert.Equal(!rejectRegistration, rearmed);
            Assert.Equal(!rejectRegistration, failed.ReclaimedByExplicitRecovery);
            Assert.Equal(before, Audit(failed));
            Assert.Equal(2, h.Ledger.Entries.Count);
            Assert.Single(h.Ledger.Entries.Where(entry => !entry.IsTerminal));
            if (rejectRegistration)
            {
                Assert.Equal(CapturePolicyCleanupLedger.RearmFailedErrorCode, error);
                Assert.DoesNotContain(candidate!, h.Ledger.Entries);
                Assert.Equal(CapturePolicyCleanupLedger.RegistrationRejectedWarningCode,
                    h.Ledger.RegistrationRejectedCode);
            }
            else
            {
                Assert.Null(error);
                Assert.Same(candidate, h.Ledger.Entries[^1]);
            }
        }
        finally
        {
            h.Ledger.BeforeExplicitRecoveryRegistration = previousHook;
        }
    }

    [Fact]
    public void F02_没有未解决责任或未改过affinity时不能凭空恢复()
    {
        LedgerHarness h = new();
        Assert.False(h.Ledger.TryRearmForExplicitRecovery(h.Generation.Current, h.Now, out string? error));
        Assert.Equal(CapturePolicyCleanupLedger.NoUnresolvedEntryErrorCode, error);
        Assert.Empty(h.Ledger.Entries);

        WindowCapturePolicyCleanup unchanged = h.Add(new IntPtr(0x9876), 0x22u, affinityChanged: false);
        h.Ledger.FinalizeOnFormClosed(h.Now);
        CleanupAudit before = Audit(unchanged);
        Assert.Equal(CapturePolicyCleanupState.Failed, unchanged.State);
        Assert.False(h.Ledger.TryRearmForExplicitRecovery(h.Generation.Current, h.Now, out error));
        Assert.Equal(CapturePolicyCleanupLedger.NoUnresolvedEntryErrorCode, error);
        Assert.Equal(before, Audit(unchanged));
        Assert.False(unchanged.ReclaimedByExplicitRecovery);
        Assert.Single(h.Ledger.Entries);
        Assert.Empty(h.Restores);
        Assert.Null(h.Ledger.GetBlockingReasonForGeneration(h.Generation.Current));
    }

    [Fact]
    public void F02_新代次不会接管旧失败责任且不访问旧HWND()
    {
        LedgerHarness h = new();
        WindowCapturePolicyCleanup failed = h.Add(new IntPtr(0x1234), 0x22u);
        h.Exhaust(failed);
        CleanupAudit before = Audit(failed);
        int windowChecks = h.WindowChecks;
        int restoreCalls = h.Restores.Count;
        long current = h.Generation.Advance();

        Assert.False(h.Ledger.TryRearmForExplicitRecovery(current, h.Now, out string? error));
        Assert.Equal(CapturePolicyCleanupLedger.NoUnresolvedEntryErrorCode, error);
        Assert.Equal(0, h.Ledger.Pump(h.Now.AddDays(1)));

        Assert.Equal(before, Audit(failed));
        Assert.False(failed.ReclaimedByExplicitRecovery);
        Assert.Equal(windowChecks, h.WindowChecks);
        Assert.Equal(restoreCalls, h.Restores.Count);
        Assert.Single(h.Ledger.Entries);
        Assert.Null(h.Ledger.GetBlockingReasonForGeneration(current));
    }

    [Fact]
    public void F02_接管后句柄换代在窗口探针前终结旧责任()
    {
        LedgerHarness h = new();
        WindowCapturePolicyCleanup failed = h.Add(new IntPtr(0x1234), 0x22u);
        h.Exhaust(failed);
        CleanupAudit before = Audit(failed);
        Assert.True(h.Ledger.TryRearmForExplicitRecovery(h.Generation.Current, h.Now, out _));
        WindowCapturePolicyCleanup recovery = h.Ledger.Entries[^1];
        int windowChecks = h.WindowChecks;
        int restoreCalls = h.Restores.Count;
        long current = h.Generation.Advance();

        Assert.Equal(0, h.Ledger.Pump(h.Now));

        Assert.Equal(windowChecks, h.WindowChecks);
        Assert.Equal(restoreCalls, h.Restores.Count);
        Assert.Equal(CapturePolicyCleanupState.Completed, recovery.State);
        Assert.Equal("superseded_by_new_generation", recovery.TerminalReason);
        Assert.Equal(before, Audit(failed));
        Assert.True(failed.ReclaimedByExplicitRecovery);
        Assert.Null(h.Ledger.GetBlockingReasonForGeneration(current));
    }

    private static GpuFailureReport StartupFailure() => new(
        GpuFailureStage.Startup,
        "gpu_presentation_start_failed",
        "replacement host failed before binding",
        PresentationBindingState.Unbound,
        FirstFrameOutcome.NotAttempted);

    private static RenderingTransitionCoordinator CreateGpuCoordinator(
        WindowHandleGeneration generation,
        out RenderingRouteDecision route)
    {
        RenderingTransitionCoordinator coordinator = new(generation);
        Assert.True(coordinator.TryBeginTransition(
            new RenderingRouteRequest(
                new ThemeRenderingConfig
                {
                    Backend = ThemeRenderingConfig.GpuBackend,
                    Material = ThemeRenderingConfig.LiquidGlassMaterial,
                    Parameters = new Dictionary<string, JsonElement>
                    {
                        ["liveBackground"] = JsonSerializer.SerializeToElement(true),
                        ["cornerRadius"] = JsonSerializer.SerializeToElement(24),
                        ["primaryCut"] = JsonSerializer.SerializeToElement(14),
                    },
                },
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: false),
            RenderingTransitionTrigger.UserThemeChange,
            out var decision));
        route = decision!;
        Assert.True(route.IsGpu);
        coordinator.PublishStable(route.Route);
        return coordinator;
    }

    private static UIController CreateController(
        Form form,
        RenderingTransitionCoordinator coordinator,
        GpuFailureFallbackScheduler scheduler,
        RenderingRouteDecision? route = null)
    {
        // 使用生产方法，只跳过会启动硬件和 UI 定时器的构造函数。
        UIController controller = (UIController)RuntimeHelpers.GetUninitializedObject(typeof(UIController));
        ControllerField("_form").SetValue(controller, form);
        ControllerField("_transition").SetValue(controller, coordinator);
        ControllerField("_gpuFailureFallback").SetValue(controller, scheduler);
        ControllerField("_gpuRoute").SetValue(controller, route);
        return controller;
    }

    private static FieldInfo GpuHostField => ControllerField("_gpuPresentation");

    private static FieldInfo ControllerField(string name) =>
        typeof(UIController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static CleanupAudit Audit(WindowCapturePolicyCleanup cleanup) => new(
        cleanup.State,
        cleanup.Attempts,
        cleanup.TerminalReason,
        cleanup.LastErrorCode,
        cleanup.LastErrorMessage,
        cleanup.NextAttemptAt);

    private sealed record CleanupAudit(
        CapturePolicyCleanupState State,
        int Attempts,
        string? TerminalReason,
        string? ErrorCode,
        string? ErrorMessage,
        DateTimeOffset NextAttemptAt);

    private sealed class LedgerHarness
    {
        internal WindowHandleGeneration Generation { get; } = new();
        internal DateTimeOffset Now { get; private set; } = DateTimeOffset.UnixEpoch;
        internal bool RestoreSucceeds { get; set; }
        internal int WindowChecks { get; private set; }
        internal List<WindowCapturePolicyCleanup> Restores { get; } = [];
        internal CapturePolicyCleanupLedger Ledger { get; }

        internal LedgerHarness()
        {
            Ledger = new CapturePolicyCleanupLedger(
                () => Generation.Current,
                _ => { WindowChecks++; return true; },
                cleanup =>
                {
                    Restores.Add(cleanup);
                    return RestoreSucceeds
                        ? new CapturePolicyRestoreResult(true, null, null)
                        : new CapturePolicyRestoreResult(false,
                            CapturePolicyCleanupLedger.RestoreFailedErrorCode, "injected restore failure");
                });
        }

        internal WindowCapturePolicyCleanup Add(IntPtr window, uint affinity, bool affinityChanged = true)
        {
            WindowCapturePolicyCleanup cleanup = new()
            {
                WindowHandle = window,
                Generation = Generation.Current,
                OriginalAffinity = affinity,
                AffinityChangedBySession = affinityChanged,
            };
            Assert.True(Ledger.Add(cleanup));
            return cleanup;
        }

        internal void Exhaust(WindowCapturePolicyCleanup cleanup)
        {
            for (int i = 0; i < CapturePolicyCleanupLedger.MaxAttempts && !cleanup.IsTerminal; i++)
            {
                if (Now < cleanup.NextAttemptAt)
                {
                    Now = cleanup.NextAttemptAt;
                }

                Assert.Equal(1, Ledger.Pump(Now));
            }

            Assert.Equal(CapturePolicyCleanupState.Failed, cleanup.State);
            Assert.Equal(CapturePolicyCleanupLedger.MaxAttempts, cleanup.Attempts);
            Assert.Equal("exhausted", cleanup.TerminalReason);
            Assert.Equal(CapturePolicyCleanupLedger.RetryExhaustedErrorCode, cleanup.LastErrorCode);
        }
    }
}
