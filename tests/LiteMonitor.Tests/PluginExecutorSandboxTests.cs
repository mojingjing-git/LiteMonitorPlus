using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using Xunit;
using Xunit.Abstractions;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [LM-009] 插件沙箱**接入执行路径**的端到端回归测试。
    ///
    /// ★ 背景：S3.2 只落地了沙箱**原语**（<c>PluginSandboxLimits</c> / <c>PluginSandboxScope</c> /
    ///   <c>PluginSandbox</c>），而 <c>PluginExecutor</c> 的真实执行链路完全没有引用它们 ——
    ///   六个上限在真实插件执行时**一个都不会生效**。S3.3 把它们接进执行路径，本文件验证"接上了"。
    ///
    /// ★ 判别力要求：每条用例都必须能证明"**执行路径**上的限制"，而不是"直接调用沙箱原语"的限制。
    ///   因此本文件一律通过 <see cref="PluginExecutor"/> 的**生产入口**驱动：
    ///   · 公开入口 <c>ExecuteInstanceAsync</c>（实例级作用域、目标数上限、凭据校验、步骤链）；
    ///   · 私有入口 <c>ExecuteStepAsync</c> / <c>ExecuteStepWithScopeAsync</c> / <c>ExecuteInstanceWithScopeAsync</c>
    ///     （与生产路径同一条代码，仅用于缩短总时长上限以便快速复现）。
    ///   变体证据见交付报告（隔离副本 <c>.artifacts/wt-s33</c> 中改与验）。
    /// </summary>
    public sealed class PluginExecutorSandboxTests
    {
        private readonly ITestOutputHelper _output;

        public PluginExecutorSandboxTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ==========================================================
        // 1. 响应体字节上限：生效上限 = min(实例级上限, 沙箱硬天花板 2 MiB)
        // ==========================================================

        /// <summary>
        /// 端到端：响应体超过**沙箱硬天花板**（2 MiB）时，经公开入口 <c>ExecuteInstanceAsync</c>
        /// 执行必须失败，且原因码可判定为 <c>LM009_RESPONSE_TOO_LARGE</c>。
        /// <para>实例级上限默认 8 MiB —— 若没有接沙箱，这次执行会**成功**（3 MiB 响应被接受），
        /// 因此本用例直接证明"沙箱天花板在执行路径上生效"。</para>
        /// </summary>
        [Fact]
        public async Task 端到端_响应体超沙箱天花板_失败且原因码可判定()
        {
            var payload = new byte[PluginSandboxLimits.MaxResponseBytes + 4096];
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Bytes(payload)));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-resp-sandbox");
            var tmpl = ChainTemplate(server.Url + "/big");

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(() => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.ResponseTooLarge, ex.Reason);

            // 生效的上限是沙箱天花板（2 MiB），而不是实例级的 8 MiB
            var tooLarge = Assert.IsType<PluginResponseTooLargeException>(ex);
            Assert.Equal(PluginSandboxLimits.MaxResponseBytes, tooLarge.Limit);
            Assert.Equal(2 * 1024 * 1024, tooLarge.Limit);

            // 两条响应体超限路径统一到同一异常族
            Assert.IsAssignableFrom<PluginSandboxException>(tooLarge);
        }

        /// <summary>
        /// 端到端：实例级上限**更严**时先触发（min 规则的另一侧），异常类型与既有 LM-003a 行为一致。
        /// </summary>
        [Fact]
        public async Task 端到端_实例级上限更严时先触发_抛LM003a异常()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Bytes(new byte[64 * 1024])));
            using var exec = new PluginExecutor();
            SetField(exec, "_maxResponseBytes", 1024);

            var inst = NewInstance("inst-resp-instance");
            var tmpl = ChainTemplate(server.Url + "/big");

            var ex = await Assert.ThrowsAsync<PluginResponseTooLargeException>(() => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(1024, ex.Limit);
            Assert.NotNull(ex.DeclaredLength);
            // 原因码口径与沙箱路径一致
            Assert.Equal(PluginSandboxReasons.ResponseTooLarge, ex.Reason);
        }

        /// <summary>
        /// 回归（LM-003a）：既有"步骤执行路径"上的实例级上限行为**未被破坏** ——
        /// 仍抛 <see cref="PluginResponseTooLargeException"/>（精确类型），上限仍是 1024。
        /// </summary>
        [Fact]
        public async Task 回归_实例级响应上限在步骤路径上仍抛原异常()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Bytes(new byte[64 * 1024])));
            using var exec = new PluginExecutor();
            SetField(exec, "_maxResponseBytes", 1024);

            var ex = await Assert.ThrowsAsync<PluginResponseTooLargeException>(
                () => RunStepAsync(exec, NewInstance("inst-resp-legacy"), NewStep(server.Url + "/big")));

            Assert.Equal(1024, ex.Limit);
            Assert.NotNull(ex.DeclaredLength);
            Assert.Equal(PluginSandboxReasons.ResponseTooLarge, ex.Reason);
        }

        // ==========================================================
        // 2. 步骤数上限（实例内累计，native:// 步骤同样计入）
        // ==========================================================

        /// <summary>
        /// 端到端：链式步骤数超过 16 时失败，原因码 <c>LM009_STEP_LIMIT</c>；
        /// 且第 17 步**没有发出请求**（上限在步骤开始前判定）。
        /// </summary>
        [Fact]
        public async Task 端到端_步骤数超限_失败且原因码为LM009_STEP_LIMIT()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-steps");
            var tmpl = ChainTemplate(server.Url + "/s", PluginSandboxLimits.MaxStepsPerExecution + 1);

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(() => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.StepLimit, ex.Reason);
            // 前 16 步已执行，第 17 步在发请求前被拒
            Assert.Equal(PluginSandboxLimits.MaxStepsPerExecution, server.RequestCount);
        }

        /// <summary>
        /// 端到端：步骤数**恰好等于**上限（16）时不失败 —— 证明上限不是"少一步"的偏保守值。
        /// </summary>
        [Fact]
        public async Task 端到端_步骤数恰好等于上限_正常成功()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-steps-ok");
            var tmpl = ChainTemplate(server.Url + "/s", PluginSandboxLimits.MaxStepsPerExecution);

            Assert.True(await exec.ExecuteInstanceAsync(inst, tmpl));
            Assert.Equal(PluginSandboxLimits.MaxStepsPerExecution, server.RequestCount);
        }

        /// <summary>
        /// native:// 步骤不发 HTTP、不占并发槽，但**同样计入步骤数上限**。
        /// <para>这里直接逐步驱动步骤执行（每次驱动一个步骤，失败不中断后续驱动），
        /// 用一个必然失败的 native 处理器（未知 host）消耗步骤预算：前 16 次是"未知 native host"，
        /// 第 17 次必须是沙箱步骤数上限。</para>
        /// </summary>
        [Fact]
        public async Task native步骤_同样计入步骤数上限()
        {
            using var exec = new PluginExecutor();
            using var scope = new PluginSandboxScope();

            var inst = NewInstance("inst-native-steps");
            var step = NewStep("native://unknown-native-host");

            for (int i = 0; i < PluginSandboxLimits.MaxStepsPerExecution; i++)
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(
                    () => RunStepWithScopeAsync(exec, inst, step, scope, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                Assert.IsNotType<PluginSandboxException>(ex);
            }

            var limitEx = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => RunStepWithScopeAsync(exec, inst, step, scope, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            Assert.Equal(PluginSandboxReasons.StepLimit, limitEx.Reason);
        }

        // ==========================================================
        // 3. 目标展开数上限
        // ==========================================================

        /// <summary>
        /// 端到端：目标数超过 64 时失败，原因码 <c>LM009_TARGET_LIMIT</c>，
        /// 且**一条请求都不应发出**（上限在入口判定，先于任何请求）。
        /// </summary>
        [Fact]
        public async Task 端到端_目标数超限_失败且原因码为LM009_TARGET_LIMIT()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-targets");
            inst.Targets = Enumerable.Range(0, PluginSandboxLimits.MaxTargetsPerStep + 1)
                .Select(i => new Dictionary<string, string> { ["n"] = i.ToString() })
                .ToList();

            var tmpl = ChainTemplate(server.Url + "/t");

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(() => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.TargetLimit, ex.Reason);
            Assert.Equal(0, server.RequestCount);
        }

        /// <summary>
        /// 端到端：目标数**恰好等于**上限（64）时，目标上限**不得**触发。
        /// <para>注意两个上限的交互：64 个目标 × 1 步 = 64 步，而步骤数上限（16）是
        /// <b>实例内累计</b>的（每个 target 的步骤链都计入同一份预算）⇒ 本次执行会以
        /// "步骤数上限"失败。这正是"目标上限（64）没有触发"的证据。</para>
        /// </summary>
        [Fact]
        public async Task 端到端_目标数恰好等于上限_目标上限不触发()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-targets-ok");
            inst.Targets = Enumerable.Range(0, PluginSandboxLimits.MaxTargetsPerStep)
                .Select(i => new Dictionary<string, string> { ["n"] = i.ToString() })
                .ToList();

            var tmpl = ChainTemplate(server.Url + "/t");

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(() => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.NotEqual(PluginSandboxReasons.TargetLimit, ex.Reason);
            Assert.Equal(PluginSandboxReasons.StepLimit, ex.Reason);
            // 目标上限放行 ⇒ 请求确实发出去了（不是被目标上限挡下的）
            Assert.True(server.RequestCount > 0, "目标数等于上限时请求必须照常发出");
        }

        // ==========================================================
        // 4. 单次执行总时长上限
        // ==========================================================

        /// <summary>
        /// 端到端：单次执行总时长到期时失败，原因码 <c>LM009_DEADLINE_EXCEEDED</c>。
        /// <para>用缩短后的作用域上限（400 ms）复现（生产值是 30 s，见 <see cref="PluginSandboxLimits"/>），
        /// 作用域通过与生产同一条路径 <c>ExecuteInstanceWithScopeAsync</c> 传入。</para>
        /// <para>外部超时保护：整条用例最多等 30 s，绝不挂死。</para>
        /// </summary>
        [Fact]
        public async Task 端到端_总时长超限_失败且原因码为LM009_DEADLINE_EXCEEDED()
        {
            using var server = new TinyHttpServer(async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                return TestResponse.Text("slow");
            });
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-deadline");
            var tmpl = ChainTemplate(server.Url + "/slow");

            using var scope = new PluginSandboxScope(
                CancellationToken.None,
                TimeSpan.FromMilliseconds(400),
                PluginSandboxLimits.MaxStepsPerExecution,
                PluginSandboxLimits.MaxTargetsPerStep);

            var task = RunInstanceWithScopeAsync(exec, inst, tmpl, scope,
                PluginProcessor.GetDeclaredHosts(tmpl));

            var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(ReferenceEquals(finished, task), "总时长上限未在预期时间内生效（用例可能挂死）");

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(() => task);
            Assert.Equal(PluginSandboxReasons.DeadlineExceeded, ex.Reason);
        }

        // ==========================================================
        // 5. 并发上限（进程级 8 槽）
        // ==========================================================

        /// <summary>
        /// 端到端：并发请求数超过 8 时失败，原因码 <c>LM009_CONCURRENCY_LIMIT</c>，
        /// 且**只有 8 条请求真的发出**（第 9 条在拿到槽位之前就被拒）。
        /// </summary>
        [Fact]
        public async Task 端到端_并发超限_失败且原因码为LM009_CONCURRENCY_LIMIT()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            using var exec = new PluginExecutor();

            // 占住响应：让前 8 个请求一直持有并发槽位
            server.ArmHold();

            int total = PluginSandboxLimits.MaxConcurrentRequests + 1;
            var tasks = new List<Task>();
            for (int i = 0; i < total; i++)
            {
                int n = i;
                tasks.Add(RunStepAsync(exec, NewInstance("inst-conc-" + n), NewStep(server.Url + "/c" + n)));
            }
            var outcomes = tasks.Select(CaptureAsync).ToList();

            // 等前 8 条请求真的到达服务端（它们各自占着一个槽位）
            await WaitUntilAsync(() => server.RequestCount >= PluginSandboxLimits.MaxConcurrentRequests, TimeSpan.FromSeconds(20));
            Assert.Equal(PluginSandboxLimits.MaxConcurrentRequests, server.RequestCount);

            // 第 9 个请求等不到槽位（排队上限 1 s）→ 明确失败
            await WaitUntilAsync(() => outcomes[total - 1].IsCompleted, TimeSpan.FromSeconds(15));
            Assert.True(outcomes[total - 1].IsCompleted, "第 9 个请求未在排队上限内明确失败");

            // 放行其余 8 条
            server.ReleaseHeld();

            var results = await Task.WhenAll(outcomes);

            var failures = results.Where(ex => ex != null).ToList();
            Assert.Single(failures);
            var sandbox = Assert.IsAssignableFrom<PluginSandboxException>(failures[0]);
            Assert.Equal(PluginSandboxReasons.ConcurrencyLimit, sandbox.Reason);

            // 只有 8 条请求真的发出（超限的那条在发请求前就被拒）
            Assert.Equal(PluginSandboxLimits.MaxConcurrentRequests, server.RequestCount);

            // 槽位全部归还
            Assert.Equal(PluginSandboxLimits.MaxConcurrentRequests, PluginSandbox.AvailableRequestSlots);
        }

        // ==========================================================
        // 6. 凭据目标校验
        // ==========================================================

        /// <summary>
        /// 端到端：带凭据的请求目标主机**不在**插件显式声明集合内（主机来自用户填写的模板）时，
        /// 请求**不得发出**（服务端一条都没收到），原因码 <c>LM009_CREDENTIAL_TARGET_NOT_ALLOWED</c>。
        /// </summary>
        [Fact]
        public async Task 端到端_凭据请求目标未声明_请求不发出且原因码可判定()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-cred-deny");
            // 目标主机来自用户填写的模板 ⇒ 插件并未声明它
            var tmpl = ChainTemplate("http://{{target}}/x");
            tmpl.Inputs = new List<PluginInput>
            {
                new PluginInput { Key = "target", DefaultValue = "127.0.0.1:" + server.Port }
            };
            tmpl.Execution.Steps[0].Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer super-secret-value"
            };

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(() => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);
            // 请求根本没发出去 ⇒ 凭据没有外泄
            Assert.Equal(0, server.RequestCount);
        }

        /// <summary>
        /// 端到端：带凭据的请求目标主机**在**插件显式声明集合内时正常执行（凭据照常发送）。
        /// </summary>
        [Fact]
        public async Task 端到端_凭据请求目标已声明_正常发出并成功()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-cred-allow");
            // 字面量主机 ⇒ 构成插件的显式声明
            var tmpl = ChainTemplate(server.Url + "/ok");
            tmpl.Execution.Steps[0].Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer super-secret-value"
            };

            Assert.True(await exec.ExecuteInstanceAsync(inst, tmpl));
            Assert.Equal(1, server.RequestCount);
            Assert.True(server.Requests[0].Headers.ContainsKey("Authorization"));
        }

        // ==========================================================
        // 7. 正则超时（执行路径可达性）
        // ==========================================================

        /// <summary>
        /// 端到端：变换里跑灾难性回溯正则时失败，原因码 <c>LM009_REGEX_TIMEOUT</c>。
        /// <para>证明 <c>PluginProcessor</c> 侧的"正则时限"经由**执行路径**真正可达
        /// （不是只调用原语才有）。</para>
        /// </summary>
        [Fact]
        public async Task 端到端_正则超时_失败且原因码为LM009_REGEX_TIMEOUT()
        {
            string payload = new string('a', 40) + "!";
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text(payload)));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-regex");
            var tmpl = ChainTemplate(server.Url + "/re");
            tmpl.Execution.Steps[0].Process = new List<PluginTransform>
            {
                new PluginTransform
                {
                    TargetVar = "out",
                    SourceVar = "v",
                    Function = "regex_replace",
                    Pattern = "^(a+)+$",
                    To = "X"
                }
            };

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(() => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.RegexTimeout, ex.Reason);
        }

        // ==========================================================
        // 7. 变换循环受总时长约束（裁决 5）
        // ==========================================================

        /// <summary>
        /// 端到端：变换循环必须受作用域令牌约束 —— 总时长到期时中止变换循环，
        /// 原因码 <c>LM009_DEADLINE_EXCEEDED</c>。
        /// <para>若变换仍走"不带 token"的重载，循环不会被打断 ⇒ 步骤会"成功"返回 ⇒ 本用例失败。</para>
        /// </summary>
        [Fact]
        public async Task 端到端_变换循环被总时长上限打断_原因码为LM009_DEADLINE_EXCEEDED()
        {
            using var server = new TinyHttpServer(async _ =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
                return TestResponse.Text(new string('a', 20000));
            });
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-transform");
            var tmpl = ChainTemplate(server.Url + "/tr");

            // 大量变换：循环耗时远大于剩余预算（变换不被中断时它会跑好几秒）
            var transforms = new List<PluginTransform>();
            for (int i = 0; i < 100_000; i++)
            {
                transforms.Add(new PluginTransform
                {
                    TargetVar = "v",
                    SourceVar = "v",
                    Function = "regex_replace",
                    Pattern = "a{5,}",
                    To = "$0"
                });
            }
            tmpl.Execution.Steps[0].Process = transforms;

            using var scope = new PluginSandboxScope(
                CancellationToken.None,
                TimeSpan.FromMilliseconds(300),
                PluginSandboxLimits.MaxStepsPerExecution,
                PluginSandboxLimits.MaxTargetsPerStep);

            var task = RunInstanceWithScopeAsync(exec, inst, tmpl, scope,
                PluginProcessor.GetDeclaredHosts(tmpl));

            var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60)));
            Assert.True(ReferenceEquals(finished, task), "总时长上限未在预期时间内生效（用例可能挂死）");

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(() => task);
            Assert.Equal(PluginSandboxReasons.DeadlineExceeded, ex.Reason);
        }

        // ==========================================================
        // 8. 内置插件影响评估（实测声明主机集合）
        // ==========================================================

        /// <summary>
        /// 实测全部内置插件（<c>resources/plugins/*.json</c>）的**声明主机集合**，
        /// 并核对是否存在"凭据请求 + 目标主机来自用户填写模板"的组合 ——
        /// 那种组合会被新的凭据目标校验拒绝（这是 LM-009 的设计意图）。
        /// </summary>
        [Fact]
        public void 内置插件_声明主机集合与凭据组合实测()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "resources", "plugins");
            Assert.True(Directory.Exists(dir), $"未找到内置插件目录：{dir}");

            var files = Directory.GetFiles(dir, "*.json")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.NotEmpty(files);

            var lines = new List<string>();
            int credentialStepCount = 0;

            foreach (var file in files)
            {
                var tmpl = JsonSerializer.Deserialize<PluginTemplate>(File.ReadAllText(file));
                Assert.NotNull(tmpl);

                var declared = PluginProcessor.GetDeclaredHosts(tmpl);
                lines.Add($"{tmpl.Id,-14} 声明主机=[{(declared.Count == 0 ? "(空)" : string.Join(", ", declared.OrderBy(h => h, StringComparer.OrdinalIgnoreCase)))}]");

                var steps = tmpl.Execution?.Steps ?? new List<PluginExecutionStep>();
                foreach (var step in steps)
                {
                    if (!PluginSandbox.HasCredentialHeader(step.Headers)) continue;
                    credentialStepCount++;

                    // 不变式：带凭据的步骤，其目标主机必须是插件**显式声明**的
                    // （主机来自模板 ⇒ 未声明 ⇒ 会被拒绝）
                    Assert.False(HostFromTemplate(step.Url),
                        $"内置插件 {tmpl.Id} 的步骤 {step.Id} 带凭据，但目标主机来自用户填写的模板" +
                        $"（{step.Url}）—— 会被 LM009_CREDENTIAL_TARGET_NOT_ALLOWED 拒绝");
                }
            }

            _output.WriteLine("内置插件声明主机集合实测：");
            foreach (var line in lines) _output.WriteLine("  " + line);
            _output.WriteLine($"带凭据头的内置步骤数：{credentialStepCount}");

            // 当前内置插件**没有任何**带凭据头的步骤 —— 新校验对内置插件零影响
            Assert.Equal(0, credentialStepCount);
        }

        // ==========================================================
        // 工具
        // ==========================================================

        private static bool HostFromTemplate(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            string trimmed = url.Trim();
            int schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) return false;
            string rest = trimmed.Substring(schemeEnd + 3);
            int slash = rest.IndexOf('/');
            string authority = slash >= 0 ? rest.Substring(0, slash) : rest;
            return authority.Contains("{{");
        }

        private static PluginInstanceConfig NewInstance(string id) => new PluginInstanceConfig { Id = id };

        private static PluginExecutionStep NewStep(string url, string method = "GET", string body = "",
            Dictionary<string, string> headers = null, string encoding = "utf-8", int cacheMinutes = 0)
        {
            return new PluginExecutionStep
            {
                Id = "s1",
                Url = url,
                Method = method,
                Body = body,
                Headers = headers,
                ResponseEncoding = encoding,
                ResponseFormat = "text",
                Extract = new Dictionary<string, string> { ["v"] = "$" },
                CacheMinutes = cacheMinutes
            };
        }

        /// <summary>构造一条链式模板；步骤 URL 互不相同（避免在途请求被合并）。</summary>
        private static PluginTemplate ChainTemplate(string url, int stepCount = 1)
        {
            var steps = new List<PluginExecutionStep>();
            for (int i = 0; i < stepCount; i++)
            {
                int n = i;
                steps.Add(new PluginExecutionStep
                {
                    Id = "s" + n,
                    Url = stepCount == 1 ? url : url + "-" + n,
                    Method = "GET",
                    ResponseEncoding = "utf-8",
                    ResponseFormat = "text",
                    Extract = new Dictionary<string, string> { ["v"] = "$" },
                    CacheMinutes = 0
                });
            }

            return new PluginTemplate
            {
                Id = "tmpl",
                Meta = new PluginMeta { Name = "T" },
                Execution = new PluginExecution { Type = "chain", Steps = steps },
                Outputs = new List<PluginOutput>()
            };
        }

        /// <summary>驱动产品的步骤执行路径（私有方法，签名与生产一致）。</summary>
        private static async Task RunStepAsync(PluginExecutor exec, PluginInstanceConfig inst,
            PluginExecutionStep step, CancellationToken token = default)
        {
            var context = new Dictionary<string, string>();
            var task = (Task)Method("ExecuteStepAsync").Invoke(exec, new object[] { inst, step, context, "", token });
            await task.ConfigureAwait(false);
        }

        /// <summary>驱动产品的**带作用域**步骤执行路径（与生产同一条代码）。</summary>
        private static async Task RunStepWithScopeAsync(PluginExecutor exec, PluginInstanceConfig inst,
            PluginExecutionStep step, PluginSandboxScope scope, HashSet<string> declaredHosts,
            CancellationToken token = default)
        {
            var context = new Dictionary<string, string>();
            var task = (Task)Method("ExecuteStepWithScopeAsync").Invoke(exec,
                new object[] { inst, step, context, "", token, scope, declaredHosts });
            await task.ConfigureAwait(false);
        }

        /// <summary>驱动产品的**带作用域**实例执行路径（与生产同一条代码）。</summary>
        private static Task<bool> RunInstanceWithScopeAsync(PluginExecutor exec, PluginInstanceConfig inst,
            PluginTemplate tmpl, PluginSandboxScope scope, HashSet<string> declaredHosts,
            CancellationToken token = default)
        {
            return (Task<bool>)Method("ExecuteInstanceWithScopeAsync").Invoke(exec,
                new object[] { inst, tmpl, token, scope, declaredHosts });
        }

        private static async Task<Exception> CaptureAsync(Task task)
        {
            try { await task.ConfigureAwait(false); return null; }
            catch (Exception ex) { return ex; }
        }

        private static MethodInfo Method(string name)
        {
            var m = typeof(PluginExecutor).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            Assert.NotNull(m);
            return m;
        }

        private static void SetField(object target, string name, object value)
        {
            var f = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            f.SetValue(target, value);
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (predicate()) return true;
                await Task.Delay(25);
            }
            return predicate();
        }

        // ---------------------------------------------------------------
        // 本地回环 HTTP 服务器（不触公网，自带请求计数）
        // ---------------------------------------------------------------

        private sealed class RecordedRequest
        {
            public string Method = "";
            public string Target = "";
            public string Body = "";
            public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class TestResponse
        {
            public int Status = 200;
            public byte[] Body = Array.Empty<byte>();
            public string ContentType = "text/plain; charset=utf-8";

            public static TestResponse Text(string s) =>
                new TestResponse { Body = Encoding.UTF8.GetBytes(s) };

            public static TestResponse Bytes(byte[] b) =>
                new TestResponse { Body = b };
        }

        private sealed class TinyHttpServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new();
            private readonly List<RecordedRequest> _requests = new();
            private readonly object _sync = new();
            private readonly object _gateLock = new();
            private TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private bool _hold;

            public TinyHttpServer(Func<RecordedRequest, Task<TestResponse>> handler)
            {
                Handler = handler;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = Task.Run(AcceptLoopAsync);
            }

            public Func<RecordedRequest, Task<TestResponse>> Handler { get; set; }

            public int Port { get; }
            public string Url => "http://127.0.0.1:" + Port;

            public int RequestCount { get { lock (_sync) return _requests.Count; } }
            public List<RecordedRequest> Requests { get { lock (_sync) return _requests.ToList(); } }

            /// <summary>武装"暂不回应"：之后的请求会被挂起，直到 <see cref="ReleaseHeld"/>。</summary>
            public void ArmHold()
            {
                lock (_gateLock)
                {
                    _gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _hold = true;
                }
            }

            public void ReleaseHeld()
            {
                lock (_gateLock)
                {
                    _hold = false;
                    _gate.TrySetResult(true);
                }
            }

            private Task WaitGateAsync()
            {
                lock (_gateLock)
                {
                    return _hold ? _gate.Task : Task.CompletedTask;
                }
            }

            private async Task AcceptLoopAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch { return; }
                    _ = Task.Run(() => HandleAsync(client));
                }
            }

            private async Task HandleAsync(TcpClient client)
            {
                try
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var req = await ReadRequestAsync(stream);
                        if (req == null) return;

                        lock (_sync) _requests.Add(req);

                        if (_hold) await WaitGateAsync();
                        var resp = await Handler(req);
                        await WriteResponseAsync(stream, resp);
                    }
                }
                catch { /* 连接被对端断开等，忽略 */ }
            }

            private static async Task<RecordedRequest> ReadRequestAsync(NetworkStream stream)
            {
                var buffer = new byte[8192];
                var head = new List<byte>();
                int headerEnd = -1;
                while (headerEnd < 0)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (read <= 0) return null;
                    for (int i = 0; i < read; i++) head.Add(buffer[i]);
                    headerEnd = IndexOfCrLfCrLf(head);
                    if (head.Count > 64 * 1024) return null;
                }

                string text = Encoding.ASCII.GetString(head.ToArray(), 0, headerEnd);
                var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var parts = lines[0].Split(' ');
                var req = new RecordedRequest
                {
                    Method = parts.Length > 0 ? parts[0] : "",
                    Target = parts.Length > 1 ? parts[1] : ""
                };
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon <= 0) continue;
                    req.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }

                if (req.Headers.TryGetValue("Content-Length", out var lenText) && int.TryParse(lenText, out int len) && len > 0)
                {
                    var body = new byte[len];
                    int offset = 0;
                    while (offset < len)
                    {
                        int read = await stream.ReadAsync(body, offset, len - offset);
                        if (read <= 0) break;
                        offset += read;
                    }
                    req.Body = Encoding.UTF8.GetString(body, 0, offset);
                }
                return req;
            }

            private static int IndexOfCrLfCrLf(List<byte> bytes)
            {
                for (int i = 0; i + 3 < bytes.Count; i++)
                {
                    if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n') return i;
                }
                return -1;
            }

            private static async Task WriteResponseAsync(NetworkStream stream, TestResponse resp)
            {
                string reason = resp.Status == 200 ? "OK" : "Error";
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {resp.Status} {reason}\r\n" +
                    $"Content-Type: {resp.ContentType}\r\n" +
                    $"Content-Length: {resp.Body.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(head, 0, head.Length);
                if (resp.Body.Length > 0) await stream.WriteAsync(resp.Body, 0, resp.Body.Length);
                await stream.FlushAsync();
            }

            public void Dispose()
            {
                try { _cts.Cancel(); } catch { }
                try { _listener.Stop(); } catch { }
                try { _cts.Dispose(); } catch { }
            }
        }
    }
}
