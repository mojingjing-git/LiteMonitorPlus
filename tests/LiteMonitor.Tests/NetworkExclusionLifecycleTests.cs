using System;
using System.Collections.Generic;
using System.Threading;
using LiteMonitor.src.SystemServices.NetworkTraffic;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// #610 N2：后端生命周期与状态机。
    ///
    /// 全部用**假事件源 + 假时钟**驱动：
    /// 不开 ETW 会话、不需要管理员、不碰真实网卡。
    /// </summary>
    public class NetworkExclusionLifecycleTests
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
            public int DisposeCount;
            public bool FailOnStart;
            public Exception? StartException;
            public TrafficSourceStatus Status { get; set; } = TrafficSourceStatus.Stopped;
            public string? StatusDetail { get; set; }
            public long EventsProcessed { get; set; }
            public long EventsLost { get; set; }
            public long EventsUndecodable { get; set; }
            public long BuffersLost => 0;
            public int Generation { get; set; }

            public string SessionName => "Fake.NetExclusion.Session";

            public void Start()
            {
                StartCount++;
                if (FailOnStart)
                {
                    Status = TrafficSourceStatus.Failed;
                    StatusDetail = (StartException ?? new UnauthorizedAccessException("Access is denied. (0x80070005 (E_ACCESSDENIED))")).Message;
                    throw StartException ?? new UnauthorizedAccessException("Access is denied. (0x80070005 (E_ACCESSDENIED))");
                }
                Generation++;
                Status = TrafficSourceStatus.Running;
            }

            public void Stop()
            {
                StopCount++;
                Status = TrafficSourceStatus.Stopped;
            }

            public void Dispose()
            {
                DisposeCount++;
                Status = TrafficSourceStatus.Stopped;
            }

            public event Action<NetworkTrafficEvent>? EventReceived;

            public void Emit(NetworkTrafficEvent e)
            {
                EventsProcessed++;
                EventReceived?.Invoke(e);
            }
        }

        private static NetworkTrafficEvent Event(string remote, string local, long size, NetworkTrafficDirection direction)
        {
            return new NetworkTrafficEvent(
                Timestamp: DateTime.UtcNow,
                Protocol: NetworkTrafficProtocol.Udp,
                Direction: direction,
                RemoteAddress: IpKey.Parse(remote),
                LocalAddress: IpKey.Parse(local),
                RemotePort: 5000,
                LocalPort: 6000,
                SizeBytes: size,
                ProcessId: 1,
                Kind: NetworkTrafficEventKind.Data);
        }

        private sealed class Fixture
        {
            public FakeTimeProvider Clock = new();
            public FakeEventSource Source = new();
            public FakeAttributor Attributor = new();
            public NetworkTrafficExclusionService Service = null!;
        }

        private static Fixture NewFixture(double windowSeconds = 1.0)
        {
            var f = new Fixture();
            f.Attributor.Add(IpKey.Parse("192.168.31.71"), 6);
            f.Service = new NetworkTrafficExclusionService(f.Source, f.Attributor, f.Clock, windowSeconds);
            return f;
        }

        /// <summary>把服务推到「有需求 + 有规则 + 有目标网卡」的状态。</summary>
        private static void Arm(Fixture f, string[] rules)
        {
            f.Service.SetTargetAdapter(6, null);
            f.Service.ApplyConfiguration(true, rules);
            f.Service.NotifyRealtimeDemand();
        }

        // =========================================================
        // 准入：什么时候才允许创建会话
        // =========================================================

        [Fact]
        public void 功能关闭时不创建任何会话()
        {
            var f = NewFixture();
            f.Service.ApplyConfiguration(false, new[] { "10.0.0.1" });
            f.Service.NotifyRealtimeDemand();
            f.Service.Tick();

            Assert.Equal(0, f.Source.StartCount);
            Assert.Equal(NetworkTrafficExclusionState.Disabled, f.Service.GetStatus().State);
        }

        [Fact]
        public void 没有有效规则时不创建任何会话()
        {
            var f = NewFixture();
            f.Service.SetTargetAdapter(6, null);
            f.Service.ApplyConfiguration(true, Array.Empty<string>());
            f.Service.NotifyRealtimeDemand();
            f.Service.Tick();

            Assert.Equal(0, f.Source.StartCount);
            Assert.False(f.Service.IsEffective);
            Assert.Equal(NetworkTrafficExclusionState.Idle, f.Service.GetStatus().State);
        }

        [Fact]
        public void 规则有错误行时不创建会话且状态为失败()
        {
            var f = NewFixture();
            f.Service.SetTargetAdapter(6, null);
            f.Service.ApplyConfiguration(true, new[] { "10.0.0.1", "example.com" });
            f.Service.NotifyRealtimeDemand();
            f.Service.Tick();

            Assert.Equal(0, f.Source.StartCount);
            NetworkTrafficExclusionStatus status = f.Service.GetStatus();
            Assert.Equal(NetworkTrafficExclusionState.Failed, status.State);
            Assert.False(status.Effective);
            Assert.Single(status.RuleErrors);
        }

        [Fact]
        public void 没有实时网速需求时不创建会话()
        {
            var f = NewFixture();
            f.Service.SetTargetAdapter(6, null);
            f.Service.ApplyConfiguration(true, new[] { "10.0.0.1" });
            // 不调用 NotifyRealtimeDemand
            f.Service.Tick();

            Assert.Equal(0, f.Source.StartCount);
        }

        [Fact]
        public void 目标网卡未识别时不创建会话()
        {
            var f = NewFixture();
            f.Service.ApplyConfiguration(true, new[] { "10.0.0.1" });
            f.Service.NotifyRealtimeDemand();
            f.Service.Tick();

            Assert.Equal(0, f.Source.StartCount);
            Assert.Equal(NetworkTrafficExclusionState.Idle, f.Service.GetStatus().State);
        }

        [Fact]
        public void 网卡归属歧义时明确拒绝不启动()
        {
            var f = NewFixture();
            f.Service.SetTargetAdapter(6, "ambiguous adapter attribution: 192.168.1.5 is present on 2 interfaces");
            f.Service.ApplyConfiguration(true, new[] { "10.0.0.1" });
            f.Service.NotifyRealtimeDemand();
            f.Service.Tick();

            Assert.Equal(0, f.Source.StartCount);
            NetworkTrafficExclusionStatus status = f.Service.GetStatus();
            Assert.Equal(NetworkTrafficExclusionState.Failed, status.State);
            Assert.False(status.Effective);
            Assert.Contains("ambiguous", status.Detail!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void 条件齐备时创建会话()
        {
            var f = NewFixture();
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();

            Assert.Equal(1, f.Source.StartCount);
        }

        // =========================================================
        // 预热 / 窗口
        // =========================================================

        [Fact]
        public void 首个窗口被丢弃不会产出假速率()
        {
            var f = NewFixture();
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();                       // t=0: 开会话 + 开窗口
            Assert.Null(f.Service.GetSnapshot());

            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();                       // t=1: 关闭首窗（预热期内）→ 丢弃

            Assert.Null(f.Service.GetSnapshot());
            Assert.False(f.Service.IsEffective);
            Assert.False(f.Service.TryGetFilteredRate("NET.Up", out _));
        }

        [Fact]
        public void 预热期之后给出正确的过滤速率()
        {
            var f = NewFixture();
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();                       // t=0

            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();                       // t=1 首窗丢弃
            f.Source.Emit(Event("10.0.0.1", "192.168.31.71", 1000, NetworkTrafficDirection.Send));
            f.Source.Emit(Event("10.0.0.2", "192.168.31.71", 500, NetworkTrafficDirection.Send));

            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();                       // t=2: 发布第二个窗口

            Assert.True(f.Service.IsEffective);
            Assert.True(f.Service.TryGetFilteredRate("NET.Up", out double up));
            Assert.Equal(500.0, up, 6);
            Assert.True(f.Service.TryGetFilteredRate("NET.Down", out double down));
            Assert.Equal(0.0, down, 6);
        }

        [Fact]
        public void 睡眠长间隔不会产生尖峰()
        {
            var f = NewFixture();
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();                       // t=2 发布正常窗口

            Assert.True(f.Service.TryGetFilteredRate("NET.Up", out double before));
            Assert.Equal(0.0, before, 6);

            // 模拟休眠 5 分钟后恢复：事件被丢弃，不补造尖峰
            f.Clock.Advance(TimeSpan.FromMinutes(5));
            f.Source.Emit(Event("10.0.0.2", "192.168.31.71", 5_000_000, NetworkTrafficDirection.Send));
            f.Service.Tick();

            Assert.Null(f.Service.GetSnapshot());
            Assert.False(f.Service.TryGetFilteredRate("NET.Up", out _));
        }

        // =========================================================
        // 失败态
        // =========================================================

        [Fact]
        public void 后端启动失败时状态为失败且按单调时间退避()
        {
            var f = NewFixture();
            f.Source.FailOnStart = true;
            f.Source.StartException = new UnauthorizedAccessException("Access is denied. (0x80070005 (E_ACCESSDENIED))");
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();

            Assert.Equal(1, f.Source.StartCount);
            NetworkTrafficExclusionStatus status = f.Service.GetStatus();
            Assert.Equal(NetworkTrafficExclusionState.Failed, status.State);
            Assert.False(status.Effective);

            // 退避期内不再重试
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();
            Assert.Equal(1, f.Source.StartCount);

            // 退避到期后才重试
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            f.Service.Tick();
            Assert.Equal(2, f.Source.StartCount);
        }

        [Fact]
        public void 权限不足时明确呈现未生效而不是过滤后的0()
        {
            var f = NewFixture();
            f.Source.FailOnStart = true;
            f.Source.StartException = new UnauthorizedAccessException("Access is denied. (0x80070005 (E_ACCESSDENIED))");
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();

            Assert.False(f.Service.TryGetFilteredRate("NET.Up", out _));
            Assert.Null(f.Service.GetSnapshot());
            Assert.Contains("E_ACCESSDENIED", f.Source.StatusDetail ?? "", StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void 收不到事件时给出事件缺失状态()
        {
            var f = NewFixture();
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();

            f.Clock.Advance(TimeSpan.FromSeconds(12));
            NetworkTrafficExclusionStatus status = f.Service.GetStatus();

            Assert.Equal(NetworkTrafficExclusionState.Failed, status.State);
            Assert.False(status.Effective);
        }

        // =========================================================
        // 热切换 / 停止 / 释放
        // =========================================================

        [Fact]
        public void 规则热更新后重新建立基线()
        {
            var f = NewFixture();
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();
            Assert.True(f.Service.IsEffective);

            f.Service.ApplyConfiguration(true, new[] { "10.0.0.9" });

            // 规则换代 → 旧快照作废（不出现尖峰）
            Assert.Null(f.Service.GetSnapshot());
            Assert.False(f.Service.IsEffective);
        }

        [Fact]
        public void 网卡切换后旧快照作废()
        {
            var f = NewFixture();
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();
            Assert.True(f.Service.IsEffective);

            f.Service.SetTargetAdapter(12, null);
            Assert.Null(f.Service.GetSnapshot());
        }

        [Fact]
        public void 关闭功能会停掉会话并恢复旧数据源()
        {
            var f = NewFixture();
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();
            Assert.Equal(1, f.Source.StartCount);

            f.Service.ApplyConfiguration(false, new[] { "10.0.0.1" });
            f.Service.Tick();

            Assert.Equal(1, f.Source.StopCount);
            Assert.False(f.Service.IsEnabled);
            Assert.Null(f.Service.GetSnapshot());
        }

        [Fact]
        public void 停止后可以恢复()
        {
            var f = NewFixture();
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();
            f.Service.Stop();
            Assert.Equal(1, f.Source.StopCount);

            f.Service.NotifyRealtimeDemand();
            f.Service.Tick();
            Assert.Equal(2, f.Source.StartCount);
        }

        [Fact]
        public void 释放后不会复活()
        {
            var f = NewFixture();
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();
            f.Service.Dispose();

            Assert.Equal(1, f.Source.DisposeCount);

            f.Service.Tick();
            f.Service.NotifyRealtimeDemand();
            f.Service.Tick();
            Assert.Equal(1, f.Source.StartCount);
        }

        [Fact]
        public void 重复释放是安全的()
        {
            var f = NewFixture();
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();
            f.Service.Dispose();
            f.Service.Dispose();

            Assert.Equal(1, f.Source.DisposeCount);
        }

        [Fact]
        public void 旧代次迟到的事件不会污染新窗口()
        {
            var f = NewFixture();
            Arm(f, new[] { "10.0.0.1" });
            f.Service.Tick();

            // 手动用旧代次喂事件（模拟上一代会话的迟到回调）
            var filterField = typeof(NetworkTrafficExclusionService)
                .GetField("_filter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(filterField);
            var filter = (NetworkTrafficFilter)filterField!.GetValue(f.Service)!;

            filter.Apply(Event("10.0.0.2", "192.168.31.71", 100_000, NetworkTrafficDirection.Send), 999);

            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Service.Tick();

            Assert.True(f.Service.TryGetFilteredRate("NET.Up", out double up));
            Assert.Equal(0.0, up, 6);
        }
    }
}
