using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【独立复核 · 批次 A】LM-073 / LM-074 / LM-093 的**独立**钉住用例
/// + 合同 §4.5 :198（恢复精确原值）的守卫。
/// <para>★ 本文件由复核者（verifier）自行编写，**不复用**实现者的测试文件；
/// 结论只以本文件的用例 + 自设计变异的实测结果为准。</para>
/// </summary>
public sealed class IndependentBatchACapturePolicyVerificationTests
{
    // ================================================================
    // LM-073：全部 WDA 取得入口共用同一个闸门
    // ================================================================

    /// <summary>
    /// LM-073 ①：**四个**"要施加 WDA_EXCLUDEFROMCAPTURE"的入口
    /// （Start / TryReconfigure 的 live 分支 / Resume / NotifyOutputChanged）
    /// 在施加之前都必须查**同一个**闸门；命中 ⇒ 拒绝 + 不得对 HWND 施加 0x11。
    /// </summary>
    [Fact]
    public void LM073_四个WDA取得入口都必须过闸门且不得施加排除()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory();
            try
            {
                // ── 入口 1：Start ──
                {
                    RecordingWindowAdapter adapter = new(originalAffinity: 0);
                    using Form form = new();
                    _ = form.Handle;
                    WindowHandleGeneration generations = new();
                    using GpuPresentationHostController host = CreateHost(
                        form, generations, adapter, liveBackgroundRequested: false);

                    CapturePolicyCleanupLedger ledger = CreateLedger(() => generations.Current, _ => true);
                    ledger.Add(NewCleanup(generations.Current, originalAffinity: 0, changed: true));
                    host.CapturePolicyCleanupPendingProbe = ledger.HasPendingForGeneration;
                    host.CapturePolicyCleanupBlockingProbe = ledger.GetBlockingReasonForGeneration;

                    Assert.False(host.Start(
                        WindowCapturePolicy.WdaExcludeFromCapture,
                        out string? startCode,
                        out _));
                    Assert.Equal("gpu_capture_policy_cleanup_pending", startCode);
                    Assert.Empty(adapter.SetHistory);
                }

                // ── 入口 2：TryReconfigure 的 live 分支 ──
                {
                    RecordingWindowAdapter adapter = new(originalAffinity: 0);
                    using Form form = new();
                    _ = form.Handle;
                    WindowHandleGeneration generations = new();
                    using GpuPresentationHostController host = CreateHost(
                        form, generations, adapter, liveBackgroundRequested: false);

                    CapturePolicyCleanupLedger ledger = CreateLedger(() => generations.Current, _ => true);
                    ledger.Add(NewCleanup(generations.Current, originalAffinity: 0, changed: true));
                    host.CapturePolicyCleanupPendingProbe = ledger.HasPendingForGeneration;
                    host.CapturePolicyCleanupBlockingProbe = ledger.GetBlockingReasonForGeneration;

                    Assert.False(host.TryReconfigure(
                        MaterialKind.LiquidGlass,
                        liveBackgroundRequested: true,
                        horizontalLayout: false,
                        renderingConfig: null,
                        userScale: 1f,
                        out string? reconfigureCode,
                        out _));
                    Assert.Equal("gpu_capture_policy_cleanup_pending", reconfigureCode);
                    Assert.Empty(adapter.SetHistory);
                }

                // ── 入口 3：Resume（重新施加排除）──
                {
                    RecordingWindowAdapter adapter = new(originalAffinity: 0);
                    using Form form = new();
                    _ = form.Handle;
                    WindowHandleGeneration generations = new();
                    using GpuPresentationHostController host = CreateHost(
                        form, generations, adapter, liveBackgroundRequested: true);

                    CapturePolicyCleanupLedger ledger = CreateLedger(() => generations.Current, _ => true);
                    ledger.Add(NewCleanup(generations.Current, originalAffinity: 0, changed: true));
                    host.CapturePolicyCleanupPendingProbe = ledger.HasPendingForGeneration;
                    host.CapturePolicyCleanupBlockingProbe = ledger.GetBlockingReasonForGeneration;

                    ForceSuspended(host);
                    host.Resume();

                    Assert.Equal("gpu_capture_policy_cleanup_pending", host.LastErrorCode);
                    Assert.Empty(adapter.SetHistory);
                }

                // ── 入口 4：NotifyOutputChanged（显示器变化后重新施加排除）──
                //   无头环境拿不到真实渲染器 ⇒ 用 IL 结构断言钉住"闸门在施加之前"。
                {
                    MethodInfo method = typeof(GpuPresentationHostController).GetMethod(
                        "NotifyOutputChanged",
                        BindingFlags.Instance | BindingFlags.Public)!;
                    Assert.NotNull(method);

                    IlInspector.Disassembly body = IlInspector.Disassemble(method);
                    int gateAt = CallOffsetOf(body, typeof(GpuPresentationHostController), "TryBlockCapturePolicyOwnership");
                    int applyAt = CallOffsetOf(body, typeof(PresentationSession), "SetCapturePolicy");

                    Assert.True(gateAt >= 0, "NotifyOutputChanged 必须查 WDA 取得闸门（LM-073）。");
                    Assert.True(applyAt >= 0, "NotifyOutputChanged 里找不到 SetCapturePolicy —— 护栏前提被破坏。");
                    Assert.True(gateAt < applyAt, "闸门检查必须位于 SetCapturePolicy 施加**之前**（LM-073）。");
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    /// <summary>
    /// LM-073 ②：**释放** WDA 的分支（TryReconfigure 的 liveBackgroundRequested = false）
    /// **不受**闸门约束 —— 释放只会让 HWND 回到原策略，不会叠加第二个所有者。
    /// </summary>
    [Fact]
    public void LM073_释放WDA的分支不受闸门约束()
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
                using GpuPresentationHostController host = CreateHost(
                    form, generations, adapter, liveBackgroundRequested: false);

                CapturePolicyCleanupLedger ledger = CreateLedger(() => generations.Current, _ => true);
                ledger.Add(NewCleanup(generations.Current, originalAffinity: 0, changed: true));
                host.CapturePolicyCleanupPendingProbe = ledger.HasPendingForGeneration;
                host.CapturePolicyCleanupBlockingProbe = ledger.GetBlockingReasonForGeneration;

                host.TryReconfigure(
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    horizontalLayout: false,
                    renderingConfig: null,
                    userScale: 1f,
                    out string? errorCode,
                    out _);

                Assert.NotEqual("gpu_capture_policy_cleanup_pending", errorCode);
                Assert.NotEqual("gpu_capture_policy_cleanup_unresolved", errorCode);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    /// <summary>
    /// 不得项：**TryReconfigure 的 public 签名未变**（加参数即破坏 API）。
    /// </summary>
    [Fact]
    public void LM073_TryReconfigure的public签名未变()
    {
        MethodInfo method = typeof(GpuPresentationHostController).GetMethod(
            "TryReconfigure",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            new[]
            {
                typeof(MaterialKind),
                typeof(bool),
                typeof(bool),
                typeof(ThemeRenderingConfig),
                typeof(float),
                typeof(string).MakeByRefType(),
                typeof(string).MakeByRefType(),
                typeof(LiteMonitor.src.Core.Theme),
            },
            null);

        Assert.NotNull(method);
        Assert.Equal(8, method!.GetParameters().Length);
        Assert.True(method.IsPublic, "TryReconfigure 必须仍是 public。");
    }

    // ================================================================
    // LM-074：同代次「未达成清理目标」仍占用闸门 + 明确恢复
    // ================================================================

    /// <summary>
    /// LM-074 ①：同代次存在 <c>Failed</c>（且 <c>AffinityChangedBySession == true</c>）
    /// ⇒ 新的 WDA 取得**被拒绝**，错误码 = <c>gpu_capture_policy_cleanup_unresolved</c>，
    /// 且与 <c>gpu_capture_policy_cleanup_pending</c> **可区分**；HWND 上不得被叠加 0x11。
    /// </summary>
    [Fact]
    public void LM074_同代次耗尽的未解决责任仍占用闸门且错误码可区分()
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

                long current = generations.Current;
                List<WindowCapturePolicyCleanup> restored = [];
                CapturePolicyCleanupLedger ledger = new(
                    () => current,
                    _ => true,
                    cleanup =>
                    {
                        restored.Add(cleanup);
                        return new CapturePolicyRestoreResult(false, "restore_failed", "合成失败");
                    });

                // 同代次、非终态 ⇒ 先占用该代次（pending）。
                WindowCapturePolicyCleanup entry = NewCleanup(current, originalAffinity: 0, changed: true);
                Assert.True(ledger.Add(entry));

                DateTimeOffset now = DateTimeOffset.UtcNow;
                for (int i = 0; i < 8 && !entry.IsTerminal; i++)
                {
                    now = now.AddSeconds(5);
                    ledger.Pump(now);
                }

                // 前置：预算耗尽 ⇒ 终态 Failed + exhausted。
                Assert.True(entry.IsTerminal, "前置条件：该责任应当已耗尽预算。");
                Assert.Equal(CapturePolicyCleanupState.Failed, entry.State);
                Assert.Equal("exhausted", entry.TerminalReason);

                // ★ 判据：同代次仍然被占用，且原因码是 unresolved（与 pending 可区分）。
                Assert.Equal("gpu_capture_policy_cleanup_unresolved", ledger.GetBlockingReasonForGeneration(current));

                using GpuPresentationHostController host = CreateHost(
                    form, generations, adapter, liveBackgroundRequested: false);
                host.CapturePolicyCleanupPendingProbe = ledger.HasPendingForGeneration;
                host.CapturePolicyCleanupBlockingProbe = ledger.GetBlockingReasonForGeneration;

                Assert.False(host.Start(
                    WindowCapturePolicy.WdaExcludeFromCapture,
                    out string? errorCode,
                    out _));
                Assert.Equal("gpu_capture_policy_cleanup_unresolved", errorCode);
                Assert.NotEqual("gpu_capture_policy_cleanup_pending", errorCode);
                Assert.Empty(adapter.SetHistory);

                // ★ pending 与 unresolved 两个码必须不同（否则无法区分两种故障）。
                Assert.NotEqual(
                    CapturePolicyCleanupLedger.PendingBlockingErrorCode,
                    CapturePolicyCleanupLedger.UnresolvedBlockingErrorCode);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    /// <summary>
    /// LM-074 ②：<c>Failed</c> 但 <c>AffinityChangedBySession == false</c>
    /// ⇒ 本程序没改过 affinity ⇒ 没有残留 ⇒ **不占用**该代次。
    /// </summary>
    [Fact]
    public void LM074_未改过affinity的失败条目不占用该代次()
    {
        long current = 7;
        CapturePolicyCleanupLedger ledger = CreateLedger(() => current, _ => true);

        WindowCapturePolicyCleanup entry = NewCleanup(current, originalAffinity: 0, changed: false);
        Assert.True(ledger.Add(entry));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 8 && !entry.IsTerminal; i++)
        {
            now = now.AddSeconds(5);
            ledger.Pump(now);
        }

        Assert.True(entry.IsTerminal, "前置条件：该责任应当已终结。");
        Assert.Equal(CapturePolicyCleanupState.Completed, entry.State);

        // ★ 没改过 affinity ⇒ 不残留 ⇒ 不占用。
        Assert.Null(ledger.GetBlockingReasonForGeneration(current));
        Assert.False(ledger.HasUnresolvedForGeneration(current));
    }

    /// <summary>
    /// LM-074 ③：**不同代次** ⇒ 不受阻（合同 §6.4 / §7.4：跨代次不受同代次闸门约束）。
    /// </summary>
    [Fact]
    public void LM074_不同代次不受闸门阻挠()
    {
        long current = 5;
        CapturePolicyCleanupLedger ledger = CreateLedger(() => current, _ => true);

        WindowCapturePolicyCleanup entry = NewCleanup(current, originalAffinity: 0, changed: true);
        Assert.True(ledger.Add(entry));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 8 && !entry.IsTerminal; i++)
        {
            now = now.AddSeconds(5);
            ledger.Pump(now);
        }

        Assert.True(entry.IsTerminal);
        Assert.Equal("gpu_capture_policy_cleanup_unresolved", ledger.GetBlockingReasonForGeneration(current));

        // ★ 代次推进 ⇒ 旧代次的未解决清理不再阻挠新代次。
        current = 6;
        Assert.Null(ledger.GetBlockingReasonForGeneration(6));
    }

    /// <summary>
    /// LM-074 ④：**明确恢复** ⇒ 新责任携带**精确原值**、同一 <c>WindowHandle</c>、
    /// 同一 <c>Generation</c>、<c>Attempts</c> 归零；旧责任**保持 Failed + exhausted**
    /// （不得被改成"成功"），只被打上"已被接管"标记。
    /// </summary>
    [Fact]
    public void LM074_明确恢复携带精确原值同一句柄同一代次且尝试归零()
    {
        long current = 11;
        List<WindowCapturePolicyCleanup> restored = [];
        CapturePolicyCleanupLedger ledger = new(
            () => current,
            _ => true,
            cleanup =>
            {
                restored.Add(cleanup);
                return new CapturePolicyRestoreResult(false, "restore_failed", "合成失败");
            });

        WindowCapturePolicyCleanup failed = NewCleanup(current, originalAffinity: 0x22, changed: true);
        Assert.True(ledger.Add(failed));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 8 && !failed.IsTerminal; i++)
        {
            now = now.AddSeconds(5);
            ledger.Pump(now);
        }

        Assert.True(failed.IsTerminal);
        Assert.Equal(CapturePolicyCleanupState.Failed, failed.State);
        Assert.Equal("exhausted", failed.TerminalReason);
        int failedAttemptsBefore = failed.Attempts;

        // ★ 明确恢复（只接受 unresolved 拒绝码）。
        bool scheduled = ledger.TryScheduleExplicitRecoveryForRejectedCode(
            "gpu_capture_policy_cleanup_unresolved",
            current,
            now.AddSeconds(1),
            out string? rearmError);
        Assert.True(scheduled, $"明确恢复必须被安排（error={rearmError}）。");

        // ★ 新责任：精确原值 / 同一句柄 / 同一代次 / 尝试归零。
        WindowCapturePolicyCleanup rebuilt = Assert.Single(
            ledger.Entries.Where(e => !ReferenceEquals(e, failed)));
        Assert.Equal(0x22u, rebuilt.OriginalAffinity);
        Assert.Equal(failed.WindowHandle, rebuilt.WindowHandle);
        Assert.Equal(failed.Generation, rebuilt.Generation);
        Assert.Equal(current, rebuilt.Generation);
        Assert.Equal(0, rebuilt.Attempts);
        Assert.True(rebuilt.AffinityChangedBySession);
        Assert.False(rebuilt.IsTerminal);

        // ★ 旧责任：保持 Failed + exhausted（不得被改成"成功"），只被标记为"已被接管"。
        Assert.Equal(CapturePolicyCleanupState.Failed, failed.State);
        Assert.Equal("exhausted", failed.TerminalReason);
        Assert.Equal(failedAttemptsBefore, failed.Attempts);

        // ★ 接管后同代次的闸门由**新责任**把关（原因 = pending）。
        Assert.Equal(
            CapturePolicyCleanupLedger.PendingBlockingErrorCode,
            ledger.GetBlockingReasonForGeneration(current));
    }

    /// <summary>
    /// LM-074 ⑤：明确恢复之后，**旧责任不再重试、不再触碰 HWND**（合同 §6.3 终态不可回退）；
    /// 新责任按合同 §6.4 的退避重试**真的**把**精确原值**写回去。
    /// </summary>
    [Fact]
    public void LM074_明确恢复后旧责任不再重试而新责任写回精确原值()
    {
        long current = 21;
        List<WindowCapturePolicyCleanup> restored = [];
        bool restoreSucceeds = false;
        CapturePolicyCleanupLedger ledger = new(
            () => current,
            _ => true,
            cleanup =>
            {
                restored.Add(cleanup);
                return restoreSucceeds
                    ? new CapturePolicyRestoreResult(true, null, null)
                    : new CapturePolicyRestoreResult(false, "restore_failed", "合成失败");
            });

        WindowCapturePolicyCleanup failed = NewCleanup(current, originalAffinity: 0x33, changed: true);
        Assert.True(ledger.Add(failed));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 8 && !failed.IsTerminal; i++)
        {
            now = now.AddSeconds(5);
            ledger.Pump(now);
        }

        Assert.True(failed.IsTerminal);

        Assert.True(ledger.TryScheduleExplicitRecoveryForRejectedCode(
            "gpu_capture_policy_cleanup_unresolved",
            current,
            now.AddSeconds(1),
            out _));

        // 接管完成的**那一刻**起，旧责任就不得再被重试。
        int callsAtRearm = restored.Count;

        // 恢复成功 ⇒ 新责任第一次 Pump 就应把精确原值写回。
        restoreSucceeds = true;
        now = now.AddSeconds(10);
        ledger.Pump(now);

        WindowCapturePolicyCleanup rebuilt = Assert.Single(
            ledger.Entries.Where(e => !ReferenceEquals(e, failed)));

        // ★ 接管之后的恢复尝试**只**针对新责任（旧责任不得再触碰 HWND，合同 §6.3）。
        List<WindowCapturePolicyCleanup> callsAfterRearm = restored.Skip(callsAtRearm).ToList();
        Assert.Single(callsAfterRearm);
        Assert.DoesNotContain(failed, callsAfterRearm);
        Assert.Same(rebuilt, callsAfterRearm[0]);

        Assert.Equal(CapturePolicyCleanupState.Completed, rebuilt.State);
        Assert.Equal(0x33u, rebuilt.OriginalAffinity);

        // ★ 重建的责任拥有**自己的**预算（合同 §6.4：尝试 1、2 属于宿主停止流程，
        //   那两次已由旧责任消耗掉 ⇒ 重建责任不得再补记）。
        //   成功恢复**不计**失败次数 ⇒ 重建责任不得凭空多出 2 次"幽灵尝试"。
        Assert.Equal(0, rebuilt.Attempts);

        // ★ 写回成功 ⇒ 该代次不再被占用。
        Assert.Null(ledger.GetBlockingReasonForGeneration(current));
    }

    /// <summary>
    /// LM-074 ⑥：**只有 unresolved 拒绝码**才安排明确恢复；
    /// pending（还在正常重试）与其它任何失败码**一律不触发**。
    /// </summary>
    [Fact]
    public void LM074_只有未解决拒绝码才安排明确恢复()
    {
        long current = 31;
        CapturePolicyCleanupLedger ledger = CreateLedger(() => current, _ => true);

        WindowCapturePolicyCleanup failed = NewCleanup(current, originalAffinity: 0x44, changed: true);
        Assert.True(ledger.Add(failed));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 8 && !failed.IsTerminal; i++)
        {
            now = now.AddSeconds(5);
            ledger.Pump(now);
        }

        Assert.True(failed.IsTerminal);

        Assert.False(ledger.TryScheduleExplicitRecoveryForRejectedCode(
            "gpu_capture_policy_cleanup_pending", current, now, out _));
        Assert.False(ledger.TryScheduleExplicitRecoveryForRejectedCode(
            "gpu_presentation_not_reconfigurable", current, now, out _));
        Assert.False(ledger.TryScheduleExplicitRecoveryForRejectedCode(null, current, now, out _));
        Assert.False(ledger.TryScheduleExplicitRecoveryForRejectedCode(
            string.Empty, current, now, out _));

        // 上面四次都不得新建条目。
        Assert.Single(ledger.Entries);

        Assert.True(ledger.TryScheduleExplicitRecoveryForRejectedCode(
            "gpu_capture_policy_cleanup_unresolved", current, now, out _));
        Assert.Equal(2, ledger.Entries.Count);
    }

    /// <summary>
    /// LM-074 ⑦：同代次已有**非终态**责任时**不得**再重建（INV-4）。
    /// </summary>
    [Fact]
    public void LM074_同代次已有待处理责任时不得再重建()
    {
        long current = 41;
        CapturePolicyCleanupLedger ledger = CreateLedger(() => current, _ => true);

        // 一条非终态（Pending）责任。
        WindowCapturePolicyCleanup pending = NewCleanup(current, originalAffinity: 0x55, changed: true);
        Assert.True(ledger.Add(pending));

        bool scheduled = ledger.TryScheduleExplicitRecoveryForRejectedCode(
            "gpu_capture_policy_cleanup_unresolved",
            current,
            DateTimeOffset.UtcNow,
            out string? rearmError);

        Assert.False(scheduled, "同代次已有非终态责任时不得再重建（INV-4）。");
        Assert.NotNull(rearmError);
        Assert.Contains(rearmError, new[]
        {
            CapturePolicyCleanupLedger.AlreadyPendingErrorCode,
            CapturePolicyCleanupLedger.NoUnresolvedEntryErrorCode,
        });
        Assert.Single(ledger.Entries);
    }

    // ================================================================
    // LM-093：同一实例登记幂等
    // ================================================================

    /// <summary>
    /// LM-093 ①：同一条记录登记**两次** ⇒ 第二次返回 true、<c>Entries</c> 数量不变、
    /// 且**不**产生 <c>RegistrationRejectedCode</c>（也不占用告警通道）。
    /// </summary>
    [Fact]
    public void LM093_同一条记录登记两次是幂等的且不产生被拒诊断()
    {
        long current = 51;
        CapturePolicyCleanupLedger ledger = CreateLedger(() => current, _ => true);

        WindowCapturePolicyCleanup entry = NewCleanup(current, originalAffinity: 0, changed: true);

        Assert.True(ledger.Add(entry));
        Assert.Equal(1, ledger.Entries.Count);

        Assert.True(ledger.Add(entry), "同一实例二次登记必须幂等返回 true（已在册）。");
        Assert.Equal(1, ledger.Entries.Count);
        Assert.Null(ledger.RegistrationRejectedCode);
        Assert.NotEqual(
            CapturePolicyCleanupLedger.RegistrationRejectedWarningCode,
            ledger.CleanupWarningCode);
    }

    /// <summary>
    /// LM-093 ②：同代次登记**不同**的一条 ⇒ 返回 false **且**
    /// <c>RegistrationRejectedCode == "gpu_capture_policy_cleanup_registration_rejected"</c>。
    /// </summary>
    [Fact]
    public void LM093_同代次登记不同条目才是真被拒并留下诊断()
    {
        long current = 61;
        CapturePolicyCleanupLedger ledger = CreateLedger(() => current, _ => true);

        Assert.True(ledger.Add(NewCleanup(current, originalAffinity: 0, changed: true)));

        bool second = ledger.Add(NewCleanup(current, originalAffinity: 0, changed: true));

        Assert.False(second, "同代次已有非终态责任时，第二条不同记录必须被拒（INV-4）。");
        Assert.Equal(
            "gpu_capture_policy_cleanup_registration_rejected",
            ledger.RegistrationRejectedCode);
        Assert.Equal(
            CapturePolicyCleanupLedger.RegistrationRejectedWarningCode,
            ledger.CleanupWarningCode);
        Assert.Equal(1, ledger.Entries.Count);
    }

    /// <summary>
    /// LM-093 ③：登记**终态**责任必须被拒（合同 §6.3 终态不可回退 ⇒ 不得再登记）。
    /// </summary>
    [Fact]
    public void LM093_登记终态责任必须被拒()
    {
        long current = 71;
        CapturePolicyCleanupLedger ledger = CreateLedger(() => current, _ => true);

        WindowCapturePolicyCleanup terminal = NewCleanup(current, originalAffinity: 0, changed: true);
        Assert.True(terminal.TryMarkCompleted("restored"));

        Assert.False(ledger.Add(terminal), "终态责任不得再被登记（合同 §6.3）。");
        Assert.Empty(ledger.Entries);
    }

    /// <summary>
    /// LM-093 ④：**正常移交**（同一条责任被登记两次）⇒ 告警通道**不得**出现"登记被拒"码。
    /// <para>★ 合同 §5.3 规则 2：告警通道（<c>CleanupWarningCode</c>）与错误通道互不覆盖，
    /// 且不得被正常移交污染。</para>
    /// </summary>
    [Fact]
    public void LM093_正常移交的双路登记不得污染告警通道()
    {
        long current = 81;
        CapturePolicyCleanupLedger ledger = CreateLedger(() => current, _ => true);

        // 宿主移交的**同一实例**：事件回调 + LastCapturePolicyCleanup 兜底两条路径。
        WindowCapturePolicyCleanup handedOff = NewCleanup(current, originalAffinity: 0x66, changed: true);

        Assert.True(ledger.Add(handedOff));   // 事件回调路径
        Assert.True(ledger.Add(handedOff));   // 兜底路径（同一实例）

        Assert.Null(ledger.RegistrationRejectedCode);
        Assert.NotEqual(
            "gpu_capture_policy_cleanup_registration_rejected",
            ledger.CleanupWarningCode);
        Assert.Single(ledger.Entries);
    }

    /// <summary>
    /// LM-093 ⑤：清理侧告警**只**写告警通道，**不得**覆盖清理根因 / 呈现根因
    /// （合同 §5.3 规则 2）。
    /// </summary>
    [Fact]
    public void LM093_清理告警只写告警通道不得覆盖清理根因()
    {
        long current = 91;
        CapturePolicyCleanupLedger ledger = new(
            () => current,
            _ => true,
            _ => new CapturePolicyRestoreResult(false, "restore_failed", "合成失败"));

        // 先制造一条"清理根因"：同代次的责任失败一次（仍是 Pending）。
        WindowCapturePolicyCleanup entry = NewCleanup(current, originalAffinity: 0, changed: true);
        Assert.True(ledger.Add(entry));
        DateTimeOffset now = DateTimeOffset.UtcNow.AddSeconds(5);
        ledger.Pump(now);

        string? rootCauseBefore = ledger.CleanupErrorCode;
        Assert.Equal("restore_failed", rootCauseBefore);

        // 再登记同代次的**另一条**（INV-4：同代次已有非终态责任 ⇒ 必须被拒）
        // ⇒ 只允许写告警通道。
        Assert.False(ledger.Add(NewCleanup(current, originalAffinity: 0, changed: true)));

        Assert.Equal(
            "gpu_capture_policy_cleanup_registration_rejected",
            ledger.CleanupWarningCode);

        // ★ 告警通道不得覆盖清理根因（合同 §5.3 规则 2）。
        Assert.Equal(rootCauseBefore, ledger.CleanupErrorCode);
    }

    /// <summary>
    /// LM-093 ⑥：**协调器层**的清理告警只写告警通道，
    /// **不得**覆盖呈现根因（<c>FailureCode</c>）与清理根因（<c>CleanupErrorCode</c>）
    /// （合同 §5.3 规则 2）。
    /// </summary>
    [Fact]
    public void LM093_协调器层的清理告警不得覆盖呈现根因与清理根因()
    {
        var coordinator = new RenderingTransitionCoordinator(new WindowHandleGeneration());

        coordinator.RecordPresentationRootCause("gpu_presentation_start_failed");
        coordinator.RecordCleanupState(
            CapturePolicyCleanupState.Failed,
            "presentation_capture_policy_restore_failed");
        coordinator.RecordCleanupWarning(
            "gpu_capture_policy_cleanup_registration_rejected");

        RenderingTransitionState state = coordinator.State;
        Assert.Equal("gpu_presentation_start_failed", state.FailureCode);
        Assert.Equal("presentation_capture_policy_restore_failed", state.CleanupErrorCode);
        Assert.Equal(
            "gpu_capture_policy_cleanup_registration_rejected",
            state.CleanupWarningCode);
    }

    // ================================================================
    // 合同 §4.5 :198 守卫：恢复必须是**精确原值**，不得固定写成 WDA_NONE
    // ================================================================

    /// <summary>
    /// 合同 §4.5 :198 + §7.3 R3：会话停止时必须把**精确原值**写回"读到它的那个句柄"，
    /// **不得**固定写成 WDA_NONE，也不得把残留的 0x11 当成原值。
    /// </summary>
    [Fact]
    public void 合同_会话停止必须把精确原值写回而不是WDA_NONE()
    {
        RecordingWindowAdapter adapter = new(originalAffinity: 0x22);
        var session = new PresentationSession(adapter);

        Assert.True(session.Start(
            new IntPtr(0x1000),
            new RenderGenerations(1, 1, 1, 1, 1),
            WindowCapturePolicy.WdaExcludeFromCapture,
            out string? errorCode,
            out _), $"Start 失败：{errorCode}");

        // 施加了排除 ⇒ 适配器必须看到 0x11。
        Assert.Contains(adapter.SetHistory, entry => entry.Affinity == 0x11u);

        Assert.True(session.BeginStop(out _));
        Assert.True(session.CompleteStop(out _, out _));

        // ★ 写回的是**精确原值 0x22**，不是 WDA_NONE(0)，也不是残留的 0x11。
        Assert.Contains(adapter.SetHistory, entry => entry.Affinity == 0x22u);
        Assert.DoesNotContain(adapter.SetHistory, entry => entry.Affinity == 0u);
    }

    // ================================================================
    // 辅助
    // ================================================================

    private static string TemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "LiteMonitor.Tests",
            "lm-verify-batchA-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>在 STA 线程上执行（WinForms 控件需要 STA）。</summary>
    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
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

    private static GpuPresentationHostController CreateHost(
        Form form,
        WindowHandleGeneration generations,
        IPresentationWindowAdapter adapter,
        bool liveBackgroundRequested)
    {
        string directory = TemporaryDirectory();
        return new GpuPresentationHostController(
            form,
            MaterialKind.LiquidGlass,
            liveBackgroundRequested: liveBackgroundRequested,
            startupGuard: new GpuStartupGuard(directory, @"C:\apps\A"),
            windowAdapter: adapter,
            handleGeneration: generations);
    }

    /// <summary>造一条清理责任。</summary>
    private static WindowCapturePolicyCleanup NewCleanup(
        long generation,
        uint originalAffinity,
        bool changed) =>
        new()
        {
            Generation = generation,
            WindowHandle = new IntPtr(0x1234),
            OriginalAffinity = originalAffinity,
            AffinityChangedBySession = changed,
        };

    /// <summary>造一个"恢复总是失败"的 ledger。</summary>
    private static CapturePolicyCleanupLedger CreateLedger(
        Func<long> currentGeneration,
        Func<IntPtr, bool> isWindowAlive) =>
        new(
            currentGeneration,
            isWindowAlive,
            _ => new CapturePolicyRestoreResult(false, "restore_failed", "合成失败"));

    /// <summary>
    /// 把宿主置于"已挂起"状态（生产的 Suspend 需要真实 GPU 渲染器，
    /// 确定性单测里拿不到 ⇒ 只为驱动 Resume 的闸门分支）。
    /// </summary>
    private static void ForceSuspended(GpuPresentationHostController host)
    {
        FieldInfo field = typeof(GpuPresentationHostController).GetField(
            "_suspended",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(host, true);
    }

    private static int CallOffsetOf(
        IlInspector.Disassembly body,
        Type declaringType,
        string name)
    {
        foreach (IlInspector.IlInstruction instruction in body.Instructions)
        {
            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
            {
                continue;
            }

            if (instruction.Target is not MethodInfo called)
            {
                continue;
            }

            if (called.DeclaringType == declaringType && called.Name == name)
            {
                return instruction.Offset;
            }
        }

        return -1;
    }

    /// <summary>记录 affinity 读写的窗口适配器。</summary>
    private sealed class RecordingWindowAdapter : IPresentationWindowAdapter
    {
        private uint _currentAffinity;

        public RecordingWindowAdapter(uint originalAffinity = 0)
        {
            _currentAffinity = originalAffinity;
        }

        public List<(IntPtr Window, uint Affinity)> SetHistory { get; } = [];

        public bool TryGetDisplayAffinity(
            IntPtr windowHandle,
            out uint affinity,
            out string? errorCode,
            out string? errorMessage)
        {
            affinity = _currentAffinity;
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
            _currentAffinity = affinity;
            errorCode = null;
            errorMessage = null;
            return true;
        }
    }
}
