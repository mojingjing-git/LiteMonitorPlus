using LiteMonitor.Rendering.Configuration;

namespace LiteMonitor.Rendering.Presentation;

/// <summary>
/// 【T02 合同 §6.4 / §7.4】WDA 清理责任在某一次判定下的结论。
/// </summary>
public enum CapturePolicyCleanupJudgement
{
    /// <summary>该清理责任已终结（终态不可回退，合同 §6.3）。</summary>
    Terminal = 0,

    /// <summary>
    /// 【合同 §7.4 第 1 条】<c>cleanup.Generation &lt; 当前代次</c> ⇒
    /// **禁止对该 HWND 执行任何 Win32 调用**（包括 <c>IsWindow</c>），直接终结。
    /// </summary>
    SupersededByNewGeneration = 1,

    /// <summary>
    /// 【合同 §7.4 第 2 条】同代次但 <c>IsWindow</c> 为假 ⇒ 旧代次已销毁，清理同步终结。
    /// </summary>
    WindowDestroyed = 2,

    /// <summary>【合同 §6.4】重试预算耗尽（总上限 6 次）。</summary>
    RetryExhausted = 3,

    /// <summary>可以立即重试一次恢复。</summary>
    RetryDue = 4,

    /// <summary>退避未到（<c>Now &lt; NextAttemptAt</c>）⇒ 禁止每帧重试。</summary>
    RetryNotDueYet = 5,

    /// <summary>
    /// 防御分支：清理记录的代次**大于**当前代次（生产中不可能发生）⇒ 同样禁止触碰该 HWND。
    /// </summary>
    GenerationMismatch = 6,
}

/// <summary>
/// 【T02 合同 §6.4 / §7.4】WDA 清理责任的**重试预算、退避表与跨代次判定**。
/// <para><b>纯 CLR、无 Win32 依赖</b>（除显式传入的 <c>isWindowAlive</c> 探针）⇒ 可被确定性单测覆盖。</para>
/// <para><b>所有权</b>：本类是**规则**的唯一落点；ledger（持有者、退避驱动、终结迁移）属 owner B，
/// 它调用本类的判定即可，不必重新推导合同条款。</para>
/// </summary>
public static class CapturePolicyCleanupPolicy
{
    /// <summary>
    /// 【合同 §6.4】清理尝试总上限（尝试 1、2 = 宿主停止时的首次与内联重试；
    /// 尝试 3–6 = ledger 退避重试）。
    /// </summary>
    public const int MaxAttempts = 6;

    /// <summary>
    /// 【合同 §6.4】尝试 3–6 之间的退避间隔：250 / 500 / 1000 / 2000 ms。
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> RetryBackoff =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1000),
        TimeSpan.FromMilliseconds(2000),
    ];

    /// <summary>【合同 §6.4】耗尽后的清理错误码。</summary>
    public const string RetryExhaustedErrorCode = "gpu_capture_policy_restore_retry_exhausted";

    /// <summary>【合同 §7.4】跨代次被取代时的告警码（信息级，不是错误）。</summary>
    public const string SupersededWarningCode = "gpu_capture_policy_cleanup_superseded";

    /// <summary>【合同 §6.4】恢复失败的清理错误码。</summary>
    public const string RestoreFailedErrorCode = "presentation_capture_policy_restore_failed";

    /// <summary>
    /// 【合同 §6.4】已尝试 <paramref name="attempts"/> 次之后、下一次尝试之前的退避间隔。
    /// <para>尝试 1 之后立即重试（无退避）；尝试 2 之后为 250 ms；随后依次为
    /// 500 / 1000 / 2000 ms；尝试 6 之后不再退避（预算耗尽）。</para>
    /// </summary>
    public static TimeSpan BackoffAfterAttempt(int attempts)
    {
        if (attempts < 2)
        {
            return TimeSpan.Zero;
        }

        int index = attempts - 2;
        if (index >= RetryBackoff.Count)
        {
            index = RetryBackoff.Count - 1;
        }

        return RetryBackoff[index];
    }

    /// <summary>
    /// 【合同 §7.4】跨代次丢弃判据。**判定顺序不可交换**：
    /// <list type="number">
    /// <item><c>cleanup.Generation &lt; 当前代次</c> ⇒ 禁止任何 Win32 调用（含 <c>IsWindow</c>），
    /// 直接终结（<see cref="CapturePolicyCleanupJudgement.SupersededByNewGeneration"/>）。</item>
    /// <item><c>cleanup.Generation == 当前代次</c> ⇒ 此时才允许用 <c>IsWindow</c> 判活：
    /// 假 ⇒ <see cref="CapturePolicyCleanupJudgement.WindowDestroyed"/>；
    /// 真 ⇒ 仍属当前代次，按 <see cref="MaxAttempts"/> 的预算重试。</item>
    /// </list>
    /// <para><paramref name="isWindowAlive"/> 只在**允许**调用 Win32 时才被调用。</para>
    /// </summary>
    public static CapturePolicyCleanupJudgement Judge(
        WindowCapturePolicyCleanup cleanup,
        long currentGeneration,
        DateTimeOffset now,
        Func<IntPtr, bool> isWindowAlive)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(isWindowAlive);

        if (cleanup.IsTerminal)
        {
            return CapturePolicyCleanupJudgement.Terminal;
        }

        if (cleanup.Generation > 0 && cleanup.Generation < currentGeneration)
        {
            return CapturePolicyCleanupJudgement.SupersededByNewGeneration;
        }

        if (cleanup.Generation > currentGeneration)
        {
            // 防御分支：不允许触碰一个"来自未来代次"的 HWND。
            return CapturePolicyCleanupJudgement.GenerationMismatch;
        }

        if (cleanup.Generation > 0 &&
            cleanup.Generation == currentGeneration &&
            !isWindowAlive(cleanup.WindowHandle))
        {
            return CapturePolicyCleanupJudgement.WindowDestroyed;
        }

        if (cleanup.Attempts >= MaxAttempts)
        {
            return CapturePolicyCleanupJudgement.RetryExhausted;
        }

        return now < cleanup.NextAttemptAt
            ? CapturePolicyCleanupJudgement.RetryNotDueYet
            : CapturePolicyCleanupJudgement.RetryDue;
    }
}
