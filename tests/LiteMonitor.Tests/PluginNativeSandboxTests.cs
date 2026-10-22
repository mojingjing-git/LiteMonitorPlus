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
using LiteMonitor.src.Plugins.Native;
using Xunit;
using Xunit.Abstractions;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [LM-039] <c>native://</c> 步骤的沙箱回归测试。
    ///
    /// ★ 背景：S3.3/S3.4 把沙箱六上限接进了 <see cref="PluginExecutor"/> 的 **HTTP 步骤路径**，
    ///   但 <c>native://</c> 步骤（<c>ExecuteNativeAsync</c>）走的是
    ///   <c>CityCodeResolver</c> / <c>CryptoNative</c> **自带的 HttpClient** ——
    ///   不占并发槽、不受 2 MiB 响应上限、不做允许目标校验、也不绑定 <c>scope.Token</c>；
    ///   并且 <c>native://crypto?...&amp;fallback=&lt;URL&gt;</c> 会取回**用户提供的任意 URL**。
    ///   本文件验证"原生步骤已接入**同一套**闸门"。
    ///
    /// ★ 判别力要求（每条用例都必须能抓住对应缺陷）：
    ///   · 并发用例观测**服务端收到的最大并发数**（不只看异常）；
    ///   · 字节上限用例额外断言"只发出 1 个请求"（证明上限没有被"换下一个源"吞掉）；
    ///   · 总时长用例断言**实际耗时**（证明不是等自带超时跑满）；
    ///   · 目标校验用例断言**服务端一个请求都没收到**（证明校验发生在取数之前）。
    ///
    /// ★ 不触公网：官方数据源的 URL 通过 <see cref="CryptoNative.SourceUrlOverrides"/> 指向本地回环
    ///   服务器（测试接缝，只换"请求去哪"，闸门仍是生产同一条路径）。
    /// </summary>
    public sealed class PluginNativeSandboxTests
    {
        private readonly ITestOutputHelper _output;

        public PluginNativeSandboxTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>Bybit 形状的行情 JSON（官方源第一个就是 Bybit，解析器按该形状解析）。</summary>
        private const string BybitTickerJson =
            "{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{\"list\":[" +
            "{\"symbol\":\"BTCUSDT\",\"lastPrice\":\"95000\",\"price24hPcnt\":\"0.0123\"," +
            "\"highPrice24h\":\"96000\",\"lowPrice24h\":\"94000\",\"turnover24h\":\"123456\"}]}}";

        // ==========================================================
        // 1. 并发闸门（进程级 8 槽）—— native:// 步骤同样必须占槽
        // ==========================================================

        /// <summary>
        /// [LM-039] 并发闸门：多个原生步骤并发取数时，**服务端观测到的最大并发数**不得超过
        /// <see cref="PluginSandboxLimits.MaxConcurrentRequests"/>（8）；
        /// 超限的那一条必须以 <see cref="PluginSandboxReasons.ConcurrencyLimit"/> **明确失败**
        /// （不得静默截断、不得静默排队）。
        /// <para>变异点：把原生取数路径里的并发槽位获取去掉 ⇒ 服务端并发数变成 9 ⇒ 本用例变红。</para>
        /// </summary>
        [Fact]
        public async Task 原生步骤_并发闸门生效_并发不超过上限且超限以LM009_CONCURRENCY_LIMIT失败()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text(BybitTickerJson)));
            server.ArmHold();                       // 占住响应：让先到的请求一直持有并发槽位
            using var exec = new PluginExecutor();
            using var ov = new SourceOverride(server.Url);

            int total = PluginSandboxLimits.MaxConcurrentRequests + 1;   // 9 条
            var tasks = new List<Task>();
            for (int i = 0; i < total; i++)
            {
                int n = i;
                tasks.Add(RunStepAsync(exec,
                    NewInstance("inst-native-conc-" + n),
                    NewStep("native://crypto?symbol=" + UniqueSymbol("CONC" + n))));
            }
            var outcomes = tasks.Select(CaptureAsync).ToList();

            // 前 8 条请求真的到达服务端（各自占着一个槽位）
            await WaitUntilAsync(() => server.RequestCount >= PluginSandboxLimits.MaxConcurrentRequests, TimeSpan.FromSeconds(20));
            Assert.Equal(PluginSandboxLimits.MaxConcurrentRequests, server.RequestCount);

            // 第 9 条等不到槽位（排队上限 1 s）→ 明确失败
            await WaitUntilAsync(() => outcomes[total - 1].IsCompleted, TimeSpan.FromSeconds(10));
            Assert.True(outcomes[total - 1].IsCompleted, "第 9 个原生请求未在排队上限内明确失败");

            // 放行其余 8 条
            server.ReleaseHeld();

            var results = await Task.WhenAll(outcomes);
            var failures = results.Where(ex => ex != null).ToList();
            Assert.Single(failures);
            var sandbox = Assert.IsAssignableFrom<PluginSandboxException>(failures[0]);
            Assert.Equal(PluginSandboxReasons.ConcurrencyLimit, sandbox.Reason);

            // ★ 核心断言：服务端观测到的并发数不得超过上限
            Assert.True(server.MaxConcurrency <= PluginSandboxLimits.MaxConcurrentRequests,
                $"服务端观测到的最大并发 {server.MaxConcurrency} 超过上限 {PluginSandboxLimits.MaxConcurrentRequests}");
            _output.WriteLine($"服务端最大并发={server.MaxConcurrency}，请求总数={server.RequestCount}");

            // 槽位全部归还
            Assert.Equal(PluginSandboxLimits.MaxConcurrentRequests, PluginSandbox.AvailableRequestSlots);
        }

        // ==========================================================
        // 2. 响应体字节上限（2 MiB）
        // ==========================================================

        /// <summary>
        /// [LM-039] 响应体字节上限：原生步骤的响应超过 <see cref="PluginSandboxLimits.MaxResponseBytes"/>
        /// （2 MiB）时必须按**与 HTTP 路径相同的原因码**失败
        /// （<see cref="PluginSandboxReasons.ResponseTooLarge"/>），
        /// 且**不得**被"这个源不可用、换下一个源"吞掉 —— 只应发出 1 个请求。
        /// <para>变异点：去掉原生取数路径的字节上限 ⇒ 请求成功 ⇒ 本用例变红；
        /// 或让上限异常被源循环吞掉 ⇒ 原因码不再是 LM009_RESPONSE_TOO_LARGE ⇒ 同样变红。</para>
        /// </summary>
        [Fact]
        public async Task 原生步骤_响应体超沙箱天花板_按LM009_RESPONSE_TOO_LARGE失败()
        {
            var payload = new byte[PluginSandboxLimits.MaxResponseBytes + 4096];
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Bytes(payload)));
            using var exec = new PluginExecutor();
            using var ov = new SourceOverride(server.Url);

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => RunStepAsync(exec,
                    NewInstance("inst-native-big"),
                    NewStep("native://crypto?symbol=" + UniqueSymbol("BIG"))));

            Assert.Equal(PluginSandboxReasons.ResponseTooLarge, ex.Reason);

            // 上限不得被"换下一个源"吞掉：只应发出 1 个请求（不是 4 个源各试一次）
            Assert.Equal(1, server.RequestCount);
        }

        // ==========================================================
        // 3. 单次执行总时长（可中止）
        // ==========================================================

        /// <summary>
        /// [LM-039] 总时长上限：作用域令牌到期后，原生请求必须**及时中止**（不得等自带超时跑满），
        /// 原因码 <see cref="PluginSandboxReasons.DeadlineExceeded"/>。
        /// <para>判别力：作用域总时长 400 ms，而原生源自带超时是 3 s（回退源 8 s）——
        /// 若请求没绑定 <c>scope.Token</c>，本例会跑满 3 s 甚至更久 ⇒ 耗时断言变红。</para>
        /// </summary>
        [Fact]
        public async Task 原生步骤_总时长到期_被及时中止且原因码为LM009_DEADLINE_EXCEEDED()
        {
            using var server = new TinyHttpServer(async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));   // 挂住：原生请求会一直等
                return TestResponse.Text(BybitTickerJson);
            });
            using var exec = new PluginExecutor();
            using var ov = new SourceOverride(server.Url);

            var inst = NewInstance("inst-native-deadline");
            var step = NewStep("native://crypto?symbol=" + UniqueSymbol("DL"));

            using var scope = new PluginSandboxScope(
                CancellationToken.None,
                TimeSpan.FromMilliseconds(400),
                PluginSandboxLimits.MaxStepsPerExecution,
                PluginSandboxLimits.MaxTargetsPerStep);

            var sw = Stopwatch.StartNew();
            var task = RunStepWithScopeAsync(exec, inst, step, scope, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(ReferenceEquals(finished, task), "总时长上限未在预期时间内生效（用例可能挂死）");

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(() => task);
            Assert.Equal(PluginSandboxReasons.DeadlineExceeded, ex.Reason);

            // ★ 必须"及时"中止：不得等自带超时（官方源 3 s / 回退源 8 s）跑满
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
                $"原生请求未被及时中止（实际耗时 {sw.ElapsedMilliseconds} ms）");
            _output.WriteLine($"原生步骤被中止耗时：{sw.ElapsedMilliseconds} ms");
        }

        // ==========================================================
        // 4. 允许目标校验（fallback 等用户可控 URL）
        // ==========================================================

        /// <summary>
        /// [LM-039] <c>fallback</c> 指向**用户填写**的、插件**未声明**的主机时，
        /// 必须被判定为"不允许的目标"（原因码 <see cref="PluginSandboxReasons.CredentialTargetNotAllowed"/>），
        /// 而不是"请求失败"；并且**一个请求都不发出**（校验发生在取数之前）。
        /// <para>变异点：去掉允许目标校验 ⇒ 该 URL 会被真的取回 ⇒ 服务端收到请求 ⇒ 本用例变红。</para>
        /// </summary>
        [Fact]
        public async Task 原生步骤_fallback主机来自用户输入且未声明_被判定为不允许的目标且不发出请求()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("{}")));
            using var exec = new PluginExecutor();
            using var ov = new SourceOverride(server.Url);   // 官方源也指向本地：校验若晚于取数，这里会收到请求

            var tmpl = ChainTemplate("native://crypto?symbol=" + UniqueSymbol("FB") + "&fallback={{evil}}");
            tmpl.Inputs = new List<PluginInput>
            {
                new PluginInput { Key = "evil", DefaultValue = server.Url + "/evil" }
            };

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(NewInstance("inst-native-fb"), tmpl));

            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);
            // 用户填的任意 URL 与官方源都没有被访问
            Assert.Equal(0, server.RequestCount);
        }

        /// <summary>
        /// [LM-039] 反向护栏：<c>fallback</c> 指向**插件模板里写死**的主机时**必须仍然可用**
        /// （新闸门不得误杀合法回退源 —— 内置插件 <c>Crypto.json</c> 正是这种形态）。
        /// </summary>
        [Fact]
        public async Task 原生步骤_fallback主机已在模板声明_允许取回()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("{\"ok\":true}")));
            using var exec = new PluginExecutor();
            // 官方源全部指向不可达端口 ⇒ 官方源失败后走 fallback（本地回环）
            using var ov = new SourceOverride("http://127.0.0.1:1");

            var step = NewStep("native://crypto?symbol=" + UniqueSymbol("FBOK") + "&fallback=" + server.Url + "/fb");

            await RunStepAsync(exec, NewInstance("inst-native-fbok"), step);

            Assert.Equal(1, server.RequestCount);
        }

        /// <summary>
        /// [LM-039] 凭据目标校验：带凭据头的原生步骤，其 <c>fallback</c> 目标必须通过
        /// <see cref="PluginSandbox.EnsureCredentialTargetAllowed"/>（与 HTTP 步骤路径**同一口径**：
        /// 凭据只允许发往插件**模板级显式声明**的主机）。
        /// <para>变异点：去掉原生路径的凭据目标校验 ⇒ 该步骤会继续取回未声明主机 ⇒ 本用例变红。</para>
        /// </summary>
        [Fact]
        public async Task 原生步骤_带凭据且回退主机未在模板级声明_按LM009_CREDENTIAL_TARGET_NOT_ALLOWED拒绝()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("{}")));
            using var exec = new PluginExecutor();
            using var ov = new SourceOverride("http://127.0.0.1:1");

            // 主机只出现在 native URL 的内嵌参数里（未在模板级声明集合中），且步骤带凭据头
            var step = NewStep("native://crypto?symbol=" + UniqueSymbol("CRED") + "&fallback=" + server.Url + "/fb");
            step.Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer super-secret-value" };

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => RunStepAsync(exec, NewInstance("inst-native-cred"), step));

            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);
            Assert.Equal(0, server.RequestCount);
        }

        // ==========================================================
        // 5. 既有功能未回归
        // ==========================================================

        /// <summary>
        /// [LM-039] 既有功能未回归：<c>native://crypto</c> 与 <c>native://citycode</c> 在允许目标下
        /// **仍然成功**（原生处理器的取数与解析没被新闸门破坏）。
        /// </summary>
        [Fact]
        public async Task 原生步骤_既有功能未回归_crypto与citycode仍成功()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text(BybitTickerJson)));
            using var exec = new PluginExecutor();
            using var ov = new SourceOverride(server.Url);

            // crypto：官方源（本地回环）→ 解析 → 步骤成功
            await RunStepAsync(exec,
                NewInstance("inst-native-ok-crypto"),
                NewStep("native://crypto?symbol=" + UniqueSymbol("OK")));

            // citycode：走随包分发的本地城市库（离线路径）
            await RunStepAsync(exec,
                NewInstance("inst-native-ok-city"),
                NewStep("native://citycode?city=上海"));

            Assert.True(server.RequestCount >= 1, "crypto 原生步骤应真的发出过请求");
        }

        /// <summary>
        /// [LM-039] 内置插件（<c>Crypto.json</c> / <c>Weather.json</c>）的原生步骤
        /// **不会被新闸门误杀**，且内置 <c>Crypto</c> 插件端到端仍可执行。
        /// <para>① 内置插件显式写死的回退主机必须落在允许集合内；
        /// ② 端到端跑一遍内置模板（官方源指向本地回环），原生步骤必须成功。</para>
        /// </summary>
        [Fact]
        public async Task 内置插件_原生步骤仍在允许目标集合内且Crypto可端到端执行()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "resources", "plugins");
            Assert.True(Directory.Exists(dir), $"未找到内置插件目录：{dir}");

            var crypto = JsonSerializer.Deserialize<PluginTemplate>(
                File.ReadAllText(Path.Combine(dir, "Crypto.json")));
            Assert.NotNull(crypto);

            var cryptoStep = crypto.Execution.Steps.First(s => s.Url.StartsWith("native://", StringComparison.OrdinalIgnoreCase));
            var declared = PluginProcessor.GetDeclaredHosts(crypto);
            var allowed = PluginExecutor.GetNativeAllowedHosts(cryptoStep.Url, declared);

            // 内置插件在模板里写死的回退主机必须在允许集合内
            Assert.Contains("api.binance.com", allowed);
            foreach (var host in declared) Assert.Contains(host, allowed);

            // 端到端：内置 Crypto 模板（原生步骤 → 官方源指向本地回环）
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text(BybitTickerJson)));
            using var ov = new SourceOverride(server.Url);
            using var exec = new PluginExecutor();

            var inst = new PluginInstanceConfig { Id = "inst-builtin-crypto" };
            inst.InputValues = new Dictionary<string, string> { ["symbol"] = UniqueSymbol("BLT") };

            bool ok = await exec.ExecuteInstanceAsync(inst, crypto);
            Assert.True(ok, "内置 Crypto 插件的原生步骤应正常执行");
            _output.WriteLine($"内置插件声明主机=[{string.Join(", ", declared)}]，原生允许集合=[{string.Join(", ", allowed)}]");
        }

        // ==========================================================
        // 工具
        // ==========================================================

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

        /// <summary>构造一条链式模板（单步）。</summary>
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

        /// <summary>
        /// 每次用例都用**唯一**的交易对：<c>CryptoNative</c> 的行情缓存是进程级静态的，
        /// 复用同一个交易对会让第二次执行直接命中缓存（请求数变 0 ⇒ 用例失去判别力）。
        /// </summary>
        private static string UniqueSymbol(string tag) =>
            "LM039" + tag + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();

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
        /// 测试接缝：把 <see cref="CryptoNative"/> 的官方数据源 URL 指向本地回环服务器
        /// （避免用例触公网）。**只换"请求去哪"**，闸门仍是生产同一条路径。
        /// </summary>
        private sealed class SourceOverride : IDisposable
        {
            private readonly Dictionary<string, string> _previous;

            public SourceOverride(string baseUrl)
            {
                _previous = CryptoNative.SourceUrlOverrides;
                CryptoNative.SourceUrlOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Bybit"] = baseUrl + "/bybit?symbol={symbol}",
                    ["GateIO"] = baseUrl + "/gate?symbol={symbol}",
                    ["Binance"] = baseUrl + "/binance?symbol={symbol}",
                    ["OKX"] = baseUrl + "/okx?symbol={symbol}",
                };
            }

            public void Dispose()
            {
                CryptoNative.SourceUrlOverrides = _previous;
            }
        }

        // ---------------------------------------------------------------
        // 本地回环 HTTP 服务器（不触公网，自带请求计数与最大并发观测）
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
            private int _inFlight;
            private int _maxConcurrency;

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

            /// <summary>服务端观测到的**最大同时在途请求数**（并发闸门的直接证据）。</summary>
            public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

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
                int current = Interlocked.Increment(ref _inFlight);
                int observed = Volatile.Read(ref _maxConcurrency);
                while (current > observed &&
                       Interlocked.CompareExchange(ref _maxConcurrency, current, observed) != observed)
                {
                    observed = Volatile.Read(ref _maxConcurrency);
                }

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
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
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
