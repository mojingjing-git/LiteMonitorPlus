using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.src.SystemServices; // HardwareMonitor 与既有 HardwareServices 类型（B5 命名空间统一前的现状）

namespace LiteMonitor.SystemServices; // 新文件按目标命名空间方案落位（architecture-assert 白名单导向），旧文件由 B5 机械统一

/// <summary>
/// C1 契约的进程内首版适配器：包装现有 <see cref="HardwareMonitor"/>，不引入额外轮询、
/// 不改变任何现有行为（P-B1B2 B1 步骤③）。采样入口统一委托既有唯一协调器
/// <c>UpdateIfDueAsync</c>；设备目录由既有静态枚举组装。
/// 带注释的「B2 补齐」项（快照单位/样本时效/稳定设备绑定/需求多通道）在消费方迁移时逐项做实，
/// 在此之前消费方仍走原路径，因此这些占位值不进入任何现有显示或落盘路径。
/// </summary>
public sealed class HardwareClientAdapter : IHardwareClient
{
    private readonly HardwareMonitor _monitor;
    private readonly IReadOnlyList<string> _snapshotKeys;
    private long _batchId;
    private volatile bool _disposed;

    /// <param name="monitor">被包装的采集器（由组合根创建并持有）。</param>
    /// <param name="snapshotKeys">快照覆盖的指标 Key 集合（组合根从配置取，适配器不读全局 Settings）。</param>
    public HardwareClientAdapter(HardwareMonitor monitor, IReadOnlyList<string>? snapshotKeys = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _snapshotKeys = snapshotKeys ?? Array.Empty<string>();
    }

    public HardwareSnapshot GetLatestSnapshot()
    {
        ThrowIfDisposed();
        var samples = new List<MetricSample>(_snapshotKeys.Count);
        foreach (var key in _snapshotKeys)
        {
            float? value = _monitor.Get(key);
            samples.Add(new MetricSample(
                Key: key,
                StableDeviceId: null,          // B2：由设备绑定解释（F07 稳定身份）
                Value: value,
                Unit: null,                    // B2 随消费方迁移填充；显示端当前自行格式化（B4 前行为不变）
                Quality: value.HasValue ? SampleQuality.Good : SampleQuality.Missing,
                MissingReason: value.HasValue ? null : "no-value",
                SampleAgeMs: 0));              // B2：接 SamplingStatistics 的采样计时
        }
        return new HardwareSnapshot(
            BatchId: unchecked(++_batchId),
            CatalogVersion: _monitor.SamplingGeneration,
            ConfigRevision: -1,                // B2：对应 ApplyConfig 的生效 revision
            Samples: samples,
            SamplingDuration: TimeSpan.Zero,   // B2：接 SamplingStatistics
            TimestampMonoMs: Environment.TickCount64);
    }

    public Task<HardwareCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new List<HardwareCatalogEntry>();
        if (_monitor.IsReady)
        {
            foreach (var name in HardwareMonitor.ListAllNetworks()) entries.Add(Entry(name, "Network"));
            foreach (var name in HardwareMonitor.ListAllDisks()) entries.Add(Entry(name, "Disk"));
            foreach (var name in HardwareMonitor.ListAllGpus()) entries.Add(Entry(name, "Gpu"));
            foreach (var name in HardwareMonitor.ListAllFans()) entries.Add(Entry(name, "Fan"));
            foreach (var name in HardwareMonitor.ListAllMoboTemps()) entries.Add(Entry(name, "MoboTemp"));
        }
        var catalog = new HardwareCatalog(_monitor.SamplingGeneration, entries);
        return Task.FromResult(catalog);
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
                _monitor.ApplyControllerProbeMode((ControllerProbeMode)config.ControllerProbe.ModeRaw);
            }
            if (config.DiskProbe.ModeRaw != -1
                && Enum.IsDefined(typeof(DiskProbeMode), config.DiskProbe.ModeRaw))
            {
                _monitor.ApplyDiskProbeMode((DiskProbeMode)config.DiskProbe.ModeRaw);
            }
            _monitor.RefreshHardwareConfig();
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
        // V1 仅映射 Web 通道（现状唯一显式需求入口）；UI/任务栏/历史当前隐式常开，B2 迁移时逐通道接线。
        _monitor.SetWebDataDemand(demand.Web);
    }

    public HardwareHealth GetHealth()
    {
        ThrowIfDisposed();
        return new HardwareHealth(
            State: _monitor.IsReady ? HardwareState.Ready : HardwareState.Starting,
            CurrentOperation: null,
            ProgressSeq: _monitor.SamplingGeneration,
            DiagnosticReason: HardwareMonitor.LastHardwareError);
    }

    public Task<bool> RequestRescanAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        // 复用唯一采样协调器的限频入口：重复请求天然合并，不另起重扫路径。
        return _monitor.UpdateIfDueAsync(cancellationToken);
    }

    public void Dispose()
    {
        // 只标记自身关闭、拒绝后续操作；被包装的采集器由组合根（UIController）拥有并释放（Plan B §4.4）。
        _disposed = true;
    }

    private static HardwareCatalogEntry Entry(string name, string deviceType) => new(
        StableId: name,          // B2：SystemHardwarPage/LiteTreeView 迁移时替换为 F07 稳定身份
        RuntimeId: name,
        ParentStableId: null,
        DeviceType: deviceType,
        SensorType: null,
        Available: true);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HardwareClientAdapter));
    }
}
