using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
        private readonly string _oldHash;
        private readonly string _oldSalt;

        public WebAuthHardeningTests()
        {
            _cfg = Settings.Load();
            _oldPassword = _cfg.WebServerPassword;
            _oldHash = _cfg.WebServerPasswordHash;
            _oldSalt = _cfg.WebServerPasswordSalt;
        }

        public void Dispose()
        {
            _cfg.WebServerPassword = _oldPassword;
            _cfg.WebServerPasswordHash = _oldHash;
            _cfg.WebServerPasswordSalt = _oldSalt;
        }

        // ================================================================
        // D8 —— 口令的 URL 解码
        // ================================================================

        /// <summary>
        /// 前端用的是 <c>encodeURIComponent</c>（见 WebPageContent 的 <c>'?pwd=' + encodeURIComponent(pwd)</c>），
        /// 所以含空格的口令在线上是 <c>%20</c>。修复前服务端拿 <c>a%20b</c> 去比对 <c>a b</c>，永远失败。
        /// </summary>
        [Fact]
        public async Task 口令含空格时仍能通过鉴权()
        {
            await AssertPasswordAcceptedAsync("pa ss word");
        }

        /// <summary>
        /// 这一组字符正是"口令里最容易被写进去、又最容易被编码搞坏"的那批：
        /// <c>&amp;</c> 会被正则的值捕获截断、<c>%</c> 会造成二次解码歧义、<c>#</c> 若不编码会被当片段分隔符。
        /// </summary>
        [Fact]
        public async Task 口令含URL保留字符时仍能通过鉴权()
        {
            await AssertPasswordAcceptedAsync("a&b=c%d+e#f?g/h");
        }

        [Fact]
        public async Task 口令含中文时仍能通过鉴权()
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
            SetPassword(real);

            await WithServerAsync(async (port, _) =>
            {
                string wrong = await SendHttpAsync(port, "/api/auth/login", method: "POST",
                    body: "{\"password\":\"correct-passworX\"}", contentType: "application/json");
                Assert.Contains("401", FirstLine(wrong));
                Assert.DoesNotContain("Set-Cookie", wrong);

                // 少一个字符（长度不同）
                string shorter = await SendHttpAsync(port, "/api/auth/login", method: "POST",
                    body: "{\"password\":\"correct-passwor\"}", contentType: "application/json");
                Assert.Contains("401", FirstLine(shorter));
                Assert.DoesNotContain("Set-Cookie", shorter);

                // 完全不带口令字段 ⇒ 请求体不合规
                string missing = await SendHttpAsync(port, "/api/auth/login", method: "POST",
                    body: "{}", contentType: "application/json");
                Assert.Contains("400", FirstLine(missing));
                Assert.DoesNotContain("Set-Cookie", missing);

                // 反证：正确口令仍然能登录
                var ok = await LoginAsync(port, real);
                Assert.False(string.IsNullOrEmpty(ok.SessionId));
            });
        }

        /// <summary>
        /// 裸 <c>+</c> 必须按字面量处理，**不能**当成空格。
        /// </summary>
        /// <remarks>
        /// ★ 这条用例是"口令必须被逐字比较"的护栏：
        ///   配置的口令是 <c>a+b</c>，那么 <c>a b</c>（真空格）与 <c>a%20b</c>（字面量）都必须匹配不上。
        ///   旧实现里这条护栏防的是 <c>WebUtility.UrlDecode</c> 把裸 <c>+</c> 当空格；
        ///   新协议口令走 JSON 请求体、不再 URL 编码，但"逐字比较"这条性质必须继续锁住。
        /// </remarks>
        [Fact]
        public async Task 口令里的加号不被当成空格()
        {
            const string real = "a+b";
            SetPassword(real);

            await WithServerAsync(async (port, _) =>
            {
                // ① 原样的加号（字面量）必须匹配
                var login = await LoginAsync(port, real);
                Assert.False(string.IsNullOrEmpty(login.SessionId));

                // ② 真空格 ⇒ 匹配不上
                string asSpace = await SendHttpAsync(port, "/api/auth/login", method: "POST",
                    body: "{\"password\":\"a b\"}", contentType: "application/json");
                Assert.Contains("401", FirstLine(asSpace));

                // ③ 字面的 "%20"（不做任何解码）⇒ 同样匹配不上
                string percent = await SendHttpAsync(port, "/api/auth/login", method: "POST",
                    body: "{\"password\":\"a%20b\"}", contentType: "application/json");
                Assert.Contains("401", FirstLine(percent));
            });
        }

        /// <summary>
        /// 没设口令时：登录（空口令）仍然换取主会话，HTTP API 仍走同一套会话口径。
        /// </summary>
        /// <remarks>
        /// ★ L0（未配置口令）下没有可校验的凭据，但**协议口径保持统一**：
        ///   登录照样签发会话与 CSRF 令牌，HTTP API 照样要求会话 ——
        ///   否则就会多出"第二套鉴权口径"。
        /// </remarks>
        [Fact]
        public async Task 未设置口令时登录仍可换取会话()
        {
            SetPassword(null);

            await WithServerAsync(async (port, _) =>
            {
                var login = await LoginAsync(port, "");
                Assert.False(string.IsNullOrEmpty(login.SessionId));

                // 带会话 ⇒ 200
                string resp = await SendHttpAsync(port, "/api/snapshot", cookie: login.SessionId);
                Assert.Contains("200", FirstLine(resp));

                // 不带会话 ⇒ 401（L0 也不开"无会话直通"的后门）
                string noSession = await SendHttpAsync(port, "/api/snapshot");
                Assert.Contains("401", FirstLine(noSession));
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
            SetPassword(null);
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
            SetPassword(null);

            await WithServerAsync(async (port, server) =>
            {
                SetHeaderReadTimeout(server, TimeSpan.FromMilliseconds(400));

                var login = await LoginAsync(port, "");

                // 连发 5 条：期限是每连接独立的，不该因为前一条而影响后面
                for (int i = 0; i < 5; i++)
                {
                    string resp = await SendHttpAsync(port, "/api/snapshot", cookie: login.SessionId);
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
            SetPassword(null);

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
            SetPassword(null);

            await WithServerAsync(async (port, server) =>
            {
                SetMaxConcurrentConnections(server, 1);

                var login = await LoginAsync(port, "");

                // 用满再放空，重复三轮：若名额泄漏，第二轮之后就会全部被拒
                for (int round = 0; round < 3; round++)
                {
                    string resp = await SendHttpAsync(port, "/api/snapshot", cookie: login.SessionId);
                    Assert.Contains("200", FirstLine(resp));
                }
            });
        }

        // ================================================================
        // 参数污染 —— 重复的会话 Cookie
        // ================================================================

        /// <summary>
        /// 同一请求里出现两次 <c>lmsid</c> 时必须拒绝 —— **即使两个值都正确**。
        /// </summary>
        /// <remarks>
        /// ★ 新协议下凭据走 Cookie（<c>?pwd=</c> 已移除），参数污染的载体随之变成
        ///   <c>Cookie: lmsid=…; lmsid=…</c>。
        ///   风险形态不变：校验环节与使用环节若取了不同的那一个，就会出现
        ///   "用一个会话过校验、用另一个会话干活"。正确做法是**拒绝歧义本身**。
        /// ★ 用例里同时断言"单个 lmsid 仍然通过"：否则 401 可能来自别的原因，测不出真实行为。
        /// ★ 这条对应契约 W2 与验收矩阵 A10。
        /// </remarks>
        [Fact]
        public async Task 重复的lmsidCookie被拒绝()
        {
            SetPassword("dup-pwd");

            await WithServerAsync(async (port, _) =>
            {
                var login = await LoginAsync(port, "dup-pwd");

                // 单个 lmsid 仍然可用
                string single = await SendHttpAsync(port, "/api/snapshot", cookie: login.SessionId);
                Assert.Contains("200", FirstLine(single));

                // 重复的 lmsid ⇒ 一律拒绝
                string duplicated = await SendHttpAsync(port, "/api/snapshot",
                    cookie: login.SessionId + "; lmsid=" + login.SessionId);
                Assert.Contains("401", FirstLine(duplicated));

                // 干扰形态：第一个是错的、第二个是对的 ⇒ 仍然拒绝
                string mixed = await SendHttpAsync(port, "/api/snapshot",
                    cookie: "wrong-session; lmsid=" + login.SessionId);
                Assert.Contains("401", FirstLine(mixed));
            });
        }

        // ================================================================
        // WebSocket 握手 —— Sec-WebSocket-Key 的严格解析
        // ================================================================

        /// <summary>RFC 6455 §1.3 的示例值：解码后正好 16 字节，对应的 Accept 是公开可验证的常量。</summary>
        private const string RfcWsKey = "dGhlIHNhbXBsZSBub25jZQ==";
        private const string RfcWsAccept = "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=";

        /// <summary>
        /// 合法握手必须照常成功，且 Accept 与 RFC 示例一致 —— 这是"没把功能改坏"的护栏。
        /// </summary>
        [Fact]
        public async Task 合法WebSocket握手返回101且Accept正确()
        {
            SetPassword(null);   // L0：没有口令也要走"登录 → 票据"的同一套流程

            await WithServerAsync(async (port, _) =>
            {
                var login = await LoginAsync(port, "");
                string ticket = await RequestTicketAsync(port, login.SessionId, login.Csrf);

                string resp = await SendRawAsync(port,
                    WsHandshake(port, ticket, "Sec-WebSocket-Key: " + RfcWsKey + "\r\n"));

                Assert.Contains("101", FirstLine(resp));
                Assert.Contains(RfcWsAccept, resp);
            });
        }

        /// <summary>
        /// 头名大小写不敏感：HTTP 规定头名按不区分大小写匹配，小写形态同样是合法请求。
        /// </summary>
        [Fact]
        public async Task WebSocketKey头名大小写不敏感()
        {
            SetPassword(null);

            await WithServerAsync(async (port, _) =>
            {
                var login = await LoginAsync(port, "");
                string ticket = await RequestTicketAsync(port, login.SessionId, login.Csrf);

                string resp = await SendRawAsync(port,
                    WsHandshake(port, ticket, "sec-websocket-key: " + RfcWsKey + "\r\n"));

                Assert.Contains("101", FirstLine(resp));
                Assert.Contains(RfcWsAccept, resp);
            });
        }

        /// <summary>
        /// 重复的 <c>Sec-WebSocket-Key</c> 必须拒绝：取第一个还是最后一个决定 Accept 基于谁计算，
        /// 属于解析歧义，不猜。
        /// </summary>
        [Fact]
        public async Task 重复的SecWebSocketKey被拒绝()
        {
            SetPassword(null);

            await WithServerAsync(async (port, _) =>
            {
                var login = await LoginAsync(port, "");
                string ticket = await RequestTicketAsync(port, login.SessionId, login.Csrf);

                string resp = await SendRawAsync(port,
                    WsHandshake(port, ticket,
                        "Sec-WebSocket-Key: " + RfcWsKey + "\r\n" +
                        "Sec-WebSocket-Key: " + RfcWsKey + "\r\n"));

                Assert.DoesNotContain("101", resp);
            });
        }

        /// <summary>
        /// 取值不是「16 字节 Base64」时必须拒绝：回 101 只会把错误推迟到帧解析阶段，
        /// 还会让它占用一个已登记的有效连接名额。
        /// </summary>
        [Theory]
        [InlineData("")]                                   // 头存在但值为空
        [InlineData("not-base64-at-all!!")]                 // 不是 Base64
        [InlineData("AAAA")]                               // 合法 Base64，但只有 3 字节
        public async Task 非法SecWebSocketKey被拒绝(string value)
        {
            SetPassword(null);

            await WithServerAsync(async (port, _) =>
            {
                var login = await LoginAsync(port, "");
                string ticket = await RequestTicketAsync(port, login.SessionId, login.Csrf);

                string resp = await SendRawAsync(port,
                    WsHandshake(port, ticket, "Sec-WebSocket-Key: " + value + "\r\n"));

                Assert.DoesNotContain("101", resp);
            });
        }

        /// <summary>头缺失时必须拒绝，而不是算出一个基于空串的 Accept。</summary>
        [Fact]
        public async Task 缺失SecWebSocketKey被拒绝()
        {
            SetPassword(null);

            await WithServerAsync(async (port, _) =>
            {
                var login = await LoginAsync(port, "");
                string ticket = await RequestTicketAsync(port, login.SessionId, login.Csrf);

                string resp = await SendRawAsync(port, WsHandshake(port, ticket, ""));
                Assert.DoesNotContain("101", resp);
            });
        }

        /// <summary>
        /// 票据缺失 / 无效时握手必须被拒（凭据口径与 HTTP 一致）。
        /// </summary>
        [Fact]
        public async Task 无票据的WebSocket握手被拒绝()
        {
            SetPassword(null);

            await WithServerAsync(async (port, _) =>
            {
                string resp = await SendRawAsync(port,
                    "GET /ws HTTP/1.1\r\n" +
                    $"Host: 127.0.0.1:{port}\r\n" +
                    "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                    "Sec-WebSocket-Key: " + RfcWsKey + "\r\n\r\n");

                Assert.DoesNotContain("101", resp);
                Assert.Contains("401", FirstLine(resp));
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

        /// <summary>
        /// 设置访问口令（按**生产形态**：只写哈希 + 盐，内存里不留明文）。
        /// </summary>
        /// <remarks>
        /// ★ 之所以不直接写明文：迁移后的生产状态就是"只有哈希 + 盐"，
        ///   用例必须跑在真实形态上（顺带避免登录时触发惰性迁移去写磁盘）。
        /// ★ 传 null ⇒ 不配置口令（L0）。
        /// </remarks>
        private void SetPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                _cfg.WebServerPassword = "";
                _cfg.WebServerPasswordHash = "";
                _cfg.WebServerPasswordSalt = "";
                return;
            }
            _cfg.WebServerPasswordSalt = Settings.CreateWebPasswordSalt();
            _cfg.WebServerPasswordHash = Settings.ComputeWebPasswordHash(password, _cfg.WebServerPasswordSalt);
            _cfg.WebServerPassword = "";
        }

        /// <summary>
        /// 按新协议登录：<c>POST /api/auth/login</c> ⇒ (会话 ID, CSRF 令牌)。
        /// </summary>
        private async Task<(string SessionId, string Csrf)> LoginAsync(int port, string password)
        {
            string body = "{\"password\":\"" + JsonEscape(password) + "\"}";
            string resp = await SendHttpAsync(port, "/api/auth/login", method: "POST", body: body, contentType: "application/json");
            Assert.Contains("200", FirstLine(resp));

            string setCookie = FindHeader(resp, "Set-Cookie");
            Assert.False(string.IsNullOrEmpty(setCookie), "登录响应必须下发 lmsid Cookie");

            string sessionId = "";
            int eq = setCookie.IndexOf('=');
            int semi = setCookie.IndexOf(';');
            if (eq > 0 && semi > eq) sessionId = setCookie.Substring(eq + 1, semi - eq - 1);
            Assert.False(string.IsNullOrEmpty(sessionId), "登录响应必须下发 lmsid Cookie");

            string csrf = "";
            int idx = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (idx >= 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(resp.Substring(idx + 4));
                    if (doc.RootElement.TryGetProperty("csrf", out var el)) csrf = el.GetString() ?? "";
                }
                catch (JsonException) { }
            }
            Assert.False(string.IsNullOrEmpty(csrf), "登录响应必须下发 csrf 令牌");
            return (sessionId, csrf);
        }

        /// <summary>申请一次性票据（需要会话 Cookie + CSRF 头）。</summary>
        private async Task<string> RequestTicketAsync(int port, string sessionId, string csrf)
        {
            string resp = await SendHttpAsync(port, "/api/auth/ticket", method: "POST", cookie: sessionId, csrf: csrf);
            Assert.Contains("200", FirstLine(resp));

            int idx = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(resp.Substring(idx + 4));
            string ticket = doc.RootElement.TryGetProperty("ticket", out var el) ? (el.GetString() ?? "") : "";
            Assert.False(string.IsNullOrEmpty(ticket), "票据端点必须返回 ticket");
            return ticket;
        }

        /// <summary>JSON 字符串转义（口令里的引号 / 反斜杠 / 控制字符不能破坏报文）。</summary>
        private static string JsonEscape(string value)
        {
            var sb = new StringBuilder();
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>取某个响应头的值（找不到返回空串）。</summary>
        private static string FindHeader(string response, string name)
        {
            foreach (string line in (response ?? "").Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                {
                    return t.Substring(name.Length + 1).Trim();
                }
            }
            return "";
        }

        /// <summary>
        /// 把口令设成给定值，断言"用**原样**口令登录能取到会话与快照，
        /// 而一个不一样的口令取不到。
        /// </summary>
        /// <remarks>
        /// ★ 新协议里口令走 JSON 请求体（不再进 URL），因此**不存在** URL 编码错配；
        ///   但"口令必须被逐字比较"这条性质仍然要锁住 ——
        ///   所以这里同时断言：原样口令通过、字形相近但不同的口令被拒。
        /// </remarks>
        private async Task AssertPasswordAcceptedAsync(string password)
        {
            SetPassword(password);

            await WithServerAsync(async (port, _) =>
            {
                // ① 原样口令 ⇒ 登录成功并拿到会话
                var login = await LoginAsync(port, password);
                Assert.False(string.IsNullOrEmpty(login.SessionId));

                // ② 用它取快照 ⇒ 200
                string resp = await SendHttpAsync(port, "/api/snapshot", cookie: login.SessionId);
                Assert.Contains("200", FirstLine(resp));

                // ③ 反证：字形相近但不同的口令必须被拒（否则"能登进去"可能只是校验被放行）
                string wrong = await SendHttpAsync(port, "/api/auth/login", method: "POST",
                    body: "{\"password\":\"" + JsonEscape(password + "x") + "\"}",
                    contentType: "application/json");
                Assert.Contains("401", FirstLine(wrong));
                Assert.DoesNotContain("Set-Cookie", wrong);
            });
        }

        /// <summary>取 HTTP 响应状态行（第一行）。</summary>
        private static string FirstLine(string httpResponse)
        {
            int idx = httpResponse.IndexOf('\n');
            return idx < 0 ? httpResponse : httpResponse.Substring(0, idx);
        }

        private static async Task<string> SendHttpAsync(int port, string pathAndQuery,
            string cookie = null, string csrf = null, string method = "GET",
            string body = null, string contentType = null)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();

            var head = new StringBuilder();
            head.Append(method).Append(' ').Append(pathAndQuery).Append(" HTTP/1.1\r\n");
            head.Append($"Host: 127.0.0.1:{port}\r\n");
            if (!string.IsNullOrEmpty(cookie)) head.Append($"Cookie: lmsid={cookie}\r\n");
            if (!string.IsNullOrEmpty(csrf)) head.Append($"X-LiteMonitor-CSRF: {csrf}\r\n");
            if (!string.IsNullOrEmpty(contentType)) head.Append($"Content-Type: {contentType}\r\n");
            if (body != null) head.Append($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
            head.Append("Connection: close\r\n\r\n");
            if (body != null) head.Append(body);

            byte[] req = Encoding.UTF8.GetBytes(head.ToString());
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

        /// <summary>
        /// 发一段原始请求，读到「响应头结束（空行）」、对端关闭或超时为止。
        /// </summary>
        /// <remarks>
        /// ★ 不能直接复用 <see cref="SendHttpAsync"/>：成功握手后服务端会进入帧循环、
        ///   连接保持打开，读到连接关闭为止会一直等到超时。这里见空行就收手。
        /// </remarks>
        private static async Task<string> SendRawAsync(int port, string raw)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();

            byte[] req = Encoding.ASCII.GetBytes(raw);
            await stream.WriteAsync(req, 0, req.Length);
            await stream.FlushAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var sb = new StringBuilder();
            var buf = new byte[4096];
            try
            {
                while (true)
                {
                    int n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                    if (n == 0) break;
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                    if (sb.ToString().Contains("\r\n\r\n")) break;   // 响应头已完整
                }
            }
            catch (IOException) { /* 对端关闭 */ }
            catch (OperationCanceledException) { /* 超时：把已收到的内容交回去 */ }

            return sb.ToString();
        }

        /// <summary>拼一段 WebSocket 升级请求；<paramref name="keyHeader"/> 允许为空或写多行。</summary>
        /// <remarks>
        /// ★ 凭据是**一次性票据**（<c>?ticket=</c>）：浏览器 WebSocket API 不能自定义
        ///   Authorization 头，票据短 TTL + 一次性 + 仅限 WS 三者叠加。
        /// </remarks>
        private static string WsHandshake(int port, string ticket, string keyHeader)
        {
            return $"GET /ws?ticket={ticket} HTTP/1.1\r\n" +
                   $"Host: 127.0.0.1:{port}\r\n" +
                   "Upgrade: websocket\r\n" +
                   "Connection: Upgrade\r\n" +
                   keyHeader +
                   "\r\n";
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
