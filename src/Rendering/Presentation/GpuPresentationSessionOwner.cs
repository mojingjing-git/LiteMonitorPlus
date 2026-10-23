using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;

namespace LiteMonitor.Rendering.Presentation;

/// <summary>
/// 【T02 合同 §6.2 / 裁决 D2】宿主呈现会话的**所有权容器** —— GPU-001 的结构性修复点。
/// <para><b>核心不变量</b>：停止后宿主<b>不再持有</b>会话引用。
/// affinity 恢复失败时，清理责任被<b>移交</b>成一条
/// <see cref="WindowCapturePolicyCleanup"/>（合同 §6.2 的所有权形状），
/// 宿主**绝不**用"宿主引用非空"表达清理责任 ——
/// 那正是 GPU-001 的根因（合同 §1.1 的六行链条：
/// 恢复失败 ⇒ <c>_presentation</c> 不置 null ⇒ <c>WindowCapturePolicyRestored</c> 恒为 false
/// ⇒ <c>UIController.StopGpuPresentation()</c> 早退 ⇒ 三条重启路径全部不可达）。</para>
/// <para><b>重试预算</b>（合同 §6.4）：本类只做<b>尝试 1</b>（首次 <c>CompleteStop</c>）与
/// <b>尝试 2</b>（既有内联重试，"失败后立即再试一次"）；尝试 3–6 的退避重试由 ledger
/// （owner B 持有）驱动，规则落点见 <see cref="CapturePolicyCleanupPolicy"/>。</para>
/// </summary>
public sealed class GpuPresentationSessionOwner
{
    private PresentationSession? _session;

    /// <summary>宿主当前持有的呈现会话；<c>null</c> = 宿主**不**持有会话（与清理责任无关）。</summary>
    public PresentationSession? Session => _session;

    /// <summary>宿主当前是否持有呈现会话。</summary>
    public bool OwnsSession => _session is not null;

    /// <summary>把呈现会话交给宿主持有。</summary>
    public void Bind(PresentationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>
    /// 停止呈现会话并**无条件**释放宿主对它的引用。
    /// <para>【合同 §6.4】尝试 1 = 首次 <c>CompleteStop</c>；失败后**立即**再试一次（尝试 2）；
    /// 两次都失败 ⇒ 清理责任被移交，返回一条 <see cref="WindowCapturePolicyCleanup"/>。</para>
    /// <para>【合同 §6.3】移交后宿主**不得**再触碰该 HWND —— 后续恢复由 ledger 负责。</para>
    /// </summary>
    /// <param name="generation">该会话所属的句柄代次（来自唯一真相源）。</param>
    /// <param name="now">当前时刻（用于计算 ledger 的首次退避时刻）。</param>
    /// <param name="errorCode">失败原因码；停止流程干净时为 <c>null</c>。</param>
    /// <param name="errorMessage">失败消息。</param>
    /// <returns>
    /// 需要移交给 ledger 的清理责任；<c>null</c> = 无未完成的清理责任
    /// （恢复成功，或本就没有由本程序修改过的 affinity）。
    /// </returns>
    public WindowCapturePolicyCleanup? Stop(
        long generation,
        DateTimeOffset now,
        out string? errorCode,
        out string? errorMessage)
    {
        PresentationSession? session = _session;

        // 【合同 §6.2 / INV-1】释放自己是**义务**，不是可选：
        // 无论恢复成功与否，宿主都不再持有会话引用。
        _session = null;

        if (session is null)
        {
            errorCode = null;
            errorMessage = null;
            return null;
        }

        if (session.State == RenderLifecycleState.Disabled)
        {
            // 从未启动过的会话不持有任何东西 ⇒ 无需停止，也无清理责任。
            _session = null;
            errorCode = null;
            errorMessage = null;
            return null;
        }

        // 清理记录必须在停止**之前**取材：恢复成功会清空原值。
        WindowCapturePolicyCleanup? cleanup = session.CreateCapturePolicyCleanup(generation);

        // 尝试 1（合同 §6.4）
        session.BeginStop(out _);
        if (session.CompleteStop(out string? restoreCode, out string? restoreMessage))
        {
            errorCode = null;
            errorMessage = null;
            return null;
        }

        string firstCode = restoreCode ?? CapturePolicyCleanupPolicy.RestoreFailedErrorCode;
        cleanup?.TryRecordFailedAttempt(firstCode, restoreMessage, now);

        // 尝试 2（合同 §6.4：既有内联重试 —— "retry once while the HWND still exists"）
        session.BeginStop(out _);
        if (session.CompleteStop(out string? retryCode, out string? retryMessage))
        {
            // 第二次成功 ⇒ 没有未完成的清理责任。
            errorCode = firstCode;
            errorMessage = restoreMessage;
            return null;
        }

        // 两次都失败 ⇒ 移交清理责任；尝试 2 之后的下一次尝试由 ledger 在 250 ms 后发起。
        cleanup?.TryRecordFailedAttempt(
            retryCode ?? CapturePolicyCleanupPolicy.RestoreFailedErrorCode,
            retryMessage ?? restoreMessage,
            now + CapturePolicyCleanupPolicy.BackoffAfterAttempt(2));

        errorCode = firstCode;
        errorMessage = retryMessage ?? restoreMessage;
        return cleanup;
    }
}
