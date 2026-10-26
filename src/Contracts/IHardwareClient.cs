namespace LiteMonitor.Contracts;

/// <summary>
/// C1 采集契约 v2（2026-10-26 W2 契约演进，集成人独占本文件）。
/// <para>演进规则（完善计划 §2.1/§14）：两端（进程内适配器/管道客户端）不得表达不同的
/// 成功、时效或缺失含义；新成员先确保两端兼容，再迁移消费者，最后撤除兼容桥。</para>
/// <para>成员分域：<b>快照面</b>由 P-E01 实现于适配器；<b>命令面</b>语义由 P-E02 定义
/// （进程内实现落适配器、管道实现落 PipeHardwareClient/Host）。带默认实现的新成员
/// 在未被实现前抛 <see cref="NotSupportedException"/>——当前无调用方，运行时安全。</para>
/// </summary>
public interface IHardwareClient : IDisposable
{
    // ── 快照面（P-E01：真实采样完成时发布；读取不推进代次/时效）────────────────

    /// <summary>同步读取最新已发布完整快照；不触发采样、不改变 BatchId。</summary>
    HardwareSnapshot GetLatestSnapshot();

    /// <summary>异步获取完整版本设备/传感器目录；不向调用方暴露对象锁或 LHM 对象。</summary>
    Task<HardwareCatalog> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>查询采集层健康状态与当前操作。</summary>
    HardwareHealth GetHealth();

    /// <summary>获取当前网络出口 IP（委托既有 NetworkManager 缓存逻辑，不新增探测）。</summary>
    string? GetNetworkIP();

    // ── 命令面（P-E02 语义表：合并/超时/取消/操作记录）──────────────────────────

    /// <summary>应用采集配置（异步）：返回实际生效 revision 或明确失败；仅相同 revision/内容可合并。</summary>
    Task<SamplingApplyResult> ApplyConfigAsync(SamplingConfig config, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("IHardwareClient v2：由具体客户端实现（P-E02）");

    /// <summary>请求"到期才采"的一次采样（共享唯一协调器，同一采样周期合并）。原 RequestRescanAsync 语义。</summary>
    Task<bool> RequestSamplingAsync(CancellationToken cancellationToken = default)
        => RequestRescanAsync(cancellationToken);

    /// <summary>显式全量重扫（独立操作 ID、有界、状态可查）；不是 UI Tick 的替代品。</summary>
    Task<HardwareOperationResult> RequestFullRescanAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("IHardwareClient v2：由具体客户端实现（P-E02）");

    /// <summary>按消费者身份登记/更新需求（聚合后交采样协调器；关闭窗口只释放自己的）。</summary>
    void SubmitDemand(string consumerId, SamplingDemand demand)
        => SubmitDemand(demand); // 默认桥接旧单桶（消费面迁移前的兼容行为）

    /// <summary>释放消费者需求；返回是否存在过登记。</summary>
    bool ReleaseDemand(string consumerId)
        => throw new NotSupportedException("IHardwareClient v2：由具体客户端实现（P-E01 需求租约）");

    // ── 兼容桥（既有成员；撤除阶段：ApplyConfig→E03、RequestRescanAsync/SubmitDemand→E01-C）──

    /// <summary>[兼容桥] 同步应用配置——不得在 UI 线程调用；既有调用方（SamplingConfigApplier）随 E03 迁移到 <see cref="ApplyConfigAsync"/>。</summary>
    SamplingApplyResult ApplyConfig(SamplingConfig config);

    /// <summary>[兼容桥] 语义已收窄为"到期采样"；显式重扫请用 <see cref="RequestFullRescanAsync"/>。</summary>
    Task<bool> RequestRescanAsync(CancellationToken cancellationToken = default);

    /// <summary>[兼容桥] 单桶需求（无消费者身份）；消费者迁移后由 keyed 重载取代。</summary>
    void SubmitDemand(SamplingDemand demand);
}
