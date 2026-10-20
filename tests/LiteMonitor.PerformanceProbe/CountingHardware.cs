using System.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.PerformanceProbe;

/// <summary>
/// 空的 ISettings 实现。LHM 的 Hardware 基类构造函数需要它，
/// 探针不需要任何真实的设置项。
/// </summary>
internal sealed class ProbeSettings : ISettings
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public bool Contains(string name) => _values.ContainsKey(name);

    public void SetValue(string name, string value) => _values[name] = value;

    public string GetValue(string name, string value) => _values.TryGetValue(name, out var v) ? v : value;

    public void Remove(string name) => _values.Remove(name);
}

/// <summary>
/// IHardware 计数代理（继承 LHM 公开的 <see cref="Hardware"/> 基类）。
///
/// 目的：在不修改生产代码（HardwareMonitor / HardwareValueProvider / UIController / Settings）
/// 的前提下，统计 LibreHardwareMonitor **真实** 的 Update() 调用次数与耗时。
///
/// 为什么继承 Hardware 而不是直接实现 IHardware：
/// LHM 的 Computer.Hardware 是按分组现算出来的副本，硬件对象存放在各分组的
/// 强类型集合里（NVIDIA 分组是 List&lt;Hardware&gt;，CPU 分组是 List&lt;GenericCpu&gt;…）。
/// 只有当分组集合的元素类型是基类 Hardware 时，代理对象才能被放进去。
///
/// 只做计数与转发，不改变任何业务语义；所有成员都直接委托给被包装对象。
/// </summary>
internal sealed class CountingHardware : Hardware
{
    private readonly IHardware _inner;
    private long _updateCount;
    private long _updateWallTicks;

    public CountingHardware(IHardware inner, ISettings settings)
        : base(inner.Name, inner.Identifier, settings)
    {
        _inner = inner;
    }

    /// <summary>报告用标签：硬件名 + 类型。</summary>
    public string Label => $"{_inner.Name} [{_inner.HardwareType}]";

    /// <summary>真实 Update() 调用次数。</summary>
    public long UpdateCount => Interlocked.Read(ref _updateCount);

    /// <summary>真实 Update() 累计消耗的墙钟时间（毫秒）。</summary>
    public double UpdateWallMs => Interlocked.Read(ref _updateWallTicks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>被包装的真实硬件对象。</summary>
    public IHardware Inner => _inner;

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _updateCount, 0);
        Interlocked.Exchange(ref _updateWallTicks, 0);
    }

    public override void Update()
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            _inner.Update();
        }
        finally
        {
            Interlocked.Increment(ref _updateCount);
            Interlocked.Add(ref _updateWallTicks, Stopwatch.GetTimestamp() - start);
        }
    }

    // ---------------- 以下全部是透明转发 ----------------

    public override HardwareType HardwareType => _inner.HardwareType;

    public override ISensor[] Sensors => _inner.Sensors;

    public override IHardware[] SubHardware => _inner.SubHardware;

    public override IHardware? Parent => _inner.Parent;

    public override IDictionary<string, string> Properties => _inner.Properties;

    public override string GetReport() => _inner.GetReport();

    /// <summary>关闭时要把被包装的真实硬件一起关掉，否则驱动句柄会泄漏。</summary>
    public override void Close()
    {
        try { (_inner as Hardware)?.Close(); } catch { }
        try { base.Close(); } catch { }
    }
}
