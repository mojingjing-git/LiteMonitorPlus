using System.Reflection;
using System.Reflection.Emit;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Presentation;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【LM-093】清理责任**登记**的同一实例幂等 —— 消除正常移交上的假"登记被拒"告警。
/// <para>背景：owner B 的移交是<b>双路</b>的（<c>CapturePolicyCleanupRequired</c> 事件回调
/// + <c>LastCapturePolicyCleanup</c> 兜底），两条路径登记的是<b>同一实例</b>。
/// ledger 的 <c>Add</c> 原先只看"同代次已有非终态条目" ⇒ 第二次登记（同一实例）被拒
/// ⇒ LM-074 新增的留痕逻辑把这次<b>正常移交</b>记成"登记被拒"，
/// 而且 <c>CleanupWarningCode</c> 是<b>单槽</b> ⇒ 误报会挤掉
/// <c>gpu_capture_policy_cleanup_superseded</c> 这类真正需要人看的告警。</para>
/// <para>★ 裁决（修在 ledger 层）：<c>Add</c> 的判定顺序改为
/// ① 同一实例已在册 ⇒ 幂等返回 <c>true</c>（不写任何被拒诊断）
/// → ② 终态 ⇒ 拒绝 → ③ INV-4（<b>不同</b>条目撞同代次）⇒ 拒绝 ⇒ <b>这才是真被拒</b> ⇒ 留痕。</para>
/// </summary>
public sealed class CapturePolicyCleanupRegistrationTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    // ================================================================
    // ① 同一实例登记两次 ⇒ 幂等（"已在册"），不新增、不产生被拒诊断
    // ================================================================

    [Fact]
    public void LM093_同一条记录登记两次返回已在册且不产生被拒诊断()
    {
        CapturePolicyCleanupLedger ledger = CreateLedger();
        WindowCapturePolicyCleanup cleanup = NewCleanup(generation: 3);

        Assert.True(ledger.Add(cleanup));
        Assert.Single(ledger.Entries);

        // ★ 同一实例再登记一次 ⇒ 幂等（"已在册"），不新增条目、不产生被拒诊断。
        Assert.True(ledger.Add(cleanup));
        Assert.Single(ledger.Entries);
        Assert.Null(ledger.RegistrationRejectedCode);
        Assert.NotEqual(
            "gpu_capture_policy_cleanup_registration_rejected",
            ledger.CleanupWarningCode);
    }

    // ================================================================
    // ② 同代次登记**不同**条目 ⇒ 真被拒 ⇒ 留痕
    // ================================================================

    [Fact]
    public void LM093_同代次登记不同条目才是真被拒并留下诊断()
    {
        CapturePolicyCleanupLedger ledger = CreateLedger();
        Assert.True(ledger.Add(NewCleanup(generation: 3)));

        // ★ **不同**条目撞同代次 ⇒ 这才是 INV-4 的真冲突 ⇒ 拒绝并留痕。
        Assert.False(ledger.Add(NewCleanup(generation: 3)));
        Assert.Equal(
            "gpu_capture_policy_cleanup_registration_rejected",
            ledger.RegistrationRejectedCode);
        Assert.Equal(
            "gpu_capture_policy_cleanup_registration_rejected",
            ledger.CleanupWarningCode);
        Assert.Single(ledger.Entries);
    }

    // ================================================================
    // ③ 正常移交的双路登记 ⇒ 告警通道**不**出现假告警
    // ================================================================

    [Fact]
    public void LM093_正常移交的双路登记不会在告警通道留下假告警()
    {
        // ── 前提（结构护栏）：owner B 的移交确实是**双路** ——
        //   事件回调（OnGpuCapturePolicyCleanupRequired）+ LastCapturePolicyCleanup 兜底。
        MethodInfo method = typeof(UIController).GetMethod(
            "StopGpuPresentation",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);

        IlInspector.Disassembly body = IlInspector.Disassemble(method!);
        int handoffReadAt = CallOffsetOf(
            body,
            typeof(GpuPresentationHostController),
            "get_LastCapturePolicyCleanup");
        int addAt = CallOffsetOf(body, typeof(CapturePolicyCleanupLedger), "Add");

        Assert.True(
            handoffReadAt >= 0 && addAt >= 0,
            "UIController.StopGpuPresentation 必须把 LastCapturePolicyCleanup（兜底路径）"
            + "登记进 ledger —— 护栏前提被破坏。");
        Assert.True(
            handoffReadAt < addAt,
            "兜底路径必须先取 LastCapturePolicyCleanup 再登记进 ledger（LM-093）。");

        // ── 行为：双路登记的是**同一实例** ⇒ 第二次是幂等 ⇒ 告警通道干净。
        CapturePolicyCleanupLedger ledger = CreateLedger();
        WindowCapturePolicyCleanup cleanup = NewCleanup(generation: 3);

        Assert.True(ledger.Add(cleanup));    // 事件回调路径
        Assert.True(ledger.Add(cleanup));    // LastCapturePolicyCleanup 兜底路径（同一实例）

        Assert.Single(ledger.Entries);
        Assert.Null(ledger.RegistrationRejectedCode);
        Assert.NotEqual(
            "gpu_capture_policy_cleanup_registration_rejected",
            ledger.CleanupWarningCode);
    }

    // ================================================================
    // ④ 登记被拒只写告警通道，**不得**覆盖清理根因 / 呈现根因（合同 §5.3 规则 2）
    // ================================================================

    [Fact]
    public void LM093_登记被拒只写告警通道不得覆盖清理根因与呈现根因()
    {
        // ── ledger 层：CleanupWarningCode 与 CleanupErrorCode 互不覆盖 ──
        CapturePolicyCleanupLedger ledger = CreateLedger();
        ledger.Add(NewCleanup(generation: 3));

        // 先让这条责任失败一次 ⇒ ledger 记下**清理根因**。
        ledger.Pump(T0);
        Assert.Equal(
            CapturePolicyCleanupLedger.RestoreFailedErrorCode,
            ledger.CleanupErrorCode);

        // 真被拒 ⇒ 只写**告警**通道。
        Assert.False(ledger.Add(NewCleanup(generation: 3)));
        Assert.Equal(
            "gpu_capture_policy_cleanup_registration_rejected",
            ledger.CleanupWarningCode);

        // ★ 清理根因**不被**告警覆盖（合同 §5.3 规则 2：三个诊断字段互不覆盖）。
        Assert.Equal(
            CapturePolicyCleanupLedger.RestoreFailedErrorCode,
            ledger.CleanupErrorCode);

        // ── 协调器层：RecordCleanupWarning 不得覆盖呈现根因 / 清理根因 ──
        RenderingTransitionCoordinator coordinator =
            new(new WindowHandleGeneration());
        coordinator.RecordPresentationRootCause("gpu_presentation_failed");
        coordinator.RecordCleanupState(
            CapturePolicyCleanupState.Failed,
            CapturePolicyCleanupLedger.RestoreFailedErrorCode);
        coordinator.RecordCleanupWarning(
            "gpu_capture_policy_cleanup_registration_rejected");

        RenderingTransitionState state = coordinator.State;
        Assert.Equal("gpu_presentation_failed", state.FailureCode);
        Assert.Equal(
            CapturePolicyCleanupLedger.RestoreFailedErrorCode,
            state.CleanupErrorCode);
        Assert.Equal(
            "gpu_capture_policy_cleanup_registration_rejected",
            state.CleanupWarningCode);
    }

    // ================================================================
    // 辅助
    // ================================================================

    private static WindowCapturePolicyCleanup NewCleanup(long generation) => new()
    {
        Generation = generation,
        WindowHandle = new IntPtr(0x1234),
        OriginalAffinity = 0,
        AffinityChangedBySession = true,
    };

    private static CapturePolicyCleanupLedger CreateLedger()
    {
        return new CapturePolicyCleanupLedger(
            () => 3,
            _ => true,
            _ => new CapturePolicyRestoreResult(
                false,
                CapturePolicyCleanupLedger.RestoreFailedErrorCode,
                "boom"));
    }

    /// <summary>
    /// 找到"调用了 <paramref name="declaringType"/>.<paramref name="name"/>"的第一条指令偏移；
    /// 找不到返回 -1。
    /// </summary>
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
}
