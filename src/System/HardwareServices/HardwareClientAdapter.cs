using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware; // 目录枚举在采集层内进行（System 层允许引用 LHM；UI/Web 不允许）
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
    private volatile SamplingDemand? _lastDemand;
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
        var computer = _monitor.ComputerInstance;
        if (computer != null && _monitor.IsReady)
        {
            // 锁内一次性快照设备/传感器树（含当前值与峰值），消费方不再持有 LHM 活对象。
            lock (_monitor.SyncLock)
            {
                foreach (IHardware hw in computer.Hardware)
                    AddHardwareEntries(entries, hw, parentStableId: null);
            }
        }
        return Task.FromResult(new HardwareCatalog(_monitor.SamplingGeneration, entries));
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
        // V1 只存储合并需求记录、不驱动采集层：Web 通道仍由 LiteWebServer 的需求回调持有
        // （此处若强行映射 SetWebDataDemand，传感器窗口提交需求会把 Web 需求误关掉）。
        // 各通道真实合流随 B3 应用协调接线；记录可从 GetHealth/诊断侧观察。
        _lastDemand = demand;
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

    public string? GetNetworkIP()
    {
        ThrowIfDisposed();
        return _monitor.GetNetworkIP();
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

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HardwareClientAdapter));
    }
}
