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

        private static Fixture NewFixture(Action? onDeviceSelectionChanged = null)
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
                fixture.Clock,
                onDeviceSelectionChanged);

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
            f.LastValid["CPU.Temp"] = 40f;

            // 用户把首选显卡换成了另一张卡
            f.Cfg.PreferredGpu = "NVIDIA GeForce RTX 3080";

            f.Provider.OnCollectionStarted(2);

            // 显卡相关的"最后有效值"必须被标记作废（否则会显示另一张卡的温度）
            Assert.True(f.Provider.IsLastValidValueStale("GPU.0.Temp"));
            Assert.True(f.Provider.IsLastValidValueStale("GPU.0.Fan"));
            // 其它设备不受影响
            Assert.False(f.Provider.IsLastValidValueStale("CPU.Temp"));
            Assert.True(f.LastValid.ContainsKey("CPU.Temp"));
        }

        [Fact]
        public void 设备切换只标记不直接改共享字典()
        {
            var f = NewFixture();
            f.LastValid["GPU.0.Temp"] = 55f;

            f.Cfg.PreferredGpu = "NVIDIA GeForce RTX 3080";
            f.Provider.OnCollectionStarted(2);

            // 采集线程不写共享的 _lastValidMap（非线程安全，GetValue 抢锁失败时会无锁读它）。
            // 真正的删除推迟到"有人持锁读这个 Key"的时候。
            Assert.True(f.LastValid.ContainsKey("GPU.0.Temp"));
        }

        [Fact]
        public void 设备选择变更会通知外部重新采样()
        {
            int notified = 0;
            var f = NewFixture(onDeviceSelectionChanged: () => notified++);

            // 没有变更 -> 不通知
            f.Provider.OnCollectionStarted(1);
            Assert.Equal(0, notified);

            // 换了首选显卡 -> 通知一次（生产侧接协调器 Invalidate()）
            f.Cfg.PreferredGpu = "NVIDIA GeForce RTX 3080";
            f.Provider.OnCollectionStarted(2);
            Assert.Equal(1, notified);

            // 再来一轮：配置已经应用过，不重复通知
            f.Provider.OnCollectionStarted(3);
            Assert.Equal(1, notified);
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

        // =========================================================
        // 合法 0：必须被缓存，而且不能被当成"缺失"
        // =========================================================

        [Fact]
        public void 合法的0会被缓存_不会被当成缺失()
        {
            var f = NewFixture();

            // 预置一个"最后有效值 = 0"（0 是合法读数，例如网卡当前没有流量）
            f.LastValid["NET.Up"] = 0f;

            f.Provider.OnCollectionStarted(1);
            long before = f.Statistics.ValueComputations;

            float? first = f.Provider.GetValue("NET.Up");

            // 取到的是 0，而不是 null（0 不是缺失）
            Assert.NotNull(first);
            Assert.Equal(0f, first.Value);

            long afterFirst = f.Statistics.ValueComputations;
            float? second = f.Provider.GetValue("NET.Up");

            // 同代次第二个消费者直接命中缓存，不再重算
            Assert.Equal(0f, second.Value);
            Assert.Equal(afterFirst, f.Statistics.ValueComputations);
            Assert.Equal(before + 1, afterFirst);
        }

        [Fact]
        public void 合法的0在CPU温度兜底上同样被缓存()
        {
            var f = NewFixture();
            f.LastValid["CPU.Temp"] = 0f;

            f.Provider.OnCollectionStarted(1);
            long before = f.Statistics.ValueComputations;

            float? first = f.Provider.GetValue("CPU.Temp");
            Assert.NotNull(first);
            Assert.Equal(0f, first.Value);

            long afterFirst = f.Statistics.ValueComputations;
            float? second = f.Provider.GetValue("CPU.Temp");

            Assert.Equal(0f, second.Value);
            Assert.Equal(afterFirst, f.Statistics.ValueComputations);
        }

        [Fact]
        public void 采集落定后旧代次的缓存被作废()
        {
            var f = NewFixture();
            f.Provider.OnCollectionStarted(1);

            f.Provider.GetValue("CPU.Temp");
            long afterFirst = f.Statistics.ValueComputations;

            // 采集结束：把"采集中途被读到并写进缓存的旧值"作废
            f.Provider.OnCollectionSettled(1);

            f.Provider.GetValue("CPU.Temp");
            Assert.Equal(afterFirst + 1, f.Statistics.ValueComputations);
        }

        // =========================================================
        // ★ 复审 R2 [P2]：旧代次的收尾不能清掉新代次已经算好的值
        // =========================================================

        [Fact]
        public void 旧代次的收尾不会清掉新代次的缓存()
        {
            var cache = new GenerationValueCache();
            cache.BeginGeneration(2);
            cache.SetValue("CPU.Load", 12f);

            // 旧代次（1）的收尾晚到
            cache.SettleGeneration(1);

            // 新代次已经算好的值不能被清掉
            Assert.True(cache.TryGetValue("CPU.Load", out float? value));
            Assert.Equal(12f, value.Value);

            // 代次也不能被往回写
            Assert.Equal(2, cache.Generation);
        }

        [Fact]
        public void 旧代次的落定不会作废新代次已经算好的值()
        {
            var f = NewFixture();
            f.Provider.OnCollectionStarted(2);

            f.Provider.GetValue("CPU.Temp");
            long afterFirst = f.Statistics.ValueComputations;

            // 旧代次（1）的收尾晚到：不能把新代次的值作废掉
            f.Provider.OnCollectionSettled(1);

            f.Provider.GetValue("CPU.Temp");
            Assert.Equal(afterFirst, f.Statistics.ValueComputations);
        }

        [Fact]
        public void 更旧的代次不能把缓存代次往回写()
        {
            var cache = new GenerationValueCache();
            cache.BeginGeneration(5);
            cache.SetValue("CPU.Load", 12f);

            // 更旧的代次推进：代次不动、缓存也不清
            cache.BeginGeneration(3);

            Assert.Equal(5, cache.Generation);
            Assert.True(cache.TryGetValue("CPU.Load", out float? value));
            Assert.Equal(12f, value.Value);
        }
    }
}
