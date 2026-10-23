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
                while (cleanup.Attempts < 2)
                {
                    cleanup.TryRecordFailedAttempt(
                        RestoreFailedErrorCode,
                        cleanup.LastErrorMessage,
                        now);
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
