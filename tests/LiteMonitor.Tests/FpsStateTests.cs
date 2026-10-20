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
        /// <summary>
        /// 等待"外部状态成立"的预算（毫秒）。
        /// 断言的语义是"该状态最终发生"（不靠 sleep 制造时序），
        /// 预算只用于兜住高负载下 .NET 线程池调度被推迟的情况，
        /// 因此给足即可——放宽预算不会让断言变成恒真。
        /// </summary>
        private const int StateWaitMs = 20000;

        /// <summary>
        /// "服务应当及时进入运行状态"的短预算（毫秒，真实单调时钟）。
        ///
        /// 与 <see cref="StateWaitMs"/> 的分工（两条断言并存，不靠放宽预算糊过去）：
        /// - <see cref="StateWaitMs"/> 只保证"该状态最终发生"，
        ///   用来兜住高负载下 .NET 线程池调度被推迟的情况，不约束快慢；
        /// - 本预算是一条**及时性**护栏：防止"启动耗时 5–20s"的性能回归
        ///   被上面那条宽松预算掩盖。
        /// 取值依据：健康检查每 3 秒一轮，服务最迟应在下一轮检查内进入运行状态，
        /// 因此给到 5s（≈ 3s 检查周期 + 2s 余量）。
        /// </summary>
        private const int StartLatencyBudgetMs = 5000;

        /// <summary>
        /// "某个状态**不得**发生"的等待窗（毫秒）。
        /// 用于"退避窗口内不会再被拉起"这类否定性结论：
        /// 必须给被测系统一个真实时间窗，否则断言可能跑在后台启动任务之前而假通过。
        /// </summary>
        private const int MustNotHappenWaitMs = 1500;

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
            private readonly SemaphoreSlim _startSignals = new(0);
            private int _startCount;
            private int _stopCount;
            private int _ensureCount;
            private int _isRunning;
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

            // 用 Volatile 读写：跨线程可见，避免"启动了但断言读到的还是旧值"这种假阴性
            public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

            public bool EnsureAvailable()
            {
                Interlocked.Increment(ref _ensureCount);
                return true;
            }

            public bool Start(Action<string> onLine, out string? error)
            {
                Interlocked.Increment(ref _startCount);
                error = null;
                Volatile.Write(ref _isRunning, 1);
                _startEntered.Set();
                _startSignals.Release();

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
                Volatile.Write(ref _isRunning, 0);
                try { _cts?.Cancel(); } catch { }
                _cts = null;
            }

            /// <summary>放行被 <see cref="BlockStart"/> 阻塞的 Start()</summary>
            public void ReleaseStart() => _startRelease.Set();

            /// <summary>
            /// 等待"第 nth 次启动"发生（确定性同步点：等真实状态，不靠 sleep 碰运气）。
            /// </summary>
            public bool WaitForStart(int nth, int timeoutMs)
            {
                var sw = Stopwatch.StartNew();
                while (Volatile.Read(ref _startCount) < nth)
                {
                    int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
                    if (remaining <= 0) return Volatile.Read(ref _startCount) >= nth;
                    _startSignals.Wait(Math.Min(remaining, 50));
                }
                return true;
            }

            /// <summary>模拟外部服务进程退出</summary>
            public void RaiseExited()
            {
                Volatile.Write(ref _isRunning, 0);
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

        /// <summary>
        /// 等待条件成立（返回是否在超时前成立）。
        /// 只用于"等一个真实状态"，不用于制造线程交错。
        /// </summary>
        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return condition();
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
            int readCycles = 0;                   // 统计线程已推进的轮数（写线程据此强制交错）
            var errors = new ConcurrentBag<Exception>();
            var start = new ManualResetEventSlim(false);
            // 每个写线程写入第一帧后置位：统计线程等它，保证"统计时确实已有帧"
            var writersStarted = new CountdownEvent(3);

            Task WriteTask(int index, int pid, int count)
            {
                const int chunks = 10;
                return Task.Run(() =>
                {
                    start.Wait();
                    bool signaled = false;
                    int perChunk = count / chunks;
                    try
                    {
                        for (int chunk = 0; chunk < chunks; chunk++)
                        {
                            int from = chunk * perChunk;
                            int to = chunk == chunks - 1 ? count : from + perChunk;
                            for (int i = from; i < to; i++)
                            {
                                store.RecordLine($"game.exe,{pid},1,2,3");
                                if (!signaled) { signaled = true; writersStarted.Signal(); }
                                Thread.Yield(); // 让统计线程有机会在"取帧"与"封存"之间插入
                            }

                            // ★ 等统计线程推进到下一轮再写下一批：
                            //   用同步点强制"写入"与"统计/清理"在同一时间段内交错，
                            //   而不是依赖线程调度碰运气（写线程先跑完的话，本用例就退化成了单线程）。
                            var sw = Stopwatch.StartNew();
                            while (Volatile.Read(ref readCycles) < chunk + 1 && sw.ElapsedMilliseconds < 5000)
                            {
                                Thread.Sleep(1);
                            }
                        }
                    }
                    catch (Exception ex) { errors.Add(ex); }
                    finally
                    {
                        // 兜底置位：写线程异常退出时不能让统计线程一直等下去
                        if (!signaled) { signaled = true; writersStarted.Signal(); }
                    }
                });
            }

            var writers = new List<Task>
            {
                WriteTask(0, 4242, liveFrames),
                WriteTask(1, 4243, exitedFrames),
                WriteTask(2, 4244, earlyFrames)
            };

            // 统计线程：时钟每轮 +100ms，一直跑到写线程全部结束，
            // 保证 retire/seal 与 RecordLine 真正交错（而不是写线程先跑完）。
            Task ReadTask()
            {
                return Task.Run(() =>
                {
                    start.Wait();
                    try
                    {
                        // ★ 等三个写线程都写入第一帧后再开始统计：
                        //   否则空状态下 AdvanceCycle 极快，统计线程可能在任何帧写入之前
                        //   就把 20000 轮跑完，"必须触发过 retire 路径"就成了时序敏感断言。
                        writersStarted.Wait();
                        int cycles = 0;
                        do
                        {
                            clock.Advance(100);
                            // ★ 必须原子累加：两个统计线程同时 "+=" 会丢失更新，
                            //   出现"明明清理过却断言 cleaned == 0"的假阴性。
                            Interlocked.Add(ref cleaned, store.AdvanceCycle(0.1));
                            Interlocked.Increment(ref readCycles);
                            cycles++;
                        } while (cycles < 20000 && Volatile.Read(ref writersDone) < 3);
                    }
                    catch (Exception ex) { errors.Add(ex); }
                });
            }

            var readers = new List<Task> { ReadTask(), ReadTask() };

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
                Interlocked.Add(ref cleaned, store.AdvanceCycle(1.0));
            }

            // ★ 关键断言：帧总数守恒（不多不少）。
            //   "封存后残留帧转移"这条路径由确定性用例单独覆盖
            //   （见 封存后残留帧确定性转移且转移帧不丢不重），
            //   本并发用例不再断言"是否恰好发生过一次转移"——
            //   那取决于统计线程与写线程是否真的交错，属于时序敏感断言，会让用例变 flaky。
            Assert.Equal(liveFrames + exitedFrames + earlyFrames, store.TotalFramesObserved);
        }

        // ================================================================
        // 8b) 封存后残留帧转移：确定性覆盖（不依赖线程交错）
        // ================================================================

        [Fact]
        public void 封存后残留帧确定性转移且转移帧不丢不重()
        {
            var clock = new FakeClock();
            FpsProcessStateStore? store = null;
            int injected = 0;

            // 假查询器在"查询"这一刻补写一帧：
            // 这一帧必然落在"取走帧"与"封存并收走残留帧"之间，
            // 于是必然走"封存 + 残留帧转移"路径 —— 与线程调度无关，不靠 sleep 碰运气。
            var query = new FakeProcessQuery
            {
                Handler = pid =>
                {
                    store!.RecordFrame(pid);
                    Interlocked.Increment(ref injected);
                    return FpsProcessQueryResult.CreateExited();
                }
            };
            store = NewStore(query, clock);

            store.RecordFrame(4242);       // 先写入 1 帧
            clock.Advance(500);
            store.AdvanceCycle(0.5);

            // 第 1 轮：正常取走 1 帧；查询时补写的 1 帧成为"封存后的残留帧" → 被转移到新条目
            Assert.Equal(1, injected);
            Assert.Equal(1, store.RetiredTransferredFrames);
            Assert.Equal(1, store.TotalFramesObserved);

            // 进程"复活"：查询改回 Found → 被转移的条目重新存活并继续参与统计
            query.Handler = pid => FpsProcessQueryResult.CreateFound("game.exe", 1);
            clock.Advance(500);
            store.AdvanceCycle(0.5);

            // 进程存活后不再产生新的转移
            Assert.Equal(1, store.RetiredTransferredFrames);

            // 对应关系：被转移的每一帧都恰好在复活后被统计一次（既不丢帧，也不重复统计）
            Assert.Equal(1 + store.RetiredTransferredFrames, store.TotalFramesObserved);

            // 复活的条目确实重新参与统计（说明被转移的帧回到了存活条目上）
            var revived = Assert.Single(store.GetDiagnostics());
            Assert.Equal("game.exe", revived.Name);
            Assert.Equal(1, revived.HistorySamples);
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
            var errors = new ConcurrentBag<Exception>();

            // ★ 用专用线程而不是线程池任务：
            //   机器一忙，.NET 线程池的线程注入非常慢，16 个调用者可能迟迟排不上队，
            //   "全部就位"的等待就会超时（与被测逻辑无关，纯属压测环境噪声）。
            var threads = new Thread[callers];
            for (int i = 0; i < callers; i++)
            {
                threads[i] = new Thread(() =>
                {
                    ready.Signal();
                    release.Wait();
                    try { counter.GetFps(); }
                    catch (Exception ex) { errors.Add(ex); }
                });
                threads[i].IsBackground = true;
                threads[i].Start();
            }

            // 16 个调用者全部就位后同时放行 → 制造真实重叠
            Assert.True(ready.Wait(StateWaitMs));
            release.Set();

            // 第一次启动进入假启动器后被阻塞：此时其余启动任务都撞上启动闸门
            Assert.True(launcher.StartEntered.Wait(StateWaitMs));
            // 第一次启动被 BlockStart 卡住期间，启动闸门一定是被占着的，
            // 其余调用者都会在闸门上排队（这里给它们一个确定的时间窗）
            Thread.Sleep(200);
            launcher.ReleaseStart();

            // 等"服务确实在运行"这个有因果关系的状态（而不是等一个与状态无因果的计数）
            Assert.True(WaitUntil(() => counter.IsServiceRunning, StateWaitMs), "第一次启动应当完成");

            // 所有并发调用者都返回后，"只启动过一次"才是最终结论
            var sw = Stopwatch.StartNew();
            foreach (Thread t in threads)
            {
                int remain = (int)Math.Max(1, StateWaitMs - sw.ElapsedMilliseconds);
                Assert.True(t.Join(remain), "所有并发调用者都应当返回");
            }
            Assert.Empty(errors);
            Assert.Equal(1, launcher.StartCount);

            // 服务已在运行：后续再请求也不会重复启动
            // （服务一旦在运行，StartService 内的二次复查会挡住任何后续拉起）
            for (int i = 0; i < 10; i++) counter.GetFps();
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
            Assert.True(launcher.WaitForStart(1, StateWaitMs * 2), "健康检查应当补启动服务");
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
            Assert.True(launcher.WaitForStart(1, StateWaitMs), "第一次启动应当发生");
            // 等"服务确实在运行"这个有因果关系的状态，而不是等一个与状态无因果的计数
            Assert.True(WaitUntil(() => counter.IsServiceRunning, StateWaitMs), "服务应当处于运行状态");

            // 需求关闭 → 可恢复停止（不是最终释放）
            demand = false;
            counter.StopService();
            Assert.Equal(1, launcher.StopCount);
            Assert.False(counter.IsServiceRunning);

            // 需求重新开启 → 仍然能启动
            demand = true;
            counter.GetFps();
            Assert.True(launcher.WaitForStart(2, StateWaitMs), "需求恢复后应当能再次启动");
            Assert.Equal(2, launcher.StartCount);
            Assert.True(WaitUntil(() => counter.IsServiceRunning, StateWaitMs), "服务应当处于运行状态");

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
            Assert.True(launcher.WaitForStart(1, StateWaitMs), "第一次启动应当发生");
            // 等"服务确实在运行"这个有因果关系的状态，而不是等一个与状态无因果的计数
            Assert.True(WaitUntil(() => counter.IsServiceRunning, StateWaitMs), "服务应当处于运行状态");

            // 外部进程退出（PresentMon 因无权限/异常退出）
            launcher.RaiseExited();

            // 运行状态必须立刻回落，Web 侧才能区分"未启动"与"启动但为 0"
            Assert.False(counter.IsServiceRunning);
        }

        // ================================================================
        // 17) 启动失败退避：进程"起来即退"不得被无界重拉起
        // ================================================================

        [Fact]
        public void 进程秒退后在退避窗口内不再拉起外部服务()
        {
            var launcher = new FakeLauncher();
            var clock = new FakeClock();
            using var counter = new FpsCounter(null, launcher, new FakeProcessQuery(), () => true, () => clock.Now);
            counter.FpsDemandProvider = () => true;

            // 第一次启动
            counter.GetFps();
            Assert.True(launcher.WaitForStart(1, StateWaitMs), "第一次启动应当发生");
            Assert.True(WaitUntil(() => counter.IsServiceRunning, StateWaitMs), "服务应当处于运行状态");

            // 进程"起来即退"（PresentMon 一起就退出）
            launcher.RaiseExited();
            Assert.False(counter.IsServiceRunning, "进程退出后运行状态必须立刻回落");

            // 退避窗口内：无论被访问多少次，都不得再次拉起外部进程
            // （否则外部 PresentMon 会按 GetFps() 调用频率被无界重拉起）
            for (int i = 0; i < 10; i++) counter.GetFps();

            // 退避必须真的拦下每一次拉起请求（该计数在 GetFps() 调用链内同步递增 → 确定性）
            Assert.True(counter.StartBlockedByBackoffCount >= 10,
                "每一次拉起请求都应当被启动失败退避拦下");

            // 启动器一次都不该被再次要求拉起
            Assert.Equal(1, launcher.StartCount);
        }

        [Fact]
        public void 退避窗口过后允许重新拉起外部服务()
        {
            var launcher = new FakeLauncher();
            var clock = new FakeClock();
            using var counter = new FpsCounter(null, launcher, new FakeProcessQuery(), () => true, () => clock.Now);
            counter.FpsDemandProvider = () => true;

            counter.GetFps();
            Assert.True(launcher.WaitForStart(1, StateWaitMs), "第一次启动应当发生");
            Assert.True(WaitUntil(() => counter.IsServiceRunning, StateWaitMs), "服务应当处于运行状态");

            launcher.RaiseExited();

            // 退避窗口内不拉起
            for (int i = 0; i < 5; i++) counter.GetFps();
            Assert.True(counter.StartBlockedByBackoffCount >= 5,
                "退避窗口内的每一次拉起请求都应当被拦下");
            Assert.Equal(1, launcher.StartCount);

            // 假时钟推进过 30 秒退避窗口（无需真实等待 30 秒）→ 允许重新尝试
            clock.Advance(30_001);
            counter.GetFps();
            Assert.True(launcher.WaitForStart(2, StateWaitMs), "退避窗口过后应当允许重新拉起");
            Assert.Equal(2, launcher.StartCount);
        }

        [Fact]
        public void 需求恢复在退避窗口内不拉起且越过窗口后能拉起()
        {
            // ★ 本用例的语义在修复轮 3 被改写：
            //   旧用例"需求消失后清理退避且需求恢复时能重新拉起"断言的是**本轮废止的行为**
            //   （需求跃迁清零退避 ⇒ 需求抖动可以击穿 30s 退避窗口）。
            //   新语义：退避的成因是"上次启动失败"，与当前有无需求无关 →
            //           需求恢复时若仍在退避窗口内 → **不得**拉起；
            //           推进时钟越过窗口后 → 必须能拉起。
            var launcher = new FakeLauncher();
            var clock = new FakeClock();
            using var counter = new FpsCounter(null, launcher, new FakeProcessQuery(), () => true, () => clock.Now);
            bool demand = true;
            counter.FpsDemandProvider = () => demand;

            counter.GetFps();
            Assert.True(launcher.WaitForStart(1, StateWaitMs), "第一次启动应当发生");
            Assert.True(WaitUntil(() => counter.IsServiceRunning, StateWaitMs), "服务应当处于运行状态");

            // 进程退出 → 记一次 30s 启动失败退避
            launcher.RaiseExited();
            Assert.False(counter.IsServiceRunning, "进程退出后运行状态必须立刻回落");

            // 退避窗口内：需求仍在 → 拉起被拦下
            counter.GetFps();
            Assert.True(counter.StartBlockedByBackoffCount >= 1,
                "退避窗口内的拉起请求应当被拦下");
            Assert.Equal(1, launcher.StartCount);

            // 需求消失 → **不得**清掉退避
            // （退避的成因是"上次启动失败"，与当前有没有需求无关）
            demand = false;
            counter.GetFps();
            Assert.False(counter.IsFpsDemandActive, "需求应当处于关闭状态");
            Assert.True(launcher.StartCount == 1, "需求消失不得拉起外部服务");

            // 需求恢复，但仍在退避窗口内 → 依然不得拉起
            demand = true;
            long blockedBefore = counter.StartBlockedByBackoffCount;
            counter.GetFps();
            Assert.True(counter.IsFpsDemandActive, "需求应当处于开启状态");
            Assert.True(counter.StartBlockedByBackoffCount > blockedBefore,
                "需求恢复时若仍在退避窗口内，拉起请求必须被退避拦下");
            Assert.False(launcher.WaitForStart(2, MustNotHappenWaitMs),
                "退避窗口内不得因需求恢复而再次拉起外部服务");
            Assert.True(launcher.StartCount == 1, "退避窗口内不得再次拉起外部服务");

            // 假时钟推进过 30 秒退避窗口（无需真实等待 30 秒）→ 需求仍在 → 必须能重新拉起
            clock.Advance(30_001);
            counter.GetFps();
            Assert.True(launcher.WaitForStart(2, StateWaitMs), "退避窗口过后应当允许重新拉起");
            Assert.Equal(2, launcher.StartCount);
        }

        [Fact]
        public void 需求抖动不会击穿退避窗口()
        {
            // 场景 S7（修复轮 2 复审复现）：秒退并记退避后，
            // demand=false → GetFps() → demand=true → GetFps()
            // → 30s 窗口内 StartCount 由 1 变成 2（退避被需求抖动击穿）。
            //
            // 生产可达性：webFpsDemand 的 HTTP 租期只有 2 秒，
            // 轮询间隔 > 2s 的外部消费者会让需求每轮抖动一次。
            var launcher = new FakeLauncher();
            var clock = new FakeClock();
            using var counter = new FpsCounter(null, launcher, new FakeProcessQuery(), () => true, () => clock.Now);
            bool demand = true;
            counter.FpsDemandProvider = () => demand;

            counter.GetFps();
            Assert.True(launcher.WaitForStart(1, StateWaitMs), "第一次启动应当发生");
            Assert.True(WaitUntil(() => counter.IsServiceRunning, StateWaitMs), "服务应当处于运行状态");

            // 进程秒退 → 记一次 30s 启动失败退避
            launcher.RaiseExited();
            Assert.False(counter.IsServiceRunning, "进程退出后运行状态必须立刻回落");

            // 需求抖动 3 轮（每轮 false→true）：退避窗口内每一次拉起请求都仍被拦下
            for (int round = 1; round <= 3; round++)
            {
                demand = false;
                counter.GetFps();
                Assert.False(counter.IsFpsDemandActive, $"第 {round} 轮抖动：需求应当处于关闭状态");

                demand = true;
                long blockedBefore = counter.StartBlockedByBackoffCount;
                counter.GetFps();
                Assert.True(counter.IsFpsDemandActive, $"第 {round} 轮抖动：需求应当处于开启状态");
                Assert.True(counter.StartBlockedByBackoffCount > blockedBefore,
                    $"第 {round} 轮需求抖动时，拉起请求必须仍被退避拦下（抖动不得击穿退避窗口）");
            }

            // 整个抖动过程中不得再拉起一次外部进程
            Assert.False(launcher.WaitForStart(2, MustNotHappenWaitMs),
                "需求抖动不得击穿退避窗口（退避的成因是上次启动失败，与需求无关）");
            Assert.True(launcher.StartCount == 1, "退避窗口内不得再次拉起外部服务");
        }

        [Fact]
        public void 服务应当在短时间内进入运行状态而不是拖到宽松预算上限()
        {
            // 背景：修复轮 2 把等待预算从 3s/5s/8s 放宽到 20s（健康检查 40s）。
            // 断言语义没被削弱（仍是"该状态最终发生"），但"启动耗时 5–20s"的性能回归不再被发现。
            // 这里保留一条**短**的及时性断言（真实单调时钟；假时钟测不出"慢"），
            // 与上面那条宽松的"最终发生"断言并存。
            var launcher = new FakeLauncher();
            using var counter = new FpsCounter(null, launcher, new FakeProcessQuery(), () => true);

            // 预热线程池：避免"首次注入线程"的延迟被算进启动耗时
            Task.Run(() => { }).GetAwaiter().GetResult();

            var sw = Stopwatch.StartNew();
            counter.GetFps();
            Assert.True(launcher.WaitForStart(1, StateWaitMs), "第一次启动应当发生");
            Assert.True(WaitUntil(() => counter.IsServiceRunning, StateWaitMs), "服务应当处于运行状态");
            long elapsed = sw.ElapsedMilliseconds;

            Assert.True(elapsed < StartLatencyBudgetMs,
                $"服务应当在 {StartLatencyBudgetMs}ms 内进入运行状态（实际 {elapsed}ms）；" +
                "否则'启动变慢'的性能回归会被宽松的'最终发生'预算掩盖");
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
