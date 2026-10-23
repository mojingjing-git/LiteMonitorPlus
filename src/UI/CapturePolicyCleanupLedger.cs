using LiteMonitor.Rendering.Configuration;
using System.Runtime.InteropServices;

namespace LiteMonitor
{
    /// <summary>
    /// 一次 affinity 恢复尝试的结果。
    /// </summary>
    /// <param name="Succeeded">是否恢复成功。</param>
    /// <param name="ErrorCode">失败原因码（合同 §5.1 / §5.2）。</param>
    /// <param name="ErrorMessage">失败消息。</param>
    internal readonly record struct CapturePolicyRestoreResult(
        bool Succeeded,
        string? ErrorCode,
        string? ErrorMessage);

    /// <summary>
    /// 【合同 §6.2 / §6.4 / §7.4 / INV-1 ~ INV-6】WDA 清理责任的**独立持有者**。
    /// <para><b>所有权形状</b>（合同 §6.2）：
    /// <code>
    /// UI 生命周期所有者（UIController / 协调器，owner B）
    /// │   持有：_gpuPresentation?            ← 只表示"宿主是否活着"
    /// │   持有：CapturePolicyCleanupLedger  ← 独立持有全部未完成的清理责任
    /// └─ GpuPresentationHostController（owner C）
    ///       职责：停止时若 affinity 恢复失败 ⇒ 【移交】一份 WindowCapturePolicyCleanup 给 ledger
    /// </code>
    /// ⇒ 清理责任**永不**通过宿主引用表达（合同 §6.2 不变量 INV-1）。</para>
    /// <para><b>纯 CLR</b>（不直接碰 HWND；窗口存活探针与恢复动作都由外部注入）⇒ 可被确定性单测覆盖。</para>
    /// </summary>
    internal sealed class CapturePolicyCleanupLedger
    {
        /// <summary>【合同 §6.4】清理尝试总上限（尝试 1、2 = 宿主停止时的首次与内联重试；尝试 3–6 = ledger 退避重试）。</summary>
        internal const int MaxAttempts = 6;

        /// <summary>【合同 §6.4】尝试 3–6 之间的退避间隔。</summary>
        private static readonly TimeSpan[] RetryBackoff =
        [
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(1000),
            TimeSpan.FromMilliseconds(2000),
        ];

        /// <summary>【合同 §5.2】清理预算耗尽后的清理错误码。</summary>
        internal const string RetryExhaustedErrorCode = "gpu_capture_policy_restore_retry_exhausted";

        /// <summary>【合同 §5.2】跨代次被取代时的告警码（信息级，不是错误）。</summary>
        internal const string SupersededWarningCode = "gpu_capture_policy_cleanup_superseded";

        /// <summary>【合同 §5.1】恢复失败的清理错误码。</summary>
        internal const string RestoreFailedErrorCode = "presentation_capture_policy_restore_failed";

        /// <summary>
        /// 【LM-074】闸门阻断原因码：同一代次仍有**未完成**的清理责任（还在正常重试）。
        /// </summary>
        internal const string PendingBlockingErrorCode = "gpu_capture_policy_cleanup_pending";

        /// <summary>
        /// 【LM-074】闸门阻断原因码：同一代次的清理责任已**耗尽**，且**原策略从未恢复**
        /// （本程序改过 affinity 却没能写回）⇒ HWND 上可能仍残留 <c>WDA_EXCLUDEFROMCAPTURE</c>。
        /// </summary>
        internal const string UnresolvedBlockingErrorCode = "gpu_capture_policy_cleanup_unresolved";

        /// <summary>【LM-074】明确恢复失败：该代次没有可重建的未解决条目。</summary>
        internal const string NoUnresolvedEntryErrorCode =
            "gpu_capture_policy_cleanup_no_unresolved_entry";

        /// <summary>【LM-074】明确恢复失败：该代次已有非终态责任（INV-4，不得叠加第二条）。</summary>
        internal const string AlreadyPendingErrorCode =
            "gpu_capture_policy_cleanup_already_pending";

        /// <summary>【LM-074】明确恢复失败：无法登记新的清理责任。</summary>
        internal const string RearmFailedErrorCode =
            "gpu_capture_policy_cleanup_rearm_failed";

        /// <summary>
        /// 【LM-074】清理责任**登记被拒**的诊断码（INV-4：同代次已有非终态责任）。
        /// <para>★ 被拒的移交**不得静默丢弃** —— 它往往正是那条"误采残留值当原值"的记录。</para>
        /// </summary>
        internal const string RegistrationRejectedWarningCode =
            "gpu_capture_policy_cleanup_registration_rejected";

        /// <summary>
        /// 【LM-074】最近一次"清理责任登记被拒"的诊断码；从未被拒过则为 <c>null</c>。
        /// <para>★ 落点：<see cref="Add"/> 因 INV-4 拒绝一条移交时写入；
        /// owner B（<c>UIController</c>）把它写进协调器的
        /// <c>RenderingTransitionState.CleanupWarningCode</c> ⇒ 被拒不静默。</para>
        /// </summary>
        internal string? RegistrationRejectedCode { get; private set; }

        private readonly List<WindowCapturePolicyCleanup> _entries = new();
        private readonly Func<long> _currentGeneration;
        private readonly Func<IntPtr, bool> _isWindowAlive;
        private readonly Func<WindowCapturePolicyCleanup, CapturePolicyRestoreResult> _restore;

        /// <summary>
        /// 构造 ledger。
        /// </summary>
        /// <param name="currentGeneration">当前句柄代次读数（唯一真相源）。</param>
        /// <param name="isWindowAlive">窗口存活探针（<c>IsWindow</c>）。<b>只允许在代次相同时代调用</b>（合同 §7.4）。</param>
        /// <param name="restore">恢复动作：把原始 affinity 精确写回旧 HWND（合同 §4.5 :198）。</param>
        internal CapturePolicyCleanupLedger(
            Func<long> currentGeneration,
            Func<IntPtr, bool> isWindowAlive,
            Func<WindowCapturePolicyCleanup, CapturePolicyRestoreResult> restore)
        {
            _currentGeneration = currentGeneration ?? throw new ArgumentNullException(nameof(currentGeneration));
            _isWindowAlive = isWindowAlive ?? throw new ArgumentNullException(nameof(isWindowAlive));
            _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        }

        /// <summary>全部清理责任（合同 §6.2 的 <c>Entries</c>）。</summary>
        internal IReadOnlyList<WindowCapturePolicyCleanup> Entries => _entries;

        /// <summary>清理状态（合同 §2 维度 7）。</summary>
        internal CapturePolicyCleanupState State { get; private set; } = CapturePolicyCleanupState.None;

        /// <summary>清理根因（<c>CleanupErrorCode</c>）—— 与呈现根因**互不覆盖**（合同 §5.3 规则 2）。</summary>
        internal string? CleanupErrorCode { get; private set; }

        /// <summary>清理告警（<c>CleanupWarningCode</c>）—— 信息级。</summary>
        internal string? CleanupWarningCode { get; private set; }

        /// <summary>
        /// 【合同 §6.2 INV-4 / §6.3】登记一条清理责任。
        /// <para>同一代次（同一 HWND）**至多一条** <c>Pending</c>：重复登记直接拒绝。</para>
        /// </summary>
        /// <returns><c>true</c> = 已登记。</returns>
        internal bool Add(WindowCapturePolicyCleanup cleanup)
        {
            ArgumentNullException.ThrowIfNull(cleanup);

            if (cleanup.State == CapturePolicyCleanupState.Completed ||
                cleanup.State == CapturePolicyCleanupState.Failed)
            {
                // 已终结的责任不再登记（终态不可回退，合同 §6.3）。
                return false;
            }

            foreach (WindowCapturePolicyCleanup existing in _entries)
            {
                if (existing.Generation == cleanup.Generation && !existing.IsTerminal)
                {
                    // INV-4：同一代次至多一条 Pending。
                    // ★【LM-074】被拒的移交**不得静默丢弃** ⇒ 留下可观测的诊断
                    //   （诊断码 + 告警通道），由 owner B 写进协调器的 CleanupWarningCode。
                    RegistrationRejectedCode = RegistrationRejectedWarningCode;
                    CleanupWarningCode = RegistrationRejectedWarningCode;
                    return false;
                }
            }

            _entries.Add(cleanup);
            State = CapturePolicyCleanupState.Pending;
            if (!string.IsNullOrEmpty(cleanup.LastErrorCode))
            {
                CleanupErrorCode = cleanup.LastErrorCode;
            }

            return true;
        }

        /// <summary>
        /// 【合同 §6.2 INV-6】同一代次是否仍有未完成的清理责任。
        /// <para>新代次取得 WDA 所有权前必须查询：同一 HWND 清理未完成 ⇒ **拒绝**叠加新的 affinity 所有者。</para>
        /// </summary>
        internal bool HasPendingForGeneration(long generation)
        {
            foreach (WindowCapturePolicyCleanup entry in _entries)
            {
                if (entry.Generation == generation && !entry.IsTerminal)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 【LM-074】同一代次是否仍被"**未完成的 affinity 清理**"占用，以及占用原因。
        /// <para>★ 判据（与 <see cref="HasPendingForGeneration"/> **并列**，不是替代 ——
        /// 后者的既有语义不得改写）：同一代次存在 <c>Pending</c> 条目 ⇒ 占用，原因 = pending；
        /// 或存在 <c>Failed</c> 且 <see cref="WindowCapturePolicyCleanup.AffinityChangedBySession"/>
        /// 为 <c>true</c> 的条目 ⇒ 占用，原因 = unresolved。</para>
        /// <para>★ 语义分离（报告 R10 的根因修复）：重试预算耗尽只意味着"**停止重试**"，
        /// **不等于**"**安全恢复**"。原策略没被写回 ⇒ HWND 上仍可能残留
        /// <c>WDA_EXCLUDEFROMCAPTURE</c> ⇒ 同代次**不得**叠加新的 affinity 所有者
        /// （合同 §6.2 INV-6 / §4.5 :202 的支配条款）。</para>
        /// <para>★ <c>Failed</c> 且本程序**没改过** affinity ⇒ 没有残留 ⇒ **不占用**。</para>
        /// <para>★ <c>Failed</c> 但**已被明确恢复接管**
        /// （<see cref="WindowCapturePolicyCleanup.ReclaimedByExplicitRecovery"/>）⇒
        /// 恢复责任已经换人 ⇒ 由**新责任**（Pending）继续把关 ⇒ **不占用**。</para>
        /// </summary>
        /// <returns>阻断原因码；<c>null</c> = 该代次未被占用。</returns>
        internal string? GetBlockingReasonForGeneration(long generation)
        {
            bool unresolved = false;

            foreach (WindowCapturePolicyCleanup entry in _entries)
            {
                if (entry.Generation != generation)
                {
                    continue;
                }

                if (!entry.IsTerminal)
                {
                    // Pending 优先：它代表"还在按合同 §6.4 正常重试"。
                    return PendingBlockingErrorCode;
                }

                if (entry.State == CapturePolicyCleanupState.Failed &&
                    entry.AffinityChangedBySession &&
                    !entry.ReclaimedByExplicitRecovery)
                {
                    unresolved = true;
                }
            }

            return unresolved ? UnresolvedBlockingErrorCode : null;
        }

        /// <summary>
        /// 【LM-074】同一代次是否仍被"**未完成的 affinity 清理**"占用（布尔版）。
        /// <para>★ <see cref="HasPendingForGeneration"/> 的既有语义（只认非终态条目）**不变**；
        /// 本方法是**新增的并列判据**，覆盖"已耗尽但从未恢复"这一档。</para>
        /// </summary>
        internal bool HasUnresolvedForGeneration(long generation) =>
            GetBlockingReasonForGeneration(generation) is not null;

        /// <summary>
        /// 【LM-074】**明确恢复**：对同代次"已 <c>Failed</c> 且由本程序改过 affinity"的条目
        /// **重建一条新的清理责任**。
        /// <para>★ 新责任沿用该条目的**精确原值** <see cref="WindowCapturePolicyCleanup.OriginalAffinity"/>、
        /// 同一 <see cref="WindowCapturePolicyCleanup.WindowHandle"/>、同一
        /// <see cref="WindowCapturePolicyCleanup.Generation"/>；<c>Attempts</c> 归零；
        /// <c>NextAttemptAt</c> = 立即。</para>
        /// <para>★ 旧条目**不再重试、也不再触碰 HWND**（终结为
        /// <c>Completed("rearmed_by_explicit_recovery")</c>）⇒ **不违反**合同 §6.3
        /// "终态不可回退"。</para>
        /// <para>★ 同代次已有非终态条目时**必须拒绝**（否则会叠加第二条 Pending，违反 INV-4），
        /// ⇒ 用户反复点击不会无限堆积条目。</para>
        /// </summary>
        /// <returns><c>true</c> = 已重建一条新的清理责任。</returns>
        internal bool TryRearmForExplicitRecovery(
            long generation,
            DateTimeOffset now,
            out string? errorCode)
        {
            errorCode = null;

            WindowCapturePolicyCleanup? failed = null;
            bool hasNonTerminal = false;

            foreach (WindowCapturePolicyCleanup entry in _entries)
            {
                if (entry.Generation != generation)
                {
                    continue;
                }

                if (!entry.IsTerminal)
                {
                    hasNonTerminal = true;
                    continue;
                }

                if (failed is null &&
                    entry.State == CapturePolicyCleanupState.Failed &&
                    entry.AffinityChangedBySession)
                {
                    failed = entry;
                }
            }

            if (failed is null)
            {
                errorCode = NoUnresolvedEntryErrorCode;
                return false;
            }

            if (hasNonTerminal)
            {
                // INV-4：同一代次至多一条非终态条目 ⇒ 不得再叠加第二条。
                errorCode = AlreadyPendingErrorCode;
                return false;
            }

            WindowCapturePolicyCleanup rearmed = new()
            {
                WindowHandle = failed.WindowHandle,
                Generation = failed.Generation,
                OriginalAffinity = failed.OriginalAffinity,
                AffinityChangedBySession = failed.AffinityChangedBySession,
                RebuiltByExplicitRecovery = true,
            };
            rearmed.TryScheduleImmediateAttempt(now);

            // ★ 先登记新责任（失败则旧责任原样保留）⇒ 不会出现"无人负责恢复"的空窗。
            if (!Add(rearmed))
            {
                errorCode = RearmFailedErrorCode;
                return false;
            }

            // 旧责任**保持 Failed / exhausted**（如实记账：原策略始终没被写回），
            // 只打上"已被接管"标记 ⇒ 不再占用该代次的闸门；
            // 它仍然终态、ledger 不会再重试它、也不会再对它做任何 Win32 调用。
            failed.TryMarkReclaimedByExplicitRecovery();
            return true;
        }

        /// <summary>
        /// 【合同 §6.4】驱动一次退避重试（由 UI Tick 调用；<b>仅当</b> <c>Now &gt;= NextAttemptAt</c> 才尝试）。
        /// <para>★ 判定顺序不可交换（合同 §7.4）：先比代次，**只有同代次**才允许调用 <c>IsWindow</c>。</para>
        /// <para>★ 禁止每帧重试：退避未到 ⇒ 直接返回。</para>
        /// </summary>
        /// <returns>本次实际发起的恢复尝试次数（0 = 没有到期项）。</returns>
        internal int Pump(DateTimeOffset now)
        {
            int attempts = 0;
            long current = _currentGeneration();

            foreach (WindowCapturePolicyCleanup cleanup in _entries)
            {
                if (cleanup.IsTerminal)
                {
                    continue;
                }

                // ── 合同 §7.4 第 1 条：代次判据**先于** IsWindow ──
                if (cleanup.Generation > 0 && cleanup.Generation < current)
                {
                    // 旧代次已被取代 ⇒ 禁止对该 HWND 执行任何 Win32 调用（含 IsWindow）。
                    cleanup.TryMarkSupersededByNewGeneration();
                    CleanupWarningCode = SupersededWarningCode;
                    State = CapturePolicyCleanupState.Completed;
                    continue;
                }

                if (cleanup.Generation > current)
                {
                    // 防御分支：不允许触碰"来自未来代次"的 HWND。
                    continue;
                }

                // ── 合同 §7.4 第 2 条：同代次才允许用 IsWindow 判活 ──
                if (cleanup.Generation > 0 && !_isWindowAlive(cleanup.WindowHandle))
                {
                    cleanup.TryMarkCompleted("window_destroyed");
                    State = CapturePolicyCleanupState.Completed;
                    continue;
                }

                if (!cleanup.AffinityChangedBySession)
                {
                    // 本程序没有改过 affinity ⇒ 无需恢复，直接终结。
                    cleanup.TryMarkCompleted("not_modified_by_session");
                    State = CapturePolicyCleanupState.Completed;
                    continue;
                }

                if (cleanup.Attempts >= MaxAttempts)
                {
                    cleanup.TryMarkFailed("exhausted", RetryExhaustedErrorCode);
                    CleanupErrorCode = RetryExhaustedErrorCode;
                    State = CapturePolicyCleanupState.Failed;
                    continue;
                }

                if (now < cleanup.NextAttemptAt)
                {
                    // 退避未到 ⇒ 禁止每帧重试（合同 §6.4）。
                    continue;
                }

                // ★ 合同 §6.4：尝试 **1、2** 属于**宿主停止流程**（首次 CompleteStop + 内联重试），
                //   尝试 **3–6** 才是 ledger 的退避重试（间隔 250 / 500 / 1000 / 2000 ms）。
                //   宿主移交过来的记录若还没记满这两次（移交时未登记），这里补记 ——
                //   否则退避表与"总上限 6 次"的口径会漂。
                //   ★【LM-074】由"明确恢复"重建的责任拥有**自己的**预算（Attempts 从 0 开始），
                //   那两次宿主停止尝试已经由旧责任消耗掉 ⇒ 这里**不再补记**。
                if (!cleanup.RebuiltByExplicitRecovery)
                {
                    while (cleanup.Attempts < 2)
                    {
                        cleanup.TryRecordFailedAttempt(
                            RestoreFailedErrorCode,
                            cleanup.LastErrorMessage,
                            now);
                    }
                }

                attempts++;
                CapturePolicyRestoreResult result = _restore(cleanup);
                if (result.Succeeded)
                {
                    cleanup.TryMarkCompleted("restored");
                    State = CapturePolicyCleanupState.Completed;
                    continue;
                }

                string code = result.ErrorCode ?? RestoreFailedErrorCode;
                TimeSpan backoff = BackoffAfterAttempt(cleanup.Attempts + 1);
                cleanup.TryRecordFailedAttempt(code, result.ErrorMessage, now + backoff);
                CleanupErrorCode = code;
                State = CapturePolicyCleanupState.Pending;

                if (cleanup.Attempts >= MaxAttempts)
                {
                    cleanup.TryMarkFailed("exhausted", RetryExhaustedErrorCode);
                    CleanupErrorCode = RetryExhaustedErrorCode;
                    State = CapturePolicyCleanupState.Failed;
                }
            }

            return attempts;
        }

        /// <summary>
        /// 【合同 §6.4 / INV-5】窗体关闭时的终结：最多再尝试 1 次；仍失败 ⇒ 终态
        /// <c>Failed</c> + <c>form_closed</c>；**幂等**（重复 Dispose 安全）。
        /// <para>★ <paramref name="allowFinalAttempt"/> = <c>false</c>（生产默认）：
        /// 关闭拥有最高优先级（合同 §4 全局约束："关闭过程中不得重建 HWND、**不得恢复 affinity**、
        /// 不得重新创建宿主"）⇒ 关闭时**不发起**新的恢复尝试，只把未终结的责任终结为
        /// <c>form_closed</c>。合同 §6.4 的"关闭时最多再尝试 1 次"是**上限**，0 次同样合规。</para>
        /// </summary>
        internal void FinalizeOnFormClosed(DateTimeOffset now, bool allowFinalAttempt = false)
        {
            long current = _currentGeneration();

            foreach (WindowCapturePolicyCleanup cleanup in _entries)
            {
                if (cleanup.IsTerminal)
                {
                    continue;
                }

                if (cleanup.Generation > 0 && cleanup.Generation < current)
                {
                    cleanup.TryMarkSupersededByNewGeneration();
                    CleanupWarningCode = SupersededWarningCode;
                    continue;
                }

                if (allowFinalAttempt &&
                    cleanup.Generation == current &&
                    cleanup.AffinityChangedBySession &&
                    cleanup.Attempts < MaxAttempts)
                {
                    // 关闭时最多再尝试 1 次（合同 §6.4）。
                    CapturePolicyRestoreResult result = _restore(cleanup);
                    if (result.Succeeded)
                    {
                        cleanup.TryMarkCompleted("restored");
                        continue;
                    }

                    cleanup.TryRecordFailedAttempt(
                        result.ErrorCode ?? RestoreFailedErrorCode,
                        result.ErrorMessage,
                        now);
                }

                cleanup.TryMarkFailed("form_closed", cleanup.LastErrorCode, cleanup.LastErrorMessage);
                CleanupErrorCode = cleanup.LastErrorCode ?? CleanupErrorCode;
            }

            State = CapturePolicyCleanupState.Failed;
        }

        /// <summary>
        /// 【合同 §6.4】已尝试 <paramref name="attempts"/> 次之后、下一次尝试之前的退避间隔。
        /// <para>尝试 1 之后立即重试（无退避）；尝试 2 之后 250 ms；随后 500 / 1000 / 2000 ms；
        /// 尝试 6 之后不再退避（预算耗尽）。</para>
        /// </summary>
        internal static TimeSpan BackoffAfterAttempt(int attempts)
        {
            if (attempts < 2)
            {
                return TimeSpan.Zero;
            }

            int index = attempts - 2;
            if (index >= RetryBackoff.Length)
            {
                index = RetryBackoff.Length - 1;
            }

            return RetryBackoff[index];
        }
    }
}
