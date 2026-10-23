using System.Runtime.ExceptionServices;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【LM-074】恢复**耗尽**后仍允许在同一 HWND 取得新 WDA 所有权（报告 R10）。
/// <para>根因：ledger 的重试预算耗尽 ⇒ 条目被置为 <c>Failed</c> ⇒ <c>IsTerminal == true</c> ⇒
/// 既有判据 <c>HasPendingForGeneration</c> 随即返回 false ⇒ 闸门放行 ⇒ 新会话把**残留**的
/// 0x11 当成"外部原值"，旧记录又不再重试 ⇒ 排除状态一直残留到 HWND 销毁。</para>
/// <para>★ 契约张力裁决（合同 §6.2 INV-6 / §4.5 :202，支配条款）："重试耗尽"只是
/// <b>停止重试</b>，<b>不等于</b> <b>安全恢复</b>；同代次的清理责任**仍被占用** ⇒
/// 新的 affinity 所有者必须被拒绝，直到"明确恢复"或"受控重建 / 代次推进"。</para>
/// </summary>
public sealed class CapturePolicyCleanupUnresolvedGateTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    // ================================================================
    // 1. 同代次耗尽 ⇒ 新的 WDA 取得被拒绝，且错误码与 "pending" 可区分
    // ================================================================

    [Fact]
    public void LM074_同代次恢复耗尽后新的WDA取得必须被拒绝且错误码可区分()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory();
            try
            {
                RecordingWindowAdapter adapter = new(originalAffinity: 0);
                using Form form = new();
                _ = form.Handle;
                WindowHandleGeneration generations = new();
                using GpuPresentationHostController host = new(
                    form,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: new GpuStartupGuard(directory, @"C:\apps\A"),
                    windowAdapter: adapter,
                    handleGeneration: generations);

                RecordingRestoreProbe probe = new() { Succeed = false };
                CapturePolicyCleanupLedger ledger = CreateLedger(generations, probe);
                ledger.Add(new WindowCapturePolicyCleanup
                {
                    Generation = generations.Current,
                    WindowHandle = new IntPtr(0x1234),
                    OriginalAffinity = 0,
                    AffinityChangedBySession = true,
                });
                Exhaust(ledger, T0);

                // 预算耗尽 ⇒ 终态 Failed（合同 §6.3）。
                Assert.Equal(CapturePolicyCleanupState.Failed, ledger.Entries[0].State);
                Assert.Equal("exhausted", ledger.Entries[0].TerminalReason);

                host.CapturePolicyCleanupPendingProbe = ledger.HasPendingForGeneration;
                host.CapturePolicyCleanupBlockingProbe = ledger.GetBlockingReasonForGeneration;

                Assert.False(host.Start(
                    WindowCapturePolicy.WdaExcludeFromCapture,
                    out string? errorCode,
                    out _));

                // ★ 错误码必须与"还在重试"可区分。
                Assert.Equal("gpu_capture_policy_cleanup_unresolved", errorCode);
                Assert.NotEqual("gpu_capture_policy_cleanup_pending", errorCode);

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
    // 2. 不同代次 ⇒ 不受阻
    // ================================================================

    [Fact]
    public void LM074_不同代次不受闸门阻挠()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory();
            try
            {
                RecordingWindowAdapter adapter = new(originalAffinity: 0);
                using Form form = new();
                _ = form.Handle;
                WindowHandleGeneration generations = new();
                using GpuPresentationHostController host = new(
                    form,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: new GpuStartupGuard(directory, @"C:\apps\A"),
                    windowAdapter: adapter,
                    handleGeneration: generations);

                RecordingRestoreProbe probe = new() { Succeed = false };
                CapturePolicyCleanupLedger ledger = CreateLedger(generations, probe);

                // 旧代次耗尽（未解决）。
                long staleGeneration = generations.Current;
                ledger.Add(new WindowCapturePolicyCleanup
                {
                    Generation = staleGeneration,
                    WindowHandle = new IntPtr(0x1234),
                    OriginalAffinity = 0,
                    AffinityChangedBySession = true,
                });
                Exhaust(ledger, T0);
                Assert.Equal("gpu_capture_policy_cleanup_unresolved",
                    ledger.GetBlockingReasonForGeneration(staleGeneration));

                // ★ 新代次（受控重建 / 代次推进）⇒ 不受阻。
                long advanced = generations.Advance();
                Assert.Null(ledger.GetBlockingReasonForGeneration(advanced));
                Assert.False(ledger.HasUnresolvedForGeneration(advanced));

                host.CapturePolicyCleanupPendingProbe = ledger.HasPendingForGeneration;
                host.CapturePolicyCleanupBlockingProbe = ledger.GetBlockingReasonForGeneration;

                // 闸门没有拦下 ⇒ 宿主继续往下走（此处因没有 GPU 渲染器而失败，
                // 但失败原因**不得**是闸门码）。
                host.TryReconfigure(
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: true,
                    horizontalLayout: false,
                    renderingConfig: null,
                    userScale: 1f,
                    out string? errorCode,
                    out _);

                Assert.NotEqual("gpu_capture_policy_cleanup_unresolved", errorCode);
                Assert.NotEqual("gpu_capture_policy_cleanup_pending", errorCode);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    // ================================================================
    // 3. 明确恢复 ⇒ 闸门解除 且 原策略被**精确**写回
    // ================================================================

    [Fact]
    public void LM074_明确恢复成功后闸门解除且原策略被精确写回()
    {
        RecordingRestoreProbe probe = new() { Succeed = false };
        WindowHandleGeneration generations = new();
        CapturePolicyCleanupLedger ledger = CreateLedger(generations, probe);

        // 精确原值 = 0x22（**不是**残留的 0x11）。
        ledger.Add(new WindowCapturePolicyCleanup
        {
            Generation = generations.Current,
            WindowHandle = new IntPtr(0x1234),
            OriginalAffinity = 0x22u,
            AffinityChangedBySession = true,
        });
        Exhaust(ledger, T0);
        Assert.Equal("gpu_capture_policy_cleanup_unresolved",
            ledger.GetBlockingReasonForGeneration(generations.Current));

        // ★ 明确恢复（用户显式动作触发）。
        Assert.True(ledger.TryRearmForExplicitRecovery(
            generations.Current,
            T0,
            out string? rearmErrorCode));
        Assert.Null(rearmErrorCode);

        // 新的清理责任已就位 ⇒ 闸门仍然关闭，但原因回到"pending"。
        Assert.Equal("gpu_capture_policy_cleanup_pending",
            ledger.GetBlockingReasonForGeneration(generations.Current));

        // 退避重试驱动 ⇒ 真正把**精确原值**写回 HWND。
        probe.Succeed = true;
        ledger.Pump(T0.AddSeconds(1));

        Assert.NotEmpty(probe.Calls);
        (IntPtr window, uint originalAffinity, int attemptsAtCall) = probe.Calls[^1];
        Assert.Equal(new IntPtr(0x1234), window);
        Assert.Equal(0x22u, originalAffinity);
        Assert.DoesNotContain(probe.Calls, call => call.originalAffinity == 0x11u);

        // ★ 闸门解除。
        Assert.Null(ledger.GetBlockingReasonForGeneration(generations.Current));
        Assert.False(ledger.HasUnresolvedForGeneration(generations.Current));
    }

    // ================================================================
    // 4. 明确恢复后，旧 Failed 条目不再重试（合同 §6.3）
    // ================================================================

    [Fact]
    public void LM074_明确恢复后旧的失败条目不再重试()
    {
        RecordingRestoreProbe probe = new() { Succeed = false };
        WindowHandleGeneration generations = new();
        CapturePolicyCleanupLedger ledger = CreateLedger(generations, probe);

        ledger.Add(new WindowCapturePolicyCleanup
        {
            Generation = generations.Current,
            WindowHandle = new IntPtr(0x1234),
            OriginalAffinity = 0x22u,
            AffinityChangedBySession = true,
        });
        Exhaust(ledger, T0);

        WindowCapturePolicyCleanup staleEntry = ledger.Entries[0];
        int attemptsBefore = staleEntry.Attempts;
        int callsBefore = probe.Calls.Count;

        Assert.True(ledger.TryRearmForExplicitRecovery(
            generations.Current,
            T0,
            out _));

        // ★ 旧条目**如实记账**：仍然是 Failed + exhausted（原策略始终没被写回），
        //   只是"已被明确恢复接管" ⇒ 它不再占用该代次的闸门。
        Assert.True(staleEntry.IsTerminal);
        Assert.Equal(CapturePolicyCleanupState.Failed, staleEntry.State);
        Assert.Equal("exhausted", staleEntry.TerminalReason);
        Assert.True(staleEntry.ReclaimedByExplicitRecovery);

        probe.Succeed = true;
        ledger.Pump(T0.AddSeconds(1));
        ledger.Pump(T0.AddSeconds(2));

        // ★ 旧条目不再重试：尝试次数不再增长。
        Assert.Equal(attemptsBefore, staleEntry.Attempts);
        Assert.True(staleEntry.IsTerminal);
        Assert.Equal(CapturePolicyCleanupState.Failed, staleEntry.State);

        // 恢复动作只发生在**新的**责任上（旧条目不得再触碰 HWND）。
        Assert.All(probe.Calls.Skip(callsBefore), call => Assert.Equal(0, call.attemptsAtCall));
    }

    // ================================================================
    // 4b. 明确恢复后闸门解除，且旧条目的**失败记账未被改写**
    // ================================================================

    [Fact]
    public void LM074_明确恢复成功后闸门解除且旧条目的失败记账未被改写()
    {
        RecordingRestoreProbe probe = new() { Succeed = false };
        WindowHandleGeneration generations = new();
        CapturePolicyCleanupLedger ledger = CreateLedger(generations, probe);

        ledger.Add(new WindowCapturePolicyCleanup
        {
            Generation = generations.Current,
            WindowHandle = new IntPtr(0x1234),
            OriginalAffinity = 0x22u,
            AffinityChangedBySession = true,
        });
        Exhaust(ledger, T0);

        WindowCapturePolicyCleanup staleEntry = ledger.Entries[0];
        Assert.Equal(CapturePolicyCleanupState.Failed, staleEntry.State);

        Assert.True(ledger.TryRearmForExplicitRecovery(
            generations.Current,
            T0,
            out _));

        // 重建责任接管期间闸门仍关闭（由**新责任**把关）。
        Assert.Equal("gpu_capture_policy_cleanup_pending",
            ledger.GetBlockingReasonForGeneration(generations.Current));

        // 新责任恢复成功 ⇒ 闸门解除。
        probe.Succeed = true;
        ledger.Pump(T0.AddSeconds(1));
        Assert.Null(ledger.GetBlockingReasonForGeneration(generations.Current));
        Assert.False(ledger.HasUnresolvedForGeneration(generations.Current));

        // ★★ 旧条目的**失败记账不得被改写成成功**：它仍然是 Failed + exhausted，
        //    只是打了"已被接管"标记（防止有人再把 Failed 改回 Completed —— 那是不实记账：
        //    原策略始终没有被写回，残留仍在 HWND 上）。
        Assert.Equal(CapturePolicyCleanupState.Failed, staleEntry.State);
        Assert.NotEqual(CapturePolicyCleanupState.Completed, staleEntry.State);
        Assert.Equal("exhausted", staleEntry.TerminalReason);
        Assert.True(staleEntry.ReclaimedByExplicitRecovery);
    }

    // ================================================================
    // 4c. 明确恢复必须**先于**宿主停止移交；被拒的移交必须留痕
    // ================================================================

    [Fact]
    public void LM074_明确恢复先于宿主停止移交且被拒的移交有留痕()
    {
        RecordingRestoreProbe probe = new() { Succeed = false };
        WindowHandleGeneration generations = new();
        CapturePolicyCleanupLedger ledger = CreateLedger(generations, probe);
        long generation = generations.Current;

        // ① 同代次已有 Failed + unresolved（精确原值 0x22）。
        ledger.Add(new WindowCapturePolicyCleanup
        {
            Generation = generation,
            WindowHandle = new IntPtr(0x1234),
            OriginalAffinity = 0x22u,
            AffinityChangedBySession = true,
        });
        Exhaust(ledger, T0);
        Assert.Equal("gpu_capture_policy_cleanup_unresolved",
            ledger.GetBlockingReasonForGeneration(generation));

        // ② **先**安排明确恢复 ⇒ 重建责任携带**精确原值**（不是残留值）。
        Assert.True(ledger.TryRearmForExplicitRecovery(generation, T0, out _));
        WindowCapturePolicyCleanup rearmed = ledger.Entries[^1];
        Assert.Equal(0x22u, rearmed.OriginalAffinity);
        Assert.NotEqual(0x11u, rearmed.OriginalAffinity);

        // ③ **随后**宿主停止移交一条同代次记录（它误采了残留 0x11 当原值）
        //    ⇒ 被 INV-4 拒绝（同代次已有非终态责任）。
        bool handedOff = ledger.Add(new WindowCapturePolicyCleanup
        {
            Generation = generation,
            WindowHandle = new IntPtr(0x1234),
            OriginalAffinity = 0x11u,
            AffinityChangedBySession = true,
        });
        Assert.False(handedOff);

        // ③' 被拒的移交**必须留痕**（不得静默丢弃）。
        Assert.Equal(
            "gpu_capture_policy_cleanup_registration_rejected",
            ledger.RegistrationRejectedCode);
        Assert.Equal(
            "gpu_capture_policy_cleanup_registration_rejected",
            ledger.CleanupWarningCode);

        // ④ 后续写回的是**精确原值**（0x22），绝不是残留值（0x11）。
        probe.Succeed = true;
        ledger.Pump(T0.AddSeconds(1));
        Assert.NotEmpty(probe.Calls);
        Assert.Equal(0x22u, probe.Calls[^1].originalAffinity);
        Assert.DoesNotContain(probe.Calls, call => call.originalAffinity == 0x11u);
    }

    // ================================================================
    // 5. Failed 且本程序没改过 affinity ⇒ 不占用该代次
    // ================================================================

    [Fact]
    public void LM074_未修改过affinity的失败条目不占用该代次()
    {
        WindowHandleGeneration generations = new();
        RecordingRestoreProbe probe = new() { Succeed = false };
        CapturePolicyCleanupLedger ledger = CreateLedger(generations, probe);

        ledger.Add(new WindowCapturePolicyCleanup
        {
            Generation = generations.Current,
            WindowHandle = new IntPtr(0x1234),
            OriginalAffinity = 0,
            AffinityChangedBySession = false,
        });

        // 关闭时终结（合同 §6.4 / INV-5）⇒ Failed + form_closed。
        ledger.FinalizeOnFormClosed(T0);
        Assert.Equal(CapturePolicyCleanupState.Failed, ledger.Entries[0].State);
        Assert.Equal("form_closed", ledger.Entries[0].TerminalReason);

        // ★ 本程序没改过 affinity ⇒ 没有残留 ⇒ 不占用该代次。
        Assert.Null(ledger.GetBlockingReasonForGeneration(generations.Current));
        Assert.False(ledger.HasUnresolvedForGeneration(generations.Current));
    }

    // ================================================================
    // 6. 同代次已有非终态责任时不得再重建（INV-4）
    // ================================================================

    [Fact]
    public void LM074_同代次已有待处理责任时不得再重建()
    {
        RecordingRestoreProbe probe = new() { Succeed = false };
        WindowHandleGeneration generations = new();
        CapturePolicyCleanupLedger ledger = CreateLedger(generations, probe);

        ledger.Add(new WindowCapturePolicyCleanup
        {
            Generation = generations.Current,
            WindowHandle = new IntPtr(0x1234),
            OriginalAffinity = 0x22u,
            AffinityChangedBySession = true,
        });
        Exhaust(ledger, T0);

        // 同代次再登记一条 Pending（旧的已终态 ⇒ Add 允许）。
        Assert.True(ledger.Add(new WindowCapturePolicyCleanup
        {
            Generation = generations.Current,
            WindowHandle = new IntPtr(0x1234),
            OriginalAffinity = 0x22u,
            AffinityChangedBySession = true,
        }));

        // ★ 不得再重建（否则会叠加第二条 Pending，违反 INV-4）。
        Assert.False(ledger.TryRearmForExplicitRecovery(
            generations.Current,
            T0,
            out string? errorCode));
        Assert.NotNull(errorCode);

        int pendingBefore = ledger.Entries.Count(entry => !entry.IsTerminal);
        Assert.False(ledger.TryRearmForExplicitRecovery(
            generations.Current,
            T0,
            out _));
        Assert.Equal(pendingBefore, ledger.Entries.Count(entry => !entry.IsTerminal));
    }

    // ================================================================
    // 7. 没有未解决条目时明确恢复必须被拒绝
    // ================================================================

    [Fact]
    public void LM074_没有未解决条目时明确恢复必须被拒绝()
    {
        WindowHandleGeneration generations = new();
        RecordingRestoreProbe probe = new() { Succeed = false };
        CapturePolicyCleanupLedger ledger = CreateLedger(generations, probe);

        Assert.False(ledger.TryRearmForExplicitRecovery(
            generations.Current,
            T0,
            out string? errorCode));
        Assert.NotNull(errorCode);
        Assert.Empty(ledger.Entries);
    }

    // ================================================================
    // 8. 代次推进 ⇒ 旧代次的未解决清理自动终结（受控重建解除）
    // ================================================================

    [Fact]
    public void LM074_代次推进后旧代次的未解决清理不再阻挠新代次()
    {
        RecordingRestoreProbe probe = new() { Succeed = false };
        WindowHandleGeneration generations = new();
        CapturePolicyCleanupLedger ledger = CreateLedger(generations, probe);

        long staleGeneration = generations.Current;
        ledger.Add(new WindowCapturePolicyCleanup
        {
            Generation = staleGeneration,
            WindowHandle = new IntPtr(0x1234),
            OriginalAffinity = 0x22u,
            AffinityChangedBySession = true,
        });
        Exhaust(ledger, T0);

        long advanced = generations.Advance();

        // 新代次不受阻（残留随旧 HWND 一起消失）。
        Assert.Null(ledger.GetBlockingReasonForGeneration(advanced));
        Assert.False(ledger.HasUnresolvedForGeneration(advanced));

        // 旧代次仍占用，直到 ledger 按合同 §7.4 第 1 条把它终结。
        Assert.Equal("gpu_capture_policy_cleanup_unresolved",
            ledger.GetBlockingReasonForGeneration(staleGeneration));

        // ★ 合同 §6.3"终态不可回退"：已 Failed 的条目**不再被触碰**
        //   （ledger 不会再重试它、也不会再对那个 HWND 做任何 Win32 调用）。
        //   ⇒ 它的状态保持 Failed；解除靠的是"判据按代次匹配"——
        //   新代次的 HWND 是全新的，残留随旧 HWND 一起消失。
        ledger.Pump(T0.AddSeconds(1));
        Assert.Equal(CapturePolicyCleanupState.Failed, ledger.Entries[0].State);
        Assert.Equal("exhausted", ledger.Entries[0].TerminalReason);

        // 新代次始终不受阻。
        Assert.Null(ledger.GetBlockingReasonForGeneration(advanced));
        Assert.False(ledger.HasUnresolvedForGeneration(advanced));
    }

    // ================================================================
    // 辅助
    // ================================================================

    private static string TemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "LiteMonitor.Tests",
            "lm074-gate-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// 把 ledger 推进到"重试预算耗尽"（合同 §6.4 的总上限 6 次）。
    /// </summary>
    private static void Exhaust(CapturePolicyCleanupLedger ledger, DateTimeOffset start)
    {
        DateTimeOffset cursor = start;
        for (int i = 0; i < 20 && !ledger.Entries[0].IsTerminal; i++)
        {
            cursor = cursor.AddSeconds(5);
            ledger.Pump(cursor);
        }
    }

    private static CapturePolicyCleanupLedger CreateLedger(
        WindowHandleGeneration generations,
        RecordingRestoreProbe probe)
    {
        return new CapturePolicyCleanupLedger(
            () => generations.Current,
            _ => true,
            cleanup =>
            {
                probe.Calls.Add((
                    cleanup.WindowHandle,
                    cleanup.OriginalAffinity,
                    cleanup.Attempts));
                return probe.Succeed
                    ? new CapturePolicyRestoreResult(true, null, null)
                    : new CapturePolicyRestoreResult(
                        false,
                        CapturePolicyCleanupLedger.RestoreFailedErrorCode,
                        "boom");
            });
    }

    private sealed class RecordingRestoreProbe
    {
        public bool Succeed { get; set; }

        public List<(IntPtr window, uint originalAffinity, int attemptsAtCall)> Calls { get; } = [];
    }

    private sealed class RecordingWindowAdapter : IPresentationWindowAdapter
    {
        private readonly uint _originalAffinity;

        public RecordingWindowAdapter(uint originalAffinity = 0)
        {
            _originalAffinity = originalAffinity;
            CurrentAffinity = originalAffinity;
        }

        public uint CurrentAffinity { get; set; }

        public List<(IntPtr Window, uint Affinity)> SetHistory { get; } = [];

        public bool TryGetDisplayAffinity(
            IntPtr windowHandle,
            out uint affinity,
            out string? errorCode,
            out string? errorMessage)
        {
            affinity = CurrentAffinity;
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
            SetHistory.Add((windowHandle, affinity));
            CurrentAffinity = affinity;
            errorCode = null;
            errorMessage = null;
            return true;
        }
    }
}
