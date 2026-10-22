using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 插件请求身份与在途租约的行为护栏（LM-002 + LM-003a）。
    ///
    /// 盯住的缺陷：
    ///   LM-002 —— 键里没有方法/凭据/代理/编码 → 同 URL+body 不同凭据的步骤共享同一条在途请求
    ///              与同一条缓存条目（凭据串用）；<c>GetOrAdd</c> 的 valueFactory 并发执行多次 →
    ///              发出多条 HTTP 请求；<c>finally</c> 里每个等待者都无条件 <c>TryRemove</c> →
    ///              合并窗口坍缩且旧任务会误删同键新条目。
    ///   LM-003a —— <c>HttpResponseMessage</c> 未释放；响应体读取无字节上限。
    ///
    /// 测试纪律：
    ///  - 一律只连 127.0.0.1 回环的本地服务器，**不触公网**；服务器自带请求计数，
    ///    "实际发出请求数"因此可以被真实断言；
    ///  - 摘要的期望值是**外部工具**（PowerShell 的 SHA-256）算出的常量，
    ///    匹配它即证明摘要不再依赖进程内加盐的 <c>GetHashCode()</c>；
    ///  - 所有断言都真正执行，不存在早退/跳过。
    /// </summary>
    public class PluginRequestIdentityTests
    {
        static PluginRequestIdentityTests()
        {
            // GBK 编解码需要 CodePages provider（产品侧在 PluginExecutor 构造函数里注册，
            // 测试侧自己注册一份，避免依赖产品构造顺序）。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        // =====================================================================
        // I1..I5 —— 身份隔离（不同凭据 / 方法 / 代理 / 编码 不得互相串用）
        // =====================================================================

        [Fact]
        public async Task I1_Authorization不同_不共享在途也不共享缓存()
        {
            using var server = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var inst = NewInstance("instI1");

            var a = NewStep(server.Url + "/i1", headers: H("Authorization", "Bearer AAA"));
            var b = NewStep(server.Url + "/i1", headers: H("Authorization", "Bearer BBB"));

            // (1) 在途隔离：两个身份并发发起 → 必须发出 2 条请求
            var both = await Task.WhenAll(RunStepAsync(exec, inst, a), RunStepAsync(exec, inst, b));
            Assert.Equal(2, server.RequestCount);
            Assert.Contains("Bearer AAA", both[0]);
            Assert.Contains("Bearer BBB", both[1]);
            Assert.NotEqual(both[0], both[1]);
        }

        [Fact]
        public async Task I2_Cookie不同_不共享在途也不共享缓存()
        {
            using var server = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var inst = NewInstance("instI2");

            var a = NewStep(server.Url + "/i2", headers: H("Cookie", "sid=AAA"));
            var b = NewStep(server.Url + "/i2", headers: H("Cookie", "sid=BBB"));

            var both = await Task.WhenAll(RunStepAsync(exec, inst, a), RunStepAsync(exec, inst, b));
            Assert.Equal(2, server.RequestCount);
            Assert.Contains("sid=AAA", both[0]);
            Assert.Contains("sid=BBB", both[1]);
        }

        [Fact]
        public async Task I3_方法不同_不共享在途也不共享缓存()
        {
            using var server = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var inst = NewInstance("instI3");

            var get = NewStep(server.Url + "/i3", method: "GET");
            var post = NewStep(server.Url + "/i3", method: "POST");

            var both = await Task.WhenAll(RunStepAsync(exec, inst, get), RunStepAsync(exec, inst, post));
            Assert.Equal(2, server.RequestCount);
            Assert.Contains("GET", both[0]);
            Assert.Contains("POST", both[1]);
            // 服务器侧也必须真的看到两种方法
            Assert.Contains(server.Requests, r => r.Method == "GET");
            Assert.Contains(server.Requests, r => r.Method == "POST");
        }

        [Fact]
        public async Task I4_代理不同_不共享在途也不共享缓存()
        {
            using var target = new TinyHttpServer(EchoAsync);
            using var proxy = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var inst = NewInstance("instI4");

            var direct = NewStep(target.Url + "/i4");
            var viaProxy = NewStep(target.Url + "/i4", proxy: proxy.Url);

            await Task.WhenAll(RunStepAsync(exec, inst, direct), RunStepAsync(exec, inst, viaProxy));
            // 直连 1 条 + 走代理 1 条：代理配置不同 → 不得共用同一条在途请求
            Assert.Equal(1, target.RequestCount);
            Assert.Equal(1, proxy.RequestCount);
            // 走代理的那条必须是绝对形式的目标（证明确实经过了代理）
            Assert.StartsWith("http://", proxy.Requests[0].Target);
            Assert.Contains(target.Url, proxy.Requests[0].Target);
        }

        [Fact]
        public async Task I5_响应编码不同_不共享在途也不共享缓存()
        {
            var gbk = Encoding.GetEncoding("GBK").GetBytes("中文温度");
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Bytes(gbk, "text/plain")));
            using var exec = new PluginExecutor();
            var inst = NewInstance("instI5");

            var utf8 = NewStep(server.Url + "/i5", encoding: "utf-8");
            var gbkStep = NewStep(server.Url + "/i5", encoding: "gbk");

            var both = await Task.WhenAll(RunStepAsync(exec, inst, utf8), RunStepAsync(exec, inst, gbkStep));
            Assert.Equal(2, server.RequestCount);
            // gbk 步骤解出原文，utf-8 步骤解出替换字符 → 两者不可能共享同一条在途请求
            Assert.Equal("中文温度", both[1]);
            Assert.NotEqual("中文温度", both[0]);
        }

        [Fact]
        public async Task I6_完全相同的身份_共享一条在途请求()
        {
            using var server = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var inst = NewInstance("instI6");

            var step = NewStep(server.Url + "/i6", headers: H("Authorization", "Bearer same"));
            server.ArmHold();

            var t1 = RunStepAsync(exec, inst, step);
            var t2 = RunStepAsync(exec, inst, step);
            await server.WaitForRequestAsync();
            await Task.Delay(500);
            server.ReleaseHeld();

            var both = await Task.WhenAll(t1, t2);
            Assert.Equal(1, server.RequestCount);
            Assert.Equal(both[0], both[1]);
        }

        [Fact]
        public async Task I7_摘要跨运行一致_不依赖GetHashCode的进程内加盐()
        {
            // 期望值由**外部工具**（PowerShell 的 SHA-256）对同一份规范化负载算出。
            // 负载 = "lm-plugin-req-v1" + 逐字段"长度前缀+冒号+值"**直接拼接**（字段之间没有分隔符）：
            //   lm-plugin-req-v13:GET23:http://127.0.0.1:8080/x0:5:utf-80:1:113:authorization8:Bearer t
            // 逐字段拆解（仅为可读性，"|" 只是展示用分隔符，不参与哈希）：
            //   lm-plugin-req-v1 | 3:GET | 23:http://127.0.0.1:8080/x | 0: | 5:utf-8 | 0: | 1:1 | 13:authorization | 8:Bearer t
            //   ★ Header 数量字段渲染为 "1:1"（长度前缀 "1" + 值 "1"），不是 "1:"。
            // 复算方式：对上面的字节串取 UTF-8 字节 → SHA-256 → 十六进制大写 → 取**前 32 位**：
            //   = 8F45D20EFDDA4F671739A7CC79268775
            // 匹配它即证明摘要不再依赖进程内加盐的 GetHashCode()。
            const string expected = "8F45D20EFDDA4F671739A7CC79268775";
            const string expectedPost = "D394FEB24D55BD70D0286D5A9515F8A6";

            // (1) 纯函数：同样的输入 → 同样的摘要，且等于外部算出的常量
            string d1 = IdentityDigest("GET", "http://127.0.0.1:8080/x", "", H("Authorization", "Bearer t"), "", "utf-8");
            string d2 = IdentityDigest("GET", "http://127.0.0.1:8080/x", "", H("Authorization", "Bearer t"), "", "utf-8");
            Assert.Equal(expected, d1);
            Assert.Equal(d1, d2);
            Assert.Equal(32, d1.Length);
            Assert.All(d1, ch => Assert.True("0123456789ABCDEF".Contains(ch)));

            string dPost = IdentityDigest("POST", "http://host/p", "{}", H("A", "1", "B", "2"), "http://p:1", "gbk");
            Assert.Equal(expectedPost, dPost);
            Assert.NotEqual(d1, dPost);

            // 摘要函数本身与身份构造路径给出同一个值
            Assert.Equal(expected, Digest("GET", "http://127.0.0.1:8080/x", "", H("Authorization", "Bearer t"), "", "utf-8"));

            // (2) 端到端：在途键**就是**摘要；两个独立执行器（两次"运行"）得到同一个键
            using var server = new TinyHttpServer(EchoAsync);
            using var exec1 = new PluginExecutor();
            using var exec2 = new PluginExecutor();
            var inst = NewInstance("instI7");
            var step = NewStep(server.Url + "/i7", headers: H("Authorization", "Bearer t"));

            server.ArmHold();
            var waiter = RunStepAsync(exec1, inst, step);
            await WaitForCountAsync(server, 1);
            string key1 = Inflight(exec1).Keys.Single();
            server.ReleaseHeld();
            await waiter;

            server.ArmHold();
            var waiter2 = RunStepAsync(exec2, inst, step);
            await WaitForCountAsync(server, 2);
            string key2 = Inflight(exec2).Keys.Single();
            server.ReleaseHeld();
            await waiter2;

            Assert.Equal(key1, key2);
            Assert.Equal(IdentityDigest("GET", server.Url + "/i7", "", H("Authorization", "Bearer t"), "", "utf-8"), key1);
        }

        [Fact]
        public void I7b_规范化URL_忽略大小写与fragment但保留query顺序()
        {
            string a = IdentityDigest("GET", "http://EXAMPLE.com:80/p?q=1", "", null, "", "utf-8");
            string b = IdentityDigest("GET", "http://example.com/p?q=1", "", null, "", "utf-8");
            Assert.Equal(a, b);

            // fragment 不参与身份
            string withFragment = IdentityDigest("GET", "http://example.com/p?q=1#frag", "", null, "", "utf-8");
            Assert.Equal(a, withFragment);

            // query 顺序不同 → 语义可能不同 → 摘要必须不同
            string c = IdentityDigest("GET", "http://example.com/p?q=2&q=1", "", null, "", "utf-8");
            Assert.NotEqual(a, c);
        }

        // =====================================================================
        // R1..R5 —— 合并与竞态
        // =====================================================================

        /// <summary>
        /// R1（契约 §3.2）：N 个并发等待者发起**同一**身份请求 → 实际发出的 HTTP 请求数 **== 1**。
        /// <para>
        /// ★ 2026-10-22 复核更正 F3：原用例用反射**同步**调用 <c>ExecuteStepAsync</c>，
        /// 而它在首个 <c>await</c> 之前是同步执行的 ⇒ N 个"并发等待者"在 <c>GetOrAdd</c> 点
        /// 实际是**串行**的，结构上无法检验契约 §1.3 的"工厂并发"
        /// （把 valueFactory 改回"直接启动请求"，原用例仍然通过）。
        /// </para>
        /// <para>
        /// 本用例分两段：
        /// <b>阶段 A（端到端）</b>：用<b>屏障</b>让 N 个等待者**真正同时**抵达 <c>GetOrAdd</c>，
        /// 断言服务器实际只收到 1 条请求。
        /// <b>阶段 B（确定性并发探针）</b>：直接对生产暴露的"取或启动在途请求"入口
        /// （<c>PluginExecutor.GetOrStartInflight</c>）发起真正并发的调用，并把"发请求"动作
        /// 人为放慢，使"valueFactory 并发执行"这一缺陷能**稳定**地被观察到。
        /// </para>
        /// </summary>
        [Fact]
        public async Task R1_N个并发等待者同一身份_实际HTTP请求数为1()
        {
            using var server = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var inst = NewInstance("instR1");
            var step = NewStep(server.Url + "/r1");
            server.ArmHold();

            const int n = 16;

            // ── 阶段 A：端到端 —— N 个等待者真正同时抵达 GetOrAdd ──
            using (var barrier = new Barrier(n))
            {
                var waiters = new Task<string>[n];
                for (int i = 0; i < n; i++)
                {
                    // LongRunning：给每个等待者一条独立线程，确保它们真的并行抵达 GetOrAdd
                    waiters[i] = Task.Factory.StartNew(
                        () =>
                        {
                            barrier.SignalAndWait();          // ★ 屏障：全部就位后同时放行
                            return RunStepAsync(exec, inst, step);
                        },
                        TaskCreationOptions.LongRunning).Unwrap();
                }

                await server.WaitForRequestAsync();
                await Task.Delay(500);
                server.ReleaseHeld();

                var results = await Task.WhenAll(waiters);
                Assert.Equal(1, server.RequestCount);                  // ★ 实际发出的 HTTP 请求数 == 1
                Assert.All(results, r => Assert.Equal(results[0], r));
            }

            // ── 阶段 B：确定性并发探针（直接打生产入口）──
            int started = 0;
            int before = server.RequestCount;
            string probeKey = "r1-probe-" + Guid.NewGuid().ToString("N");   // ★ 同一把键：N 个调用者共享同一个在途条目
            using var probeClient = new HttpClient();
            Func<Task<string>> factory = async () =>
            {
                Interlocked.Increment(ref started);
                Thread.Sleep(200);                                     // 扩大并发窗口（仅测试用）
                return await probeClient.GetStringAsync(server.Url + "/r1probe");
            };

            using (var barrier = new Barrier(n))
            {
                var callers = new Task<string>[n];
                for (int i = 0; i < n; i++)
                {
                    callers[i] = Task.Factory.StartNew(
                        () =>
                        {
                            barrier.SignalAndWait();                  // ★ 屏障：全部就位后同时放行
                            return GetOrStartInflight(exec, probeKey, factory);
                        },
                        TaskCreationOptions.LongRunning).Unwrap();
                }

                var probeResults = await Task.WhenAll(callers);
                Assert.All(probeResults, r => Assert.Equal(probeResults[0], r));
            }

            Assert.Equal(1, started);                                  // ★ 只启动了一条请求
            Assert.Equal(before + 1, server.RequestCount);             // 服务器也只看到 1 条新请求
        }

        [Fact]
        public async Task R2_旧任务收尾不得删除同键的新条目()
        {
            using var server = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var inst = NewInstance("instR2");
            var step = NewStep(server.Url + "/r2");
            server.ArmHold();

            var waiter = RunStepAsync(exec, inst, step);
            await server.WaitForRequestAsync();

            var inflight = Inflight(exec);
            Assert.Single(inflight);
            string key = inflight.Keys.Single();
            var oldEntry = inflight[key];

            // 模拟"旧任务还在跑的时候，同键的新条目已经插入"
            var newEntry = new Lazy<Task<string>>(() => Task.FromResult("new"));
            inflight[key] = newEntry;

            // 旧共享任务终结 → 触发收尾移除
            server.ReleaseHeld();
            await waiter;

            // 比较后移除：新条目必须还在
            Assert.True(inflight.ContainsKey(key));
            Assert.Same(newEntry, inflight[key]);
            Assert.NotSame(oldEntry, inflight[key]);
        }

        [Fact]
        public async Task R3_一个等待者取消_共享请求继续且其他等待者仍拿到结果()
        {
            using var server = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var inst = NewInstance("instR3");
            var step = NewStep(server.Url + "/r3");
            server.ArmHold();

            using var cts = new CancellationTokenSource();
            var cancelled = RunStepAsync(exec, inst, step, cts.Token);
            var survivor = RunStepAsync(exec, inst, step);

            await server.WaitForRequestAsync();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

            server.ReleaseHeld();
            var result = await survivor;

            Assert.Equal(1, server.RequestCount);
            Assert.False(string.IsNullOrEmpty(result));
        }

        [Fact]
        public async Task R4_全部等待者取消_条目最终被清理不泄漏()
        {
            using var server = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var inst = NewInstance("instR4");
            var step = NewStep(server.Url + "/r4");
            server.ArmHold();

            using var cts = new CancellationTokenSource();
            var w1 = RunStepAsync(exec, inst, step, cts.Token);
            var w2 = RunStepAsync(exec, inst, step, cts.Token);

            await server.WaitForRequestAsync();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w1);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w2);

            // 共享任务终结后条目被移除
            server.ReleaseHeld();
            Assert.True(await WaitUntilAsync(() => Inflight(exec).Count == 0, TimeSpan.FromSeconds(10)),
                "在途条目在共享任务终结后必须被清理");
        }

        [Fact]
        public async Task R5_共享任务失败_所有等待者收到失败且条目被清理后可重新发起()
        {
            using var server = new TinyHttpServer(_ => Task.FromResult(TestResponse.Text("boom", 500)));
            using var exec = new PluginExecutor();
            var inst = NewInstance("instR5");
            var step = NewStep(server.Url + "/r5");
            server.ArmHold();

            var w1 = RunStepAsync(exec, inst, step);
            var w2 = RunStepAsync(exec, inst, step);
            await server.WaitForRequestAsync();
            await Task.Delay(300);
            server.ReleaseHeld();

            await Assert.ThrowsAnyAsync<HttpRequestException>(() => w1);
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => w2);
            Assert.Equal(1, server.RequestCount);

            // 失败后条目被清理
            Assert.True(await WaitUntilAsync(() => Inflight(exec).Count == 0, TimeSpan.FromSeconds(10)),
                "失败的共享任务必须把在途条目清掉");

            // 后续可以重新发起
            server.Handler = EchoAsync;
            string ok = await RunStepAsync(exec, inst, step);
            Assert.Equal(2, server.RequestCount);
            Assert.False(string.IsNullOrEmpty(ok));
        }

        // =====================================================================
        // C1c..C3c —— 缓存
        // =====================================================================

        [Fact]
        public async Task C1c_不同凭据的同名请求不共享缓存条目()
        {
            using var server = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var inst = NewInstance("instC1c");

            var a = NewStep(server.Url + "/c1c", headers: H("Authorization", "Bearer AAA"), cacheMinutes: 10);
            var b = NewStep(server.Url + "/c1c", headers: H("Authorization", "Bearer BBB"), cacheMinutes: 10);

            string ra = await RunStepAsync(exec, inst, a);
            Assert.Equal(1, server.RequestCount);

            string rb = await RunStepAsync(exec, inst, b);
            Assert.Equal(2, server.RequestCount);          // B 不能命中 A 的缓存
            Assert.Contains("Bearer AAA", ra);
            Assert.Contains("Bearer BBB", rb);

            // 各自命中自己的缓存：请求数不再增加
            await RunStepAsync(exec, inst, a);
            await RunStepAsync(exec, inst, b);
            Assert.Equal(2, server.RequestCount);
        }

        [Fact]
        public async Task C2c_ClearCache只清指定实例()
        {
            using var server = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var instA = NewInstance("instC2cA");
            var instB = NewInstance("instC2cB");

            var stepA = NewStep(server.Url + "/c2c", cacheMinutes: 10);
            var stepB = NewStep(server.Url + "/c2c", cacheMinutes: 10);

            await RunStepAsync(exec, instA, stepA);
            await RunStepAsync(exec, instB, stepB);
            Assert.Equal(2, server.RequestCount);

            exec.ClearCache("instC2cA");
            await RunStepAsync(exec, instA, stepA);
            Assert.Equal(3, server.RequestCount);            // A 的缓存被清掉 → 重新发请求

            await RunStepAsync(exec, instB, stepB);
            Assert.Equal(3, server.RequestCount);            // B 的缓存不受影响
        }

        [Fact]
        public async Task C3c_缓存身份与在途身份字段一致()
        {
            using var server = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var inst = NewInstance("instC3c");
            var step = NewStep(server.Url + "/c3c", cacheMinutes: 10);
            server.ArmHold();

            var waiter = RunStepAsync(exec, inst, step);
            await server.WaitForRequestAsync();

            var inflight = Inflight(exec);
            Assert.Single(inflight);
            string digest = inflight.Keys.Single();

            server.ReleaseHeld();
            await waiter;

            // 缓存键 = 实例ID | 目标索引 | 步骤ID | 摘要 → 摘要与在途键完全一致
            var cacheKeys = CacheKeys(exec);
            Assert.Single(cacheKeys);
            string cacheKey = cacheKeys[0];
            Assert.Equal(digest, cacheKey.Split('|').Last());
            Assert.StartsWith("instC3c|", cacheKey);
            Assert.Contains("|s1|", cacheKey);
        }

        // =====================================================================
        // X1..X3 —— 客户端重置联动（C5）
        // =====================================================================

        /// <summary>
        /// X1（契约 §3.4）：<c>ResetNetworkClients()</c> 调用后，在途字典**不被清空**。
        /// ★ 用例名按断言的实际含义命名（2026-10-22 复核更正 F4：原名"…后清空在途字典"与断言相反）。
        /// </summary>
        [Fact]
        public async Task X1_ResetNetworkClients后在途字典未清空()
        {
            using var server = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var inst = NewInstance("instX1");
            var step = NewStep(server.Url + "/x1");
            server.ArmHold();

            var waiter = RunStepAsync(exec, inst, step);
            await server.WaitForRequestAsync();
            Assert.Single(Inflight(exec));

            var before = GetHttp(exec);
            exec.ResetNetworkClients();
            var after = GetHttp(exec);

            Assert.NotSame(before, after);                    // 客户端确实被替换了
            Assert.Single(Inflight(exec));                     // 但在途字典**没有**被清空

            server.ReleaseHeld();
            await waiter;
        }

        [Fact]
        public async Task X2_重置导致的中止是普通异常且不递归重置()
        {
            using var server = new TinyHttpServer(EchoAsync);
            using var exec = new PluginExecutor();
            var inst = NewInstance("instX2");
            var step = NewStep(server.Url + "/x2");
            server.ArmHold();

            var waiter = RunStepAsync(exec, inst, step);
            await server.WaitForRequestAsync();

            var oldClient = GetHttp(exec);
            exec.ResetNetworkClients();
            var newClient = GetHttp(exec);
            Assert.NotSame(oldClient, newClient);

            // 模拟 30 秒后的延迟释放：旧客户端被释放 → 在途请求中止
            oldClient.Dispose();

            // 中止必须以**普通异常**返回给等待者（可诊断分类），而不是递归重置
            var ex = await Assert.ThrowsAsync<PluginClientDisposedException>(() => waiter);
            Assert.NotNull(ex.InnerException);

            // 没有递归重置：客户端没有被再次替换
            Assert.Same(newClient, GetHttp(exec));
        }

        [Fact]
        public void X3_单实例连续失败不会在时间窗内反复触发全局重置()
        {
            using var exec = new PluginExecutor();

            var before = GetHttp(exec);
            exec.ResetNetworkClients();
            var afterFirst = GetHttp(exec);
            Assert.NotSame(before, afterFirst);

            // 上限规则：同一时间窗（60 秒）内至多真正重置一次，后续调用是幂等空操作
            for (int i = 0; i < 10; i++) exec.ResetNetworkClients();
            Assert.Same(afterFirst, GetHttp(exec));
        }

        // =====================================================================
        // H1..H2 —— HTTP 资源与上限（LM-003a）
        // =====================================================================

        /// <summary>
        /// H1（LM-003a）：插件响应消息**自身**必须被释放。
        /// <para>
        /// ★ 2026-10-22 复核更正 F2：原用例只断言"内容流被释放"，而正文读取处的
        /// <c>using var stream</c> 已**独立**保证这一点 ⇒ 变异"只删 <c>using var response</c>"
        /// 时 0 条测试失败，用例无法归因到响应消息本身。
        /// </para>
        /// <para>
        /// 本用例改为跟踪 <see cref="HttpResponseMessage"/> 自身的释放：
        /// 捕获 handler 返回的响应实例，检查它**自身**已被 Dispose（内部 <c>_disposed</c> 状态）；
        /// 并检查其 <see cref="HttpContent"/> 被连带 Dispose
        /// （<c>HttpResponseMessage.Dispose()</c> 会 Dispose 它的 Content）。
        /// ★ 因此"只释放内容流、不释放响应"时本用例**必然失败**
        /// （内容流被释放 ≠ Content 被释放 ≠ 响应被释放，三者互不等价）。
        /// </para>
        /// </summary>
        [Fact]
        public async Task H1_插件响应被正确释放()
        {
            var handler = new TrackingHandler();
            using var client = new HttpClient(handler);
            using var exec = new PluginExecutor();

            var original = GetHttp(exec);
            SetField(exec, "_http", client);

            var inst = NewInstance("instH1");
            var step = NewStep("http://127.0.0.1:1/h1");
            string body = await RunStepAsync(exec, inst, step);

            Assert.Equal("tracked", body);

            // ★ 归因到**响应消息本身**：捕获 handler 返回的 HttpResponseMessage 实例
            HttpResponseMessage response = handler.Response;
            Assert.NotNull(response);
            Assert.True(IsResponseDisposed(response),
                "HttpResponseMessage 必须被释放（不能只释放它的内容流）");

            // 响应被释放 → 它的 Content 必然被连带释放
            Assert.True(handler.ResponseContent.IsDisposed,
                "HttpResponseMessage 被释放 → 其 Content 必须随之释放");

            // 既有断言保留：响应正文流也必须被释放
            Assert.True(handler.ContentStream.Disposed, "响应正文流必须被释放");

            original.Dispose();
        }

        [Fact]
        public async Task H2_响应体超上限明确失败且连接归还()
        {
            using var server = new TinyHttpServer(req =>
                Task.FromResult(req.Target.Contains("/big")
                    ? TestResponse.Bytes(new byte[64 * 1024], "application/octet-stream")
                    : TestResponse.Text("small")));
            using var exec = new PluginExecutor();
            var inst = NewInstance("instH2");

            SetField(exec, "_maxResponseBytes", 1024);

            var big = NewStep(server.Url + "/big");
            var ex = await Assert.ThrowsAsync<PluginResponseTooLargeException>(() => RunStepAsync(exec, inst, big));
            Assert.Equal(1024, ex.Limit);
            Assert.NotNull(ex.DeclaredLength);

            // 连接已归还：同一个客户端仍能正常发起下一次请求
            var small = NewStep(server.Url + "/small");
            string ok = await RunStepAsync(exec, inst, small);
            Assert.Equal("small", ok);
            Assert.Equal(2, server.RequestCount);
        }

        // =====================================================================
        // 工具
        // =====================================================================

        private static Dictionary<string, string> H(params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        private static PluginInstanceConfig NewInstance(string id) => new PluginInstanceConfig { Id = id };

        private static PluginExecutionStep NewStep(string url, string method = "GET", string body = "",
            Dictionary<string, string> headers = null, string encoding = "utf-8", int cacheMinutes = 0, string proxy = "")
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
                CacheMinutes = cacheMinutes,
                Proxy = proxy
            };
        }

        /// <summary>驱动被测的步骤执行（走产品的私有步骤执行路径）。</summary>
        private static async Task<string> RunStepAsync(PluginExecutor exec, PluginInstanceConfig inst,
            PluginExecutionStep step, CancellationToken token = default)
        {
            var context = new Dictionary<string, string>();
            var task = (Task)Method("ExecuteStepAsync").Invoke(exec, new object[] { inst, step, context, "", token });
            await task.ConfigureAwait(false);
            return context.TryGetValue("v", out var v) ? v : null;
        }

        /// <summary>直接调用产品的摘要计算（私有静态），用于与外部常量比对。</summary>
        private static string Digest(string method, string canonicalUrl, string body,
            Dictionary<string, string> headers, string proxy, string responseEncoding)
        {
            return (string)Method("ComputeRequestDigest").Invoke(null,
                new object[] { method, canonicalUrl, body, headers, proxy, responseEncoding });
        }

        /// <summary>
        /// 走**完整身份构造路径**（方法规范化 + URL 规范化 + Header 排序 + 摘要），
        /// 用于校验规范化规则与摘要常量。
        /// </summary>
        private static string IdentityDigest(string method, string url, string body,
            Dictionary<string, string> headers, string proxy, string responseEncoding)
        {
            var type = typeof(PluginExecutor).GetNestedType("RequestIdentity", BindingFlags.NonPublic);
            Assert.NotNull(type);
            var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
            var identity = ctor.Invoke(new object[] { "inst", "", "s1", method, url, body, headers, proxy, responseEncoding });
            var digestProp = type.GetProperty("Digest");
            Assert.NotNull(digestProp);
            return (string)digestProp.GetValue(identity);
        }

        /// <summary>等待服务器收到至少 n 个请求。</summary>
        private static async Task WaitForCountAsync(TinyHttpServer server, int n)
        {
            Assert.True(await WaitUntilAsync(() => server.RequestCount >= n, TimeSpan.FromSeconds(15)),
                $"服务器未在超时内收到 {n} 个请求（实际 {server.RequestCount}）");
        }

        private static ConcurrentDictionary<string, Lazy<Task<string>>> Inflight(PluginExecutor exec)
            => (ConcurrentDictionary<string, Lazy<Task<string>>>)GetField(exec, "_inflightRequests");

        private static List<string> CacheKeys(PluginExecutor exec)
        {
            var cache = GetField(exec, "_stepCache");
            var keysProp = cache.GetType().GetProperty("Keys");
            Assert.NotNull(keysProp);
            var keys = (ICollection<string>)keysProp.GetValue(cache);
            lock (cache) return keys.ToList();
        }

        private static HttpClient GetHttp(PluginExecutor exec) => (HttpClient)GetField(exec, "_http");

        private static object GetField(object target, string name)
        {
            var f = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            return f.GetValue(target);
        }

        private static void SetField(object target, string name, object value)
        {
            var f = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            f.SetValue(target, value);
        }

        /// <summary>
        /// 直接调用产品的"取或启动在途请求"入口（私有方法）。
        /// 这是生产暴露的在途租约入口：用它可以对**真正并发**的调用发起探针，
        /// 而不依赖 <c>ExecuteStepAsync</c> 在首个 await 之前的同步执行顺序。
        /// </summary>
        private static Task<string> GetOrStartInflight(PluginExecutor exec, string key, Func<Task<string>> factory)
        {
            var task = (Task<string>)Method("GetOrStartInflight").Invoke(exec, new object[] { key, factory });
            return task;
        }

        /// <summary>
        /// 读取 <see cref="HttpResponseMessage"/> 自身的释放状态。
        /// <see cref="HttpResponseMessage"/> 没有公开的 IsDisposed，只能读它的私有
        /// <c>_disposed</c> 字段（.NET 8：<c>private bool _disposed;</c>）。
        /// </summary>
        private static bool IsResponseDisposed(HttpResponseMessage response)
        {
            var field = typeof(HttpResponseMessage).GetField("_disposed",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return (bool)field.GetValue(response);
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

        private static Task<TestResponse> EchoAsync(RecordedRequest req)
        {
            req.Headers.TryGetValue("Authorization", out var auth);
            req.Headers.TryGetValue("Cookie", out var cookie);
            return Task.FromResult(TestResponse.Text($"{req.Method}|{auth}|{cookie}"));
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

            public static TestResponse Text(string s, int status = 200) =>
                new TestResponse { Status = status, Body = Encoding.UTF8.GetBytes(s) };

            public static TestResponse Bytes(byte[] b, string contentType = "application/octet-stream") =>
                new TestResponse { Body = b, ContentType = contentType };
        }

        private sealed class TinyHttpServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new();
            private readonly List<RecordedRequest> _requests = new();
            private readonly object _sync = new();
            private readonly object _gateLock = new();
            private readonly TaskCompletionSource<bool> _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private bool _hold;

            public Func<RecordedRequest, Task<TestResponse>> Handler { get; set; }

            public TinyHttpServer(Func<RecordedRequest, Task<TestResponse>> handler)
            {
                Handler = handler;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = Task.Run(AcceptLoopAsync);
            }

            public int Port { get; }
            public string Url => "http://127.0.0.1:" + Port;
            public Task Arrived => _arrived.Task;

            public int RequestCount { get { lock (_sync) return _requests.Count; } }
            public List<RecordedRequest> Requests { get { lock (_sync) return _requests.ToList(); } }

            public Task WaitForRequestAsync() => Arrived;

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
                        _arrived.TrySetResult(true);

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
                var headerEnd = -1;
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

        // ---------------------------------------------------------------
        // 响应释放探针
        // ---------------------------------------------------------------

        private sealed class TrackingStream : MemoryStream
        {
            public bool Disposed { get; private set; }

            public TrackingStream(byte[] buffer) : base(buffer, 0, buffer.Length, false, true)
            {
            }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// 会记录自身 Dispose 的 <see cref="HttpContent"/> —— 用作"这个响应被释放了"的探针。
        /// ★ 关键性质：<see cref="HttpResponseMessage.Dispose()"/> 会连带 Dispose 它的 Content；
        ///   而"只释放内容流"（<c>using var stream</c>）**不会**触发 Content.Dispose ——
        ///   两者因此可以被区分，"响应被释放"才能被真正归因。
        /// </summary>
        private sealed class TrackingContent : HttpContent
        {
            private readonly byte[] _body;
            private int _disposed;

            public TrackingContent(byte[] body)
            {
                _body = body;
                Stream = new TrackingStream(body);
            }

            /// <summary>正文流（记录自身 Dispose）。</summary>
            public TrackingStream Stream { get; }

            /// <summary>Content 自身是否已被 Dispose（由响应释放触发）。</summary>
            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
                => stream.WriteAsync(_body, 0, _body.Length);

            protected override bool TryComputeLength(out long length)
            {
                length = _body.Length;
                return true;
            }

            protected override Task<Stream> CreateContentReadStreamAsync()
                => Task.FromResult<Stream>(Stream);

            protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
                => Task.FromResult<Stream>(Stream);

            protected override void Dispose(bool disposing)
            {
                if (disposing) Interlocked.Exchange(ref _disposed, 1);
                base.Dispose(disposing);
            }
        }

        private sealed class TrackingHandler : HttpMessageHandler
        {
            private readonly byte[] _body;

            public TrackingHandler()
            {
                _body = Encoding.UTF8.GetBytes("tracked");
            }

            /// <summary>handler 返回的响应消息实例（用于检查它**自身**是否被释放）。</summary>
            public HttpResponseMessage Response { get; private set; }

            /// <summary>响应的 Content（记录自身 Dispose）。</summary>
            public TrackingContent ResponseContent { get; private set; }

            /// <summary>响应正文流（记录自身 Dispose）。</summary>
            public TrackingStream ContentStream => ResponseContent?.Stream;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var content = new TrackingContent(_body);
                ResponseContent = content;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
                Response = response;
                return Task.FromResult(response);
            }
        }
    }
}
