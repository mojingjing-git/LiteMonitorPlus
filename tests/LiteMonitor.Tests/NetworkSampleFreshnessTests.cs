using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// #96 / F03：网络值的**时效**、**失败恢复**与**自动选卡**。
    ///
    /// <para>缺陷（修复前）：<c>NetworkManager</c> 的 <c>lastValidMap</c> 冻结回退**永不失效**，
    /// 上层的通用 <c>_lastValidMap</c>、启动缓存、采集中读取分支也各自能把旧值重新发布成"新样本"。
    /// 网卡停更（链路变化 / LHM 传感器失效）时，面板上的 NET.Up / NET.Down 会**卡住不动**。</para>
    ///
    /// <para>修复后：样本带**值 / 设备身份 / 设备代次 / 成功采样时间 / 质量**；
    /// 旧值最多保留 3 个采样周期（上限 15 秒），保留期内标 Stale，到期为 Missing；
    /// 读缓存**不刷新**样本寿命；自动模式按"存活 + 采样成功 + 有效流量"重新评分，
    /// **读取失败**才触发有限重选；手动模式设备消失时保留选择并显示不可用。</para>
    ///
    /// <para>这些用例**不启动 LHM、不碰驱动、不访问外网**：
    ///  - 网卡用 LHM 公开的 <c>Hardware</c> 基类桩（只统计 Update 次数）；
    ///  - <c>Computer.Hardware</c> 通过 LHM 内部的 group 列表注入（与既有磁盘用例同一手法）；
    ///  - 时间一律用**可注入的假时钟**，不使用 <c>Thread.Sleep</c>。</para>
    /// </summary>
    public class NetworkSampleFreshnessTests
    {
        // =========================================================
        // 测试替身
        // =========================================================

        /// <summary>假时钟：单调时间戳可推进，墙上时间与它独立。</summary>
        private sealed class FakeTimeProvider : TimeProvider
        {
            private long _ticks;

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => Interlocked.Read(ref _ticks);
            public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

            public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
        }

        /// <summary>LHM 的空设置实现（Hardware 基类构造函数需要）。</summary>
        private sealed class FakeLhmSettings : ISettings
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
            public bool Contains(string name) => _values.ContainsKey(name);
            public void SetValue(string name, string value) => _values[name] = value;
            public string GetValue(string name, string value) => _values.TryGetValue(name, out var v) ? v : value;
            public void Remove(string name) => _values.Remove(name);
        }

        /// <summary>假的 Throughput 传感器：值可随时改写（模拟停更 / 恢复）。</summary>
        private sealed class FakeThroughputSensor : ISensor
        {
            public FakeThroughputSensor(string name, float value) { Name = name; Value = value; }
            public string Name { get; set; }
            public float? Value { get; set; }
            public SensorType SensorType => SensorType.Throughput;
            public IControl Control => null!;
            public IHardware Hardware => null!;
            public Identifier Identifier => default;
            public int Index => 0;
            public bool IsDefaultHidden => false;
            public float? Max => null;
            public float? Min => null;
            public IReadOnlyList<IParameter> Parameters => Array.Empty<IParameter>();
            public IEnumerable<SensorValue> Values => Array.Empty<SensorValue>();
            public TimeSpan ValuesTimeWindow { get; set; }
            public void ResetMin() { }
            public void ResetMax() { }
            public void ClearValues() { }
            public void Accept(IVisitor visitor) { }
            public void Traverse(IVisitor visitor) { }
        }

        /// <summary>假的网卡硬件：只统计真实 Update 次数，不接触驱动。</summary>
        private sealed class FakeNetworkHardware : Hardware
        {
            private readonly ISensor[] _sensors;

            public FakeNetworkHardware(string name, params ISensor[] sensors)
                : base(name, new Identifier(new[] { name }), new FakeLhmSettings())
            {
                _sensors = sensors;
            }

            public override HardwareType HardwareType => HardwareType.Network;
            public override ISensor[] Sensors => _sensors;
            public int UpdateCount { get; private set; }
            public override void Update() => UpdateCount++;
        }

        private sealed class Fixture
        {
            public Settings Cfg = new();
            public SensorMap Map = new();
            public Computer Computer = null!;
            public NetworkManager Manager = null!;
            public HardwareValueProvider Provider = null!;
            public FakeTimeProvider Clock = new();
            public Dictionary<string, float> LastValid = new();
            public object Sync = new();
            public List<FakeNetworkHardware> Nics = new();
        }

        /// <summary>
        /// 造一个只含指定假网卡的 Computer。
        /// <para>★ LHM 0.9.6 的 <c>Computer.Hardware</c> 每次访问都返回**新建的列表副本**，
        /// 直接往返回值上 Add 是无效的；必须往内部的 group 列表里塞（既有磁盘用例同一手法）。</para>
        /// </summary>
        private static Computer NewComputerWith(params Hardware[] hardware)
        {
            var computer = new Computer();
            var groups = (IList)PrivateField(typeof(Computer), "_groups", false).GetValue(computer)!;
            Type groupType = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Memory.MemoryGroup", true)!;
            object group = RuntimeHelpers.GetUninitializedObject(groupType);
            PrivateField(groupType, "_hardware", false).SetValue(group, hardware.ToList());
            groups.Add(group);
            return computer;
        }

        private static FieldInfo PrivateField(Type type, string name, bool isStatic)
        {
            var field = type.GetField(name, BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance));
            Assert.NotNull(field);
            return field!;
        }

        private static Fixture NewFixture(params FakeNetworkHardware[] nics)
        {
            var f = new Fixture();
            f.Nics.AddRange(nics);
            f.Computer = NewComputerWith(nics.Cast<Hardware>().ToArray());

            f.Manager = new NetworkManager(new PerformanceCounterManager(), f.Clock);
            f.Provider = new HardwareValueProvider(
                f.Computer,
                f.Cfg,
                f.Map,
                f.Manager,
                new DiskManager(),
                null,
                new PerformanceCounterManager(),
                f.Sync,
                f.LastValid,
                new HardwareSamplingStatistics(),
                f.Clock);

            return f;
        }

        private static FakeNetworkHardware NewNic(string name, float up, float down = 0f)
        {
            return new FakeNetworkHardware(name,
                new FakeThroughputSensor("Upload", up),
                new FakeThroughputSensor("Download", down));
        }

        private NetworkSample Read(Fixture f, string key = "NET.Up")
            => f.Manager.GetBestSample(key, f.Computer, f.Cfg, f.LastValid, f.Sync);

        // =========================================================
        // ① 全链路：正值 -> 失败 -> TTL 边界 -> 缺失 -> 恢复
        // =========================================================

        [Fact]
        public void 正值_读取失败_保留期内旧值_到期后缺失_恢复后重新出值()
        {
            var nic = NewNic("F03NicA", 1234f);
            var f = NewFixture(nic);

            // 1) 正值
            NetworkSample positive = Read(f);
            Assert.Equal(NetworkSampleQuality.Fresh, positive.Quality);
            Assert.Equal(1234f, positive.Value);

            // 2) 读取失败（网卡停更：LHM 传感器变成 NaN）
            ((FakeThroughputSensor)nic.Sensors[0]).Value = float.NaN;
            f.Clock.Advance(TimeSpan.FromSeconds(1));

            NetworkSample failed = Read(f);
            // 保留期内：旧值仍可见，但质量是 Stale（明确标记为陈旧）
            Assert.Equal(NetworkSampleQuality.Stale, failed.Quality);
            Assert.Equal(1234f, failed.Value);

            // 3) TTL 边界：保留期 = 3 个采样周期 = 3 秒（默认 1 秒周期）
            f.Clock.Advance(TimeSpan.FromSeconds(2));

            NetworkSample expired = Read(f);
            Assert.Equal(NetworkSampleQuality.Missing, expired.Quality);
            Assert.False(expired.HasValue);

            // 4) 恢复：采集层推进了采样轮次 -> 重新拿到 Fresh 样本
            ((FakeThroughputSensor)nic.Sensors[0]).Value = 4321f;
            f.Manager.Samples.AdvanceSampleEpoch();

            NetworkSample recovered = Read(f);
            Assert.Equal(NetworkSampleQuality.Fresh, recovered.Quality);
            Assert.Equal(4321f, recovered.Value);
        }

        // =========================================================
        // ② 读缓存 1 万次不刷新 TTL
        // =========================================================

        [Fact]
        public void 读一万次不会刷新样本寿命()
        {
            var nic = NewNic("F03NicA", 777f);
            var f = NewFixture(nic);

            Assert.Equal(NetworkSampleQuality.Fresh, Read(f).Quality);

            // 采集层不再推进采样轮次（网卡停更），但 UI / Web 继续高频读取
            for (int i = 0; i < 10000; i++)
            {
                Read(f);
            }

            // 超过保留期后必须变成缺失：读缓存不能给旧值续命（这正是 #96 的冻结机制）
            f.Clock.Advance(TimeSpan.FromSeconds(4));

            NetworkSample expired = Read(f);
            Assert.Equal(NetworkSampleQuality.Missing, expired.Quality);
            Assert.False(expired.HasValue);
        }

        [Fact]
        public void 同一采样轮次内重复读取只更新值不刷新成功时间()
        {
            var nic = NewNic("F03NicA", 10f);
            var f = NewFixture(nic);

            Read(f);
            f.Clock.Advance(TimeSpan.FromSeconds(2));

            // 同一轮次内的重复读：值会更新，但成功时间不刷新
            ((FakeThroughputSensor)nic.Sensors[0]).Value = 20f;
            Read(f);

            // 再走 1 秒就超过保留期（2 + 1 = 3 秒）
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(NetworkSampleQuality.Missing, Read(f).Quality);
        }

        // =========================================================
        // ③ 真实 0 流量始终 Fresh（0 是有效数据，不是缺失）
        // =========================================================

        [Fact]
        public void 真实0流量是有效样本而不是缺失()
        {
            var nic = NewNic("F03NicA", 0f);
            var f = NewFixture(nic);

            NetworkSample sample = Read(f);
            Assert.Equal(NetworkSampleQuality.Fresh, sample.Quality);
            Assert.True(sample.HasValue);
            Assert.Equal(0f, sample.Value);
        }

        [Fact]
        public void 空闲网卡不会被当成坏网卡也不会触发换卡()
        {
            var busy = NewNic("F03BusyNic", 5000f);
            var idle = NewNic("F03IdleNic", 0f);
            var f = NewFixture(busy, idle);

            // 先让"忙"的那张被选中
            Assert.Equal(5000f, Read(f).Value);
            Assert.Equal("F03BusyNic", f.Manager.SelectedDeviceName);

            // 两张都空闲时不应来回换卡
            ((FakeThroughputSensor)busy.Sensors[0]).Value = 0f;
            f.Clock.Advance(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 5; i++)
            {
                Read(f);
                f.Clock.Advance(TimeSpan.FromSeconds(4));
            }

            Assert.Equal("F03BusyNic", f.Manager.SelectedDeviceName);
        }

        // =========================================================
        // ④ 拔卡恢复
        // =========================================================

        [Fact]
        public void 拔掉旧网卡后另一张网卡可以恢复()
        {
            var old = NewNic("F03OldNic", 8000f);
            var backup = NewNic("F03BackupNic", 64f);
            var f = NewFixture(old, backup);

            // 先选中"旧"网卡
            Assert.Equal(8000f, Read(f).Value);
            Assert.Equal("F03OldNic", f.Manager.SelectedDeviceName);

            // 拔掉旧网卡：它的传感器停更（NaN）
            ((FakeThroughputSensor)old.Sensors[0]).Value = float.NaN;

            // 保留期内仍然显示旧值（Stale），到期后必须变成缺失而不是继续冻结
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            NetworkSample stale = Read(f);
            Assert.Equal(NetworkSampleQuality.Stale, stale.Quality);

            // 超过重选窗口后：读取失败触发**有限重选**，另一张网卡接管
            f.Clock.Advance(TimeSpan.FromSeconds(3));
            NetworkSample recovered = Read(f);
            Assert.Equal(NetworkSampleQuality.Fresh, recovered.Quality);
            Assert.Equal(64f, recovered.Value);
            Assert.Equal("F03BackupNic", f.Manager.SelectedDeviceName);
        }

        [Fact]
        public void 重选是有限的_不会因为持续失败而无限切换()
        {
            var broken = NewNic("F03BrokenNic", 100f);
            var f = NewFixture(broken);

            Read(f);
            ((FakeThroughputSensor)broken.Sensors[0]).Value = float.NaN;

            // 同一个重选窗口内反复读取：不会每次都重扫（重选有节流）
            for (int i = 0; i < 20; i++)
            {
                Read(f);
                f.Clock.Advance(TimeSpan.FromMilliseconds(10));
            }

            // 超过保留期 -> 缺失（明确显示 "--"，而不是把旧值冻住）
            f.Clock.Advance(TimeSpan.FromSeconds(4));
            Assert.Equal(NetworkSampleQuality.Missing, Read(f).Quality);
        }

        // =========================================================
        // ⑤ 手动选卡：不被改写，设备消失时显示不可用
        // =========================================================

        [Fact]
        public void 手动选卡不会被自动改写成另一张网卡()
        {
            var preferred = NewNic("F03PreferredNic", 100f);
            var other = NewNic("F03OtherNic", 9999f);
            var f = NewFixture(preferred, other);
            f.Cfg.PreferredNetwork = "F03PreferredNic";

            Assert.Equal(100f, Read(f).Value);
            Assert.Equal("F03PreferredNic", f.Manager.SelectedDeviceName);

            // 手动选中的网卡读失败：仍然保留手动选择，不切到另一张
            ((FakeThroughputSensor)preferred.Sensors[0]).Value = float.NaN;
            f.Clock.Advance(TimeSpan.FromSeconds(4));

            Read(f);
            Assert.Equal("F03PreferredNic", f.Cfg.PreferredNetwork);
            Assert.Equal("F03PreferredNic", f.Manager.SelectedDeviceName);
        }

        [Fact]
        public void 手动选卡设备消失时保留选择并显示不可用()
        {
            var f = NewFixture();
            f.Cfg.PreferredNetwork = "F03GoneNic";

            NetworkSample sample = Read(f);

            // 设备消失：显示缺失（不可用），但**保留**用户的选择
            Assert.Equal(NetworkSampleQuality.Missing, sample.Quality);
            Assert.Equal("F03GoneNic", f.Cfg.PreferredNetwork);
            Assert.Equal("F03GoneNic", f.Manager.SelectedDeviceName == "" ? "F03GoneNic" : f.Manager.SelectedDeviceName);
        }

        // =========================================================
        // ⑥ 设备删除 / 首选变化 / 硬件重载：立即失效
        // =========================================================

        [Fact]
        public void 设备删除后旧样本立即作废()
        {
            var nic = NewNic("F03NicA", 555f);
            var f = NewFixture(nic);

            Assert.Equal(555f, Read(f).Value);

            // 设备被删除：样本立即作废（不允许旧值继续冒充当前读数）
            f.Manager.InvalidateNetworkSamples();
            ((FakeThroughputSensor)nic.Sensors[0]).Value = float.NaN;

            NetworkSample sample = Read(f);
            Assert.Equal(NetworkSampleQuality.Missing, sample.Quality);
            Assert.False(sample.HasValue);
        }

        [Fact]
        public void 首选网卡变化会立即失效样本并推进设备代次()
        {
            var nic = NewNic("F03NicA", 555f);
            var f = NewFixture(nic);

            Assert.Equal(555f, Read(f).Value);
            long generationBefore = f.Manager.DeviceGeneration;

            // 用户改选网卡 -> 采集代次开始时立即失效旧样本
            f.Cfg.PreferredNetwork = "F03NicB";
            f.Provider.OnCollectionStarted(2);

            Assert.True(f.Manager.DeviceGeneration > generationBefore);
            Assert.Equal(NetworkSampleQuality.Missing, f.Manager.Samples.Get("NET.Up").Quality);
        }

        [Fact]
        public void 硬件重载会清空样本登记()
        {
            var nic = NewNic("F03NicA", 555f);
            var f = NewFixture(nic);

            Assert.Equal(555f, Read(f).Value);

            f.Manager.ClearCache();

            Assert.Equal(NetworkSampleQuality.Missing, f.Manager.Samples.Get("NET.Up").Quality);
        }

        // =========================================================
        // ⑦ 采样调用频率不得随 UI 刷新倍增
        // =========================================================

        [Fact]
        public void 高频读取不会触发额外的网卡Update()
        {
            var nic = NewNic("F03NicA", 100f);
            var f = NewFixture(nic);

            for (int i = 0; i < 200; i++)
            {
                Read(f);
                f.Provider.GetValue("NET.Up");
                f.Provider.GetValue("NET.Down");
            }

            // 取值路径只读传感器，绝不触发 hw.Update()（采样频率仍由采集协调器决定）
            Assert.Equal(0, nic.UpdateCount);
        }

        [Fact]
        public void 采集层更新目标网卡会推进采样轮次()
        {
            var nic = NewNic("F03NicA", 100f);
            var f = NewFixture(nic);

            // 先读一次：自动选优把目标网卡选出来
            Read(f);
            Assert.Equal("F03NicA", f.Manager.SelectedDeviceName);

            long epochBefore = f.Manager.Samples.SampleEpoch;
            f.Manager.ProcessUpdate(nic, f.Cfg, 1.0, false);

            // 目标网卡真的被更新过 -> 新的采样轮次（样本寿命由这里刷新）
            Assert.Equal(epochBefore + 1, f.Manager.Samples.SampleEpoch);
            Assert.Equal(1, nic.UpdateCount);
        }

        // =========================================================
        // ⑧ 阈值集中定义：保留窗口 = min(3 周期, 15 秒)
        // =========================================================

        [Fact]
        public void 保留窗口最多三个采样周期()
        {
            var clock = new FakeTimeProvider();
            var policy = new NetworkSampleFreshnessPolicy(clock);
            policy.SampleCycleSeconds = 1.0;

            Assert.Equal(3.0, policy.RetentionSeconds, 6);

            var registry = new NetworkSampleRegistry(policy);
            registry.MarkSampled("NET.Up", 100f, "nic", 1);

            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(NetworkSampleQuality.Stale, registry.Get("NET.Up").Quality);

            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(NetworkSampleQuality.Missing, registry.Get("NET.Up").Quality);
        }

        [Fact]
        public void 慢刷新用户的保留窗口上限是十五秒()
        {
            var clock = new FakeTimeProvider();
            var policy = new NetworkSampleFreshnessPolicy(clock);
            policy.SampleCycleSeconds = 10.0;

            Assert.Equal(15.0, policy.RetentionSeconds, 6);
        }

        [Fact]
        public void 采样周期按配置刷新周期推导()
        {
            Assert.Equal(1.0, NetworkSampleFreshnessPolicy.ResolveSampleCycleSeconds(1000), 6);
            Assert.Equal(5.0, NetworkSampleFreshnessPolicy.ResolveSampleCycleSeconds(5000), 6);
            // 非法值回退到最小周期（每秒一次）
            Assert.Equal(1.0, NetworkSampleFreshnessPolicy.ResolveSampleCycleSeconds(0), 6);
            Assert.Equal(1.0, NetworkSampleFreshnessPolicy.ResolveSampleCycleSeconds(100), 6);
        }

        [Fact]
        public void 配置刷新周期决定实际保留窗口()
        {
            var nic = NewNic("F03NicA", 100f);
            var f = NewFixture(nic);
            f.Cfg.RefreshMs = 5000;   // 慢刷新：5 秒一个采样周期

            Read(f);
            // 保留窗口 = min(15 秒, 3 × 5 秒) = 15 秒
            Assert.Equal(15.0, f.Manager.Samples.Policy.RetentionSeconds, 6);

            // 还在保留期内：读得到值，但采集层没推进采样轮次 -> 不刷新成功时间
            f.Clock.Advance(TimeSpan.FromSeconds(14));
            Assert.Equal(NetworkSampleQuality.Fresh, Read(f).Quality);

            // 超过 15 秒上限 -> 缺失（不能把旧值一直续命）
            f.Clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(NetworkSampleQuality.Missing, Read(f).Quality);
        }

        // =========================================================
        // ⑨ 旧值一次性采纳（兼容桥）：有界，且不会被反复捡回来
        // =========================================================

        [Fact]
        public void 旧值只会被一次性采纳且寿命有界()
        {
            var clock = new FakeTimeProvider();
            var registry = new NetworkSampleRegistry(new NetworkSampleFreshnessPolicy(clock));

            // 第一次：采纳旧值
            NetworkSample adopted = registry.GetOrAdopt("NET.Up", 42f, "nic", 1);
            Assert.Equal(NetworkSampleQuality.Stale, adopted.Quality);
            Assert.Equal(42f, adopted.Value);

            // 第二次：不再采纳（已登记过），直接用登记处的样本
            NetworkSample again = registry.GetOrAdopt("NET.Up", 42f, "nic", 1);
            Assert.Equal(NetworkSampleQuality.Stale, again.Quality);

            // 采纳出来的旧值同样有寿命上限
            clock.Advance(TimeSpan.FromSeconds(4));
            Assert.Equal(NetworkSampleQuality.Missing, registry.Get("NET.Up").Quality);
        }

        [Fact]
        public void 作废之后的旧值不会被重新采纳()
        {
            var clock = new FakeTimeProvider();
            var registry = new NetworkSampleRegistry(new NetworkSampleFreshnessPolicy(clock));

            registry.MarkSampled("NET.Up", 42f, "nicA", 1);
            registry.InvalidateDevice("nicA");

            Assert.Equal(NetworkSampleQuality.Missing, registry.Get("NET.Up").Quality);
            // 已登记过的 Key 不允许再被一次性采纳（否则旧值会被无限期捡回来）
            Assert.True(registry.IsRegistered("NET.Up"));
            Assert.False(registry.AdoptLegacy("NET.Up", 42f, "nicA", 1));
            Assert.Equal(NetworkSampleQuality.Missing, registry.Get("NET.Up").Quality);
        }

        // =========================================================
        // ⑩ Provider 层：网络值不走通用最后有效值兜底
        // =========================================================

        [Fact]
        public void Provider层的网络值超过保留期后变成缺失()
        {
            var nic = NewNic("F03NicA", 321f);
            var f = NewFixture(nic);

            f.Provider.OnCollectionStarted(1);
            Assert.Equal(321f, f.Provider.GetValue("NET.Up"));

            // 采集层停更 + 超过保留期 -> 面板必须显示缺失，而不是把旧值冻住
            f.Clock.Advance(TimeSpan.FromSeconds(4));
            f.Provider.OnCollectionStarted(2);

            Assert.Null(f.Provider.GetValue("NET.Up"));
        }

        [Fact]
        public void Provider层不会把网络旧值写回通用最后有效值字典()
        {
            var nic = NewNic("F03NicA", 321f);
            var f = NewFixture(nic);

            f.Provider.OnCollectionStarted(1);
            f.Provider.GetValue("NET.Up");

            // 网络样本有独立的登记处，通用字典不再承载网络值
            Assert.False(f.LastValid.ContainsKey("NET.Up"));
        }
    }
}
