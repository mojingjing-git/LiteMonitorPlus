using System.Runtime.ExceptionServices;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【GPU T04】清理责任转交、代次、有限重试与呈现历史的确定性单测。
/// <para>不依赖真实桌面 / GPU / DXGI：全部用例只驱动
/// <see cref="PresentationSession"/>、<see cref="GpuPresentationSessionOwner"/>、
/// <see cref="CapturePolicyCleanupPolicy"/> 与宿主停止路径。</para>
/// </summary>
public sealed class GpuPresentationTransitionTests
{
    private static readonly RenderGenerations Generations = new(1, 1, 1, 1, 1);

    // ================================================================
    // 1. 清理责任转交（合同 §6.2 / 裁决 D2 —— GPU-001 的根因）
    // ================================================================

    [Fact]
    public void 恢复连续失败两次后宿主仍然释放自己并移交清理责任()
    {
        // 两次恢复写入都失败（尝试 1、2 全部失败）。
        RecordingWindowAdapter adapter = new(originalAffinity: 0, failRestoreWrites: 2);
        GpuPresentationSessionOwner owner = new();
        PresentationSession session = new(adapter);
        Assert.True(session.Start(
            new IntPtr(42),
            Generations,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _));
        owner.Bind(session);

        WindowCapturePolicyCleanup? cleanup = owner.Stop(
            generation: 7,
            now: DateTimeOffset.UnixEpoch,
            out string? errorCode,
            out _);

        // 宿主**无条件**释放自己 —— 清理责任绝不通过"宿主引用非空"表达。
        Assert.Null(owner.Session);
        Assert.False(owner.OwnsSession);

        // 两次失败之后仍持有清理责任（合同 §6.4 的尝试 1、2）。
        Assert.NotNull(cleanup);
        Assert.Equal(2, cleanup!.Attempts);
        Assert.Equal(CapturePolicyCleanupState.Pending, cleanup.State);
        Assert.Equal(new IntPtr(42), cleanup.WindowHandle);
        Assert.Equal(7, cleanup.Generation);
        Assert.Equal(0u, cleanup.OriginalAffinity);
        Assert.True(cleanup.AffinityChangedBySession);
        Assert.Equal("presentation_capture_policy_restore_failed", errorCode);

        // 下一次重试（尝试 3）由 ledger 在 250 ms 后发起。
        Assert.Equal(
            DateTimeOffset.UnixEpoch.AddMilliseconds(250),
            cleanup.NextAttemptAt);
    }

    [Fact]
    public void 内联重试成功时不产生任何清理责任()
    {
        // 只让**第一次**恢复失败：内联重试（尝试 2）成功。
        RecordingWindowAdapter adapter = new(originalAffinity: 0, failRestoreWrites: 1);
        GpuPresentationSessionOwner owner = new();
        PresentationSession session = new(adapter);
        Assert.True(session.Start(
            new IntPtr(42),
            Generations,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _));
        owner.Bind(session);

        WindowCapturePolicyCleanup? cleanup = owner.Stop(
            generation: 7,
            now: DateTimeOffset.UnixEpoch,
            out _,
            out _);

        Assert.Null(cleanup);
        Assert.Null(owner.Session);
    }

    [Fact]
    public void 没有未完成清理责任时停止不产生记录()
    {
        // WindowCapturePolicy.None 从不修改 affinity ⇒ 无需恢复。
        RecordingWindowAdapter adapter = new(originalAffinity: 0);
        GpuPresentationSessionOwner owner = new();
        PresentationSession session = new(adapter);
        Assert.True(session.Start(
            new IntPtr(42),
            Generations,
            WindowCapturePolicy.None,
            out _,
            out _));
        owner.Bind(session);

        WindowCapturePolicyCleanup? cleanup = owner.Stop(
            generation: 7,
            now: DateTimeOffset.UnixEpoch,
            out string? errorCode,
            out _);

        Assert.Null(cleanup);
        Assert.Null(errorCode);
        Assert.Null(owner.Session);
        Assert.Empty(adapter.SetHistory);
    }

    // ================================================================
    // 2. 宿主停止路径（GPU-001 的六行链条在此被切断）
    // ================================================================

    [Fact]
    public void 宿主在恢复失败时不再保留已释放的会话并把清理记录交给订阅者()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory();
            try
            {
                RecordingWindowAdapter adapter = new(originalAffinity: 0, failRestoreWrites: 2);
                using Form form = new();
                using GpuPresentationHostController host = new(
                    form,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: new GpuStartupGuard(directory, @"C:\apps\A"),
                    windowAdapter: adapter);

                List<WindowCapturePolicyCleanup> handed = [];
                host.CapturePolicyCleanupRequired += cleanup => handed.Add(cleanup);

                PresentationSession session = new(adapter);
                Assert.True(session.Start(
                    new IntPtr(42),
                    Generations,
                    WindowCapturePolicy.WdaExcludeFromCapture,
                    out _,
                    out _));
                host.SessionOwner.Bind(session);

                host.Dispose();

                // GPU-001 的根因：恢复失败后宿主**不再**保留会话引用。
                Assert.Null(host.SessionOwner.Session);
                Assert.True(host.WindowCapturePolicyRestored);

                // 清理责任被移交，而不是随宿主一起丢失。
                Assert.Single(handed);
                Assert.Equal(2, handed[0].Attempts);
                Assert.Equal(new IntPtr(42), handed[0].WindowHandle);
                Assert.Equal(CapturePolicyCleanupState.Pending, handed[0].State);
                Assert.Same(handed[0], host.LastCapturePolicyCleanup);

                // 呈现历史与首帧结果在宿主未渲染时仍是初始值。
                Assert.Equal(PresentationBindingState.Unbound, host.BindingState);
                Assert.Equal(FirstFrameOutcome.NotAttempted, host.FirstFrameOutcome);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void 同一代次仍有未完成清理时宿主拒绝叠加新的affinity所有者()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory();
            try
            {
                RecordingWindowAdapter adapter = new(originalAffinity: 0);
                using Form form = new();
                _ = form.Handle;
                using GpuPresentationHostController host = new(
                    form,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: new GpuStartupGuard(directory, @"C:\apps\A"),
                    windowAdapter: adapter);

                // 合同 §6.2 INV-6：同一代次有未完成清理 ⇒ 拒绝取得所有权。
                host.CapturePolicyCleanupPendingProbe = _ => true;

                Assert.False(host.Start(
                    WindowCapturePolicy.WdaExcludeFromCapture,
                    out string? errorCode,
                    out _));
                Assert.Equal("gpu_capture_policy_cleanup_pending", errorCode);

                // 未取得所有权 ⇒ 不得对窗口施加任何 affinity。
                Assert.Empty(adapter.SetHistory);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    // ================================================================
    // 3. PresentationSession 的 R1–R6（合同 §7.3）
    // ================================================================

    [Fact]
    public void 句柄代次没有推进时拒绝重建且保持旧句柄()
    {
        RecordingWindowAdapter adapter = new(originalAffinity: 0);
        WindowHandleGeneration generations = new();
        PresentationSession session = new(adapter, generations);
        Assert.True(session.Start(
            new IntPtr(42),
            Generations,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _));

        // 未推进代次 ⇒ 拒绝（合同 §7.3 R1）。
        Assert.False(session.RecreateWindow(
            new IntPtr(43),
            Generations with { Window = 2 },
            WindowCapturePolicy.WdaExcludeFromCapture,
            out string? errorCode,
            out _));
        Assert.Equal("presentation_session_generation_not_advanced", errorCode);

        // R2：句柄与代次保持旧值。
        Assert.Equal(new IntPtr(42), session.WindowHandle);
        Assert.Equal(generations.Current, session.HandleGeneration);
        Assert.Empty(adapter.SetHistory.Skip(1));
    }

    [Fact]
    public void 重建先恢复旧句柄再在新句柄重读原值()
    {
        RecordingWindowAdapter adapter = new(originalAffinity: 0);
        WindowHandleGeneration generations = new();
        PresentationSession session = new(adapter, generations);
        session.Start(
            new IntPtr(42),
            Generations,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _);

        long advanced = generations.Advance();
        Assert.True(session.RecreateWindow(
            new IntPtr(43),
            Generations with { Window = 2 },
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _));

        // R3：原值必须来自**新**句柄（读记录的最后一项是新句柄）。
        Assert.Equal(new IntPtr(42), adapter.ReadHistory[0]);
        Assert.Equal(new IntPtr(43), adapter.ReadHistory[^1]);

        // 旧句柄先恢复原值，新句柄再独立取得所有权。
        Assert.Equal((new IntPtr(42), 0x11u), adapter.SetHistory[0]);
        Assert.Equal((new IntPtr(42), 0u), adapter.SetHistory[1]);
        Assert.Equal((new IntPtr(43), 0x11u), adapter.SetHistory[2]);

        // R5：会话暴露当前句柄代次。
        Assert.Equal(advanced, session.HandleGeneration);
        Assert.Equal(advanced, session.Diagnostics(WindowCapturePolicy.WdaExcludeFromCapture).HandleGeneration);
    }

    [Fact]
    public void 重建时恢复失败保持旧句柄并给出可区分的错误码()
    {
        RecordingWindowAdapter adapter = new(originalAffinity: 0, failRestoreWrites: 1);
        WindowHandleGeneration generations = new();
        PresentationSession session = new(adapter, generations);
        session.Start(
            new IntPtr(42),
            Generations,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _);
        generations.Advance();

        Assert.False(session.RecreateWindow(
            new IntPtr(43),
            Generations with { Window = 2 },
            WindowCapturePolicy.WdaExcludeFromCapture,
            out string? errorCode,
            out _));

        // R6：恢复失败与"状态不允许"必须可区分。
        Assert.Equal("presentation_capture_policy_restore_failed", errorCode);
        Assert.NotEqual("presentation_session_not_recreatable", errorCode);

        // R2：句柄与代次保持旧值，清理责任仍由会话持有。
        Assert.Equal(new IntPtr(42), session.WindowHandle);
        Assert.Equal(RenderLifecycleState.Fallback, session.State);
        Assert.True(session.OwnsCapturePolicyCleanup);

        WindowCapturePolicyCleanup? cleanup = session.CreateCapturePolicyCleanup(generations.Current);
        Assert.NotNull(cleanup);
        Assert.Equal(new IntPtr(42), cleanup!.WindowHandle);
        Assert.Equal(0u, cleanup.OriginalAffinity);
        Assert.True(cleanup.AffinityChangedBySession);
    }

    [Fact]
    public void 未启动的会话重建时给出状态不允许的错误码()
    {
        RecordingWindowAdapter adapter = new(originalAffinity: 0);
        PresentationSession session = new(adapter);

        Assert.False(session.RecreateWindow(
            new IntPtr(43),
            Generations with { Window = 2 },
            WindowCapturePolicy.WdaExcludeFromCapture,
            out string? errorCode,
            out _));
        Assert.Equal("presentation_session_not_recreatable", errorCode);
    }

    [Fact]
    public void 新宿主在新句柄上独立读取原值不继承旧宿主的affinity()
    {
        RecordingWindowAdapter adapter = new(originalAffinity: 0, failRestoreWrites: 2);
        adapter.ReadValues[new IntPtr(42)] = 0u;
        adapter.ReadValues[new IntPtr(43)] = 0x22u;

        // 旧宿主：读 0 → 施加 0x11 → 恢复连续失败（affinity 停留在 0x11）。
        PresentationSession oldSession = new(adapter);
        Assert.True(oldSession.Start(
            new IntPtr(42),
            Generations,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _));
        GpuPresentationSessionOwner oldOwner = new();
        oldOwner.Bind(oldSession);
        WindowCapturePolicyCleanup? handed = oldOwner.Stop(
            generation: 1,
            now: DateTimeOffset.UnixEpoch,
            out _,
            out _);
        Assert.NotNull(handed);

        // 新宿主：新句柄 ⇒ 独立读原值 0x22，并独立取得所有权。
        PresentationSession newSession = new(adapter);
        Assert.True(newSession.Start(
            new IntPtr(43),
            Generations,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _));
        Assert.True(newSession.BeginStop(out _));
        Assert.True(newSession.CompleteStop(out _, out _));

        // 新句柄恢复的是**它自己的**原值 0x22，而不是旧句柄的 0。
        Assert.Equal((new IntPtr(43), 0x22u), adapter.SetHistory[^1]);
    }

    // ================================================================
    // 4. 跨代次丢弃判据与重试预算（合同 §6.4 / §7.4）
    // ================================================================

    [Fact]
    public void 旧代次的清理责任被取代时禁止任何窗口调用并直接终结()
    {
        WindowCapturePolicyCleanup cleanup = new()
        {
            WindowHandle = new IntPtr(42),
            Generation = 3,
            OriginalAffinity = 0,
            AffinityChangedBySession = true,
        };
        int probes = 0;

        CapturePolicyCleanupJudgement judgement = CapturePolicyCleanupPolicy.Judge(
            cleanup,
            currentGeneration: 4,
            now: DateTimeOffset.UnixEpoch,
            isWindowAlive: _ =>
            {
                probes++;
                return true;
            });

        Assert.Equal(CapturePolicyCleanupJudgement.SupersededByNewGeneration, judgement);
        Assert.Equal(0, probes);

        Assert.True(cleanup.TryMarkSupersededByNewGeneration());
        Assert.Equal(CapturePolicyCleanupState.Completed, cleanup.State);
        Assert.Equal("superseded_by_new_generation", cleanup.TerminalReason);
        Assert.Equal("gpu_capture_policy_cleanup_superseded", cleanup.WarningCode);
    }

    [Fact]
    public void 同代次的旧窗口被销毁后清理可以完成()
    {
        WindowCapturePolicyCleanup cleanup = new()
        {
            WindowHandle = new IntPtr(42),
            Generation = 4,
            OriginalAffinity = 0,
            AffinityChangedBySession = true,
        };
        int probes = 0;

        CapturePolicyCleanupJudgement judgement = CapturePolicyCleanupPolicy.Judge(
            cleanup,
            currentGeneration: 4,
            now: DateTimeOffset.UnixEpoch,
            isWindowAlive: _ =>
            {
                probes++;
                return false;
            });

        Assert.Equal(CapturePolicyCleanupJudgement.WindowDestroyed, judgement);
        Assert.Equal(1, probes);

        Assert.True(cleanup.TryMarkCompleted("window_destroyed"));
        Assert.Equal(CapturePolicyCleanupState.Completed, cleanup.State);
        Assert.Equal("window_destroyed", cleanup.TerminalReason);
    }

    [Fact]
    public void 清理的重试预算是六次且退避为250_500_1000_2000毫秒()
    {
        Assert.Equal(6, CapturePolicyCleanupPolicy.MaxAttempts);
        Assert.Equal(TimeSpan.Zero, CapturePolicyCleanupPolicy.BackoffAfterAttempt(1));
        Assert.Equal(TimeSpan.FromMilliseconds(250), CapturePolicyCleanupPolicy.BackoffAfterAttempt(2));
        Assert.Equal(TimeSpan.FromMilliseconds(500), CapturePolicyCleanupPolicy.BackoffAfterAttempt(3));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), CapturePolicyCleanupPolicy.BackoffAfterAttempt(4));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), CapturePolicyCleanupPolicy.BackoffAfterAttempt(5));
    }

    [Fact]
    public void 退避未到不得重试且六次之后预算耗尽()
    {
        WindowCapturePolicyCleanup cleanup = new()
        {
            WindowHandle = new IntPtr(42),
            Generation = 4,
            OriginalAffinity = 0,
            AffinityChangedBySession = true,
        };
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        cleanup.TryRecordFailedAttempt("failed", "message", now.AddMilliseconds(250));

        Assert.Equal(
            CapturePolicyCleanupJudgement.RetryNotDueYet,
            CapturePolicyCleanupPolicy.Judge(cleanup, 4, now, _ => true));
        Assert.Equal(
            CapturePolicyCleanupJudgement.RetryDue,
            CapturePolicyCleanupPolicy.Judge(cleanup, 4, now.AddMilliseconds(250), _ => true));

        for (int attempt = 0; attempt < 5; attempt++)
        {
            cleanup.TryRecordFailedAttempt("failed", "message", now);
        }

        Assert.Equal(6, cleanup.Attempts);
        Assert.Equal(
            CapturePolicyCleanupJudgement.RetryExhausted,
            CapturePolicyCleanupPolicy.Judge(cleanup, 4, now, _ => true));

        // 耗尽后终态不可回退（合同 §6.3）。
        Assert.True(cleanup.TryMarkFailed(
            "exhausted",
            CapturePolicyCleanupPolicy.RetryExhaustedErrorCode));
        Assert.Equal(CapturePolicyCleanupState.Failed, cleanup.State);
        Assert.False(cleanup.TryRecordFailedAttempt("again", "again", now));
        Assert.Equal(6, cleanup.Attempts);
    }

    // ================================================================
    // 辅助
    // ================================================================

    private static string TemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "LiteMonitor.Tests",
            "gpu-t04-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>在 STA 线程上执行（WinForms 控件需要 STA）。</summary>
    private static void RunSta(Action action)
    {
        Exception? captured = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    private sealed class RecordingWindowAdapter : IPresentationWindowAdapter
    {
        private readonly uint _originalAffinity;
        private int _failRestoreWrites;

        public RecordingWindowAdapter(uint originalAffinity = 0, int failRestoreWrites = 0)
        {
            _originalAffinity = originalAffinity;
            _failRestoreWrites = failRestoreWrites;
            CurrentAffinity = originalAffinity;
        }

        public uint CurrentAffinity { get; set; }

        public int ReadCount { get; private set; }

        public List<IntPtr> ReadHistory { get; } = [];

        public Dictionary<IntPtr, uint> ReadValues { get; } = [];

        public List<(IntPtr Window, uint Affinity)> SetHistory { get; } = [];

        public bool TryGetDisplayAffinity(
            IntPtr windowHandle,
            out uint affinity,
            out string? errorCode,
            out string? errorMessage)
        {
            ReadCount++;
            ReadHistory.Add(windowHandle);
            affinity = ReadValues.TryGetValue(windowHandle, out uint value)
                ? value
                : CurrentAffinity;
            errorCode = null;
            errorMessage = null;
            return true;
        }

        public bool TrySetDisplayAffinity(
            IntPtr windowHandle,
            uint affinity,
            out string? errorCode,
            out string? errorMessage)
        {
            // 恢复动作写入的是**原始** affinity；用单独的计数控制它失败几次。
            if (affinity == _originalAffinity && _failRestoreWrites > 0)
            {
                _failRestoreWrites--;
                errorCode = "presentation_capture_policy_failed";
                errorMessage = "Rejected by the test adapter.";
                return false;
            }

            SetHistory.Add((windowHandle, affinity));
            CurrentAffinity = affinity;
            errorCode = null;
            errorMessage = null;
            return true;
        }
    }
}
