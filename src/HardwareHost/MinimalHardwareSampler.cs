using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.Contracts;

namespace LiteMonitor.HardwareHost;

/// <summary>
/// I2 最小真实采样器：直接用 LHM <see cref="Computer"/> 打开 CPU/GPU，按调用方给定的
/// 周期产出一批 <c>CPU.Load/CPU.Temp/CPU.Clock + GPU.{i}.Load/Temp/Fan</c>（i=0 另带
/// <c>GPU.*</c> 别名）样本。刻意**不**复用生产 <c>HardwareMonitor</c>（其 Settings/
/// MetricUtils/WinForms 依赖的迁入属 I3）——本类型只证明"子进程真实读数 + IPC 语义"。
/// 选择策略：负载取 Total/Core 传感器、温度与时钟取最大值、风扇取最大 Fan——是保守
/// 的首版启发式，与生产 SensorMap 的差异在 I2 报告"指标集差异"节如实列出。
/// </summary>
public sealed class MinimalHardwareSampler : IDisposable
{
    /// <summary>测试/原子性探针模式：不读硬件，按固定 12 键输出 batch*1000+i 合成值。</summary>
    public const int TestPatternSampleCount = 12;

    private readonly Computer? _computer;
    private readonly bool _testPattern;
    private long _batchId;
    private long _generation;
    private bool _disposed;

    public MinimalHardwareSampler(bool testPattern = false)
    {
        _testPattern = testPattern;
        if (!testPattern)
        {
            // 最小设备面：仅 CPU/GPU（母板/存储/网络/内存控制器不在 I2 范围，见报告已知边界）。
            _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            _computer.Open();
        }
        _generation++;
    }

    /// <summary>当前目录代次（每次重新 Open +1；测试模式下恒为构造次数）。</summary>
    public long Generation => _generation;

    /// <summary>执行一轮采集并组装完整快照（原子批次：值与批次号同源，不混代次）。</summary>
    /// <param name="configRevision">当前生效的配置 revision（盖在快照上供调用方判定"实际生效版本"）。</param>
    /// <param name="sessionEpoch">会话纪元（E02 §6.7：Host 重启/重连时递增，旧会话快照不得混入新会话）。</param>
    public HardwareSnapshot SampleOnce(int configRevision, int sessionEpoch = 0)
    {
        ThrowIfDisposed();
        long batch = unchecked(++_batchId);
        long timestamp = Environment.TickCount64;
        var samples = new List<MetricSample>();
        var sw = Stopwatch.StartNew();

        if (_testPattern)
        {
            for (int i = 0; i < TestPatternSampleCount; i++)
                samples.Add(new MetricSample(
                    Key: TestKey(i),
                    StableDeviceId: null,
                    Value: batch * 1000f + i,
                    Unit: null,
                    Quality: SampleQuality.Good,
                    MissingReason: null,
                    SampleAgeMs: 0));
        }
        else
        {
            foreach (IHardware hw in _computer!.Hardware)
                hw.Update();
            Collect(samples);
        }

        sw.Stop();
        return new HardwareSnapshot(
            BatchId: batch,
            CatalogVersion: _generation,
            ConfigRevision: configRevision,
            Samples: samples,
            SamplingDuration: sw.Elapsed,
            TimestampMonoMs: timestamp,
            SessionEpoch: sessionEpoch);
    }

    /// <summary>
    /// 显式重扫（E02 §6「显式硬件重扫」）：重建设备/传感器目录并递增目录代次。
    /// <para>最小 Host 的重扫语义 = **目录重建 + 代次递增**（CatalogVersion 变化可观测，
    /// 目录与读数不混代次）；真实设备重开（<c>Computer.Close/Open</c>）属 I3/E09 的
    /// 采集器迁移范围，不在最小 Host 内实施（已知边界，见 E02 验收报告）。</para>
    /// <para>调用方须保证与采样互斥（Host 侧用采样闸门串行化）。</para>
    /// </summary>
    public HardwareCatalog Rescan()
    {
        ThrowIfDisposed();
        _generation++;
        return BuildCatalog();
    }

    /// <summary>设备/传感器目录（供 catalog 命令；结构与 HardwareClientAdapter 的锁内遍历一致）。</summary>
    public HardwareCatalog BuildCatalog()
    {
        ThrowIfDisposed();
        var entries = new List<HardwareCatalogEntry>();
        if (_computer != null)
        {
            foreach (IHardware hw in _computer.Hardware)
                AddEntries(entries, hw, parentStableId: null);
        }
        return new HardwareCatalog(_generation, entries);
    }

    private static void AddEntries(List<HardwareCatalogEntry> entries, IHardware hw, string? parentStableId)
    {
        string id = hw.Identifier.ToString();
        entries.Add(new HardwareCatalogEntry(
            StableId: id, RuntimeId: id, ParentStableId: parentStableId,
            DeviceType: hw.HardwareType.ToString(), SensorType: null,
            Available: true, Name: hw.Name));
        foreach (ISensor sensor in hw.Sensors)
        {
            string sensorId = sensor.Identifier.ToString();
            entries.Add(new HardwareCatalogEntry(
                StableId: sensorId, RuntimeId: sensorId, ParentStableId: id,
                DeviceType: hw.HardwareType.ToString(), SensorType: sensor.SensorType.ToString(),
                Available: true, Name: sensor.Name, Value: sensor.Value, Max: sensor.Max));
        }
        foreach (IHardware sub in hw.SubHardware)
            AddEntries(entries, sub, id);
    }

    private void Collect(List<MetricSample> samples)
    {
        IHardware? cpu = _computer!.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu != null)
        {
            AddSample(samples, "CPU.Load", FirstSensor(cpu, SensorType.Load, "CPU Total"));
            AddSample(samples, "CPU.Temp", MaxSensor(cpu, SensorType.Temperature));
            AddSample(samples, "CPU.Clock", MaxSensor(cpu, SensorType.Clock));
        }
        else
        {
            AddMissing(samples, "CPU.Load");
            AddMissing(samples, "CPU.Temp");
            AddMissing(samples, "CPU.Clock");
        }

        var gpus = _computer.Hardware.Where(h => h.HardwareType
            is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel).ToList();
        if (gpus.Count == 0)
        {
            AddMissing(samples, "GPU.Load");
            AddMissing(samples, "GPU.Temp");
            AddMissing(samples, "GPU.Fan");
        }
        for (int i = 0; i < gpus.Count; i++)
        {
            string prefix = $"GPU.{i}.";
            AddSample(samples, prefix + "Load", FirstSensor(gpus[i], SensorType.Load, "GPU Core"));
            AddSample(samples, prefix + "Temp", MaxSensor(gpus[i], SensorType.Temperature));
            AddSample(samples, prefix + "Fan", MaxSensor(gpus[i], SensorType.Fan));
            if (i == 0)
            {
                // i=0 别名与生产 Key 兼容（生产公开 Key "GPU.Load" 恒指首卡，Plan A §4）。
                AddSample(samples, "GPU.Load", FirstSensor(gpus[i], SensorType.Load, "GPU Core"));
                AddSample(samples, "GPU.Temp", MaxSensor(gpus[i], SensorType.Temperature));
                AddSample(samples, "GPU.Fan", MaxSensor(gpus[i], SensorType.Fan));
            }
        }
    }

    private static void AddSample(List<MetricSample> samples, string key, ISensor? sensor)
    {
        if (sensor?.Value.HasValue == true)
        {
            float v = sensor.Value.Value;
            samples.Add(new MetricSample(key, null, v, null, SampleQuality.Good, null, 0));
        }
        else
        {
            AddMissing(samples, key);
        }
    }

    private static void AddMissing(List<MetricSample> samples, string key) =>
        samples.Add(new MetricSample(key, null, null, null, SampleQuality.Missing, "no-sensor", 0));

    private static ISensor? FirstSensor(IHardware hw, SensorType type, string name) =>
        hw.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name)
        ?? hw.Sensors.FirstOrDefault(s => s.SensorType == type);

    private static ISensor? MaxSensor(IHardware hw, SensorType type)
    {
        ISensor? best = null;
        foreach (ISensor s in hw.Sensors)
        {
            if (s.SensorType != type || !s.Value.HasValue) continue;
            if (best == null || s.Value.Value > best.Value!.Value) best = s;
        }
        return best;
    }

    internal static string TestKey(int i) => i switch
    {
        0 => "CPU.Load",
        1 => "CPU.Temp",
        2 => "CPU.Clock",
        _ => $"GPU.{(i - 3) / 3}." + (((i - 3) % 3) switch { 0 => "Load", 1 => "Temp", _ => "Fan" })
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _computer?.Close();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
