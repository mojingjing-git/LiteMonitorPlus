namespace LiteMonitor.Rendering.Presentation;

/// <summary>
/// 【T02 合同 §10 GPU-005 / 裁决 D6】"受控放弃但停止流程不干净"的启动尝试终结入口。
/// <para>合同规定：guard 的 pending 只允许被两种事件清除 ——
/// ① <b>首帧成功</b>（<c>MarkFirstSuccessfulFrame</c>）；② <b>受控放弃且停止流程本身干净</b>
/// （含 affinity 恢复成功）。首帧失败且停止流程不干净（例如 WDA 恢复失败）⇒
/// <b>pending 必须保留</b>，下次启动进入 conservative、等待用户显式重试。</para>
/// <para><b>集成者须知（owner A）</b>：在 <c>GpuStartupAttempt</c> 上实现本接口即可 ——
/// 实现必须把该尝试从 guard 的尝试集合中注销，但**不得**删除 pending 标记文件。
/// owner C 的宿主在"停止流程不干净"时优先走本接口；未实现时退化为"不结束该尝试"，
/// 同样保证 pending 被保留（代价是该尝试在进程内保持未注销）。</para>
/// </summary>
public interface IGpuStartupAttemptAbandon
{
    /// <summary>放弃该启动尝试，但**保留** guard 的 pending 标记。</summary>
    void AbandonPreservingPending();
}
