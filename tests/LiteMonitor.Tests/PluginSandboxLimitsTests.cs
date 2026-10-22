using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [LM-009] 插件执行沙箱上限回归测试。
    ///
    /// ★ 缺陷：插件处理器（<c>src/Plugins/PluginProcessor.cs</c>）基线上
    ///   · 正则<b>无匹配时限</b>：灾难性回溯可以长期占用线程；
    ///   · 正则异常被 <c>catch { return val; }</c> <b>一并吞掉</b>，
    ///     调用方无法区分"正则没匹配上"与"正则跑炸了"；
    ///   · 步骤数 / 目标展开数 / 单次执行总时长 / 并发数 <b>都没有上限</b>。
    ///
    /// ★ 修复口径：六个上限全部落地为<b>具体数值</b>（见 <see cref="PluginSandboxLimits"/>），
    ///   触达上限一律是<b>可判定的失败</b>（带原因码的 <see cref="PluginSandboxException"/>），
    ///   <b>不</b>静默截断后当作成功；正则只吞"明确的超时"，其余异常向上传播。
    ///
    /// ★ 每条用例都必须<b>可证伪</b>：把对应上限去掉（或把缺陷改回去）后本用例必须失败。
    ///   反向验证证据见交付报告（在隔离副本 <c>.artifacts/w32</c> 中改与验）。
    /// </summary>
    public sealed class PluginSandboxLimitsTests
    {
        // ==========================================================
        // 1. 上限常量本身：具体数值，防"被改成 0 / 无限制"
        // ==========================================================

        [Fact]
        public void 上限常量_全部为具体数值且非零()
        {
            Assert.Equal(2 * 1024 * 1024, PluginSandboxLimits.MaxResponseBytes);
            Assert.Equal(500, PluginSandboxLimits.MaxRegexMatchTimeoutMs);
            Assert.Equal(16, PluginSandboxLimits.MaxStepsPerExecution);
            Assert.Equal(64, PluginSandboxLimits.MaxTargetsPerStep);
            Assert.Equal(30_000, PluginSandboxLimits.MaxExecutionDurationMs);
            Assert.Equal(8, PluginSandboxLimits.MaxConcurrentRequests);
            Assert.Equal(1_000, PluginSandboxLimits.ConcurrencyAcquireTimeoutMs);

            // 任何一项被改成 0（或负数）都视为"上限被抽掉"
            Assert.True(PluginSandboxLimits.MaxResponseBytes > 0, "响应字节上限必须为正");
            Assert.True(PluginSandboxLimits.MaxRegexMatchTimeoutMs > 0, "正则时限必须为正");
            Assert.True(PluginSandboxLimits.MaxStepsPerExecution > 0, "步骤数上限必须为正");
            Assert.True(PluginSandboxLimits.MaxTargetsPerStep > 0, "目标展开数上限必须为正");
            Assert.True(PluginSandboxLimits.MaxExecutionDurationMs > 0, "总时长上限必须为正");
            Assert.True(PluginSandboxLimits.MaxConcurrentRequests > 0, "并发上限必须为正");
            Assert.True(PluginSandboxLimits.ConcurrencyAcquireTimeoutMs > 0, "并发排队等待上限必须为正");

            // 正则时限的 TimeSpan 形式必须与毫秒常量一致
            Assert.Equal(
                TimeSpan.FromMilliseconds(PluginSandboxLimits.MaxRegexMatchTimeoutMs),
                PluginSandboxLimits.RegexMatchTimeout);
        }

        [Fact]
        public void 默认作用域_直接采用常量上限()
        {
            using var scope = new PluginSandboxScope();

            Assert.Equal(PluginSandboxLimits.MaxStepsPerExecution, scope.MaxSteps);
            Assert.Equal(PluginSandboxLimits.MaxTargetsPerStep, scope.MaxTargets);
            Assert.Equal(
                TimeSpan.FromMilliseconds(PluginSandboxLimits.MaxExecutionDurationMs),
                scope.MaxDuration);
        }

        // ==========================================================
        // 2. 响应字节数上限
        // ==========================================================

        [Fact]
        public async Task 响应体超限_该步骤失败_且不是静默截断成成功()
        {
            int limit = PluginSandboxLimits.MaxResponseBytes;

            using var scope = new PluginSandboxScope();

            // 超限 1 字节
            var payload = new byte[limit + 1];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + (i % 26));

            var ex = await Assert.ThrowsAsync<PluginSandboxException>(async () =>
            {
                await scope.RunStepAsync(_ => Task.FromResult<Stream>(new MemoryStream(payload)));
            });

            Assert.Equal(PluginSandboxReasons.ResponseTooLarge, ex.Reason);
            // 失败必须可判定：原因码 + 明确的上限数值
            Assert.Contains(limit.ToString(), ex.Message);

            // 对照组：刚好等于上限 → 成功，且内容完整（证明上限是真的在数字节）
            using var okScope = new PluginSandboxScope();
            var exact = new byte[limit];
            string text = await okScope.RunStepAsync(_ => Task.FromResult<Stream>(new MemoryStream(exact)));
            Assert.Equal(limit, text.Length);
        }

        [Fact]
        public async Task 响应体读取_超限时中止读取而不是读满全文()
        {
            // 计数流：记录实际被读走的字节数，证明超限后**没有**继续把正文读完
            var counting = new CountingStream(new byte[PluginSandboxLimits.MaxResponseBytes * 2]);

            await Assert.ThrowsAsync<PluginSandboxException>(async () =>
            {
                await PluginSandbox.ReadResponseBodyAsync(counting, PluginSandboxLimits.MaxResponseBytes, CancellationToken.None);
            });

            Assert.True(
                counting.BytesRead < counting.TotalLength,
                $"超限后仍读完了全文（读了 {counting.BytesRead} / {counting.TotalLength} 字节）");
        }

        // ==========================================================
        // 3. 正则匹配时限（灾难性回溯）
        // ==========================================================

        [Fact]
        public void 正则灾难性回溯_在时限内失败而不是长期占用线程()
        {
            // 经典指数回溯：^(a+)+$ 对 "aaaa...a!" 会走指数级分支
            var context = new Dictionary<string, string>
            {
                ["src"] = new string('a', 40) + "!"
            };

            var transforms = new List<PluginTransform>
            {
                new PluginTransform
                {
                    TargetVar = "out",
                    SourceVar = "src",
                    Function = "regex_replace",
                    Pattern = "^(a+)+$",
                    To = "X"
                }
            };

            var sw = Stopwatch.StartNew();
            var ex = Assert.Throws<PluginSandboxException>(() => PluginProcessor.ApplyTransforms(transforms, context));
            sw.Stop();

            Assert.Equal(PluginSandboxReasons.RegexTimeout, ex.Reason);
            // 时限是 500 ms；允许一定的调度余量，但绝不能是"跑到天荒地老"
            Assert.True(
                sw.ElapsedMilliseconds < 5000,
                $"灾难性回溯耗时 {sw.ElapsedMilliseconds} ms，说明正则时限没有真正生效");
            // 失败步骤不得产出"看起来成功"的值
            Assert.False(context.ContainsKey("out"), "超时失败后不得写入目标变量");
        }

        [Fact]
        public void 正则超时_在regex_match路径上同样是明确失败()
        {
            var context = new Dictionary<string, string>
            {
                ["src"] = new string('a', 40) + "!"
            };

            var transforms = new List<PluginTransform>
            {
                new PluginTransform
                {
                    TargetVar = "out",
                    SourceVar = "src",
                    Function = "regex_match",
                    Pattern = "^(a+)+$",
                    To = "1"
                }
            };

            var ex = Assert.Throws<PluginSandboxException>(() => PluginProcessor.ApplyTransforms(transforms, context));
            Assert.Equal(PluginSandboxReasons.RegexTimeout, ex.Reason);
            Assert.False(context.ContainsKey("out"));
        }

        [Fact]
        public void 正常正则变换_仍然可用_没有被新上限误伤()
        {
            var context = new Dictionary<string, string>
            {
                ["repo_path"] = "mojingjing-git/LiteMonitorPlus"
            };

            var transforms = new List<PluginTransform>
            {
                new PluginTransform
                {
                    TargetVar = "repo_short",
                    SourceVar = "repo_path",
                    Function = "regex_match",
                    Pattern = "([^/]+)$",
                    To = "1"
                }
            };

            PluginProcessor.ApplyTransforms(transforms, context);

            Assert.Equal("LiteMonitorPlus", context["repo_short"]);
        }

        // ==========================================================
        // 4. 步骤数上限
        // ==========================================================

        [Fact]
        public void 步骤数超限_失败()
        {
            using var scope = new PluginSandboxScope();

            for (int i = 0; i < PluginSandboxLimits.MaxStepsPerExecution; i++)
            {
                scope.EnsureStepAllowed(); // 上限之内：正常放行
            }

            var ex = Assert.Throws<PluginSandboxException>(() => scope.EnsureStepAllowed());

            Assert.Equal(PluginSandboxReasons.StepLimit, ex.Reason);
            Assert.Contains(PluginSandboxLimits.MaxStepsPerExecution.ToString(), ex.Message);
        }

        // ==========================================================
        // 4b. 三种计数口径的边界（S3.4：步骤数按目标计）
        // ==========================================================

        /// <summary>
        /// 步骤数**按目标计**：每个目标拥有独立的步骤预算（≤ 16）。
        /// <para>边界：第 16 步通过、第 17 步以 <c>LM009_STEP_LIMIT</c> 失败；
        /// 一个目标用满预算**不影响**其它目标（这正是"跨目标不累计"的含义）。</para>
        /// </summary>
        [Fact]
        public void 步骤数_按目标计_每个目标独立预算_第16步通过第17步失败()
        {
            using var scope = new PluginSandboxScope();
            using var t1 = scope.BeginTarget();
            using var t2 = scope.BeginTarget();

            // 第 16 步（含）通过
            for (int i = 0; i < PluginSandboxLimits.MaxStepsPerExecution; i++)
            {
                t1.EnsureStepAllowed();
            }
            Assert.Equal(PluginSandboxLimits.MaxStepsPerExecution, t1.StepCount);

            // 第 17 步失败
            var ex = Assert.Throws<PluginSandboxException>(() => t1.EnsureStepAllowed());
            Assert.Equal(PluginSandboxReasons.StepLimit, ex.Reason);
            Assert.Contains(PluginSandboxLimits.MaxStepsPerExecution.ToString(), ex.Message);

            // t2 的预算完全独立：仍可走满 16 步（第 17 步才失败）
            for (int i = 0; i < PluginSandboxLimits.MaxStepsPerExecution; i++)
            {
                t2.EnsureStepAllowed();
            }
            Assert.Equal(PluginSandboxLimits.MaxStepsPerExecution, t2.StepCount);
            Assert.Equal(
                PluginSandboxReasons.StepLimit,
                Assert.Throws<PluginSandboxException>(() => t2.EnsureStepAllowed()).Reason);
        }

        /// <summary>
        /// 目标展开数**按实例计**：目标级作用域上的计数累加到**实例级作用域**上，
        /// 一次执行内跨 target 累计；<b>累计值</b>超过上限即报 <c>LM009_TARGET_LIMIT</c>。
        /// <para>[LM-040] 判定依据必须是<b>累计值</b>（不是本次传入的展开数）：
        /// 旧实现比较的是入参 ⇒ "每次都传一个 ≤ 64 的数字"就能把累计值撑到 70 而不报错，
        /// "跨目标累计 ≤ 64"只是文档说法、并非强制约束。</para>
        /// </summary>
        [Fact]
        public void 目标展开数_按实例计_跨目标累计超上限即失败()
        {
            using var scope = new PluginSandboxScope();
            using var t1 = scope.BeginTarget();
            using var t2 = scope.BeginTarget();

            // 单次展开 40 个目标（累计 40，未达上限）
            scope.EnsureTargetsAllowed(40);
            Assert.Equal(40, scope.TargetCount);

            // 目标级作用域的展开数**累加到实例级作用域**上：40 + 24 = 64（恰好等于上限，放行）
            t1.EnsureTargetsAllowed(24);
            Assert.Equal(64, scope.TargetCount);
            Assert.Equal(64, t2.TargetCount);

            // 再展开 1 个 ⇒ 累计 65 > 64 ⇒ **必须失败**（LM-040：判定依据是累计值）
            var ex = Assert.Throws<PluginSandboxException>(() => t2.EnsureTargetsAllowed(1));
            Assert.Equal(PluginSandboxReasons.TargetLimit, ex.Reason);
            Assert.Contains(PluginSandboxLimits.MaxTargetsPerStep.ToString(), ex.Message);
            Assert.NotEqual(PluginSandboxReasons.StepLimit, ex.Reason);

            // 累计值确实被记下来了（不是"只判单次"）
            Assert.Equal(65, scope.TargetCount);
        }

        /// <summary>
        /// [LM-044] 生产默认总时长必须等于契约值 30 秒。
        /// <para>生产入口 <c>PluginExecutor.ExecuteInstanceAsync</c> 用的是<b>默认构造</b>，
        /// 因此"默认构造的总时长"就是"生产入口实际使用的总时长"；
        /// 这里把它钉死，防止生产默认时长被悄悄改成 5 分钟。</para>
        /// </summary>
        [Fact]
        public void 生产默认总时长_等于契约值30秒()
        {
            // 契约值本身（字面量，防"被改成别的数"）
            Assert.Equal(30_000, PluginSandboxLimits.MaxExecutionDurationMs);
            Assert.Equal(TimeSpan.FromMilliseconds(30_000), PluginSandboxScope.DefaultExecutionDuration);

            // 生产入口走的默认构造 ⇒ 其总时长必须与契约值一致
            using var scope = new PluginSandboxScope();
            Assert.Equal(PluginSandboxLimits.MaxExecutionDurationMs, (int)scope.MaxDuration.TotalMilliseconds);
            Assert.Equal(TimeSpan.FromMilliseconds(30_000), scope.MaxDuration);

            // 目标级作用域继承实例级的总时长（不得另有出处）
            using var target = scope.BeginTarget();
            Assert.Equal(scope.MaxDuration, target.MaxDuration);
        }

        /// <summary>
        /// 总时长**按实例计**：目标级作用域与实例级作用域**共用同一个截止时刻与取消令牌**
        /// ⇒ 总时长在一次执行内跨目标累计（而不是每个目标各算一份）。
        /// </summary>
        [Fact]
        public void 总时长_按实例计_跨目标共享同一截止时刻与令牌()
        {
            using var scope = new PluginSandboxScope(
                CancellationToken.None,
                TimeSpan.FromMilliseconds(150),
                PluginSandboxLimits.MaxStepsPerExecution,
                PluginSandboxLimits.MaxTargetsPerStep);

            using var t1 = scope.BeginTarget();
            using var t2 = scope.BeginTarget();

            // 同一截止时刻、同一令牌（⇒ 一个 target 结束不会释放整次执行的令牌）
            Assert.Equal(scope.DeadlineUtc, t1.DeadlineUtc);
            Assert.Equal(scope.DeadlineUtc, t2.DeadlineUtc);
            Assert.Equal(scope.Token, t1.Token);
            Assert.Equal(scope.Token, t2.Token);
            Assert.Same(scope, t1.Owner);
            Assert.Same(scope, t2.Owner);

            Thread.Sleep(250);

            // 目标级作用域也按**实例级**截止时刻判定到期
            var ex = Assert.Throws<PluginSandboxException>(() => t2.EnsureNotExpired());
            Assert.Equal(PluginSandboxReasons.DeadlineExceeded, ex.Reason);
            Assert.True(scope.IsExpired);
        }

        /// <summary>
        /// 目标级作用域被释放时，**不得**连带释放实例级作用域的取消令牌
        /// （否则一个 target 结束就会打断其它 target 的请求）。
        /// </summary>
        [Fact]
        public void 目标级作用域释放_不释放实例级作用域的令牌()
        {
            using var scope = new PluginSandboxScope();

            var target = scope.BeginTarget();
            target.EnsureStepAllowed();
            target.Dispose();

            Assert.False(scope.Token.IsCancellationRequested, "目标级作用域释放后实例级令牌被误释放");
            // 实例级作用域仍然可用
            scope.EnsureStepAllowed();
            scope.EnsureNotExpired();
            Assert.False(scope.Token.IsCancellationRequested);
        }

        // ==========================================================
        // 5. 目标展开数上限
        // ==========================================================

        [Fact]
        public void 目标展开数超限_失败()
        {
            using var scope = new PluginSandboxScope();

            scope.EnsureTargetsAllowed(PluginSandboxLimits.MaxTargetsPerStep); // 上限之内：正常放行

            var ex = Assert.Throws<PluginSandboxException>(
                () => scope.EnsureTargetsAllowed(PluginSandboxLimits.MaxTargetsPerStep + 1));

            Assert.Equal(PluginSandboxReasons.TargetLimit, ex.Reason);
            Assert.Contains(PluginSandboxLimits.MaxTargetsPerStep.ToString(), ex.Message);
        }

        // ==========================================================
        // 6. 单次执行总时长上限
        // ==========================================================

        [Fact]
        public async Task 单次执行总时长超限_失败()
        {
            using var cts = new CancellationTokenSource();
            using var scope = new PluginSandboxScope(
                cts.Token,
                TimeSpan.FromMilliseconds(300),
                PluginSandboxLimits.MaxStepsPerExecution,
                PluginSandboxLimits.MaxTargetsPerStep);

            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<PluginSandboxException>(async () =>
            {
                // 模拟"网络等待"：一直挂着，直到作用域的期限到期把它取消
                await scope.RunAsync(async token =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                    return 1;
                });
            });
            sw.Stop();

            Assert.Equal(PluginSandboxReasons.DeadlineExceeded, ex.Reason);
            Assert.True(
                sw.ElapsedMilliseconds < 5000,
                $"总时长上限没有生效（耗时 {sw.ElapsedMilliseconds} ms）");
        }

        [Fact]
        public void 总时长到期_主动检查也能判定失败()
        {
            using var scope = new PluginSandboxScope(
                CancellationToken.None,
                TimeSpan.FromMilliseconds(120),
                PluginSandboxLimits.MaxStepsPerExecution,
                PluginSandboxLimits.MaxTargetsPerStep);

            Thread.Sleep(200);

            var ex = Assert.Throws<PluginSandboxException>(() => scope.EnsureNotExpired());
            Assert.Equal(PluginSandboxReasons.DeadlineExceeded, ex.Reason);
            Assert.True(scope.IsExpired);
        }

        // ==========================================================
        // 7. 并发数上限
        // ==========================================================

        [Fact]
        public async Task 并发数超限_超出部分明确失败而不是无限排队()
        {
            var slots = new List<IDisposable>();
            try
            {
                for (int i = 0; i < PluginSandboxLimits.MaxConcurrentRequests; i++)
                {
                    slots.Add(await PluginSandbox.AcquireRequestSlotAsync(CancellationToken.None));
                }

                Assert.Equal(0, PluginSandbox.AvailableRequestSlots);

                var sw = Stopwatch.StartNew();
                var ex = await Assert.ThrowsAsync<PluginSandboxException>(async () =>
                {
                    await PluginSandbox.AcquireRequestSlotAsync(CancellationToken.None);
                });
                sw.Stop();

                Assert.Equal(PluginSandboxReasons.ConcurrencyLimit, ex.Reason);
                // 失败前应当先把排队等待用完（行为确定：排队 → 等不到 → 明确失败）
                Assert.True(
                    sw.ElapsedMilliseconds >= PluginSandboxLimits.ConcurrencyAcquireTimeoutMs - 200,
                    $"并发超限未按预期等待排队超时就失败（耗时 {sw.ElapsedMilliseconds} ms）");
            }
            finally
            {
                foreach (var slot in slots) slot.Dispose();
            }

            Assert.Equal(PluginSandboxLimits.MaxConcurrentRequests, PluginSandbox.AvailableRequestSlots);
        }

        [Fact]
        public async Task 并发闸门_实际并发数不超过上限()
        {
            int current = 0;
            int max = 0;

            async Task Work()
            {
                int now = Interlocked.Increment(ref current);
                int observed;
                do
                {
                    observed = Volatile.Read(ref max);
                    if (now <= observed) break;
                }
                while (Interlocked.CompareExchange(ref max, now, observed) != observed);

                await Task.Delay(30);

                Interlocked.Decrement(ref current);
            }

            var tasks = new List<Task>();
            for (int i = 0; i < 24; i++)
            {
                tasks.Add(Task.Run(async () => await PluginSandbox.RunBoundedRequestAsync(
                    async () => { await Work(); return 0; }, CancellationToken.None)));
            }

            await Task.WhenAll(tasks);

            Assert.True(
                max <= PluginSandboxLimits.MaxConcurrentRequests,
                $"观测到的最大并发 {max} 超过上限 {PluginSandboxLimits.MaxConcurrentRequests}");
            Assert.Equal(PluginSandboxLimits.MaxConcurrentRequests, PluginSandbox.AvailableRequestSlots);
        }

        // ==========================================================
        // 8. 取消可响应
        // ==========================================================

        [Fact]
        public async Task 取消可响应_执行中途取消及时结束_且不产生未观察异常()
        {
            var unobserved = new List<Exception>();
            void Handler(object sender, UnobservedTaskExceptionEventArgs args)
            {
                lock (unobserved) unobserved.Add(args.Exception);
                args.SetObserved();
            }

            TaskScheduler.UnobservedTaskException += Handler;
            try
            {
                using var cts = new CancellationTokenSource();
                using var scope = new PluginSandboxScope(cts.Token);

                // 正文读取会一直阻塞，直到令牌被取消 —— 模拟"超大正文 + 取消"
                var task = scope.RunStepAsync(_ => Task.FromResult<Stream>(new BlockingStream()));

                await Task.Delay(150);
                var sw = Stopwatch.StartNew();
                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await task; });
                sw.Stop();

                Assert.True(
                    sw.ElapsedMilliseconds < 5000,
                    $"取消未及时生效（等待了 {sw.ElapsedMilliseconds} ms）");

                // 未观察异常：强制 GC 后仍不应出现
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                lock (unobserved)
                {
                    Assert.Empty(unobserved);
                }
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= Handler;
            }
        }

        // ==========================================================
        // 9. 非超时异常不再被吞
        // ==========================================================

        [Fact]
        public void 非超时异常_向上传播而不是被静默转成原值()
        {
            var context = new Dictionary<string, string> { ["src"] = "hello" };

            var transforms = new List<PluginTransform>
            {
                new PluginTransform
                {
                    TargetVar = "out",
                    SourceVar = "src",
                    Function = "regex_replace",
                    Pattern = "[",   // 非法 pattern → RegexParseException（ArgumentException 子类）
                    To = "X"
                }
            };

            var ex = Assert.ThrowsAny<ArgumentException>(() => PluginProcessor.ApplyTransforms(transforms, context));

            Assert.IsType<RegexParseException>(ex);
            // 关键：不得被 `catch { return val; }` 吞掉后当作成功（原值 "hello" 写进了目标变量）
            Assert.False(context.ContainsKey("out"), "异常被吞掉后仍写入了目标变量，等同于静默成功");
        }

        [Fact]
        public void 非超时异常_空pattern同样向上传播()
        {
            var context = new Dictionary<string, string> { ["src"] = "hello" };

            var transforms = new List<PluginTransform>
            {
                new PluginTransform
                {
                    TargetVar = "out",
                    SourceVar = "src",
                    Function = "regex_match",
                    Pattern = null,   // 非法 pattern → ArgumentNullException
                    To = "1"
                }
            };

            Assert.ThrowsAny<ArgumentException>(() => PluginProcessor.ApplyTransforms(transforms, context));
            Assert.False(context.ContainsKey("out"));
        }

        // ==========================================================
        // 10. 凭据只发往插件显式声明的目标主机
        // ==========================================================

        [Fact]
        public void API_Key_不得发往未声明的目标主机()
        {
            var tmpl = BuildTemplate("https://api.example.com/v1/quote");

            var declared = PluginProcessor.GetDeclaredHosts(tmpl);
            Assert.Contains("api.example.com", declared);

            var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer SECRET-TOKEN" };

            // 未声明的主机 → 拒绝，凭据不外发
            var ex = Assert.Throws<PluginSandboxException>(() =>
                PluginSandbox.EnsureCredentialTargetAllowed("https://evil.example.net/steal", headers, declared));

            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);
            // 失败信息里不得出现凭据本身
            Assert.DoesNotContain("SECRET-TOKEN", ex.Message, StringComparison.Ordinal);

            // 已声明的主机 → 放行
            PluginSandbox.EnsureCredentialTargetAllowed("https://api.example.com/v1/quote", headers, declared);

            // 不带凭据的普通请求不受该限制
            PluginSandbox.EnsureCredentialTargetAllowed(
                "https://evil.example.net/steal",
                new Dictionary<string, string> { ["Accept"] = "application/json" },
                declared);
        }

        [Fact]
        public void 目标主机来自模板变量_不构成显式声明_凭据请求一律拒绝()
        {
            var tmpl = BuildTemplate("{{target_url}}");

            Assert.Empty(PluginProcessor.GetDeclaredHosts(tmpl));

            var headers = new Dictionary<string, string> { ["X-Api-Key"] = "SECRET-TOKEN" };

            var ex = Assert.Throws<PluginSandboxException>(() =>
                PluginSandbox.EnsureCredentialTargetAllowed("https://api.example.com/v1/quote", headers, tmpl));

            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);
        }

        [Fact]
        public void 凭据识别_覆盖常见凭据头且不误伤普通头()
        {
            Assert.True(PluginSandbox.IsCredentialHeaderName("Authorization"));
            Assert.True(PluginSandbox.IsCredentialHeaderName("cookie"));
            Assert.True(PluginSandbox.IsCredentialHeaderName("X-Api-Key"));
            Assert.False(PluginSandbox.IsCredentialHeaderName("Accept"));
            Assert.False(PluginSandbox.IsCredentialHeaderName("User-Agent"));
            Assert.False(PluginSandbox.IsCredentialHeaderName("Content-Type"));
        }

        // ==========================================================
        // 11. 插件处理器不得进入高权限命令执行入口
        // ==========================================================

        [Fact]
        public void 插件处理器_不含任何外部命令执行入口()
        {
            string source = File.ReadAllText(ResolvePluginProcessorSourcePath());

            foreach (string forbidden in new[]
            {
                "Process.Start",
                "System.Diagnostics.Process",
                "cmd.exe",
                "powershell",
                "ShellExecute",
                "CreateProcess"
            })
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ==========================================================
        // 辅助
        // ==========================================================

        private static PluginTemplate BuildTemplate(string stepUrl)
        {
            return new PluginTemplate
            {
                Id = "sandbox.demo",
                Execution = new PluginExecution
                {
                    Type = "chain",
                    Url = "",
                    Steps = new List<PluginExecutionStep>
                    {
                        new PluginExecutionStep
                        {
                            Id = "s1",
                            Url = stepUrl,
                            Method = "GET"
                        }
                    }
                }
            };
        }

        /// <summary>定位仓库内的插件处理器源码（只读，用于结构护栏）。</summary>
        private static string ResolvePluginProcessorSourcePath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "src", "Plugins", "PluginProcessor.cs");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("找不到 src/Plugins/PluginProcessor.cs");
        }

        /// <summary>会一直阻塞到令牌取消的流（模拟超大正文 / 慢网络）。</summary>
        private sealed class BlockingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>记录实际被读走字节数的内存流。</summary>
        private sealed class CountingStream : Stream
        {
            private readonly byte[] _content;
            private int _position;

            public CountingStream(byte[] content)
            {
                _content = content;
            }

            public int TotalLength => _content.Length;
            public int BytesRead => _position;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _content.Length;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = Math.Min(count, _content.Length - _position);
                Array.Copy(_content, _position, buffer, offset, n);
                _position += n;
                return n;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Read(buffer, offset, count));
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
