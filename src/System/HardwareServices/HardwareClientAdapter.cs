using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware; // 目录枚举在采集层内进行（System 层允许引用 LHM；UI/Web 不允许）
using LiteMonitor.Contracts;
using LiteMonitor.src.SystemServices; // HardwareMonitor 与既有 HardwareServices 类型（B5 命名空间统一前的现状）

namespace LiteMonitor.SystemServices; // 新文件按目标命名空间方案落位（architecture-assert 白名单导向），旧文件由 B5 机械统一

/// <summary>
/// C1 契约的进程内首版适配器：包装现有 <see cref="HardwareMonitor"/>。
///
/// ★ E01-A（生产者发布）：<see cref="GetLatestSnapshot"/> 现在**只返回已发布批次**——
///   读取**不**推进 BatchId、**不**改变时效字段；批次由采集层在真实采样完成后原子发布。
///
/// ★ 目录与需求（E01-B）与命令面（E01-C）在后续提交里补齐。
/// </summary>
public sealed class HardwareClientAdapter : IHardwareClient
{
    private readonly HardwareMonitor _monitor;
    private readonly IHardwareCollector _collector;
    private readonly SnapshotPublisher _fallbackPublisher = new SnapshotPublisher();
    private volatile SamplingDemand? _lastDemand;
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
        _monitor = monitor;
    }

    /// <summary>
    /// 测试接缝：以能力面注入采集层（生产实现仍是 <see cref="HardwareMonitor"/>）。
    /// <para>★ 目的是让快照面能在**不打开真实硬件树**的环境里被断言。</para>
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

    private HardwareCatalog BuildCatalog()
    {
        var entries = new List<HardwareCatalogEntry>();
        var monitor = _monitor;
        var computer = monitor?.ComputerInstance;
        if (computer != null && monitor!.IsReady)
        {
            // 锁内一次性快照设备/传感器树（含当前值与峰值），消费方不再持有 LHM 活对象。
            lock (monitor.SyncLock)
            {
                foreach (IHardware hw in computer.Hardware)
                    AddHardwareEntries(entries, hw, parentStableId: null);
            }
        }
        return new HardwareCatalog(monitor?.SamplingGeneration ?? 0, entries);
    }

    private static void AddHardwareEntries(List<HardwareCatalogEntry> entries, IHardware hw, string? parentStableId)
    {
        string id = hw.Identifier.ToString();
        entries.Add(new HardwareCatalogEntry(
            StableId: id,
            RuntimeId: id,
            ParentStableId: parentStableId,
            DeviceType: hw.HardwareType.ToString(),
            SensorType: null,
            Available: true,
            Name: hw.Name));
        foreach (ISensor sensor in hw.Sensors)
        {
            string sensorId = sensor.Identifier.ToString();
            entries.Add(new HardwareCatalogEntry(
                StableId: sensorId,
                RuntimeId: sensorId,
                ParentStableId: id,
                DeviceType: hw.HardwareType.ToString(),
                SensorType: sensor.SensorType.ToString(),
                Available: true,
                Name: sensor.Name,
                Value: sensor.Value,
                Max: sensor.Max));
        }
        foreach (IHardware sub in hw.SubHardware)
            AddHardwareEntries(entries, sub, id);
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

    public void SubmitDemand(SamplingDemand demand)
    {
        ThrowIfDisposed();
        if (demand == null) throw new ArgumentNullException(nameof(demand));
        // V1 只存储合并需求记录、不驱动采集层：Web 通道仍由 LiteWebServer 的需求回调持有
        // （此处若强行映射 SetWebDataDemand，传感器窗口提交需求会把 Web 需求误关掉）。
        // 各通道真实合流随 B3 应用协调接线；记录可从 GetHealth/诊断侧观察。
        _lastDemand = demand;
    }

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
