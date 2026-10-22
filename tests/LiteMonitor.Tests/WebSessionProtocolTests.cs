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
    /// Web 主会话 / 一次性票据 / 访问边界 / 凭据迁移的行为护栏（契约 S2.0 · W1–W13.5）。
    ///
    /// 盯住的缺陷：
    ///   SEC-001 —— 监听范围默认全接口 + CORS <c>*</c> + 无 Host/Origin 校验；
    ///   SEC-002 —— 凭据持久化于 localStorage 与查询串（口令进 URL）、默认环回缺配置迁移；
    ///   LM-005 / LM-006 —— 主会话协议未定义（无会话 / 无票据 / 无 CSRF / 无注销）；
    ///   LM-023 —— 凭据加密与旧备份明文冲突、DPAPI 作用域未定义。
    ///
    /// 测试纪律：
    ///  - 只连 127.0.0.1 回环，不触外网、不启动 LHM / 驱动；
    ///  - 会改 <c>Settings.Load()</c> 的 Web 相关字段 ⇒ 挂到 "cfgfile" 集合串行执行；
    ///  - TTL 用例走**可注入 TimeProvider**（假时钟），不靠长 Thread.Sleep；
    ///  - 监听地址类断言核对**实际监听套接字**（系统监听表 + 服务自身端点），不只读配置。
    /// </summary>
    [Collection("cfgfile")]
    public class WebSessionProtocolTests : IDisposable
    {
        // ---------------- 设置现场（每个用例前后还原）----------------
        private Settings _cfg;
        private string _oldPassword;
        private string _oldHash;
        private string _oldSalt;
        private WebServerBindMode _oldBindMode;
        private string _oldLanAddresses;
        private string _oldAllowedOrigins;
        private bool _oldTrustProxy;
        private bool _oldEnabled;
        private int _oldPort;

        // settings.json / settings.json.bak 的沙箱（迁移类用例要真的动文件）
        private static readonly string MainPath = SettingsHelper.FilePath;
        private static readonly string BackupPath = Path.Combine(
            Path.GetDirectoryName(SettingsHelper.FilePath) ?? ".", "settings.json.bak");
        private readonly string _sandbox;
        private bool _settingsFileTouched;

        public WebSessionProtocolTests()
        {
            _cfg = Settings.Load();
            _oldPassword = _cfg.WebServerPassword;
            _oldHash = _cfg.WebServerPasswordHash;
            _oldSalt = _cfg.WebServerPasswordSalt;
            _oldBindMode = _cfg.WebServerBindMode;
            _oldLanAddresses = _cfg.WebServerLanAddresses;
            _oldAllowedOrigins = _cfg.WebServerAllowedOrigins;
            _oldTrustProxy = _cfg.WebServerTrustProxyHeaders;
            _oldEnabled = _cfg.WebServerEnabled;
            _oldPort = _cfg.WebServerPort;

            _sandbox = Path.Combine(Path.GetTempPath(), "lmsettings-webproto-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sandbox);
        }

        public void Dispose()
        {
            try
            {
                if (_settingsFileTouched)
                {
                    RestoreSettingsFiles();
                    Settings.Load(forceReload: true);
                }

                _cfg = Settings.Load();
                _cfg.WebServerPassword = _oldPassword;
                _cfg.WebServerPasswordHash = _oldHash;
                _cfg.WebServerPasswordSalt = _oldSalt;
                _cfg.WebServerBindMode = _oldBindMode;
                _cfg.WebServerLanAddresses = _oldLanAddresses;
                _cfg.WebServerAllowedOrigins = _oldAllowedOrigins;
                _cfg.WebServerTrustProxyHeaders = _oldTrustProxy;
                _cfg.WebServerEnabled = _oldEnabled;
                _cfg.WebServerPort = _oldPort;
            }
            catch { /* 还原失败不应掩盖用例本身的失败 */ }
            finally
            {
                try { Directory.Delete(_sandbox, true); } catch { }
            }
        }

        // ================================================================
        //  N · 访问边界（SEC-001）
        // ================================================================

        /// <summary>
        /// N1：本机模式下服务**不**监听非环回地址。
        /// </summary>
        /// <remarks>
        /// ★ 核对的是**实际监听套接字**：既看服务自己记录的端点，也看系统监听表
        ///   （<see cref="IPGlobalProperties"/> 与 netstat 同源，但不受系统语言影响）。
        /// ★ 这里刻意**不**使用测试接缝 <c>ListenAddress</c>：要验的就是生产默认行为。
        /// </remarks>
        [Fact]
        public async Task N1_本机模式只监听环回地址()
        {
            int port = FindFreePort();
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            _cfg.WebServerBindMode = WebServerBindMode.Loopback;

            using var server = new LiteWebServer(
                _cfg,
                samplingRequester: _ => Task.FromResult(true),
                demandApplier: _ => true);

            Assert.True(server.Start(out string err), "LiteWebServer 启动失败: " + err);
            try
            {
                // ① 服务自己记录的端点
                IPEndPoint own = server.CurrentListenEndpoint;
                Assert.NotNull(own);
                Assert.True(IPAddress.IsLoopback(own.Address),
                    $"本机模式必须只监听环回地址，实际监听 {own.Address}");

                // ② 系统监听表：同一端口上不得出现非环回监听
                var systemListeners = GetListeningAddresses(port);
                Assert.NotEmpty(systemListeners);
                foreach (IPEndPoint ep in systemListeners)
                {
                    Assert.True(IPAddress.IsLoopback(ep.Address),
                        $"端口 {port} 上出现了非环回监听地址 {ep.Address}（本机模式不得对外暴露）");
                }
            }
            finally
            {
                server.Stop();
                ResetLiteWebServerInstance();
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// N2：局域网模式必须配置非空口令；口令为空 ⇒ 启动失败并给出明确错误。
        /// </summary>
        [Fact]
        public void N2_局域网模式口令为空时拒绝启动并给出明确错误()
        {
            int port = FindFreePort();
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            _cfg.WebServerBindMode = WebServerBindMode.Lan;
            _cfg.WebServerPassword = "";
            _cfg.WebServerPasswordHash = "";
            _cfg.WebServerPasswordSalt = "";

            try
            {
                using (var server = new LiteWebServer(
                    _cfg,
                    samplingRequester: _ => Task.FromResult(true),
                    demandApplier: _ => true))
                {
                    bool ok = server.Start(out string err);
                    Assert.False(ok, "局域网模式 + 空口令 ⇒ 必须拒绝启动");
                    Assert.False(string.IsNullOrWhiteSpace(err), "拒绝启动必须给出明确错误文本");
                    Assert.Contains("口令", err);
                    Assert.False(server.IsRunning);
                    Assert.Null(server.CurrentListenEndpoint);
                }
            }
            finally
            {
                ResetLiteWebServerInstance();
            }

            // 对照：配了口令就能启动
            UsePassword("lan-strong-password");
            try
            {
                using (var server = new LiteWebServer(
                    _cfg,
                    samplingRequester: _ => Task.FromResult(true),
                    demandApplier: _ => true))
                {
                    bool ok = server.Start(out string err);
                    Assert.True(ok, "局域网模式 + 非空口令 ⇒ 应当可以启动: " + err);
                    server.Stop();
                }
            }
            finally
            {
                ResetLiteWebServerInstance();
            }
        }

        /// <summary>N3：Host 头不匹配允许集合 ⇒ 403（防 DNS Rebinding）。</summary>
        [Fact]
        public async Task N3_Host不匹配时返回403()
        {
            await WithServerAsync(async (port, _) =>
            {
                string resp = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: attacker.example.com\r\nConnection: close\r\n\r\n");
                Assert.Contains("403", FirstLine(resp));
            });
        }

        /// <summary>N4：<c>Origin: null</c> ⇒ 拒绝。</summary>
        [Fact]
        public async Task N4_Origin为null时返回403()
        {
            await WithServerAsync(async (port, _) =>
            {
                string resp = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nOrigin: null\r\nConnection: close\r\n\r\n");
                Assert.Contains("403", FirstLine(resp));
            });
        }

        /// <summary>
        /// N5：Origin 缺失 ⇒ 允许；但 Host 校验仍然生效。
        /// </summary>
        /// <remarks>
        /// ★ 用例走 L0（未配置口令）：契约补遗裁定 L0 下 <c>POST /api/auth/login</c>
        ///   以 <c>{"password":""}</c> 必须成功并签发主会话 —— 所以这里不需要口令也能拿到会话。
        /// ★ 快照接口在任何模式下都要求主会话（没有"无会话直通"的第二套口径），
        ///   所以"允许"这一侧必须带会话 Cookie 才能拿到 200。
        /// </remarks>
        [Fact]
        public async Task N5_Origin缺失时允许_但Host仍然生效()
        {
            await WithServerAsync(async (port, _) =>
            {
                UseNoPassword();

                // ① L0 登录 ⇒ 拿到主会话
                string loginResp = await SendRawAsync(port, BuildLoginRequest(port, ""));
                Assert.Contains("200", FirstLine(loginResp));
                string sessionId = ExtractCookieValue(loginResp, "lmsid");
                Assert.False(string.IsNullOrEmpty(sessionId));

                // ② 没有 Origin（脚本 / curl）⇒ 允许
                string ok = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={sessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("200", FirstLine(ok));

                // ③ 没有 Origin，但 Host 不对 ⇒ 仍然拒绝
                string bad = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: evil.example.com\r\nCookie: lmsid={sessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("403", FirstLine(bad));
            });
        }

        /// <summary>
        /// N6：跨源页面读不到监控数据（Origin 不在允许集合 ⇒ 403，且响应体里没有快照）。
        /// </summary>
        [Fact]
        public async Task N6_跨源页面读不到监控数据()
        {
            await WithServerAsync(async (port, _) =>
            {
                string resp = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                    "Origin: http://evil.example.com\r\nConnection: close\r\n\r\n");
                Assert.Contains("403", FirstLine(resp));
                Assert.DoesNotContain("\"sys\"", resp);
            });
        }

        /// <summary>N7：响应中**不再**出现 <c>Access-Control-Allow-Origin: *</c>。</summary>
        [Fact]
        public async Task N7_响应不再返回通配CORS头()
        {
            await WithServerAsync(async (port, _) =>
            {
                string resp = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
                Assert.DoesNotContain("Access-Control-Allow-Origin: *", resp);

                // 默认（同源、无显式跨源配置）⇒ 一个 CORS 头都不返回
                Assert.DoesNotContain("Access-Control-Allow-Origin", resp);
                Assert.DoesNotContain("Access-Control-Allow-Credentials", resp);
            });
        }

        /// <summary>
        /// N8：Stop / Start 之后旧会话失效、新会话可用。
        /// </summary>
        [Fact]
        public async Task N8_重启后旧会话失效新会话可用()
        {
            await WithServerAsync(async (port, server) =>
            {
                LoginResult login = await LoginAsync(port, "restart-password");
                Assert.NotNull(login.SessionId);

                // 旧会话可用
                string before = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("200", FirstLine(before));

                // 重启
                server.Stop();
                Assert.True(server.Start(out string err), "重启失败: " + err);

                // 旧会话失效（会话是内存态）
                string after = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("401", FirstLine(after));

                // 新会话可用
                LoginResult login2 = await LoginAsync(port, "restart-password");
                Assert.NotNull(login2.SessionId);
                string fresh = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={login2.SessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("200", FirstLine(fresh));
            });
        }

        // ================================================================
        //  A · 会话与票据（LM-005 / LM-006）
        // ================================================================

        /// <summary>A1：正确口令可换取主会话；响应含带 HttpOnly 与 SameSite=Strict 的 Set-Cookie。</summary>
        [Fact]
        public async Task A1_正确口令换取主会话且Cookie属性正确()
        {
            await WithServerAsync(async (port, _) =>
            {
                LoginResult login = await LoginAsync(port, "correct-password");
                Assert.NotNull(login);
                Assert.False(string.IsNullOrEmpty(login!.SessionId));
                Assert.Contains("Set-Cookie: lmsid=" + login.SessionId, login.RawResponse);
                Assert.Contains("HttpOnly", login.RawResponse);
                Assert.Contains("SameSite=Strict", login.RawResponse);
                Assert.Contains("Path=/", login.RawResponse);
                Assert.Contains("Max-Age=1800", login.RawResponse);

                // CSRF 令牌由登录响应下发，且与"键 = 会话 ID"的派生一致
                Assert.False(string.IsNullOrEmpty(login.Csrf));
                Assert.Equal(WebSessionManager.ComputeCsrfToken(login.SessionId), login.Csrf);
            });
        }

        /// <summary>A2：错误口令 ⇒ 401，且**不**签发任何 Cookie。</summary>
        [Fact]
        public async Task A2_错误口令返回401且不签发Cookie()
        {
            await WithServerAsync(async (port, _) =>
            {
                UsePassword("real-password");
                string resp = await SendRawAsync(port, BuildLoginRequest(port, "wrong-password"));
                Assert.Contains("401", FirstLine(resp));
                Assert.DoesNotContain("Set-Cookie", resp);
            });
        }

        /// <summary>A3：票据 TTL（30 秒）过期后使用 ⇒ 被拒。</summary>
        [Fact]
        public async Task A3_票据过期后被拒()
        {
            var clock = new FakeTimeProvider();
            using var host = new LoopbackHost(NewManager(clock));

            string password = "ticket-ttl-password";
            UsePassword(password);

            LoginResult login = await LoginAsync(host.Port, password);
            Assert.NotNull(login);

            string ticket = await RequestTicketAsync(host.Port, login!.SessionId, login.Csrf);
            Assert.False(string.IsNullOrEmpty(ticket));

            // 推进 31 秒 ⇒ 超过 30 秒 TTL
            clock.Advance(TimeSpan.FromSeconds(31));

            string resp = await SendRawAsync(host.Port, BuildWsRequest(host.Port, ticket));
            Assert.DoesNotContain("101", resp);
            Assert.Contains("401", resp);
        }

        /// <summary>A4：票据用过即废（同一票据第二次使用 ⇒ 被拒）。</summary>
        [Fact]
        public async Task A4_票据用过即废()
        {
            await WithServerAsync(async (port, _) =>
            {
                LoginResult login = await LoginAsync(port, "one-time-password");
                string ticket = await RequestTicketAsync(port, login!.SessionId, login.Csrf);

                // 第一次：握手成功
                string first = await SendRawAsync(port, BuildWsRequest(port, ticket));
                Assert.Contains("101", first);

                // 第二次：同一个票据必须被拒
                string second = await SendRawAsync(port, BuildWsRequest(port, ticket));
                Assert.DoesNotContain("101", second);
                Assert.Contains("401", second);
            });
        }

        /// <summary>A5：票据只能用于 WS 握手；用于 HTTP API ⇒ 被拒。</summary>
        [Fact]
        public async Task A5_票据不能用于HTTP接口()
        {
            await WithServerAsync(async (port, _) =>
            {
                LoginResult login = await LoginAsync(port, "ticket-scope-password");
                string ticket = await RequestTicketAsync(port, login!.SessionId, login.Csrf);

                // ① 把票据当作会话 Cookie 传给快照接口 ⇒ 必须被拒
                string resp = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={ticket}\r\nConnection: close\r\n\r\n");
                Assert.Contains("401", FirstLine(resp));

                // ② 把票据放进快照接口的查询串 ⇒ 同样不被接受
                string resp2 = await SendRawAsync(port,
                    $"GET /api/snapshot?ticket={ticket} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
                Assert.Contains("401", FirstLine(resp2));
            });
        }

        /// <summary>A6：改密后，既有主会话与票据全部失效。</summary>
        [Fact]
        public async Task A6_改密后既有会话与票据全部失效()
        {
            await WithServerAsync(async (port, _) =>
            {
                LoginResult login = await LoginAsync(port, "old-password");
                string ticket = await RequestTicketAsync(port, login!.SessionId, login.Csrf);
                Assert.False(string.IsNullOrEmpty(ticket));

                // 改密（换哈希 + 盐）
                UsePassword("new-password");

                // ① 既有主会话失效
                string resp = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={login!.SessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("401", FirstLine(resp));

                // ② 既有票据失效
                string ws = await SendRawAsync(port, BuildWsRequest(port, ticket));
                Assert.DoesNotContain("101", ws);
                Assert.Contains("401", ws);
            });
        }

        /// <summary>A7：显式注销后，既有 Cookie 立即失效。</summary>
        [Fact]
        public async Task A7_显式注销后Cookie立即失效()
        {
            await WithServerAsync(async (port, _) =>
            {
                LoginResult login = await LoginAsync(port, "logout-password");

                string logout = await SendRawAsync(port,
                    $"POST /api/auth/logout HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                    $"Cookie: lmsid={login!.SessionId}\r\nX-LiteMonitor-CSRF: {login.Csrf}\r\n" +
                    "Content-Length: 0\r\nConnection: close\r\n\r\n");
                Assert.Contains("204", FirstLine(logout));
                Assert.Contains("Set-Cookie: lmsid=;", logout);
                Assert.Contains("Max-Age=0", logout);

                // 注销后同一个 Cookie 立刻失效
                string resp = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("401", FirstLine(resp));
            });
        }

        /// <summary>A8：会话空闲超过空闲 TTL（2 分钟）⇒ 失效。</summary>
        [Fact]
        public async Task A8_会话空闲超过空闲期限后失效()
        {
            var clock = new FakeTimeProvider();
            using var host = new LoopbackHost(NewManager(clock));

            string password = "idle-password";
            UsePassword(password);

            LoginResult login = await LoginAsync(host.Port, password);
            Assert.NotNull(login);

            // 推进 2 分 1 秒（超过空闲 TTL，但没到绝对 TTL）
            clock.Advance(TimeSpan.FromSeconds(121));

            string resp = await SendRawAsync(host.Port,
                $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{host.Port}\r\nCookie: lmsid={login!.SessionId}\r\nConnection: close\r\n\r\n");
            Assert.Contains("401", FirstLine(resp));
        }

        /// <summary>
        /// A9：HTTP 与 WS 的鉴权判断一致（同一凭据在两条通道上的结果相同）。
        /// </summary>
        /// <remarks>
        /// ★ 两条通道共用同一套判定口径：同一个（无效）凭据在 HTTP 与 WS 上都必须被拒；
        ///   口令变更后，两条通道上的既有凭据**同时**失效。
        /// </remarks>
        [Fact]
        public async Task A9_HTTP与WS的鉴权判断一致()
        {
            await WithServerAsync(async (port, _) =>
            {
                LoginResult login = await LoginAsync(port, "consistency-password");

                // ① 无效票据：HTTP 与 WS 都拒绝
                string httpResp = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid=not-a-real-session\r\nConnection: close\r\n\r\n");
                Assert.Contains("401", FirstLine(httpResp));

                string wsResp = await SendRawAsync(port, BuildWsRequest(port, "not-a-real-ticket"));
                Assert.Contains("401", wsResp);
                Assert.DoesNotContain("101", wsResp);

                // ② 改密后：两条通道上的既有凭据同时失效
                string ticket = await RequestTicketAsync(port, login!.SessionId, login.Csrf);
                UsePassword("changed-again-password");

                string httpAfter = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("401", FirstLine(httpAfter));

                string wsAfter = await SendRawAsync(port, BuildWsRequest(port, ticket));
                Assert.Contains("401", wsAfter);
            });
        }

        /// <summary>A10：重复的 <c>lmsid</c> Cookie ⇒ 被拒。</summary>
        [Fact]
        public async Task A10_重复的lmsidCookie被拒()
        {
            await WithServerAsync(async (port, _) =>
            {
                LoginResult login = await LoginAsync(port, "dup-cookie-password");

                string resp = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                    $"Cookie: lmsid={login!.SessionId}; lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("401", FirstLine(resp));

                // 反证：单个 Cookie 仍然可用（否则 401 可能来自别的原因）
                string single = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("200", FirstLine(single));
            });
        }

        // ================================================================
        //  F · CSRF 与请求体（W4 / W7）
        // ================================================================

        /// <summary>F1：改变状态的端点缺少 <c>X-LiteMonitor-CSRF</c> ⇒ 被拒。</summary>
        [Fact]
        public async Task F1_缺少CSRF头被拒()
        {
            await WithServerAsync(async (port, _) =>
            {
                LoginResult login = await LoginAsync(port, "csrf-password");

                string ticketResp = await SendRawAsync(port,
                    $"POST /api/auth/ticket HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={login!.SessionId}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                Assert.Contains("403", FirstLine(ticketResp));

                string logoutResp = await SendRawAsync(port,
                    $"POST /api/auth/logout HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={login.SessionId}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                Assert.Contains("403", FirstLine(logoutResp));
            });
        }

        /// <summary>F2：CSRF 头值错误 ⇒ 被拒。</summary>
        [Fact]
        public async Task F2_CSRF头值错误被拒()
        {
            await WithServerAsync(async (port, _) =>
            {
                LoginResult login = await LoginAsync(port, "csrf-wrong-password");

                string resp = await SendRawAsync(port,
                    $"POST /api/auth/ticket HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={login!.SessionId}\r\n" +
                    "X-LiteMonitor-CSRF: totally-wrong-token\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                Assert.Contains("403", FirstLine(resp));

                // 反证：正确令牌可用
                string ok = await SendRawAsync(port,
                    $"POST /api/auth/ticket HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={login.SessionId}\r\n" +
                    $"X-LiteMonitor-CSRF: {login.Csrf}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                Assert.Contains("200", FirstLine(ok));
            });
        }

        /// <summary>
        /// F3：请求体超过 4 KB ⇒ 被拒，且**不**把 body 读完。
        /// </summary>
        /// <remarks>
        /// ★ 验证"没读完"的办法：声明一个很大的 Content-Length，但实际只发很少的字节。
        ///   如果服务端真的去读 body，它会一直等到读取期限（5 秒）才回应；
        ///   正确实现是**立刻**拒绝 ⇒ 响应必须很快返回。
        /// </remarks>
        [Fact]
        public async Task F3_超过4KB的请求体被拒且不读完()
        {
            await WithServerAsync(async (port, _) =>
            {
                UsePassword("body-limit-password");
                string body = "{\"password\":\"" + new string('x', 8000) + "\"}";
                var sw = Stopwatch.StartNew();
                string resp = await SendRawAsync(port,
                    $"POST /api/auth/login HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                    $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                    $"Connection: close\r\n\r\n{body}");
                sw.Stop();

                Assert.Contains("400", FirstLine(resp));
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
                    $"超限请求体必须**立刻**被拒（实际耗时 {sw.ElapsedMilliseconds}ms —— 说明服务端在读 body）");
            });
        }

        /// <summary>F4：重复 JSON 字段 ⇒ 被拒。</summary>
        [Fact]
        public async Task F4_重复JSON字段被拒()
        {
            await WithServerAsync(async (port, _) =>
            {
                UsePassword("dup-field-password");
                string body = "{\"password\":\"a\",\"password\":\"b\"}";
                string resp = await SendRawAsync(port,
                    $"POST /api/auth/login HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                    $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                    $"Connection: close\r\n\r\n{body}");
                Assert.Contains("400", FirstLine(resp));
                Assert.DoesNotContain("Set-Cookie", resp);
            });
        }

        /// <summary>F5：非 <c>application/json</c> 的 Content-Type ⇒ 被拒。</summary>
        [Fact]
        public async Task F5_非JSON的ContentType被拒()
        {
            await WithServerAsync(async (port, _) =>
            {
                UsePassword("content-type-password");
                string body = "{\"password\":\"x\"}";
                string resp = await SendRawAsync(port,
                    $"POST /api/auth/login HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                    $"Content-Type: text/plain\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                    $"Connection: close\r\n\r\n{body}");
                Assert.Contains("400", FirstLine(resp));
                Assert.DoesNotContain("Set-Cookie", resp);
            });
        }

        /// <summary>
        /// F6：未鉴权请求**不会**触发请求体读取。
        /// </summary>
        /// <remarks>
        /// ★ 验证办法与 F3 相同：声明一个很大的 Content-Length 但只发少量字节。
        ///   未鉴权（无有效会话）的请求必须在**鉴权阶段**就返回 401，
        ///   根本不该进入读 body 的流程 ⇒ 响应必须立刻返回。
        /// </remarks>
        [Fact]
        public async Task F6_未鉴权请求不触发请求体读取()
        {
            await WithServerAsync(async (port, _) =>
            {
                string body = "{\"password\":\"" + new string('y', 8000) + "\"}";
                var sw = Stopwatch.StartNew();
                string resp = await SendRawAsync(port,
                    $"POST /api/auth/ticket HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                    $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                    $"Connection: close\r\n\r\n{body}");
                sw.Stop();

                Assert.Contains("401", FirstLine(resp));
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
                    $"未鉴权请求不得触发请求体读取（实际耗时 {sw.ElapsedMilliseconds}ms）");
            });
        }

        // ================================================================
        //  P · 凭据存储与迁移（W9 / W12）
        // ================================================================

        /// <summary>
        /// P1 / P2 / P5：迁移后 settings.json 与 settings.json.bak 里**不再有**明文口令，
        /// 而正确的旧口令**仍然可用**。
        /// </summary>
        [Fact]
        public void P1_P2_P5_迁移后主文件与备份都不再含明文且旧口令仍可用()
        {
            const string legacyPassword = "legacy-plain-password-1";

            BackupSettingsFiles();

            // 造一个"旧版本"的配置：只有明文口令
            string legacyJson =
                "{\"WebServerEnabled\":true,\"WebServerPort\":5000,\"WebServerPassword\":\"" + legacyPassword + "\"}";
            File.WriteAllText(MainPath, legacyJson);
            File.WriteAllText(BackupPath, legacyJson);
            _settingsFileTouched = true;

            // 重新加载：明文必须被读到（否则旧用户升级后直接登不进去）
            Settings loaded = Settings.Load(forceReload: true);
            _cfg = loaded;
            Assert.Equal(legacyPassword, loaded.WebServerPassword);

            // 触发惰性迁移
            Assert.True(loaded.EnsureWebPasswordMigrated(), "惰性迁移必须成功");

            // P1：主文件里只有哈希 + 盐，没有明文
            string main = File.ReadAllText(MainPath);
            Assert.DoesNotContain(legacyPassword, main);
            Assert.Contains("WebServerPasswordHash", main);
            Assert.Contains("WebServerPasswordSalt", main);
            Assert.False(string.IsNullOrEmpty(loaded.WebServerPasswordHash));

            // P2：备份里同样没有明文
            string bak = File.ReadAllText(BackupPath);
            Assert.DoesNotContain(legacyPassword, bak);

            // P5：迁移前后，正确的旧口令仍然可用
            Assert.True(loaded.VerifyWebPassword(legacyPassword));
            Assert.False(loaded.VerifyWebPassword("wrong-password"));
        }

        /// <summary>
        /// P3：解密失败（模拟换机器 / 换用户）⇒ **明确报错**，
        /// 且监听范围**未**因此扩大、原凭据**未**被删除。
        /// </summary>
        [Fact]
        public void P3_凭据解密失败时明确报错且不扩大监听范围不删除原凭据()
        {
            BackupSettingsFiles();

            // ① 造一个受 DPAPI（CurrentUser）保护的插件凭据并落盘
            var inst = new PluginInstanceConfig
            {
                Id = "inst-dpapi-1",
                TemplateId = "tmpl-1",
                Enabled = true,
                InputValues = new Dictionary<string, string> { { "api_key", "super-secret-value" } }
            };
            var cfg = new Settings { PluginInstances = new List<PluginInstanceConfig> { inst } };
            // ★ 显式落盘"仅本机"监听范围：解密失败**不得**把它放宽成 Lan
            cfg.WebServerEnabled = true;
            cfg.WebServerBindMode = WebServerBindMode.Loopback;
            cfg.Save();

            string main = File.ReadAllText(MainPath);
            Assert.DoesNotContain("super-secret-value", main);      // 落盘的是密文
            Assert.Contains(ProtectedSecret.Prefix, main);

            // ② 模拟"换机器 / 换用户"：破坏密文（DPAPI 的完整性校验必然失败）
            string broken = main.Replace("\"api_key\":\"", "\"api_key\":\"");
            int idx = main.IndexOf(ProtectedSecret.Prefix, StringComparison.Ordinal);
            Assert.True(idx > 0, "落盘内容里应当有受保护的凭据");
            string corrupted = main.Substring(0, idx + ProtectedSecret.Prefix.Length) + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                               main.Substring(main.IndexOf('"', idx + ProtectedSecret.Prefix.Length));
            File.WriteAllText(MainPath, corrupted);
            _settingsFileTouched = true;

            WebServerBindMode modeBefore = cfg.WebServerBindMode;
            Assert.Equal(WebServerBindMode.Loopback, modeBefore);

            // ③ 重新加载：必须**明确报错**，实例被停用，但原凭据仍在磁盘上
            Settings loaded = Settings.Load(forceReload: true);
            _cfg = loaded;

            Assert.NotEmpty(loaded.CredentialErrors);
            Assert.Contains(loaded.CredentialErrors, e => e.Contains("凭据"));
            Assert.False(loaded.PluginInstances[0].Enabled);

            // 原凭据未被删除：磁盘上仍然有那串（被破坏的）密文
            string after = File.ReadAllText(MainPath);
            Assert.Contains(ProtectedSecret.Prefix, after);
            Assert.DoesNotContain("super-secret-value", after);

            // 监听范围未因此扩大
            Assert.Equal(modeBefore, loaded.WebServerBindMode);
            Assert.False(loaded.WebServerBindModeNeedsConvergence);

            // 直接调用解密接口：失败必须给出明确原因，且不返回空串冒充成功
            Assert.False(ProtectedSecret.TryUnprotect(ProtectedSecret.Prefix + "AAAA", out string plain, out string err));
            Assert.Equal("", plain);
            Assert.False(string.IsNullOrWhiteSpace(err));
        }

        /// <summary>
        /// P4：存量配置（<c>WebServerEnabled=true</c> 且没有新字段）升级后
        /// 行为符合 W12（保持旧的局域网行为 + 提示收敛），且**可回退**。
        /// </summary>
        [Fact]
        public void P4_存量配置保持旧行为且可回退()
        {
            BackupSettingsFiles();

            // 旧配置：有 Web 服务、没有"监听范围"字段
            File.WriteAllText(MainPath,
                "{\"WebServerEnabled\":true,\"WebServerPort\":5000,\"WebServerPassword\":\"\"}");
            _settingsFileTouched = true;

            Settings loaded = Settings.Load(forceReload: true);
            _cfg = loaded;

            // ★ 不静默变更监听范围：存量配置保持旧行为（等价 Lan）
            Assert.Equal(WebServerBindMode.Lan, loaded.WebServerBindMode);
            // ★ 并由 UI 提示收敛
            Assert.True(loaded.WebServerBindModeNeedsConvergence);

            // ★ 可回退：用户随时可以显式改回仅本机
            loaded.WebServerBindMode = WebServerBindMode.Loopback;
            Assert.Equal(WebServerBindMode.Loopback, loaded.WebServerBindMode);
            loaded.Save();

            Settings reloaded = Settings.Load(forceReload: true);
            _cfg = reloaded;
            Assert.Equal(WebServerBindMode.Loopback, reloaded.WebServerBindMode);
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

        /// <summary>清掉口令（明文 / 哈希 / 盐）⇒ 进入 L0 未配置口令模式。</summary>
        private void UseNoPassword()
        {
            _cfg.WebServerPassword = "";
            _cfg.WebServerPasswordHash = "";
            _cfg.WebServerPasswordSalt = "";
        }

        /// <summary>
        /// 起一个只监听回环的 LiteWebServer。
        /// </summary>
        /// <param name="body">用例主体。</param>
        /// <param name="password">
        /// 要配置的口令；<c>null</c> ⇒ 不配置口令（L0 无凭据模式）。
        /// </param>
        private async Task WithServerAsync(Func<int, LiteWebServer, Task> body, string password = null)
        {
            int port = FindFreePort();

            if (password != null) UsePassword(password);

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

        /// <summary>构造一个使用假时钟的会话管理器（TTL 用例完全确定性）。</summary>
        private static WebSessionManager NewManager(TimeProvider clock)
        {
            return new WebSessionManager(
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("k", "test");
                    writer.WriteEndObject();
                },
                demandState: null,
                timeProvider: clock);
        }

        private sealed class LoginResult
        {
            public string SessionId = "";
            public string Csrf = "";
            public string RawResponse = "";
        }

        /// <summary>
        /// 执行一次登录并解析出会话 ID 与 CSRF 令牌。
        /// </summary>
        /// <remarks>
        /// ★ 这个辅助方法会**先把服务端的口令设成 <paramref name="password"/>** 再登录 ——
        ///   口令的验证材料是哈希 + 盐（生产态就是这样），测试不必每次都手工构造。
        ///   需要"用错误口令登录"的用例请直接发原始报文，不要走这里。
        /// </remarks>
        private async Task<LoginResult> LoginAsync(int port, string password)
        {
            UsePassword(password);

            string resp = await SendRawAsync(port, BuildLoginRequest(port, password));
            Assert.True(FirstLine(resp).Contains("200"), "登录失败，响应: " + resp);

            string setCookie = FindHeaderValue(resp, "Set-Cookie");
            Assert.NotNull(setCookie);

            string sessionId = "";
            int eq = setCookie!.IndexOf('=');
            int semi = setCookie.IndexOf(';');
            if (eq > 0 && semi > eq) sessionId = setCookie.Substring(eq + 1, semi - eq - 1);

            string csrf = "";
            try
            {
                using var doc = JsonDocument.Parse(ExtractBody(resp));
                if (doc.RootElement.TryGetProperty("csrf", out var el)) csrf = el.GetString() ?? "";
            }
            catch (JsonException) { }

            return new LoginResult { SessionId = sessionId, Csrf = csrf, RawResponse = resp };
        }

        /// <summary>申请一次性票据。</summary>
        private static async Task<string> RequestTicketAsync(int port, string sessionId, string csrf)
        {
            string resp = await SendRawAsync(port,
                $"POST /api/auth/ticket HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={sessionId}\r\n" +
                $"X-LiteMonitor-CSRF: {csrf}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            Assert.Contains("200", FirstLine(resp));

            using var doc = JsonDocument.Parse(ExtractBody(resp));
            return doc.RootElement.TryGetProperty("ticket", out var el) ? (el.GetString() ?? "") : "";
        }

        private static string BuildLoginRequest(int port, string password)
        {
            string body = "{\"password\":\"" + JsonEscape(password) + "\"}";
            return $"POST /api/auth/login HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                   $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                   $"Connection: close\r\n\r\n{body}";
        }

        private static string BuildWsRequest(int port, string ticket)
        {
            return $"GET /ws?ticket={ticket} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                   "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                   "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        }

        private static string JsonEscape(string value)
        {
            var sb = new StringBuilder();
            foreach (char c in value)
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

        /// <summary>从 <c>Set-Cookie</c> 里取出某个 Cookie 的值。</summary>
        private static string ExtractCookieValue(string response, string cookieName)
        {
            string setCookie = FindHeaderValue(response, "Set-Cookie");
            if (string.IsNullOrEmpty(setCookie)) return "";

            foreach (string part in setCookie!.Split(';'))
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

        /// <summary>系统监听表里该端口上的监听端点（与 netstat 同源，但不受系统语言影响）。</summary>
        private static List<IPEndPoint> GetListeningAddresses(int port)
        {
            var result = new List<IPEndPoint>();
            try
            {
                foreach (IPEndPoint ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                {
                    if (ep.Port == port) result.Add(ep);
                }
            }
            catch (Exception) { /* 取不到系统监听表时由调用方断言"非空"失败 */ }
            return result;
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

        private void BackupSettingsFiles()
        {
            string dir = Path.GetDirectoryName(MainPath) ?? ".";
            if (File.Exists(MainPath)) File.Copy(MainPath, Path.Combine(_sandbox, "main.json"), true);
            if (File.Exists(BackupPath)) File.Copy(BackupPath, Path.Combine(_sandbox, "backup.json"), true);
        }

        private void RestoreSettingsFiles()
        {
            string dir = Path.GetDirectoryName(MainPath) ?? ".";
            string savedMain = Path.Combine(_sandbox, "main.json");
            string savedBackup = Path.Combine(_sandbox, "backup.json");

            try { if (File.Exists(savedMain)) File.Copy(savedMain, MainPath, true); else if (File.Exists(MainPath)) File.Delete(MainPath); } catch { }
            try { if (File.Exists(savedBackup)) File.Copy(savedBackup, BackupPath, true); else if (File.Exists(BackupPath)) File.Delete(BackupPath); } catch { }
        }

        /// <summary>可注入的单调时钟（TTL 用例不靠 Thread.Sleep）。</summary>
        private sealed class FakeTimeProvider : TimeProvider
        {
            private long _ticks;
            public FakeTimeProvider(long startTicks = 0) { _ticks = startTicks; }
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => Interlocked.Read(ref _ticks);
            public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            public void Advance(TimeSpan delta)
                => Interlocked.Add(ref _ticks, (long)Math.Round(delta.TotalSeconds * TimestampFrequency));
        }

        /// <summary>回环 Web 宿主：接受连接后交给 WebSessionManager 处理。</summary>
        private sealed class LoopbackHost : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly WebSessionManager _manager;

            public int Port { get; }

            public LoopbackHost(WebSessionManager manager)
            {
                _manager = manager;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = Task.Run(AcceptLoop);
            }

            private async Task AcceptLoop()
            {
                try
                {
                    while (true)
                    {
                        TcpClient client = await _listener.AcceptTcpClientAsync();
                        _ = Task.Run(async () =>
                        {
                            try { await _manager.HandleClientAsync(client); }
                            catch { }
                        });
                    }
                }
                catch { /* 监听器已停止 */ }
            }

            public void Dispose()
            {
                try { _listener.Stop(); } catch { }
            }
        }
    }
}
