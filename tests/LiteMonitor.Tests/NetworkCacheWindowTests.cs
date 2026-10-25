using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    /// #96 / F03b：网络地址缓存窗与网卡重匹配节流的收口。
    ///
    /// <para>缺陷（修复前）：
    ///  - <c>NetworkManager.GetCurrentIP()</c> 用**固定 30 秒**窗口决定能否复用缓存的 IP，
    ///    而网络变更事件只覆盖了 <c>NetworkAddressChanged</c>：<b>可用性</b>变更
    ///    （Wi-Fi ↔ 有线切换、链路 down/up）不会让缓存失效，热插拔后最长 30 秒仍返回**旧网卡的 IP**；
    ///  - <c>MatchNativeNetworkAdapter</c> 用**固定 10 秒**节流重匹配：网卡消失
    ///    （计数读取失败 / 匹配失配）后，重匹配同样被压住最长 10 秒。</para>
    ///
    /// <para>修复后：两个时间窗都改成<b>事件驱动优先 + 时间窗兜底</b>：
    ///  - 地址变更 / 可用性变更事件 → 立即作废缓存并重取（不等 30 秒）；
    ///  - 事件丢失 → 时间窗到期仍然兜底重取；
    ///  - 原生网卡失效 → 立即允许重匹配；节流只约束"无失效信号时不要反复空转"；
    ///  - 失效与重取**有界**（单轮最多 N 次，事件风暴不会拉爆网卡枚举频率）。</para>
    ///
    /// <para>这些用例**不启动 LHM、不碰驱动、不访问外网**：
    ///  - 网卡用 <see cref="INetworkAdapterSnapshot"/> 假实现（热插拔 / Wi-Fi↔有线 / 双网卡都可驱动）；
    ///  - 网络变更事件用 <see cref="INetworkChangeSource"/> 假实现；
    ///  - LHM 网卡用 <c>Hardware</c> 基类桩（只统计 Update 次数）；
    ///  - 时间一律用**可注入的假时钟**，不使用 <c>Thread.Sleep</c>。</para>
    /// </summary>
    public class NetworkCacheWindowTests
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

        /// <summary>假网卡：可控制 IP、链路状态与字节计数。</summary>
        private sealed class FakeAdapter : INetworkAdapterSnapshot
        {
            public FakeAdapter(string name, string? description = null, bool up = true, bool loopback = false, string? ip = null)
            {
                Name = name;
                Description = description ?? name;
                IsOperationalUp = up;
                IsLoopback = loopback;
                IPv4 = ip == null ? Array.Empty<string>() : new[] { ip };
            }

            public string Name { get; }
            public string Description { get; }
            public bool IsOperationalUp { get; set; }
            public bool IsLoopback { get; }
            public int? IfIndex => null;

            /// <summary>IPv4 地址列表（可改写，模拟 DHCP 续租 / 换网段）。</summary>
            public string[] IPv4 { get; set; }

            public long BytesSent { get; set; }
            public long BytesReceived { get; set; }

            /// <summary>false = 计数读取失败（等价于网卡实例已失效）。</summary>
            public bool TrafficAvailable { get; set; } = true;

            public IReadOnlyList<string> GetIPv4Addresses() => IPv4;

            public bool TryGetTrafficBytes(out long bytesSent, out long bytesReceived)
            {
                if (!TrafficAvailable)
                {
                    bytesSent = 0;
                    bytesReceived = 0;
                    return false;
                }
                bytesSent = BytesSent;
                bytesReceived = BytesReceived;
                return true;
            }
        }

        /// <summary>假网卡源：记录枚举次数（用来观察"有没有真的去枚举网卡"）。</summary>
        private sealed class FakeAdapterSource : INetworkAdapterSource
        {
            private readonly List<FakeAdapter> _adapters = new List<FakeAdapter>();

            /// <summary>网卡被枚举的次数（= 真正读网卡的次数）。</summary>
            public int EnumerationCount { get; private set; }

            /// <summary>true = 枚举本身失败（等价于系统接口抛异常）。</summary>
            public bool ThrowOnEnumerate { get; set; }

            public void Set(params FakeAdapter[] adapters)
            {
                _adapters.Clear();
                _adapters.AddRange(adapters);
            }

            public IReadOnlyList<INetworkAdapterSnapshot> GetAdapters()
            {
                EnumerationCount++;
                if (ThrowOnEnumerate) throw new InvalidOperationException("模拟枚举网卡失败");
                return _adapters.Cast<INetworkAdapterSnapshot>().ToList();
            }
        }

        /// <summary>假网络变更事件源：测试可以精确地"发出事件"。</summary>
        private sealed class FakeChangeSource : INetworkChangeSource
        {
            public event EventHandler<NetworkChangeKind>? Changed;

            public int RaiseCount { get; private set; }

            public void Raise(NetworkChangeKind kind = NetworkChangeKind.Address)
            {
                RaiseCount++;
                Changed?.Invoke(this, kind);
            }
        }

        /// <summary>LHM 的空设置实现（Hardware 基类构造函数需要）。</summary>
        private sealed class FakeLhmSettings : ISettings
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public bool Contains(string name) => _values.ContainsKey(name);
            public void SetValue(string name, string value) => _values[name] = value;
            public string GetValue(string name, string value) => _values.TryGetValue(name, out var v) ? v : value;
            public void Remove(string name) => _values.Remove(name);
        }

        /// <summary>假的 Throughput 传感器：值可随时改写。</summary>
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

        private static FakeNetworkHardware NewNic(string name, float up, float down = 0f)
        {
            return new FakeNetworkHardware(name,
                new FakeThroughputSensor("Upload", up),
                new FakeThroughputSensor("Download", down));
        }

        /// <summary>被测对象与它的全部可注入替身。</summary>
        private sealed class Fixture
        {
            public Settings Cfg = new Settings();
            public SensorMap Map = new SensorMap();
            public Computer Computer = null!;
            public NetworkManager Manager = null!;
            public FakeTimeProvider Clock = new FakeTimeProvider();
            public FakeAdapterSource Adapters = new FakeAdapterSource();
            public FakeChangeSource Changes = new FakeChangeSource();
            public NetworkAddressCache Cache = null!;
            public Dictionary<string, float> LastValid = new Dictionary<string, float>();
            public object Sync = new object();
        }

        /// <summary>
        /// 造一个"与真实网卡完全隔离"的夹具。
        /// <para>★ 每个用例都用**独立的**缓存与事件源 —— 既不会读到本机真实网卡，
        /// 也不会通过共享静态缓存污染其它用例。</para>
        /// </summary>
        private static Fixture NewFixture(params FakeNetworkHardware[] nics)
        {
            var f = new Fixture();
            f.Computer = NewComputerWith(nics.Cast<Hardware>().ToArray());
            f.Cache = new NetworkAddressCache(f.Clock);
            f.Manager = new NetworkManager(
                new PerformanceCounterManager(),
                f.Clock,
                null,
                f.Adapters,
                f.Changes,
                f.Cache);
            return f;
        }

        private static NetworkSample Read(Fixture f, string key = "NET.Up")
            => f.Manager.GetBestSample(key, f.Computer, f.Cfg, f.LastValid, f.Sync);

        // =========================================================
        // ① 事件驱动：热插拔 / Wi-Fi↔有线切换后立即刷新（不等 30 秒）
        // =========================================================

        [Fact]
        public void 网卡热插拔后立即刷新_不等三十秒缓存窗()
        {
            var f = NewFixture();
            var nicA = new FakeAdapter("F03bNicA", ip: "192.168.1.10");
            f.Adapters.Set(nicA);

            Assert.Equal("192.168.1.10", f.Manager.GetCurrentIP());

            // 热插拔：A 消失，B 出现（系统会发出**可用性**变更事件）
            f.Adapters.Set(new FakeAdapter("F03bNicB", ip: "192.168.1.20"));
            f.Changes.Raise(NetworkChangeKind.Availability);

            // ★ 不推进时钟：事件驱动必须立刻拿到新网卡的 IP
            Assert.Equal("192.168.1.20", f.Manager.GetCurrentIP());
        }

        [Fact]
        public void 地址变更事件后立即刷新_不等三十秒缓存窗()
        {
            var f = NewFixture();
            var nicA = new FakeAdapter("F03bNicA", ip: "192.168.1.10");
            f.Adapters.Set(nicA);

            Assert.Equal("192.168.1.10", f.Manager.GetCurrentIP());

            // 同一张网卡换了地址（DHCP 续租 / 换网段）
            nicA.IPv4 = new[] { "192.168.1.30" };
            f.Changes.Raise(NetworkChangeKind.Address);

            Assert.Equal("192.168.1.30", f.Manager.GetCurrentIP());
        }

        [Fact]
        public void WiFi切有线后立即刷新到有线的地址()
        {
            var f = NewFixture();
            var wifi = new FakeAdapter("Wi-Fi", description: "Intel(R) Wi-Fi 6 AX201", ip: "192.168.1.10");
            var wired = new FakeAdapter("以太网", description: "Realtek PCIe GbE Family Controller", ip: "192.168.2.10");
            f.Adapters.Set(wifi, wired);

            // 先连 Wi-Fi
            Assert.Equal("192.168.1.10", f.Manager.GetCurrentIP());

            // 切到有线：Wi-Fi 链路 down，有线链路 up
            wifi.IsOperationalUp = false;
            wired.IsOperationalUp = true;
            f.Changes.Raise(NetworkChangeKind.Availability);

            // ★ 立刻反映有线网卡，而不是继续显示 Wi-Fi 的旧 IP
            Assert.Equal("192.168.2.10", f.Manager.GetCurrentIP());
        }

        [Fact]
        public void 网络变更后不再返回旧网卡的IP()
        {
            var f = NewFixture();
            f.Adapters.Set(new FakeAdapter("F03bNicA", ip: "192.168.1.10"));
            Assert.Equal("192.168.1.10", f.Manager.GetCurrentIP());

            // 网卡被拔掉：没有任何网卡了
            f.Adapters.Set();
            f.Changes.Raise(NetworkChangeKind.Availability);

            NetworkAddressResult result = f.Manager.GetCurrentIPResult();

            // ★ 旧网卡的 IP 绝不能继续冒充当前地址
            Assert.NotEqual("192.168.1.10", result.Address);
            Assert.Equal(NetworkAddressStatus.Missing, result.Status);
            Assert.False(result.HasAddress);
        }

        // =========================================================
        // ② 时间窗兜底：事件丢失时仍然生效
        // =========================================================

        [Fact]
        public void 事件丢失时时间窗兜底仍然生效()
        {
            var f = NewFixture();
            var nic = new FakeAdapter("F03bNicA", ip: "192.168.1.10");
            f.Adapters.Set(nic);

            Assert.Equal("192.168.1.10", f.Manager.GetCurrentIP());

            // 网卡换了地址，但**事件丢了**（没有发出任何变更事件）
            nic.IPv4 = new[] { "192.168.1.40" };

            // 窗口内：仍然复用缓存（这是有意的节流，不是 bug）
            f.Clock.Advance(TimeSpan.FromSeconds(29));
            Assert.Equal("192.168.1.10", f.Manager.GetCurrentIP());

            // ★ 窗口到期 → 兜底重取（事件丢失也能恢复）
            f.Clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal("192.168.1.40", f.Manager.GetCurrentIP());
        }

        [Fact]
        public void 时间窗内不会重复枚举网卡()
        {
            var f = NewFixture();
            f.Adapters.Set(new FakeAdapter("F03bNicA", ip: "192.168.1.10"));

            Assert.Equal("192.168.1.10", f.Manager.GetCurrentIP());
            int afterFirst = f.Adapters.EnumerationCount;

            for (int i = 0; i < 50; i++)
            {
                Assert.Equal("192.168.1.10", f.Manager.GetCurrentIP());
                f.Clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            // ★ 30 秒窗口内的高频读取不得把网卡枚举频率拉爆
            Assert.Equal(afterFirst, f.Adapters.EnumerationCount);
        }

        // =========================================================
        // ③ 事件风暴有界
        // =========================================================

        [Fact]
        public void 事件风暴不会导致重取风暴()
        {
            var f = NewFixture();
            f.Adapters.Set(new FakeAdapter("F03bNicA", ip: "192.168.1.10"));
            f.Manager.GetCurrentIP();
            int before = f.Adapters.EnumerationCount;

            // 一次物理网络变更往往连发上百个事件
            for (int i = 0; i < 100; i++)
            {
                f.Changes.Raise(NetworkChangeKind.Address);
                f.Changes.Raise(NetworkChangeKind.Availability);
            }

            // 连续读取 200 次（模拟 UI 高频读）
            for (int i = 0; i < 200; i++)
            {
                f.Manager.GetCurrentIP();
            }

            int enumerations = f.Adapters.EnumerationCount - before;
            // ★ 单轮预算上限是 3 次：事件风暴不允许把它拉爆
            Assert.True(enumerations <= NetworkRefreshGate.DefaultMaxAttemptsPerRound,
                $"事件风暴引发了 {enumerations} 次网卡枚举，超过单轮预算 {NetworkRefreshGate.DefaultMaxAttemptsPerRound}");
        }

        [Fact]
        public void 持续事件下重取频率仍然受时间约束()
        {
            var f = NewFixture();
            f.Adapters.Set(new FakeAdapter("F03bNicA", ip: "192.168.1.10"));
            f.Manager.GetCurrentIP();
            int before = f.Adapters.EnumerationCount;

            // 每 1.5 秒来一个事件（去抖窗口是 1 秒，所以每个事件都会推进代次）
            for (int i = 0; i < 20; i++)
            {
                f.Clock.Advance(TimeSpan.FromMilliseconds(1500));
                f.Changes.Raise(NetworkChangeKind.Address);
                f.Manager.GetCurrentIP();
            }

            int enumerations = f.Adapters.EnumerationCount - before;
            // ★ 每个事件最多触发 1 次重取（有界），不会变成"每个事件重试 N 次"的放大
            Assert.True(enumerations <= 20,
                $"20 个事件引发了 {enumerations} 次网卡枚举，超过每事件 1 次的上限");
        }

        // =========================================================
        // ④ 双网卡并存：地址归属于真正在用的那张卡
        // =========================================================

        [Fact]
        public void 双网卡并存时地址归属于选中的设备()
        {
            var nicA = NewNic("F03bNicA", 5000f);
            var nicB = NewNic("F03bNicB", 10f);
            var f = NewFixture(nicA, nicB);

            var adapterA = new FakeAdapter("F03bNicA", ip: "192.168.1.10");
            var adapterB = new FakeAdapter("F03bNicB", ip: "192.168.2.10");
            f.Adapters.Set(adapterA, adapterB);

            // 自动选优选出 A（更忙）
            Assert.Equal(5000f, Read(f).Value);
            Assert.Equal("F03bNicA", f.Manager.SelectedDeviceName);

            // 采集一轮：A 的原生网卡被匹配上
            f.Manager.ProcessUpdate(nicA, f.Cfg, 1.0, false);

            // ★ 显示的是 A 的地址，不是 B 的
            Assert.Equal("192.168.1.10", f.Manager.GetCurrentIP());

            // A 被拔掉：可用性变更事件到达
            adapterA.TrafficAvailable = false;
            f.Adapters.Set(adapterB);
            f.Changes.Raise(NetworkChangeKind.Availability);

            // ★ 不能继续显示 A 的旧 IP
            string ip = f.Manager.GetCurrentIP();
            Assert.NotEqual("192.168.1.10", ip);
            Assert.Equal("192.168.2.10", ip);
        }

        // =========================================================
        // ⑤ 缺失与失败可区分（0 是有效数据原则的地址版）
        // =========================================================

        [Fact]
        public void 网卡没有IPv4是缺失而不是读取失败()
        {
            var f = NewFixture();
            f.Adapters.Set(new FakeAdapter("F03bNicA"));   // 没有任何 IPv4

            NetworkAddressResult result = f.Manager.GetCurrentIPResult();

            Assert.Equal(NetworkAddressStatus.Missing, result.Status);
            Assert.Equal("", result.Address);
            // ★ 绝不用 "0.0.0.0" 冒充
            Assert.NotEqual("0.0.0.0", result.Address);
        }

        [Fact]
        public void 枚举网卡失败是读取失败而不是缺失()
        {
            var f = NewFixture();
            f.Adapters.Set(new FakeAdapter("F03bNicA", ip: "192.168.1.10"));
            Assert.Equal("192.168.1.10", f.Manager.GetCurrentIP());

            f.Adapters.ThrowOnEnumerate = true;
            f.Clock.Advance(TimeSpan.FromSeconds(31));   // 让时间窗到期，强制重新取值

            NetworkAddressResult result = f.Manager.GetCurrentIPResult();

            Assert.Equal(NetworkAddressStatus.Failed, result.Status);
            Assert.NotEqual("0.0.0.0", result.Address);
        }

        // =========================================================
        // ⑥ 原生网卡重匹配：失效即立即重匹配（不等 10 秒节流）
        // =========================================================

        [Fact]
        public void 原生网卡失效后立即重新匹配_不等十秒节流()
        {
            var nic = NewNic("F03bNicA", 100f);
            var f = NewFixture(nic);
            f.Cfg.PreferredNetwork = "F03bNicA";

            var adapter = new FakeAdapter("F03bNicA", ip: "192.168.1.10");
            f.Adapters.Set(adapter);

            // 第一轮：匹配成功（枚举一次）
            f.Manager.ProcessUpdate(nic, f.Cfg, 1.0, false);
            int afterMatch = f.Adapters.EnumerationCount;
            Assert.Equal(1, afterMatch);

            // 网卡被拔掉：计数读取失败 → 原生网卡实例失效
            adapter.TrafficAvailable = false;
            f.Manager.ProcessUpdate(nic, f.Cfg, 1.0, false);

            // 网卡回来了（新的实例）
            var adapter2 = new FakeAdapter("F03bNicA", ip: "192.168.1.11");
            adapter2.BytesSent = 5000;
            f.Adapters.Set(adapter2);

            // ★ 只过了 1 秒（旧的实现要等满 10 秒才允许重匹配）
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Manager.ProcessUpdate(nic, f.Cfg, 1.0, false);

            Assert.True(f.Adapters.EnumerationCount > afterMatch,
                "原生网卡失效后没有立即重新匹配（仍被 10 秒节流压住）");

            // ★ 重新匹配到的是**新**网卡实例（字节计数重新建立了基线）
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            adapter2.BytesSent = 6000;
            f.Manager.ProcessUpdate(nic, f.Cfg, 1.0, false);
            Assert.Equal(1000, f.Cfg.SessionUploadBytes);
        }

        [Fact]
        public void 无失效信号时重匹配仍然被节流()
        {
            var nic = NewNic("F03bNicA", 100f);
            var f = NewFixture(nic);
            f.Cfg.PreferredNetwork = "F03bNicA";

            // 系统里根本没有这张网卡 → 匹配一直失败
            f.Adapters.Set();

            f.Manager.ProcessUpdate(nic, f.Cfg, 1.0, false);
            int afterFirst = f.Adapters.EnumerationCount;
            Assert.Equal(1, afterFirst);

            // ★ 没有失效信号：1 秒内不应该反复空转枚举
            f.Clock.Advance(TimeSpan.FromMilliseconds(500));
            f.Manager.ProcessUpdate(nic, f.Cfg, 1.0, false);
            Assert.Equal(afterFirst, f.Adapters.EnumerationCount);
        }

        [Fact]
        public void 网络变更事件后立即允许重新匹配()
        {
            var nic = NewNic("F03bNicA", 100f);
            var f = NewFixture(nic);
            f.Cfg.PreferredNetwork = "F03bNicA";

            var adapter = new FakeAdapter("F03bNicA", ip: "192.168.1.10");
            f.Adapters.Set(adapter);

            f.Manager.ProcessUpdate(nic, f.Cfg, 1.0, false);
            int afterMatch = f.Adapters.EnumerationCount;
            Assert.Equal(1, afterMatch);

            // 网络变更 → 已锁定的原生网卡实例作废
            f.Changes.Raise(NetworkChangeKind.Availability);

            // ★ 立即重新匹配（不推进时钟）
            f.Manager.ProcessUpdate(nic, f.Cfg, 1.0, false);
            Assert.True(f.Adapters.EnumerationCount > afterMatch,
                "网络变更事件没有让重匹配立即放行");
        }

        // =========================================================
        // ⑦ 抽象层单测：地址缓存的代次与去抖
        // =========================================================

        [Fact]
        public void 地址缓存在代次推进后立即失效()
        {
            var clock = new FakeTimeProvider();
            var cache = new NetworkAddressCache(clock);

            cache.Store("192.168.1.10");
            Assert.True(cache.HasValidValue);
            Assert.Equal("192.168.1.10", cache.GetValidValue());

            long before = cache.Generation;
            cache.AdvanceGeneration();

            Assert.True(cache.Generation > before);
            // ★ 旧值立即失效（但仍可作为"上次已知值"取回）
            Assert.False(cache.HasValidValue);
            Assert.Equal("", cache.GetValidValue());
            Assert.Equal("192.168.1.10", cache.PeekValue());
        }

        [Fact]
        public void 地址缓存的代次推进有去抖()
        {
            var clock = new FakeTimeProvider();
            var cache = new NetworkAddressCache(clock);

            long g1 = cache.AdvanceGeneration();
            long g2 = cache.AdvanceGeneration();
            // ★ 同一波事件（1 秒内）只推进一次
            Assert.Equal(g1, g2);

            clock.Advance(TimeSpan.FromMilliseconds(1500));
            long g3 = cache.AdvanceGeneration();
            Assert.True(g3 > g1);
        }

        [Fact]
        public void 刷新闸门在事件驱动下立即放行()
        {
            var clock = new FakeTimeProvider();
            var gate = new NetworkRefreshGate(clock);

            // 缓存已作废 + 代次前进 → 立即放行
            Assert.True(gate.ShouldRefresh(cacheValid: false, generation: 1));
            // 同一代次的重复读：受最短重试间隔约束
            Assert.False(gate.ShouldRefresh(cacheValid: false, generation: 1));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(gate.ShouldRefresh(cacheValid: false, generation: 1));
        }

        [Fact]
        public void 刷新闸门的时间窗兜底有界()
        {
            var clock = new FakeTimeProvider();
            var gate = new NetworkRefreshGate(clock);

            // 从未成功取值 → 窗口已到期
            Assert.True(gate.IsWindowExpired);
            Assert.True(gate.ShouldRefresh(cacheValid: true, generation: 0));

            gate.MarkRefreshSucceeded();
            Assert.False(gate.IsWindowExpired);
            Assert.False(gate.ShouldRefresh(cacheValid: true, generation: 0));

            // 窗口到期 → 兜底放行，但**单轮预算**用尽后不再放行
            clock.Advance(TimeSpan.FromSeconds(31));
            Assert.True(gate.IsWindowExpired);
            int allowed = 0;
            for (int i = 0; i < 10; i++)
            {
                if (gate.ShouldRefresh(cacheValid: true, generation: 0)) allowed++;
            }
            Assert.Equal(NetworkRefreshGate.DefaultMaxAttemptsPerRound, allowed);
        }
    }
}
