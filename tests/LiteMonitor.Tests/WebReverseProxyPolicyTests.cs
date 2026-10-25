#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
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
    /// [F02] 反向代理 / 外部 authority 策略的行为护栏（覆盖 #580）。
    ///
    /// 盯住的缺陷：fork 的 <c>WebSessionManager</c> 对 Host / Origin 做白名单 + 端口强校验
    /// ⇒ **任何外部反代必然 403**（上游完全不做 Host 校验，同场景返回 101）。
    /// 同时 <c>Settings.WebServerTrustProxyHeaders</c> 是**死设置**（全仓零消费点）。
    ///
    /// 测试纪律：
    ///  - 只连 127.0.0.1 回环，不触外网、不启动 LHM / 驱动；
    ///  - 反向代理用**本地可控**的裸 TCP 代理（两种模式：保留 Host / 重写 Host）；
    ///  - 会改 <c>Settings</c> 的白名单 / 代理配置 → 挂到 "cfgfile" 集合串行执行；
    ///  - 时间相关的用例靠真实请求往返，不空等。
    /// </summary>
    [Collection("cfgfile")]
    public class WebReverseProxyPolicyTests : IDisposable
    {
        private const string RfcWsKey = "dGhlIHNhbXBsZSBub25jZQ==";
        private const string ExternalHost = "tunnel.example.com";
        private const string ExternalAuthority = "https://tunnel.example.com";

        /// <summary>浏览器形态的 Origin 头（外部源）。</summary>
        private const string BrowserOrigin = "Origin: https://tunnel.example.com\r\n";

        private readonly Settings _cfg;
        private readonly string _oldAllowedHosts;
        private readonly string _oldTrustedProxies;
        private readonly bool _oldTrustProxyHeaders;
        private readonly string _oldPassword;
        private readonly string _oldHash;
        private readonly string _oldSalt;

        public WebReverseProxyPolicyTests()
        {
            _cfg = Settings.Load();
            _oldAllowedHosts = _cfg.WebServerAllowedHosts;
            _oldTrustedProxies = _cfg.WebServerTrustedProxies;
            _oldTrustProxyHeaders = _cfg.WebServerTrustProxyHeaders;
            _oldPassword = _cfg.WebServerPassword;
            _oldHash = _cfg.WebServerPasswordHash;
            _oldSalt = _cfg.WebServerPasswordSalt;
        }

        public void Dispose()
        {
            _cfg.WebServerAllowedHosts = _oldAllowedHosts;
            _cfg.WebServerTrustedProxies = _oldTrustedProxies;
            _cfg.WebServerTrustProxyHeaders = _oldTrustProxyHeaders;
            _cfg.WebServerPassword = _oldPassword;
            _cfg.WebServerPasswordHash = _oldHash;
            _cfg.WebServerPasswordSalt = _oldSalt;
        }

        // ================================================================
        // 纯函数：authority 解析与白名单匹配
        // ================================================================

        /// <summary>
        /// authority 必须**严格**解析：非法端口一律失败（不"猜"一个端口出来）。
        /// </summary>
        /// <remarks>
        /// ★ 旧实现遇到 <c>127.0.0.1:abc</c> 会把端口悄悄丢掉、只按主机名放行
        ///   ⇒ 那是个绕过口。IPv6 不带方括号也必须拒绝（多冒号有歧义）。
        /// </remarks>
        [Fact]
        public void authority解析对非法端口与歧义IPv6一律失败()
        {
            Assert.False(WebServerAccessPolicy.TryParseAuthority("127.0.0.1:abc", out _, out _));
            Assert.False(WebServerAccessPolicy.TryParseAuthority("127.0.0.1:", out _, out _));
            Assert.False(WebServerAccessPolicy.TryParseAuthority("127.0.0.1:0", out _, out _));
            Assert.False(WebServerAccessPolicy.TryParseAuthority("127.0.0.1:70000", out _, out _));
            Assert.False(WebServerAccessPolicy.TryParseAuthority("::1", out _, out _));
            Assert.False(WebServerAccessPolicy.TryParseAuthority("", out _, out _));

            Assert.True(WebServerAccessPolicy.TryParseAuthority("127.0.0.1:5000", out string h1, out int? p1));
            Assert.Equal("127.0.0.1", h1);
            Assert.Equal(5000, p1);

            Assert.True(WebServerAccessPolicy.TryParseAuthority("127.0.0.1", out string h2, out int? p2));
            Assert.Equal("127.0.0.1", h2);
            Assert.Null(p2);

            Assert.True(WebServerAccessPolicy.TryParseAuthority("[2001:db8::1]:443", out string h3, out int? p3));
            Assert.Equal("2001:db8::1", h3);
            Assert.Equal(443, p3);
        }

        /// <summary>
        /// 白名单条目支持 <c>[scheme://]host[:port]</c> 三种写法。
        /// </summary>
        [Fact]
        public void 白名单条目支持协议与端口()
        {
            Assert.True(WebServerAccessPolicy.TryParseAuthorityEntry(ExternalAuthority, out string? s1, out string h1, out int? p1));
            Assert.Equal("https", s1);
            Assert.Equal(ExternalHost, h1);
            Assert.Null(p1);

            Assert.True(WebServerAccessPolicy.TryParseAuthorityEntry("tunnel.example.com:8443", out string? s2, out string h2, out int? p2));
            Assert.Null(s2);
            Assert.Equal(ExternalHost, h2);
            Assert.Equal(8443, p2);

            Assert.True(WebServerAccessPolicy.TryParseAuthorityEntry("https://tunnel.example.com:8443", out string? s3, out string h3, out int? p3));
            Assert.Equal("https", s3);
            Assert.Equal(ExternalHost, h3);
            Assert.Equal(8443, p3);

            // 非法条目：协议不是 http/https、端口非法
            Assert.False(WebServerAccessPolicy.TryParseAuthorityEntry("ftp://tunnel.example.com", out _, out _, out _));
            Assert.False(WebServerAccessPolicy.TryParseAuthorityEntry("tunnel.example.com:abc", out _, out _, out _));
        }

        /// <summary>
        /// 外部 authority 的端口**不得**与内部监听端口比较（外部 443 ≠ 内部 5000）。
        /// </summary>
        [Fact]
        public void 外部authority按白名单条目取端口而不是内部监听端口()
        {
            var entries = WebServerAccessPolicy.ParseExternalAuthorities(ExternalAuthority);
            Assert.Single(entries);

            // 请求只带主机名（没有端口）⇒ 有效端口来自白名单条目（https ⇒ 443），不是内部端口
            Assert.True(WebServerAccessPolicy.MatchExternalAuthority(
                entries, "https", ExternalHost, null, out WebEffectiveAuthority matched));
            Assert.Equal(443, matched.Port);
            Assert.Equal("https", matched.Scheme);
            Assert.True(matched.IsExternal);

            // 请求带了**冲突**的显式端口 ⇒ 拒绝
            Assert.False(WebServerAccessPolicy.MatchExternalAuthority(
                entries, "https", ExternalHost, 5000, out _));

            // 主机名不在白名单 ⇒ 拒绝
            Assert.False(WebServerAccessPolicy.MatchExternalAuthority(
                entries, "https", "evil.example", null, out _));
        }

        /// <summary>可信代理是**精确 IP** 匹配（不接受网段 / 通配）。</summary>
        [Fact]
        public void 可信代理按精确IP匹配()
        {
            var trusted = WebServerAccessPolicy.ParseTrustedProxies("127.0.0.1, ::1");
            Assert.True(WebServerAccessPolicy.IsTrustedProxyAddress(trusted, IPAddress.Loopback));
            Assert.True(WebServerAccessPolicy.IsTrustedProxyAddress(trusted, IPAddress.IPv6Loopback));
            Assert.False(WebServerAccessPolicy.IsTrustedProxyAddress(trusted, IPAddress.Parse("127.0.0.2")));
            Assert.False(WebServerAccessPolicy.IsTrustedProxyAddress(trusted, null));
        }

        // ================================================================
        // 直连回归：不得回退 fork 已有能力
        // ================================================================

        /// <summary>
        /// 环回直连的既有认证链路**行为保持**：登录 → 取票 → WS 101 → 快照 200。
        /// </summary>
        [Fact]
        public async Task 环回直连的既有行为不变()
        {
            SetPassword("loopback-password");
            _cfg.WebServerAllowedHosts = "";
            _cfg.WebServerTrustedProxies = "";
            _cfg.WebServerTrustProxyHeaders = false;

            await WithServerAsync(async (port, _) =>
            {
                string host = "127.0.0.1:" + port;
                var login = await LoginAsync(port, host, "loopback-password");
                string ticket = await RequestTicketAsync(port, host, login.SessionId, login.Csrf);

                string ws = await SendRawAsync(port, WsHandshake(host, ticket));
                Assert.Contains("101", FirstLine(ws));

                string snapshot = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: {host}\r\nCookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("200", FirstLine(snapshot));
            });
        }

        /// <summary>
        /// 没有配置外部白名单时，外部 Host 一律被拒（既有行为保持）。
        /// </summary>
        [Fact]
        public async Task 未配置白名单时外部Host被拒绝()
        {
            SetPassword("no-whitelist-password");
            _cfg.WebServerAllowedHosts = "";
            _cfg.WebServerTrustedProxies = "";
            _cfg.WebServerTrustProxyHeaders = false;

            await WithServerAsync(async (port, _) =>
            {
                string ws = await SendRawAsync(port, WsHandshake(ExternalHost, ""));
                Assert.Contains("403", FirstLine(ws));
            });
        }

        /// <summary>
        /// Host 头里的**非法端口**必须被拒（旧实现会丢掉端口只按主机名放行）。
        /// </summary>
        [Fact]
        public async Task 非法端口的Host被拒绝()
        {
            SetPassword("strict-port-password");
            _cfg.WebServerAllowedHosts = "";

            await WithServerAsync(async (port, _) =>
            {
                string resp = await SendRawAsync(port,
                    "GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:not-a-port\r\nConnection: close\r\n\r\n");
                Assert.Contains("403", FirstLine(resp));
            });
        }

        // ================================================================
        // #580 核心：白名单配置后，外部 authority 的完整链路必须走通
        // ================================================================

        /// <summary>
        /// **#580 核心**：配置外部白名单后，外部 authority 的
        /// 登录 → 取票 → WS 101 → 快照 **完整成功**。
        /// </summary>
        [Fact]
        public async Task 白名单配置后外部Host的完整链路走通()
        {
            SetPassword("external-password");
            _cfg.WebServerAllowedHosts = ExternalAuthority;
            _cfg.WebServerTrustedProxies = "";
            _cfg.WebServerTrustProxyHeaders = false;

            await WithServerAsync(async (port, _) =>
            {
                var login = await LoginAsync(port, ExternalHost, "external-password",
                    extraHeaders: BrowserOrigin);
                string ticket = await RequestTicketAsync(port, ExternalHost, login.SessionId, login.Csrf);

                string ws = await SendRawAsync(port, WsHandshake(ExternalHost, ticket, extraHeaders: BrowserOrigin));
                Assert.Contains("101", FirstLine(ws));

                string snapshot = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: {ExternalHost}\r\n" +
                    $"Origin: {ExternalAuthority}\r\nCookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("200", FirstLine(snapshot));
            });
        }

        /// <summary>
        /// 外部访问**必须**配置口令：不继承"环回无凭据"的信任。
        /// </summary>
        /// <remarks>
        /// ★ 白名单在**服务启动之后**才写进配置（模拟"运行中改了配置"），
        ///   这样才能验证请求层的兜底判定 —— 启动层的拒绝由另一个用例覆盖。
        /// </remarks>
        [Fact]
        public async Task 外部访问未配置口令时被拒绝()
        {
            SetPassword(null);
            _cfg.WebServerAllowedHosts = "";
            _cfg.WebServerTrustedProxies = "";
            _cfg.WebServerTrustProxyHeaders = false;

            await WithServerAsync(async (port, _) =>
            {
                // 运行中把外部地址加进白名单（没有口令）
                _cfg.WebServerAllowedHosts = ExternalAuthority;
                try
                {
                    string resp = await SendRawAsync(port,
                        $"GET /api/snapshot HTTP/1.1\r\nHost: {ExternalHost}\r\nConnection: close\r\n\r\n");
                    Assert.Contains("403", FirstLine(resp));
                }
                finally
                {
                    _cfg.WebServerAllowedHosts = "";
                }
            });
        }

        /// <summary>
        /// 配置了外部白名单却**没有口令** ⇒ 服务拒绝启动（与局域网同口径）。
        /// </summary>
        [Fact]
        public void 配置外部白名单但没有口令时服务拒绝启动()
        {
            SetPassword(null);
            _cfg.WebServerAllowedHosts = ExternalAuthority;

            int port = FindFreePort();
            bool oldEnabled = _cfg.WebServerEnabled;
            int oldPort = _cfg.WebServerPort;
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            try
            {
                using var server = new LiteWebServer(_cfg,
                    samplingRequester: _ => Task.FromResult(true),
                    demandApplier: _ => true);
                UseLoopbackListener(server);

                Assert.False(server.Start(out string err));
                Assert.Contains("口令", err);
            }
            finally
            {
                _cfg.WebServerEnabled = oldEnabled;
                _cfg.WebServerPort = oldPort;
                ResetLiteWebServerInstance();
            }
        }

        // ================================================================
        // 可信代理 + 转发头
        // ================================================================

        /// <summary>
        /// 可信对端 + 信任开关 + 合法单值转发头 ⇒ 采用**外部 authority**。
        /// </summary>
        [Fact]
        public async Task 可信对端加转发头时采用外部authority()
        {
            SetPassword("trusted-proxy-password");
            _cfg.WebServerAllowedHosts = ExternalAuthority;
            _cfg.WebServerTrustedProxies = "127.0.0.1";
            _cfg.WebServerTrustProxyHeaders = true;

            await WithServerAsync(async (port, _) =>
            {
                // Host 头是本机地址，但转发头声明了外部 authority ⇒ 以转发头为准
                string host = "127.0.0.1:" + port;
                var login = await LoginAsync(port, host, "trusted-proxy-password",
                    extraHeaders: $"X-Forwarded-Host: {ExternalHost}\r\nX-Forwarded-Proto: https\r\n");
                string ticket = await RequestTicketAsync(port, host, login.SessionId, login.Csrf,
                    extraHeaders: $"X-Forwarded-Host: {ExternalHost}\r\nX-Forwarded-Proto: https\r\n");

                string ws = await SendRawAsync(port, WsHandshake(host, ticket,
                    extraHeaders: $"X-Forwarded-Host: {ExternalHost}\r\nX-Forwarded-Proto: https\r\n"));
                Assert.Contains("101", FirstLine(ws));
            });
        }

        /// <summary>
        /// **不可信对端**伪造转发头 ⇒ 转发头**不能**让原本不允许的请求通过。
        /// </summary>
        [Fact]
        public async Task 不可信对端伪造转发头不能放行()
        {
            SetPassword("untrusted-peer-password");
            _cfg.WebServerAllowedHosts = "";               // 外部 authority 未授权
            _cfg.WebServerTrustedProxies = "10.11.12.13";  // 实际对端（127.0.0.1）**不在**列表里
            _cfg.WebServerTrustProxyHeaders = true;

            await WithServerAsync(async (port, _) =>
            {
                // Host 是未授权的外部域名，转发头想把它"洗成"本机地址 ⇒ 必须仍然被拒
                string resp = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: {ExternalHost}\r\n" +
                    $"X-Forwarded-Host: 127.0.0.1:{port}\r\nX-Forwarded-Proto: https\r\nConnection: close\r\n\r\n");
                Assert.Contains("403", FirstLine(resp));
            });
        }

        /// <summary>
        /// 信任开关**关闭** ⇒ 转发头不参与策略（哪怕对端在可信列表里）。
        /// </summary>
        [Fact]
        public async Task 信任开关关闭时转发头不参与判定()
        {
            SetPassword("trust-off-password");
            _cfg.WebServerAllowedHosts = "";               // 外部 authority 未授权
            _cfg.WebServerTrustedProxies = "127.0.0.1";
            _cfg.WebServerTrustProxyHeaders = false;       // ★ 开关关闭

            await WithServerAsync(async (port, _) =>
            {
                string resp = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: {ExternalHost}\r\n" +
                    $"X-Forwarded-Host: 127.0.0.1:{port}\r\nX-Forwarded-Proto: https\r\nConnection: close\r\n\r\n");
                Assert.Contains("403", FirstLine(resp));
            });
        }

        /// <summary>
        /// 转发头**多值 / 重复 / 非法** ⇒ 明确 4xx（不猜、不回退成 200 登录页）。
        /// </summary>
        [Fact]
        public async Task 转发头多值或重复时明确拒绝()
        {
            SetPassword("ambiguous-proxy-password");
            _cfg.WebServerAllowedHosts = ExternalAuthority;
            _cfg.WebServerTrustedProxies = "127.0.0.1";
            _cfg.WebServerTrustProxyHeaders = true;

            await WithServerAsync(async (port, _) =>
            {
                string host = "127.0.0.1:" + port;

                // ① 一个头里塞了逗号多值
                string multi = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: {host}\r\n" +
                    $"X-Forwarded-Host: {ExternalHost}, evil.example\r\nConnection: close\r\n\r\n");
                Assert.Contains("400", FirstLine(multi));

                // ② 重复头
                string dup = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: {host}\r\n" +
                    $"X-Forwarded-Host: {ExternalHost}\r\nX-Forwarded-Host: evil.example\r\nConnection: close\r\n\r\n");
                Assert.Contains("400", FirstLine(dup));

                // ③ 协议头非法
                string badProto = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: {host}\r\n" +
                    $"X-Forwarded-Proto: gopher\r\nConnection: close\r\n\r\n");
                Assert.Contains("400", FirstLine(badProto));
            });
        }

        // ================================================================
        // Origin 同源判定
        // ================================================================

        /// <summary>
        /// Origin 按「有效外部 scheme + authority」做同源判定。
        /// </summary>
        [Fact]
        public async Task 外部Origin同源放行跨源拒绝()
        {
            SetPassword("origin-password");
            _cfg.WebServerAllowedHosts = ExternalAuthority;
            _cfg.WebServerTrustedProxies = "";
            _cfg.WebServerTrustProxyHeaders = false;

            await WithServerAsync(async (port, _) =>
            {
                // 同源（协议 + 主机 + 端口全等）⇒ 放行
                string sameOrigin = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: {ExternalHost}\r\n" +
                    $"Origin: {ExternalAuthority}\r\nConnection: close\r\n\r\n");
                Assert.DoesNotContain("403", FirstLine(sameOrigin));

                // 跨源 ⇒ 拒绝
                string cross = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: {ExternalHost}\r\n" +
                    "Origin: https://evil.example\r\nConnection: close\r\n\r\n");
                Assert.Contains("403", FirstLine(cross));
            });
        }

        // ================================================================
        // 策略代次：白名单收紧后旧会话失效
        // ================================================================

        /// <summary>
        /// 白名单收紧后，**旧会话**按策略代次失效（不能继续用）。
        /// </summary>
        [Fact]
        public async Task 白名单收紧后旧会话失效()
        {
            SetPassword("generation-password");
            _cfg.WebServerAllowedHosts = ExternalAuthority + ",extra.example.com";
            _cfg.WebServerTrustedProxies = "";
            _cfg.WebServerTrustProxyHeaders = false;

            await WithServerAsync(async (port, _) =>
            {
                var login = await LoginAsync(port, ExternalHost, "generation-password");

                // 收紧白名单（去掉 extra.example.com）⇒ 策略代次变化
                _cfg.WebServerAllowedHosts = ExternalAuthority;
                try
                {
                    string resp = await SendRawAsync(port,
                        $"GET /api/snapshot HTTP/1.1\r\nHost: {ExternalHost}\r\n" +
                        $"Cookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
                    Assert.Contains("401", FirstLine(resp));
                }
                finally
                {
                    _cfg.WebServerAllowedHosts = ExternalAuthority + ",extra.example.com";
                }
            });
        }

        // ================================================================
        // Cookie 的 Secure 属性
        // ================================================================

        /// <summary>
        /// 外部 **https** 链路下发的会话 Cookie 必须带 <c>Secure</c>。
        /// </summary>
        [Fact]
        public async Task 外部https链路的Cookie带Secure()
        {
            SetPassword("secure-cookie-password");
            _cfg.WebServerAllowedHosts = ExternalAuthority;
            _cfg.WebServerTrustedProxies = "";
            _cfg.WebServerTrustProxyHeaders = false;

            await WithServerAsync(async (port, _) =>
            {
                string body = "{\"password\":\"secure-cookie-password\"}";
                string resp = await SendRawAsync(port,
                    $"POST /api/auth/login HTTP/1.1\r\nHost: {ExternalHost}\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n" + body);

                Assert.Contains("200", FirstLine(resp));
                string setCookie = FindHeader(resp, "Set-Cookie");
                Assert.False(string.IsNullOrEmpty(setCookie), "登录响应必须下发 lmsid Cookie");
                Assert.Contains("Secure", setCookie);
            });
        }

        // ================================================================
        // 本地可控反向代理：两种 Host 模式
        // ================================================================

        /// <summary>
        /// 本地反向代理**保留 Host** ⇒ 完整链路走通（#580 的真实形态之一）。
        /// </summary>
        [Fact]
        public async Task 本地反向代理保留Host时完整链路走通()
        {
            SetPassword("proxy-keep-host-password");
            _cfg.WebServerAllowedHosts = ExternalAuthority;
            _cfg.WebServerTrustedProxies = "";
            _cfg.WebServerTrustProxyHeaders = false;

            await WithServerAsync(async (port, _) =>
            {
                using var proxy = new LocalReverseProxy(port, rewriteHost: false);
                int proxyPort = proxy.Port;

                var login = await LoginAsync(proxyPort, ExternalHost, "proxy-keep-host-password",
                    extraHeaders: BrowserOrigin);
                string ticket = await RequestTicketAsync(proxyPort, ExternalHost, login.SessionId, login.Csrf,
                    extraHeaders: BrowserOrigin);

                string ws = await SendRawAsync(proxyPort, WsHandshake(ExternalHost, ticket, extraHeaders: BrowserOrigin));
                Assert.Contains("101", FirstLine(ws));

                string snapshot = await SendRawAsync(proxyPort,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: {ExternalHost}\r\n" +
                    $"Origin: {ExternalAuthority}\r\nCookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("200", FirstLine(snapshot));
            });
        }

        /// <summary>
        /// 本地反向代理**重写 Host**（并补转发头）⇒ 完整链路走通（可信代理形态）。
        /// </summary>
        [Fact]
        public async Task 本地反向代理重写Host时完整链路走通()
        {
            SetPassword("proxy-rewrite-host-password");
            _cfg.WebServerAllowedHosts = ExternalAuthority;
            _cfg.WebServerTrustedProxies = "127.0.0.1";
            _cfg.WebServerTrustProxyHeaders = true;

            await WithServerAsync(async (port, _) =>
            {
                using var proxy = new LocalReverseProxy(port, rewriteHost: true);
                int proxyPort = proxy.Port;

                // ★ 浏览器形态：Origin 是外部源。代理把 Host 重写成了源站地址，
                //   只有"采信转发头"才认得出这是同源请求 ⇒ 不采信转发头就必然 403。
                var login = await LoginAsync(proxyPort, ExternalHost, "proxy-rewrite-host-password",
                    extraHeaders: BrowserOrigin);
                string ticket = await RequestTicketAsync(proxyPort, ExternalHost, login.SessionId, login.Csrf,
                    extraHeaders: BrowserOrigin);

                string ws = await SendRawAsync(proxyPort, WsHandshake(ExternalHost, ticket, extraHeaders: BrowserOrigin));
                Assert.Contains("101", FirstLine(ws));

                string snapshot = await SendRawAsync(proxyPort,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: {ExternalHost}\r\n" +
                    $"Origin: {ExternalAuthority}\r\nCookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("200", FirstLine(snapshot));
            });
        }

        // ================================================================
        // 应用设置路径（AppActions）：停止 / 重启
        // ================================================================

        /// <summary>
        /// 运行中把口令清掉 ⇒ 配置了外部地址的服务**必须停止**（与局域网同口径）。
        /// </summary>
        [Fact]
        public async Task 运行中清除口令时配置了外部地址的服务必须停止()
        {
            int port = FindFreePort();
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            _cfg.WebServerBindMode = WebServerBindMode.Loopback;
            _cfg.WebServerAllowedHosts = ExternalAuthority;
            _cfg.WebServerTrustedProxies = "";
            _cfg.WebServerTrustProxyHeaders = false;
            SetPassword("external-stop-password");

            using var server = new LiteWebServer(_cfg,
                samplingRequester: _ => Task.FromResult(true),
                demandApplier: _ => true);
            server.ListenAddress = IPAddress.Loopback;   // ★ 测试期间只监听回环

            Assert.True(server.Start(out string err),
                "配置了外部地址 + 已配置口令 ⇒ 应当可以启动: " + err);

            // 运行中清除访问口令（与设置页「清除访问口令」按钮一致）
            SetPassword(null);

            var result = AppActions.ApplyWebServerWithResult(_cfg);

            Assert.True(result.RejectedExternalWithoutPassword,
                "配置了外部地址 + 无有效口令 ⇒ 必须被明确拒绝（不能静默继续对外提供访问）");
            Assert.False(server.IsRunning,
                "配置了外部地址 + 无有效口令 ⇒ 网页服务必须停止（这是安全边界，不是可选行为）");

            // 端口上确实不再有人监听（事实断言，不看标志位）
            await AssertPortNotListeningAsync(port);

            ResetLiteWebServerInstance();
        }

        /// <summary>
        /// 外部白名单变更 ⇒ 应用设置**必须重启**服务 ⇒ 旧会话（与旧 WS）随之撤销。
        /// </summary>
        [Fact]
        public async Task 外部白名单变更时应用设置会重启服务并撤销旧会话()
        {
            SetPassword("external-restart-password");
            _cfg.WebServerAllowedHosts = ExternalAuthority;
            _cfg.WebServerTrustedProxies = "";
            _cfg.WebServerTrustProxyHeaders = false;

            await WithServerAsync(async (port, server) =>
            {
                var login = await LoginAsync(port, ExternalHost, "external-restart-password");

                // 收紧白名单 ⇒ 访问边界指纹变化
                _cfg.WebServerAllowedHosts = ExternalAuthority + ",extra.example.com";
                try
                {
                    var result = AppActions.ApplyWebServerWithResult(_cfg);
                    Assert.True(result.Restarted, "访问边界变更 ⇒ 必须重启服务");
                    Assert.True(server.IsRunning, "重启后服务应当仍在运行");

                    // 重启 ⇒ 旧会话撤销（不能带着旧策略继续用）
                    string resp = await SendRawAsync(port,
                        $"GET /api/snapshot HTTP/1.1\r\nHost: {ExternalHost}\r\n" +
                        $"Cookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
                    Assert.Contains("401", FirstLine(resp));
                }
                finally
                {
                    _cfg.WebServerAllowedHosts = ExternalAuthority;
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

        /// <summary>
        /// 设置访问口令（按**生产形态**：只写哈希 + 盐，内存里不留明文）。
        /// </summary>
        /// <remarks>
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

        /// <summary>登录：返回 (会话 ID, CSRF 令牌)。</summary>
        private async Task<(string SessionId, string Csrf)> LoginAsync(
            int port, string host, string password, string extraHeaders = "")
        {
            string body = "{\"password\":\"" + password + "\"}";
            string resp = await SendRawAsync(port,
                $"POST /api/auth/login HTTP/1.1\r\nHost: {host}\r\n" +
                extraHeaders +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n" + body);

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
        private async Task<string> RequestTicketAsync(
            int port, string host, string sessionId, string csrf, string extraHeaders = "")
        {
            string resp = await SendRawAsync(port,
                $"POST /api/auth/ticket HTTP/1.1\r\nHost: {host}\r\n" +
                extraHeaders +
                $"Cookie: lmsid={sessionId}\r\nX-LiteMonitor-CSRF: {csrf}\r\nConnection: close\r\n\r\n");
            Assert.Contains("200", FirstLine(resp));

            int idx = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(resp.Substring(idx + 4));
            string ticket = doc.RootElement.TryGetProperty("ticket", out var el) ? (el.GetString() ?? "") : "";
            Assert.False(string.IsNullOrEmpty(ticket), "票据端点必须返回 ticket");
            return ticket;
        }

        /// <summary>取 HTTP 响应状态行（第一行）。</summary>
        private static string FirstLine(string httpResponse)
        {
            int idx = httpResponse.IndexOf('\n');
            return idx < 0 ? httpResponse : httpResponse.Substring(0, idx);
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

        /// <summary>拼一段 WebSocket 升级请求（票据是一次性的）。</summary>
        private static string WsHandshake(string host, string ticket, string extraHeaders = "")
        {
            return $"GET /ws{(string.IsNullOrEmpty(ticket) ? "" : "?ticket=" + ticket)} HTTP/1.1\r\n" +
                   $"Host: {host}\r\n" +
                   extraHeaders +
                   "Upgrade: websocket\r\n" +
                   "Connection: Upgrade\r\n" +
                   $"Sec-WebSocket-Key: {RfcWsKey}\r\n" +
                   "Sec-WebSocket-Version: 13\r\n\r\n";
        }

        /// <summary>
        /// 发一段原始请求：读到「响应头结束（空行）」后，若响应带 <c>Content-Length</c> 再把正文读完。
        /// </summary>
        /// <remarks>
        /// ★ 正文必须读完：登录 / 取票的响应体里是 csrf 与 ticket。
        /// ★ 101 升级响应没有 <c>Content-Length</c> ⇒ 读到空行就收手（否则会一直等到超时）。
        /// </remarks>
        private static async Task<string> SendRawAsync(int port, string raw)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();

            byte[] req = Encoding.UTF8.GetBytes(raw);
            await stream.WriteAsync(req, 0, req.Length);
            await stream.FlushAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sb = new StringBuilder();
            var buf = new byte[4096];
            int expectedBody = -1;
            bool headerDone = false;
            try
            {
                while (true)
                {
                    int n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                    if (n == 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));

                    if (!headerDone)
                    {
                        string text = sb.ToString();
                        int idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (idx < 0) continue;

                        headerDone = true;
                        expectedBody = ParseContentLength(text.Substring(0, idx));
                        if (expectedBody <= 0) break;      // 无正文（或 101）⇒ 收手
                    }

                    int headerEnd = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0 && sb.Length - (headerEnd + 4) >= expectedBody) break;
                }
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }

            return sb.ToString();
        }

        /// <summary>从响应头里取 Content-Length（没有 ⇒ -1）。</summary>
        private static int ParseContentLength(string head)
        {
            foreach (string line in head.Split('\n'))
            {
                string t = line.Trim();
                if (!t.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
                string v = t.Substring("Content-Length:".Length).Trim();
                return int.TryParse(v, out int len) ? len : -1;
            }
            return -1;
        }

        private static int FindFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        /// <summary>断言"该端口上已经没人监听"（连不上即通过）。</summary>
        private static async Task AssertPortNotListeningAsync(int port)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
            }
            catch (SocketException)
            {
                return;     // 连不上 ⇒ 确实没人监听 ✓
            }
            catch (Exception)
            {
                return;
            }

            Assert.True(false, $"端口 {port} 上仍然有人监听（服务没有真正停止）");
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

        /// <summary>复位静态 <c>LiteWebServer.Instance</c>，避免把已 Dispose 的实例留给后续用例。</summary>
        private static void ResetLiteWebServerInstance()
        {
            var prop = typeof(LiteWebServer).GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);
            var setter = prop?.GetSetMethod(nonPublic: true);
            setter?.Invoke(null, new object[] { null });
        }

        /// <summary>
        /// 本地可控的反向代理（裸 TCP）：把客户端请求转发到被测服务。
        /// </summary>
        /// <remarks>
        /// ★ 两种模式：
        ///   <list type="bullet">
        ///   <item><b>保留 Host</b>（<c>rewriteHost = false</c>）：原样转发 —— 对应
        ///     "代理把客户端的 Host 头原样带给源站"的形态（Cloudflare Tunnel 默认行为）。</item>
        ///   <item><b>重写 Host</b>（<c>rewriteHost = true</c>）：把 Host 改成源站地址，
        ///     并补上 <c>X-Forwarded-Host</c> / <c>X-Forwarded-Proto</c> —— 对应
        ///     "代理重写 Host，靠转发头告诉源站真实外部地址"的形态。</item>
        ///   </list>
        /// ★ 代理本身从**环回**连接源站 ⇒ 源站看到的对端 IP 是 127.0.0.1
        ///   （这正是"可信代理即使来自环回也要求口令"要覆盖的场景）。
        /// </remarks>
        private sealed class LocalReverseProxy : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly int _targetPort;
            private readonly bool _rewriteHost;
            private readonly string _forwardedHost;
            private readonly string _forwardedProto;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();

            public int Port { get; }

            public LocalReverseProxy(int targetPort, bool rewriteHost,
                string forwardedHost = ExternalHost, string forwardedProto = "https")
            {
                _targetPort = targetPort;
                _rewriteHost = rewriteHost;
                _forwardedHost = forwardedHost;
                _forwardedProto = forwardedProto;

                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

                _ = Task.Run(AcceptLoopAsync);
            }

            private async Task AcceptLoopAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch { return; }
                    _ = Task.Run(() => PumpAsync(client));
                }
            }

            private async Task PumpAsync(TcpClient client)
            {
                using (client)
                {
                    using var upstream = new TcpClient();
                    try { await upstream.ConnectAsync(IPAddress.Loopback, _targetPort, _cts.Token); }
                    catch { return; }

                    using var clientStream = client.GetStream();
                    using var upstreamStream = upstream.GetStream();

                    Task up = PumpStreamAsync(clientStream, upstreamStream, rewrite: _rewriteHost);
                    Task down = PumpStreamAsync(upstreamStream, clientStream, rewrite: false);
                    await Task.WhenAny(up, down);
                }
            }

            private async Task PumpStreamAsync(NetworkStream from, NetworkStream to, bool rewrite)
            {
                var buf = new byte[8192];
                var pending = new MemoryStream();
                bool headerDone = false;

                try
                {
                    while (true)
                    {
                        int n = await from.ReadAsync(buf, 0, buf.Length, _cts.Token);
                        if (n <= 0) break;

                        if (rewrite && !headerDone)
                        {
                            pending.Write(buf, 0, n);
                            string text = Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length);
                            int idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (idx < 0) continue;     // 头还没收全

                            headerDone = true;
                            string head = text.Substring(0, idx);
                            string rest = text.Substring(idx);
                            byte[] outBytes = Encoding.UTF8.GetBytes(RewriteHead(head) + rest);
                            await to.WriteAsync(outBytes, 0, outBytes.Length, _cts.Token);
                            await to.FlushAsync(_cts.Token);
                            continue;
                        }

                        await to.WriteAsync(buf, 0, n, _cts.Token);
                        await to.FlushAsync(_cts.Token);
                    }
                }
                catch { }
            }

            /// <summary>把 Host 头改成源站地址，并补上转发头。</summary>
            private string RewriteHead(string head)
            {
                var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var output = new List<string>();
                foreach (string line in lines)
                {
                    if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    {
                        output.Add("Host: 127.0.0.1:" + _targetPort);
                        output.Add("X-Forwarded-Host: " + _forwardedHost);
                        output.Add("X-Forwarded-Proto: " + _forwardedProto);
                    }
                    else
                    {
                        output.Add(line);
                    }
                }
                return string.Join("\r\n", output);
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
