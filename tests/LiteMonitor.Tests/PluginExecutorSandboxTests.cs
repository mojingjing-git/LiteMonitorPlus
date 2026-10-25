using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.Plugins.Native;
using LiteMonitor.src.SystemServices.InfoService;
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
        // 2b. 步骤数上限（按目标计 —— S3.4）
        // ==========================================================

        /// <summary>
        /// 端到端（S3.4 新增）：<b>多目标</b>下"目标数 × 每目标步数"的乘积<b>超过 16</b>
        /// （20 个目标 × 1 步 = 20 &gt; 16），但<b>每个目标</b>的步数都 ≤ 16 ⇒
        /// 整次执行必须**成功**，失败原因码**不得**是 <c>LM009_STEP_LIMIT</c>。
        /// <para>旧语义（步骤数实例内累计）下这里会以 LM009_STEP_LIMIT 失败 ——
        /// 那正是本次要消灭的用户可见功能退化（Stock 配 9 个标的、Weather 配 6 个城市即失败）。</para>
        /// </summary>
        [Fact]
        public async Task 端到端_多目标_每目标步数未超限_乘积超16也成功()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            using var exec = new PluginExecutor();

            int targetCount = 20;   // 20 × 1 = 20 步 > 16（每个目标仍只有 1 步）
            var inst = NewInstance("inst-multi-target");
            inst.Targets = Enumerable.Range(0, targetCount)
                .Select(i => new Dictionary<string, string> { ["n"] = i.ToString() })
                .ToList();

            // 每个目标请求各自的 URL（避免在途请求被合并 ⇒ 覆盖到"每个目标都真的跑了一遍"）
            var tmpl = ChainTemplate(server.Url + "/m?n={{n}}");
            tmpl.Inputs = new List<PluginInput> { new PluginInput { Key = "n", DefaultValue = "0" } };

            PluginSandboxException failure = null;
            bool ok = false;
            try { ok = await exec.ExecuteInstanceAsync(inst, tmpl); }
            catch (PluginSandboxException ex) { failure = ex; }

            // 失败原因码不得是步骤数上限（也不得是任何其它上限）
            // [LM-043] 原先这里的 `Assert.NotEqual(StepLimit, failure?.Reason)` 在 failure == null 时
            //   恒真（空转断言）⇒ 已删除；承重的是下面两条。
            Assert.Null(failure);
            Assert.True(ok, "多目标（每目标步数 ≤ 16）时执行应当成功");
            Assert.True(
                server.RequestCount >= targetCount,
                $"每个目标都应发出请求（实际 {server.RequestCount} / {targetCount}）");
        }

        /// <summary>
        /// 端到端（S3.4 新增）：多目标下，<b>单个目标</b>的步骤链超过 16 步时
        /// 仍以 <c>LM009_STEP_LIMIT</c> 失败 —— 步骤数上限<b>没有被放宽</b>，
        /// 只是从"实例内累计"改成"按目标计"。
        /// <para>两个上限互不掩盖：本例目标数（3）远低于目标上限（64），
        /// 失败原因码必须是步骤数上限而不是目标数上限。</para>
        /// </summary>
        [Fact]
        public async Task 端到端_多目标_单目标链超16步_仍按LM009_STEP_LIMIT失败()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-multi-step-limit");
            inst.Targets = Enumerable.Range(0, 3)
                .Select(i => new Dictionary<string, string> { ["n"] = i.ToString() })
                .ToList();

            // 每个目标 17 步 ⇒ 单条链超限
            var tmpl = ChainTemplate(server.Url + "/s", PluginSandboxLimits.MaxStepsPerExecution + 1);

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(() => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.StepLimit, ex.Reason);
            // 不得被目标数上限掩盖
            Assert.NotEqual(PluginSandboxReasons.TargetLimit, ex.Reason);
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
        /// <para>S3.4 语义变更：步骤数上限**按目标计**（每个 target 至多 16 步），
        /// 64 个目标 × 1 步 = 64 步 &gt; 16，但**每个目标都只有 1 步** ⇒ 不触达步骤数上限，
        /// 整次执行**应当成功** —— 这正是"目标上限（64）没有触发"的证据。
        /// （旧语义"步骤数实例内累计"下这里会以 LM009_STEP_LIMIT 失败。）</para>
        /// <para>[LM-042] URL 必须是<b>目标专属</b>的（带 <c>{{n}}</c> 变量）：
        /// 旧版 URL 对 64 个目标完全相同 ⇒ 在途去重（<c>PluginExecutor</c> 的请求合并）
        /// 会把并发中的相同请求合并成一条，服务端实际只收到 61 个请求 ⇒
        /// "64 个目标都真的跑了各自的一步"根本没被证明。
        /// 现在断言收紧为<b>恰好 64 个请求</b>。</para>
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

            // 每个目标请求各自的 URL（避免在途请求被合并 ⇒ 覆盖到"每个目标都真的跑了一遍"）
            var tmpl = ChainTemplate(server.Url + "/t?n={{n}}");
            tmpl.Inputs = new List<PluginInput> { new PluginInput { Key = "n", DefaultValue = "0" } };

            PluginSandboxException failure = null;
            bool ok = false;
            try { ok = await exec.ExecuteInstanceAsync(inst, tmpl); }
            catch (PluginSandboxException ex) { failure = ex; }

            // 步骤数按目标计 ⇒ 每个目标只有 1 步 ⇒ 整次执行成功，不得因任何上限失败
            // （[LM-043] 原先这里的 `Assert.NotEqual(TargetLimit, failure?.Reason)` 在
            //   failure == null 时恒真，是空转断言 ⇒ 已删除；承重的是下面两条）
            Assert.Null(failure);
            Assert.True(ok, "目标数等于上限（每个目标仅 1 步）时执行应当成功");
            // [LM-042] 目标上限放行 ⇒ 64 个目标**各自**发出了一次请求（不多不少）
            Assert.Equal(PluginSandboxLimits.MaxTargetsPerStep, server.RequestCount);
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

        /// <summary>
        /// 端到端（S3.4 新增）：总时长上限**跨 target 累计** —— 一次执行共用**一个截止时刻**。
        /// <para>场景：作用域总时长 400 ms，实例有 12 个目标；执行器按索引错峰启动目标
        /// （第 i 个目标在 i × 50 ms 启动）⇒ 第 8 个及以后的目标在**实例截止时刻之后**才启动。
        /// 它们必须被**实例级**截止时刻拒掉（原因码 <c>LM009_DEADLINE_EXCEEDED</c>）——
        /// 若总时长改成"每目标各自计算"，这些后启动的目标会拿到全新的 400 ms 预算而成功执行，
        /// 本用例随即失败。</para>
        /// <para>注意失败原因码必须是总时长到期，**不得**是步骤数上限（12 个目标 × 1 步 = 12 步，
        /// 按目标计与按实例计都不触达 16 步）。</para>
        /// </summary>
        [Fact]
        public async Task 端到端_总时长跨target累计_后启动的target仍按实例截止时刻判定()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-deadline-cumulative");
            inst.Targets = Enumerable.Range(0, 12)
                .Select(i => new Dictionary<string, string> { ["n"] = i.ToString() })
                .ToList();

            var tmpl = ChainTemplate(server.Url + "/d");

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
            // 不得被步骤数上限掩盖（12 个目标 × 1 步 = 12 步 < 16）
            Assert.NotEqual(PluginSandboxReasons.StepLimit, ex.Reason);
        }

        /// <summary>
        /// 端到端（[LM-044]）：**生产入口** <c>ExecuteInstanceAsync</c> 实际使用的
        /// 单次执行总时长，必须等于契约值 <see cref="PluginSandboxLimits.MaxExecutionDurationMs"/>（30 s）。
        /// <para>缺口背景：上面 3 条总时长用例都<b>注入测试自建作用域</b>并走私有方法
        /// ⇒ 把生产入口的默认时长改成 5 分钟，它们仍然全绿 ⇒ 30 s 这个契约值从未被钉住。</para>
        /// <para>本用例走<b>生产入口</b>（<c>ExecuteInstanceAsync</c>），并取回"本次生产入口
        /// 实际创建的那个实例级作用域"（按本次独有的取消令牌索引），断言它的
        /// <c>MaxDuration</c> 就是 30 s。</para>
        /// </summary>
        [Fact]
        public async Task 端到端_生产入口实际使用的总时长_等于契约值30秒()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            using var exec = new RecordingPluginExecutor();

            var inst = NewInstance("inst-prod-duration");
            var tmpl = ChainTemplate(server.Url + "/dur");

            using var cts = new CancellationTokenSource();

            Assert.True(await exec.ExecuteInstanceAsync(inst, tmpl, cts.Token));

            // 生产入口实际创建的实例级作用域（经 CreateScope 观测，不依赖进程级全局状态）
            var scope = exec.CapturedScope;
            Assert.NotNull(scope);

            // 契约值（字面量 + 常量双重钉死）
            Assert.Equal(PluginSandboxLimits.MaxExecutionDurationMs, (int)scope.MaxDuration.TotalMilliseconds);
            Assert.Equal(TimeSpan.FromMilliseconds(30_000), scope.MaxDuration);

            // 同一作用域的其它上限也不得在生产入口上漂移
            Assert.Equal(PluginSandboxLimits.MaxStepsPerExecution, scope.MaxSteps);
            Assert.Equal(PluginSandboxLimits.MaxTargetsPerStep, scope.MaxTargets);
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
        /// <para>[LM-041] <b>钉住 500 ms 这个数值</b>（旧版只锁"可达性 + 原因码"，
        /// 把 <c>RegexMatchTimeout</c> 从 500 ms 改成 10 s 时本用例仍然全绿）：
        /// ① 内层 <see cref="RegexMatchTimeoutException"/> 携带"实际超时的时限"，
        /// 直接断言它就是 500 ms；② 另加一条<b>宽松</b>的耗时上界（5 s），
        /// 证明生产路径是"几百毫秒就失败"而不是"跑满 10 秒"（不做脆弱的精确计时）。</para>
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

            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(() => exec.ExecuteInstanceAsync(inst, tmpl));
            sw.Stop();

            Assert.Equal(PluginSandboxReasons.RegexTimeout, ex.Reason);

            // [LM-041] 数值钉死：实际超时的时限就是契约值 500 ms
            // （字面量 500 ms ⇒ 常量被改成 10 s 时本断言必然变红）
            var timeoutEx = Assert.IsType<RegexMatchTimeoutException>(ex.InnerException);
            Assert.Equal(TimeSpan.FromMilliseconds(500), timeoutEx.MatchTimeout);
            Assert.Equal(PluginSandboxLimits.RegexMatchTimeout, timeoutEx.MatchTimeout);
            Assert.Equal(PluginSandboxLimits.MaxRegexMatchTimeoutMs, (int)timeoutEx.MatchTimeout.TotalMilliseconds);

            // [LM-041] 耗时上界（宽松）：500 ms 的时限 ⇒ 远小于 10 s
            // （留足调度余量，避免精确计时导致的脆弱断言）
            Assert.True(
                sw.ElapsedMilliseconds < 5000,
                $"正则超时耗时 {sw.ElapsedMilliseconds} ms，说明 500 ms 的时限没有真正生效");
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

        /// <summary>
        /// 实测全部内置插件（<c>resources/plugins/*.json</c>）在<b>多目标</b>下的沙箱行为（S3.4）。
        /// <para>对每个内置插件：① 统计<b>每目标</b>的步骤链长度（必须 ≤ 16）；
        /// ② 算出旧语义（步骤数实例内累计）下会被步骤数上限挡下的目标数；
        /// ③ 用"旧语义恰好会失败"的目标数**实跑**一次，断言**不得**出现沙箱上限类失败
        /// （尤其是 <c>LM009_STEP_LIMIT</c>）。</para>
        /// <para>说明：为避免触公网，步骤 URL 一律改写到本地回环服务器（步骤条数不变）；
        /// 这里测量的是<b>沙箱计数口径</b>，不是插件的业务正确性。</para>
        /// </summary>
        [Fact]
        public async Task 内置插件_多目标实测_每目标步数均在上限内()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "resources", "plugins");
            Assert.True(Directory.Exists(dir), $"未找到内置插件目录：{dir}");

            var files = Directory.GetFiles(dir, "*.json")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.NotEmpty(files);

            using var server = new TinyHttpServer(_ =>
                Task.FromResult(TestResponse.Bytes(Encoding.UTF8.GetBytes("{\"data\":{\"forecast\":[]}}"))));
            using var exec = new PluginExecutor();

            var lines = new List<string>();

            foreach (var file in files)
            {
                var tmpl = JsonSerializer.Deserialize<PluginTemplate>(File.ReadAllText(file));
                Assert.NotNull(tmpl);

                var steps = tmpl.Execution?.Steps ?? new List<PluginExecutionStep>();
                int perTargetSteps = Math.Max(steps.Count, 1);

                // ① 每目标的步骤链长度必须 ≤ 步骤数上限（步骤数按目标计）
                Assert.True(
                    perTargetSteps <= PluginSandboxLimits.MaxStepsPerExecution,
                    $"内置插件 {tmpl.Id} 的每目标步骤数 {perTargetSteps} 超过上限 {PluginSandboxLimits.MaxStepsPerExecution}");

                // ② 旧语义（实例内累计）下允许的最大目标数
                int oldMaxTargets = PluginSandboxLimits.MaxStepsPerExecution / perTargetSteps;
                int probeTargets = oldMaxTargets + 1;   // 旧语义下恰好会失败的目标数

                // ③ 实跑：步骤 URL 改写到本地回环（步骤条数保持不变），并去掉
                //    extract/process/outputs/proxy —— 目的是**隔离沙箱计数口径**（插件业务正确性不在本用例范围：
                //    本地哑响应会让"提取值缺失即快速失败"的产品逻辑报错、ProxyLatency 的代理也不可达，
                //    二者都会掩盖沙箱结论）。
                foreach (var step in steps)
                {
                    step.Url = server.Url + "/" + step.Id;
                    step.Extract = null;
                    step.Process = null;
                    step.Proxy = null;
                }
                tmpl.Outputs = new List<PluginOutput>();

                var inst = new PluginInstanceConfig { Id = "inst-builtin-" + tmpl.Id };
                inst.Targets = Enumerable.Range(0, probeTargets)
                    .Select(_ => new Dictionary<string, string>())
                    .ToList();

                string outcome;
                try
                {
                    bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);
                    outcome = ok ? "成功" : "失败（非沙箱上限：业务/解析/网络）";
                }
                catch (PluginSandboxException ex)
                {
                    outcome = "沙箱上限失败：" + ex.Reason;
                }

                // 沙箱的三个上限都不得触发（尤其步骤数上限）
                Assert.DoesNotContain(PluginSandboxReasons.StepLimit, outcome);
                Assert.DoesNotContain(PluginSandboxReasons.TargetLimit, outcome);
                Assert.DoesNotContain(PluginSandboxReasons.DeadlineExceeded, outcome);

                lines.Add(
                    $"{tmpl.Id,-14} 每目标步数={perTargetSteps}  旧语义最大目标数={oldMaxTargets}  " +
                    $"实跑目标数={probeTargets}  结果={outcome}");
            }

            _output.WriteLine("内置插件多目标实测（步骤数按目标计）：");
            foreach (var line in lines) _output.WriteLine("  " + line);
        }

        // ==========================================================
        // 9. [F13] 步骤级失败兜底（continue_on_error）与本地倒计时兜底
        // ==========================================================

        /// <summary>
        /// [F13] 主源返回 <c>null</c>（issue #611 的真实症状）时，
        /// <b>不得</b>只显示错误标记 ⇒ 必须回落到本地元旦倒计时，且显示的是"距离元旦 N 天"。
        /// <para>本用例走**生产入口** <c>ExecuteInstanceAsync</c>（真实步骤链 + 真实发布路径）。</para>
        /// </summary>
        [Fact]
        public async Task 兜底_主源返回null_发布元旦倒计时而非错误标记()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("null")));
            using var exec = new PluginExecutor();
            exec.Clock = new FixedTimeProvider(new DateTime(2026, 12, 31, 10, 0, 0), TimeZoneInfo.Utc);

            var inst = NewInstance("inst-f13-null");
            var tmpl = HolidayChainTemplate(server.Url + "/next");

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.True(ok);
            Assert.Equal("1", InfoService.Instance.GetValue("inst-f13-null.next"));
            Assert.Equal("距离元旦", InfoService.Instance.GetValue("PROP.Label.DASH.inst-f13-null.next"));
            Assert.NotEqual(PluginConstants.STATUS_ERROR, InfoService.Instance.GetValue("inst-f13-null.next"));
        }

        /// <summary>
        /// [F13] 主源正常时**不得**触发兜底：发布的是真实节假日数据，标签仍是主源给出的节假日名。
        /// </summary>
        [Fact]
        public async Task 兜底_主源正常_不触发兜底且发布真实节假日数据()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(
                TestResponse.Text("{\"holiday\":{\"name\":\"国庆\",\"date\":\"2026-10-01\",\"rest\":5}}")));
            using var exec = new PluginExecutor();
            exec.Clock = new FixedTimeProvider(new DateTime(2026, 12, 31, 10, 0, 0), TimeZoneInfo.Utc);

            var inst = NewInstance("inst-f13-ok");
            var tmpl = HolidayChainTemplate(server.Url + "/next");

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.True(ok);
            Assert.Equal("5", InfoService.Instance.GetValue("inst-f13-ok.next"));
            // 标签来自主源的节假日名 ⇒ 证明兜底步骤被 skip_if_set 跳过
            Assert.Equal("距离国庆", InfoService.Instance.GetValue("PROP.Label.DASH.inst-f13-ok.next"));
        }

        /// <summary>
        /// [F13] 主源返回**非法 JSON**（业务可恢复失败）⇒ 兜底生效，发布元旦倒计时。
        /// </summary>
        [Fact]
        public async Task 兜底_主源非法JSON_发布元旦倒计时()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("{\"holiday\":")));
            using var exec = new PluginExecutor();
            exec.Clock = new FixedTimeProvider(new DateTime(2026, 12, 31, 10, 0, 0), TimeZoneInfo.Utc);

            var inst = NewInstance("inst-f13-badjson");
            var tmpl = HolidayChainTemplate(server.Url + "/next");

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.True(ok);
            Assert.Equal("1", InfoService.Instance.GetValue("inst-f13-badjson.next"));
            Assert.NotEqual(PluginConstants.STATUS_ERROR, InfoService.Instance.GetValue("inst-f13-badjson.next"));
        }

        /// <summary>
        /// [F13] 主源返回 HTTP 500（请求失败）⇒ 兜底生效，发布元旦倒计时。
        /// </summary>
        [Fact]
        public async Task 兜底_主源HTTP失败_发布元旦倒计时()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(
                new TestResponse { Status = 500, Body = Encoding.UTF8.GetBytes("boom") }));
            using var exec = new PluginExecutor();
            exec.Clock = new FixedTimeProvider(new DateTime(2026, 12, 31, 10, 0, 0), TimeZoneInfo.Utc);

            var inst = NewInstance("inst-f13-500");
            var tmpl = HolidayChainTemplate(server.Url + "/next");

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.True(ok);
            Assert.Equal("1", InfoService.Instance.GetValue("inst-f13-500.next"));
            Assert.NotEqual(PluginConstants.STATUS_ERROR, InfoService.Instance.GetValue("inst-f13-500.next"));
        }

        /// <summary>
        /// [F13] 主源**单请求超时** ⇒ 与"总预算耗尽"区分开：单请求超时是业务可恢复失败，兜底生效。
        /// <para>把客户端超时缩短到 300 ms（生产值 10 s），服务器挂起不回应 ⇒ 必然触发单请求超时。</para>
        /// </summary>
        [Fact]
        public async Task 兜底_主源单请求超时_发布元旦倒计时()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            server.ArmHold();
            using var exec = new PluginExecutor();
            exec.Clock = new FixedTimeProvider(new DateTime(2026, 12, 31, 10, 0, 0), TimeZoneInfo.Utc);
            SetHttpTimeout(exec, TimeSpan.FromMilliseconds(300));

            var inst = NewInstance("inst-f13-timeout");
            var tmpl = HolidayChainTemplate(server.Url + "/next");

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.True(ok);
            Assert.Equal("1", InfoService.Instance.GetValue("inst-f13-timeout.next"));
            Assert.NotEqual(PluginConstants.STATUS_ERROR, InfoService.Instance.GetValue("inst-f13-timeout.next"));
        }

        /// <summary>
        /// [F13] 主源**字段提取失败**时，失败步骤留下的半成品<b>不得</b>污染链上下文：
        /// 提取成功的 <c>rest=5</c> 不得被合并回去、也不得让兜底步骤的 <c>skip_if_set</c> 误跳过。
        /// <para>断言：发布的是元旦倒计时（1），<b>不是</b>主源那个半成品 5。</para>
        /// </summary>
        [Fact]
        public async Task 兜底_主源字段提取失败_半成品不污染变量且不误触发skip_if_set()
        {
            // 缺 name ⇒ 提取得到 ❓ ⇒ 该步骤判定失败（但 rest 提取成功 = 5）
            using var server = new TinyHttpServer(_ => Task.FromResult(
                TestResponse.Text("{\"holiday\":{\"date\":\"2026-10-01\",\"rest\":5}}")));
            using var exec = new PluginExecutor();
            exec.Clock = new FixedTimeProvider(new DateTime(2026, 12, 31, 10, 0, 0), TimeZoneInfo.Utc);

            var inst = NewInstance("inst-f13-partial");
            var tmpl = HolidayChainTemplate(server.Url + "/next");

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.True(ok);
            // 兜底步骤确实执行了（skip_if_set 没有被半成品触发）
            Assert.Equal("距离元旦", InfoService.Instance.GetValue("PROP.Label.DASH.inst-f13-partial.next"));
            // 发布值是元旦倒计时，而不是主源那个"提取成功但整体失败"的 5
            Assert.Equal("1", InfoService.Instance.GetValue("inst-f13-partial.next"));
            Assert.NotEqual("5", InfoService.Instance.GetValue("inst-f13-partial.next"));
            Assert.NotEqual(PluginConstants.STATUS_UNKNOWN, InfoService.Instance.GetValue("inst-f13-partial.next"));
        }

        /// <summary>
        /// [F13] 兜底源**也失败** ⇒ 整条链失败 ⇒ 发布**错误状态**（不得当成正常值）。
        /// </summary>
        [Fact]
        public async Task 兜底_备用源也失败_发布错误状态而非正常值()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(
                new TestResponse { Status = 500, Body = Encoding.UTF8.GetBytes("boom") }));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-f13-both-fail");
            var tmpl = TwoStepHttpTemplate(server.Url + "/primary", server.Url + "/backup");

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.False(ok);
            Assert.Equal(PluginConstants.STATUS_ERROR, InfoService.Instance.GetValue("inst-f13-both-fail.next"));
        }

        /// <summary>
        /// [F13] <c>continue_on_error</c> **缺省为 false** ⇒ 步骤失败照旧中断整条链（既有行为不变）。
        /// </summary>
        [Fact]
        public async Task 兜底_缺省continue_on_error为false_步骤失败仍中断整条链()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(
                new TestResponse { Status = 500, Body = Encoding.UTF8.GetBytes("boom") }));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-f13-default-false");
            var tmpl = TwoStepHttpTemplate(server.Url + "/primary", server.Url + "/backup",
                primaryContinueOnError: false);

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.False(ok);
            // 第二步根本没被执行
            Assert.Equal(1, server.RequestCount);
            Assert.Equal(PluginConstants.STATUS_ERROR, InfoService.Instance.GetValue("inst-f13-default-false.next"));
        }

        /// <summary>
        /// [F13] **外层取消不可被兜底吞掉**：取消后整条链必须终止，备用步骤不得执行。
        /// </summary>
        [Fact]
        public async Task 兜底_外层取消不被吞掉_备用步骤不得执行()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            server.ArmHold();
            using var exec = new PluginExecutor();
            using var cts = new CancellationTokenSource();

            var inst = NewInstance("inst-f13-cancel");
            var tmpl = TwoStepHttpTemplate(server.Url + "/primary", server.Url + "/backup");

            var task = exec.ExecuteInstanceAsync(inst, tmpl, cts.Token);
            Assert.True(await WaitUntilAsync(() => server.RequestCount >= 1, TimeSpan.FromSeconds(10)),
                "主源请求未发出（用例可能挂死）");

            cts.Cancel();
            bool ok = await task;

            Assert.False(ok);
            // 备用步骤没有被执行 ⇒ 取消没有被降级成"兜底成功"
            Assert.Equal(1, server.RequestCount);
        }

        /// <summary>
        /// [F13] **单次执行总时长耗尽不可被兜底吞掉**：原因码仍是 LM009_DEADLINE_EXCEEDED，
        /// 备用步骤不得执行。
        /// </summary>
        [Fact]
        public async Task 兜底_总时长耗尽不被吞掉_仍抛LM009_DEADLINE_EXCEEDED()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            server.ArmHold();
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-f13-deadline");
            var tmpl = TwoStepHttpTemplate(server.Url + "/primary", server.Url + "/backup");

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
            Assert.Equal(1, server.RequestCount);
        }

        /// <summary>
        /// [F13] **沙箱违规不可被兜底吞掉**：响应体超限仍抛 LM009_RESPONSE_TOO_LARGE，
        /// 备用步骤不得执行。
        /// </summary>
        [Fact]
        public async Task 兜底_响应体超沙箱天花板不被吞掉_仍抛LM009_RESPONSE_TOO_LARGE()
        {
            var payload = new byte[PluginSandboxLimits.MaxResponseBytes + 4096];
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Bytes(payload)));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-f13-toolarge");
            var tmpl = TwoStepHttpTemplate(server.Url + "/primary", server.Url + "/backup");

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(() => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.ResponseTooLarge, ex.Reason);
            Assert.Equal(1, server.RequestCount);
        }

        /// <summary>
        /// [F13] **步骤数上限不可被兜底吞掉**：仍抛 LM009_STEP_LIMIT。
        /// </summary>
        [Fact]
        public async Task 兜底_步骤数超限不被吞掉_仍抛LM009_STEP_LIMIT()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("ok")));
            using var exec = new PluginExecutor();

            var inst = NewInstance("inst-f13-steplimit");
            var tmpl = ManyStepTemplate(server.Url, PluginSandboxLimits.MaxStepsPerExecution + 1);

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(() => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.StepLimit, ex.Reason);
        }

        /// <summary>
        /// [F13] 主源**恢复**后自动回切：下一次执行必须回到真实节假日数据，
        /// 兜底值不得继续留在界面上。
        /// </summary>
        [Fact]
        public async Task 兜底_主源恢复后自动回切到真实节假日数据()
        {
            bool healthy = false;
            using var server = new TinyHttpServer(_ => Task.FromResult(healthy
                ? TestResponse.Text("{\"holiday\":{\"name\":\"国庆\",\"date\":\"2026-10-01\",\"rest\":5}}")
                : TestResponse.Text("null")));
            using var exec = new PluginExecutor();
            exec.Clock = new FixedTimeProvider(new DateTime(2026, 12, 31, 10, 0, 0), TimeZoneInfo.Utc);

            var inst = NewInstance("inst-f13-recover");
            var tmpl = HolidayChainTemplate(server.Url + "/next");

            Assert.True(await exec.ExecuteInstanceAsync(inst, tmpl));
            Assert.Equal("1", InfoService.Instance.GetValue("inst-f13-recover.next"));

            healthy = true;
            Assert.True(await exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal("5", InfoService.Instance.GetValue("inst-f13-recover.next"));
            Assert.Equal("距离国庆", InfoService.Instance.GetValue("PROP.Label.DASH.inst-f13-recover.next"));
        }

        // ==========================================================
        // 10. [F13] native://newyear 本地兜底计算
        // ==========================================================

        /// <summary>[F13] 跨年：12-31 的元旦倒计时是 1 天，目标日期是次年 1 月 1 日。</summary>
        [Fact]
        public void 元旦倒计时_跨年十二三十一日为一天()
        {
            var json = NewYearNative.GetCountdownJson(new DateTime(2026, 12, 31));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("2027-01-01", root.GetProperty("date").GetString());
            Assert.Equal(1, root.GetProperty("rest").GetInt32());
        }

        /// <summary>[F13] 元旦当天：倒计时为 <b>0 天</b>（元旦就是今天），目标日期仍是当天。</summary>
        [Fact]
        public void 元旦倒计时_元旦当天为零天()
        {
            var json = NewYearNative.GetCountdownJson(new DateTime(2027, 1, 1));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("2027-01-01", root.GetProperty("date").GetString());
            Assert.Equal(0, root.GetProperty("rest").GetInt32());
        }

        /// <summary>
        /// [F13] 闰年：倒计时区间**跨过 2 月 29 日**时多算一天
        /// （2027-02-28 → 2028-01-01 共 307 天；2028-02-28 → 2029-01-01 共 308 天）。
        /// </summary>
        [Fact]
        public void 元旦倒计时_闰年跨过二月二十九日多算一天()
        {
            using var normal = JsonDocument.Parse(NewYearNative.GetCountdownJson(new DateTime(2027, 2, 28)));
            using var leap = JsonDocument.Parse(NewYearNative.GetCountdownJson(new DateTime(2028, 2, 28)));

            Assert.Equal("2028-01-01", normal.RootElement.GetProperty("date").GetString());
            Assert.Equal(307, normal.RootElement.GetProperty("rest").GetInt32());

            Assert.Equal("2029-01-01", leap.RootElement.GetProperty("date").GetString());
            Assert.Equal(308, leap.RootElement.GetProperty("rest").GetInt32());
        }

        /// <summary>
        /// [F13] 时区：按**本地日期**计算 —— 同一瞬间在 UTC 下还是 12-30，
        /// 在 UTC+14 下已是 12-31 ⇒ 倒计时必须跟着本地日期变（2 天 vs 1 天）。
        /// </summary>
        [Fact]
        public void 元旦倒计时_按本地日期计算_时区跨日结果不同()
        {
            var instant = new DateTime(2026, 12, 30, 23, 30, 0, DateTimeKind.Utc);
            var plus14 = TimeZoneInfo.CreateCustomTimeZone("F13+14", TimeSpan.FromHours(14), "F13+14", "F13+14");

            var utcJson = NewYearNative.GetCountdownJson(new FixedTimeProvider(instant, TimeZoneInfo.Utc));
            var plus14Json = NewYearNative.GetCountdownJson(new FixedTimeProvider(instant, plus14));

            using var utcDoc = JsonDocument.Parse(utcJson);
            using var plus14Doc = JsonDocument.Parse(plus14Json);

            Assert.Equal("2027-01-01", utcDoc.RootElement.GetProperty("date").GetString());
            Assert.Equal(2, utcDoc.RootElement.GetProperty("rest").GetInt32());

            Assert.Equal("2027-01-01", plus14Doc.RootElement.GetProperty("date").GetString());
            Assert.Equal(1, plus14Doc.RootElement.GetProperty("rest").GetInt32());
        }

        /// <summary>
        /// [F13] 本地兜底**不冒充法定节假日 / 调休数据**：名称恒为"元旦"，
        /// 且显式标注数据来源为本地推算。
        /// </summary>
        [Fact]
        public void 元旦倒计时_不冒充法定节假日_名称恒为元旦且标注本地来源()
        {
            var json = NewYearNative.GetCountdownJson(new DateTime(2026, 10, 1));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("元旦", root.GetProperty("name").GetString());
            Assert.Equal("local", root.GetProperty("source").GetString());
            // 不得出现调休 / 法定节假日名相关的字段
            Assert.False(root.TryGetProperty("holiday", out _));
            Assert.False(root.TryGetProperty("workday", out _));
        }

        /// <summary>
        /// [F13] 随包发布的内置 Holiday 模板必须具备"HTTPS 主源 + 步骤级兜底 + 本地兜底步骤"。
        /// <para>这是模板契约：缺任何一项，#611 的降级路径就不成立。</para>
        /// </summary>
        [Fact]
        public void 内置Holiday模板_主源HTTPS且带步骤级兜底与本地兜底步骤()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "resources", "plugins");
            string file = Path.Combine(dir, "Holiday.json");
            Assert.True(File.Exists(file), $"未找到内置插件模板：{file}");

            var tmpl = JsonSerializer.Deserialize<PluginTemplate>(File.ReadAllText(file));
            Assert.NotNull(tmpl);
            Assert.Equal("Holiday", tmpl.Id);

            var steps = tmpl.Execution?.Steps;
            Assert.NotNull(steps);
            Assert.True(steps.Count >= 2, "内置 Holiday 模板缺少兜底步骤");

            var primary = steps[0];
            Assert.StartsWith("https://", primary.Url, StringComparison.Ordinal);
            Assert.True(primary.ContinueOnError, "主源步骤必须声明 continue_on_error 才能进入兜底步骤");

            var fallback = steps[1];
            Assert.Equal("native://newyear", fallback.Url);
            Assert.Equal("rest", fallback.SkipIfSet);
            Assert.False(fallback.ContinueOnError, "兜底步骤本身失败时必须照旧失败（不得再兜底一层）");
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

        /// <summary>
        /// [F13] 构造"主源 → 本地兜底"链式模板（与随包发布的内置 Holiday 模板同构）：
        /// 主源声明 <c>continue_on_error</c>，兜底步骤用 <c>skip_if_set="rest"</c>。
        /// </summary>
        private static PluginTemplate HolidayChainTemplate(string primaryUrl)
        {
            return new PluginTemplate
            {
                Id = "tmpl",
                Meta = new PluginMeta { Name = "放假倒计时" },
                Execution = new PluginExecution
                {
                    Type = "chain",
                    Steps = new List<PluginExecutionStep>
                    {
                        new PluginExecutionStep
                        {
                            Id = "fetch_holiday",
                            Url = primaryUrl,
                            Method = "GET",
                            ResponseEncoding = "utf-8",
                            ResponseFormat = "json",
                            ContinueOnError = true,
                            Extract = new Dictionary<string, string>
                            {
                                ["name"] = "holiday.name",
                                ["date"] = "holiday.date",
                                ["rest"] = "holiday.rest"
                            }
                        },
                        new PluginExecutionStep
                        {
                            Id = "fallback_newyear",
                            Url = "native://newyear",
                            Method = "GET",
                            ResponseEncoding = "utf-8",
                            ResponseFormat = "json",
                            SkipIfSet = "rest",
                            Extract = new Dictionary<string, string>
                            {
                                ["name"] = "name",
                                ["date"] = "date",
                                ["rest"] = "rest"
                            }
                        }
                    }
                },
                Outputs = new List<PluginOutput>
                {
                    new PluginOutput
                    {
                        Key = "next",
                        Label = "距离{{name}}",
                        ShortLabel = "🏖️",
                        Format = "{{rest}}",
                        Unit = " 天"
                    }
                }
            };
        }

        /// <summary>
        /// [F13] 构造"主源（可回退） → 备用源（不可回退）"两个 HTTP 步骤的模板：
        /// 用于断言"兜底源也失败 ⇒ 错误状态"以及"取消 / 沙箱违规不得被吞掉"。
        /// </summary>
        private static PluginTemplate TwoStepHttpTemplate(string primaryUrl, string backupUrl,
            bool primaryContinueOnError = true)
        {
            return new PluginTemplate
            {
                Id = "tmpl",
                Meta = new PluginMeta { Name = "两步" },
                Execution = new PluginExecution
                {
                    Type = "chain",
                    Steps = new List<PluginExecutionStep>
                    {
                        new PluginExecutionStep
                        {
                            Id = "primary",
                            Url = primaryUrl,
                            Method = "GET",
                            ResponseEncoding = "utf-8",
                            ResponseFormat = "json",
                            ContinueOnError = primaryContinueOnError,
                            Extract = new Dictionary<string, string> { ["rest"] = "data.rest" }
                        },
                        new PluginExecutionStep
                        {
                            Id = "backup",
                            Url = backupUrl,
                            Method = "GET",
                            ResponseEncoding = "utf-8",
                            ResponseFormat = "json",
                            Extract = new Dictionary<string, string> { ["rest"] = "data.rest" }
                        }
                    }
                },
                Outputs = new List<PluginOutput>
                {
                    new PluginOutput { Key = "next", Label = "值", ShortLabel = "v", Format = "{{rest}}" }
                }
            };
        }

        /// <summary>
        /// [F13] 构造 N 步的链式模板（每步 URL 不同，避免在途请求被合并）。
        /// </summary>
        private static PluginTemplate ManyStepTemplate(string url, int stepCount)
        {
            var steps = new List<PluginExecutionStep>();
            for (int i = 0; i < stepCount; i++)
            {
                int n = i;
                steps.Add(new PluginExecutionStep
                {
                    Id = "s" + n,
                    Url = url + "/s" + n,
                    Method = "GET",
                    ResponseEncoding = "utf-8",
                    ResponseFormat = "text",
                    ContinueOnError = true,
                    Extract = new Dictionary<string, string> { ["v"] = "$" }
                });
            }

            return new PluginTemplate
            {
                Id = "tmpl",
                Meta = new PluginMeta { Name = "多步" },
                Execution = new PluginExecution { Type = "chain", Steps = steps },
                Outputs = new List<PluginOutput>()
            };
        }

        /// <summary>把生产客户端的单请求超时缩短（必须在第一次请求之前调用）。</summary>
        private static void SetHttpTimeout(PluginExecutor exec, TimeSpan timeout)
        {
            var field = typeof(PluginExecutor).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            var client = field.GetValue(exec) as HttpClient;
            Assert.NotNull(client);
            client.Timeout = timeout;
        }

        /// <summary>
        /// [F13] 固定时钟：让"本地兜底"的跨年 / 闰年 / 时区行为可被确定性断言。
        /// </summary>
        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _utcNow;
            private readonly TimeZoneInfo _localZone;

            public FixedTimeProvider(DateTime utcNow, TimeZoneInfo localZone)
            {
                _utcNow = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
                _localZone = localZone;
            }

            public override DateTimeOffset GetUtcNow() => _utcNow;

            public override TimeZoneInfo LocalTimeZone => _localZone;
        }

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

        /// <summary>
        /// [LM-044] 记录生产入口实际创建的实例级沙箱作用域。
        /// <para>覆写 <see cref="CreateScope"/> 并<b>调用基类实现</b>
        /// ⇒ 观测到的就是生产路径真正使用的那份作用域：若生产入口改成硬编码时长
        /// （例如 5 分钟）或绕开本方法直接 new，观测结果随之改变 ⇒ 断言必然变红。</para>
        /// </summary>
        private sealed class RecordingPluginExecutor : PluginExecutor
        {
            /// <summary>生产入口本次实际创建的实例级作用域（未创建过时为 null）。</summary>
            public PluginSandboxScope CapturedScope { get; private set; }

            protected override PluginSandboxScope CreateScope(System.Threading.CancellationToken token)
            {
                CapturedScope = base.CreateScope(token);
                return CapturedScope;
            }
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
