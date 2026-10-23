using System;

namespace LiteMonitor.Rendering.Configuration;

/// <summary>
/// 【T02 合同 §3.2】GPU ↔ legacy 运行时切换的阶段。取值与计划 §4.2 :156 **逐字一致，不增不减**。
/// </summary>
public enum RenderingTransitionPhase
{
    Stable = 0,
    Stopping = 1,
    RecreatingHandle = 2,
    Restoring = 3,
    Starting = 4,
    Failed = 5,
}

/// <summary>
/// 【T02 合同 §3.2】HWND 呈现历史。取值与计划 §4.2 :158 逐字一致。
/// </summary>
public enum PresentationBindingState
{
    /// <summary>未绑定。</summary>
    Unbound = 0,

    /// <summary>已绑定。</summary>
    Bound = 1,

    /// <summary>已尝试呈现。</summary>
    PresentAttempted = 2,

    /// <summary>已成功呈现。</summary>
    Presented = 3,
}

/// <summary>
/// 【T02 合同 §3.2】捕获策略（Windows Display Affinity）清理状态。取值与计划 §4.2 :159 逐字一致。
/// </summary>
public enum CapturePolicyCleanupState
{
    None = 0,
    Pending = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>
/// 【T02 合同 §3.2】首帧结果。区分"未尝试 / 成功 / 失败 / 结果不明" ——
/// "结果不明"是计划 §4.1 :124 保守重建的触发依据。
/// </summary>
public enum FirstFrameOutcome
{
    NotAttempted = 0,
    Succeeded = 1,
    Failed = 2,
    Unknown = 3,
}

/// <summary>
/// 【T02 合同 §3.2】切换触发原因。用于诊断"为什么进入了这次切换"。
/// </summary>
public enum RenderingTransitionTrigger
{
    UserThemeChange = 0,
    UserGpuToggle = 1,
    UserLiveToggle = 2,
    UserWdaAcceptance = 3,
    RuntimeFailure = 4,
    HandleRecreated = 5,
    VisibilityChanged = 6,
    StartupGuardConservative = 7,
    ExplicitRetry = 8,
    DpiChanged = 9,
    DisplayChanged = 10,
}

/// <summary>
/// 【T02 合同 §3.2】纯状态快照。全部为不可变 record；写入者 = 协调器（owner B）。
/// <para>三个诊断字段 <see cref="FailureCode"/> / <see cref="CleanupErrorCode"/> /
/// <see cref="CleanupWarningCode"/> **互不覆盖**（合同 §5.3 规则 2）：
/// 写清理诊断**不得**清空呈现根因。</para>
/// </summary>
public sealed record RenderingTransitionState(
    long TransitionId,
    long HandleGeneration,
    long HostGeneration,
    long LastAppliedRequestVersion,
    RenderingRoute ExpectedRoute,
    RenderingRoute EffectiveRoute,
    RenderingTransitionPhase Phase,
    PresentationBindingState BindingState,
    CapturePolicyCleanupState CleanupState,
    RenderingTransitionTrigger Trigger,
    FirstFrameOutcome FirstFrameOutcome,
    bool PendingFirstFrame,
    string? FailureCode,
    string? CleanupErrorCode,
    string? CleanupWarningCode);

/// <summary>
/// 【T02 合同 §3.2 / §6】清理记录 —— GPU-001 的责任载体。
/// <para>可变状态机，**纯 CLR**（不依赖 WinForms / DXGI）⇒ 可被 T06 直接单测。</para>
/// <para><b>终态不可回退</b>（合同 §6.3）：<see cref="CapturePolicyCleanupState.Completed"/> /
/// <see cref="CapturePolicyCleanupState.Failed"/> 之后不再重试、不再触碰 HWND。
/// 因此本类的所有状态迁移方法在已终结时都返回 <c>false</c> 且**不改动任何状态**
/// （幂等，合同 §6.4"关闭时…幂等" / INV-5）。</para>
/// </summary>
public sealed class WindowCapturePolicyCleanup
{
    /// <summary>
    /// 新建一条清理责任。初始状态即 <see cref="CapturePolicyCleanupState.Pending"/>：
    /// 记录一旦产生就代表"有未完成的清理责任"。
    /// </summary>
    public WindowCapturePolicyCleanup()
    {
        State = CapturePolicyCleanupState.Pending;
    }

    /// <summary>旧 HWND（仅在 <see cref="Generation"/> == 当前代次时允许触碰）。</summary>
    public IntPtr WindowHandle { get; init; }

    /// <summary>【裁决】所属句柄代次（来自唯一真相源 <see cref="WindowHandleGeneration"/>）。</summary>
    public long Generation { get; init; }

    /// <summary>原始 affinity（精确值，**不写死** WDA_NONE —— 合同 §4.5 :198）。</summary>
    public uint OriginalAffinity { get; init; }

    /// <summary>是否由本程序修改过（未改过 ⇒ 无需恢复）。</summary>
    public bool AffinityChangedBySession { get; init; }

    /// <summary>已尝试次数（清理专用预算，合同 §6.4 总上限 6 次）。</summary>
    public int Attempts { get; private set; }

    /// <summary>最后一次失败的错误码。</summary>
    public string? LastErrorCode { get; private set; }

    /// <summary>最后一次失败的可诊断消息。</summary>
    public string? LastErrorMessage { get; private set; }

    /// <summary>下一次允许尝试的时刻（退避重试用，合同 §6.4 的 250/500/1000/2000 ms）。</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>清理状态。</summary>
    public CapturePolicyCleanupState State { get; private set; }

    /// <summary>
    /// 终结原因：<c>superseded</c> / <c>superseded_by_new_generation</c> /
    /// <c>window_destroyed</c> / <c>exhausted</c> / <c>form_closed</c>。
    /// </summary>
    public string? TerminalReason { get; private set; }

    /// <summary>告警码（合同 §7.4：跨代次被取代时记 <c>gpu_capture_policy_cleanup_superseded</c>）。</summary>
    public string? WarningCode { get; private set; }

    /// <summary>是否已终结（终态不可回退）。</summary>
    public bool IsTerminal => State is CapturePolicyCleanupState.Completed
                                   or CapturePolicyCleanupState.Failed;

    /// <summary>
    /// 记录一次失败尝试。调用方负责按合同 §6.4 计算 <paramref name="nextAttemptAt"/>。
    /// 已终结时返回 <c>false</c> 且不改动任何状态。
    /// </summary>
    public bool TryRecordFailedAttempt(string? errorCode, string? errorMessage, DateTimeOffset nextAttemptAt)
    {
        if (IsTerminal) return false;

        Attempts++;
        LastErrorCode = errorCode;
        LastErrorMessage = errorMessage;
        NextAttemptAt = nextAttemptAt;
        State = CapturePolicyCleanupState.Pending;
        return true;
    }

    /// <summary>
    /// 终结为"已完成"（例如恢复成功、旧代次已销毁、被新代次取代、窗体关闭）。
    /// 已终结时返回 <c>false</c> 且不改动任何状态（幂等）。
    /// </summary>
    public bool TryMarkCompleted(string terminalReason)
    {
        if (IsTerminal) return false;

        State = CapturePolicyCleanupState.Completed;
        TerminalReason = terminalReason;
        return true;
    }

    /// <summary>
    /// 终结为"已失败"（例如重试预算耗尽 <c>exhausted</c>、窗体关闭时最后一次仍失败 <c>form_closed</c>）。
    /// 已终结时返回 <c>false</c> 且不改动任何状态（幂等）。
    /// </summary>
    public bool TryMarkFailed(string terminalReason, string? errorCode = null, string? errorMessage = null)
    {
        if (IsTerminal) return false;

        State = CapturePolicyCleanupState.Failed;
        TerminalReason = terminalReason;
        if (errorCode != null) LastErrorCode = errorCode;
        if (errorMessage != null) LastErrorMessage = errorMessage;
        return true;
    }

    /// <summary>
    /// 跨代次被取代时终结（合同 §7.4 第 1 条）：
    /// <c>cleanup.Generation &lt; WindowHandleGeneration.Current</c> ⇒
    /// **禁止对该 HWND 执行任何 Win32 调用**（包括 <c>IsWindow</c>），直接终结。
    /// </summary>
    public bool TryMarkSupersededByNewGeneration()
    {
        if (IsTerminal) return false;

        State = CapturePolicyCleanupState.Completed;
        TerminalReason = "superseded_by_new_generation";
        WarningCode = "gpu_capture_policy_cleanup_superseded";
        return true;
    }
}
