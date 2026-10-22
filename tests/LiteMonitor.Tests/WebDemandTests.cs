using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// Web 有效需求状态 / WebFpsEnabled 设置项 / FPS 占位输出 的单元测试。
    ///
    /// 约束（与仓库既有测试风格一致）：
    ///  - 不启动 LHM、驱动、真实 PresentMon；
    ///  - 不发起外网请求（只用 127.0.0.1 回环）；
    ///  - 租期判断用注入的假时钟（单调），不靠真实等待；
    ///  - 不新增 Mock 框架，私有替身都定义在本文件内。
    ///
    /// 本类会改 <c>Settings.Load()</c> 里的访问密码，因此挂到 "cfgfile" 集合上串行执行。
    /// </summary>
    [Collection("cfgfile")]
    public class WebDemandTests : IDisposable
    {
        private static readonly string[] AllLangs = { "zh", "zh-tw", "en", "ja", "ko", "ru", "de", "es", "fr" };

        private readonly Settings _cfg;
        private readonly string _oldPassword;
        private readonly string _oldHash;
        private readonly string _oldSalt;

        public WebDemandTests()
        {
            _cfg = Settings.Load();
            _oldPassword = _cfg.WebServerPassword;
            _oldHash = _cfg.WebServerPasswordHash;
            _oldSalt = _cfg.WebServerPasswordSalt;

            // ★ 默认无口令（L0）；**哈希与盐也要清掉** ——
            //   验证材料是"哈希 + 盐"，只清明文字段会留下上一条用例的哈希，
            //   于是服务端会以为配置了口令（L1），与用例的前提不符。
            _cfg.WebServerPassword = "";
            _cfg.WebServerPasswordHash = "";
            _cfg.WebServerPasswordSalt = "";
            _sessions.Clear();
        }

        public void Dispose()
        {
            _cfg.WebServerPassword = _oldPassword;
            _cfg.WebServerPasswordHash = _oldHash;
            _cfg.WebServerPasswordSalt = _oldSalt;
            _sessions.Clear();
        }

        // ================================================================
        // 替身 / 私有 helper
        // ================================================================

        /// <summary>假单调时钟（100ns 刻度，可手动推进）</summary>
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

        /// <summary>回环 Web 宿主：接受连接后交给 WebSessionManager 处理</summary>
        private sealed class LoopbackHost : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly WebSessionManager _manager;

            public int Port { get; }

            /// <param name="socketBufferSize">
            /// &gt;0 时把监听套接字的收发缓冲都调小：客户端不读数据时，
            /// 服务器的写操作会真的被卡住（用来测"写失败"这条回收路径）。
            /// </param>
            public LoopbackHost(WebSessionManager manager, int socketBufferSize = 0)
            {
                _manager = manager;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                if (socketBufferSize > 0)
                {
                    _listener.Server.SendBufferSize = socketBufferSize;
                    _listener.Server.ReceiveBufferSize = socketBufferSize;
                }
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
                        var client = await _listener.AcceptTcpClientAsync();
                        _ = _manager.HandleClientAsync(client);
                    }
                }
                catch { /* 监听器已停止 */ }
            }

            public void Dispose()
            {
                try { _listener.Stop(); } catch { }
            }
        }

        /// <summary>发一个普通 HTTP 请求并把响应读回来（服务器读完会关闭连接）</summary>
        /// <param name="sendDelayMs">
        /// 请求的发送延迟：服务器此时已经进入 HandleClientAsync 并阻塞在读请求头上，
        /// 用来制造"请求跨越 Stop 边界"的时间窗。
        /// </param>
        /// <param name="authenticate">
        /// 是否自动带上**主会话 Cookie**（新协议下 HTTP API 一律要求会话）。
        /// 验证"未鉴权请求"的行为时传 false。
        /// </param>
        private static async Task<string> SendHttpAsync(int port, string pathAndQuery, int sendDelayMs = 0, bool authenticate = true)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();

            if (sendDelayMs > 0) await Task.Delay(sendDelayMs);

            var head = new StringBuilder();
            head.Append($"GET {pathAndQuery} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n");
            if (authenticate)
            {
                string cookie = await EnsureSessionCookieAsync(port);
                if (!string.IsNullOrEmpty(cookie)) head.Append($"Cookie: lmsid={cookie}\r\n");
            }
            head.Append("Connection: close\r\n\r\n");

            var req = Encoding.ASCII.GetBytes(head.ToString());
            await stream.WriteAsync(req, 0, req.Length);
            await stream.FlushAsync();

            var sb = new StringBuilder();
            var buf = new byte[4096];
            using var cts = new CancellationTokenSource(5000);
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

        /// <summary>
        /// 按端口缓存的登录会话（票据是一次性的，**不能**缓存）。
        /// </summary>
        /// <remarks>
        /// ★ 新协议下 HTTP API 一律要求主会话、WS 握手一律要求一次性票据；
        ///   这些用例关注的是"需求 / 租期"行为，鉴权由本辅助方法按真实协议走一遍。
        /// </remarks>
        private static readonly Dictionary<int, (string SessionId, string Csrf)> _sessions = new Dictionary<int, (string, string)>();

        /// <summary>取（必要时先登录）该端口上的主会话 Cookie 值。</summary>
        private static async Task<string> EnsureSessionCookieAsync(int port)
        {
            if (_sessions.TryGetValue(port, out var cached)) return cached.SessionId;

            // ★ 口令取当前配置：L0（未配置口令）时是空串 —— 空口令登录同样能换取会话。
            string password = Settings.Load().WebServerPassword ?? "";
            var login = await LoginAsync(port, password);
            _sessions[port] = login;
            return login.SessionId;
        }

        /// <summary>
        /// 按新协议登录：<c>POST /api/auth/login</c> ⇒ (会话 ID, CSRF 令牌)。
        /// </summary>
        private static async Task<(string SessionId, string Csrf)> LoginAsync(int port, string password)
        {
            string body = "{\"password\":\"" + (password ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}";
            string resp = await SendPostAsync(port, "/api/auth/login", body);
            Assert.True(resp.Contains("200"), "登录失败: " + resp);

            string setCookie = FindHeader(resp, "Set-Cookie");
            Assert.False(string.IsNullOrEmpty(setCookie), "登录响应必须下发 lmsid Cookie");
            int eq = setCookie.IndexOf('=');
            int semi = setCookie.IndexOf(';');
            string sessionId = (eq > 0 && semi > eq) ? setCookie.Substring(eq + 1, semi - eq - 1) : "";
            Assert.False(string.IsNullOrEmpty(sessionId), "登录响应必须下发 lmsid Cookie");

            string csrf = "";
            int idx = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (idx >= 0)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(resp.Substring(idx + 4));
                    if (doc.RootElement.TryGetProperty("csrf", out var el)) csrf = el.GetString() ?? "";
                }
                catch (System.Text.Json.JsonException) { }
            }
            Assert.False(string.IsNullOrEmpty(csrf), "登录响应必须下发 csrf 令牌");
            return (sessionId, csrf);
        }

        /// <summary>申请一次性票据（会话 Cookie + CSRF 头）。</summary>
        private static async Task<string> RequestTicketAsync(int port, string sessionId, string csrf)
        {
            string resp = await SendPostAsync(port, "/api/auth/ticket", "", sessionId, csrf);
            Assert.True(resp.Contains("200"), "票据申请失败: " + resp);

            int idx = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            using var doc = System.Text.Json.JsonDocument.Parse(resp.Substring(idx + 4));
            string ticket = doc.RootElement.TryGetProperty("ticket", out var el) ? (el.GetString() ?? "") : "";
            Assert.False(string.IsNullOrEmpty(ticket), "票据端点必须返回 ticket");
            return ticket;
        }

        /// <summary>发一个 POST 请求（可带请求体 / Cookie / CSRF 头）。</summary>
        private static async Task<string> SendPostAsync(int port, string path, string body,
            string cookie = null, string csrf = null, string contentType = "application/json")
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();

            var head = new StringBuilder();
            head.Append($"POST {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n");
            if (!string.IsNullOrEmpty(cookie)) head.Append($"Cookie: lmsid={cookie}\r\n");
            if (!string.IsNullOrEmpty(csrf)) head.Append($"X-LiteMonitor-CSRF: {csrf}\r\n");
            if (!string.IsNullOrEmpty(contentType)) head.Append($"Content-Type: {contentType}\r\n");
            head.Append($"Content-Length: {Encoding.UTF8.GetByteCount(body ?? "")}\r\n");
            head.Append("Connection: close\r\n\r\n");
            if (!string.IsNullOrEmpty(body)) head.Append(body);

            var req = Encoding.UTF8.GetBytes(head.ToString());
            await stream.WriteAsync(req, 0, req.Length);
            await stream.FlushAsync();

            var sb = new StringBuilder();
            var buf = new byte[4096];
            using var cts = new CancellationTokenSource(10000);
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

        /// <summary>完成一次 WebSocket 握手，返回保持连接的客户端</summary>
        /// <param name="sendDelayMs">
        /// 握手请求的发送延迟：服务器此时已经进入 HandleClientAsync 并阻塞在读请求头上，
        /// 用来制造"握手跨越 Stop 边界"的时间窗。
        /// </param>
        /// <param name="receiveBufferSize">
        /// 客户端接收缓冲大小（调小后，客户端不读数据时服务器的写操作会真的卡住）。
        /// </param>
        private static async Task<TcpClient> OpenWebSocketAsync(
            int port, string query = "", int sendDelayMs = 0, int receiveBufferSize = 0)
        {
            var client = new TcpClient();
            if (receiveBufferSize > 0) client.Client.ReceiveBufferSize = receiveBufferSize;
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();

            if (sendDelayMs > 0) await Task.Delay(sendDelayMs);

            // ★ 新协议下 WS 握手必须带**一次性票据**；没显式给就现换一张（票据用过即废）。
            string target = query ?? "";
            if (!target.Contains("ticket="))
            {
                string sessionId = await EnsureSessionCookieAsync(port);
                string csrf = _sessions.TryGetValue(port, out var cached) ? cached.Csrf : "";
                string ticket = await RequestTicketAsync(port, sessionId, csrf);
                target = "?ticket=" + ticket;
            }

            var req = Encoding.ASCII.GetBytes(
                $"GET /{target} HTTP/1.1\r\n" +
                $"Host: 127.0.0.1:{port}\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n");
            await stream.WriteAsync(req, 0, req.Length);
            await stream.FlushAsync();

            var sb = new StringBuilder();
            var buf = new byte[1024];
            using var cts = new CancellationTokenSource(5000);
            try
            {
                while (!sb.ToString().Contains("\r\n\r\n"))
                {
                    int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                    if (n <= 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
            }
            catch (OperationCanceledException) { }

            if (!sb.ToString().Contains("101"))
            {
                client.Close();
                throw new InvalidOperationException("WebSocket 握手失败: " + sb);
            }
            return client;
        }

        /// <summary>轮询等待条件成立（用于等待服务器侧回收连接）</summary>
        private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                await Task.Delay(20);
            }
            return condition();
        }

        // 注意：测试项目是 <Nullable>disable</Nullable>，这里不能用可空引用类型注解（会报 CS8632）
        private static WebSessionManager NewManager(WebDemandState demand = null)
        {
            // 空的快照内容：只关心需求状态，不关心 JSON
            return new WebSessionManager(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("k", "test");
                writer.WriteEndObject();
            }, demand);
        }

        [Fact]
        public async Task QuickRestartOldConnectionCompletesWithoutGrantingLease()
        {
            var clock = new FakeTimeProvider();
            var demand = new WebDemandState(clock, TimeSpan.FromSeconds(2));
            var manager = new WebSessionManager(
                (writer, _) =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("k", "test");
                    writer.WriteEndObject();
                    return Task.CompletedTask;
                },
                demand,
                clock,
                _ => Task.FromResult(false));

            var atLeaseCheck = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLeaseCheck = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            // Pause before the old request crosses the lease-check boundary.
            manager.HttpLeaseCheckTestHook = () =>
            {
                atLeaseCheck.TrySetResult(true);
                releaseLeaseCheck.Task.GetAwaiter().GetResult();
            };

            manager.Start();
            using var host = new LoopbackHost(manager);
            Task<string> oldRequest = SendHttpAsync(host.Port, "/api/snapshot");
            try
            {
                await atLeaseCheck.Task.WaitAsync(TimeSpan.FromSeconds(5));

                int oldGeneration = manager.Generation;
                manager.Stop();
                manager.Start();
                Assert.NotEqual(oldGeneration, manager.Generation);

                // The response may finish, but its old generation cannot grant demand.
                releaseLeaseCheck.TrySetResult(true);
                string oldResponse = await oldRequest.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Contains("200", oldResponse);
                Assert.False(manager.HasHttpLease);
                Assert.False(manager.HasWebDataDemand);

                manager.HttpLeaseCheckTestHook = null;

                // ★ Stop 会清空全部主会话（契约 W2：服务重启即全部失效）⇒
                //   重启后的请求必须**重新登录**拿新会话，旧 Cookie 已被撤销。
                _sessions.Remove(host.Port);
                string newResponse = await SendHttpAsync(host.Port, "/api/snapshot");

                Assert.Contains("200", newResponse);
                Assert.True(manager.HasHttpLease);
                Assert.True(manager.HasWebDataDemand);
            }
            finally
            {
                releaseLeaseCheck.TrySetResult(true);
                manager.Stop();
            }
        }

        private static string LangPath(string lang)
            => Path.Combine(AppContext.BaseDirectory, "resources", "lang", lang + ".json");

        private static Dictionary<string, string> LoadMenu(string lang)
        {
            string path = LangPath(lang);
            Assert.True(File.Exists(path), "语言包缺失: " + path);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(doc.RootElement.TryGetProperty("Menu", out var menu), lang + " 缺少 Menu 节点");

            // ★ 大小写敏感：key 名必须逐字一致（"webserverfps" 这种大小写错误也要被抓出来）
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in menu.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    map[prop.Name] = prop.Value.GetString() ?? "";
            }
            return map;
        }

        // ================================================================
        // 1. WebFpsEnabled 设置项
        // ================================================================

        [Fact]
        public void 旧设置JSON缺少WebFpsEnabled时默认为false()
        {
            // 一份"旧版本"的 settings.json：完全没有 WebFpsEnabled 这个字段
            const string legacy = "{\"RefreshMs\":1000,\"WebServerEnabled\":true,\"WebServerPort\":5000,\"WebServerPassword\":\"\"}";

            var cfg = JsonSerializer.Deserialize<Settings>(legacy, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            Assert.NotNull(cfg);
            Assert.False(cfg!.WebFpsEnabled);
        }

        [Fact]
        public void 新建设置的WebFpsEnabled默认为false()
        {
            Assert.False(new Settings().WebFpsEnabled);
        }

        [Fact]
        public void WebFpsEnabled可以持久化()
        {
            var cfg = new Settings { WebFpsEnabled = true };
            string json = JsonSerializer.Serialize(cfg);
            var back = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            Assert.NotNull(back);
            Assert.True(back!.WebFpsEnabled);
            Assert.Contains("WebFpsEnabled", json);
        }

        // ================================================================
        // 2. 需求状态（单元）
        // ================================================================

        [Fact]
        public void HTTP租期在有效期内成立_过期后失效()
        {
            var clock = new FakeTimeProvider();
            var demand = new WebDemandState(clock, TimeSpan.FromSeconds(2));

            Assert.False(demand.HasHttpLease);
            Assert.False(demand.HasAnyDemand);

            demand.GrantHttpLease();
            Assert.True(demand.HasHttpLease);
            Assert.True(demand.HasAnyDemand);

            clock.Advance(TimeSpan.FromMilliseconds(1999));
            Assert.True(demand.HasHttpLease);

            clock.Advance(TimeSpan.FromMilliseconds(2));
            Assert.False(demand.HasHttpLease);
            Assert.False(demand.HasAnyDemand);
        }

        [Fact]
        public void 连续授权会延长HTTP租期()
        {
            var clock = new FakeTimeProvider();
            var demand = new WebDemandState(clock, TimeSpan.FromSeconds(2));

            demand.GrantHttpLease();
            clock.Advance(TimeSpan.FromMilliseconds(1500));
            demand.GrantHttpLease();          // 连续轮询
            clock.Advance(TimeSpan.FromMilliseconds(1500));

            // 距离第一次授权已过 3 秒，但因为第二次授权仍在租期内
            Assert.True(demand.HasHttpLease);
        }

        [Fact]
        public void 单个连接断开后需求消失_多连接时断开一个仍有需求()
        {
            var demand = new WebDemandState(new FakeTimeProvider());

            demand.AddAuthenticatedConnection();
            Assert.Equal(1, demand.AuthenticatedConnectionCount);
            Assert.True(demand.HasAnyDemand);

            demand.AddAuthenticatedConnection();
            Assert.Equal(2, demand.AuthenticatedConnectionCount);

            demand.RemoveAuthenticatedConnection();
            Assert.Equal(1, demand.AuthenticatedConnectionCount);
            Assert.True(demand.HasAnyDemand);

            demand.RemoveAuthenticatedConnection();
            Assert.Equal(0, demand.AuthenticatedConnectionCount);
            Assert.False(demand.HasAnyDemand);
        }

        [Fact]
        public void 重复移除连接不会把计数减成负数()
        {
            var demand = new WebDemandState(new FakeTimeProvider());

            demand.RemoveAuthenticatedConnection();
            demand.RemoveAuthenticatedConnection();

            Assert.Equal(0, demand.AuthenticatedConnectionCount);
            Assert.False(demand.HasAnyDemand);
        }

        [Fact]
        public void Clear清空连接与HTTP租期()
        {
            var demand = new WebDemandState(new FakeTimeProvider());
            demand.AddAuthenticatedConnection();
            demand.GrantHttpLease();

            demand.Clear();

            Assert.Equal(0, demand.AuthenticatedConnectionCount);
            Assert.False(demand.HasHttpLease);
            Assert.False(demand.HasAnyDemand);
        }

        [Fact]
        public void 需求快照不撕裂_连接数与租期来自同一时刻()
        {
            var clock = new FakeTimeProvider();
            var demand = new WebDemandState(clock, TimeSpan.FromSeconds(2));

            demand.AddAuthenticatedConnection();
            demand.GrantHttpLease();

            var snap = demand.GetSnapshot();
            Assert.Equal(1, snap.AuthenticatedConnections);
            Assert.True(snap.HasHttpLease);
            Assert.True(snap.HasAnyDemand);

            // 租期过期、连接还在 => 仍有需求
            clock.Advance(TimeSpan.FromSeconds(3));
            snap = demand.GetSnapshot();
            Assert.False(snap.HasHttpLease);
            Assert.Equal(1, snap.AuthenticatedConnections);
            Assert.True(snap.HasAnyDemand);

            // 连接断开 => 没有需求
            demand.RemoveAuthenticatedConnection();
            snap = demand.GetSnapshot();
            Assert.Equal(0, snap.AuthenticatedConnections);
            Assert.False(snap.HasAnyDemand);
        }

        [Fact]
        public async Task 需求状态并发读写不抛异常且快照不撕裂()
        {
            var demand = new WebDemandState(new FakeTimeProvider());
            var errors = new List<Exception>();
            using var cts = new CancellationTokenSource();

            // 写线程只在三个**合法**状态之间切换：
            //   Clear()            -> (0 个连接, 无租期)   ← 原子地把两个字段一起清零
            //   AddConnection()    -> (1 个连接, 无租期)
            //   GrantHttpLease()   -> (1 个连接, 有租期)
            // 因此 "0 个连接却有租期" 永远不可能合法出现 —— 读线程一旦看到就是撕裂。
            var writer = Task.Run(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        demand.Clear();
                        demand.AddAuthenticatedConnection();
                        demand.GrantHttpLease();
                    }
                }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            });

            var readers = new List<Task>();
            for (int i = 0; i < 4; i++)
            {
                readers.Add(Task.Run(() =>
                {
                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            var snap = demand.GetSnapshot();
                            if (snap.AuthenticatedConnections < 0)
                                throw new InvalidOperationException("连接数出现负值: " + snap.AuthenticatedConnections);
                            if (snap.AuthenticatedConnections > 1)
                                throw new InvalidOperationException("连接数出现不可能的值: " + snap.AuthenticatedConnections);
                            if (snap.AuthenticatedConnections == 0 && snap.HasHttpLease)
                                throw new InvalidOperationException("快照撕裂: 0 个连接却读到有效租期");
                        }
                    }
                    catch (Exception ex) { lock (errors) errors.Add(ex); }
                }));
            }

            await Task.Delay(300);
            cts.Cancel();
            await Task.WhenAll(readers);
            await writer;

            Assert.Empty(errors);

            demand.Clear();
            Assert.Equal(0, demand.AuthenticatedConnectionCount);
            Assert.False(demand.HasHttpLease);
        }

        // ================================================================
        // 3. WebSessionManager（回环真实连接）
        // ================================================================

        [Fact]
        public async Task 已鉴权的快照请求产生短需求租期()
        {
            var manager = NewManager();
            manager.Start();
            using var host = new LoopbackHost(manager);

            string resp = await SendHttpAsync(host.Port, "/api/snapshot");
            Assert.Contains("200", resp);
            Assert.True(manager.HasHttpLease);
            Assert.True(manager.HasWebDataDemand);
            Assert.Equal(0, manager.AuthenticatedConnectionCount);

            manager.Stop();
        }

        [Fact]
        public async Task 跨越Stop边界的快照请求不再授予租期()
        {
            // 请求在服务器"等请求头"时发生 Stop：
            // Stop 已经清空全部需求，就不该再被这次在途请求补上一个 2 秒租期。
            var manager = NewManager();
            manager.Start();
            using var host = new LoopbackHost(manager);

            var request = SendHttpAsync(host.Port, "/api/snapshot", sendDelayMs: 400);
            await Task.Delay(150);        // 服务器已进入 HandleClientAsync，在等请求头
            manager.Stop();               // 代次失效 + 需求清零

            string resp = await request;
            Assert.Contains("200", resp);
            Assert.False(manager.HasHttpLease);
            Assert.False(manager.HasWebDataDemand);
        }

        [Fact]
        public async Task 未鉴权的快照请求不产生任何需求()
        {
            _cfg.WebServerPassword = "s3cret";

            var manager = NewManager();
            manager.Start();
            using var host = new LoopbackHost(manager);

            string resp = await SendHttpAsync(host.Port, "/api/snapshot", authenticate: false);
            Assert.Contains("401", resp);

            Assert.False(manager.HasHttpLease);
            Assert.False(manager.HasWebDataDemand);
            Assert.Equal(0, manager.AuthenticatedConnectionCount);

            manager.Stop();
        }

        [Fact]
        public async Task 仅取HTML或favicon不产生任何需求()
        {
            _cfg.WebServerPassword = "s3cret";

            var manager = NewManager();
            manager.Start();
            using var host = new LoopbackHost(manager);

            string html = await SendHttpAsync(host.Port, "/");
            Assert.Contains("200", html);

            string favicon = await SendHttpAsync(host.Port, "/favicon.ico");
            Assert.Contains("404", favicon);

            Assert.False(manager.HasHttpLease);
            Assert.False(manager.HasWebDataDemand);
            Assert.Equal(0, manager.AuthenticatedConnectionCount);

            manager.Stop();
        }

        [Fact]
        public async Task WebSocket连接计入需求_断开后需求被回收()
        {
            var manager = NewManager();
            manager.Start();               // 未启动的会话管理器不登记任何连接需求
            using var host = new LoopbackHost(manager);

            using var ws = await OpenWebSocketAsync(host.Port);

            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 1));
            Assert.True(manager.HasWebDataDemand);

            // 断开：服务器侧读循环结束 -> 回收需求
            ws.Close();
            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 0));
            Assert.False(manager.HasWebDataDemand);

            manager.Stop();
        }

        [Fact]
        public async Task 多个WebSocket连接时断开一个仍保留需求()
        {
            var manager = NewManager();
            manager.Start();
            using var host = new LoopbackHost(manager);

            using var ws1 = await OpenWebSocketAsync(host.Port);
            using var ws2 = await OpenWebSocketAsync(host.Port);

            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 2));

            ws1.Close();
            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 1));
            Assert.True(manager.HasWebDataDemand);

            ws2.Close();
            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 0));
            Assert.False(manager.HasWebDataDemand);

            manager.Stop();
        }

        [Fact]
        public async Task Stop清空全部需求_重启不残留旧需求()
        {
            var manager = NewManager();
            manager.Start();
            using var host = new LoopbackHost(manager);

            using var ws = await OpenWebSocketAsync(host.Port);
            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 1));

            // 停止服务：连接与 HTTP 租期都必须被清空
            manager.Stop();
            Assert.Equal(0, manager.AuthenticatedConnectionCount);
            Assert.False(manager.HasHttpLease);
            Assert.False(manager.HasWebDataDemand);

            // 重启：不能留下任何旧需求
            manager.Start();
            await Task.Delay(100);
            Assert.Equal(0, manager.AuthenticatedConnectionCount);
            Assert.False(manager.HasWebDataDemand);

            manager.Stop();
        }

        // ------------------------------------------------------------
        // 需求回收：Stop 边界 / 发送异常
        // ------------------------------------------------------------

        [Fact]
        public async Task 握手跨越Stop边界时连接不计入需求()
        {
            // 复现评审指出的竞态：
            //   客户端已连上，服务器正阻塞在读握手请求头（代次 = G），
            //   此时 Stop() 发生（代次变成 G+1，需求被清零），
            //   随后握手才成功 —— 这时绝不能把连接重新登记成需求。
            //   否则 Stop 不会再遍历一次集合去关闭它，需求会永久卡住。
            var manager = NewManager();
            manager.Start();
            using var host = new LoopbackHost(manager);

            // 客户端连上后延迟 400ms 才发握手请求
            var handshake = OpenWebSocketAsync(host.Port, sendDelayMs: 400);
            await Task.Delay(150);            // 服务器此时已进入 HandleClientAsync，在等请求头
            manager.Stop();                   // 代次失效 + 需求清零

            using var ws = await handshake;   // 握手此刻才成功返回
            await Task.Delay(300);            // 等服务器侧处理完这次握手

            Assert.Equal(0, manager.AuthenticatedConnectionCount);
            Assert.False(manager.HasWebDataDemand);
        }

        [Fact]
        public async Task 停止后到达的连接不计入需求()
        {
            var manager = NewManager();
            manager.Start();
            using var host = new LoopbackHost(manager);

            manager.Stop();                   // 服务已停

            using var ws = await OpenWebSocketAsync(host.Port);
            await Task.Delay(300);

            Assert.Equal(0, manager.AuthenticatedConnectionCount);
            Assert.False(manager.HasWebDataDemand);
        }

        [Fact]
        public async Task 广播写失败时需求被回收()
        {
            // 专门盯"写失败"这条回收路径（SendFrameSafeAsync 的 catch -> RemoveClient）：
            //  - 快照内容接近广播缓冲上限（32KB），客户端的接收缓冲被调小；
            //  - 客户端握手之后**不再读取**任何数据，服务器的写操作必然被卡住；
            //  - 写超时（2 秒）触发 catch -> RemoveClient -> 需求回收。
            // 客户端连接始终保持打开，所以"读"路径不会先触发，这条用例只可能由写失败回收。
            var manager = new WebSessionManager(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("k", new string('x', 30000));
                writer.WriteEndObject();
            });
            manager.Start();
            using var host = new LoopbackHost(manager, socketBufferSize: 4096);

            using var ws = await OpenWebSocketAsync(host.Port, receiveBufferSize: 4096);
            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 1));

            // 写失败后需求必须被回收
            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 0, 30000));
            Assert.False(manager.HasWebDataDemand);

            manager.Stop();
        }

        [Fact]
        public async Task 连接被重置时需求被回收且计数不重复扣减()
        {
            // LingerOption(true, 0) 让 close() 发 RST 而不是 FIN：
            // 服务器的读路径与广播写路径会同时失败，两条路径都会调用 RemoveClient，
            // 这里断言需求归零且**不会**被扣成负数（计数配平）。
            var manager = NewManager();
            manager.Start();
            using var host = new LoopbackHost(manager);

            var ws = await OpenWebSocketAsync(host.Port);
            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 1));

            ws.Client.LingerState = new LingerOption(true, 0);   // close 时发 RST
            ws.Close();

            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 0));
            await Task.Delay(500);            // 给广播写路径一点时间（它也会失败并再次 RemoveClient）
            Assert.Equal(0, manager.AuthenticatedConnectionCount);
            Assert.False(manager.HasWebDataDemand);

            manager.Stop();
        }

        // ================================================================
        // 4. FPS 需求规则与快照占位
        // ================================================================

        [Fact]
        public void FPS需求规则_本地启用或Web显式开启()
        {
            // 默认：本地没启用 FPS、网页版也没开 => 没有需求
            var cfg = new Settings();
            Assert.False(WebFpsDemand.IsLocalFpsDemand(cfg));
            Assert.False(WebFpsDemand.IsFpsDemand(cfg, hasWebDataDemand: true));

            // 本地启用（面板可见）=> 有需求，且与有没有 Web 需求无关
            cfg.MonitorItems.Add(new MonitorItemConfig { Key = "FPS", VisibleInPanel = true });
            Assert.True(WebFpsDemand.IsLocalFpsDemand(cfg));
            Assert.True(WebFpsDemand.IsFpsDemand(cfg, hasWebDataDemand: false));

            // 只开网页版：必须同时满足"有合法 Web 需求"
            var webOnly = new Settings { WebFpsEnabled = true };
            Assert.False(WebFpsDemand.IsLocalFpsDemand(webOnly));
            Assert.False(WebFpsDemand.IsWebFpsDemand(webOnly, hasWebDataDemand: false));
            Assert.True(WebFpsDemand.IsWebFpsDemand(webOnly, hasWebDataDemand: true));
            Assert.True(WebFpsDemand.IsFpsDemand(webOnly, hasWebDataDemand: true));

            // 网页版开关关闭时，即使有 Web 需求也没有 FPS 需求
            var webOff = new Settings { WebFpsEnabled = false };
            Assert.False(WebFpsDemand.IsFpsDemand(webOff, hasWebDataDemand: true));
        }

        [Fact]
        public void FPS未启用时输出占位且不读取数值()
        {
            int readCount = 0;

            var state = WebSnapshotValues.ResolveValue("FPS", fpsDemand: false, k =>
            {
                readCount++;
                return 0f;      // 采集服务没跑时 FpsCounter 就是返回 0
            }, out string value, out float? raw);

            Assert.Equal(WebSnapshotValueState.NotEnabled, state);
            Assert.Equal("--", value);
            Assert.Null(raw);

            // ★ 关键：不读取数值 => 不会触发外部 FPS 采集服务的惰性启动
            Assert.Equal(0, readCount);
        }

        [Fact]
        public void FPS启用时读取真实数值而不是占位()
        {
            var state = WebSnapshotValues.ResolveValue("FPS", fpsDemand: true, k => 58.2f, out string value, out float? raw);

            Assert.Equal(WebSnapshotValueState.HasData, state);
            Assert.NotNull(raw);
            Assert.Equal(58.2f, raw!.Value);
            Assert.Equal("58", value);
            Assert.NotEqual("--", value);
        }

        [Fact]
        public void 普通指标没有数据时仍然跳过该行()
        {
            var state = WebSnapshotValues.ResolveValue("BAT.Percent", fpsDemand: false, k => null, out string value, out float? raw);

            Assert.Equal(WebSnapshotValueState.NoData, state);
            Assert.Equal("--", value);
            Assert.Null(raw);
        }

        [Fact]
        public void 普通指标有数据时按真实数值输出()
        {
            var state = WebSnapshotValues.ResolveValue("CPU.Load", fpsDemand: false, k => 42.5f, out string value, out float? raw);

            Assert.Equal(WebSnapshotValueState.HasData, state);
            Assert.NotNull(raw);
            Assert.Equal(42.5f, raw!.Value);
            Assert.Equal("42.5", value);
        }

        // ================================================================
        // 5. 语言包文案
        // ================================================================

        [Theory]
        [InlineData("zh")]
        [InlineData("zh-tw")]
        [InlineData("en")]
        [InlineData("ja")]
        [InlineData("ko")]
        [InlineData("ru")]
        [InlineData("de")]
        [InlineData("es")]
        [InlineData("fr")]
        public void 九个语言包都有网页帧率与刷新频率说明(string lang)
        {
            var menu = LoadMenu(lang);

            foreach (var key in new[] { "WebServerFps", "WebServerFpsTip", "RefreshTip" })
            {
                Assert.True(menu.ContainsKey(key), lang + " 缺少 " + key);
                Assert.False(string.IsNullOrWhiteSpace(menu[key]), lang + " 的 " + key + " 是空字符串");
            }
        }

        [Fact]
        public void 九个语言包的文案key名完全一致()
        {
            var reference = LoadMenu("zh").Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

            foreach (var lang in AllLangs)
            {
                var keys = LoadMenu(lang).Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                // ★ 大小写敏感比较：key 名必须逐字一致
                var missing = reference.Except(keys, StringComparer.Ordinal).ToList();
                var extra = keys.Except(reference, StringComparer.Ordinal).ToList();

                Assert.True(missing.Count == 0, lang + " 缺少 key: " + string.Join(", ", missing.Take(10)));
                Assert.True(extra.Count == 0, lang + " 多出 key: " + string.Join(", ", extra.Take(10)));
            }
        }
    }
}
