using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// FPS PID 状态清理与查询重试的单元测试。
    ///
    /// 全部使用注入的进程查询器、假时钟与假启动器：
    /// - 不依赖真实进程列表
    /// - 不启动真实 PresentMon / ETW
    /// - 不发起任何外网请求
    /// </summary>
    public class FpsStateTests
    {
        // ================================================================
        // 测试替身
        // ================================================================

        /// <summary>假时钟（毫秒单调）</summary>
        private sealed class FakeClock
        {
            private long _now;
            public FakeClock(long start = 1_000) { _now = start; }
            public long Now => Interlocked.Read(ref _now);
            public void Advance(long ms) => Interlocked.Add(ref _now, ms);
        }

        /// <summary>假进程查询器：按 PID 返回预设结果，并统计查询次数</summary>
        private sealed class FakeProcessQuery : IFpsProcessQuery
        {
            private readonly ConcurrentDictionary<int, FpsProcessQueryResult> _results = new();
            private int _calls;

            /// <summary>可选的动态行为（优先于预设结果）</summary>
            public Func<int, FpsProcessQueryResult>? Handler { get; set; }

            /// <summary>
            /// 模拟进程查询耗时（毫秒）。
            /// 真实环境里 Process.GetProcessById 可能耗时数毫秒，
            /// 这里用它拉开"统计线程取走帧"与"封存后收走残留帧"之间的窗口，
            /// 让并发测试真正走到残留帧转移路径。
            /// </summary>
            public int DelayMs { get; set; }

            public int Calls => Volatile.Read(ref _calls);

            public void Set(int pid, FpsProcessQueryResult result) => _results[pid] = result;

            public FpsProcessQueryResult Query(int pid)
            {
                Interlocked.Increment(ref _calls);
                if (DelayMs > 0) Thread.Sleep(DelayMs);
                if (Handler != null) return Handler(pid);
                return _results.TryGetValue(pid, out FpsProcessQueryResult r)
                    ? r
                    : FpsProcessQueryResult.CreateExited();
            }
        }

        /// <summary>假外部服务启动器：只计数，不启动任何真实进程</summary>
        private sealed class FakeLauncher : IFpsServiceLauncher, IFpsServiceExitNotifier
        {
            private readonly ManualResetEventSlim _startEntered = new(false);
            private readonly ManualResetEventSlim _startRelease = new(false);
            private int _startCount;
            private int _stopCount;
            private int _ensureCount;
            private CancellationTokenSource? _cts;

            /// <summary>外部服务进程退出时触发</summary>
            public event Action? Exited;

            /// <summary>为 true 时 Start() 会阻塞在 <see cref="ReleaseStart"/> 上（用于确定性并发测试）</summary>
            public bool BlockStart { get; set; }

            /// <summary>进入 Start() 时被置位</summary>
            public ManualResetEventSlim StartEntered => _startEntered;

            public int StartCount => Volatile.Read(ref _startCount);
            public int StopCount => Volatile.Read(ref _stopCount);
            public int EnsureCount => Volatile.Read(ref _ensureCount);
            public bool IsRunning { get; private set; }

            public bool EnsureAvailable()
            {
                Interlocked.Increment(ref _ensureCount);
                return true;
            }

            public bool Start(Action<string> onLine, out string? error)
            {
                Interlocked.Increment(ref _startCount);
                error = null;
                IsRunning = true;
                _startEntered.Set();

                if (BlockStart) _startRelease.Wait(5000);

                // 模拟数据心跳，避免健康检查把服务判成僵死而重启
                var cts = new CancellationTokenSource();
                _cts = cts;
                Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        try { await Task.Delay(50, cts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                        onLine("game.exe,4242,1,2,3");
                    }
                });
                return true;
            }

            public void Stop()
            {
                Interlocked.Increment(ref _stopCount);
                IsRunning = false;
                try { _cts?.Cancel(); } catch { }
                _cts = null;
            }

            /// <summary>放行被 <see cref="BlockStart"/> 阻塞的 Start()</summary>
            public void ReleaseStart() => _startRelease.Set();

            /// <summary>模拟外部服务进程退出</summary>
            public void RaiseExited()
            {
                IsRunning = false;
                try { _cts?.Cancel(); } catch { }
                _cts = null;
                Exited?.Invoke();
            }
        }

        private static FpsProcessStateStore NewStore(FakeProcessQuery query, FakeClock clock, FpsStateOptions? options = null)
        {
            options ??= new FpsStateOptions();
            options.Clock = () => clock.Now;
            return new FpsProcessStateStore(query, options);
        }

        private static void WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return;
                Thread.Sleep(10);
            }
        }

        // ================================================================
        // 1) 已退出 PID 不残留
        // ================================================================

        [Fact]
        public void 已退出的PID不残留()
        {
            var clock = new FakeClock();
            var query = new FakeProcessQuery();
            var store = NewStore(query, clock);

            store.RecordFrame(4242);
            Assert.Equal(1, store.ProcessCount);

            // 第一轮统计：查询发现进程已退出 → 整体清除
            store.AdvanceCycle(0.5);
            Assert.Equal(0, store.ProcessCount);
            Assert.Empty(store.GetDiagnostics());
        }

        [Fact]
        public void 已退出的PID不会每轮重复查询()
        {
            var clock = new FakeClock();
            var query = new FakeProcessQuery();
            var store = NewStore(query, clock);

            store.RecordFrame(4242);
            store.AdvanceCycle(0.5);
            Assert.Equal(0, store.ProcessCount);
            Assert.Equal(1, query.Calls);

            for (int i = 0; i < 12; i++)
            {
                clock.Advance(500);
                store.AdvanceCycle(0.5);
            }
            Assert.Equal(1, query.Calls);
        }

        // ================================================================
        // 2) 100 条失效记录：清理后不再每轮产生查询
        // ================================================================

        [Fact]
        public void 一百条失效记录清理后不再每轮产生查询()
        {
            var clock = new FakeClock();
            var query = new FakeProcessQuery();
            var store = NewStore(query, clock);

            for (int pid = 1000; pid < 1100; pid++) store.RecordFrame(pid);
            Assert.Equal(100, store.ProcessCount);

            // 第一轮：100 次查询后全部判定为已退出并清理
            store.AdvanceCycle(0.5);
            Assert.Equal(0, store.ProcessCount);
            Assert.Equal(100, query.Calls);

            // 后续 12 轮：不再产生任何进程查询（修复前这里是 12 × 100 = 1200 次）
            for (int i = 0; i < 12; i++)
            {
                clock.Advance(500);
                store.AdvanceCycle(0.5);
            }
            Assert.Equal(100, query.Calls);
            Assert.Equal(0, store.ProcessCount);
        }

        // ================================================================
        // 3) Unknown 查询按期限重试（负缓存）
        // ================================================================

        [Fact]
        public void Unknown查询按期限重试而不是每轮都查()
        {
            var clock = new FakeClock();
            var query = new FakeProcessQuery { Handler = _ => FpsProcessQueryResult.CreateUnknown() };
            var store = NewStore(query, clock);

            store.RecordFrame(777);
            store.AdvanceCycle(0.5);
            Assert.Equal(1, query.Calls);

            // 负缓存期内（< 5 秒）不重复查询
            clock.Advance(1000);
            store.AdvanceCycle(0.5);
            Assert.Equal(1, query.Calls);

            clock.Advance(2000);
            store.AdvanceCycle(0.5);
            Assert.Equal(1, query.Calls);

            // 超过 5 秒才允许再查一次
            clock.Advance(5000);
            store.AdvanceCycle(0.5);
            Assert.Equal(2, query.Calls);
        }

        [Fact]
        public void 一百条Unknown记录在规模下同样遵守负缓存()
        {
            var clock = new FakeClock();
            var query = new FakeProcessQuery { Handler = _ => FpsProcessQueryResult.CreateUnknown() };
            var store = NewStore(query, clock);

            for (int pid = 1000; pid < 1100; pid++) store.RecordFrame(pid);

            store.AdvanceCycle(0.5);
            Assert.Equal(100, query.Calls);

            // 负缓存期内：100 个 PID 一次都不再查（旧实现是每轮 100 次）
            clock.Advance(1000);
            store.AdvanceCycle(0.5);
            Assert.Equal(100, query.Calls);

            clock.Advance(3000);
            store.AdvanceCycle(0.5);
            Assert.Equal(100, query.Calls);

            // 超过 5 秒：每个 PID 各重试一次
            clock.Advance(5000);
            store.AdvanceCycle(0.5);
            Assert.Equal(200, query.Calls);
        }

        [Fact]
        public void 查询未知的进程不参与FPS统计()
        {
            var clock = new FakeClock();
            var query = new FakeProcessQuery { Handler = _ => FpsProcessQueryResult.CreateUnknown() };
            var store = NewStore(query, clock);

            for (int i = 0; i < 10; i++)
            {
                store.RecordFrame(777);
                clock.Advance(500);
                store.AdvanceCycle(0.5);
            }

            Assert.False(store.TryGetFps(777, out _));
            Assert.False(store.HasActiveFps);
        }

        // ================================================================
        // 4) 短暂无帧不误删正常活动记录
        // ================================================================

        [Fact]
        public void 短暂无帧不误删正常活动记录也不抹掉平滑历史()
        {
            var clock = new FakeClock();
            var query = new FakeProcessQuery();
            query.Set(555, FpsProcessQueryResult.CreateFound("game.exe", 1));
            var store = NewStore(query, clock);

            // 先建立统计
            for (int i = 0; i < 8; i++)
            {
                store.RecordFrame(555);
                clock.Advance(500);
                store.AdvanceCycle(0.5);
            }
            Assert.True(store.TryGetFps(555, out float fps));
            Assert.True(fps > 0f, "有连续帧时应当有 FPS");
            Assert.True(store.GetDiagnostics().Single().HistorySamples > 0);

            // 短暂停顿 5 秒（远小于 30 秒闲置期限）：记录与历史都要保留
            for (int i = 0; i < 10; i++)
            {
                clock.Advance(500);
                store.AdvanceCycle(0.5);
            }

            Assert.Equal(1, store.ProcessCount);
            Assert.True(store.GetDiagnostics().Single().HasHistory, "短暂停顿不应抹掉平滑历史");
            Assert.True(store.GetDiagnostics().Single().HistorySamples > 0, "滑动窗口不应被清空");
        }

        // ================================================================
        // 5) 长期闲置有界清理
        // ================================================================

        [Fact]
        public void 长期闲置的记录按期限有界清理()
        {
            var clock = new FakeClock();
            var query = new FakeProcessQuery();
            query.Set(888, FpsProcessQueryResult.CreateFound("game.exe", 1));
            var store = NewStore(query, clock);

            store.RecordFrame(888);
            clock.Advance(500);
            store.AdvanceCycle(0.5);
            Assert.Equal(1, store.ProcessCount);

            // 29 秒：还没到 30 秒期限
            clock.Advance(29_000);
            store.AdvanceCycle(0.5);
            Assert.Equal(1, store.ProcessCount);

            // 超过 30 秒：清理
            clock.Advance(2_000);
            store.AdvanceCycle(0.5);
            Assert.Equal(0, store.ProcessCount);
        }

        [Fact]
        public void 查询持续失败的记录同样按闲置期限回收()
        {
            var clock = new FakeClock();
            // 查询一直失败（权限不足 / 瞬时失败），且不再产生新帧
            var query = new FakeProcessQuery { Handler = _ => FpsProcessQueryResult.CreateUnknown() };
            var store = NewStore(query, clock);

            for (int pid = 2000; pid < 2100; pid++) store.RecordFrame(pid);
            Assert.Equal(100, store.ProcessCount);

            // 查询失败不影响统计，但记录仍然存在
            store.AdvanceCycle(0.5);
            Assert.Equal(100, store.ProcessCount);

            // 超过闲置期限后必须被回收（否则 _entries 单调增长）
            clock.Advance(31_000);
            store.AdvanceCycle(0.5);
            Assert.Equal(0, store.ProcessCount);
        }

        [Fact]
        public void 排除进程的记录同样按闲置期限回收()
        {
            var clock = new FakeClock();
            var query = new FakeProcessQuery { Handler = _ => FpsProcessQueryResult.CreateFound("PresentMon", 1) };
            var options = new FpsStateOptions
            {
                ExcludedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PresentMon" }
            };
            var store = NewStore(query, clock, options);

            store.RecordFrame(3001);
            store.AdvanceCycle(0.5);
            Assert.Equal(1, store.ProcessCount);

            clock.Advance(31_000);
            store.AdvanceCycle(0.5);
            Assert.Equal(0, store.ProcessCount);
        }

        // ================================================================
        // 6) PID 重用：旧名称与历史不能套到新进程
        // ================================================================

        [Fact]
        public void PID重用时旧名称与历史不套到新进程()
        {
            var clock = new FakeClock();
            var query = new FakeProcessQuery();
            query.Set(600, FpsProcessQueryResult.CreateFound("oldgame.exe", 111));
            var store = NewStore(query, clock);

            for (int i = 0; i < 8; i++)
            {
                store.RecordFrame(600);
                clock.Advance(500);
                store.AdvanceCycle(0.5);
            }

            var before = store.GetDiagnostics().Single();
            Assert.Equal("oldgame.exe", before.Name);
            Assert.Equal(0, before.Generation);
            Assert.True(before.HasHistory);
            Assert.True(before.Fps > 0f);

            // 同一 PID 被新进程复用（身份标识与名称都变了）
            query.Set(600, FpsProcessQueryResult.CreateFound("newgame.exe", 222));
            clock.Advance(6000); // 超过刷新期限 → 会重新查询
            store.AdvanceCycle(0.5);

            var after = store.GetDiagnostics().Single();
            Assert.Equal("newgame.exe", after.Name);
            Assert.Equal(1, after.Generation);
            Assert.Equal(0f, after.Fps);                 // 旧进程的 FPS 不能继续上报
            Assert.Equal(1, after.HistorySamples);        // 历史被重置（而不是沿用旧的 4 个采样）
        }

        // ================================================================
        // 7) PID 重新出现能恢复统计
        // ================================================================

        [Fact]
        public void PID退出后重新出现能恢复统计()
        {
            var clock = new FakeClock();
            var query = new FakeProcessQuery();
            query.Set(700, FpsProcessQueryResult.CreateFound("game.exe", 1));
            var store = NewStore(query, clock);

            for (int i = 0; i < 8; i++)
            {
                store.RecordFrame(700);
                clock.Advance(500);
                store.AdvanceCycle(0.5);
            }
            Assert.True(store.TryGetFps(700, out _));

            // 进程退出 → 状态被整体清除
            query.Set(700, FpsProcessQueryResult.CreateExited());
            clock.Advance(6000);
            store.AdvanceCycle(0.5);
            Assert.Equal(0, store.ProcessCount);

            // 同一 PID 再次出现 → 统计可以重新建立
            query.Set(700, FpsProcessQueryResult.CreateFound("game.exe", 2));
            for (int i = 0; i < 10; i++)
            {
                store.RecordFrame(700);
                clock.Advance(500);
                store.AdvanceCycle(0.5);
            }

            Assert.True(store.TryGetFps(700, out float fps));
            Assert.True(fps > 0f, "PID 重新出现后应能恢复统计");
        }

        // ================================================================
        // 8) 解析与统计并发：清理/封存与新帧真正交错，且不丢帧
        // ================================================================

        [Fact]
        public void 解析与统计并发时清理封存与新帧交错不会永久丢帧()
        {
            var clock = new FakeClock();
            // 4243 的进程已退出（每轮都会被 retire → 封存 + 残留帧转移），
            // 4242 / 4244 正常存活；4244 早早停止产生帧 → 30 秒后走闲置清理路径。
            var query = new FakeProcessQuery
            {
                // 模拟进程查询耗时：拉开"取帧"与"封存收残留帧"之间的窗口
                DelayMs = 1,
                Handler = pid => pid == 4243
                    ? FpsProcessQueryResult.CreateExited()
                    : FpsProcessQueryResult.CreateFound("game.exe", 1)
            };
            var store = NewStore(query, clock);

            const int liveFrames = 20_000;
            const int exitedFrames = 20_000;
            const int earlyFrames = 10_000;
            int cleaned = 0;
            int writersDone = 0;
            var errors = new ConcurrentBag<Exception>();
            var start = new ManualResetEventSlim(false);

            var writers = new List<Task>
            {
                Task.Run(() =>
                {
                    start.Wait();
                    try
                    {
                        for (int i = 0; i < liveFrames; i++)
                        {
                            store.RecordLine("game.exe,4242,1,2,3");
                            Thread.Yield(); // 让统计线程有机会在"取帧"与"封存"之间插入
                        }
                    }
                    catch (Exception ex) { errors.Add(ex); }
                }),
                Task.Run(() =>
                {
                    start.Wait();
                    try
                    {
                        for (int i = 0; i < exitedFrames; i++)
                        {
                            store.RecordLine("game.exe,4243,1,2,3");
                            Thread.Yield();
                        }
                    }
                    catch (Exception ex) { errors.Add(ex); }
                }),
                Task.Run(() =>
                {
                    start.Wait();
                    try
                    {
                        for (int i = 0; i < earlyFrames; i++)
                        {
                            store.RecordLine("game.exe,4244,1,2,3");
                            Thread.Yield();
                        }
                    }
                    catch (Exception ex) { errors.Add(ex); }
                })
            };

            // 统计线程：时钟每轮 +100ms，一直跑到写线程全部结束，
            // 保证 retire/seal 与 RecordLine 真正交错（而不是写线程先跑完）。
            var readers = new List<Task>
            {
                Task.Run(() =>
                {
                    start.Wait();
                    try
                    {
                        for (int i = 0; i < 20000 && Volatile.Read(ref writersDone) < 3; i++)
                        {
                            clock.Advance(100);
                            cleaned += store.AdvanceCycle(0.1);
                        }
                    }
                    catch (Exception ex) { errors.Add(ex); }
                }),
                Task.Run(() =>
                {
                    start.Wait();
                    try
                    {
                        for (int i = 0; i < 20000 && Volatile.Read(ref writersDone) < 3; i++)
                        {
                            clock.Advance(100);
                            cleaned += store.AdvanceCycle(0.1);
                        }
                    }
                    catch (Exception ex) { errors.Add(ex); }
                })
            };

            start.Set();
            Task.WaitAll(writers.ToArray());
            Volatile.Write(ref writersDone, 3);
            Task.WaitAll(readers.ToArray());

            Assert.Empty(errors);
            Assert.True(cleaned > 0, "本测试必须真的触发过 retire/封存路径");

            // 排空残留帧 + 让停止产生帧的 PID 跨过 30 秒闲置期限：
            // 时钟每次 +1 秒，40 轮足以让 4244 走闲置清理路径。
            for (int i = 0; i < 40; i++)
            {
                clock.Advance(1000);
                cleaned += store.AdvanceCycle(1.0);
            }

            // ★ 关键断言：封存后确实发生过"残留帧转移"，说明封存+转移协议真的被走到了
            Assert.True(store.RetiredTransferredFrames > 0, "本测试必须真的触发过'封存后转移残留帧'路径");

            // 全部写入的帧都必须被统计消费，一帧都不能少
            Assert.Equal(liveFrames + exitedFrames + earlyFrames, store.TotalFramesObserved);
        }

        // ================================================================
        // 9)~14) FpsCounter 级别：启动防重入、需求接线缝、可恢复停止
        // ================================================================

        [Fact]
        public void 多消费者并发请求不会重复启动外部服务()
        {
            var launcher = new FakeLauncher { BlockStart = true };
            using var counter = new FpsCounter(null, launcher, new FakeProcessQuery(), () => true);
            counter.FpsDemandProvider = () => true;

            const int callers = 16;
            var ready = new CountdownEvent(callers);
            var release = new ManualResetEventSlim(false);

            var tasks = Enumerable.Range(0, callers).Select(_ => Task.Run(() =>
            {
                ready.Signal();
                release.Wait();
                counter.GetFps();
            })).ToArray();

            // 16 个调用者全部就位后同时放行 → 制造真实重叠
            Assert.True(ready.Wait(5000));
            release.Set();

            // 第一次启动进入假启动器后被阻塞：此时其余启动任务都撞上启动闸门
            Assert.True(launcher.StartEntered.Wait(5000));
            Thread.Sleep(200);
            launcher.ReleaseStart();

            WaitUntil(() => launcher.StartCount >= 1, 3000);
            Thread.Sleep(300);

            Assert.Equal(1, launcher.StartCount);

            // 服务已在运行：后续再请求也不会重复启动
            for (int i = 0; i < 10; i++) counter.GetFps();
            Thread.Sleep(200);
            Assert.Equal(1, launcher.StartCount);
        }

        [Fact]
        public void 没有需求时不启动也不下载()
        {
            var launcher = new FakeLauncher();
            using var counter = new FpsCounter(null, launcher, new FakeProcessQuery(), () => true);
            counter.FpsDemandProvider = () => false;

            for (int i = 0; i < 5; i++) counter.GetFps();
            Thread.Sleep(200);

            Assert.Equal(0, launcher.EnsureCount);
            Assert.Equal(0, launcher.StartCount);
            Assert.False(counter.IsServiceRunning);
            Assert.False(counter.IsFpsDemandActive);
        }

        [Fact]
        public void 非管理员时不启动也不下载外部服务()
        {
            var launcher = new FakeLauncher();
            // 模拟非提权运行（PresentMon 需要管理员权限）
            using var counter = new FpsCounter(null, launcher, new FakeProcessQuery(), () => false);
            counter.FpsDemandProvider = () => true;

            for (int i = 0; i < 5; i++) counter.GetFps();
            Thread.Sleep(300);

            // 既不检查可用性（不下载），也不拉起进程
            Assert.Equal(0, launcher.EnsureCount);
            Assert.Equal(0, launcher.StartCount);
            Assert.False(counter.IsServiceRunning);
        }

        [Fact]
        public void 需求为真时健康检查会补启动未运行的服务()
        {
            var launcher = new FakeLauncher();
            using var counter = new FpsCounter(null, launcher, new FakeProcessQuery(), () => true);
            counter.FpsDemandProvider = () => true;

            // 没有调用 GetFps()，服务尚未启动
            Assert.Equal(0, launcher.StartCount);

            // 健康检查（3 秒一轮）发现需求为真 → 补启动，保证"需求为真 ⇒ 服务在运行"
            WaitUntil(() => launcher.StartCount >= 1, 8000);
            Assert.Equal(1, launcher.StartCount);
        }

        [Fact]
        public void 关闭全部需求后可恢复停止并能再次启动()
        {
            var launcher = new FakeLauncher();
            var counter = new FpsCounter(null, launcher, new FakeProcessQuery(), () => true);
            bool demand = true;
            counter.FpsDemandProvider = () => demand;

            // 需求开启 → 启动
            counter.GetFps();
            WaitUntil(() => launcher.StartCount == 1, 3000);
            Assert.Equal(1, launcher.StartCount);
            Assert.True(counter.IsServiceRunning);

            // 需求关闭 → 可恢复停止（不是最终释放）
            demand = false;
            counter.StopService();
            Assert.Equal(1, launcher.StopCount);
            Assert.False(counter.IsServiceRunning);

            // 需求重新开启 → 仍然能启动
            demand = true;
            counter.GetFps();
            WaitUntil(() => launcher.StartCount == 2, 3000);
            Assert.Equal(2, launcher.StartCount);
            Assert.True(counter.IsServiceRunning);

            // 最终释放之后不能再启动
            counter.Dispose();
            counter.GetFps();
            Thread.Sleep(200);
            Assert.Equal(2, launcher.StartCount);
        }

        // ================================================================
        // 15)~16) 外部进程退出与最终释放
        // ================================================================

        [Fact]
        public void 外部服务进程退出后运行状态立刻回落()
        {
            var launcher = new FakeLauncher();
            using var counter = new FpsCounter(null, launcher, new FakeProcessQuery(), () => true);
            counter.FpsDemandProvider = () => true;

            counter.GetFps();
            WaitUntil(() => launcher.StartCount == 1, 3000);
            Assert.True(counter.IsServiceRunning);

            // 外部进程退出（PresentMon 因无权限/异常退出）
            launcher.RaiseExited();

            // 运行状态必须立刻回落，Web 侧才能区分"未启动"与"启动但为 0"
            Assert.False(counter.IsServiceRunning);
        }

        [Fact]
        public void Dispose后不会产生未观测的后台任务异常()
        {
            var observed = new ConcurrentBag<Exception>();
            void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
            {
                if (e.Exception.InnerException is ObjectDisposedException) observed.Add(e.Exception);
            }

            TaskScheduler.UnobservedTaskException += Handler;
            try
            {
                // 反复创建/释放，逼出"后台循环读取已释放的 CancellationTokenSource"的竞态
                for (int i = 0; i < 30; i++)
                {
                    var counter = new FpsCounter(null, new FakeLauncher(), new FakeProcessQuery(), () => true);
                    Thread.Sleep(7);
                    counter.Dispose();
                }

                // 等待后台循环（500ms 统计循环 / 3s 健康检查）走到下一次读取 Token 的位置
                Thread.Sleep(3500);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(200);
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= Handler;
            }

            Assert.Empty(observed);
        }
    }
}
