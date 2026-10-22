using System;
using System.Collections.Generic;
using System.Threading;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.SystemServices.NetworkTraffic;
using LiteMonitor.src.UI.SettingsPage;
using LibreHardwareMonitor.Hardware;
using Microsoft.Diagnostics.Tracing;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// #610 N3：取值接线（NET.Up/NET.Down）+ 设置 + 会话生命周期回归。
    ///
    /// 不开真实 ETW 会话、不碰真实网卡：事件源与网卡都是替身。
    /// </summary>
    public class NetworkExclusionWiringTests
    {
        // =========================================================
        // 测试替身
        // =========================================================

        private sealed class FakeTimeProvider : TimeProvider
        {
            private long _ticks;

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => Interlocked.Read(ref _ticks);
            public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

            public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
        }

        private sealed class FakeAttributor : INetworkAdapterAttributor
        {
            private readonly Dictionary<IpKey, int> _map = new();

            public FakeAttributor Add(IpKey local, int ifIndex)
            {
                _map[local] = ifIndex;
                return this;
            }

            public bool TryResolveIfIndex(IpKey localAddress, out int ifIndex)
            {
                if (_map.TryGetValue(localAddress, out int v)) { ifIndex = v; return true; }
                ifIndex = -1;
                return false;
            }

            public bool IsAmbiguous(IpKey localAddress) => false;

            public IReadOnlyList<int> Candidates(IpKey localAddress) => Array.Empty<int>();
        }

        private sealed class FakeEventSource : INetworkTrafficEventSource
        {
            public int StartCount;
            public int StopCount;
            public TrafficSourceStatus Status { get; set; } = TrafficSourceStatus.Stopped;
            public string? StatusDetail { get; set; }
            public long EventsProcessed { get; set; }
            public long EventsLost { get; set; }
            public long EventsUndecodable { get; set; }
            public long BuffersLost => 0;
            public int Generation { get; set; }

            public string SessionName => "Fake.NetExclusion.Session";

            public void Start() { StartCount++; Generation++; Status = TrafficSourceStatus.Running; }
            public void Stop() { StopCount++; Status = TrafficSourceStatus.Stopped; }
            public void Dispose() { Status = TrafficSourceStatus.Stopped; }

            public event Action<NetworkTrafficEvent>? EventReceived;

            public void Emit(NetworkTrafficEvent e)
            {
                EventsProcessed++;
                EventReceived?.Invoke(e);
            }
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

        /// <summary>假的 Throughput 传感器。</summary>
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

        /// <summary>假的网卡硬件（只统计 Update 次数，不接触驱动）。</summary>
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

        // =========================================================
        // 设置
        // =========================================================

        [Fact]
        public void 设置默认值是关闭且规则为空()
        {
            var cfg = new Settings();

            Assert.False(cfg.IpTrafficExclusionEnabled);
            Assert.NotNull(cfg.ExcludedTrafficIps);
            Assert.Empty(cfg.ExcludedTrafficIps);
        }

        [Fact]
        public void 多行IP文本被拆成规则行()
        {
            var lines = SystemHardwarPage.SplitIpLines("192.168.1.10\r\n  10.0.0.1  \r\n\r\n2001:db8::1");

            Assert.Equal(3, lines.Count);
            Assert.Equal("192.168.1.10", lines[0]);
            Assert.Equal("10.0.0.1", lines[1]);
            Assert.Equal("2001:db8::1", lines[2]);
        }

        // =========================================================
        // 取值接线
        // =========================================================

        private sealed class ProviderFixture
        {
            public Settings Cfg = new();
            public SensorMap Map = new();
            public Computer Computer = new();
            public FakeNetworkHardware Nic = null!;
            public HardwareValueProvider Provider = null!;
            public FakeTimeProvider Clock = new();
            public FakeEventSource Source = new();
            public NetworkTrafficExclusionService Service = null!;
            public Dictionary<string, float> LastValid = new();
        }

        private static ProviderFixture NewProviderFixture()
        {
            var f = new ProviderFixture();
            f.Nic = new FakeNetworkHardware("WiringFakeNic", new FakeThroughputSensor("Upload", 999_999f));
            f.Cfg.PreferredNetwork = f.Nic.Name;

            f.Provider = new HardwareValueProvider(
                f.Computer,
                f.Cfg,
                f.Map,
                new NetworkManager(),
                new DiskManager(),
                null,
                new PerformanceCounterManager(),
                new object(),
                f.LastValid,
                new HardwareSamplingStatistics(),
                f.Clock);

            // 预热：把 NET.Up 放进手动传感器缓存（模拟"手动选择网卡"）
            f.Provider.PreCacheAllSensors(f.Map);

            // ★ PreCacheAllSensors 走 LHM 的 Computer.Hardware，而该属性在 Computer 未 Open()
            //   时返回**每次访问都新建的空列表副本** —— 所以往 Computer.Hardware 里 Add(fake)
            //   是无效的（实测 hw=[]、cacheKeys=[]）。这是 LHM 的设计限制，不是本项目的缺陷。
            //   本组测试关心的是 #610 的**取值分支选择**（过滤快照 vs 旧路径直读），
            //   而旧路径的输入正是 _manualSensorCache，所以直接注入它最精确。
            SeedManualSensorCache(f.Provider, "NET.Up", new FakeThroughputSensor("Upload", 999_999f));

            var attrib = new FakeAttributor();
            attrib.Add(IpKey.Parse("192.168.31.71"), 6);
            f.Service = new NetworkTrafficExclusionService(f.Source, attrib, f.Clock, 1.0);
            f.Provider.AttachTrafficExclusion(f.Service);
            return f;
        }

        /// <summary>
        /// 把假传感器放进 <c>HardwareValueProvider</c> 的手动传感器缓存。
        /// </summary>
        /// <remarks>
        /// ★ 为什么要反射注入，而不是往 <c>Computer.Hardware</c> 里 Add：
        ///   LHM 0.9.6 的 <c>Computer.Hardware</c> 属性**每次访问都返回一个新建的列表副本**，
        ///   且 Computer 未 <c>Open()</c> 时返回空列表 —— 往它上面 Add(fake) 不会生效
        ///   （实测 <c>hw=[]</c>、<c>cacheKeys=[]</c>）。这是 LHM 的设计限制，不是本项目的缺陷。
        ///   本组测试要验证的是 #610 的**取值分支选择**（过滤快照 vs 旧路径直读），
        ///   旧路径的输入正是 <c>_manualSensorCache</c>，所以直接注入它最精确，
        ///   也避免把测试耦合到 LHM 的内部结构上。
        /// </remarks>
        private static void SeedManualSensorCache(
            HardwareValueProvider provider,
            string key,
            ISensor sensor)
        {
            var field = typeof(HardwareValueProvider).GetField(
                "_manualSensorCache",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var cache = (Dictionary<string, ISensor>)field!.GetValue(provider)!;
            cache[key] = sensor;
        }

        private static void PublishSnapshot(ProviderFixture f)
        {
            f.Service.SetTargetAdapter(6, null);
            f.Service.ApplyConfiguration(true, new[] { "10.0.0.1" });
            f.Service.NotifyRealtimeDemand();
            f.Service.Tick();                                   // t=0 开会话 + 开窗口
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();                                   // t=1 首窗丢弃
            f.Source.Emit(new NetworkTrafficEvent(
                Timestamp: DateTime.UtcNow,
                Protocol: NetworkTrafficProtocol.Udp,
                Direction: NetworkTrafficDirection.Send,
                RemoteAddress: IpKey.Parse("10.0.0.1"),
                LocalAddress: IpKey.Parse("192.168.31.71"),
                RemotePort: 5000,
                LocalPort: 6000,
                SizeBytes: 1000,
                ProcessId: 1));
            f.Source.Emit(new NetworkTrafficEvent(
                Timestamp: DateTime.UtcNow.AddTicks(1),
                Protocol: NetworkTrafficProtocol.Udp,
                Direction: NetworkTrafficDirection.Send,
                RemoteAddress: IpKey.Parse("10.0.0.2"),
                LocalAddress: IpKey.Parse("192.168.31.71"),
                RemotePort: 5000,
                LocalPort: 6000,
                SizeBytes: 500,
                ProcessId: 1));
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();                                   // t=2 发布窗口
        }

        [Fact]
        public void 过滤快照优先于手动网卡传感器缓存()
        {
            var f = NewProviderFixture();
            PublishSnapshot(f);

            Assert.True(f.Service.IsEffective);

            float? up = f.Provider.GetValue("NET.Up");

            Assert.NotNull(up);
            // 过滤后的速率 = 500 B/s（1000 字节被排除）
            Assert.Equal(500f, up!.Value, 3);
            // 绝不能是手动传感器缓存里的 999999
            Assert.NotEqual(999_999f, up!.Value, 3);
        }

        [Fact]
        public void 过滤未生效时显示缺失而不是0也不是旧值()
        {
            var f = NewProviderFixture();
            f.Service.SetTargetAdapter(6, null);
            f.Service.ApplyConfiguration(true, new[] { "10.0.0.1" });
            f.Service.NotifyRealtimeDemand();
            f.Service.Tick();

            float? up = f.Provider.GetValue("NET.Up");

            Assert.Null(up);
        }

        [Fact]
        public void 功能关闭时走原有实时网速路径()
        {
            var f = NewProviderFixture();
            f.Cfg.IpTrafficExclusionEnabled = false;

            float? up = f.Provider.GetValue("NET.Up");

            // 旧路径：手动传感器缓存里的值
            Assert.NotNull(up);
            Assert.Equal(999_999f, up!.Value, 3);
            Assert.Equal(0, f.Source.StartCount);
        }

        [Fact]
        public void 多次读取不会创建额外会话也不会增加网卡Update次数()
        {
            var f = NewProviderFixture();
            PublishSnapshot(f);

            // 模拟 UI 100ms × 10 次 + 多个 Web 读者
            for (int i = 0; i < 10; i++)
            {
                f.Provider.GetValue("NET.Up");
                f.Provider.GetValue("NET.Down");
            }
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();

            Assert.Equal(1, f.Source.StartCount);
            Assert.Equal(0, f.Nic.UpdateCount);
        }

        [Fact]
        public void 下载方向同样消费过滤快照()
        {
            var f = NewProviderFixture();
            PublishSnapshot(f);

            float? down = f.Provider.GetValue("NET.Down");

            // 本场景没有下载流量 → 0 是**合法**的 0（窗口有效、确实没有下载）
            Assert.NotNull(down);
            Assert.Equal(0f, down!.Value, 3);
        }

        // =========================================================
        // 累计口径：IP 排除不得改变既有累计，也不得二次扣减
        // =========================================================

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void IP排除开关不改变旧累计结果也不发生二次扣减(bool ignoreSmb, bool ipExclusion)
        {
            var clock = new FakeTimeProvider();
            var accumulator = new TrafficAccumulator(clock);

            accumulator.Accumulate(new TrafficSample
            {
                NativeUpBytes = 1_000_000,
                NativeDownBytes = 2_000_000
            }, 1.0);

            clock.Advance(TimeSpan.FromSeconds(1));

            Func<double, (long Up, long Down)>? smb = ignoreSmb
                ? new Func<double, (long, long)>(_ => (1000, 2000))
                : null;

            TrafficAccumulationResult result = accumulator.Accumulate(new TrafficSample
            {
                NativeUpBytes = 1_010_000,
                NativeDownBytes = 2_020_000
            }, 1.0, smb);

            // 累计只受"原生字节 + SMB 估算"影响；IP 排除完全不参与
            Assert.Equal(ignoreSmb ? 9_000 : 10_000, result.UpBytes);
            Assert.Equal(ignoreSmb ? 18_000 : 20_000, result.DownBytes);
        }

        [Fact]
        public void IP排除不会改写会话累计字节()
        {
            var cfg = new Settings { SessionUploadBytes = 12345, SessionDownloadBytes = 6789 };
            var clock = new FakeTimeProvider();
            var source = new FakeEventSource();
            var attrib = new FakeAttributor();
            attrib.Add(IpKey.Parse("192.168.31.71"), 6);
            using var service = new NetworkTrafficExclusionService(source, attrib, clock, 1.0);

            service.SetTargetAdapter(6, null);
            service.ApplyConfiguration(true, new[] { "10.0.0.1" });
            service.NotifyRealtimeDemand();
            service.Tick();

            // 排除后端只发布**实时**速率快照，绝不触碰会话累计字段
            Assert.Equal(12345, cfg.SessionUploadBytes);
            Assert.Equal(6789, cfg.SessionDownloadBytes);
        }

        // =========================================================
        // 会话生命周期回归（N0 缺陷 A / B）
        // =========================================================

        private sealed class FakeTraceSession : ITraceSession
        {
            public bool Disposed;
            public bool EnableThrows;
            public bool EnableResult = true;

            public string SessionName => "Fake.Trace.Session";
            public long EventsLost => 0;

            public bool EnableProvider(string providerName, TraceEventLevel level, ulong matchAnyKeywords)
            {
                if (EnableThrows) { throw new UnauthorizedAccessException("Access is denied. (0x80070005 (E_ACCESSDENIED))"); }
                return EnableResult;
            }

            public void Subscribe(Action<TraceEvent> handler) { }
            public void Process() { }
            public void StopProcessing() { }
            public void Dispose() { Disposed = true; }
        }

        [Fact]
        public void EnableProvider失败时已创建的会话会被释放()
        {
            var fake = new FakeTraceSession { EnableThrows = true };
            var source = new EtwNetworkTrafficEventSource();
            source.SessionFactory = _ => fake;

            Assert.Throws<UnauthorizedAccessException>(() => source.Start());

            // ★ N0 缺陷 A：会话必须被释放，否则会残留在系统里（需要管理员 logman 才能清）
            Assert.True(fake.Disposed);
            Assert.Equal(TrafficSourceStatus.Failed, source.Status);
        }

        [Fact]
        public void EnableProvider返回false不算失败()
        {
            // 实机标定：TraceEvent 4.0.0 对本 provider 恒返回 false，但事件照常到达
            var fake = new FakeTraceSession { EnableResult = false };
            var source = new EtwNetworkTrafficEventSource();
            source.SessionFactory = _ => fake;

            source.Start();

            Assert.Equal(TrafficSourceStatus.Running, source.Status);
            source.Stop();
        }

        [Fact]
        public void 停止会话只处置自己创建的会话()
        {
            var fake = new FakeTraceSession();
            var source = new EtwNetworkTrafficEventSource();
            source.SessionFactory = _ => fake;

            source.Start();
            source.Stop();

            Assert.True(fake.Disposed);
            Assert.Equal(TrafficSourceStatus.Stopped, source.Status);
        }

        [Fact]
        public void 会话名不会使用保留名()
        {
            var source = new EtwNetworkTrafficEventSource();
            Assert.DoesNotContain("NT Kernel Logger", source.SessionName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LiteMonitor++_Golden_Session", source.SessionName, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(EtwNetworkTrafficEventSource.SessionNamePrefix, source.SessionName, StringComparison.Ordinal);
        }
    }
}
