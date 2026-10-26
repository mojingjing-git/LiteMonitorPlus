using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.src.SystemServices; // ControllerProbeMode / DiskProbeMode

namespace LiteMonitor.SystemServices
{
    /// <summary>
    /// E01：进程内适配器依赖的**采集层能力面**。
    ///
    /// ★ 目的：把 <c>HardwareClientAdapter</c> 与具体的 <c>HardwareMonitor</c> 解耦，
    ///   使命令面（ApplyConfigAsync / RequestFullRescanAsync / 需求租约）与快照面
    ///   可以在**不构造真实硬件树**的环境里被完整断言
    ///   （<c>HardwareMonitor</c> 的构造函数会打开 LHM 硬件树，单测不可实例化）。
    ///
    /// ★ 生产实现：<c>HardwareMonitor</c>（本进程唯一采集器）。
    ///   契约语义与管道侧（<c>PipeHardwareClient</c>）一一对应，便于 P-E02 做语义一致性对照。
    /// </summary>
    public interface IHardwareCollector
    {
        /// <summary>采集层是否已完成初始化（可以真实采集）。</summary>
        bool IsReady { get; }

        /// <summary>当前采集代次（每完成一次真实采集 +1）。</summary>
        int SamplingGeneration { get; }

        /// <summary>最近一次**已发布**的快照（未发布过 ⇒ null）。</summary>
        HardwareSnapshot? PublishedSnapshot { get; }

        /// <summary>最近一次**已发布**的目录（未发布过 ⇒ null）。</summary>
        HardwareCatalog? PublishedCatalog { get; }

        /// <summary>当前批次的批次级缺失原因（"initializing" / "collection-failed" / null）。</summary>
        string? CurrentMissingReason { get; }

        /// <summary>读一个指标值（消费侧即时读取；不推进任何代次）。</summary>
        float? Get(string key);

        /// <summary>取当前网络出口 IP（委托既有 NetworkManager 缓存逻辑，不新增探测）。</summary>
        string? GetNetworkIP();

        /// <summary>登记 / 撤销"合法的 Web 数据需求"（沿用既有 SetWebDataDemand 语义）。</summary>
        void SetWebDataDemand(bool hasDemand);

        /// <summary>配置**实际生效**后回写 revision（进快照的 ConfigRevision）。</summary>
        void RecordSamplingConfigRevision(int revision);

        /// <summary>应用控制器探测策略（返回是否真正生效）。</summary>
        bool ApplyControllerProbeMode(ControllerProbeMode mode);

        /// <summary>应用磁盘探测策略（返回是否真正生效）。</summary>
        bool ApplyDiskProbeMode(DiskProbeMode mode);

        /// <summary>按当前配置刷新硬件树（可能触发重载）。</summary>
        void RefreshHardwareConfig();

        /// <summary>请求一次"到期才采"的采样（共享唯一协调器）。</summary>
        Task<bool> RequestSamplingAsync(CancellationToken cancellationToken);

        /// <summary>
        /// 显式全量重扫：强制下一轮采集立刻执行并等待其落定。
        /// <para>★ 返回 false 表示"没有真正执行"（采集层未就绪 / 已释放）。</para>
        /// </summary>
        Task<bool> RequestFullRescanAsync(CancellationToken cancellationToken);
    }
}
