using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Actions;
using LiteMonitor.src.WebServer;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// R03：网页服务的**凭据策略**护栏 —— 运行中清除 / 更换访问口令时的确定行为。
    /// </summary>
    /// <remarks>
    /// 盯住的缺陷（R03 · P1 · 网页）：
    ///   局域网模式服务运行中清除访问口令 ⇒ LAN 监听继续存在，
    ///   且后续登录进入"无口令"分支 ⇒ 局域网内可无凭据访问。
    /// <para>★ 根因：<c>AppActions.ApplyWebServer</c> 判定"要不要重启"时**只比较明文口令**，
    ///   而迁移后的生产形态里明文恒为空串 ⇒ 永远判定"无需重启"；
    ///   同时运行中的配置变更（<c>Settings.Load()</c> 单例）立刻被登录路径看到 ⇒ 进入 L0 分支。</para>
    ///
    /// 测试纪律：
    ///  - 只连 127.0.0.1 回环（<c>LiteWebServer.ListenAddress</c> 测试接缝），
    ///    **不**在测试窗口里真的对局域网开放一个服务；
    ///  - 会改 <c>Settings.Load()</c> 的 Web 相关字段 ⇒ 挂到 "cfgfile" 集合串行执行；
    ///  - 用例前后还原口令 / 盐 / 哈希 / 监听范围 / 端口 / 启停开关。
    /// </remarks>
    [Collection("cfgfile")]
    public class WebPasswordPolicyTests : IDisposable
    {
        // ---------------- 设置现场（每个用例前后还原）----------------
        private readonly Settings _cfg;
        private readonly string _oldPassword;
        private readonly string _oldHash;
        private readonly string _oldSalt;
        private readonly WebServerBindMode _oldBindMode;
        private readonly bool _oldEnabled;
        private readonly int _oldPort;

        public WebPasswordPolicyTests()
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
                _cfg.WebServerPassword = _oldPassword;
                _cfg.WebServerPasswordHash = _oldHash;
                _cfg.WebServerPasswordSalt = _oldSalt;
                _cfg.WebServerBindMode = _oldBindMode;
                _cfg.WebServerEnabled = _oldEnabled;
                _cfg.WebServerPort = _oldPort;
            }
            catch { /* 还原失败不应掩盖用例本身的失败 */ }
            finally
            {
                ResetLiteWebServerInstance();
            }
        }

        // ================================================================
        //  ① 哈希配置启动 → 运行中清除口令 → 应用 ⇒ LAN 服务必须停止
        // ================================================================

        /// <summary>
        /// R03 主用例：LAN 模式 + 已配置口令（哈希形态）跑起来之后，运行中把口令清掉并"应用设置"，
        /// 网页服务**必须停止**（确定行为），而不是"无口令继续对外监听"。
        /// </summary>
        /// <remarks>
        /// ★ 这条用例直接复现缺陷现场：
        ///   迁移后的生产形态里明文恒为空串 ⇒ 旧实现拿明文比较 ⇒ 判定"无需重启" ⇒ LAN 监听继续存在；
        ///   而登录路径读的是 <c>Settings.Load()</c> 单例 ⇒ 已经进入 L0（无口令）分支。
        /// ★ 断言"端口上不再有人监听"而不是只看标志位：标志位可以骗人，端口不会。
        /// </remarks>
        [Fact]
        public async Task R03_哈希配置运行中清除口令_LAN监听必须停止()
        {
            int port = FindFreePort();
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            _cfg.WebServerBindMode = WebServerBindMode.Lan;

            // ★ 生产形态：只写哈希 + 盐，内存里不留明文
            UsePassword("r03-lan-password");

            using var server = new LiteWebServer(
                _cfg,
                samplingRequester: _ => Task.FromResult(true),
                demandApplier: _ => true);
            server.ListenAddress = IPAddress.Loopback;   // ★ 测试期间只监听回环

            Assert.True(server.Start(out string err),
                "LAN 模式 + 已配置口令 ⇒ 应当可以启动: " + err);
            Assert.True(server.IsRunning);
            Assert.Equal(WebServerBindMode.Lan, _cfg.WebServerBindMode);

            // ① 运行中清除访问口令（明文 / 哈希 / 盐全清 —— 与设置页「清除访问口令」按钮一致）
            UseNoPassword();

            // ② 应用设置
            var result = AppActions.ApplyWebServerWithResult(_cfg);

            // ③ 必须是**确定**的结果：拒绝 + 停止（不得静默继续监听）
            Assert.True(result.RejectedLanWithoutPassword,
                "局域网模式 + 无有效口令 ⇒ 必须被明确拒绝（结果里要有明确标记，不能静默继续监听）");
            Assert.False(server.IsRunning,
                "局域网模式 + 无有效口令 ⇒ 网页服务必须停止（这是安全边界，不是可选行为）");
            Assert.True(server.CurrentListenEndpoint == null, "停止后不得残留监听端点");
            Assert.Equal(0, server.AuthenticatedConnectionCount);

            // ④ 端口上确实不再有人监听（事实断言，不看标志位）
            await AssertPortNotListeningAsync(port);
        }

        // ================================================================
        //  ② 清除凭据后既有会话 / 票据 / WS 连接必须被撤销
        // ================================================================

        /// <summary>
        /// 凭据被清除 ⇒ 既有主会话、一次性票据与**已建立的 WebSocket 连接**必须全部失效。
        /// </summary>
        /// <remarks>
        /// ★ 用「仅本机 + 无口令」这一组合来观察撤销：仅本机模式允许 L0（无口令），
        ///   所以服务会**重启**而不是停掉 —— 重启就会撤销全部会话 / 票据 / WS 连接。
        /// ★ 三个断言分别盯三条通道：主会话（Cookie）、票据（WS 握手）、已建立的 WS（长连接）。
        /// </remarks>
        [Fact]
        public async Task R03_清除凭据后既有会话票据与WebSocket必须被撤销()
        {
            int port = FindFreePort();
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            _cfg.WebServerBindMode = WebServerBindMode.Loopback;
            UsePassword("r03-revoke-password");

            using var server = new LiteWebServer(
                _cfg,
                samplingRequester: _ => Task.FromResult(true),
                demandApplier: _ => true);
            server.ListenAddress = IPAddress.Loopback;

            Assert.True(server.Start(out string err), "启动失败: " + err);

            // ① 登录 ⇒ 主会话 + CSRF
            var login = await LoginAsync(port, "r03-revoke-password");
            Assert.False(string.IsNullOrEmpty(login.SessionId), "登录必须签发主会话");

            // ② 一次性票据
            string ticket = await RequestTicketAsync(port, login.SessionId, login.Csrf);
            Assert.False(string.IsNullOrEmpty(ticket), "必须签发一次性票据");

            // ③ 已建立的 WebSocket 连接
            using var ws = new WsClient();
            await ws.ConnectAsync(port, ticket);
            Assert.Contains("101", FirstLine(ws.HandshakeResponse));
            Task<int> closed = ws.WaitForCloseAsync();

            // ④ 运行中清除访问口令 ⇒ 应用设置（凭据变了 ⇒ 必须重启）
            UseNoPassword();
            var result = AppActions.ApplyWebServerWithResult(_cfg);

            Assert.True(result.Restarted,
                "凭据被清除 ⇒ 必须重启服务（重启会连带撤销会话 / 票据 / WS 连接）");
            Assert.True(server.IsRunning, "仅本机模式 + 无口令 ⇒ 允许以 L0 方式继续服务");

            // ⑤ 旧主会话失效
            string snapshot = await SendRawAsync(port,
                $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                $"Cookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
            Assert.Contains("401", FirstLine(snapshot));

            // ⑥ 旧票据失效（WS 握手被拒）
            string wsResp = await SendRawAsync(port, BuildWsRequest(port, ticket));
            Assert.Contains("401", FirstLine(wsResp));

            // ⑦ 已建立的 WebSocket 被服务端断开
            Task done = await Task.WhenAny(closed, Task.Delay(5000));
            Assert.Same(closed, done);
        }

        // ================================================================
        //  ③ 冷启动：LAN 无口令仍然拒绝启动（回归保护）
        // ================================================================

        /// <summary>
        /// 回归保护：冷启动时「局域网模式 + 无口令」仍然必须**拒绝启动**并给出明确错误。
        /// </summary>
        /// <remarks>
        /// ★ R03 的修复不能把冷启动这条约束放松掉 —— 运行中的变更也不许绕过它。
        /// </remarks>
        [Fact]
        public void R03_冷启动局域网模式无口令仍然拒绝启动()
        {
            int port = FindFreePort();
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            _cfg.WebServerBindMode = WebServerBindMode.Lan;
            UseNoPassword();

            using var server = new LiteWebServer(
                _cfg,
                samplingRequester: _ => Task.FromResult(true),
                demandApplier: _ => true);
            server.ListenAddress = IPAddress.Loopback;

            bool ok = server.Start(out string err);
            Assert.False(ok, "局域网模式 + 空口令 ⇒ 必须拒绝启动（回归保护）");
            Assert.False(string.IsNullOrWhiteSpace(err), "拒绝启动必须给出明确错误文本");
            Assert.Contains("口令", err);
            Assert.False(server.IsRunning);
            Assert.Null(server.CurrentListenEndpoint);
        }

        // ================================================================
        //  ④ 凭据未变 ⇒ 不得无故重启服务
        // ================================================================

        /// <summary>
        /// 凭据没变时"应用设置"**不得**重启服务（否则每次点应用都断连，网页端体验直接崩）。
        /// </summary>
        /// <remarks>
        /// ★ 反证手法：重启会清空会话表 ⇒ 会话失效。
        ///   所以"旧会话仍然可用"就是"没有重启"的证据（比看标志位更硬）。
        /// </remarks>
        [Fact]
        public async Task R03_凭据未变时应用设置不得重启服务()
        {
            int port = FindFreePort();
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            _cfg.WebServerBindMode = WebServerBindMode.Loopback;
            UsePassword("r03-stable-password");

            using var server = new LiteWebServer(
                _cfg,
                samplingRequester: _ => Task.FromResult(true),
                demandApplier: _ => true);
            server.ListenAddress = IPAddress.Loopback;

            Assert.True(server.Start(out string err), "启动失败: " + err);

            var login = await LoginAsync(port, "r03-stable-password");
            Assert.False(string.IsNullOrEmpty(login.SessionId));

            // ★ 凭据没变 ⇒ 应用设置
            var result = AppActions.ApplyWebServerWithResult(_cfg);

            Assert.False(result.Restarted,
                "凭据未变 ⇒ 不得重启服务（每次点『应用』都断连是不可接受的）");
            Assert.True(server.IsRunning);
            Assert.Equal(port, server.CurrentRunningPort);

            // 反证：会话仍然有效（重启 ⇒ 会话表被清空 ⇒ 会话失效）
            string snapshot = await SendRawAsync(port,
                $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                $"Cookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
            Assert.Contains("200", FirstLine(snapshot));
        }

        // ================================================================
        //  ⑤ 凭据被更换 ⇒ 必须重启并撤销旧会话
        // ================================================================

        /// <summary>
        /// 凭据被**更换**（不是清空）同样必须被识别出来 ⇒ 重启服务 + 撤销旧会话。
        /// </summary>
        /// <remarks>
        /// ★ 哈希形态下明文恒为空串 ⇒ 旧实现（比明文）发现不了这次变更 ⇒ 服务不重启、
        ///   旧会话与旧 WS 连接继续活着。这条用例就是"改回只比明文"这条变异的靶子。
        /// </remarks>
        [Fact]
        public async Task R03_凭据被更换时服务必须重启并撤销旧会话()
        {
            int port = FindFreePort();
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            _cfg.WebServerBindMode = WebServerBindMode.Loopback;
            UsePassword("r03-old-password");

            using var server = new LiteWebServer(
                _cfg,
                samplingRequester: _ => Task.FromResult(true),
                demandApplier: _ => true);
            server.ListenAddress = IPAddress.Loopback;

            Assert.True(server.Start(out string err), "启动失败: " + err);

            var oldLogin = await LoginAsync(port, "r03-old-password");
            Assert.False(string.IsNullOrEmpty(oldLogin.SessionId));

            // 换成新口令（仍然是哈希形态：明文恒为空）
            UsePassword("r03-new-password");

            var result = AppActions.ApplyWebServerWithResult(_cfg);
            Assert.True(result.Restarted,
                "凭据被更换 ⇒ 必须重启服务（哈希形态下明文恒为空，比明文发现不了这次变更）");
            Assert.True(server.IsRunning);

            // 旧会话失效
            string snapshot = await SendRawAsync(port,
                $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                $"Cookie: lmsid={oldLogin.SessionId}\r\nConnection: close\r\n\r\n");
            Assert.Contains("401", FirstLine(snapshot));

            // 新口令可以登录（服务确实换成了新凭据）
            var newLogin = await LoginAsync(port, "r03-new-password");
            Assert.False(string.IsNullOrEmpty(newLogin.SessionId));
            Assert.NotEqual(oldLogin.SessionId, newLogin.SessionId);
        }

        // ================================================================
        //  ⑥ 运行时监听策略：已对局域网暴露时，无口令一律拒绝
        // ================================================================

        /// <summary>
        /// 「对局域网暴露 + 没有有效凭据」必须一律拒绝 —— 哪怕配置写着「仅本机」。
        /// </summary>
        /// <remarks>
        /// ★ 为什么要看**运行时**事实：监听范围变更后服务尚未重绑时，
        ///   配置可能写着「仅本机」而服务其实还绑在 0.0.0.0 上。
        ///   这时把口令清掉就必须停 —— 否则旧的对外监听会一直敞着。
        /// </remarks>
        [Fact]
        public void R03_运行时已对局域网暴露时无口令必须被拒绝()
        {
            _cfg.WebServerBindMode = WebServerBindMode.Loopback;
            UseNoPassword();

            // ① 运行时确实绑在非回环地址上 ⇒ 无口令 ⇒ 必须拒绝
            Assert.True(WebServerAccessPolicy.IsLanAccessForbidden(_cfg, runtimeLanExposed: true),
                "运行时已对局域网暴露时，没有有效口令就必须拒绝监听");

            // ② 仅本机 + 无口令 ⇒ 允许（L0：契约 W1 允许"无凭据"，但只允许本机）
            Assert.False(WebServerAccessPolicy.IsLanAccessForbidden(_cfg, runtimeLanExposed: false),
                "仅本机 + 无口令 ⇒ 允许（L0 模式）");

            // ③ 局域网模式 + 无口令 ⇒ 必须拒绝
            _cfg.WebServerBindMode = WebServerBindMode.Lan;
            Assert.True(WebServerAccessPolicy.IsLanAccessForbidden(_cfg, runtimeLanExposed: false),
                "局域网模式 + 无口令 ⇒ 必须拒绝（与冷启动守卫同一条规则）");

            // ④ 局域网模式 + 有口令 ⇒ 允许
            UsePassword("r03-policy-password");
            Assert.False(WebServerAccessPolicy.IsLanAccessForbidden(_cfg, runtimeLanExposed: true),
                "有有效口令 ⇒ 允许监听");
        }

        // ================================================================
        //  辅助
        // ================================================================

        /// <summary>设置口令（只写哈希 + 盐，内存里不留明文 —— 与迁移后的生产状态一致）。</summary>
        private void UsePassword(string plain)
        {
            _cfg.WebServerPasswordSalt = Settings.CreateWebPasswordSalt();
            _cfg.WebServerPasswordHash = Settings.ComputeWebPasswordHash(plain, _cfg.WebServerPasswordSalt);
            _cfg.WebServerPassword = "";
        }

        /// <summary>清掉口令（明文 / 哈希 / 盐）⇒ 进入"没有有效凭据"状态。</summary>
        private void UseNoPassword()
        {
            _cfg.WebServerPassword = "";
            _cfg.WebServerPasswordHash = "";
            _cfg.WebServerPasswordSalt = "";
        }

        /// <summary>
        /// 登录（**不**改配置里的口令：用例自己决定口令是什么）。
        /// </summary>
        private async Task<(string SessionId, string Csrf)> LoginAsync(int port, string password)
        {
            string body = "{\"password\":\"" + JsonEscape(password) + "\"}";
            string resp = await SendRawAsync(port,
                $"POST /api/auth/login HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                "Connection: close\r\n\r\n" + body);

            Assert.Contains("200", FirstLine(resp));
            Assert.Contains("Set-Cookie: lmsid=", resp);

            string sessionId = ExtractCookieValue(resp, "lmsid");
            Assert.False(string.IsNullOrEmpty(sessionId), "登录响应必须下发 lmsid Cookie");

            string csrf = "";
            try
            {
                using var doc = JsonDocument.Parse(ExtractBody(resp));
                if (doc.RootElement.TryGetProperty("csrf", out var el)) csrf = el.GetString() ?? "";
            }
            catch (JsonException) { }

            Assert.False(string.IsNullOrEmpty(csrf), "登录响应必须下发 csrf 令牌");
            return (sessionId, csrf);
        }

        /// <summary>申请一次性票据（需要会话 Cookie + CSRF 头）。</summary>
        private static async Task<string> RequestTicketAsync(int port, string sessionId, string csrf)
        {
            string resp = await SendRawAsync(port,
                $"POST /api/auth/ticket HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={sessionId}\r\n" +
                $"X-LiteMonitor-CSRF: {csrf}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            Assert.Contains("200", FirstLine(resp));

            using var doc = JsonDocument.Parse(ExtractBody(resp));
            return doc.RootElement.TryGetProperty("ticket", out var el) ? (el.GetString() ?? "") : "";
        }

        private static string BuildWsRequest(int port, string ticket)
        {
            return $"GET /ws?ticket={ticket} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                   "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                   "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        }

        /// <summary>断言该端口上**没有**人在监听（连接必须被拒）。</summary>
        private static async Task AssertPortNotListeningAsync(int port)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
            }
            catch (SocketException)
            {
                return;   // 连接被拒 ⇒ 端口上确实没有监听 ✔
            }
            catch (Exception)
            {
                return;   // 其它连接失败同样说明"连不上"
            }

            Assert.Fail($"端口 {port} 上仍然有人监听：网页服务并没有真正停止");
        }

        /// <summary>
        /// 一条真实的 WebSocket 连接：只做"握手 + 等待服务端关闭"两件事。
        /// </summary>
        private sealed class WsClient : IDisposable
        {
            private readonly TcpClient _client = new TcpClient();
            private NetworkStream? _stream;

            /// <summary>握手响应（HTTP 头部分）。</summary>
            public string HandshakeResponse = "";

            public async Task ConnectAsync(int port, string ticket)
            {
                await _client.ConnectAsync(IPAddress.Loopback, port);
                _stream = _client.GetStream();

                string req =
                    $"GET /ws?ticket={ticket} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                    "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                    "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
                byte[] data = Encoding.UTF8.GetBytes(req);
                await _stream.WriteAsync(data, 0, data.Length);
                await _stream.FlushAsync();

                var sb = new StringBuilder();
                var buf = new byte[1024];
                using var cts = new CancellationTokenSource(10000);
                while (sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
                {
                    int n = await _stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                    if (n <= 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
                HandshakeResponse = sb.ToString();
            }

            /// <summary>
            /// 一直读到服务端关闭连接为止；返回读到的字节数。
            /// </summary>
            /// <remarks>
            /// ★ FIN（读到 0）与 RST（IOException）都算"服务端关闭了这条连接"；
            ///   读超时（对端还活着）则让调用方的 <c>Task.WhenAny</c> 判定为"没关"。
            /// </remarks>
            public async Task<int> WaitForCloseAsync()
            {
                var buf = new byte[4096];
                int total = 0;
                while (true)
                {
                    int n;
                    try
                    {
                        n = await _stream!.ReadAsync(buf.AsMemory(0, buf.Length));
                    }
                    catch (IOException)
                    {
                        return total;   // RST ⇒ 服务端断开了这条连接
                    }
                    catch (ObjectDisposedException)
                    {
                        return total;
                    }

                    if (n <= 0) return total;   // EOF ⇒ 服务端关闭了这条连接
                    total += n;
                }
            }

            public void Dispose()
            {
                try { _client.Close(); } catch { }
            }
        }

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

        /// <summary>发送原始 HTTP 报文并把响应读回来（服务器读完会关闭连接）。</summary>
        private static async Task<string> SendRawAsync(int port, string rawRequest, int timeoutMs = 10000)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();

            byte[] data = Encoding.UTF8.GetBytes(rawRequest);
            await stream.WriteAsync(data, 0, data.Length);
            await stream.FlushAsync();

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

        private static string FirstLine(string response)
        {
            int idx = response.IndexOf('\n');
            return idx < 0 ? response.Trim() : response.Substring(0, idx).Trim();
        }

        private static string FindHeaderValue(string response, string headerName)
        {
            foreach (string line in response.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
                    return t.Substring(headerName.Length + 1).Trim();
            }
            return "";
        }

        /// <summary>从 <c>Set-Cookie</c> 里取出某个 Cookie 的值。</summary>
        private static string ExtractCookieValue(string response, string cookieName)
        {
            string setCookie = FindHeaderValue(response, "Set-Cookie");
            if (string.IsNullOrEmpty(setCookie)) return "";

            foreach (string part in setCookie.Split(';'))
            {
                string p = part.Trim();
                if (!p.StartsWith(cookieName + "=", StringComparison.Ordinal)) continue;
                return p.Substring(cookieName.Length + 1);
            }
            return "";
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

        /// <summary>
        /// 复位静态 <c>LiteWebServer.Instance</c>（<c>private set</c> ⇒ 必须显式取非公开 setter）。
        /// </summary>
        private static void ResetLiteWebServerInstance()
        {
            try
            {
                var prop = typeof(LiteWebServer).GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static);
                prop?.GetSetMethod(nonPublic: true)?.Invoke(null, new object?[] { null });
            }
            catch { }
        }
    }
}
