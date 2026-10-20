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

        public WebDemandTests()
        {
            _cfg = Settings.Load();
            _oldPassword = _cfg.WebServerPassword;
            _cfg.WebServerPassword = "";   // 默认无密码，个别用例再单独打开
        }

        public void Dispose()
        {
            _cfg.WebServerPassword = _oldPassword;
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
        private static async Task<string> SendHttpAsync(int port, string pathAndQuery)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();

            var req = Encoding.ASCII.GetBytes(
                $"GET {pathAndQuery} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
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

        /// <summary>完成一次 WebSocket 握手，返回保持连接的客户端</summary>
        private static async Task<TcpClient> OpenWebSocketAsync(int port, string query = "")
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();

            var req = Encoding.ASCII.GetBytes(
                $"GET /{query} HTTP/1.1\r\n" +
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

        private static WebSessionManager NewManager(WebDemandState? demand = null)
        {
            // 空的快照内容：只关心需求状态，不关心 JSON
            return new WebSessionManager(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("k", "test");
                writer.WriteEndObject();
            }, demand);
        }

        private static string LangPath(string lang)
            => Path.Combine(AppContext.BaseDirectory, "resources", "lang", lang + ".json");

        private static Dictionary<string, string> LoadMenu(string lang)
        {
            string path = LangPath(lang);
            Assert.True(File.Exists(path), "语言包缺失: " + path);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(doc.RootElement.TryGetProperty("Menu", out var menu), lang + " 缺少 Menu 节点");

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
        public async Task 需求状态并发读写不抛异常且计数不为负()
        {
            var demand = new WebDemandState(new FakeTimeProvider());
            var errors = new List<Exception>();
            using var cts = new CancellationTokenSource();

            var workers = new List<Task>();
            for (int i = 0; i < 4; i++)
            {
                workers.Add(Task.Run(() =>
                {
                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            demand.AddAuthenticatedConnection();
                            demand.RemoveAuthenticatedConnection();
                        }
                    }
                    catch (Exception ex) { lock (errors) errors.Add(ex); }
                }));
            }
            for (int i = 0; i < 4; i++)
            {
                workers.Add(Task.Run(() =>
                {
                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            var snap = demand.GetSnapshot();
                            if (snap.AuthenticatedConnections < 0)
                                throw new InvalidOperationException("连接数出现负值: " + snap.AuthenticatedConnections);
                            if (snap.HasAnyDemand != (snap.AuthenticatedConnections > 0 || snap.HasHttpLease))
                                throw new InvalidOperationException("需求快照内部不一致");
                        }
                    }
                    catch (Exception ex) { lock (errors) errors.Add(ex); }
                }));
            }

            await Task.Delay(300);
            cts.Cancel();
            await Task.WhenAll(workers);

            Assert.Empty(errors);
            Assert.Equal(0, demand.AuthenticatedConnectionCount);
        }

        // ================================================================
        // 3. WebSessionManager（回环真实连接）
        // ================================================================

        [Fact]
        public async Task 已鉴权的快照请求产生短需求租期()
        {
            var manager = NewManager();
            using var host = new LoopbackHost(manager);

            string resp = await SendHttpAsync(host.Port, "/api/snapshot");
            Assert.Contains("200", resp);
            Assert.True(manager.HasHttpLease);
            Assert.True(manager.HasWebDataDemand);
            Assert.Equal(0, manager.AuthenticatedConnectionCount);

            manager.Stop();
        }

        [Fact]
        public async Task 未鉴权的快照请求不产生任何需求()
        {
            _cfg.WebServerPassword = "s3cret";

            var manager = NewManager();
            using var host = new LoopbackHost(manager);

            string resp = await SendHttpAsync(host.Port, "/api/snapshot");
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
                var missing = reference.Except(keys, StringComparer.OrdinalIgnoreCase).ToList();
                var extra = keys.Except(reference, StringComparer.OrdinalIgnoreCase).ToList();

                Assert.True(missing.Count == 0, lang + " 缺少 key: " + string.Join(", ", missing.Take(10)));
                Assert.True(extra.Count == 0, lang + " 多出 key: " + string.Join(", ", extra.Take(10)));
            }
        }
    }
}
