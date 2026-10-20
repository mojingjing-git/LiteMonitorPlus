using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 采样协调器（HardwareSamplingCoordinator）的行为测试。
    ///
    /// 全部用**假时钟**驱动，不用几秒/几分钟的 Sleep 验证定时逻辑。
    /// 假时钟把"单调时间戳"和"墙上时间"分开控制，因此可以直接验证
    /// "墙上时间回拨不影响调度"这条硬性要求。
    /// </summary>
    public class SamplingCoordinatorTests
    {
        /// <summary>
        /// 假时钟：单调时间戳（GetTimestamp）与墙上时间（GetUtcNow）独立。
        /// 频率取 TimeSpan.TicksPerSecond（100ns 精度），与 TimeSpan.Ticks 一一对应。
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

        private static HardwareSamplingCoordinator NewCoordinator(
            FakeTimeProvider clock,
            Func<int> periodProvider,
            Func<CancellationToken, Task> sampler,
            HardwareSamplingStatistics statistics = null)
        {
            return new HardwareSamplingCoordinator(
                periodProvider: periodProvider,
                sampler: sampler,
                timeProvider: clock,
                statistics: statistics);
        }

        // =========================================================
        // 周期门限
        // =========================================================

        [Fact]
        public async Task 界面100ms请求_60秒内真实采集不超过约60次()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;

            var coordinator = NewCoordinator(clock, () => 1000, ct =>
            {
                Interlocked.Increment(ref collections);
                return Task.CompletedTask;
            });
            coordinator.SetReady(true);

            // 600 次请求 x 100ms = 60 秒
            for (int i = 0; i < 600; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await coordinator.RequestCollectionAsync();
            }

            // 固定网格：60 秒正好 60 次（请求延迟不会让周期累积漂移）
            Assert.Equal(60, collections);
        }

        [Fact]
        public async Task 界面3000ms请求_60秒内真实采集约20次()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;

            var coordinator = NewCoordinator(clock, () => 3000, ct =>
            {
                Interlocked.Increment(ref collections);
                return Task.CompletedTask;
            });
            coordinator.SetReady(true);

            for (int i = 0; i < 600; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await coordinator.RequestCollectionAsync();
            }

            // 3000ms 周期：60 秒里 20 次
            Assert.Equal(20, collections);
        }

        [Fact]
        public async Task 界面1000ms与100ms的采集次数同量级()
        {
            // 同样的 60 秒窗口，只是界面请求频率不同
            async Task<int> RunAsync(int uiPeriodMs)
            {
                var clock = new FakeTimeProvider();
                int collections = 0;
                var coordinator = NewCoordinator(clock, () => 1000, ct =>
                {
                    Interlocked.Increment(ref collections);
                    return Task.CompletedTask;
                });
                coordinator.SetReady(true);

                for (int i = 0; i < 60000 / uiPeriodMs; i++)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(uiPeriodMs));
                    await coordinator.RequestCollectionAsync();
                }
                return collections;
            }

            int fast = await RunAsync(100);
            int normal = await RunAsync(1000);

            Assert.InRange(fast, 58, 61);
            Assert.InRange(normal, 58, 61);
        }

        [Fact]
        public async Task 未到期的请求不会创建采集任务()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;
            var statistics = new HardwareSamplingStatistics();

            var coordinator = NewCoordinator(clock, () => 1000, ct =>
            {
                Interlocked.Increment(ref collections);
                return Task.CompletedTask;
            }, statistics);
            coordinator.SetReady(true);

            // 第一次：到期 -> 采集
            Assert.True(await coordinator.RequestCollectionAsync());

            // 之后的 9 次都在同一个周期内 -> 全部未到期
            for (int i = 0; i < 9; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                Assert.False(await coordinator.RequestCollectionAsync());
            }

            Assert.Equal(1, collections);
            Assert.Equal(9, statistics.SkippedRequests);
            Assert.Equal(1, statistics.CollectionRounds);
        }

        [Fact]
        public async Task 边界时刻的多次请求只触发一次采集()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;

            var coordinator = NewCoordinator(clock, () => 1000, ct =>
            {
                Interlocked.Increment(ref collections);
                return Task.CompletedTask;
            });
            coordinator.SetReady(true);

            clock.Advance(TimeSpan.FromMilliseconds(1000));
            await coordinator.RequestCollectionAsync();
            // 同一个时间点再来一次
            await coordinator.RequestCollectionAsync();
            await coordinator.RequestCollectionAsync();

            Assert.Equal(1, collections);
        }

        [Fact]
        public async Task 并发请求共享在途采集_只采集一次()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;
            using var release = new ManualResetEventSlim(false);

            var coordinator = NewCoordinator(clock, () => 1000, async ct =>
            {
                Interlocked.Increment(ref collections);
                release.Wait(TimeSpan.FromSeconds(5));
                await Task.CompletedTask;
            });
            coordinator.SetReady(true);

            var first = coordinator.RequestCollectionAsync();
            var second = coordinator.RequestCollectionAsync();
            var third = coordinator.RequestCollectionAsync();

            // 三个请求拿到的是同一个任务
            Assert.Same(first, second);
            Assert.Same(second, third);

            release.Set();
            await Task.WhenAll(first, second, third);

            Assert.Equal(1, collections);
        }

        [Fact]
        public async Task 超长任务结束后不补跑错过的多轮()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;

            var coordinator = NewCoordinator(clock, () => 1000, ct =>
            {
                Interlocked.Increment(ref collections);
                // 假时钟：这次采集"耗时"5 秒
                clock.Advance(TimeSpan.FromSeconds(5));
                return Task.CompletedTask;
            });
            coordinator.SetReady(true);

            await coordinator.RequestCollectionAsync();
            Assert.Equal(1, collections);

            // 任务结束时已经过了 5 秒，但不允许立刻补跑
            clock.Advance(TimeSpan.FromMilliseconds(100));
            Assert.False(await coordinator.RequestCollectionAsync());
            Assert.Equal(1, collections);

            // 重新安排的期限到达后，只采一次
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(await coordinator.RequestCollectionAsync());
            Assert.Equal(2, collections);
        }

        [Fact]
        public async Task 采集异常后释放忙标志并按正常周期重试()
        {
            var clock = new FakeTimeProvider();
            int attempts = 0;
            var statistics = new HardwareSamplingStatistics();
            var logged = new List<Exception>();

            var coordinator = new HardwareSamplingCoordinator(
                periodProvider: () => 1000,
                sampler: ct =>
                {
                    Interlocked.Increment(ref attempts);
                    throw new InvalidOperationException("模拟硬件采集异常");
                },
                timeProvider: clock,
                // 注入错误出口：测试输出目录必须保持干净，不能落错误日志文件
                errorLogger: (source, ex) => logged.Add(ex),
                statistics: statistics);
            coordinator.SetReady(true);

            bool ok = await coordinator.RequestCollectionAsync();
            Assert.False(ok);
            Assert.Equal(1, attempts);
            Assert.Equal(1, statistics.FailedCollections);
            Assert.Single(logged);

            // 忙标志已释放：到期后可以再采一次（不会形成异常忙循环）
            clock.Advance(TimeSpan.FromSeconds(1));
            await coordinator.RequestCollectionAsync();
            Assert.Equal(2, attempts);

            // 没有到期就不会立刻重试
            await coordinator.RequestCollectionAsync();
            Assert.Equal(2, attempts);
        }

        [Fact]
        public async Task 结束回调抛异常不会让调用方永久挂起()
        {
            var clock = new FakeTimeProvider();
            var statistics = new HardwareSamplingStatistics();
            var logged = new List<Exception>();
            var errorLogged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var coordinator = new HardwareSamplingCoordinator(
                periodProvider: () => 1000,
                sampler: ct => Task.CompletedTask,
                timeProvider: clock,
                onCollectionCompleted: (generation, success) => throw new InvalidOperationException("回调炸了"),
                errorLogger: (source, ex) => { logged.Add(ex); errorLogged.TrySetResult(true); },
                statistics: statistics);
            coordinator.SetReady(true);

            // ★ starter.Task 是调用方唯一的完成信号：
            //   回调抛异常也必须让它完成，否则 UI 的 await 永远不返回，
            //   Tick 的 finally 走不到 -> _busy 永久为 true -> 界面彻底停止刷新。
            Task<bool> task = coordinator.RequestCollectionAsync();
            Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(task, completed);
            Assert.True(task.Result);

            // 回调里的异常被协调器接住并交给错误出口
            await errorLogged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(logged);
        }

        [Fact]
        public async Task 采集落定回调抛异常同样不会让调用方挂起()
        {
            var clock = new FakeTimeProvider();
            var logged = new List<Exception>();
            var errorLogged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var coordinator = new HardwareSamplingCoordinator(
                periodProvider: () => 1000,
                sampler: ct => Task.CompletedTask,
                timeProvider: clock,
                onCollectionSettled: generation => throw new InvalidOperationException("落定回调炸了"),
                errorLogger: (source, ex) => { logged.Add(ex); errorLogged.TrySetResult(true); });
            coordinator.SetReady(true);

            Task<bool> task = coordinator.RequestCollectionAsync();
            Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(task, completed);
            Assert.True(task.Result);

            await errorLogged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(logged);
        }

        [Fact]
        public async Task 释放之后不能再开启新的采集()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;

            var coordinator = NewCoordinator(clock, () => 1000, ct =>
            {
                Interlocked.Increment(ref collections);
                return Task.CompletedTask;
            });
            coordinator.SetReady(true);

            coordinator.Dispose();

            Assert.False(await coordinator.RequestCollectionAsync());
            Assert.False(coordinator.RequestCollection());
            Assert.Equal(0, collections);
        }

        [Fact]
        public async Task 尚未就绪时不会发生真实采集()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;
            var statistics = new HardwareSamplingStatistics();

            var coordinator = NewCoordinator(clock, () => 1000, ct =>
            {
                Interlocked.Increment(ref collections);
                return Task.CompletedTask;
            }, statistics);

            // 没有 SetReady(true) -> 构造后到初始化完成之间的窗口不允许采集
            Assert.False(await coordinator.RequestCollectionAsync());
            Assert.Equal(0, collections);
            Assert.Equal(1, statistics.RejectedRequests);

            coordinator.SetReady(true);
            Assert.True(await coordinator.RequestCollectionAsync());
            Assert.Equal(1, collections);
        }

        [Fact]
        public async Task 墙上时间回拨不影响采集调度()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;

            var coordinator = NewCoordinator(clock, () => 1000, ct =>
            {
                Interlocked.Increment(ref collections);
                return Task.CompletedTask;
            });
            coordinator.SetReady(true);

            for (int i = 0; i < 5; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await coordinator.RequestCollectionAsync();
            }
            Assert.Equal(1, collections);

            // 墙上时间被改回一小时：调度必须完全不受影响
            clock.RewindWallClock(TimeSpan.FromHours(1));
            for (int i = 0; i < 5; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await coordinator.RequestCollectionAsync();
            }
            Assert.Equal(1, collections);
        }

        [Fact]
        public async Task 周期调整后按新的周期判定到期()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;
            int periodMs = 1000;

            var coordinator = NewCoordinator(clock, () => periodMs, ct =>
            {
                Interlocked.Increment(ref collections);
                return Task.CompletedTask;
            });
            coordinator.SetReady(true);

            await coordinator.RequestCollectionAsync();
            Assert.Equal(1, collections);

            // 改成 3 秒周期
            periodMs = 3000;

            clock.Advance(TimeSpan.FromMilliseconds(1000));
            Assert.False(await coordinator.RequestCollectionAsync());
            Assert.Equal(1, collections);

            clock.Advance(TimeSpan.FromMilliseconds(2000));
            Assert.True(await coordinator.RequestCollectionAsync());
            Assert.Equal(2, collections);
        }

        [Fact]
        public async Task 显式失效标记允许立即再采一次()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;

            var coordinator = NewCoordinator(clock, () => 1000, ct =>
            {
                Interlocked.Increment(ref collections);
                return Task.CompletedTask;
            });
            coordinator.SetReady(true);

            await coordinator.RequestCollectionAsync();
            Assert.Equal(1, collections);

            // 未到期：普通请求不能绕过门限
            clock.Advance(TimeSpan.FromMilliseconds(100));
            Assert.False(await coordinator.RequestCollectionAsync());
            Assert.Equal(1, collections);

            // 显式失效（初始化/重载/设备切换）后允许立即采一次
            coordinator.Invalidate();
            Assert.True(await coordinator.RequestCollectionAsync());
            Assert.Equal(2, collections);
        }

        [Fact]
        public async Task 例外采集单独计数_不混进稳态轮数()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;
            var statistics = new HardwareSamplingStatistics();

            var coordinator = NewCoordinator(clock, () => 1000, ct =>
            {
                Interlocked.Increment(ref collections);
                return Task.CompletedTask;
            }, statistics);

            // 就绪（= 初始化完成）会触发一次例外采集
            coordinator.SetReady(true);
            await coordinator.RequestCollectionAsync();

            Assert.Equal(1, statistics.ForcedCollections);
            Assert.Equal(1, statistics.CollectionRounds);

            // 之后是正常到期采集
            clock.Advance(TimeSpan.FromSeconds(1));
            await coordinator.RequestCollectionAsync();
            Assert.Equal(1, statistics.ForcedCollections);
            Assert.Equal(2, statistics.CollectionRounds);

            // 设备选择变更触发的立即采集同样单独计数
            coordinator.Invalidate();
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await coordinator.RequestCollectionAsync();
            Assert.Equal(2, statistics.ForcedCollections);
            Assert.Equal(3, statistics.CollectionRounds);
        }

        [Fact]
        public async Task 例外采集之后回到正常周期()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;

            var coordinator = NewCoordinator(clock, () => 1000, ct =>
            {
                Interlocked.Increment(ref collections);
                return Task.CompletedTask;
            });
            coordinator.SetReady(true);

            await coordinator.RequestCollectionAsync();   // 例外采集（t=0）
            Assert.Equal(1, collections);

            // 例外采集不占网格位置：下一轮仍然是"一个周期之后"
            clock.Advance(TimeSpan.FromMilliseconds(100));
            Assert.False(await coordinator.RequestCollectionAsync());
            Assert.Equal(1, collections);

            clock.Advance(TimeSpan.FromMilliseconds(900));
            Assert.True(await coordinator.RequestCollectionAsync());
            Assert.Equal(2, collections);
        }

        [Fact]
        public async Task 同步入口与异步入口共用同一个门限()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;

            var coordinator = NewCoordinator(clock, () => 1000, ct =>
            {
                Interlocked.Increment(ref collections);
                return Task.CompletedTask;
            });
            coordinator.SetReady(true);

            Assert.True(coordinator.RequestCollection());
            Assert.Equal(1, collections);

            // 同步入口采过之后，异步入口在同一个周期内不能再采
            Assert.False(await coordinator.RequestCollectionAsync());
            Assert.Equal(1, collections);
        }

        [Fact]
        public async Task 采集代次与统计计数正确推进()
        {
            var clock = new FakeTimeProvider();
            var statistics = new HardwareSamplingStatistics();
            var generations = new List<int>();

            var coordinator = new HardwareSamplingCoordinator(
                periodProvider: () => 1000,
                sampler: ct => Task.CompletedTask,
                timeProvider: clock,
                onCollectionStarted: g => generations.Add(g),
                statistics: statistics);
            coordinator.SetReady(true);

            await coordinator.RequestCollectionAsync();
            await coordinator.RequestCollectionAsync();   // 未到期
            clock.Advance(TimeSpan.FromSeconds(1));
            await coordinator.RequestCollectionAsync();

            Assert.Equal(new List<int> { 1, 2 }, generations);
            Assert.Equal(2, statistics.CollectionRounds);
            Assert.Equal(2, statistics.Generation);
            Assert.Equal(1, statistics.SkippedRequests);
            Assert.Equal(2, coordinator.Generation);
        }

        [Fact]
        public async Task 关闭过程中不再接受新请求()
        {
            var clock = new FakeTimeProvider();
            int collections = 0;
            using var release = new ManualResetEventSlim(false);
            using var samplerStarted = new ManualResetEventSlim(false);

            var coordinator = NewCoordinator(clock, () => 1000, ct =>
            {
                Interlocked.Increment(ref collections);
                // ★ 同步点：通知"采样动作真的开始了"。
                //   原用例没有这一步，Dispose() 可能抢在采样动作之前执行；
                //   线程池线程随后命中 ExecuteCollectionAsync 里的"已释放"早退，
                //   采样动作根本没被调用 -> collections == 0 -> 断言红（flaky）。
                samplerStarted.Set();
                release.Wait(TimeSpan.FromSeconds(20));
                return Task.CompletedTask;
            });
            coordinator.SetReady(true);

            var running = coordinator.RequestCollectionAsync();

            // ★ 同步点：等采样动作真的开始，再关闭。
            //   建立 happens-before：采样动作开始 -> Dispose -> 关闭。
            //   不用 Thread.Sleep 碰运气，也不用依赖线程池调度快慢。
            Assert.True(samplerStarted.Wait(TimeSpan.FromSeconds(20)),
                "采样动作未在超时内启动，测试无法确定关闭时序");

            coordinator.Dispose();

            // 关闭之后到达的请求被拒绝
            Assert.False(await coordinator.RequestCollectionAsync());

            release.Set();
            Assert.True(await running);

            Assert.Equal(1, collections);
        }

        // =========================================================
        // 关闭期被跳过的采集 ≠ 失败采集
        //
        //    ExecuteCollectionAsync 在"已释放"时早退，这一轮采集**根本没有开始**。
        //    它既不是成功也不是失败：如果被记成失败，FailedCollections 会凭空 +1，
        //    实机跑完后读到的失败计数就是含噪的数字（"失败恰好 +1"这条护栏也会被打破）。
        // =========================================================

        [Fact]
        public async Task 关闭期被跳过的采集不计入失败采集()
        {
            var clock = new FakeTimeProvider();
            var statistics = new HardwareSamplingStatistics();
            int samplerCalls = 0;
            int disposed = 0;

            // ★ 确定性地制造"请求已占位、但采集还没开始就关闭了"这个窗口。
            //   periodProvider 是在 TryBeginCollection 内部（持 _gate 锁）被调用的，
            //   而"是否已释放 / 是否就绪"的判定发生在它**之前**；
            //   因此在 periodProvider 里调用 Dispose()（同线程重入锁，合法），
            //   就能让"关闭"确定性地落在"请求已占位之后、采集动作开始之前"。
            //   这样不依赖线程池调度时序：不需要 Sleep，也不需要靠运气。
            HardwareSamplingCoordinator coordinator = null;
            coordinator = new HardwareSamplingCoordinator(
                periodProvider: () =>
                {
                    if (Interlocked.Exchange(ref disposed, 1) == 0)
                    {
                        coordinator.Dispose();
                    }
                    return 1000;
                },
                sampler: ct =>
                {
                    Interlocked.Increment(ref samplerCalls);
                    return Task.CompletedTask;
                },
                timeProvider: clock,
                // 注入错误出口：测试输出目录必须保持干净，不能落错误日志文件
                errorLogger: (source, ex) => { },
                statistics: statistics);
            coordinator.SetReady(true);

            // 关闭期请求仍然返回 false（既有语义不回归）
            Assert.False(await coordinator.RequestCollectionAsync());

            // 采样动作根本没有被调用：这不是一次"采集"，更不是一次"失败"
            Assert.Equal(0, samplerCalls);
            Assert.Equal(0, statistics.CollectionRounds);
            Assert.Equal(0, statistics.FailedCollections);
        }

        [Fact]
        public async Task 真正的采集异常恰好让失败计数加一()
        {
            var clock = new FakeTimeProvider();
            var statistics = new HardwareSamplingStatistics();
            int attempts = 0;

            var coordinator = new HardwareSamplingCoordinator(
                periodProvider: () => 1000,
                sampler: ct =>
                {
                    Interlocked.Increment(ref attempts);
                    throw new InvalidOperationException("模拟硬件采集异常");
                },
                timeProvider: clock,
                // 注入错误出口：测试输出目录必须保持干净，不能落错误日志文件
                errorLogger: (source, ex) => { },
                statistics: statistics);
            coordinator.SetReady(true);

            Assert.Equal(0, statistics.FailedCollections);

            // 第一次失败：恰好 +1（不得 +2，也不得 +0）
            Assert.False(await coordinator.RequestCollectionAsync());
            Assert.Equal(1, statistics.FailedCollections);
            Assert.Equal(1, attempts);

            // 再失败一次：仍然恰好 +1（累计 +2）
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(await coordinator.RequestCollectionAsync());
            Assert.Equal(2, statistics.FailedCollections);
            Assert.Equal(2, attempts);
        }

        [Fact]
        public async Task 关闭期跳过与真实失败互不干扰()
        {
            var clock = new FakeTimeProvider();
            var statistics = new HardwareSamplingStatistics();
            int disposeOnNextPeriod = 0;

            // 与上一条同样的确定性手法：恰好在"判定周期"的那一刻关闭，
            // 于是这一轮采集被跳过（采样动作根本不会执行）。
            HardwareSamplingCoordinator coordinator = null;
            coordinator = new HardwareSamplingCoordinator(
                periodProvider: () =>
                {
                    if (Interlocked.Exchange(ref disposeOnNextPeriod, 0) == 1)
                    {
                        coordinator.Dispose();
                    }
                    return 1000;
                },
                sampler: ct => throw new InvalidOperationException("模拟硬件采集异常"),
                timeProvider: clock,
                // 注入错误出口：测试输出目录必须保持干净，不能落错误日志文件
                errorLogger: (source, ex) => { },
                statistics: statistics);
            coordinator.SetReady(true);

            // 1) 真正的失败 -> 失败计数恰好 +1
            Assert.False(await coordinator.RequestCollectionAsync());
            Assert.Equal(1, statistics.FailedCollections);

            // 2) 到期后再次请求：这一轮在开始前就被关闭跳过
            clock.Advance(TimeSpan.FromSeconds(1));
            Interlocked.Exchange(ref disposeOnNextPeriod, 1);
            Assert.False(await coordinator.RequestCollectionAsync());

            // 3) 跳过的采集不是失败：失败计数保持 1（既没有 +1，也没有被清零）
            Assert.Equal(1, statistics.FailedCollections);
        }

        // =========================================================
        // 界面刷新频率与硬件采集频率是两件事
        // =========================================================

        [Fact]
        public void 界面刷新间隔仍然跟随RefreshMs_没有被改成每秒一次()
        {
            // UI 100 ms -> 界面仍然每 100 ms 刷新一次（取值/平滑/绘制都保持原频率）
            Assert.Equal(100, LiteMonitor.UIController.ComputeUiRefreshIntervalMs(100));
            Assert.Equal(1000, LiteMonitor.UIController.ComputeUiRefreshIntervalMs(1000));
            Assert.Equal(3000, LiteMonitor.UIController.ComputeUiRefreshIntervalMs(3000));
            // 非法值回退到 80ms 下限
            Assert.Equal(80, LiteMonitor.UIController.ComputeUiRefreshIntervalMs(0));
        }

        [Fact]
        public void 普通硬件的最小采集周期是一秒()
        {
            Assert.Equal(1000, HardwareSamplingCoordinator.MinimumPeriodMs);
        }
    }
}
