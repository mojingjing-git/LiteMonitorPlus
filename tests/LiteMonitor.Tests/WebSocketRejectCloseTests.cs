using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    /// LM-034：**被拒绝的 WebSocket 握手必须关闭连接**（不得悬挂）。
    ///
    /// <para>盯住的缺陷：<c>WebSessionManager.HandleClientAsync</c> 里，
    /// WebSocket 分支只在握手**成功**时才处理连接；握手**被拒**时
    /// （Host/Origin 校验失败 403、旧 <c>?pwd=</c> 协议 401、票据无效 401、
    /// <c>Sec-WebSocket-Key</c> 非法 400，以及写响应时抛异常），
    /// 既不关闭连接也不发 FIN —— 服务端把 401 写回去之后就让连接挂着，
    /// 直到进程结束或 GC 回收套接字为止。</para>
    ///
    /// <para>后果：每条被拒的握手都白占一个服务端套接字；攒到上限后，
    /// 这个监听端点就再也接不进正常连接（网页服务对该端点不可用）。
    /// 而且错误响应里明明写了 <c>Connection: close</c>，服务端却不关闭 ——
    /// 这本身就是协议违约。</para>
    ///
    /// <para>测试纪律：</para>
    /// <list type="bullet">
    /// <item>只连 127.0.0.1 回环；</item>
    /// <item>会改 <c>Settings.Load()</c> 的 Web 相关字段 ⇒ 挂到 "cfgfile" 集合串行执行；</item>
    /// <item>每个用例都有**外部超时保护**（<see cref="RunGuardedAsync"/>），
    /// 失败时整轮不会挂死；</item>
    /// <item>"读到 EOF"必须**有界**（读到 EOF，而不是等读超时兜底）——
    /// 超时读到 0 字节一律算失败。</item>
    /// </list>
    /// </summary>
    [Collection("cfgfile")]
    public class WebSocketRejectCloseTests : IDisposable
    {
        /// <summary>读一个响应 + 等 EOF 的上限（毫秒）。超时即判定"没关闭"。</summary>
        private const int ReadTimeoutMs = 5000;

        /// <summary>用例级外部超时保护（毫秒）：超了就直接判失败，绝不整轮挂死。</summary>
        private const int HardTimeoutMs = 90000;

        /// <summary>连续被拒握手的次数（远小于 MaxConcurrentConnections = 64）。</summary>
        private const int RejectCount = 10;

        // ---------------- 设置现场（每个用例前后还原）----------------
        private Settings _cfg;
        private string _oldPassword;
        private string _oldHash;
        private string _oldSalt;
        private WebServerBindMode _oldBindMode;
        private bool _oldEnabled;
        private int _oldPort;

        public WebSocketRejectCloseTests()
        {
            _cfg = Settings.Load();
            _oldPassword = _cfg.WebServerPassword;
            _oldHash = _cfg.WebServerPasswordHash;
            _oldSalt = _cfg.WebServerPasswordSalt;
            _oldBindMode = _cfg.WebServerBindMode;
            _oldEnabled = _cfg.WebServerEnabled;
            _oldPort = _cfg.WebServerPort;
        }

        public void Dispose()
        {
            try
            {
                _cfg = Settings.Load();
                _cfg.WebServerPassword = _oldPassword;
                _cfg.WebServerPasswordHash = _oldHash;
                _cfg.WebServerPasswordSalt = _oldSalt;
                _cfg.WebServerBindMode = _oldBindMode;
                _cfg.WebServerEnabled = _oldEnabled;
                _cfg.WebServerPort = _oldPort;
            }
            catch { /* 还原失败不应掩盖用例本身的失败 */ }
        }

        // ================================================================
        //  用例
        // ================================================================

        /// <summary>
        /// LM-034-1：被拒绝的握手（无效票据）必须**先回 401、再关闭连接**。
        /// </summary>
        /// <remarks>
        /// ★ 断言的是"**读到 EOF**"，不是"读超时"：
        ///   服务端真的发了 FIN，客户端的阻塞读才会返回 0；
        ///   如果只是等到了读超时（没关闭），用例必须失败。
        /// ★ 同时断言 401 **确实送达** —— 不是"静默断开"（静默断开虽然也关连接，
        ///   但客户端拿不到状态码，契约上仍然是错的）。
        /// </remarks>
        [Fact]
        public async Task 被拒绝的WebSocket握手先回401再关闭连接()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    var client = new TcpClient();
                    using (client)
                    {
                        await client.ConnectAsync(IPAddress.Loopback, port);
                        NetworkStream stream = client.GetStream();

                        await WriteAsync(stream, BuildWsRequest(port, "ticket=this-ticket-does-not-exist"));
                        ReadResult r = await ReadUntilEofAsync(stream, ReadTimeoutMs);

                        Assert.True(r.SawEof,
                            "被拒绝的握手之后服务端没有关闭连接（没有收到 FIN）。" + r.Describe());
                        Assert.True(r.ElapsedMs < ReadTimeoutMs,
                            "关闭耗时达到读超时上限 —— 说明是**靠超时兜底**而不是服务端真的发了 FIN。" + r.Describe());
                        Assert.Contains("401", FirstLine(r.Text));
                    }
                });
            });
        }

        /// <summary>
        /// LM-034-2：握手被拒的**每一种**原因都必须关闭连接。
        /// </summary>
        /// <remarks>
        /// ★ 覆盖 <c>Handshake</c> 内部的全部早退分支：
        ///   403（Host 校验失败 / Origin 校验失败）、401（旧 <c>?pwd=</c> 协议 / 票据无效）、
        ///   400（<c>Sec-WebSocket-Key</c> 缺失或形态非法）。
        /// ★ 旧 <c>?pwd=</c> 那一路带的是**有效**票据 —— 否则"被拒"可能只是因为票据无效，
        ///   覆盖不到"旧协议判定"这条分支。
        /// </remarks>
        [Fact]
        public async Task 握手被拒的各种原因都必须关闭连接()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    // ★ 每个用例都现取一张**新**票据：票据是一次性的，
                    //   上一条用例可能已经把它消费掉了。
                    var cases = new (string Name, Func<Task<string>> MakeRequest, string ExpectedCode)[]
                    {
                        ("Host 校验失败（缺少 Host 头）",
                            async () => BuildWsRequest(port, "ticket=" + await AcquireTicketAsync(port), includeHost: false), "403"),
                        ("Origin 校验失败（跨源白名单外的 Origin）",
                            async () => BuildWsRequest(port, "ticket=" + await AcquireTicketAsync(port),
                                extraHeader: "Origin: http://evil.example\r\n"), "403"),
                        ("旧 ?pwd= 协议（带有效票据）",
                            async () => BuildWsRequest(port, "pwd=legacy-secret&ticket=" + await AcquireTicketAsync(port)), "401"),
                        ("票据无效",
                            () => Task.FromResult(BuildWsRequest(port, "ticket=no-such-ticket")), "401"),
                        ("Sec-WebSocket-Key 形态非法",
                            async () => BuildWsRequest(port, "ticket=" + await AcquireTicketAsync(port),
                                keyOverride: "not-base64-16-bytes"), "400"),
                    };

                    foreach (var c in cases)
                    {
                        string request = await c.MakeRequest();

                        var client = new TcpClient();
                        using (client)
                        {
                            await client.ConnectAsync(IPAddress.Loopback, port);
                            NetworkStream stream = client.GetStream();

                            await WriteAsync(stream, request);
                            ReadResult r = await ReadUntilEofAsync(stream, ReadTimeoutMs);

                            Assert.True(r.SawEof,
                                $"「{c.Name}」被拒后服务端没有关闭连接（没有收到 FIN）。" + r.Describe());
                            Assert.True(FirstLine(r.Text).Contains(c.ExpectedCode),
                                $"「{c.Name}」期望状态码 {c.ExpectedCode}。" + r.Describe());
                        }
                    }
                });
            });
        }

        /// <summary>
        /// LM-034-3：握手**成功**（101）的连接**不得**被关闭。
        /// </summary>
        /// <remarks>
        /// ★ 这是修复的另一半边界：不能为了"关掉被拒的连接"而把正常的长连接也关掉。
        /// ★ 判据是"在观察窗口内读不到 EOF"（读超时说明连接还活着），
        ///   并且连接在系统层面仍然是 ESTABLISHED（确实没被拆）。
        /// </remarks>
        [Fact]
        public async Task 成功的WebSocket握手连接不被关闭()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    var client = new TcpClient();
                    using (client)
                    {
                        await client.ConnectAsync(IPAddress.Loopback, port);
                        NetworkStream stream = client.GetStream();

                        await WriteAsync(stream, BuildWsRequest(port, "ticket=" + ticket));

                        // 读握手响应（读到空行结束；服务端此时不会关闭）
                        string head = await ReadHeadAsync(stream, ReadTimeoutMs);
                        Assert.True(FirstLine(head).Contains("101"),
                            "握手没有成功（期望 101）。" + head);

                        // ★ 连接必须还活着：观察窗口内读不到 EOF
                        var buf = new byte[256];
                        using var cts = new CancellationTokenSource(1500);
                        bool sawEof = false;
                        try
                        {
                            int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                            sawEof = n <= 0;
                        }
                        catch (OperationCanceledException) { /* 读超时 = 连接还活着 */ }
                        catch (IOException) { /* 连接被拆了 */ }

                        Assert.False(sawEof, "握手成功（101）的连接被服务端关闭了 —— 修复过度。");
                        Assert.True(CountEstablishedConnections(port) >= 1,
                            "握手成功（101）后连接应当仍然处于 ESTABLISHED 状态。");
                    }
                });
            });
        }

        /// <summary>
        /// LM-034-4：连续被拒握手之后，**连接额度可被回收**，端点仍然可用。
        /// </summary>
        /// <remarks>
        /// ★ 只测"一次关闭"不足以证明额度真的回收了 —— 这里连做
        ///   <see cref="RejectCount"/> 次被拒握手，并检查两件事：
        ///   <list type="number">
        ///   <item>
        ///   <b>服务端端口上没有残留的 ESTABLISHED 连接</b> ——
        ///   这是"额度被回收"的硬证据。被拒握手后服务端把套接字挂着不放，
        ///   客户端这边把连接**故意留着不关**（模拟攻击者占住不放），
        ///   于是这些连接会一直停在 ESTABLISHED。
        ///   </item>
        ///   <item>
        ///   <b>端点仍然可用</b> —— 被拒握手之后仍能走完登录 → 换票 → 成功握手（101）。
        ///   </item>
        /// </list>
        /// ★ 观察窗口用 GC 抑制（<see cref="GC.TryStartNoGCRegion"/>）：
        ///   否则被泄漏的服务端套接字一旦被 GC 终结、连接被动拆掉，
        ///   残留连接数会假性归零，用例就失去判别力了。
        /// </remarks>
        [Fact]
        public async Task 连续被拒握手后连接额度可被回收()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    var held = new List<TcpClient>();
                    bool noGc = GC.TryStartNoGCRegion(64L * 1024 * 1024);
                    try
                    {
                        for (int i = 0; i < RejectCount; i++)
                        {
                            var client = new TcpClient();
                            held.Add(client);   // ★ 故意不关：模拟"把连接挂住"的客户端
                            await client.ConnectAsync(IPAddress.Loopback, port);
                            NetworkStream stream = client.GetStream();

                            await WriteAsync(stream, BuildWsRequest(port, "ticket=no-such-ticket-" + i));
                            ReadResult r = await ReadUntilEofAsync(stream, ReadTimeoutMs);

                            Assert.True(r.SawEof,
                                $"第 {i + 1} 次被拒握手后服务端没有关闭连接（没有收到 FIN）。" + r.Describe());
                            Assert.Contains("401", FirstLine(r.Text));
                        }

                        // ★ 额度回收的硬证据：服务端端口上不得残留 ESTABLISHED 连接
                        int lingering = await WaitForNoEstablishedConnectionsAsync(port, 3000);
                        Assert.True(lingering == 0,
                            $"连续 {RejectCount} 次被拒握手后，服务端端口上仍残留 {lingering} 条 ESTABLISHED 连接 —— 额度没有被回收。");

                        // ★ 端点仍然可用：被拒握手之后仍能成功握手
                        string ticket = await AcquireTicketAsync(port);
                        Assert.False(string.IsNullOrEmpty(ticket));

                        var ok = new TcpClient();
                        using (ok)
                        {
                            await ok.ConnectAsync(IPAddress.Loopback, port);
                            NetworkStream stream = ok.GetStream();
                            await WriteAsync(stream, BuildWsRequest(port, "ticket=" + ticket));
                            string head = await ReadHeadAsync(stream, ReadTimeoutMs);
                            Assert.True(FirstLine(head).Contains("101"),
                                "握手没有成功（期望 101）。" + head);
                        }
                    }
                    finally
                    {
                        if (noGc)
                        {
                            try { GC.EndNoGCRegion(); } catch { }
                        }
                        foreach (TcpClient c in held)
                        {
                            try { c.Close(); } catch { }
                        }
                    }
                });
            });
        }

        // ================================================================
        //  辅助
        // ================================================================

        /// <summary>
        /// 用例级**外部超时保护**：主体超过上限就判失败，避免整轮测试挂死。
        /// </summary>
        private static async Task RunGuardedAsync(Func<Task> body)
        {
            Task work = Task.Run(body);
            Task finished = await Task.WhenAny(work, Task.Delay(HardTimeoutMs));
            if (!ReferenceEquals(finished, work))
            {
                Assert.Fail($"用例超过外部超时保护（{HardTimeoutMs} ms）仍未结束 —— 疑似服务端连接悬挂。");
            }
            await work;   // 传播用例内部的断言异常
        }

        /// <summary>起一个只监听回环的 LiteWebServer（配置了口令，便于走通成功握手）。</summary>
        private async Task WithServerAsync(Func<int, LiteWebServer, Task> body)
        {
            int port = FindFreePort();

            UsePassword("lm034-reject-close-password");
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
                server.ListenAddress = IPAddress.Loopback;   // ★ 测试期间只监听回环

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

        /// <summary>设置口令（只写哈希 + 盐，内存里不留明文 —— 与迁移后的生产状态一致）。</summary>
        private void UsePassword(string plain)
        {
            _cfg.WebServerPasswordSalt = Settings.CreateWebPasswordSalt();
            _cfg.WebServerPasswordHash = Settings.ComputeWebPasswordHash(plain, _cfg.WebServerPasswordSalt);
            _cfg.WebServerPassword = "";
        }

        /// <summary>登录 + 换一张一次性 WebSocket 票据。</summary>
        private async Task<string> AcquireTicketAsync(int port)
        {
            string body = "{\"password\":\"lm034-reject-close-password\"}";
            string loginResp = await SendRawAsync(port,
                $"POST /api/auth/login HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                "Connection: close\r\n\r\n" + body);
            Assert.Contains("200", FirstLine(loginResp));

            string setCookie = FindHeaderValue(loginResp, "Set-Cookie");
            Assert.False(string.IsNullOrEmpty(setCookie));

            string sessionId = "";
            int eq = setCookie!.IndexOf('=');
            int semi = setCookie.IndexOf(';');
            if (eq > 0 && semi > eq) sessionId = setCookie.Substring(eq + 1, semi - eq - 1);

            string csrf = "";
            try
            {
                using var doc = JsonDocument.Parse(ExtractBody(loginResp));
                if (doc.RootElement.TryGetProperty("csrf", out var el)) csrf = el.GetString() ?? "";
            }
            catch (JsonException) { }

            string ticketResp = await SendRawAsync(port,
                $"POST /api/auth/ticket HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={sessionId}\r\n" +
                $"X-LiteMonitor-CSRF: {csrf}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            Assert.Contains("200", FirstLine(ticketResp));

            using var ticketDoc = JsonDocument.Parse(ExtractBody(ticketResp));
            return ticketDoc.RootElement.TryGetProperty("ticket", out var t) ? (t.GetString() ?? "") : "";
        }

        /// <summary>
        /// 构造 WebSocket 升级请求。
        /// </summary>
        /// <param name="query">查询串（**必须自带参数名**，例如 <c>ticket=xxx</c>）。</param>
        private static string BuildWsRequest(int port, string query,
                                            bool includeHost = true,
                                            string extraHeader = null,
                                            string keyOverride = null)
        {
            string key = keyOverride ?? "dGhlIHNhbXBsZSBub25jZQ==";
            var sb = new StringBuilder();
            sb.Append($"GET /ws?{query} HTTP/1.1\r\n");
            if (includeHost) sb.Append($"Host: 127.0.0.1:{port}\r\n");
            if (!string.IsNullOrEmpty(extraHeader)) sb.Append(extraHeader);
            sb.Append("Upgrade: websocket\r\n");
            sb.Append("Connection: Upgrade\r\n");
            sb.Append($"Sec-WebSocket-Key: {key}\r\n");
            sb.Append("Sec-WebSocket-Version: 13\r\n\r\n");
            return sb.ToString();
        }

        private static async Task WriteAsync(NetworkStream stream, string raw)
        {
            byte[] data = Encoding.UTF8.GetBytes(raw);
            await stream.WriteAsync(data, 0, data.Length);
            await stream.FlushAsync();
        }

        /// <summary>读取结果：区分"读到 EOF"、"读超时"、"连接被重置"。</summary>
        private sealed class ReadResult
        {
            public string Text = "";
            public bool SawEof;
            public bool TimedOut;
            public bool Reset;
            public long ElapsedMs;

            public string Describe()
            {
                string body = Text.Length > 400 ? Text.Substring(0, 400) + "…" : Text;
                return $"（耗时 {ElapsedMs} ms，EOF={SawEof}，超时={TimedOut}，重置={Reset}，" +
                       $"已读到 {Text.Length} 字节，首行=「{FirstLine(Text)}」，原始响应=「{body}」）";
            }
        }

        /// <summary>
        /// 一直读到 EOF（或超时 / 连接被重置）。
        /// </summary>
        /// <remarks>
        /// ★ 只认 <c>SawEof</c>（读返回 0）作为"服务端关闭了连接"的证据：
        ///   读超时说明连接还挂着（没关闭），连接被重置说明是被动拆的（不是优雅关闭）。
        /// </remarks>
        private static async Task<ReadResult> ReadUntilEofAsync(NetworkStream stream, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            var result = new ReadResult();
            var sb = new StringBuilder();
            var buf = new byte[4096];
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                while (true)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                    if (n <= 0) { result.SawEof = true; break; }
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
            }
            catch (OperationCanceledException) { result.TimedOut = true; }
            catch (IOException) { result.Reset = true; }
            catch (ObjectDisposedException) { result.Reset = true; }
            result.Text = sb.ToString();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        /// <summary>读取响应头（读到空行结束）。</summary>
        private static async Task<string> ReadHeadAsync(NetworkStream stream, int timeoutMs)
        {
            var sb = new StringBuilder();
            var buf = new byte[4096];
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                while (sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                    if (n <= 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            return sb.ToString();
        }

        /// <summary>发送原始 HTTP 报文并把响应读回来（服务器读完会关闭连接）。</summary>
        private static async Task<string> SendRawAsync(int port, string rawRequest, int timeoutMs = 10000)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();

            await WriteAsync(stream, rawRequest);

            var sb = new StringBuilder();
            var buf = new byte[4096];
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                while (true)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                    if (n <= 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }

            return sb.ToString();
        }

        /// <summary>系统 TCP 表里该端口上的 ESTABLISHED 连接数。</summary>
        private static int CountEstablishedConnections(int port)
        {
            int count = 0;
            try
            {
                foreach (TcpConnectionInformation conn in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                {
                    if (conn.State != TcpState.Established) continue;
                    if (conn.LocalEndPoint.Port == port || conn.RemoteEndPoint.Port == port) count++;
                }
            }
            catch (Exception) { /* 取不到系统 TCP 表时按 0 处理（由调用方断言决定成败） */ }
            return count;
        }

        /// <summary>等待端口上的 ESTABLISHED 连接数归零（带上限）。</summary>
        private static async Task<int> WaitForNoEstablishedConnectionsAsync(int port, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            int last = CountEstablishedConnections(port);
            while (last > 0 && sw.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(100);
                last = CountEstablishedConnections(port);
            }
            return last;
        }

        private static string FirstLine(string response)
        {
            int idx = response.IndexOf('\n');
            return idx >= 0 ? response.Substring(0, idx).Trim() : response.Trim();
        }

        private static string FindHeaderValue(string response, string headerName)
        {
            foreach (string line in response.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
                {
                    return t.Substring(headerName.Length + 1).Trim();
                }
            }
            return null;
        }

        /// <summary>从响应里截出正文。</summary>
        private static string ExtractBody(string response)
        {
            int idx = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (idx < 0) return "";
            return response.Substring(idx + 4);
        }

        private static int FindFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static void ResetLiteWebServerInstance()
        {
            try
            {
                var prop = typeof(LiteWebServer).GetProperty("Instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                prop?.SetValue(null, null);
            }
            catch { }
        }
    }
}
