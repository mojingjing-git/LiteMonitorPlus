using System;
using System.Collections.Generic;
using System.Threading;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 需要独占进程级静态状态（SystemOptimizer.MaintenanceScheduler）的用例集合。
    /// 显式禁用并行，避免与将来其它触碰该静态的用例互相污染。
    /// </summary>
    [CollectionDefinition("SystemOptimizerStatic", DisableParallelization = true)]
    public sealed class SystemOptimizerStaticCollection
    {
    }

    /// <summary>
    /// 维护任务调度（计划 §4.3 / §6.1）的行为测试。
    ///
    /// 全部用**假时钟**驱动，不用几秒/几分钟的 Sleep 验证定时逻辑。
    /// 假时钟把「单调时间戳」和「墙上时间」分开控制，因此可以直接验证
    /// 「墙上时间回拨不影响调度」这条硬性要求。
    ///
    /// 覆盖的已实测缺陷：旧实现用 `secondsCounter % 60 == 5` 判断到期，
    /// 同一个到期秒里被调用十次就会真正执行十次（十次文件重写）。
    ///
    /// ★ 本类会改写进程级静态 `SystemOptimizer.MaintenanceScheduler`，
    ///   因此归入禁并行 collection，避免与将来其它触碰该静态的用例互相污染。
    /// </summary>
    [Collection("SystemOptimizerStatic")]
    public class MaintenanceScheduleTests
    {
        /// <summary>
        /// 假时钟：单调时间戳（GetTimestamp）与墙上时间（GetUtcNow）独立。
        /// 频率取 TimeSpan.TicksPerSecond（100ns 精度）。
        /// </summary>
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
        // 去重：同一到期时间最多消费一次
        // =========================================================

        [Fact]
        public void 同一到期秒内重复调用只保存一次()
        {
            var clock = new FakeTimeProvider();
            int saves = 0;
            var scheduler = new MaintenanceScheduler(onTrafficSave: () => saves++, timeProvider: clock);

            clock.Advance(TimeSpan.FromSeconds(5));

            // 同一个到期秒内调用十次（对应旧实现的 %60==5 缺陷）
            for (int i = 0; i < 10; i++)
            {
                scheduler.RunDueTasks();
            }

            Assert.Equal(1, saves);
        }

        [Fact]
        public void 自动GC请求在三十秒内不能反复触发()
        {
            var clock = new FakeTimeProvider();
            int gcRequests = 0;
            var scheduler = new MaintenanceScheduler(onGcRequest: () => gcRequests++, timeProvider: clock);

            clock.Advance(TimeSpan.FromSeconds(30));

            for (int i = 0; i < 10; i++)
            {
                scheduler.RunDueTasks();
            }

            Assert.Equal(1, gcRequests);
        }

        [Fact]
        public void 未到期的调用不执行任何动作()
        {
            var clock = new FakeTimeProvider();
            int saves = 0, gcRequests = 0, trims = 0;
            var scheduler = new MaintenanceScheduler(
                onTrafficSave: () => saves++,
                onGcRequest: () => gcRequests++,
                onWorkingSetTrim: () => trims++,
                timeProvider: clock);

            clock.Advance(TimeSpan.FromSeconds(4));
            var result = scheduler.RunDueTasks();

            Assert.Equal(0, saves);
            Assert.Equal(0, gcRequests);
            Assert.Equal(0, trims);
            Assert.False(result.Any);
        }

        // =========================================================
        // 不补跑：跳过若干周期后最多执行一次
        // =========================================================

        [Fact]
        public void 跨多个期限只执行一次不补跑()
        {
            var clock = new FakeTimeProvider();
            int saves = 0;
            var scheduler = new MaintenanceScheduler(onTrafficSave: () => saves++, timeProvider: clock);

            // 首次到期
            clock.Advance(TimeSpan.FromSeconds(5));
            scheduler.RunDueTasks();
            Assert.Equal(1, saves);

            // 一口气跳过 10 个周期（10 分钟）
            clock.Advance(TimeSpan.FromMinutes(10));
            var result = scheduler.RunDueTasks();
            Assert.Equal(2, saves);          // 只执行一次（1 + 1），不补跑一串
            Assert.True(result.TrafficSave);

            // 下一次到期仍然按 60 秒周期推进
            clock.Advance(TimeSpan.FromSeconds(59));
            scheduler.RunDueTasks();
            Assert.Equal(2, saves);

            clock.Advance(TimeSpan.FromSeconds(1));
            scheduler.RunDueTasks();
            Assert.Equal(3, saves);
        }

        [Fact]
        public void 长间隔可到期而不会因为错过整秒永不保存()
        {
            var clock = new FakeTimeProvider();
            int saves = 0;
            var scheduler = new MaintenanceScheduler(onTrafficSave: () => saves++, timeProvider: clock);

            // 直接跳到 1 小时后（模拟休眠 / 长时间无请求）
            clock.Advance(TimeSpan.FromHours(1));
            scheduler.RunDueTasks();

            Assert.Equal(1, saves);
        }

        // =========================================================
        // 首次偏移与后续周期
        // =========================================================

        [Fact]
        public void 流量保存首次5秒此后每60秒()
        {
            var clock = new FakeTimeProvider();
            int saves = 0;
            var scheduler = new MaintenanceScheduler(onTrafficSave: () => saves++, timeProvider: clock);

            clock.Advance(TimeSpan.FromSeconds(4));
            scheduler.RunDueTasks();
            Assert.Equal(0, saves);

            clock.Advance(TimeSpan.FromSeconds(1));      // t = 5s
            scheduler.RunDueTasks();
            Assert.Equal(1, saves);

            clock.Advance(TimeSpan.FromSeconds(59));     // t = 64s
            scheduler.RunDueTasks();
            Assert.Equal(1, saves);

            clock.Advance(TimeSpan.FromSeconds(1));      // t = 65s
            scheduler.RunDueTasks();
            Assert.Equal(2, saves);

            clock.Advance(TimeSpan.FromSeconds(60));     // t = 125s
            scheduler.RunDueTasks();
            Assert.Equal(3, saves);
        }

        [Fact]
        public void 自动GC首次30秒此后每180秒()
        {
            var clock = new FakeTimeProvider();
            int gcRequests = 0;
            var scheduler = new MaintenanceScheduler(onGcRequest: () => gcRequests++, timeProvider: clock);

            clock.Advance(TimeSpan.FromSeconds(29));
            scheduler.RunDueTasks();
            Assert.Equal(0, gcRequests);

            clock.Advance(TimeSpan.FromSeconds(1));      // t = 30s
            scheduler.RunDueTasks();
            Assert.Equal(1, gcRequests);

            clock.Advance(TimeSpan.FromSeconds(179));    // t = 209s
            scheduler.RunDueTasks();
            Assert.Equal(1, gcRequests);

            clock.Advance(TimeSpan.FromSeconds(1));      // t = 210s
            scheduler.RunDueTasks();
            Assert.Equal(2, gcRequests);
        }

        [Fact]
        public void 自动工作集检查首次45秒此后每300秒()
        {
            var clock = new FakeTimeProvider();
            int trims = 0;
            var scheduler = new MaintenanceScheduler(onWorkingSetTrim: () => trims++, timeProvider: clock);

            clock.Advance(TimeSpan.FromSeconds(44));
            scheduler.RunDueTasks();
            Assert.Equal(0, trims);

            clock.Advance(TimeSpan.FromSeconds(1));      // t = 45s
            scheduler.RunDueTasks();
            Assert.Equal(1, trims);

            clock.Advance(TimeSpan.FromSeconds(299));    // t = 344s
            scheduler.RunDueTasks();
            Assert.Equal(1, trims);

            clock.Advance(TimeSpan.FromSeconds(1));      // t = 345s
            scheduler.RunDueTasks();
            Assert.Equal(2, trims);
        }

        // =========================================================
        // 单调时钟：墙上时间回拨不影响调度
        // =========================================================

        [Fact]
        public void 墙上时间回拨不影响调度()
        {
            var clock = new FakeTimeProvider();
            int saves = 0;
            var scheduler = new MaintenanceScheduler(onTrafficSave: () => saves++, timeProvider: clock);

            clock.Advance(TimeSpan.FromSeconds(5));
            scheduler.RunDueTasks();
            Assert.Equal(1, saves);

            // 系统时间被改回 3 小时
            clock.RewindWallClock(TimeSpan.FromHours(3));

            // 单调时间继续走，到期点仍然是 65 秒
            clock.Advance(TimeSpan.FromSeconds(60));
            scheduler.RunDueTasks();
            Assert.Equal(2, saves);
        }

        // =========================================================
        // 入口兼容性：旧签名必须保持可用
        // =========================================================

        [Fact]
        public void 维护任务入口签名保持不变()
        {
            var method = typeof(SystemOptimizer).GetMethod("RunMaintenanceTasks", new[] { typeof(long) });
            Assert.NotNull(method);
            Assert.Equal(typeof(void), method!.ReturnType);
        }

        [Fact]
        public void 旧入口走同一个调度器实例且不再依赖整数秒计数器()
        {
            var clock = new FakeTimeProvider();
            int saves = 0;
            var original = SystemOptimizer.MaintenanceScheduler;
            try
            {
                SystemOptimizer.MaintenanceScheduler = new MaintenanceScheduler(
                    onTrafficSave: () => saves++,
                    timeProvider: clock);

                clock.Advance(TimeSpan.FromSeconds(5));

                // 传入的整数秒计数器不再参与调度判断
                SystemOptimizer.RunMaintenanceTasks(12345);
                Assert.Equal(1, saves);

                // 同一到期时间再次调用不再重复执行
                SystemOptimizer.RunMaintenanceTasks(12345);
                Assert.Equal(1, saves);
            }
            finally
            {
                SystemOptimizer.MaintenanceScheduler = original;
            }
        }

        [Fact]
        public void 调度结果只标记本次真正到期并被消费的任务()
        {
            var clock = new FakeTimeProvider();
            var scheduler = new MaintenanceScheduler(timeProvider: clock);

            // t = 5s：只有流量保存到期
            clock.Advance(TimeSpan.FromSeconds(5));
            var first = scheduler.RunDueTasks();
            Assert.True(first.TrafficSave);
            Assert.False(first.GcRequest);
            Assert.False(first.WorkingSetTrim);

            // 同一秒内再次调用：什么都不到期
            var second = scheduler.RunDueTasks();
            Assert.False(second.Any);
        }

        // =========================================================
        // 同一到期时间只消费一次：并发调用也必须成立
        // =========================================================

        [Fact]
        public void 同一到期时间并发消费只成功一次()
        {
            var clock = new FakeTimeProvider();
            var trigger = new MonotonicPeriodicTrigger(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), clock);

            clock.Advance(TimeSpan.FromSeconds(5));

            int consumed = 0;
            using var start = new ManualResetEventSlim(false);
            var threads = new List<Thread>();
            for (int i = 0; i < 8; i++)
            {
                var thread = new Thread(() =>
                {
                    start.Wait();
                    if (trigger.TryConsume()) Interlocked.Increment(ref consumed);
                });
                threads.Add(thread);
                thread.Start();
            }

            start.Set();
            foreach (var thread in threads) thread.Join();

            // 核心不变量：同一个到期时间最多被消费一次
            Assert.Equal(1, consumed);
        }

        [Fact]
        public void 调度器并发调用同一到期时间只执行一次()
        {
            var clock = new FakeTimeProvider();
            int saves = 0;
            var scheduler = new MaintenanceScheduler(onTrafficSave: () => Interlocked.Increment(ref saves), timeProvider: clock);

            clock.Advance(TimeSpan.FromSeconds(5));

            using var start = new ManualResetEventSlim(false);
            var threads = new List<Thread>();
            for (int i = 0; i < 8; i++)
            {
                var thread = new Thread(() =>
                {
                    start.Wait();
                    scheduler.RunDueTasks();
                });
                threads.Add(thread);
                thread.Start();
            }

            start.Set();
            foreach (var thread in threads) thread.Join();

            Assert.Equal(1, saves);
        }

        // =========================================================
        // 剩余时间（诊断用）与"调用方持有自己的实例"
        // =========================================================

        [Fact]
        public void 剩余时间反映下一次到期点()
        {
            var clock = new FakeTimeProvider();
            var trigger = new MonotonicPeriodicTrigger(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), clock);

            // 刚创建：距首次到期还有 5 秒
            Assert.Equal(5.0, trigger.Remaining.TotalSeconds, 3);

            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(3.0, trigger.Remaining.TotalSeconds, 3);

            // 消费之后：距下一次到期 60 秒
            clock.Advance(TimeSpan.FromSeconds(3));
            Assert.True(trigger.TryConsume());
            Assert.Equal(60.0, trigger.Remaining.TotalSeconds, 3);

            // 已经到期时剩余时间为 0
            clock.Advance(TimeSpan.FromSeconds(60));
            Assert.Equal(0.0, trigger.Remaining.TotalSeconds, 3);
        }

        [Fact]
        public void 调用方可以持有自己的调度器实例()
        {
            // A2b 的接线形状：SystemOptimizer.CreateScheduler() 返回一个独立实例，
            // 周期状态属于调用方，而不是进程级静态单例。
            var clock = new FakeTimeProvider();
            var first = SystemOptimizer.CreateScheduler(clock);
            var second = SystemOptimizer.CreateScheduler(clock);

            // 每次调用得到的是**不同**实例：调用方之间的周期状态互不影响
            Assert.NotSame(first, second);
            Assert.NotSame(first, SystemOptimizer.MaintenanceScheduler);

            // 实例各自排定自己的截止时间（首次流量保存在 5 秒后）
            Assert.Equal(5.0, first.NextTrafficSaveDelay.TotalSeconds, 3);
            Assert.Equal(5.0, second.NextTrafficSaveDelay.TotalSeconds, 3);

            // 注意：这里不调用 RunDueTasks()——CreateScheduler 接入的是生产动作
            //（写流量历史文件 / 真实 GC），单元测试不应触发它们。
        }
    }
}
