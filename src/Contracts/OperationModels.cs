namespace LiteMonitor.Contracts;

/// <summary>显式操作（全量重扫等）的状态机（P-E02 语义表：排队/执行/完成/失败/已取消）。</summary>
public enum HardwareOperationState
{
    /// <summary>已受理，等待执行（在途合并的加入者也会看到此状态）。</summary>
    Queued,
    /// <summary>执行中。</summary>
    Running,
    /// <summary>完成。</summary>
    Completed,
    /// <summary>失败（FailureReason 可解释）。</summary>
    Failed,
    /// <summary>已取消（仅操作所有者可发起；加入者取消不影响共享操作）。</summary>
    Cancelled,
    /// <summary>拒绝受理（容量/截止期限超限等，给出可解释原因）。</summary>
    Rejected,
}

/// <summary>
/// 异步操作结果（P-E02）：Accepted=false 时 State=Rejected/Failed 并带原因；
/// 超时只表示本调用方停止等待——OperationId 允许调用方后续查询真实结局，不谎报底层已停止。
/// </summary>
public sealed record HardwareOperationResult(
    bool Accepted,
    string OperationId,
    HardwareOperationState State,
    string? FailureReason = null)
{
    public static HardwareOperationResult Ok(string operationId, HardwareOperationState state = HardwareOperationState.Completed)
        => new(true, operationId, state, null);

    public static HardwareOperationResult Reject(string reason, HardwareOperationState state = HardwareOperationState.Rejected)
        => new(false, string.Empty, state, reason);
}
