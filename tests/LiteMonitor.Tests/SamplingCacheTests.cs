using System;
using System.Collections.Generic;
using System.Threading;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 采集代次值缓存的行为测试。
    ///
    /// 这些用例**不启动 LHM、不碰驱动、不访问外网**：
    /// 只构造一个没有打开的 Computer（硬件列表为空），
    /// 验证"同代次只算一次 / 合法 0 缓存 / 不可用结果缓存 / 新代次更新 / 换卡不串卡"
    /// 这些纯缓存语义。
    /// </summary>
    public class SamplingCacheTests
    {
        /// <summary>假时钟（只用于启动期缓存的 TTL 判定）。</summary>
        private sealed class FakeTimeProvider : TimeProvider
        {
            private long _ticks;

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => Interlocked.Read(ref _ticks);
            public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

            public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
        }

        private sealed class Fixture
        {
            public Settings Cfg;
            public SensorMap Map;
            public HardwareValueProvider Provider;
            public HardwareSamplingStatistics Statistics;
            public Dictionary<string, float> LastValid;
            public FakeTimeProvider Clock;
        }

        private static Fixture NewFixture()
        {
            var fixture = new Fixture
            {
                Cfg = new Settings(),
                Map = new SensorMap(),
                Statistics = new HardwareSamplingStatistics(),
                LastValid = new Dictionary<string, float>(),
                Clock = new FakeTimeProvider(),
            };

            fixture.Provider = new HardwareValueProvider(
                new Computer(),
                fixture.Cfg,
                fixture.Map,
                new NetworkManager(),
                new DiskManager(),
                null,                       // 不需要 FPS（FPS 有自己的外部服务）
                new PerformanceCounterManager(),
                new object(),
                fixture.LastValid,
                fixture.Statistics,
                fixture.Clock);

            return fixture;
        }

        // =========================================================
        // GenerationValueCache 本身的语义
        // =========================================================

        [Fact]
        public void 合法的0会被缓存()
        {
            var cache = new GenerationValueCache();
            cache.SetValue("CPU.Load", 0f);

            Assert.True(cache.TryGetValue("CPU.Load", out float? value));
            Assert.NotNull(value);
            Assert.Equal(0f, value.Value);
        }

        [Fact]
        public void 本代次不可用的结果也会被缓存()
        {
            var cache = new GenerationValueCache();
            cache.SetValue("GPU.0.Temp", null);

            // 返回 true 表示"本代次已经算过"，value 为 null 表示不可用
            Assert.True(cache.TryGetValue("GPU.0.Temp", out float? value));
            Assert.Null(value);
        }

        [Fact]
        public void 新代次会让旧代次的缓存整体作废()
        {
            var cache = new GenerationValueCache();
            cache.SetValue("CPU.Load", 12f);
            Assert.True(cache.TryGetValue("CPU.Load", out _));

            cache.BeginGeneration(2);

            Assert.False(cache.TryGetValue("CPU.Load", out _));
            Assert.Equal(2, cache.Generation);
        }

        [Fact]
        public void 没有进入新代次时缓存保持不变()
        {
            var cache = new GenerationValueCache();
            cache.SetValue("CPU.Load", 12f);

            // "未到期"不会推进代次，因此缓存不清
            Assert.True(cache.TryGetValue("CPU.Load", out float? value));
            Assert.Equal(12f, value.Value);
        }

        // =========================================================
        // HardwareValueProvider 的接线
        // =========================================================

        [Fact]
        public void 同一代次多个消费者只读取一次()
        {
            var f = NewFixture();
            f.Provider.OnCollectionStarted(1);

            long before = f.Statistics.ValueComputations;

            f.Provider.GetValue("CPU.Temp");
            f.Provider.GetValue("CPU.Temp");
            f.Provider.GetValue("CPU.Temp");

            // 三个消费者读同一个 Key，只真正计算了一次
            Assert.Equal(before + 1, f.Statistics.ValueComputations);
        }

        [Fact]
        public void 未到期不会清空取值缓存()
        {
            var f = NewFixture();
            f.Provider.OnCollectionStarted(1);

            f.Provider.GetValue("CPU.Temp");
            long afterFirst = f.Statistics.ValueComputations;

            // 没有新的采集代次 -> 缓存仍然有效 -> 不会再算一次
            f.Provider.GetValue("CPU.Temp");
            Assert.Equal(afterFirst, f.Statistics.ValueComputations);
        }

        [Fact]
        public void 新代次可以重新计算()
        {
            var f = NewFixture();
            f.Provider.OnCollectionStarted(1);

            f.Provider.GetValue("CPU.Temp");
            long afterFirst = f.Statistics.ValueComputations;

            f.Provider.OnCollectionStarted(2);
            f.Provider.GetValue("CPU.Temp");

            Assert.Equal(afterFirst + 1, f.Statistics.ValueComputations);
        }

        [Fact]
        public void 设备切换不会串用上一张卡的最后有效值()
        {
            var f = NewFixture();
            f.LastValid["GPU.0.Temp"] = 55f;
            f.LastValid["GPU.0.Fan"] = 1200f;
            f.LastValid["CPU.Temp"] = 40f;

            // 用户把首选显卡换成了另一张卡
            f.Cfg.PreferredGpu = "NVIDIA GeForce RTX 3080";

            f.Provider.OnCollectionStarted(2);

            // 显卡相关的最后有效值必须被丢掉（否则会显示另一张卡的温度）
            Assert.False(f.LastValid.ContainsKey("GPU.0.Temp"));
            Assert.False(f.LastValid.ContainsKey("GPU.0.Fan"));
            // 其它设备的最后有效值保留
            Assert.True(f.LastValid.ContainsKey("CPU.Temp"));
        }

        [Fact]
        public void 映射重建后取值缓存能够恢复()
        {
            var f = NewFixture();
            f.Provider.OnCollectionStarted(1);
            f.Provider.GetValue("CPU.Temp");
            long afterFirst = f.Statistics.ValueComputations;

            // 十分钟兜底重建 / 重载走的都是 PreCacheAllSensors：
            // 它必须让取值缓存重新可用，而不是把旧值一直冻住。
            f.Provider.PreCacheAllSensors(f.Map);

            f.Provider.GetValue("CPU.Temp");
            Assert.Equal(afterFirst + 1, f.Statistics.ValueComputations);
        }

        [Fact]
        public void 启动期取值有TTL_不会按界面频率反复读性能计数器()
        {
            var f = NewFixture();

            long before = f.Statistics.ValueComputations;
            f.Provider.GetStartupValue("CPU.Load");
            f.Provider.GetStartupValue("CPU.Load");
            f.Provider.GetStartupValue("CPU.Load");

            // 同一个 TTL 窗口内只算一次
            Assert.Equal(before + 1, f.Statistics.ValueComputations);

            // TTL 过期后允许重新计算
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            f.Provider.GetStartupValue("CPU.Load");
            Assert.Equal(before + 2, f.Statistics.ValueComputations);
        }

        [Fact]
        public void 取值失败不会把失败永久缓存成健康的0之后又被复用()
        {
            var f = NewFixture();
            f.Provider.OnCollectionStarted(1);

            // 没有硬件 -> 取不到值。缓存必须记住"本代次不可用"，
            // 这样同一代次的第二个消费者不会再去走一遍昂贵回退。
            float? first = f.Provider.GetValue("GPU.0.Temp");
            long afterFirst = f.Statistics.ValueComputations;

            float? second = f.Provider.GetValue("GPU.0.Temp");

            Assert.Equal(first, second);
            Assert.Equal(afterFirst, f.Statistics.ValueComputations);
        }
    }
}
