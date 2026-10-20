using System;
using System.Threading;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 流量计时（计划 §4.3 / §6.1）的行为测试。
    ///
    /// 全部用**假时钟** + 构造的字节数驱动：
    /// - 不访问真实网卡；
    /// - 不启动 LHM / 驱动 / 外网请求；
    /// - 不写入用户的流量历史文件。
    ///
    /// 覆盖：稳定速率、原生字节计数、网卡切换、计数重置、长时间暂停、墙上时间回拨、SMB 扣除。
    /// </summary>
    public class TrafficDeltaTests
    {
        /// <summary>假时钟：单调时间戳与墙上时间独立。</summary>
        private sealed class FakeTimeProvider : TimeProvider
        {
            private long _ticks;
            private DateTimeOffset _wallClock;

            public FakeTimeProvider()
            {
                _wallClock = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
            }

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;

            public override long GetTimestamp() => Interlocked.Read(ref _ticks);

            public override DateTimeOffset GetUtcNow() => _wallClock;

            /// <summary>推进单调时间（同时推进墙上时间）。</summary>
            public void Advance(TimeSpan delta)
            {
                Interlocked.Add(ref _ticks, delta.Ticks);
                _wallClock += delta;
            }

            /// <summary>只回拨墙上时间（模拟系统时间被改回），单调时间戳不受影响。</summary>
            public void RewindWallClock(TimeSpan delta)
            {
                _wallClock -= delta;
            }
        }

        // =========================================================
        // 真实采样间隔
        // =========================================================

        [Fact]
        public void 稳定速率按两次真实采集之间的经过时间累计()
        {
            var clock = new FakeTimeProvider();
            var accumulator = new TrafficAccumulator(clock);
            var sample = new TrafficSample { LhmUpRate = 1000, LhmDownRate = 2000 };

            // 第一次采样：没有上一次真实采样，用调用方传入的间隔
            var first = accumulator.Accumulate(sample, 1.0);
            Assert.Equal(1000, first.UpBytes);
            Assert.Equal(2000, first.DownBytes);
            Assert.Equal(1.0, first.IntervalSeconds, 6);

            // 第二次采样：真实经过 2 秒（调用方传入的 99 秒被忽略）
            clock.Advance(TimeSpan.FromSeconds(2));
            var second = accumulator.Accumulate(sample, 99.0);
            Assert.Equal(2.0, second.IntervalSeconds, 6);
            Assert.Equal(2000, second.UpBytes);
            Assert.Equal(4000, second.DownBytes);
        }

        [Fact]
        public void 墙上时间回拨不产生负值或流量爆增()
        {
            var clock = new FakeTimeProvider();
            var accumulator = new TrafficAccumulator(clock);
            var sample = new TrafficSample { LhmUpRate = 1000, LhmDownRate = 1000 };

            accumulator.Accumulate(sample, 1.0);

            // 系统时间被改回：墙上时间倒退
            clock.RewindWallClock(TimeSpan.FromHours(2));

            // 单调时间只走了 1 秒
            clock.Advance(TimeSpan.FromSeconds(1));
            var result = accumulator.Accumulate(sample, 1.0);

            Assert.True(result.UpBytes >= 0);
            Assert.True(result.DownBytes >= 0);
            Assert.Equal(1.0, result.IntervalSeconds, 6);
            Assert.Equal(1000, result.UpBytes);
        }

        [Fact]
        public void 调用方传入负间隔时不会得到负增量()
        {
            var clock = new FakeTimeProvider();
            var accumulator = new TrafficAccumulator(clock);
            var sample = new TrafficSample { LhmUpRate = 5000, LhmDownRate = 5000 };

            // 第一次采样就拿到负间隔（墙上时间回拨的极端情况）
            var result = accumulator.Accumulate(sample, -3.0);

            Assert.Equal(0, result.IntervalSeconds, 6);
            Assert.Equal(0, result.UpBytes);
            Assert.Equal(0, result.DownBytes);
        }

        // =========================================================
        // 原生字节计数优先
        // =========================================================

        [Fact]
        public void 原生字节计数优先于LHM估算()
        {
            var clock = new FakeTimeProvider();
            var accumulator = new TrafficAccumulator(clock);

            // 首次：建立基线
            accumulator.Accumulate(new TrafficSample
            {
                LhmUpRate = 1000,
                LhmDownRate = 1000,
                NativeUpBytes = 1_000_000,
                NativeDownBytes = 2_000_000
            }, 1.0);

            clock.Advance(TimeSpan.FromSeconds(1));

            // LHM 说 1000 B/s，原生计数说实际多了 5000 字节
            var result = accumulator.Accumulate(new TrafficSample
            {
                LhmUpRate = 1000,
                LhmDownRate = 1000,
                NativeUpBytes = 1_005_000,
                NativeDownBytes = 2_003_000
            }, 1.0);

            Assert.True(result.NativeAvailable);
            Assert.False(result.NativeMismatch);
            Assert.Equal(5000, result.UpBytes);
            Assert.Equal(3000, result.DownBytes);
        }

        [Fact]
        public void 首次计数初始化不产生增量()
        {
            var clock = new FakeTimeProvider();
            var accumulator = new TrafficAccumulator(clock);

            var result = accumulator.Accumulate(new TrafficSample
            {
                LhmUpRate = 100,
                LhmDownRate = 100,
                NativeUpBytes = 1_000_000,
                NativeDownBytes = 2_000_000
            }, 1.0);

            Assert.True(accumulator.HasBaseline);
            Assert.Equal(0, result.UpBytes);
            Assert.Equal(0, result.DownBytes);
        }

        [Fact]
        public void 网卡切换后重新建立基线不会产生增量()
        {
            var clock = new FakeTimeProvider();
            var accumulator = new TrafficAccumulator(clock);

            accumulator.Accumulate(new TrafficSample
            {
                NativeUpBytes = 1_000_000,
                NativeDownBytes = 2_000_000
            }, 1.0);

            // 换网卡：计数基线被重置
            accumulator.ResetBaseline(50_000, 60_000);

            clock.Advance(TimeSpan.FromSeconds(1));
            var result = accumulator.Accumulate(new TrafficSample
            {
                NativeUpBytes = 50_500,
                NativeDownBytes = 60_500
            }, 1.0);

            Assert.Equal(500, result.UpBytes);
            Assert.Equal(500, result.DownBytes);
        }

        [Fact]
        public void 计数重置不产生负增量()
        {
            var clock = new FakeTimeProvider();
            var accumulator = new TrafficAccumulator(clock);

            accumulator.Accumulate(new TrafficSample
            {
                NativeUpBytes = 1_000_000,
                NativeDownBytes = 2_000_000
            }, 1.0);

            clock.Advance(TimeSpan.FromSeconds(1));

            // 计数器被重置（比上次小）
            var result = accumulator.Accumulate(new TrafficSample
            {
                NativeUpBytes = 100,
                NativeDownBytes = 200
            }, 1.0);

            Assert.Equal(0, result.UpBytes);
            Assert.Equal(0, result.DownBytes);
        }

        [Fact]
        public void 原生与LHM明显失配时回退到LHM并标记不匹配()
        {
            var clock = new FakeTimeProvider();
            var accumulator = new TrafficAccumulator(clock);

            accumulator.Accumulate(new TrafficSample
            {
                LhmUpRate = 0,
                LhmDownRate = 0,
                NativeUpBytes = 1_000_000,
                NativeDownBytes = 2_000_000
            }, 1.0);

            clock.Advance(TimeSpan.FromSeconds(1));

            // 原生计数没动，但 LHM 说有 100KB/s 的流量 -> 认为匹配错了网卡
            var result = accumulator.Accumulate(new TrafficSample
            {
                LhmUpRate = 100_000,
                LhmDownRate = 0,
                NativeUpBytes = 1_000_000,
                NativeDownBytes = 2_000_000
            }, 1.0);

            Assert.True(result.NativeMismatch);
            Assert.Equal(100_000, result.UpBytes);
        }

        // =========================================================
        // 长时间暂停 / 休眠
        // =========================================================

        [Fact]
        public void 长时间暂停按重置策略处理不补造流量尖峰()
        {
            var clock = new FakeTimeProvider();
            var accumulator = new TrafficAccumulator(clock);

            accumulator.Accumulate(new TrafficSample
            {
                NativeUpBytes = 1_000_000,
                NativeDownBytes = 2_000_000
            }, 1.0);

            // 模拟休眠 10 分钟后恢复
            clock.Advance(TimeSpan.FromMinutes(10));
            var result = accumulator.Accumulate(new TrafficSample
            {
                LhmUpRate = 1_000_000,
                LhmDownRate = 1_000_000,
                NativeUpBytes = 9_000_000,
                NativeDownBytes = 12_000_000
            }, 1.0);

            Assert.True(result.ResetByLongGap);
            Assert.Equal(0, result.UpBytes);
            Assert.Equal(0, result.DownBytes);

            // 基线被重建：下一次从当前计数开始，不把休眠期间的字节一次性算进来
            clock.Advance(TimeSpan.FromSeconds(1));
            var next = accumulator.Accumulate(new TrafficSample
            {
                NativeUpBytes = 9_000_500,
                NativeDownBytes = 12_000_500
            }, 1.0);

            Assert.Equal(500, next.UpBytes);
            Assert.Equal(500, next.DownBytes);
        }

        [Fact]
        public void 超过安全阀的单次增量被丢弃()
        {
            var clock = new FakeTimeProvider();
            var accumulator = new TrafficAccumulator(clock);

            accumulator.Accumulate(new TrafficSample { NativeUpBytes = 0, NativeDownBytes = 0 }, 1.0);

            clock.Advance(TimeSpan.FromSeconds(1));
            var result = accumulator.Accumulate(new TrafficSample
            {
                NativeUpBytes = TrafficAccumulator.MaxSingleDeltaBytes + 1,
                NativeDownBytes = 0
            }, 1.0);

            Assert.Equal(0, result.UpBytes);
        }

        // =========================================================
        // SMB 扣除
        // =========================================================

        [Fact]
        public void SMB流量按真实间隔估算并扣除()
        {
            var clock = new FakeTimeProvider();
            var accumulator = new TrafficAccumulator(clock);

            // SMB 速率 1000 B/s（上传）/ 2000 B/s（下载），按传入的间隔估算
            double observedInterval = 0;
            var smbEstimator = new Func<double, (long Up, long Down)>(seconds =>
            {
                observedInterval = seconds;
                return (1000 * (long)seconds, 2000 * (long)seconds);
            });

            accumulator.Accumulate(new TrafficSample { LhmUpRate = 0, LhmDownRate = 0 }, 1.0);

            clock.Advance(TimeSpan.FromSeconds(3));
            var result = accumulator.Accumulate(new TrafficSample
            {
                LhmUpRate = 10_000,
                LhmDownRate = 10_000
            }, 1.0, smbEstimator);

            // 估算器拿到的是真实间隔（3 秒），不是调用方传入的 1 秒
            Assert.Equal(3.0, observedInterval, 6);
            // LHM 估算 30000 字节，扣除 SMB 3000 / 6000
            Assert.Equal(27_000, result.UpBytes);
            Assert.Equal(24_000, result.DownBytes);
        }

        [Fact]
        public void SMB扣除不会把流量扣成负数()
        {
            var clock = new FakeTimeProvider();
            var accumulator = new TrafficAccumulator(clock);

            var smbEstimator = new Func<double, (long Up, long Down)>(_ => (1_000_000, 1_000_000));

            accumulator.Accumulate(new TrafficSample { LhmUpRate = 0, LhmDownRate = 0 }, 1.0);

            clock.Advance(TimeSpan.FromSeconds(1));
            var result = accumulator.Accumulate(new TrafficSample
            {
                LhmUpRate = 1000,
                LhmDownRate = 1000
            }, 1.0, smbEstimator);

            Assert.Equal(0, result.UpBytes);
            Assert.Equal(0, result.DownBytes);
        }
    }
}
