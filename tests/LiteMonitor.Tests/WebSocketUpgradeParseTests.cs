using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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
    /// P-F01（WEB-OWS）：WebSocket 升级识别必须走**结构化解析**，不能靠整段正则。
    ///
    /// <para>盯住的缺陷：修复前 <c>WebSessionManager</c> 用两句整段正则判定升级 ——
    /// <c>Regex.IsMatch(req, "GET", IgnoreCase)</c> 与
    /// <c>Regex.IsMatch(req, "Upgrade: websocket", IgnoreCase)</c>。后果：</para>
    /// <list type="bullet">
    /// <item><b>合法请求被降级</b>：RFC 7230 §3.2.4 允许冒号后 0..N 个 SP/HTAB，
    /// 而正则要求<b>恰好一个空格</b> ⇒ <c>Upgrade:websocket</c> / <c>Upgrade:  websocket</c> /
    /// <c>Upgrade:\twebsocket</c> 全部落进 HTTP 路由，客户端收到 <b>200 + 登录页 HTML</b>
    /// （浏览器侧表现就是"网页端连不上 / 灰屏"）。</item>
    /// <item><b>不该进的握手进了</b>：正则扫的是整段报文 ⇒ 伪造的 <c>X-Upgrade: websocket</c>
    /// 会命中；<c>Connection</c> 令牌、协议令牌、请求方法都没被校验。</item>
    /// </list>
    ///
    /// <para>测试纪律：</para>
    /// <list type="bullet">
    /// <item>全部走**真实产品路径**：裸 TCP → 真的 <c>LiteWebServer</c> → 真的
    /// <c>WebSessionManager</c>；不复制、不复刻任何产品判定逻辑。</item>
    /// <item>只连 127.0.0.1 回环；会改 <c>Settings.Load()</c> 的 Web 字段 ⇒ 挂 "cfgfile" 集合串行执行。</item>
    /// <item>每个用例都有**外部超时保护**，失败时整轮不会挂死；<b>不使用 Thread.Sleep 真实等待</b>。</item>
    /// </list>
    /// </summary>
    [Collection("cfgfile")]
    public class WebSocketUpgradeParseTests : IDisposable
    {
        /// <summary>RFC 6455 §1.3 的示例 Key（16 字节 Base64）。</summary>
        private const string RfcWsKey = "dGhlIHNhbXBsZSBub25jZQ==";

        /// <summary>RFC 6455 §1.3 的示例 Accept（由上面的 Key 推导）。</summary>
        private const string RfcWsAccept = "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=";

        /// <summary>读响应头（读到空行结束）的上限（毫秒）。</summary>
        private const int ReadTimeoutMs = 5000;

        /// <summary>用例级外部超时保护（毫秒）。</summary>
        private const int HardTimeoutMs = 90000;

        // ---------------- 设置现场（每个用例前后还原）----------------
        private Settings _cfg;
        private string _oldPassword;
        private string _oldHash;
        private string _oldSalt;
        private WebServerBindMode _oldBindMode;
        private bool _oldEnabled;
        private int _oldPort;

        public WebSocketUpgradeParseTests()
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
            ResetLiteWebServerInstance();
        }

        // ================================================================
        //  ① WEB-OWS 主线：Upgrade 头的合法 OWS 形态一律必须进握手
        // ================================================================

        /// <summary>
        /// 升级头的**合法** OWS 形态（RFC 7230 §3.2.4：冒号后 0..N 个 SP/HTAB）
        /// 必须一律返回 101。
        /// </summary>
        /// <remarks>
        /// ★ 修复前这些形态里只有"恰好一个空格"能进握手，其余全部落进 HTTP 路由
        ///   并回 <b>200 + 登录页 HTML</b>（WEB-OWS 的红灯基线：探针 C5 / C6）。
        /// ★ 头名大小写不敏感、协议令牌大小写不敏感（RFC 7230 §3.2 / RFC 6455 §4.1）。
        /// </remarks>
        [Theory]
        [InlineData("Upgrade: websocket")]        // 基准：一个空格
        [InlineData("Upgrade:websocket")]         // 无空格（探针 C5）
        [InlineData("Upgrade:  websocket")]        // 两个空格（探针 C6）
        [InlineData("Upgrade:\twebsocket")]        // 制表符
        [InlineData("Upgrade: \t websocket")]      // 空格 + 制表符 + 空格
        [InlineData("Upgrade: websocket\t")]       // 尾部制表符
        [InlineData("Upgrade: websocket  ")]       // 尾部两个空格
        [InlineData("UPGRADE: websocket")]         // 头名大写
        [InlineData("upgrade:WebSocket")]          // 头名小写 + 协议令牌混合大小写
        public async Task 升级头的合法OWS形态一律进入握手(string upgradeLine)
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string resp = await SendWsAsync(port, BuildWsRequest(port, ticket,
                        upgradeLine,
                        "Connection: Upgrade",
                        "Sec-WebSocket-Key: " + RfcWsKey,
                        "Sec-WebSocket-Version: 13"));

                    Assert.Contains("101", FirstLine(resp));
                    Assert.Contains(RfcWsAccept, resp);
                });
            });
        }

        // ================================================================
        //  ② Connection 列表头：多令牌 / 多个头行都必须能进握手
        // ================================================================

        /// <summary>
        /// <c>Connection</c> 是**列表头**：按逗号拆令牌，命中 <c>upgrade</c> 即可。
        /// </summary>
        /// <remarks>
        /// ★ 修复前这条路径根本没被校验过（正则只看 <c>Upgrade: websocket</c> 字面量），
        ///   现在按 RFC 6455 §4.1 精确判令牌，但仍必须保持"多令牌可用"。
        /// </remarks>
        [Theory]
        [InlineData("Connection: Upgrade")]
        [InlineData("Connection: keep-alive, Upgrade")]
        [InlineData("Connection: Upgrade, keep-alive")]
        [InlineData("Connection: keep-alive,Upgrade")]
        [InlineData("Connection: keep-alive, Upgrade, foo")]
        public async Task Connection多令牌仍能进入握手(string connectionLine)
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string resp = await SendWsAsync(port, BuildWsRequest(port, ticket,
                        "Upgrade: websocket",
                        connectionLine,
                        "Sec-WebSocket-Key: " + RfcWsKey,
                        "Sec-WebSocket-Version: 13"));

                    Assert.Contains("101", FirstLine(resp));
                });
            });
        }

        /// <summary>
        /// <c>Connection</c> 分成**两个头行**给出时，按 RFC 7230 §7 合并后仍要能进握手。
        /// </summary>
        [Fact]
        public async Task Connection拆成两个头行仍能进入握手()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string resp = await SendWsAsync(port, BuildWsRequest(port, ticket,
                        "Upgrade: websocket",
                        "Connection: keep-alive",
                        "Connection: Upgrade",
                        "Sec-WebSocket-Key: " + RfcWsKey,
                        "Sec-WebSocket-Version: 13"));

                    Assert.Contains("101", FirstLine(resp));
                });
            });
        }

        // ================================================================
        //  ③ TCP 拆包：请求分两次到达也必须能进握手
        // ================================================================

        /// <summary>
        /// 请求被**拆成两包**（中间还隔着一次真实往返）时，仍必须返回 101。
        /// </summary>
        /// <remarks>
        /// ★ 结构化解析不许依赖"一次读就拿到完整报文"：真实网络随时会把头切碎。
        ///   拆包点刻意落在**头行中间**（不是行尾），确保解析器不会"碰巧"按行拼。
        /// </remarks>
        [Fact]
        public async Task 请求被拆成两包仍能进入握手()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string raw = BuildWsRequest(port, ticket,
                        "Upgrade: websocket",
                        "Connection: Upgrade",
                        "Sec-WebSocket-Key: " + RfcWsKey,
                        "Sec-WebSocket-Version: 13");

                    int split = raw.Length / 2;
                    string resp = await SendWsAsync(port, raw, splitAt: split);

                    Assert.Contains("101", FirstLine(resp));
                    Assert.Contains(RfcWsAccept, resp);
                });
            });
        }

        // ================================================================
        //  ④ 防回归 #589：Sec-WebSocket-Key 头名大小写不敏感
        // ================================================================

        /// <summary>
        /// #589：<c>Sec-WebSocket-Key</c> 的头名大小写不敏感 —— 四种写法都必须 101。
        /// </summary>
        [Theory]
        [InlineData("Sec-WebSocket-Key")]
        [InlineData("sec-websocket-key")]
        [InlineData("SEC-WEBSOCKET-KEY")]
        [InlineData("Sec-Websocket-Key")]
        public async Task SecWebSocketKey头名大小写不敏感仍能进入握手(string keyHeader)
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string resp = await SendWsAsync(port, BuildWsRequest(port, ticket,
                        "Upgrade: websocket",
                        "Connection: Upgrade",
                        keyHeader + ": " + RfcWsKey,
                        "Sec-WebSocket-Version: 13"));

                    Assert.Contains("101", FirstLine(resp));
                    Assert.Contains(RfcWsAccept, resp);
                });
            });
        }

        // ================================================================
        //  ⑤ Key 的形态 / 重复 / 缺失 ⇒ 400
        // ================================================================

        /// <summary>
        /// Key 形态非法 / 重复 / 缺失 ⇒ 400（不是 101）。
        /// </summary>
        /// <remarks>
        /// ★ 重复的 Key 是**解析歧义**（Accept 基于哪一个算？），一律拒绝，不猜。
        /// </remarks>
        [Theory]
        [InlineData("not-base64!!")]          // 不是 Base64
        [InlineData("AAAA")]                  // 合法 Base64，但只有 3 字节
        [InlineData("")]                      // 头存在但值为空
        public async Task 非法SecWebSocketKey返回400(string keyValue)
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string resp = await SendWsAsync(port, BuildWsRequest(port, ticket,
                        "Upgrade: websocket",
                        "Connection: Upgrade",
                        "Sec-WebSocket-Key: " + keyValue,
                        "Sec-WebSocket-Version: 13"));

                    Assert.Contains("400", FirstLine(resp));
                    Assert.DoesNotContain("101", FirstLine(resp));
                });
            });
        }

        /// <summary>重复的 Key 头 ⇒ 400。</summary>
        [Fact]
        public async Task 重复的SecWebSocketKey返回400()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string resp = await SendWsAsync(port, BuildWsRequest(port, ticket,
                        "Upgrade: websocket",
                        "Connection: Upgrade",
                        "Sec-WebSocket-Key: " + RfcWsKey,
                        "Sec-WebSocket-Key: " + RfcWsKey,
                        "Sec-WebSocket-Version: 13"));

                    Assert.Contains("400", FirstLine(resp));
                });
            });
        }

        /// <summary>缺失 Key 头 ⇒ 400。</summary>
        [Fact]
        public async Task 缺失SecWebSocketKey返回400()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string resp = await SendWsAsync(port, BuildWsRequest(port, ticket,
                        "Upgrade: websocket",
                        "Connection: Upgrade",
                        "Sec-WebSocket-Version: 13"));

                    Assert.Contains("400", FirstLine(resp));
                });
            });
        }

        // ================================================================
        //  ⑥ 票据链：缺失 / 无效 / 重放 ⇒ 401
        // ================================================================

        /// <summary>没有票据 ⇒ 401。</summary>
        [Fact]
        public async Task 无票据的握手返回401()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string resp = await SendWsAsync(port, BuildWsRequest(port, "",
                        "Upgrade: websocket",
                        "Connection: Upgrade",
                        "Sec-WebSocket-Key: " + RfcWsKey,
                        "Sec-WebSocket-Version: 13"));

                    Assert.Contains("401", FirstLine(resp));
                });
            });
        }

        /// <summary>票据不存在 ⇒ 401。</summary>
        [Fact]
        public async Task 无效票据的握手返回401()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string resp = await SendWsAsync(port, BuildWsRequest(port, "no-such-ticket-at-all",
                        "Upgrade: websocket",
                        "Connection: Upgrade",
                        "Sec-WebSocket-Key: " + RfcWsKey,
                        "Sec-WebSocket-Version: 13"));

                    Assert.Contains("401", FirstLine(resp));
                });
            });
        }

        /// <summary>
        /// 票据**用过即废**：同一张票据第二次握手 ⇒ 401。
        /// </summary>
        [Fact]
        public async Task 票据重放返回401()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string first = await SendWsAsync(port, BuildWsRequest(port, ticket,
                        "Upgrade: websocket",
                        "Connection: Upgrade",
                        "Sec-WebSocket-Key: " + RfcWsKey,
                        "Sec-WebSocket-Version: 13"));
                    Assert.Contains("101", FirstLine(first));

                    string second = await SendWsAsync(port, BuildWsRequest(port, ticket,
                        "Upgrade: websocket",
                        "Connection: Upgrade",
                        "Sec-WebSocket-Key: " + RfcWsKey,
                        "Sec-WebSocket-Version: 13"));
                    Assert.Contains("401", FirstLine(second));
                });
            });
        }

        // ================================================================
        //  ⑦ 伪造的升级请求**不得**进握手
        // ================================================================

        /// <summary>
        /// 伪造的 <c>X-Upgrade: websocket</c> **不得**进握手。
        /// </summary>
        /// <remarks>
        /// ★ 修复前整段正则会把 <c>X-Upgrade: websocket</c> 当成 <c>Upgrade: websocket</c>
        ///   （子串命中）⇒ 伪造头能直接换到 101。
        /// ★ 现在必须回**明确的 4xx**（这里缺 Upgrade 头 ⇒ 400），而不是 200 登录页。
        /// </remarks>
        [Fact]
        public async Task 伪造的XUpgrade头不得进握手()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string resp = await SendWsAsync(port, BuildWsRequest(port, ticket,
                        "X-Upgrade: websocket",
                        "Connection: Upgrade",
                        "Sec-WebSocket-Key: " + RfcWsKey,
                        "Sec-WebSocket-Version: 13"));

                    Assert.DoesNotContain("101", FirstLine(resp));
                    Assert.Contains("400", FirstLine(resp));
                });
            });
        }

        /// <summary>
        /// 请求**正文**里写着 "Upgrade: websocket" 时**不得**进握手。
        /// </summary>
        /// <remarks>
        /// ★ 升级判定只认**头块**；正文里的字样一律不认。
        /// </remarks>
        [Fact]
        public async Task 正文含Upgrade文本不得进握手()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);
                    string body = "Upgrade: websocket";

                    string raw = "GET /ws?ticket=" + ticket + " HTTP/1.1\r\n"
                              + "Host: 127.0.0.1:" + port + "\r\n"
                              + "Content-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n"
                              + "Connection: close\r\n"
                              + "\r\n"
                              + body;

                    string resp = await SendWsAsync(port, raw);

                    Assert.DoesNotContain("101", FirstLine(resp));
                    Assert.Contains("200", FirstLine(resp));
                });
            });
        }

        /// <summary>
        /// 非 GET 方法的升级请求**不得**进握手（明确 405）。
        /// </summary>
        /// <remarks>
        /// ★ 修复前"GET"是整段子串匹配：方法不是 GET、但报文里任何地方出现 "GET"
        ///   （例如路径里）都会进握手。现在按**请求行的方法**判定。
        /// </remarks>
        [Fact]
        public async Task 非GET方法的升级请求不得进握手()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string raw = "POST /ws?ticket=" + ticket + " HTTP/1.1\r\n"
                              + "Host: 127.0.0.1:" + port + "\r\n"
                              + "Upgrade: websocket\r\n"
                              + "Connection: Upgrade\r\n"
                              + "Sec-WebSocket-Key: " + RfcWsKey + "\r\n"
                              + "Sec-WebSocket-Version: 13\r\n"
                              + "\r\n";

                    string resp = await SendWsAsync(port, raw);

                    Assert.DoesNotContain("101", FirstLine(resp));
                    Assert.Contains("405", FirstLine(resp));
                });
            });
        }

        /// <summary>
        /// 报告里带 "GET" 字样的**非 GET** 请求同样不得进握手
        /// （修复前的子串匹配正是在这里漏的）。
        /// </summary>
        [Fact]
        public async Task 报文里含GET字样的非GET请求不得进握手()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string raw = "POST /api/GET?ticket=" + ticket + " HTTP/1.1\r\n"
                              + "Host: 127.0.0.1:" + port + "\r\n"
                              + "Upgrade: websocket\r\n"
                              + "Connection: Upgrade\r\n"
                              + "Sec-WebSocket-Key: " + RfcWsKey + "\r\n"
                              + "Sec-WebSocket-Version: 13\r\n"
                              + "\r\n";

                    string resp = await SendWsAsync(port, raw);

                    Assert.DoesNotContain("101", FirstLine(resp));
                    Assert.Contains("405", FirstLine(resp));
                });
            });
        }

        /// <summary>
        /// 超大头（超过 8 KB 读取上限，头块没读完）**不得**进握手。
        /// </summary>
        /// <remarks>
        /// ★ 头块没读完 ⇒ 头内容不可信（后面是什么根本不知道）⇒ 431，不进握手。
        ///   修复前整段正则会在这 8 KB 里找到 <c>Upgrade: websocket</c> 并直接握手。
        /// </remarks>
        [Fact]
        public async Task 超大头不得进握手()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string padding = new string('a', 9000);
                    string raw = "GET /ws?ticket=" + ticket + " HTTP/1.1\r\n"
                              + "Host: 127.0.0.1:" + port + "\r\n"
                              + "Upgrade: websocket\r\n"
                              + "Connection: Upgrade\r\n"
                              + "Sec-WebSocket-Key: " + RfcWsKey + "\r\n"
                              + "Sec-WebSocket-Version: 13\r\n"
                              + "X-Pad: " + padding + "\r\n"
                              + "\r\n";

                    string resp = await SendWsAsync(port, raw);

                    Assert.DoesNotContain("101", FirstLine(resp));
                    Assert.Contains("431", FirstLine(resp));
                });
            });
        }

        // ================================================================
        //  ⑧ 其它畸形升级形态：明确 4xx，不得静默 200 登录页
        // ================================================================

        /// <summary>
        /// 各类畸形的升级请求必须回**明确的 4xx**（不静默 200 登录页）。
        /// </summary>
        /// <remarks>
        /// ★ 逐条对应 RFC：协议令牌必须精确等于 <c>websocket</c>（不得子串命中
        ///   <c>notwebsocket</c>）；字段名与冒号之间不得有空白；折行（obs-fold）不接受；
        ///   <c>Connection</c> 必须带 <c>upgrade</c> 令牌；RFC 6455 要求 HTTP/1.1；
        ///   长度头不得有歧义；版本必须是 13。
        /// </remarks>
        [Theory]
        [InlineData("notWebSocketProtocol")]   // Upgrade: notwebsocket（子串不得命中）
        [InlineData("spaceBeforeColon")]       // "Upgrade : websocket"
        [InlineData("obsFold")]                // 折行（obs-fold）
        [InlineData("noConnection")]           // 缺 Connection
        [InlineData("noUpgradeHeader")]        // 缺 Upgrade 头（只有 Connection: Upgrade）
        [InlineData("http10")]                 // HTTP/1.0
        [InlineData("duplicateContentLength")] // 重复的 Content-Length
        [InlineData("transferEncoding")]       // Transfer-Encoding
        [InlineData("unsupportedVersion")]     // Sec-WebSocket-Version: 8
        [InlineData("duplicateVersion")]       // 重复的 Sec-WebSocket-Version
        public async Task 畸形升级请求返回明确4xx(string variant)
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string resp = await SendWsAsync(port, BuildVariantRequest(port, ticket, variant));

                    Assert.DoesNotContain("101", FirstLine(resp));
                    Assert.Contains("400", FirstLine(resp));
                });
            });
        }

        /// <summary>
        /// 非法 Origin ⇒ 403（错误优先级：Host/Origin 先于协议形态）。
        /// </summary>
        [Fact]
        public async Task 非法Origin的升级请求返回403()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string ticket = await AcquireTicketAsync(port);

                    string resp = await SendWsAsync(port, BuildWsRequest(port, ticket,
                        "Upgrade: websocket",
                        "Connection: Upgrade",
                        "Origin: https://evil.example",
                        "Sec-WebSocket-Key: " + RfcWsKey,
                        "Sec-WebSocket-Version: 13"));

                    Assert.Contains("403", FirstLine(resp));
                });
            });
        }

        // ================================================================
        //  ⑨ 普通网页 GET 的行为必须**不变**
        // ================================================================

        /// <summary>
        /// 普通网页 GET（没有任何升级头）仍然照旧回 <b>200 + 登录页 HTML</b>。
        /// </summary>
        /// <remarks>
        /// ★ 结构化分流不得把普通网页请求误判成升级请求（更不能回 4xx）。
        /// </remarks>
        [Fact]
        public async Task 普通网页GET行为不变()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string raw = "GET / HTTP/1.1\r\n"
                              + "Host: 127.0.0.1:" + port + "\r\n"
                              + "Connection: close\r\n"
                              + "\r\n";

                    string resp = await SendHttpAsync(port, raw);

                    Assert.Contains("200", FirstLine(resp));
                    Assert.Contains("<!DOCTYPE html>", resp);
                });
            });
        }

        /// <summary>
        /// 普通网页 GET 带一个叫 <c>X-Upgrade</c> 的普通头 ⇒ 仍是 200 登录页。
        /// </summary>
        [Fact]
        public async Task 普通网页GET带XUpgrade头仍是200()
        {
            await RunGuardedAsync(async () =>
            {
                await WithServerAsync(async (port, _) =>
                {
                    string raw = "GET / HTTP/1.1\r\n"
                              + "Host: 127.0.0.1:" + port + "\r\n"
                              + "X-Upgrade: websocket\r\n"
                              + "Connection: close\r\n"
                              + "\r\n";

                    string resp = await SendHttpAsync(port, raw);

                    Assert.Contains("200", FirstLine(resp));
                    Assert.DoesNotContain("101", FirstLine(resp));
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

        /// <summary>起一个只监听回环的 LiteWebServer（L0 无口令模式，走登录 → 票据的同一套流程）。</summary>
        private async Task WithServerAsync(Func<int, LiteWebServer, Task> body)
        {
            int port = FindFreePort();

            // L0：没有口令也要走"登录 → 换票 → 握手"的同一套流程（协议口径统一）。
            _cfg.WebServerPassword = "";
            _cfg.WebServerPasswordHash = "";
            _cfg.WebServerPasswordSalt = "";

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

        /// <summary>登录 + 换一张一次性 WebSocket 票据。</summary>
        private async Task<string> AcquireTicketAsync(int port)
        {
            string body = "{\"password\":\"\"}";
            string loginResp = await SendHttpAsync(port,
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

            string ticketResp = await SendHttpAsync(port,
                $"POST /api/auth/ticket HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={sessionId}\r\n" +
                $"X-LiteMonitor-CSRF: {csrf}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            Assert.Contains("200", FirstLine(ticketResp));

            using var ticketDoc = JsonDocument.Parse(ExtractBody(ticketResp));
            return ticketDoc.RootElement.TryGetProperty("ticket", out var t) ? (t.GetString() ?? "") : "";
        }

        /// <summary>拼一段标准形态的 WebSocket 升级请求（头行由调用方逐条给出）。</summary>
        private static string BuildWsRequest(int port, string ticket, params string[] headerLines)
        {
            var sb = new StringBuilder();
            sb.Append("GET /ws");
            if (!string.IsNullOrEmpty(ticket)) sb.Append("?ticket=").Append(ticket);
            sb.Append(" HTTP/1.1\r\n");
            sb.Append("Host: 127.0.0.1:").Append(port).Append("\r\n");
            foreach (string h in headerLines) sb.Append(h).Append("\r\n");
            sb.Append("\r\n");
            return sb.ToString();
        }

        /// <summary>拼一段"畸形升级请求"（用于 <see cref="畸形升级请求返回明确4xx"/>）。</summary>
        private static string BuildVariantRequest(int port, string ticket, string variant)
        {
            string version = variant == "http10" ? "HTTP/1.0" : "HTTP/1.1";

            var sb = new StringBuilder();
            sb.Append("GET /ws?ticket=").Append(ticket).Append(' ').Append(version).Append("\r\n");
            sb.Append("Host: 127.0.0.1:").Append(port).Append("\r\n");

            switch (variant)
            {
                case "notWebSocketProtocol":
                    sb.Append("Upgrade: notwebsocket\r\n");
                    sb.Append("Connection: Upgrade\r\n");
                    break;

                case "spaceBeforeColon":
                    sb.Append("Upgrade : websocket\r\n");
                    sb.Append("Connection: Upgrade\r\n");
                    break;

                case "obsFold":
                    // 折行：续行以制表符开头（RFC 7230 §3.2.4 已废弃的 obs-fold）
                    sb.Append("Upgrade: web\r\n");
                    sb.Append("\tsocket\r\n");
                    sb.Append("Connection: Upgrade\r\n");
                    break;

                case "noConnection":
                    sb.Append("Upgrade: websocket\r\n");
                    break;

                case "noUpgradeHeader":
                    sb.Append("Connection: Upgrade\r\n");
                    break;

                case "http10":
                    sb.Append("Upgrade: websocket\r\n");
                    sb.Append("Connection: Upgrade\r\n");
                    break;

                case "duplicateContentLength":
                    sb.Append("Upgrade: websocket\r\n");
                    sb.Append("Connection: Upgrade\r\n");
                    sb.Append("Content-Length: 0\r\n");
                    sb.Append("Content-Length: 0\r\n");
                    break;

                case "transferEncoding":
                    sb.Append("Upgrade: websocket\r\n");
                    sb.Append("Connection: Upgrade\r\n");
                    sb.Append("Transfer-Encoding: chunked\r\n");
                    break;

                case "unsupportedVersion":
                    sb.Append("Upgrade: websocket\r\n");
                    sb.Append("Connection: Upgrade\r\n");
                    sb.Append("Sec-WebSocket-Version: 8\r\n");
                    break;

                case "duplicateVersion":
                    sb.Append("Upgrade: websocket\r\n");
                    sb.Append("Connection: Upgrade\r\n");
                    sb.Append("Sec-WebSocket-Version: 13\r\n");
                    sb.Append("Sec-WebSocket-Version: 13\r\n");
                    break;

                default:
                    throw new ArgumentException("未知的用例形态: " + variant);
            }

            sb.Append("Sec-WebSocket-Key: ").Append(RfcWsKey).Append("\r\n");
            sb.Append("\r\n");
            return sb.ToString();
        }

        /// <summary>
        /// 发一段 WS 握手报文，**读到响应头结束**（或对端关闭 / 超时）为止。
        /// </summary>
        /// <param name="splitAt">
        /// &gt;0 时把报文**拆成两包**发送：先发前 <paramref name="splitAt"/> 字节，
        /// 再发其余部分（用于验证 TCP 拆包不影响判定）。
        /// </param>
        private static async Task<string> SendWsAsync(int port, string raw, int splitAt = 0)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using NetworkStream stream = client.GetStream();

            byte[] data = Encoding.UTF8.GetBytes(raw);
            if (splitAt > 0 && splitAt < data.Length)
            {
                await stream.WriteAsync(data.AsMemory(0, splitAt));
                await stream.FlushAsync();
                // ★ 不 sleep：靠"第一次写完再写第二段"天然形成两个 TCP 段。
                await stream.WriteAsync(data.AsMemory(splitAt));
            }
            else
            {
                await stream.WriteAsync(data);
            }
            await stream.FlushAsync();

            return await ReadHeadAsync(stream, ReadTimeoutMs);
        }

        /// <summary>发一段 HTTP 报文并**读到对端关闭**为止（HTTP 是短连接）。</summary>
        private static async Task<string> SendHttpAsync(int port, string raw, int timeoutMs = ReadTimeoutMs)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using NetworkStream stream = client.GetStream();

            byte[] data = Encoding.UTF8.GetBytes(raw);
            await stream.WriteAsync(data);
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

        /// <summary>读取响应头（读到空行结束）；对端关闭或超时也返回已读到的内容。</summary>
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
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
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
