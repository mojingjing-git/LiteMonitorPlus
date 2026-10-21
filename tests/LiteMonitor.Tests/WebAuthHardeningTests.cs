using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Core;
using LiteMonitor.src.WebServer;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// Web 鉴权与连接处理加固的行为护栏（修复计划 W5 / D8 + D9）。
    ///
    /// 盯住的两类缺陷：
    ///   D8 —— <c>CheckAuth</c> 拿**未解码**的查询串比对原文，并且用提前返回的字符串比较。
    ///         前者让"口令含空格 / &amp; / % / + / 中文"变成**永远登不进去**（不是安全，是坏掉）；
    ///         后者把口令长度和前缀差异泄漏成响应耗时。
    ///   D9 —— <c>NetworkStream.ReadTimeout</c> 对 <c>ReadAsync</c> 完全无效，
    ///         请求头读取没有任何真实期限；同时 <c>_ = HandleClientAsync(...)</c> 没有并发上限。
    ///
    /// 测试纪律：
    ///  - 只连 127.0.0.1 回环，不触外网、不启动 LHM / 驱动；
    ///  - 会改 <c>Settings.Load()</c> 的访问密码 → 挂到 "cfgfile" 集合串行执行；
    ///  - 时间相关的用例靠 <c>internal HeaderReadTimeout</c> 接缝压到毫秒级，不空等 5 秒。
    /// </summary>
    [Collection("cfgfile")]
    public class WebAuthHardeningTests : IDisposable
    {
        private readonly Settings _cfg;
        private readonly string _oldPassword;

        public WebAuthHardeningTests()
        {
            _cfg = Settings.Load();
            _oldPassword = _cfg.WebServerPassword;
        }

        public void Dispose()
        {
            _cfg.WebServerPassword = _oldPassword;
        }

        // ================================================================
        // D8 —— 口令的 URL 解码
        // ================================================================

        /// <summary>
        /// 前端用的是 <c>encodeURIComponent</c>（见 WebPageContent 的 <c>'?pwd=' + encodeURIComponent(pwd)</c>），
        /// 所以含空格的口令在线上是 <c>%20</c>。修复前服务端拿 <c>a%20b</c> 去比对 <c>a b</c>，永远失败。
        /// </summary>
        [Fact]
        public async Task 口令含空格时经URL编码后仍能通过鉴权()
        {
            await AssertPasswordAcceptedAsync("pa ss word");
        }

        /// <summary>
        /// 这一组字符正是"口令里最容易被写进去、又最容易被编码搞坏"的那批：
        /// <c>&amp;</c> 会被正则的值捕获截断、<c>%</c> 会造成二次解码歧义、<c>#</c> 若不编码会被当片段分隔符。
        /// </summary>
        [Fact]
        public async Task 口令含URL保留字符时经URL编码后仍能通过鉴权()
        {
            await AssertPasswordAcceptedAsync("a&b=c%d+e#f?g/h");
        }

        [Fact]
        public async Task 口令含中文时经URL编码后仍能通过鉴权()
        {
            await AssertPasswordAcceptedAsync("口令密码✓");
        }

        /// <summary>
        /// 字形相近但不同的口令必须被拒绝 —— 保证上面的"能登进去"不是因为比较被彻底放行。
        /// </summary>
        [Fact]
        public async Task 错误口令仍被拒绝()
        {
            const string real = "correct-password";
            _cfg.WebServerPassword = real;

            await WithServerAsync(async (port, _) =>
            {
                string wrong = await SendHttpAsync(port, "/api/snapshot?pwd=" + Uri.EscapeDataString("correct-passworX"));
                Assert.Contains("401", FirstLine(wrong));

                // 少一个字符（长度不同）
                string shorter = await SendHttpAsync(port, "/api/snapshot?pwd=" + Uri.EscapeDataString("correct-passwor"));
                Assert.Contains("401", FirstLine(shorter));

                // 完全不带 pwd
                string missing = await SendHttpAsync(port, "/api/snapshot");
                Assert.Contains("401", FirstLine(missing));
            });
        }

        /// <summary>
        /// 裸 <c>+</c> 必须按字面量处理，**不能**当成空格。
        /// </summary>
        /// <remarks>
        /// ★ 这条用例是"选对解码器"的护栏：
        ///   <c>WebUtility.UrlDecode</c> 会把 <c>a+b</c> 解成 <c>a b</c>（表单编码语义），
        ///   于是口令 <c>a+b</c> 的裸加号请求会被判错。
        ///   而 <c>encodeURIComponent</c> 从不产生裸 <c>+</c>（它编成 <c>%2B</c>），
        ///   所以正确实现是 <c>Uri.UnescapeDataString</c>（只解 %XX、不动 +）。
        /// ★ 两条断言一起看才有意义：编码形态 <c>%2B</c> 与裸形态 <c>+</c> 都必须匹配同一份口令。
        /// </remarks>
        [Fact]
        public async Task 口令里的加号不被当成空格()
        {
            const string real = "a+b";
            _cfg.WebServerPassword = real;

            await WithServerAsync(async (port, _) =>
            {
                // ① encodeURIComponent 的产物
                string encoded = await SendHttpAsync(port, "/api/snapshot?pwd=" + Uri.EscapeDataString(real));
                Assert.Contains("200", FirstLine(encoded));

                // ② 手输的裸加号（RFC 3986 里 '+' 就是字面量）
                string bare = await SendHttpAsync(port, "/api/snapshot?pwd=a+b");
                Assert.Contains("200", FirstLine(bare));

                // ③ 反证：若把 '+' 当空格解，则"真的是空格"的口令才该匹配 —— 这里必须**不**匹配
                string asSpace = await SendHttpAsync(port, "/api/snapshot?pwd=a%20b");
                Assert.Contains("401", FirstLine(asSpace));
            });
        }

        /// <summary>
        /// 没设口令时保持既有行为：不需要 pwd 参数就能取快照。
        /// </summary>
        [Fact]
        public async Task 未设置口令时不需要参数()
        {
            _cfg.WebServerPassword = "";

            await WithServerAsync(async (port, _) =>
            {
                string resp = await SendHttpAsync(port, "/api/snapshot");
                Assert.Contains("200", FirstLine(resp));
            });
        }

        // ================================================================
        // D9 —— 请求头读取期限
        // ================================================================

        /// <summary>
        /// 只发一半请求头然后挂住（slowloris），超过期限必须被服务端主动断开。
        /// </summary>
        /// <remarks>
        /// ★ 修复前这条连接会**永远**挂着：<c>ReadTimeout</c> 只作用于同步 <c>Read</c>，
        ///   而这里走的是 <c>ReadAsync</c>。
        /// ★ 断言区间取 [期限的一半, 期限 + 3 秒]：
        ///   下界排除"根本没读就关了"，上界排除"根本没超时机制"。
        /// </remarks>
        [Fact]
        public async Task 慢速连接超过请求头期限被断开()
        {
            _cfg.WebServerPassword = "";
            var timeout = TimeSpan.FromMilliseconds(400);

            await WithServerAsync(async (port, server) =>
            {
                SetHeaderReadTimeout(server, timeout);

                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                using var stream = client.GetStream();

                // 刻意不发结尾的空行 → 请求头永不完整
                byte[] partial = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Pad: ");
                await stream.WriteAsync(partial, 0, partial.Length);
                await stream.FlushAsync();

                var sw = Stopwatch.StartNew();
                bool closed = await WaitForServerCloseAsync(stream, TimeSpan.FromSeconds(5));
                sw.Stop();

                Assert.True(closed,
                    $"读取期限 {timeout.TotalMilliseconds}ms 到点后服务端必须断开这条连接（否则 slowloris 可无限占用）");
                Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(150),
                    $"不应在读任何数据之前就断开（实际 {sw.ElapsedMilliseconds}ms）");
            });
        }

        /// <summary>
        /// 期限只约束"读完请求头"这一段：正常请求必须在期限内**照常完成**。
        /// </summary>
        /// <remarks>
        /// ★ 这条防的是"期限设得太狠或作用域放错"，把正常请求也一起掐掉。
        /// </remarks>
        [Fact]
        public async Task 正常请求不受请求头期限影响()
        {
            _cfg.WebServerPassword = "";

            await WithServerAsync(async (port, server) =>
            {
                SetHeaderReadTimeout(server, TimeSpan.FromMilliseconds(400));

                // 连发 5 条：期限是每连接独立的，不该因为前一条而影响后面
                for (int i = 0; i < 5; i++)
                {
                    string resp = await SendHttpAsync(port, "/api/snapshot");
                    Assert.Contains("200", FirstLine(resp));
                }
            });
        }

        // ================================================================
        // D9 —— 并发上限
        // ================================================================

        /// <summary>
        /// 在途连接数触顶后，新连接必须被立刻断开而不是继续累积。
        /// </summary>
        /// <remarks>
        /// ★ 上限压到 1：第 1 条连接占住名额（发一半请求头，不触发期限），
        ///   第 2 条连接应当连一个字节的响应都拿不到。
        /// ★ 这里**不能**用"请求返回 503"来断言：拒绝发生在读取请求之前，
        ///   此时服务端根本不知道来者说的是 HTTP 还是 WebSocket，只能直接关连接。
        /// </remarks>
        [Fact]
        public async Task 并发连接触顶后新连接被断开()
        {
            _cfg.WebServerPassword = "";

            await WithServerAsync(async (port, server) =>
            {
                SetMaxConcurrentConnections(server, 1);
                SetHeaderReadTimeout(server, TimeSpan.FromSeconds(10));   // 让第 1 条连接活得够久

                // ① 占住唯一名额
                using var hog = new TcpClient();
                await hog.ConnectAsync(IPAddress.Loopback, port);
                using var hogStream = hog.GetStream();
                byte[] partial = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Pad: ");
                await hogStream.WriteAsync(partial, 0, partial.Length);
                await hogStream.FlushAsync();

                // 等 accept 与计数落地
                await Task.Delay(250);

                // ② 第 2 条：完整请求，但应该连不上处理路径
                using var second = new TcpClient();
                await second.ConnectAsync(IPAddress.Loopback, port);
                using var secondStream = second.GetStream();
                byte[] full = Encoding.ASCII.GetBytes(
                    "GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
                try
                {
                    await secondStream.WriteAsync(full, 0, full.Length);
                    await secondStream.FlushAsync();
                }
                catch (IOException)
                {
                    // 已经在写之前就被关了 —— 同样是预期结果
                }

                int received = await ReadAvailableAsync(secondStream, TimeSpan.FromSeconds(3));
                Assert.Equal(0, received);
            });
        }

        /// <summary>
        /// 名额必须归还：连接正常结束后，后来的连接要能继续工作。
        /// </summary>
        /// <remarks>
        /// ★ 这条防的是"计数器只增不减" —— 那样服务跑一阵就会自己把自己锁死，
        ///   而且重启也救不回来（同一进程内计数不清零）。
        /// </remarks>
        [Fact]
        public async Task 连接结束后并发名额被归还()
        {
            _cfg.WebServerPassword = "";

            await WithServerAsync(async (port, server) =>
            {
                SetMaxConcurrentConnections(server, 1);

                // 用满再放空，重复三轮：若名额泄漏，第二轮之后就会全部被拒
                for (int round = 0; round < 3; round++)
                {
                    string resp = await SendHttpAsync(port, "/api/snapshot");
                    Assert.Contains("200", FirstLine(resp));
                }
            });
        }

        // ================================================================
        // 宿主 / helper
        // ================================================================

        /// <summary>起一个只监听回环的 LiteWebServer，跑完断言后必定停止并复位静态实例。</summary>
        private async Task WithServerAsync(Func<int, LiteWebServer, Task> body)
        {
            int port = FindFreePort();

            bool oldEnabled = _cfg.WebServerEnabled;
            int oldPort = _cfg.WebServerPort;
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            try
            {
                using var server = new LiteWebServer(
                    _cfg,
                    samplingRequester: _ => Task.FromResult(true),
                    demandApplier: _ => true);
                UseLoopbackListener(server);

                Assert.True(server.Start(out string err), "LiteWebServer 启动失败: " + err);
                await body(port, server);
            }
            finally
            {
                _cfg.WebServerEnabled = oldEnabled;
                _cfg.WebServerPort = oldPort;
                ResetLiteWebServerInstance();
            }
        }

        /// <summary>把口令设成给定值，断言 <c>encodeURIComponent</c> 编码后的请求能取到 200。</summary>
        private async Task AssertPasswordAcceptedAsync(string password)
        {
            _cfg.WebServerPassword = password;
            string encoded = Uri.EscapeDataString(password);

            // ★ 前提自检：这些口令必须真的被编码过，否则用例会因为"原文与编码串恰好相同"而假绿
            Assert.NotEqual(password, encoded);

            await WithServerAsync(async (port, _) =>
            {
                string resp = await SendHttpAsync(port, "/api/snapshot?pwd=" + encoded);
                Assert.Contains("200", FirstLine(resp));
            });
        }

        /// <summary>取 HTTP 响应状态行（第一行）。</summary>
        private static string FirstLine(string httpResponse)
        {
            int idx = httpResponse.IndexOf('\n');
            return idx < 0 ? httpResponse : httpResponse.Substring(0, idx);
        }

        private static async Task<string> SendHttpAsync(int port, string pathAndQuery)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();

            byte[] req = Encoding.ASCII.GetBytes(
                $"GET {pathAndQuery} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(req, 0, req.Length);
            await stream.FlushAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sb = new StringBuilder();
            var buf = new byte[4096];
            try
            {
                while (true)
                {
                    int n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                    if (n == 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
            }
            catch (IOException) { /* 对端关闭 */ }
            catch (OperationCanceledException) { /* 读超时：把已收到的内容交回去 */ }

            return sb.ToString();
        }

        /// <summary>等服务端关闭这条连接，返回是否在期限内观察到关闭。</summary>
        private static async Task<bool> WaitForServerCloseAsync(NetworkStream stream, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var buf = new byte[1024];
            try
            {
                while (true)
                {
                    int n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                    if (n == 0) return true;   // 正常 FIN
                }
            }
            catch (IOException) { return true; }               // RST 也算关闭
            catch (OperationCanceledException) { return false; } // 期限内没关 → 缺陷仍在
        }

        /// <summary>读一次，返回收到的字节数（0 表示对端已关闭或什么都没发）。</summary>
        private static async Task<int> ReadAvailableAsync(NetworkStream stream, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var buf = new byte[1024];
            try
            {
                return await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
            }
            catch (IOException) { return 0; }
            catch (OperationCanceledException) { return -1; }   // -1 让断言更明确地失败
        }

        private static int FindFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        /// <summary>让被测服务器只监听回环（测试期间不在局域网上暴露服务）。</summary>
        private static void UseLoopbackListener(LiteWebServer server)
        {
            var prop = typeof(LiteWebServer).GetProperty("ListenAddress",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(prop != null, "LiteWebServer 必须提供 internal ListenAddress 测试接缝");
            var setter = prop.GetSetMethod(nonPublic: true);
            Assert.True(setter != null, "LiteWebServer.ListenAddress 必须可写");
            setter.Invoke(server, new object[] { IPAddress.Loopback });
        }

        /// <summary>反射设置 internal 的并发上限接缝（找不到就响亮失败）。</summary>
        private static void SetMaxConcurrentConnections(LiteWebServer server, int max)
        {
            var prop = typeof(LiteWebServer).GetProperty("MaxConcurrentConnections",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(prop != null, "LiteWebServer 必须提供 internal MaxConcurrentConnections 接缝");
            var setter = prop.GetSetMethod(nonPublic: true);
            Assert.True(setter != null, "MaxConcurrentConnections 必须可写");
            setter.Invoke(server, new object[] { max });
        }

        /// <summary>反射设置请求头读取期限（接缝在 WebSessionManager 上）。</summary>
        private static void SetHeaderReadTimeout(LiteWebServer server, TimeSpan timeout)
        {
            var field = typeof(LiteWebServer).GetField("_sessionManager",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(field != null, "LiteWebServer 必须持有 _sessionManager 字段");

            object manager = field.GetValue(server);
            Assert.True(manager != null, "_sessionManager 不能为 null");

            var prop = manager.GetType().GetProperty("HeaderReadTimeout",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(prop != null, "WebSessionManager 必须提供 internal HeaderReadTimeout 接缝");

            var setter = prop.GetSetMethod(nonPublic: true);
            Assert.True(setter != null, "HeaderReadTimeout 必须可写");
            setter.Invoke(manager, new object[] { timeout });
        }

        /// <summary>复位静态 <c>LiteWebServer.Instance</c>，避免把已 Dispose 的实例留给后续用例。</summary>
        private static void ResetLiteWebServerInstance()
        {
            var prop = typeof(LiteWebServer).GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);
            var setter = prop?.GetSetMethod(nonPublic: true);
            setter?.Invoke(null, new object[] { null });
        }
    }
}
