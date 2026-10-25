namespace LiteMonitor.Contracts;

/// <summary>
/// C1 采集契约：UI、Web、历史与传感器窗口消费硬件数据的唯一入口。
/// 进程内首版由 <c>HardwareClientAdapter</c> 包装现有 <c>HardwareMonitor</c> 实现；
/// Plan A 的 HardwareHost 经命名管道提供同构实现。接口调用不引入额外轮询。
/// </summary>
public interface IHardwareClient : IDisposable
{
    /// <summary>同步读取本地最新完整快照；不在调用方线程触发采样。</summary>
    HardwareSnapshot GetLatestSnapshot();

    /// <summary>异步获取完整版本设备/传感器目录；不向调用方暴露对象锁或 LHM 对象。</summary>
    Task<DeviceCatalog> GetCatalogAsync(CancellationToken cancellationToken);

    /// <summary>应用采集配置与设备选择，返回生效 revision 或明确失败原因。</summary>
    SamplingApplyResult ApplyConfig(SamplingConfig config);

    /// <summary>提交/更新消费方需求（UI、任务栏、Web、历史、传感器窗口的合并结果）。</summary>
    void SubmitDemand(SamplingDemand demand);

    /// <summary>查询采集层健康状态与当前操作。</summary>
    HardwareHealth GetHealth();

    /// <summary>请求有界重扫；重复请求合并，关闭后拒绝新操作。</summary>
    Task<bool> RequestRescanAsync(CancellationToken cancellationToken);
}
