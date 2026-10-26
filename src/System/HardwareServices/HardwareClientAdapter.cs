using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.src.SystemServices; // HardwareMonitor 与既有 HardwareServices 类型（B5 命名空间统一前的现状）

namespace LiteMonitor.SystemServices; // 新文件按目标命名空间方案落位（architecture-assert 白名单导向），旧文件由 B5 机械统一

/// <summary>
/// C1 契约的进程内首版适配器：包装现有 <see cref="HardwareMonitor"/>。
///
/// ★ E01-A（生产者发布）：<see cref="GetLatestSnapshot"/> 现在**只返回已发布批次**——
///   读取**不**推进 BatchId、**不**改变时效字段；批次由采集层在真实采样完成后原子发布。
///
/// ★ E01-B（目录与需求）：目录结构由生产者发布（结构变化才递增版本），
///   传感器的**值**按 StableId 从已发布快照刷新（结构不变时不再遍历 LHM 树）；
///   需求按**消费者身份**登记（租约），关闭窗口只释放自己的那一份。
///
/// ★ 需求租约（E01-B）：需求按**消费者身份**登记（租约），关闭窗口只释放自己的那一份。
/// </summary>
public sealed class HardwareClientAdapter : IHardwareClient
{
    /// <summary>兼容桥使用的"legacy"消费者桶（旧单桶需求调用的落点）。</summary>
    public const string LegacyConsumerId = "legacy";

    /// <summary>Web 消费者桶：它的 Web 需求经 <c>SetWebDataDemand</c> 语义映射到采集层。</summary>
    public const string WebConsumerId = "web";

    private readonly IHardwareCollector _collector;
    private readonly DemandRegistry _demands = new DemandRegistry();
    private readonly SnapshotPublisher _fallbackPublisher = new SnapshotPublisher();
    private volatile bool _disposed;

    /// <param name="monitor">被包装的采集器（由组合根创建并持有）。</param>
    /// <param name="snapshotKeys">
    /// [兼容保留] 旧参数：快照覆盖的指标 Key 集合（组合根从配置取，适配器不读全局 Settings）。
    /// <para>★ E01-A 起**不再使用**：快照的样本集合改由**生产者**（采集层）决定
    /// （映射表 Key ∪ 用户配置的监控项 Key），消费方不再自行拼装样本。</para>
    /// </param>
    public HardwareClientAdapter(HardwareMonitor monitor, IReadOnlyList<string>? snapshotKeys = null)
        : this((IHardwareCollector)(monitor ?? throw new ArgumentNullException(nameof(monitor))), snapshotKeys)
    {
    }

    /// <summary>
    /// 测试接缝：以能力面注入采集层（生产实现仍是 <see cref="HardwareMonitor"/>）。
    /// <para>★ 目的是让命令面 / 需求面 / 快照面能在**不打开真实硬件树**的环境里被断言。</para>
    /// </summary>
    internal HardwareClientAdapter(IHardwareCollector collector, IReadOnlyList<string>? snapshotKeys = null)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        // ★ snapshotKeys 兼容保留但**不再使用**（见公开构造函数的说明）。
        _ = snapshotKeys;
    }

    /// <summary>
    /// 读取**已发布**的最新快照。
    /// <para>★ 读取不推进 BatchId、不改变时效字段：同一批数据读多少次都返回同一份内容。</para>
    /// <para>★ 采集层尚未发布过任何批次时，返回明确的**空批次**（不伪造成功批次）。</para>
    /// </summary>
    public HardwareSnapshot GetLatestSnapshot()
    {
        ThrowIfDisposed();
        return ReadPublishedSnapshot();
    }

    /// <summary>
    /// 读已发布批次（**不做关闭检查**）：目录构建在关闭期仍需把在途构建自然跑完（R3 关闭边界）。
    /// </summary>
    private HardwareSnapshot ReadPublishedSnapshot()
    {
        HardwareSnapshot? published = _collector.PublishedSnapshot;
        return published ?? _fallbackPublisher.Current;
    }

    /// <summary>当前批次的批次级缺失原因（"initializing" / "collection-failed" / null）。</summary>
    public string? CurrentMissingReason => _collector.CurrentMissingReason;

    // R3：目录构建改到线程池单飞执行——调用线程（UI）只负责拿 Task，不再在返回 Task 前
    // 同步等待 SyncLock（传感器窗口每秒轮询，采样持锁时会把整条 UI 消息循环拖住）。
    private CatalogSnapshotCache? _catalogCache;

    public Task<HardwareCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return (_catalogCache ??= new CatalogSnapshotCache(BuildCatalog)).GetAsync(cancellationToken);
    }

    /// <summary>
    /// 构建目录：**结构**取自生产者已发布的目录（结构变化才递增版本），
    /// 传感器的**当前值**按 StableId 从已发布快照刷新。
    /// <para>★ 结构不变时不遍历 LHM 树（只做一次 StableId 匹配）。</para>
    /// </summary>
    private HardwareCatalog BuildCatalog()
    {
        HardwareCatalog? published = _collector.PublishedCatalog;
        if (published == null || published.Entries.Count == 0)
        {
            return new HardwareCatalog(published?.Version ?? 0, Array.Empty<HardwareCatalogEntry>());
        }

        HardwareSnapshot snapshot = ReadPublishedSnapshot();
        bool collectionFailed = string.Equals(
            _collector.CurrentMissingReason,
            SnapshotPublisher.CollectionFailedReason,
            StringComparison.Ordinal);

        Dictionary<string, float?> values = BuildSensorValueIndex(snapshot);

        var entries = new HardwareCatalogEntry[published.Entries.Count];
        for (int i = 0; i < published.Entries.Count; i++)
        {
            HardwareCatalogEntry entry = published.Entries[i];
            if (entry.SensorType == null)
            {
                // 设备条目没有"当前值"（目录里设备的值恒为 null）。
                entries[i] = entry;
                continue;
            }

            if (collectionFailed)
            {
                // ★ 采集失败：不保活上一批的值（缺失必须显式可见）。
                entries[i] = entry with { Value = null };
                continue;
            }

            entries[i] = values.TryGetValue(entry.StableId, out float? value)
                ? entry with { Value = value }
                : entry with { Value = null };
        }

        return new HardwareCatalog(published.Version, entries);
    }

    /// <summary>
    /// 快照 → "StableId → 当前值"索引（只收有稳定身份的样本；缺失样本也进索引，值为 null）。
    /// </summary>
    private static Dictionary<string, float?> BuildSensorValueIndex(HardwareSnapshot snapshot)
    {
        var index = new Dictionary<string, float?>(StringComparer.Ordinal);
        foreach (MetricSample sample in snapshot.Samples)
        {
            if (string.IsNullOrEmpty(sample.StableDeviceId)) continue;
            index[sample.StableDeviceId] = sample.Value;
        }
        return index;
    }

    public SamplingApplyResult ApplyConfig(SamplingConfig config)
    {
        ThrowIfDisposed();
        if (config == null) throw new ArgumentNullException(nameof(config));
        try
        {
            if (config.ControllerProbe.ModeRaw != -1
                && Enum.IsDefined(typeof(ControllerProbeMode), config.ControllerProbe.ModeRaw))
            {
                _collector.ApplyControllerProbeMode((ControllerProbeMode)config.ControllerProbe.ModeRaw);
            }
            if (config.DiskProbe.ModeRaw != -1
                && Enum.IsDefined(typeof(DiskProbeMode), config.DiskProbe.ModeRaw))
            {
                _collector.ApplyDiskProbeMode((DiskProbeMode)config.DiskProbe.ModeRaw);
            }
            _collector.RefreshHardwareConfig();

            // ★ E01-A：配置**实际生效**后回写 revision（进快照的 ConfigRevision）。
            _collector.RecordSamplingConfigRevision(config.Revision);

            return new SamplingApplyResult(Applied: true, Revision: config.Revision, FailureReason: null);
        }
        catch (Exception ex)
        {
            return new SamplingApplyResult(Applied: false, Revision: null, FailureReason: ex.Message);
        }
    }

    // ── 需求租约（E01-B）────────────────────────────────────────────────────────

    /// <summary>
    /// [兼容桥] 单桶需求：登记到 <see cref="LegacyConsumerId"/> 桶。
    /// <para>★ 不驱动 Web 需求通道：Web 需求只经 <see cref="WebConsumerId"/> 桶映射
    /// （否则传感器窗口登记需求会把 Web 层自己的合法需求误关掉）。</para>
    /// </summary>
    public void SubmitDemand(SamplingDemand demand)
    {
        ThrowIfDisposed();
        if (demand == null) throw new ArgumentNullException(nameof(demand));
        SubmitDemand(LegacyConsumerId, demand);
    }

    /// <summary>
    /// 按消费者身份登记 / 更新需求（同消费者保留最新值，**不覆盖**其他消费者）。
    /// </summary>
    public void SubmitDemand(string consumerId, SamplingDemand demand)
    {
        ThrowIfDisposed();
        if (demand == null) throw new ArgumentNullException(nameof(demand));
        if (string.IsNullOrWhiteSpace(consumerId))
            throw new ArgumentException("consumerId 不能为空", nameof(consumerId));

        _demands.Submit(consumerId, demand);

        // ★ 只有 "web" 桶驱动 SetWebDataDemand（保持既有 Web 需求语义）。
        if (string.Equals(consumerId, WebConsumerId, StringComparison.Ordinal))
        {
            _collector.SetWebDataDemand(demand.Web);
        }
    }

    /// <summary>
    /// 释放某个消费者的需求（关闭窗口时调用）。
    /// <para>★ 释放一个消费者**不影响**其他消费者的需求。</para>
    /// </summary>
    /// <returns>true 表示确实释放过一份已登记的需求。</returns>
    public bool ReleaseDemand(string consumerId)
    {
        ThrowIfDisposed();
        bool removed = _demands.Release(consumerId);

        if (removed && string.Equals(consumerId, WebConsumerId, StringComparison.Ordinal))
        {
            _collector.SetWebDataDemand(false);
        }

        return removed;
    }

    /// <summary>当前已登记的消费者数量（诊断用）。</summary>
    public int DemandConsumerCount => _demands.ConsumerCount;

    /// <summary>当前聚合需求（各消费者需求的按位或；诊断用）。</summary>
    public SamplingDemand AggregateDemand => _demands.Aggregate;

    public HardwareHealth GetHealth()
    {
        ThrowIfDisposed();
        return new HardwareHealth(
            State: _collector.IsReady ? HardwareState.Ready : HardwareState.Starting,
            CurrentOperation: null,
            ProgressSeq: _collector.SamplingGeneration,
            DiagnosticReason: HardwareMonitor.LastHardwareError);
    }

    public string? GetNetworkIP()
    {
        ThrowIfDisposed();
        return _collector.GetNetworkIP();
    }

    public Task<bool> RequestRescanAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        // 复用唯一采样协调器的限频入口：重复请求天然合并，不另起重扫路径。
        return _collector.RequestSamplingAsync(cancellationToken);
    }

    public void Dispose()
    {
        // 只标记自身关闭、拒绝后续操作；被包装的采集器由组合根（UIController）拥有并释放（Plan B §4.4）。
        _disposed = true;
        // R3 关闭边界：在飞目录构建自然完成（消费方各自检查disposed），此后新调用被拒。
        _catalogCache = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HardwareClientAdapter));
    }
}

/// <summary>
/// R3：目录构建的在途合并缓存。同一时刻至多一个构建在飞（不增加并发 LHM 访问），
/// 构建期间的请求共享同一 Task；完成后下一次调用触发新构建（传感器窗口轮询取到的
/// 始终是"最近一次完成"的完整目录）。取消在消费侧（各调用方自行检查 disposed/可见性）。
///
/// ★ E01-B：构建结果进入"已发布目录"缓存（CatalogVersion 版本化，旧版本不覆盖新）。
///   本类仍负责**在途合并**；目录的**版本**由生产者的 <see cref="CatalogPublisher"/> 维护。
/// </summary>
public sealed class CatalogSnapshotCache
{
    private readonly Func<HardwareCatalog> _build;
    private readonly object _gate = new();
    private Task<HardwareCatalog>? _inFlight;
    private int _buildCount;

    public CatalogSnapshotCache(Func<HardwareCatalog> build) => _build = build ?? throw new ArgumentNullException(nameof(build));

    /// <summary>已触发的构建次数（测试观测：在途合并不新增构建）。</summary>
    public int BuildCount { get { lock (_gate) return _buildCount; } }

    public Task<HardwareCatalog> GetAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_inFlight != null) return _inFlight; // 在途合并
            _buildCount++;
            var build = Task.Run(() =>
            {
                try { return _build(); }
                finally { ResetInFlight(); }
            });
            _inFlight = build;
            return build;
        }
    }

    private void ResetInFlight()
    {
        lock (_gate) { _inFlight = null; }
    }
}
