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
    /// ★ 根因（2026-10-22 复核更正，原"永久停摆"表述不成立）：
    /// 基线的不可见分支在 try 之前 return，进不了 finally，而"重新武装计时器"
    /// 只发生在 finally 里 —— 也就是说基线**从未显式**重新武装过计时器，它依赖的是
    /// <see cref="System.Timers.Timer"/> 的一个**未文档化副作用**：
    /// 在 Elapsed 处理器内部给 <c>Interval</c> 赋值时，Timer 内部会执行
    /// <c>_timer.Change(interval, Timeout.Infinite)</c>，把一次性的计时器顺带重新武装
    /// （.NET 8 源码：<c>Timer.Interval</c> setter → <c>UpdateTimer()</c>）。
    /// 因此基线**并非永久停摆**，而是"靠这个副作用继续轮询"。
    /// <para>
    /// 实证（本次复核独立复现，探针见交付报告）：30ms 间隔、400ms 窗口内触发 12 次；
    /// 忠实重放基线 async-void 处理器时 6 秒内按 2 秒间隔持续触发 3 次（50ms / 2051ms / 4051ms）；
    /// 而处理器内既不赋值 Interval 也不 Start 时只触发 1 次。
    /// </para>
    /// <para>
    /// 修复后的行为**不再依赖**该副作用：不可见分支与其余退出路径统一由 <c>finally</c>
    /// 收尾，显式调用 <c>Start()</c> 重新武装，并叠加"实例代次"守卫
    /// （旧代次的回调不得重新调度、不得写回结果）。
    /// </para>
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
            // ★ 关键回归点：不可见分支必须**显式**重新武装计时器（调用 Start()）。
            //   修复前该分支在 try 之前 return，进不了 finally ⇒ 没有任何显式 Start()，
            //   StartCount 停在 1；基线只是靠 System.Timers.Timer 在处理器内赋值 Interval
            //   的未文档化副作用才得以继续轮询（并不是"永久停摆"，见类注释）。
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
        // T2b 不可见分支的**显式重新武装**（本次修复的实际机制）
        // ==========================================================

        [Fact]
        public async Task T2b_不可见分支显式重新武装_不依赖Interval赋值副作用()
        {
            using var h = new ScheduleHarness(visible: false);
            // ★ 计时器替身：赋值 Interval **不产生任何重武装副作用**（严格 Stop-Wait 语义）。
            //   这正是基线缺陷所依赖的语义 —— 基线靠 System.Timers.Timer 在处理器内赋值
            //   Interval 时"顺带"重武装才得以继续轮询；本次修复已改为显式 Start()，
            //   不再依赖该副作用。所以在这里用"赋值不重武装"的替身，
            //   调度能继续 ⇔ 显式重武装路径确实被走到。
            h.UseStrictStopWaitTimer();

            int probes = 0;
            h.Manager.VisibilityProbe = _ =>
            {
                Interlocked.Increment(ref probes);
                return false;                                    // 始终不可见
            };

            h.Manager.StartInstance(h.Instance, h.Template);

            ManualPluginTimer timer = Assert.Single(h.Timers);

            // 第 1 次心跳：不可见 → 跳过执行，但必须走"显式重新武装"
            await timer.FireAsync();

            Assert.Equal(0, h.ExecuteCount);                                  // 不可见 → 不执行
            Assert.Equal(2, timer.StartCount);                                // ★ 显式重武装（Start）被走到
            Assert.Equal(h.Manager.InvisibleRetryIntervalMs, timer.Interval); // 复查间隔已设置

            // ★ 顺序：先设置复查间隔、再重新武装（否则下一次心跳会沿用旧间隔）
            Assert.Equal(
                new[] { "start", "interval=" + h.Manager.InvisibleRetryIntervalMs, "start" },
                timer.Events);

            // 第 2 次心跳：调度**继续** —— 靠的是显式重新武装，
            // 与"赋值 Interval 会顺带重武装"这个副作用无关。
            await timer.FireAsync();

            Assert.Equal(2, probes);                                          // 可见性探针被再次调用
            Assert.Equal(3, timer.StartCount);
            Assert.Equal(0, h.ExecuteCount);                                  // 始终不可见 → 从不执行
        }

        // ==========================================================
        // T2c 生产计时器（真实 System.Timers.Timer）：显式重新武装
        // ==========================================================

        [Fact]
        public async Task T2c_生产计时器_不可见分支显式重新武装()
        {
            using var h = new ScheduleHarness(visible: false);
            h.UseProductionTimer();                             // 不注入计时器工厂 → 真实 System.Timers.Timer
            h.Manager.InvisibleRetryIntervalMs = 1000;           // 复查间隔：给读取 Enabled 留出窗口

            int probes = 0;
            h.Manager.VisibilityProbe = _ =>
            {
                Interlocked.Increment(ref probes);
                return false;                                    // 始终不可见
            };

            h.Manager.StartInstance(h.Instance, h.Template);

            // 等第一次心跳（不可见分支）走完
            Assert.True(await WaitUntilAsync(() => Volatile.Read(ref probes) >= 1, TimeSpan.FromSeconds(10)),
                "第一次心跳未在超时内发生");
            await Task.Delay(200);                                  // 让第一次心跳的收尾（显式重武装）完成

            // ★ 差异观测量：真实 System.Timers.Timer 的 Enabled。
            //   事实（.NET 8 源码 + 本次复核探针实证）：
            //   System.Timers.Timer 在 Elapsed 处理器**内部**被赋值 Interval 时会**重新武装**
            //   （setter → UpdateTimer() → _timer.Change(i, Timeout.Infinite)），
            //   但**不会**把 Enabled 置回 true —— 回调开始时 AutoReset=false 已经把
            //   内部 _enabled 置为 false，而 Interval setter 不碰 _enabled。
            //   只有显式 Start()（Enabled = true）才会让 Enabled 变成 true。
            //   ⇒ 基线（只赋值 Interval、不 Start）：Enabled == false；
            //     修复后（显式 Start 重武装）：Enabled == true。
            System.Timers.Timer inner = h.InnerProductionTimer();
            Assert.True(inner.Enabled,
                "不可见分支必须显式重新武装（调用 Start），不能只依赖赋值 Interval 的副作用");

            // 调度仍在继续（真实计时器确实还在跳）—— 修复前后都成立，仅作行为留档，
            // 不承担判别力（判别力由上面的 Enabled 断言承担）。
            Assert.True(await WaitUntilAsync(() => Volatile.Read(ref probes) >= 2, TimeSpan.FromSeconds(10)),
                "不可见分支后生产计时器没有继续轮询");

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

        /// <summary>
        /// 轮询等待条件成立（带超时）。返回是否在超时内成立。
        /// 用于等待"生产计时器已经触发过 N 次"这类真实计时器场景，
        /// 避免用固定长度的 Sleep 猜时间。
        /// </summary>
        private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (predicate()) return true;
                await Task.Delay(20).ConfigureAwait(false);
            }
            return predicate();
        }

        /// <summary>仅用于识别"本用例制造"的异常，避免误判进程内其它未观察异常。</summary>
        private sealed class ScheduleMarkerException : Exception
        {
            public ScheduleMarkerException() : base("schedule test marker") { }
        }

        /// <summary>
        /// 手动驱动的调度计时器测试替身。
        /// <para>
        /// 默认语义对齐生产计时器的 **Stop-Wait** 模式（AutoReset=false）：
        /// 触发一次之后必须被显式重新武装才会再次触发。
        /// </para>
        /// <para>
        /// ★ <paramref name="strictStopWait"/> = true 时进一步做到
        /// <b>赋值 Interval 不产生任何重武装副作用</b>：只有显式 <c>Start()</c> 才会重新武装。
        /// 这正是基线缺陷所依赖、而本次修复已不再依赖的语义
        /// （<see cref="System.Timers.Timer"/> 在处理器内赋值 Interval 会顺带重武装）。
        /// 在这种替身上"调度能继续" ⇔ 显式重武装路径确实被走到。
        /// </para>
        /// </summary>
        private sealed class ManualPluginTimer : IPluginTimer
        {
            private readonly Func<IPluginTimer, Task> _onTick;
            private readonly bool _strictStopWait;
            private readonly List<string> _events = new List<string>();
            private int _activeTicks;
            private double _interval;
            private bool _armed;

            public double Interval
            {
                get => _interval;
                set
                {
                    _interval = value;
                    Record("interval=" + value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            public bool IsRunning { get; private set; }
            public bool IsDisposed { get; private set; }
            public int StartCount { get; private set; }
            public int StopCount { get; private set; }
            public int StartAfterDisposeCount { get; private set; }
            public int MaxConcurrentTicks { get; private set; }

            /// <summary>事件序列（"start" / "interval=…" / "stop" / "dispose"），用于断言调用顺序。</summary>
            public string[] Events
            {
                get { lock (_events) return _events.ToArray(); }
            }

            public ManualPluginTimer(double interval, Func<IPluginTimer, Task> onTick, bool strictStopWait = false)
            {
                _interval = interval;
                _onTick = onTick;
                _strictStopWait = strictStopWait;
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
                _armed = true;
                StartCount++;
                Record("start");
            }

            public void Stop()
            {
                IsRunning = false;
                _armed = false;
                StopCount++;
                Record("stop");
            }

            public void Dispose()
            {
                IsDisposed = true;
                IsRunning = false;
                _armed = false;
                Record("dispose");
            }

            /// <summary>手动触发一次心跳（模拟真实计时器到点）。未武装或已释放时不触发。</summary>
            public async Task FireAsync()
            {
                if (!IsRunning || IsDisposed) return;
                // ★ 严格 Stop-Wait：必须先被显式重新武装（Start）才会再次触发。
                //   赋值 Interval 在这里**没有**任何重武装副作用（与 System.Timers.Timer 相反）。
                if (_strictStopWait && !_armed) return;
                _armed = false;

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

            private void Record(string evt)
            {
                lock (_events) _events.Add(evt);
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

            /// <summary>
            /// 改用"严格 Stop-Wait"计时器替身：赋值 Interval **不产生**重武装副作用，
            /// 只有显式 Start() 才会重新武装。用于断言"显式重武装路径确实被走到"。
            /// </summary>
            public void UseStrictStopWaitTimer()
            {
                Manager.TimerFactory = (interval, onTick) =>
                {
                    var timer = new ManualPluginTimer(interval, onTick, strictStopWait: true);
                    lock (_gate) { Timers.Add(timer); }
                    return timer;
                };
            }

            /// <summary>
            /// 取生产计时器（真实 <see cref="System.Timers.Timer"/>）的内部实例，
            /// 用于断言真实计时器自身的状态（例如 Enabled）。
            /// </summary>
            public System.Timers.Timer InnerProductionTimer()
            {
                var field = typeof(PluginManager).GetField("_timers",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(field);
                var timers = (Dictionary<string, IPluginTimer>)field.GetValue(Manager);
                var timer = Assert.Single(timers.Values);
                return ((SystemPluginTimer)timer).Inner;
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
