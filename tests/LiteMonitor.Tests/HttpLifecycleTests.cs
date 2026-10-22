using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// HTTP 资源释放与期限的回归护栏（缺陷 LM-003b / LM-018、LM-003c / LM-019，
    /// 对应契约 S1.0 §3.6 的 H3–H6）。
    ///
    /// 盯的是三件会静默劣化的事：
    ///   H3 更新元数据取数时，请求 / 响应 / JsonDocument 三样必须释放；
    ///   H4 ResponseHeadersRead 之后，正文读取必须有**自己**的期限（客户端级 Timeout 管不到这一段）；
    ///   H5 镜像测速的正文读取同样必须有独立取消期限；
    ///   H6 UpdateChecker 的验签实现不许被"顺手改坏"。
    ///
    /// ★ 测试纪律：
    ///   - 不触网：一律用本机回环上的 HttpListener（127.0.0.1 + 系统分配的端口）；
    ///   - 真正执行断言：每条用例都有可观测的判据，不允许"跑通就算过"；
    ///   - 端口由系统分配（先借一个 0 号端口问系统要，再交给 HttpListener），
    ///     不写死端口，避免撞端口或依赖外网可达性。
    /// </summary>
    public class HttpLifecycleTests
    {
        /// <summary>一份"没有有效签名"的 version.json：结构合法，但签名是空的。</summary>
        private const string SampleVersionJson =
            "{\"version\":\"9.9.9\",\"changelog\":\"test\",\"releaseDate\":\"2026-10-22\"," +
            "\"sha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"," +
            "\"signature\":\"\"}";

        // ================================================================
        // H3 · 资源释放
        // ================================================================

        /// <summary>
        /// H3：取数路径上必须能看到三处释放 —— <see cref="HttpRequestMessage"/>、
        /// <see cref="HttpResponseMessage"/>、<see cref="JsonDocument"/>。
        /// </summary>
        /// <remarks>
        /// ★ 这里用 IL 断言而不是"读源码找 using"：IL 层面能证明**编译器真的生成了释放调用**，
        ///   而不是源码里恰好写了个 using 却被重构掉了。
        ///   异步方法的状态机是编译器生成的嵌套类型，真正的逻辑在它的 MoveNext 里。
        /// </remarks>
        [Fact]
        public void 更新元数据取数路径释放了请求响应与Json文档()
        {
            IlInspector.Disassembly dis = DisassembleAsyncMethod(typeof(UpdateChecker), "GetVersionInfoAsync");

            Assert.True(
                DisposesType(dis, typeof(HttpRequestMessage)),
                $"取数路径没有释放 HttpRequestMessage —— 请求消息泄漏了。{DescribeDisposed(dis)}");

            Assert.True(
                DisposesType(dis, typeof(HttpResponseMessage)),
                $"取数路径没有释放 HttpResponseMessage —— 响应消息（及其底层连接/响应流）泄漏了。{DescribeDisposed(dis)}");

            Assert.True(
                DisposesType(dis, typeof(JsonDocument)),
                $"取数路径没有释放 JsonDocument —— JsonDocument 持有的 ArrayPool 缓冲区没归还。{DescribeDisposed(dis)}");
        }

        /// <summary>
        /// H3：运行期验证 —— 取数结束后响应消息（含其内容）确实被释放了。
        /// </summary>
        /// <remarks>
        /// ★ 用一个会记录自身 Dispose 的 HttpContent 当"探针"：
        ///   响应消息被释放 → 它的 Content 被释放 → 探针记录到。
        ///   这条断言直接命中生产路径，不依赖任何源码约定。
        /// </remarks>
        [Fact]
        public async Task 更新元数据取数结束后响应消息被释放()
        {
            var handler = new TrackingHandler(Encoding.UTF8.GetBytes(SampleVersionJson));
            using var client = new HttpClient(handler);

            var result = await UpdateChecker.GetVersionInfoAsync(
                new[] { "http://localhost/version.json" }, client);

            Assert.Null(result);
            Assert.Equal(1, handler.RequestCount);
            Assert.NotNull(handler.ResponseContent);
            Assert.True(
                handler.ResponseContent!.IsDisposed,
                "响应消息没有被释放 —— ResponseHeadersRead 之后它一直攥着底层连接与响应流");
        }

        /// <summary>
        /// H3：运行期验证 —— 取数结束后请求消息被释放了。
        /// </summary>
        /// <remarks>
        /// ★ 探针挂在请求消息的 Content 上：HttpRequestMessage.Dispose() 会连带释放它的 Content。
        ///   同时用一条"直接交给 HttpClient"的对照请求来判断：到底是我们释放了请求，
        ///   还是 HttpClient 自己释放的（若是后者，这条断言就只是端到端状态，不是"我们做的"）。
        /// </remarks>
        [Fact]
        public async Task 更新元数据取数结束后请求消息被释放()
        {
            var handler = new TrackingHandler(Encoding.UTF8.GetBytes(SampleVersionJson));
            using var client = new HttpClient(handler);

            // 对照组：绕过取数路径，直接把一个带探针的请求交给 HttpClient。
            // 在释放请求之前取样，才能看出"到底是谁释放了它"。
            var controlProbe = new TrackingContent(Array.Empty<byte>());
            bool controlDisposedByClient;
            using (var controlRequest = new HttpRequestMessage(HttpMethod.Get, "http://localhost/control"))
            {
                controlRequest.Content = controlProbe;
                using var controlResponse = await client.SendAsync(
                    controlRequest, HttpCompletionOption.ResponseHeadersRead);
                controlDisposedByClient = controlProbe.IsDisposed;
            }

            await UpdateChecker.GetVersionInfoAsync(new[] { "http://localhost/version.json" }, client);

            Assert.NotNull(handler.RequestProbe);
            Assert.True(
                handler.RequestProbe!.IsDisposed,
                "请求消息没有被释放" +
                $"（对照：HttpClient 直接发送的请求，其探针 disposed={controlDisposedByClient}，" +
                "若对照为 false，则说明这次释放确实是取数路径自己做的）");
        }

        // ================================================================
        // H4 · 更新元数据：正文读取的独立期限与字节上限
        // ================================================================

        /// <summary>
        /// H4：正文读取有**独立**期限，且超时能真正中断读取。
        /// </summary>
        /// <remarks>
        /// ★ 服务器"只发响应头、正文永远不发"。
        ///   响应头期限故意给到 30 秒、客户端自身 Timeout 给到 60 秒，
        ///   正文期限只有 0.5 秒 —— 于是只有"正文阶段自建的那份期限"能把它打断。
        ///   若正文读取仍然受响应头期限（或客户端 Timeout）管辖，这条用例会超时失败。
        /// </remarks>
        [Fact]
        public async Task 更新元数据正文读取有独立期限且超时能中断()
        {
            using var server = LoopbackHttpServer.CreateStalling();
            using var client = CreateClient(TimeSpan.FromSeconds(60));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await UpdateChecker.GetVersionInfoAsync(
                new[] { server.Url },
                client: client,
                headersTimeout: TimeSpan.FromSeconds(30),
                bodyTimeout: TimeSpan.FromMilliseconds(500),
                totalTimeout: TimeSpan.FromSeconds(40));
            sw.Stop();

            Assert.Null(result);
            Assert.Equal(UpdateChecker.UpdateCheckFailure.Network, UpdateChecker.LastFailure);
            Assert.NotNull(UpdateChecker.LastFailureDetail);
            Assert.Contains("正文读取阶段超时", UpdateChecker.LastFailureDetail);
            Assert.True(
                sw.Elapsed < TimeSpan.FromSeconds(10),
                $"正文读取没有独立期限：整体耗时 {sw.ElapsedMilliseconds}ms，" +
                "说明它是被响应头期限（30s）或客户端 Timeout（60s）兜住的，而不是正文自己的期限");
        }

        /// <summary>
        /// H4：正文超过字节上限必须**明确失败**，不能读一半照样解析。
        /// </summary>
        [Fact]
        public async Task 更新元数据正文超过字节上限时明确失败()
        {
            byte[] huge = Encoding.UTF8.GetBytes(new string('A', 64 * 1024));
            using var server = LoopbackHttpServer.CreateReturning(huge);
            using var client = CreateClient(TimeSpan.FromSeconds(60));

            var result = await UpdateChecker.GetVersionInfoAsync(
                new[] { server.Url },
                client: client,
                headersTimeout: TimeSpan.FromSeconds(10),
                bodyTimeout: TimeSpan.FromSeconds(5),
                totalTimeout: TimeSpan.FromSeconds(20),
                maxBodyBytes: 1024);

            Assert.Null(result);
            Assert.Equal(UpdateChecker.UpdateCheckFailure.Network, UpdateChecker.LastFailure);
            Assert.NotNull(UpdateChecker.LastFailureDetail);
            Assert.Contains("字节上限", UpdateChecker.LastFailureDetail);
        }

        /// <summary>
        /// 三段期限必须是三段，不能塌成一个。
        /// </summary>
        /// <remarks>
        /// 这条把"连接与响应头期限 / 正文读取期限 / 总期限"的具体口径钉住：
        /// 总期限必须大于两个阶段期限（否则总期限会先触发，等于用单一超时糊弄三段）。
        /// </remarks>
        [Fact]
        public void 更新元数据三段期限彼此独立且总期限最大()
        {
            TimeSpan headers = UpdateChecker.MetadataHeadersTimeout;
            TimeSpan body = UpdateChecker.MetadataBodyTimeout;
            TimeSpan total = UpdateChecker.MetadataTotalTimeout;

            Assert.True(headers > TimeSpan.Zero, "连接与响应头期限必须为正");
            Assert.True(body > TimeSpan.Zero, "正文读取期限必须为正");
            Assert.True(total > headers, $"总期限 {total} 必须大于响应头期限 {headers}");
            Assert.True(total > body, $"总期限 {total} 必须大于正文期限 {body}");
            Assert.True(UpdateChecker.MetadataMaxBodyBytes > 0, "正文字节上限必须为正");
        }

        // ================================================================
        // H5 · 镜像测速：正文读取的独立取消期限
        // ================================================================

        /// <summary>
        /// H5：竞速阶段的正文读取有独立期限 —— 服务器只发响应头、正文永远不来时，
        /// 竞速不能被挂死。
        /// </summary>
        /// <remarks>
        /// ★ 响应头期限给到 30 秒、竞速窗口给到 30 秒，只有 0.5 秒的正文无进展期限能打断它。
        ///   全部源失败时 <c>PickFastestAsync</c> 回落到第一个源（既有语义）。
        /// </remarks>
        [Fact]
        public async Task 镜像测速竞速阶段正文读取有独立期限()
        {
            using var server = LoopbackHttpServer.CreateStalling();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string best = await NetworkSpeedTester.PickFastestAsync(
                new[] { server.Url },
                raceTimeMs: 30000,
                isDownload: true,
                headersTimeout: TimeSpan.FromSeconds(30),
                bodyStallTimeout: TimeSpan.FromMilliseconds(500),
                totalTimeout: TimeSpan.FromSeconds(30));
            sw.Stop();

            Assert.Equal(server.Url, best);
            Assert.True(
                sw.Elapsed < TimeSpan.FromSeconds(10),
                $"镜像测速的正文读取没有独立期限：整体耗时 {sw.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// H5：测速工作线程在正文停滞之后会**重试**，而不是一路挂到测速结束。
        /// </summary>
        /// <remarks>
        /// ★ 判据是"服务器收到了几次工作线程的请求"：
        ///   工作线程发的请求不带浏览器 User-Agent（竞速阶段的才带），
        ///   据此把两类请求分开计数。
        ///   正文停滞期限触发后，工作线程必须重新发起请求；
        ///   若正文读取仍被"总期限"独占，这个线程会一直卡在第一次读上，直到整次测速结束。
        /// </remarks>
        [Fact]
        public async Task 镜像测速工作线程在正文停滞后重试而不是挂死()
        {
            using var server = LoopbackHttpServer.CreateStalling();

            double speed = await NetworkSpeedTester.TestDownloadAsyncCore(
                new[] { server.Url },
                durationSec: 4,
                threads: 1,
                progress: null,
                headersTimeout: TimeSpan.FromSeconds(20),
                bodyStallTimeout: TimeSpan.FromMilliseconds(500),
                totalTimeout: TimeSpan.FromSeconds(20));

            Assert.True(speed >= 0);
            Assert.True(
                server.WorkerRequestCount >= 2,
                $"工作线程只发出了 {server.WorkerRequestCount} 次请求 —— " +
                "正文停滞后没有重试，说明线程被挂住直到测速结束");
        }

        /// <summary>
        /// H5：更新器自己的镜像测速探针，正文读取同样有独立期限。
        /// </summary>
        [Fact]
        public async Task 更新器镜像测速正文读取有独立期限()
        {
            using var server = LoopbackHttpServer.CreateStalling();
            using var client = CreateClient(TimeSpan.FromSeconds(60));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (speed, error) = await UpdateChecker.TestMirrorSpeedAsync(
                server.Url,
                client: client,
                headersTimeout: TimeSpan.FromSeconds(30),
                bodyTimeout: TimeSpan.FromMilliseconds(500));
            sw.Stop();

            Assert.Equal(0, speed);
            Assert.NotNull(error);
            Assert.True(
                sw.Elapsed < TimeSpan.FromSeconds(10),
                $"更新器镜像测速的正文读取没有独立期限：整体耗时 {sw.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// 回归护栏：正常响应时正文照读不误（别把期限改成"永远超时"）。
        /// </summary>
        [Fact]
        public async Task 镜像测速正常响应时能读到正文()
        {
            byte[] payload = new byte[64 * 1024];
            new Random(20261022).NextBytes(payload);
            using var server = LoopbackHttpServer.CreateReturning(payload);

            string best = await NetworkSpeedTester.PickFastestAsync(
                new[] { server.Url }, raceTimeMs: 1000, isDownload: true);

            Assert.Equal(server.Url, best);
            Assert.True(server.RequestCount >= 1);
        }

        /// <summary>
        /// 回归护栏：更新器镜像测速探针在正常响应时能算出速度。
        /// </summary>
        [Fact]
        public async Task 更新器镜像测速正常响应时能算出速度()
        {
            byte[] payload = new byte[32 * 1024];
            new Random(7).NextBytes(payload);
            using var server = LoopbackHttpServer.CreateReturning(payload);
            using var client = CreateClient(TimeSpan.FromSeconds(60));

            var (speed, error) = await UpdateChecker.TestMirrorSpeedAsync(server.Url, client: client);

            Assert.Null(error);
            Assert.True(speed > 0, $"正常响应必须算出速度，实际 {speed}");
        }

        // ================================================================
        // H6 · 验签实现未被改动
        // ================================================================

        /// <summary>
        /// H6：内置验签公钥没被换过。
        /// </summary>
        [Fact]
        public void 内置验签公钥未被改动()
        {
            FieldInfo? field = typeof(UpdateChecker).GetField(
                "UpdateSigningPublicKeySpkiBase64", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(field);
            Assert.Equal(
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAElF/yF1mc8EdJOeUhr/u5vxFJQXVm7oI1cEDuugjRmj5vWM9b14bZ5os1OZD9pDm0Gvj8GXevjHnl10G7kMeOuw==",
                (string?)field!.GetRawConstantValue());
        }

        /// <summary>
        /// H6：被签名的载荷格式没被改（跨语言协议，改了就静默失效）。
        /// </summary>
        [Fact]
        public void 验签载荷格式未被改动()
        {
            string sha = new string('A', 64);
            Assert.Equal(
                $"1.0.1\n{sha}\n2026-10-21",
                UpdateChecker.BuildUpdateSignaturePayload("1.0.1", sha, "2026-10-21"));
        }

        /// <summary>
        /// H6：取数路径仍然以验签为闸门 —— 未签名的元数据必须被拒。
        /// </summary>
        [Fact]
        public void 取数路径仍然以验签为闸门()
        {
            IlInspector.ScanResult scan = ScanAsyncMethod("GetVersionInfoAsync");

            Assert.True(
                scan.Calls.Any(c => c.DeclaringType == typeof(UpdateChecker)
                                 && c.Name == "TryVerifyUpdateSignature"),
                "取数路径里找不到 TryVerifyUpdateSignature 的调用 —— 验签闸门被拆掉了");
        }

        /// <summary>
        /// H6：未签名的 version.json 必须被拒（并且归类为"来源不可信"，而不是"网络不通"）。
        /// </summary>
        [Fact]
        public async Task 未签名的更新元数据被拒绝()
        {
            using var server = LoopbackHttpServer.CreateReturning(Encoding.UTF8.GetBytes(SampleVersionJson));
            using var client = CreateClient(TimeSpan.FromSeconds(60));

            var result = await UpdateChecker.GetVersionInfoAsync(new[] { server.Url }, client: client);

            Assert.Null(result);
            Assert.Equal(UpdateChecker.UpdateCheckFailure.Signature, UpdateChecker.LastFailure);
        }

        // ================================================================
        // helper
        // ================================================================


        private static HttpClient CreateClient(TimeSpan timeout)
            => new HttpClient(new SocketsHttpHandler()) { Timeout = timeout };

        /// <summary>扫描某个异步方法的状态机 MoveNext（真正的逻辑体在那里）。</summary>
        private static IlInspector.ScanResult ScanAsyncMethod(string methodName)
        {
            MethodBase? moveNext = FindMoveNext(typeof(UpdateChecker), methodName);
            Assert.NotNull(moveNext);
            return IlInspector.Scan(moveNext!);
        }

        /// <summary>取某个异步方法生成的状态机里的 MoveNext。</summary>
        private static MethodBase? FindMoveNext(Type owner, string methodName)
        {
            foreach (Type nested in owner.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (!nested.Name.StartsWith("<" + methodName + ">", StringComparison.Ordinal)) continue;

                MethodInfo? moveNext = nested.GetMethod(
                    "MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (moveNext != null) return moveNext;
            }
            return null;
        }

        /// <summary>反汇编某个异步方法的状态机 MoveNext。</summary>
        private static IlInspector.Disassembly DisassembleAsyncMethod(Type owner, string methodName)
        {
            MethodBase? moveNext = FindMoveNext(owner, methodName);
            Assert.NotNull(moveNext);
            return IlInspector.Disassemble(moveNext!);
        }

        /// <summary>
        /// 判断方法体里是否**对指定类型的对象**调用了 Dispose。
        /// </summary>
        /// <remarks>
        /// ★ 编译器把 <c>using</c> 变量的释放统一编译成对 <see cref="IDisposable"/> 的调用，
        ///   光看调用目标分不出释放的是哪一个对象。
        ///   但 <c>using</c> 变量会被提升成状态机的字段，释放前必然先 <c>ldfld</c> 那个字段 ——
        ///   所以"Dispose 调用前面那条 ldfld 的字段类型"就是被释放对象的真实类型。
        /// </remarks>
        private static bool DisposesType(IlInspector.Disassembly dis, Type owner)
        {
            for (int i = 0; i < dis.Instructions.Count; i++)
            {
                MethodBase? target = dis.Instructions[i].Target;
                if (target == null || target.Name != "Dispose") continue;

                // 往前找最近的一条字段读取：那就是被释放对象所在的字段
                for (int k = i - 1; k >= 0 && k >= i - 4; k--)
                {
                    FieldInfo? field = dis.Instructions[k].Field;
                    if (field == null) continue;

                    // 这一处 Dispose 释放的就是该字段的对象
                    if (field.FieldType == owner) return true;
                    break;      // 释放的是别的对象，继续看下一处 Dispose
                }
            }
            return false;
        }

        /// <summary>诊断信息：这条路径上到底释放了哪些类型的对象。</summary>
        private static string DescribeDisposed(IlInspector.Disassembly dis)
        {
            var types = new System.Collections.Generic.List<string>();
            for (int i = 0; i < dis.Instructions.Count; i++)
            {
                MethodBase? target = dis.Instructions[i].Target;
                if (target == null || target.Name != "Dispose") continue;

                for (int k = i - 1; k >= 0 && k >= i - 4; k--)
                {
                    FieldInfo? field = dis.Instructions[k].Field;
                    if (field != null)
                    {
                        types.Add($"{field.FieldType.Name}({field.Name})");
                        break;
                    }
                }
            }
            return "已释放的对象：" + string.Join(", ", types);
        }

        /// <summary>
        /// 本机回环 HTTP 服务器（HttpListener + 127.0.0.1 + 系统分配的端口）。
        /// </summary>
        /// <remarks>
        /// ★ 只监听 127.0.0.1：用 <c>http://+:port/</c> 之类的通配前缀会要求管理员权限，
        ///   而 <c>http://127.0.0.1:port/</c> 普通用户即可。
        /// ★ 端口先向系统借一个（0 号端口 = 由系统分配），避免写死端口。
        /// </remarks>
        private sealed class LoopbackHttpServer : IDisposable
        {
            private readonly HttpListener _listener = new();
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _pump;
            private int _requests;
            private int _workerRequests;

            private LoopbackHttpServer(Func<HttpListenerContext, CancellationToken, Task> handler)
            {
                Port = GetFreeLoopbackPort();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                _pump = Task.Run(() => PumpAsync(handler));
            }

            internal int Port { get; }

            internal string Url => $"http://127.0.0.1:{Port}/probe";

            internal int RequestCount => Volatile.Read(ref _requests);

            /// <summary>工作线程发出的请求数（不带浏览器 User-Agent 的那些）。</summary>
            internal int WorkerRequestCount => Volatile.Read(ref _workerRequests);

            /// <summary>"只发响应头、正文永远不发"的服务器。</summary>
            internal static LoopbackHttpServer CreateStalling() => new(StallAsync);

            /// <summary>正常返回完整正文的服务器。</summary>
            internal static LoopbackHttpServer CreateReturning(byte[] body)
                => new((ctx, token) => WriteAsync(ctx, body, token));

            private async Task PumpAsync(Func<HttpListenerContext, CancellationToken, Task> handler)
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { break; }   // 监听器已关闭

                    Interlocked.Increment(ref _requests);

                    // 工作线程的请求不带浏览器 User-Agent（竞速阶段的才带）
                    if (string.IsNullOrEmpty(ctx.Request.Headers["User-Agent"]))
                        Interlocked.Increment(ref _workerRequests);

                    _ = Task.Run(async () =>
                    {
                        try { await handler(ctx, _cts.Token); }
                        catch { /* 客户端提前放弃是预期内的 */ }
                        finally
                        {
                            try { ctx.Response.Close(); } catch { }
                        }
                    });
                }
            }

            /// <summary>响应头立刻返回，正文给 1 个字节之后就再也不给。</summary>
            private static async Task StallAsync(HttpListenerContext ctx, CancellationToken token)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength64 = 1024 * 1024;   // 声明 1MB，却永远发不完

                // ★ 必须写至少 1 个字节：HttpListener 的托管实现只在**首次写入**时
                //   才把响应头刷给客户端；一个字节都不写的话，客户端连响应头都等不到，
                //   测的就变成"响应头期限"而不是"正文期限"了。
                ctx.Response.OutputStream.WriteByte(0x41);
                await ctx.Response.OutputStream.FlushAsync();

                await Task.Delay(Timeout.Infinite, token);
            }

            private static async Task WriteAsync(HttpListenerContext ctx, byte[] body, CancellationToken token)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = body.Length;

                await ctx.Response.OutputStream.WriteAsync(body, token);
                await ctx.Response.OutputStream.FlushAsync(token);
            }

            private static int GetFreeLoopbackPort()
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                return port;
            }

            public void Dispose()
            {
                _cts.Cancel();
                try { _listener.Close(); } catch { }
                try { _pump.Wait(TimeSpan.FromSeconds(3)); } catch { }
                _cts.Dispose();
            }
        }

        /// <summary>
        /// 会记录自身 Dispose 的 HttpContent —— 当作"这个响应/请求被释放了"的探针。
        /// </summary>
        private sealed class TrackingContent : HttpContent
        {
            private readonly byte[] _body;
            private int _disposed;

            internal TrackingContent(byte[] body) { _body = body; }

            internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
                => stream.WriteAsync(_body, 0, _body.Length);

            protected override bool TryComputeLength(out long length)
            {
                length = _body.Length;
                return true;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) Interlocked.Exchange(ref _disposed, 1);
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// 拦截所有请求的处理器：记录请求/响应，并给请求挂一个"释放探针"。
        /// </summary>
        private sealed class TrackingHandler : HttpMessageHandler
        {
            private readonly byte[] _body;
            private int _requestCount;

            internal TrackingHandler(byte[] body) { _body = body; }

            internal int RequestCount => Volatile.Read(ref _requestCount);

            internal HttpRequestMessage? LastRequest { get; private set; }

            internal TrackingContent? ResponseContent { get; private set; }

            internal TrackingContent? RequestProbe { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requestCount);
                LastRequest = request;

                // 请求消息被释放时，它的内容会一起被释放 —— 用它当探针。
                // 已经自带内容的请求（对照组）不动它，免得把对照的探针顶掉。
                if (request.Content == null)
                {
                    var probe = new TrackingContent(Array.Empty<byte>());
                    RequestProbe = probe;
                    request.Content = probe;
                }

                var content = new TrackingContent(_body);
                ResponseContent = content;

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                return Task.FromResult(response);
            }
        }
    }
}
