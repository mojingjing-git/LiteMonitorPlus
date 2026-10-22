using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.SystemServices.InfoService;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 插件调度计时器测试（LM-001）。
    ///
    /// 缺陷根因：不可见分支在 try 之前 return，永远进不了 finally，
    /// 而"重新武装计时器"只发生在 finally 里；AutoReset=false 的计时器
    /// 于是再也不会自行触发 ⇒ 该插件实例的调度永久停摆。
    ///
    /// 覆盖契约 docs/superpowers/plans/2026-10-22-v3-s1-inflight-lease-contract.md §3.5 的
    /// T1–T8 全部条目，全部为真实执行断言（无跳过、无早退）。
    ///
    /// 时间相关用例全部走可注入接缝（手动驱动计时器 + 可见性探针 + 执行探针），
    /// 断言不依赖 Thread.Sleep 长等待；唯一需要真实 I/O 的 T8b 用例使用
    /// 本机回环 TCP 服务器 + 显式同步点，等待控制在毫秒级。
    /// </summary>
    public sealed class PluginSchedulingTests
    {
        // ==========================================================
        // T1 启动即可见 → 能执行
        // ==========================================================

        [Fact]
        public async Task T1_启动即可见_首次心跳即执行并重新武装()
        {
            using var h = new ScheduleHarness(visible: true);
            h.Manager.StartInstance(h.Instance, h.Template);

            ManualPluginTimer timer = Assert.Single(h.Timers);
            Assert.Equal(1, timer.StartCount);                                // 初始武装
            Assert.Equal(PluginManager.InitialTickDelayMs, timer.Interval);

            await timer.FireAsync();

            Assert.Equal(1, h.ExecuteCount);                                 // 可见 → 执行
            Assert.Equal(2, timer.StartCount);                                // 执行后重新武装
            Assert.Equal(h.ExpectedIntervalMs, timer.Interval);               // 恢复正常刷新间隔
        }

        // ==========================================================
        // T2 启动即不可见 → 稍后变可见 → 恢复执行
        // ==========================================================

        [Fact]
        public async Task T2_启动即不可见_变可见后恢复执行()
        {
            using var h = new ScheduleHarness(visible: false);
            h.Manager.StartInstance(h.Instance, h.Template);

            ManualPluginTimer timer = Assert.Single(h.Timers);

            await timer.FireAsync();

            // 不可见 → 跳过执行
            Assert.Equal(0, h.ExecuteCount);
            // ★ 关键回归点：不可见分支必须重新武装计时器。
            //   修复前该分支在 try 之前 return，进不了 finally ⇒ StartCount 停在 1，调度永久停摆。
            Assert.Equal(2, timer.StartCount);
            Assert.Equal(h.Manager.InvisibleRetryIntervalMs, timer.Interval);

            // 变可见 → 恢复执行
            h.Visible = true;
            await timer.FireAsync();

            Assert.Equal(1, h.ExecuteCount);
            Assert.Equal(3, timer.StartCount);
            Assert.Equal(h.ExpectedIntervalMs, timer.Interval);
        }

        // ==========================================================
        // T2（生产计时器路径）不可见分支后仍然继续轮询
        // ==========================================================

        [Fact]
        public async Task T2b_生产计时器_不可见分支后仍然继续轮询()
        {
            using var h = new ScheduleHarness(visible: false);
            h.UseProductionTimer();                             // 不注入计时器工厂 → 真实 System.Timers.Timer
            h.Manager.InvisibleRetryIntervalMs = 30;             // 复查间隔压到毫秒级，避免长等待

            int probes = 0;
            h.Manager.VisibilityProbe = _ =>
            {
                Interlocked.Increment(ref probes);
                return false;                                    // 始终不可见
            };

            h.Manager.StartInstance(h.Instance, h.Template);

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref probes) < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }

            // ★ 真实计时器在不可见分支后仍在持续触发 ⇒ 调度没有停摆（修复前只会触发 1 次）
            Assert.True(Volatile.Read(ref probes) >= 3,
                $"不可见分支后调度停摆：可见性探测次数 = {probes}");

            h.Manager.StopInstance(h.Instance.Id);
        }

        // ==========================================================
        // T3 可见→不可见→可见 → 轮询持续正确
        // ==========================================================
        [Fact]
        public async Task T3_可见_不可见_可见_轮询持续正确()
        {
            using var h = new ScheduleHarness(visible: true);
            h.Manager.StartInstance(h.Instance, h.Template);

            ManualPluginTimer timer = Assert.Single(h.Timers);

            await timer.FireAsync();                       // 可见 → 执行
            Assert.Equal(1, h.ExecuteCount);
            Assert.Equal(h.ExpectedIntervalMs, timer.Interval);

            h.Visible = false;
            await timer.FireAsync();                       // 不可见 → 跳过但继续轮询
            Assert.Equal(1, h.ExecuteCount);
            Assert.Equal(h.Manager.InvisibleRetryIntervalMs, timer.Interval);
            Assert.Equal(3, timer.StartCount);

            h.Visible = true;
            await timer.FireAsync();                       // 恢复可见 → 再次执行
            Assert.Equal(2, h.ExecuteCount);
            Assert.Equal(h.ExpectedIntervalMs, timer.Interval);

            await timer.FireAsync();                       // 轮询持续
            Assert.Equal(3, h.ExecuteCount);
            Assert.Equal(5, timer.StartCount);
            Assert.Equal(1, timer.MaxConcurrentTicks);     // 始终单线程串行心跳
        }

        // ==========================================================
        // T4 执行期间被禁用 → 不重复启动
        // ==========================================================

        [Fact]
        public async Task T4_执行期间被禁用_不重复启动()
        {
            using var h = new ScheduleHarness(visible: true);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Execution = async (inst, tmpl, token) =>
            {
                entered.TrySetResult(true);
                await gate.Task.ConfigureAwait(false);
                return true;
            };

            h.Manager.StartInstance(h.Instance, h.Template);
            ManualPluginTimer timer = Assert.Single(h.Timers);

            Task fire = timer.FireAsync();
            await entered.Task.ConfigureAwait(false);      // 同步点：执行已经开始

            h.Instance.Enabled = false;                     // 执行期间被禁用
            gate.SetResult(true);
            await fire.ConfigureAwait(false);

            Assert.Equal(1, timer.StartCount);              // 没有重新武装 ⇒ 不会重复启动
            Assert.Equal(1, h.ExecuteCount);
        }

        // ==========================================================
        // T5 失败进入退避 → 再成功时恢复正常周期
        // ==========================================================

        [Fact]
        public async Task T5_失败进入退避_再成功恢复正常周期()
        {
            using var h = new ScheduleHarness(visible: true);
            bool succeed = false;
            h.Execution = (inst, tmpl, token) => Task.FromResult(succeed);

            h.Manager.StartInstance(h.Instance, h.Template);
            ManualPluginTimer timer = Assert.Single(h.Timers);

            await timer.FireAsync();                        // 第 1 次失败 → 5s 退避
            Assert.Equal(1, h.Manager.GetConsecutiveFailures(h.Instance.Id));
            Assert.Equal(PluginConstants.RETRY_INTERVAL_MS, timer.Interval);

            await timer.FireAsync();                        // 第 2 次失败 → 10s 退避
            Assert.Equal(2, h.Manager.GetConsecutiveFailures(h.Instance.Id));
            Assert.Equal(2 * PluginConstants.RETRY_INTERVAL_MS, timer.Interval);

            succeed = true;
            await timer.FireAsync();                        // 成功 → 恢复正常周期 + 失败计数清零
            Assert.Equal(0, h.Manager.GetConsecutiveFailures(h.Instance.Id));
            Assert.Equal(h.ExpectedIntervalMs, timer.Interval);
        }

        [Fact]
        public async Task T5b_连续失败第三次_触发网络客户端重置与缓存清理()
        {
            using var h = new ScheduleHarness(visible: true);
            h.Execution = (inst, tmpl, token) => Task.FromResult(false);

            h.Manager.StartInstance(h.Instance, h.Template);
            ManualPluginTimer timer = Assert.Single(h.Timers);

            await timer.FireAsync();
            await timer.FireAsync();
            Assert.Empty(h.NetworkResets);                  // 第 1、2 次失败不重置

            await timer.FireAsync();
            Assert.Single(h.NetworkResets);                 // 第 3 次失败触发重置
            Assert.Equal(h.Instance.Id, h.NetworkResets[0]);
            Assert.Equal(3 * PluginConstants.RETRY_INTERVAL_MS, timer.Interval);
        }

        // ==========================================================
        // T6 快速停止/启动/重启 → 无并发重复执行
        // ==========================================================

        [Fact]
        public async Task T6_快速停止启动重启_无并发重复执行()
        {
            using var h = new ScheduleHarness(visible: true);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Execution = async (inst, tmpl, token) =>
            {
                entered.TrySetResult(true);
                await gate.Task.ConfigureAwait(false);
                return true;
            };

            h.Manager.StartInstance(h.Instance, h.Template);
            ManualPluginTimer first = Assert.Single(h.Timers);

            Task fire = first.FireAsync();
            await entered.Task.ConfigureAwait(false);

            // 快速 stop / start（等价于 RestartInstance 的 stop + start 序列）
            h.Manager.StopInstance(h.Instance.Id);
            h.Manager.StartInstance(h.Instance, h.Template);
            ManualPluginTimer second = h.Timers.Last();

            gate.SetResult(true);
            await fire.ConfigureAwait(false);

            // 旧计时器不得复活
            Assert.Equal(1, first.StartCount);
            Assert.True(first.IsDisposed);
            // 同一实例最多一个有效调度循环
            Assert.Equal(1, h.Manager.GetRegisteredScheduleCount(h.Instance.Id));
            Assert.Equal(1, h.Manager.RegisteredScheduleCount);

            await second.FireAsync();
            Assert.Equal(2, h.ExecuteCount);
            Assert.Equal(1, h.MaxConcurrentExecutions);     // 旧循环与新循环从未并发执行
        }

        // ==========================================================
        // T7 取消 → 无未观察的异步异常
        // ==========================================================

        [Fact]
        public async Task T7_取消_无未观察的异步异常()
        {
            int unobserved = 0;
            EventHandler<UnobservedTaskExceptionEventArgs> handler = (s, e) =>
            {
                if (ContainsMarker(e.Exception)) Interlocked.Increment(ref unobserved);
                e.SetObserved();
            };

            TaskScheduler.UnobservedTaskException += handler;
            try
            {
                using var h = new ScheduleHarness(visible: true);
                h.Execution = (inst, tmpl, token) => throw new ScheduleMarkerException();

                h.Manager.StartInstance(h.Instance, h.Template);
                ManualPluginTimer timer = Assert.Single(h.Timers);

                // 心跳抛异常：异常必须被消化（任务不抛异常），计时器按退避重新武装
                await timer.FireAsync().ConfigureAwait(false);

                Assert.Equal(2, timer.StartCount);
                Assert.Equal(PluginConstants.RETRY_INTERVAL_MS, timer.Interval);

                // 取消场景：Stop 之后令牌被取消，且不再重新武装
                h.Manager.StopInstance(h.Instance.Id);
                Assert.Equal(2, timer.StartCount);
                Assert.NotEmpty(h.ObservedTokens);
                Assert.All(h.ObservedTokens, t => Assert.True(t.IsCancellationRequested));

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.Equal(0, unobserved);
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= handler;
            }
        }

        [Fact]
        public async Task T7b_生产计时器_心跳异常不逃逸为未观察异常()
        {
            int unobserved = 0;
            EventHandler<UnobservedTaskExceptionEventArgs> handler = (s, e) =>
            {
                if (ContainsMarker(e.Exception)) Interlocked.Increment(ref unobserved);
                e.SetObserved();
            };

            TaskScheduler.UnobservedTaskException += handler;
            try
            {
                int invoked = 0;
                using IPluginTimer timer = PluginManager.CreateProductionTimer(10, async _ =>
                {
                    Interlocked.Increment(ref invoked);
                    await Task.Yield();
                    // async void 事件处理器里未捕获的异常会逃逸到线程池并终止进程
                    throw new ScheduleMarkerException();
                });

                timer.Start();

                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while (Volatile.Read(ref invoked) == 0 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }

                Assert.Equal(1, Volatile.Read(ref invoked));

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.Equal(0, unobserved);
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= handler;
            }
        }

        // ==========================================================
        // T8 Stop 返回后：旧任务不重新调度、不写回结果；Restart 后最多一个有效调度
        // ==========================================================

        [Fact]
        public async Task T8_Stop返回后旧任务不重新调度不写回结果_Restart后最多一个有效调度()
        {
            using var h = new ScheduleHarness(visible: true);
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 返回 false：若旧结果被写回，连续失败计数会变成 1
            h.Execution = async (inst, tmpl, token) =>
            {
                entered.TrySetResult(true);
                await gate.Task.ConfigureAwait(false);
                return false;
            };

            h.Manager.StartInstance(h.Instance, h.Template);
            ManualPluginTimer first = Assert.Single(h.Timers);

            Task fire = first.FireAsync();
            await entered.Task.ConfigureAwait(false);

            h.Manager.StopInstance(h.Instance.Id);          // ★ Stop 返回

            gate.SetResult(true);                           // 旧执行继续跑完（模拟忽略取消的在途请求）
            await fire.ConfigureAwait(false);

            // 旧计时器没有重新武装
            Assert.Equal(1, first.StartCount);
            Assert.True(first.IsDisposed);
            // 旧结果没有写回（失败计数未被污染）
            Assert.Equal(0, h.Manager.GetConsecutiveFailures(h.Instance.Id));
            // 在途令牌已取消 —— 执行器据此跳过结果写回（契约 C6）
            Assert.All(h.ObservedTokens, t => Assert.True(t.IsCancellationRequested));

            // Restart：最多一个有效调度
            h.Manager.StartInstance(h.Instance, h.Template);
            ManualPluginTimer second = h.Timers.Last();
            Assert.Equal(1, h.Manager.GetRegisteredScheduleCount(h.Instance.Id));
            Assert.Equal(1, h.Manager.RegisteredScheduleCount);
            Assert.Single(h.Timers.Where(t => !t.IsDisposed));

            // 新一代的结果可以正常写回
            await second.FireAsync().ConfigureAwait(false);
            Assert.Equal(2, h.ExecuteCount);
            Assert.Equal(1, h.Manager.GetConsecutiveFailures(h.Instance.Id));
        }

        [Fact]
        public async Task T8b_Stop后在途响应不写回结果_真实执行器端到端()
        {
            using var server = new ControlledHttpServer();
            using var h = new ScheduleHarness(visible: true);
            h.UseRealExecutor();                           // 不注入执行探针 → 走真实 PluginExecutor

            h.Template.Execution.Url = server.Url;
            h.Template.Execution.Extract = new Dictionary<string, string> { { "value", "value" } };
            h.Template.Outputs = new List<PluginOutput> { new PluginOutput { Key = "val", Format = "{{value}}" } };

            h.Manager.StartInstance(h.Instance, h.Template);
            ManualPluginTimer timer = Assert.Single(h.Timers);

            string injectKey = h.Instance.Id + ".val";
            Assert.Equal(PluginConstants.STATUS_LOADING, InfoService.Instance.GetValue(injectKey));

            Task fire = timer.FireAsync();
            await server.WaitForRequestAsync().ConfigureAwait(false);   // 同步点：请求已在途

            h.Manager.StopInstance(h.Instance.Id);                      // ★ Stop 返回
            server.ReleaseResponse();                                  // 让在途响应回来
            await fire.ConfigureAwait(false);

            // ★ 结果没有写回：仍是加载占位符，不是响应里的 42
            Assert.Equal(PluginConstants.STATUS_LOADING, InfoService.Instance.GetValue(injectKey));
            // 旧计时器没有重新武装
            Assert.Equal(1, timer.StartCount);
            Assert.True(timer.IsDisposed);
        }

        // ==========================================================
        // 辅助
        // ==========================================================

        private static bool ContainsMarker(Exception ex)
        {
            for (Exception cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is ScheduleMarkerException) return true;
            }
            return false;
        }

        /// <summary>仅用于识别"本用例制造"的异常，避免误判进程内其它未观察异常。</summary>
        private sealed class ScheduleMarkerException : Exception
        {
            public ScheduleMarkerException() : base("schedule test marker") { }
        }

        /// <summary>
        /// 手动驱动的调度计时器测试替身。
        /// 语义对齐生产计时器（AutoReset=false 的 Stop-Wait 模式）：
        /// 触发一次之后必须被显式重新武装才会再次触发。
        /// </summary>
        private sealed class ManualPluginTimer : IPluginTimer
        {
            private readonly Func<IPluginTimer, Task> _onTick;
            private int _activeTicks;

            public double Interval { get; set; }
            public bool IsRunning { get; private set; }
            public bool IsDisposed { get; private set; }
            public int StartCount { get; private set; }
            public int StopCount { get; private set; }
            public int StartAfterDisposeCount { get; private set; }
            public int MaxConcurrentTicks { get; private set; }

            public ManualPluginTimer(double interval, Func<IPluginTimer, Task> onTick)
            {
                Interval = interval;
                _onTick = onTick;
            }

            public void Start()
            {
                if (IsDisposed)
                {
                    StartAfterDisposeCount++;
                    StartCount++;
                    throw new ObjectDisposedException(nameof(ManualPluginTimer));
                }
                IsRunning = true;
                StartCount++;
            }

            public void Stop()
            {
                IsRunning = false;
                StopCount++;
            }

            public void Dispose()
            {
                IsDisposed = true;
                IsRunning = false;
            }

            /// <summary>手动触发一次心跳（模拟真实计时器到点）。未武装或已释放时不触发。</summary>
            public async Task FireAsync()
            {
                if (!IsRunning || IsDisposed) return;

                int active = Interlocked.Increment(ref _activeTicks);
                if (active > MaxConcurrentTicks) MaxConcurrentTicks = active;
                try
                {
                    await _onTick(this).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeTicks);
                }
            }
        }

        /// <summary>
        /// 调度测试夹具：构造与单例隔离的 PluginManager，并注入
        /// 可见性探针 / 执行探针 / 计时器工厂，使调度行为完全确定。
        /// </summary>
        private sealed class ScheduleHarness : IDisposable
        {
            private readonly object _gate = new object();
            private int _executeCount;
            private int _activeExecutions;
            private int _maxConcurrentExecutions;

            public PluginManager Manager { get; }
            public PluginInstanceConfig Instance { get; }
            public PluginTemplate Template { get; }
            public List<ManualPluginTimer> Timers { get; } = new List<ManualPluginTimer>();
            public List<System.Threading.CancellationToken> ObservedTokens { get; } = new List<System.Threading.CancellationToken>();
            public List<string> NetworkResets { get; } = new List<string>();

            public bool Visible { get; set; }

            public int ExecuteCount { get { lock (_gate) { return _executeCount; } } }
            public int MaxConcurrentExecutions { get { lock (_gate) { return _maxConcurrentExecutions; } } }

            /// <summary>执行探针实现（可替换以模拟成功 / 失败 / 阻塞 / 异常）。</summary>
            public Func<PluginInstanceConfig, PluginTemplate, System.Threading.CancellationToken, Task<bool>> Execution { get; set; }
                = (inst, tmpl, token) => Task.FromResult(true);

            public int ExpectedIntervalMs => PluginManager.GetEffectiveInterval(Instance, Template) * 1000;

            public ScheduleHarness(bool visible)
            {
                Visible = visible;
                Manager = new PluginManager();

                Instance = new PluginInstanceConfig
                {
                    Id = "lm001-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    TemplateId = "lm001-template",
                    Enabled = true
                };

                Template = new PluginTemplate
                {
                    Id = "lm001-template",
                    Execution = new PluginExecution
                    {
                        Type = "api_json",
                        Interval = 7,
                        MinInterval = 0,
                        Url = "http://127.0.0.1:1/unused",
                        Extract = new Dictionary<string, string> { { "value", "value" } }
                    },
                    Outputs = new List<PluginOutput> { new PluginOutput { Key = "val", Format = "{{value}}" } }
                };

                Manager.VisibilityProbe = _ => Visible;

                Manager.ExecutionProbe = async (inst, tmpl, token) =>
                {
                    lock (_gate)
                    {
                        _executeCount++;
                        _activeExecutions++;
                        if (_activeExecutions > _maxConcurrentExecutions) _maxConcurrentExecutions = _activeExecutions;
                        ObservedTokens.Add(token);
                    }
                    try
                    {
                        return await Execution(inst, tmpl, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_gate) { _activeExecutions--; }
                    }
                };

                Manager.TimerFactory = (interval, onTick) =>
                {
                    var timer = new ManualPluginTimer(interval, onTick);
                    lock (_gate) { Timers.Add(timer); }
                    return timer;
                };

                Manager.NetworkResetObserver = id =>
                {
                    lock (_gate) { NetworkResets.Add(id); }
                };
            }

            /// <summary>改走真实执行器（不再注入执行探针）。</summary>
            public void UseRealExecutor()
            {
                Manager.ExecutionProbe = null;
            }

            /// <summary>改走生产计时器（真实 System.Timers.Timer，不再注入计时器工厂）。</summary>
            public void UseProductionTimer()
            {
                Manager.TimerFactory = null;
            }

            public void Dispose()
            {
                try { Manager.Stop(); } catch { }
                Manager.ExecutorInternal?.Dispose();
            }
        }

        /// <summary>
        /// 本机回环 HTTP 服务器：收到请求后挂起响应，由测试显式释放。
        /// 用于构造"在途请求"窗口，验证 Stop 之后结果不会写回。
        /// </summary>
        private sealed class ControlledHttpServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly TaskCompletionSource<bool> _requestReceived =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public int Port { get; }
            public string Url => "http://127.0.0.1:" + Port + "/data";

            public ControlledHttpServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = Task.Run(AcceptLoopAsync);
            }

            public Task WaitForRequestAsync() => _requestReceived.Task;

            public void ReleaseResponse() => _release.TrySetResult(true);

            private async Task AcceptLoopAsync()
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                        _ = Task.Run(() => HandleAsync(client));
                    }
                }
                catch { }
            }

            private async Task HandleAsync(TcpClient client)
            {
                try
                {
                    using (client)
                    {
                        NetworkStream stream = client.GetStream();
                        byte[] buffer = new byte[4096];
                        await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);

                        _requestReceived.TrySetResult(true);
                        await _release.Task.ConfigureAwait(false);

                        byte[] body = Encoding.UTF8.GetBytes("{\"value\":\"42\"}");
                        byte[] header = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                            + body.Length + "\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header, _cts.Token).ConfigureAwait(false);
                        await stream.WriteAsync(body, _cts.Token).ConfigureAwait(false);
                        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                    }
                }
                catch { }
            }

            public void Dispose()
            {
                _release.TrySetResult(true);
                try { _cts.Cancel(); } catch { }
                try { _listener.Stop(); } catch { }
                try { _cts.Dispose(); } catch { }
            }
        }
    }
}
