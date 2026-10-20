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
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.WebServer;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// B2 接线测试：Web 侧共享采样接入 + 需求传播 + FPS 占位（计划 §4.4 / §6.1）。
    ///
    /// 覆盖简报 §3 的 16 项必测用例：
    ///   1. Web 开关开但无人连接 → 不扩大采集范围
    ///   2. 未鉴权请求 → 不计入需求
    ///   3. 单个 / 多个合法连接 → 需求计数正确
    ///   4. 最后一个连接断开 → 需求被回收
    ///   5. 发送异常 → 需求被回收
    ///   6. HTTP 租期过期 → 需求被回收（不被广播周期拖延）
    ///   7. 两种消费者共用采样 → 实际硬件更新计数不叠加
    ///   8. Web 单卡选择 / 双卡配置 → 实际更新计数
    ///   9. 旧 JSON → WebFpsEnabled 默认 false
    ///  10. 隐藏 FPS → 不调用启动
    ///  11. 本地单独启用 / Web 单独启用 → 各自可用
    ///  12. 关闭一方 → 不停止另一方
    ///  13. Web FPS 关闭 → 不触发下载
    ///  14. 快照 → 不执行 hw.Update()
    ///  15. 无 FPS 需求时 → 不调用 hw.Get("FPS")
    ///  16. HasFpsData == false → 输出 "--"，不是 0
    ///
    /// 测试纪律：
    ///  - 不启动 LHM / 驱动 / 真实 PresentMon / 外网请求（只用 127.0.0.1 回环）；
    ///  - 租期与超时用**假时钟**（单调）驱动，不用 DateTime.Now / TickCount 取模；
    ///  - 不新增 NuGet 包 / 测试框架，私有替身都定义在本文件内。
    ///
    /// 本类会改 <c>Settings.Load()</c> 里的访问密码与网页版帧率开关，因此挂到 "cfgfile" 集合上串行执行。
    /// </summary>
    [Collection("cfgfile")]
    public class WebSamplingDemandTests : IDisposable
    {
        private readonly Settings _cfg;
        private readonly string _oldPassword;
        private readonly bool _oldWebFpsEnabled;

        public WebSamplingDemandTests()
        {
            _cfg = Settings.Load();
            _oldPassword = _cfg.WebServerPassword;
            _oldWebFpsEnabled = _cfg.WebFpsEnabled;
            _cfg.WebServerPassword = "";   // 默认无密码，个别用例再单独打开
        }

        public void Dispose()
        {
            _cfg.WebServerPassword = _oldPassword;
            _cfg.WebFpsEnabled = _oldWebFpsEnabled;
        }

        // ================================================================
        // 测试替身 / 私有 helper
        // ================================================================

        /// <summary>假单调时钟（100ns 刻度，可手动推进）。</summary>
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

        /// <summary>记录"写进采集层的需求值"的替身（线程安全）。</summary>
        private sealed class RecordingDemandSink
        {
            private readonly object _gate = new object();
            private readonly List<bool> _values = new List<bool>();

            public bool Apply(bool value)
            {
                lock (_gate) _values.Add(value);
                return true;
            }

            public int Count { get { lock (_gate) return _values.Count; } }

            public bool Last
            {
                get { lock (_gate) return _values.Count > 0 && _values[_values.Count - 1]; }
            }

            public List<bool> Snapshot()
            {
                lock (_gate) return new List<bool>(_values);
            }
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

        /// <summary>空的 ISettings 实现（LHM 的 Hardware 基类构造函数需要它）。</summary>
        private sealed class FakeLhmSettings : ISettings
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public bool Contains(string name) => _values.ContainsKey(name);
            public void SetValue(string name, string value) => _values[name] = value;
            public string GetValue(string name, string value) => _values.TryGetValue(name, out var v) ? v : value;
            public void Remove(string name) => _values.Remove(name);
        }

        /// <summary>假的显卡硬件（不接触驱动，只用于显卡选择规则）。</summary>
        private sealed class FakeGpuHardware : Hardware
        {
            public FakeGpuHardware(string name)
                : base(name, new Identifier(new[] { name }), new FakeLhmSettings())
            {
            }

            public override HardwareType HardwareType => LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia;
            public override ISensor[] Sensors => Array.Empty<ISensor>();
            public override void Update() { }
        }

        /// <summary>
        /// 新建一个会话管理器。
        /// </summary>
        private static WebSessionManager NewManager(
            WebDemandState demandState = null,
            TimeProvider timeProvider = null,
            WebSamplingRequester samplingRequester = null,
            Action<Utf8JsonWriter> syncProvider = null)
        {
            if (syncProvider != null)
            {
                return new WebSessionManager(
                    (writer, ct) => { syncProvider(writer); return Task.CompletedTask; },
                    demandState,
                    timeProvider,
                    samplingRequester);
            }

            // 空的快照内容：只关心需求状态与采样次数，不关心 JSON
            return new WebSessionManager(
                (writer, ct) =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("k", "test");
                    writer.WriteEndObject();
                    return Task.CompletedTask;
                },
                demandState,
                timeProvider,
                samplingRequester);
        }

        /// <summary>发一个普通 HTTP 请求并把响应读回来（服务器读完会关闭连接）。</summary>
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

        /// <summary>完成一次 WebSocket 握手，返回保持连接的客户端。</summary>
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
            using var cts = new CancellationTokenSource(10000);
            try
            {
                while (!sb.ToString().Contains("\r\n\r\n"))
                {
                    int n = await stream.ReadAsync(buf, 0, buf.Length);
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

        /// <summary>读一个完整的 WebSocket 帧负载（服务器→客户端的帧不加掩码）。</summary>
        private static async Task<byte[]> ReadFrameAsync(TcpClient client, int timeoutMs = 10000)
        {
            var stream = client.GetStream();
            using var cts = new CancellationTokenSource(timeoutMs);

            var header = new byte[2];
            await ReadExactAsync(stream, header, 2, cts.Token);

            int length = header[1] & 0x7F;
            if (length == 126)
            {
                var ext = new byte[2];
                await ReadExactAsync(stream, ext, 2, cts.Token);
                length = (ext[0] << 8) | ext[1];
            }
            else if (length == 127)
            {
                // ★ 64 位长度是**大端**（网络字节序，RFC 6455）
                var ext = new byte[8];
                await ReadExactAsync(stream, ext, 8, cts.Token);
                long value = 0;
                for (int i = 0; i < 8; i++) value = (value << 8) | ext[i];
                length = (int)value;
            }

            var payload = new byte[length];
            await ReadExactAsync(stream, payload, length, cts.Token);
            return payload;
        }

        private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken token)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), token);
                if (n <= 0) throw new IOException("连接已关闭");
                read += n;
            }
        }

        /// <summary>轮询等待条件成立。</summary>
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

        /// <summary>从 HTTP 响应里取出 JSON 主体。</summary>
        private static string ExtractJsonBody(string httpResponse)
        {
            int idx = httpResponse.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return idx >= 0 ? httpResponse.Substring(idx + 4) : httpResponse;
        }

        /// <summary>在仓库里定位源文件（从测试输出目录向上找仓库根）。</summary>
        private static string FindRepoFile(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate)) return candidate;
                if (File.Exists(Path.Combine(dir.FullName, "LiteMonitor.csproj"))) return null;  // 已到仓库根仍未找到
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>去掉注释，只留代码（用于源码级不变量断言）。</summary>
        private static string StripComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            int i = 0;
            while (i < source.Length)
            {
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n') i++;
                }
                else if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                    i = Math.Min(i + 2, source.Length);
                }
                else
                {
                    sb.Append(source[i]);
                    i++;
                }
            }
            return sb.ToString();
        }

        // ================================================================
        // 1. 需求传播（Web 需求 → 采集层 SetWebDataDemand）
        // ================================================================

        [Fact]
        public void 需求监视间隔小于一个采集周期()
        {
            // 需求消失后必须在"≤1 个采集周期(1s) + 宽限"内送达 SetWebDataDemand(false)。
            // 周期监视是 HTTP 租期到期唯一的发现途径（租期到期没有事件可挂靠）。
            Assert.True(WebDemandSynchronizer.DefaultMonitorInterval < TimeSpan.FromSeconds(1),
                "需求监视间隔必须小于一个采集周期（1 秒）");
        }

        [Fact]
        public void 需求值变化时才写入采集层_值不变不重复写()
        {
            var demand = new WebDemandState(new FakeTimeProvider());
            var manager = NewManager(demandState: demand);
            var sink = new RecordingDemandSink();
            var sync = new WebDemandSynchronizer(() => manager.HasWebDataDemand, sink.Apply);

            // 没有需求：写入一次 false
            sync.SyncNow();
            Assert.Equal(1, sink.Count);
            Assert.False(sink.Last);

            // 需求没变：不再重复写
            sync.SyncNow();
            sync.SyncNow();
            Assert.Equal(1, sink.Count);

            // 需求出现：写入 true
            demand.AddAuthenticatedConnection();
            sync.SyncNow();
            Assert.Equal(2, sink.Count);
            Assert.True(sink.Last);

            // 需求再次没变：仍然不重复写
            sync.SyncNow();
            Assert.Equal(2, sink.Count);
        }

        [Fact]
        public void 采集层实例还不存在时不记账_实例出现后补推需求()
        {
            // 对应契约：HardwareMonitor.Instance 可能为 null，或比 Web 服务更晚创建。
            object target = null;
            var sink = new RecordingDemandSink();
            var sync = new WebDemandSynchronizer(
                demandProvider: () => true,
                applyDemand: v => { if (target == null) return false; return sink.Apply(v); },
                targetProvider: () => target);

            // 采集层还不存在：写不进去 → 不记账
            sync.SyncNow();
            Assert.False(sync.HasApplied);
            Assert.Equal(0, sink.Count);

            // 采集层出现了：补推一次
            target = new object();
            sync.SyncNow();
            Assert.True(sync.HasApplied);
            Assert.True(sink.Last);
            Assert.Equal(1, sink.Count);
        }

        [Fact]
        public void 采集层换了实例后需求会被重新送达()
        {
            object target = new object();
            var sink = new RecordingDemandSink();
            var sync = new WebDemandSynchronizer(
                demandProvider: () => true,
                applyDemand: v => sink.Apply(v),
                targetProvider: () => target);

            sync.SyncNow();
            Assert.Equal(1, sink.Count);

            // 换了实例：需求值没变也要重新送达一次
            target = new object();
            sync.SyncNow();
            Assert.Equal(2, sink.Count);
            Assert.True(sink.Last);
        }

        [Fact]
        public async Task 最后一个连接断开后需求被回收并送达采集层()
        {
            var demand = new WebDemandState(new FakeTimeProvider());
            var manager = NewManager(demandState: demand);
            manager.Start();

            var sink = new RecordingDemandSink();
            var sync = new WebDemandSynchronizer(() => manager.HasWebDataDemand, sink.Apply);
            manager.DemandChanged = () => sync.SyncNow();

            using var host = new LoopbackHost(manager);
            using var client = await OpenWebSocketAsync(host.Port);

            Assert.True(await WaitUntilAsync(() => sink.Last, 5000), "连接建立后需求应被送达采集层");
            Assert.True(manager.HasWebDataDemand);
            Assert.Equal(1, manager.AuthenticatedConnectionCount);

            client.Close();

            Assert.True(await WaitUntilAsync(() => sink.Count >= 2 && !sink.Last, 5000),
                "最后一个连接断开后需求必须被回收");
            Assert.False(manager.HasWebDataDemand);
            Assert.Equal(0, manager.AuthenticatedConnectionCount);
        }

        [Fact]
        public async Task 多个连接时断开一个仍保留需求_断开最后一个才回收()
        {
            var demand = new WebDemandState(new FakeTimeProvider());
            var manager = NewManager(demandState: demand);
            manager.Start();

            var sink = new RecordingDemandSink();
            var sync = new WebDemandSynchronizer(() => manager.HasWebDataDemand, sink.Apply);
            manager.DemandChanged = () => sync.SyncNow();

            using var host = new LoopbackHost(manager);
            using var c1 = await OpenWebSocketAsync(host.Port);
            using var c2 = await OpenWebSocketAsync(host.Port);

            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 2, 5000));
            Assert.True(await WaitUntilAsync(() => sink.Last, 5000));

            c1.Close();
            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 1, 5000));
            Assert.True(manager.HasWebDataDemand, "断开一个后仍有需求");
            Assert.True(sink.Last, "需求仍为真，不应被回收");

            c2.Close();
            Assert.True(await WaitUntilAsync(() => !manager.HasWebDataDemand, 5000));
            Assert.True(await WaitUntilAsync(() => sink.Count >= 2 && !sink.Last, 5000),
                "最后一个连接断开后需求必须被回收");
        }

        [Fact]
        public async Task 发送异常时需求被回收并送达采集层()
        {
            // 客户端握手后不读任何数据 → 服务器的写会被卡住 → SendFrameSafeAsync 的写超时触发
            // → RemoveClient → 需求被回收。
            var demand = new WebDemandState(new FakeTimeProvider());
            var big = new string('x', 200 * 1024);
            var manager = NewManager(
                demandState: demand,
                syncProvider: writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("d", big);
                    writer.WriteEndObject();
                });
            manager.Start();

            var sink = new RecordingDemandSink();
            var sync = new WebDemandSynchronizer(() => manager.HasWebDataDemand, sink.Apply);
            manager.DemandChanged = () => sync.SyncNow();

            using var host = new LoopbackHost(manager);
            using var client = await OpenWebSocketAsync(host.Port);

            Assert.True(await WaitUntilAsync(() => sink.Last, 5000), "连接建立后需求应被送达采集层");
            Assert.True(manager.HasWebDataDemand);

            // 等待广播写失败（写超时 2 秒）
            Assert.True(await WaitUntilAsync(() => !manager.HasWebDataDemand, 15000),
                "发送异常后需求必须被回收");
            Assert.True(await WaitUntilAsync(() => sink.Count >= 2 && !sink.Last, 5000),
                "发送异常后必须把 false 送达采集层");
        }

        [Fact]
        public async Task HTTP租期过期后需求被回收_且不被广播周期拖延()
        {
            // ★ 本用例是简报 §2.1 第 2 条的核心：需求消失后必须及时送达 false。
            //   这里**没有任何 WebSocket 连接** → 广播循环不会产生任何帧，
            //   因此回收只能靠"周期监视"发现 HTTP 租期到期（不能只依赖广播周期）。
            var fakeTime = new FakeTimeProvider();
            var demand = new WebDemandState(fakeTime, TimeSpan.FromSeconds(2));
            var manager = NewManager(demandState: demand);
            manager.Start();

            var sink = new RecordingDemandSink();
            var sync = new WebDemandSynchronizer(() => manager.HasWebDataDemand, sink.Apply);
            manager.DemandChanged = () => sync.SyncNow();
            sync.Start();

            try
            {
                using var host = new LoopbackHost(manager);

                string resp = await SendHttpAsync(host.Port, "/api/snapshot");
                Assert.Contains("200", resp);
                Assert.True(manager.HasHttpLease);
                Assert.True(await WaitUntilAsync(() => sink.Last, 5000), "快照请求应产生需求");

                // 推进假时钟 → 租期过期（墙上时间不动）
                fakeTime.Advance(TimeSpan.FromSeconds(3));
                Assert.False(manager.HasHttpLease);

                // 周期监视必须在 1 个采集周期 + 宽限内发现并送达 false
                var sw = Stopwatch.StartNew();
                bool recovered = await WaitUntilAsync(() => sink.Count >= 2 && !sink.Last, 3000);
                sw.Stop();

                Assert.True(recovered, "HTTP 租期过期后需求必须被回收");
                Assert.False(manager.HasWebDataDemand);
                Assert.True(sw.ElapsedMilliseconds < 3000,
                    "回收不能被广播周期拖延（广播间隔可能 ≥ 2 秒）");
            }
            finally
            {
                sync.Stop();
                manager.Stop();
            }
        }

        [Fact]
        public async Task Stop之后需求为无且false已送达采集层()
        {
            var demand = new WebDemandState(new FakeTimeProvider());
            var manager = NewManager(demandState: demand);
            manager.Start();

            var sink = new RecordingDemandSink();
            var sync = new WebDemandSynchronizer(() => manager.HasWebDataDemand, sink.Apply);
            manager.DemandChanged = () => sync.SyncNow();

            using var host = new LoopbackHost(manager);
            using var client = await OpenWebSocketAsync(host.Port);
            Assert.True(await WaitUntilAsync(() => sink.Last, 5000));

            manager.Stop();

            Assert.False(manager.HasWebDataDemand);
            Assert.Equal(0, manager.AuthenticatedConnectionCount);
            Assert.False(manager.HasHttpLease);
            Assert.True(await WaitUntilAsync(() => sink.Count >= 2 && !sink.Last, 5000),
                "Stop 之后必须把 false 送达采集层");
        }

        [Fact]
        public async Task 未鉴权请求不计入需求_不产生采样()
        {
            _cfg.WebServerPassword = "s3cret";

            int samplingCalls = 0;
            var manager = NewManager(
                samplingRequester: ct => { Interlocked.Increment(ref samplingCalls); return Task.FromResult(false); });
            manager.Start();

            using var host = new LoopbackHost(manager);
            string resp = await SendHttpAsync(host.Port, "/api/snapshot");

            Assert.Contains("401", resp);
            Assert.False(manager.HasWebDataDemand);
            Assert.False(manager.HasHttpLease);
            Assert.Equal(0, manager.AuthenticatedConnectionCount);
            Assert.Equal(0, samplingCalls);   // 未鉴权请求连采样都不该触发
        }

        [Fact]
        public async Task 仅取HTML或favicon不产生需求也不触发采样()
        {
            int samplingCalls = 0;
            var manager = NewManager(
                samplingRequester: ct => { Interlocked.Increment(ref samplingCalls); return Task.FromResult(false); });
            manager.Start();

            using var host = new LoopbackHost(manager);
            string html = await SendHttpAsync(host.Port, "/index.html");
            string icon = await SendHttpAsync(host.Port, "/favicon.ico");

            Assert.Contains("200", html);
            Assert.False(manager.HasWebDataDemand);
            Assert.Equal(0, samplingCalls);
        }

        // ================================================================
        // 2. 共享采样（HTTP 快照 + 广播 共用同一条限频路径）
        // ================================================================

        [Fact]
        public async Task 两种消费者共用采样_并发HTTP与网页不叠加真实采集()
        {
            // ★ 用**真实的** HardwareSamplingCoordinator（限频 + 并发合并的唯一实现），
            //   只把"真实硬件"换成计数替身 —— 断言的是实际采集轮数。
            var fakeTime = new FakeTimeProvider();
            int collections = 0;
            int serializations = 0;

            var coordinator = new HardwareSamplingCoordinator(
                periodProvider: () => 1000,
                sampler: async ct => { Interlocked.Increment(ref collections); await Task.Yield(); },
                timeProvider: fakeTime,
                errorLogger: (source, ex) => { });   // 不写错误日志文件（避免测试输出目录出现副作用文件）
            coordinator.SetReady(true);

            var manager = NewManager(
                demandState: new WebDemandState(fakeTime),
                samplingRequester: ct => coordinator.RequestCollectionAsync(ct),
                syncProvider: writer =>
                {
                    Interlocked.Increment(ref serializations);
                    writer.WriteStartObject();
                    writer.WriteString("k", "test");
                    writer.WriteEndObject();
                });
            manager.Start();

            using var host = new LoopbackHost(manager);

            // (1) 8 个并发的 HTTP 快照请求
            var httpTasks = new Task<string>[8];
            for (int i = 0; i < httpTasks.Length; i++)
            {
                httpTasks[i] = SendHttpAsync(host.Port, "/api/snapshot");
            }
            string[] responses = await Task.WhenAll(httpTasks);
            foreach (var r in responses) Assert.Contains("200", r);

            Assert.Equal(1, collections);   // ★ 8 个并发消费者 → 只发生 1 次真实采集

            // (2) 再加上多个网页（WebSocket 广播路径）—— 同一条采样入口
            var clients = new List<TcpClient>();
            try
            {
                for (int i = 0; i < 3; i++) clients.Add(await OpenWebSocketAsync(host.Port));
                Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 3, 5000));

                // 等广播真的发生（序列化次数超过 HTTP 请求的 8 次）
                Assert.True(await WaitUntilAsync(() => Volatile.Read(ref serializations) > 8, 8000),
                    "广播路径应当也走过序列化（否则本用例的后半段是空转）");

                // 再来一轮并发 HTTP 轮询
                var more = new Task<string>[6];
                for (int i = 0; i < more.Length; i++) more[i] = SendHttpAsync(host.Port, "/api/snapshot");
                await Task.WhenAll(more);

                // ★ 真实采集仍然只有 1 次（协调器限频生效）
                Assert.Equal(1, collections);
            }
            finally
            {
                foreach (var c in clients) { try { c.Close(); } catch { } }
                manager.Stop();
            }
        }

        [Fact]
        public async Task 快照每份只请求一次共享采样且序列化不触发采集()
        {
            int samplingCalls = 0;
            int serializations = 0;

            var manager = NewManager(
                samplingRequester: ct => { Interlocked.Increment(ref samplingCalls); return Task.FromResult(true); },
                syncProvider: writer =>
                {
                    Interlocked.Increment(ref serializations);
                    writer.WriteStartObject();
                    writer.WriteString("k", "test");
                    writer.WriteEndObject();
                });
            manager.Start();

            using var host = new LoopbackHost(manager);

            for (int i = 0; i < 3; i++)
            {
                string resp = await SendHttpAsync(host.Port, "/api/snapshot");
                Assert.Contains("200", resp);
            }

            Assert.Equal(3, samplingCalls);     // 每份快照请求一次共享采样
            Assert.Equal(3, serializations);    // 序列化次数与快照数一致（序列化本身不再触发采集）
        }

        [Fact]
        public async Task 广播载荷超过32KB时不会丢帧()
        {
            // 回归：原来的 new MemoryStream(buffer) 是**不可扩容**的固定缓冲，
            // 载荷超过 32 KB 会抛 NotSupportedException，整帧丢失。
            var big = new string('y', 100 * 1024);
            var manager = NewManager(syncProvider: writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("d", big);
                writer.WriteEndObject();
            });
            manager.Start();

            using var host = new LoopbackHost(manager);
            using var client = await OpenWebSocketAsync(host.Port);

            var payload = await ReadFrameAsync(client);

            Assert.True(payload.Length > 32 * 1024, "应当收到超过 32 KB 的帧，实际 " + payload.Length);
            using var doc = JsonDocument.Parse(payload);
            Assert.Equal(big, doc.RootElement.GetProperty("d").GetString());

            manager.Stop();
        }

        [Fact]
        public void 快照路径不执行硬件全量更新()
        {
            // ★ 源码级不变量（HardwareMonitor 构造函数会 Computer.Open()，无法在单测里实例化，
            //   因此用源码扫描把"快照路径不触发 hw.Update()"锁死）。
            string[] files =
            {
                Path.Combine("src", "System", "WebServer", "LiteWebServer.cs"),
                Path.Combine("src", "System", "WebServer", "WebSessionManager.cs")
            };

            foreach (var rel in files)
            {
                string path = FindRepoFile(rel);
                Assert.True(path != null && File.Exists(path), "找不到源文件（仓库根定位失败）: " + rel);
                string code = StripComments(File.ReadAllText(path));

                Assert.False(code.Contains("UpdateAll("), rel + " 不得调用 UpdateAll（全量采集入口）");
                Assert.False(code.Contains(".Update("), rel + " 不得直接调用硬件的 .Update()");
                Assert.False(code.Contains("hw.Update("), rel + " 不得直接调用硬件的 hw.Update()");
            }
        }

        // ================================================================
        // 3. FPS 需求与网页占位
        // ================================================================

        [Fact]
        public void 无FPS需求时不读取FPS数值()
        {
            // ★ "不读数值"就是"不触发外部 FPS 服务惰性启动"的实现方式。
            int readCount = 0;
            var state = WebSnapshotValues.ResolveValue("FPS", fpsDemand: false, hasFpsData: true,
                readValue: k => { readCount++; return 58f; }, out string value, out float? raw);

            Assert.Equal(WebSnapshotValueState.NotEnabled, state);
            Assert.Equal("--", value);
            Assert.Null(raw);
            Assert.Equal(0, readCount);   // ★ readValue 一次都没被调用
        }

        [Fact]
        public void 有FPS需求但尚无可信数据时输出占位而不是0()
        {
            // 简报 §3 用例 16：HasFpsData == false → "--"，**不是** 0。
            int readCount = 0;
            var state = WebSnapshotValues.ResolveValue("FPS", fpsDemand: true, hasFpsData: false,
                readValue: k => { readCount++; return 0f; }, out string value, out float? raw);

            Assert.Equal(WebSnapshotValueState.NotEnabled, state);
            Assert.Equal("--", value);
            Assert.Null(raw);
            Assert.Equal(0, readCount);   // 没有可信数据 → 连读都不读
        }

        [Fact]
        public void 有可信FPS数据时真实的0照常输出()
        {
            // "真 0 FPS"必须能显示 0 —— 这正是需要 HasFpsData 区分的原因。
            var state = WebSnapshotValues.ResolveValue("FPS", fpsDemand: true, hasFpsData: true,
                readValue: k => 0f, out string value, out float? raw);

            Assert.Equal(WebSnapshotValueState.HasData, state);
            Assert.Equal("0", value);
            Assert.Equal(0f, raw);
        }

        [Fact]
        public void 有可信FPS数据时输出真实数值()
        {
            var state = WebSnapshotValues.ResolveValue("FPS", fpsDemand: true, hasFpsData: true,
                readValue: k => 58.4f, out string value, out float? raw);

            Assert.Equal(WebSnapshotValueState.HasData, state);
            Assert.Equal("58", value);
            Assert.Equal(58.4f, raw);
        }

        [Fact]
        public void 普通指标不受FPS占位规则影响()
        {
            var state = WebSnapshotValues.ResolveValue("CPU.Load", fpsDemand: false, hasFpsData: false,
                readValue: k => 42f, out string value, out float? raw);

            Assert.Equal(WebSnapshotValueState.HasData, state);
            Assert.Contains("42", value);
            Assert.Equal(42f, raw);
        }

        [Fact]
        public void 旧设置JSON缺少WebFpsEnabled时默认为false()
        {
            const string legacy = "{\"RefreshMs\":1000,\"WebServerEnabled\":true,\"WebServerPort\":5000,\"WebServerPassword\":\"\"}";
            var cfg = JsonSerializer.Deserialize<Settings>(legacy);
            Assert.NotNull(cfg);
            Assert.False(cfg.WebFpsEnabled);
            Assert.False(WebFpsDemand.IsWebFpsDemand(cfg, hasWebDataDemand: true));
        }

        [Fact]
        public void 本地启用FPS时不受Web开关影响()
        {
            var cfg = new Settings();
            cfg.MonitorItems.Add(new MonitorItemConfig { Key = "FPS", VisibleInPanel = true });
            cfg.WebFpsEnabled = false;

            // 本地有需求 → 有需求（网页开关关掉也不影响本地）
            Assert.True(WebFpsDemand.IsLocalFpsDemand(cfg));
            Assert.True(WebFpsDemand.IsFpsDemand(cfg, hasWebDataDemand: false));

            // 网页侧判定用的是同一个函数
            var state = WebSnapshotValues.ResolveValue("FPS",
                fpsDemand: WebFpsDemand.IsFpsDemand(cfg, hasWebDataDemand: false),
                hasFpsData: true,
                readValue: k => 30f, out string value, out float? raw);
            Assert.Equal(WebSnapshotValueState.HasData, state);
            Assert.Equal("30", value);
        }

        [Fact]
        public void Web单独启用FPS时仅在有Web需求时成立()
        {
            var cfg = new Settings();
            cfg.MonitorItems.Add(new MonitorItemConfig { Key = "CPU.Load", VisibleInPanel = true });
            cfg.WebFpsEnabled = true;

            Assert.False(WebFpsDemand.IsLocalFpsDemand(cfg));
            Assert.True(WebFpsDemand.IsWebFpsDemand(cfg, hasWebDataDemand: true));
            Assert.True(WebFpsDemand.IsFpsDemand(cfg, hasWebDataDemand: true));

            // 没有 Web 需求 → 不成立（网页端输出占位，也不会触发外部服务启动）
            Assert.False(WebFpsDemand.IsFpsDemand(cfg, hasWebDataDemand: false));
            var state = WebSnapshotValues.ResolveValue("FPS",
                fpsDemand: WebFpsDemand.IsFpsDemand(cfg, hasWebDataDemand: false),
                hasFpsData: true,
                readValue: k => 30f, out string value, out float? raw);
            Assert.Equal(WebSnapshotValueState.NotEnabled, state);
            Assert.Equal("--", value);
        }

        [Fact]
        public void WebFpsEnabled开关改动立即生效_不需要重启Web服务()
        {
            var cfg = new Settings();
            cfg.WebFpsEnabled = false;
            Assert.False(WebFpsDemand.IsWebFpsDemand(cfg, hasWebDataDemand: true));

            cfg.WebFpsEnabled = true;   // 用户在设置页打开
            Assert.True(WebFpsDemand.IsWebFpsDemand(cfg, hasWebDataDemand: true));

            cfg.WebFpsEnabled = false;  // 用户再关掉
            Assert.False(WebFpsDemand.IsWebFpsDemand(cfg, hasWebDataDemand: true));
        }

        [Fact]
        public async Task Web服务重启后需求归零且FPS需求按新配置判定()
        {
            // 重启不应让旧的 FPS 服务状态泄漏：需求归零 → 网页 FPS 需求随之归零。
            var fakeTime = new FakeTimeProvider();
            var demand = new WebDemandState(fakeTime);
            var manager = NewManager(demandState: demand);
            manager.Start();

            var sink = new RecordingDemandSink();
            var sync = new WebDemandSynchronizer(() => manager.HasWebDataDemand, sink.Apply);
            manager.DemandChanged = () => sync.SyncNow();

            using var host = new LoopbackHost(manager);
            using var client = await OpenWebSocketAsync(host.Port);
            Assert.True(await WaitUntilAsync(() => sink.Last, 5000));

            _cfg.WebFpsEnabled = true;
            Assert.True(WebFpsDemand.IsFpsDemand(_cfg, hasWebDataDemand: true));

            manager.Stop();
            Assert.False(manager.HasWebDataDemand);
            Assert.False(WebFpsDemand.IsFpsDemand(_cfg, hasWebDataDemand: manager.HasWebDataDemand));

            manager.Start();
            Assert.False(manager.HasWebDataDemand);   // 重启后不残留旧需求
        }

        // ================================================================
        // 4. Web 需求 → 采集范围（实际更新计数）
        // ================================================================

        [Fact]
        public void Web开关开着但无人连接时不扩大采集范围()
        {
            // 用例 1：Web 服务开着、但**没有合法需求** → 采集范围回到本地需求，不 ForceAll。
            var cfg = new Settings();
            cfg.MonitorItems.Add(new MonitorItemConfig { Key = "CPU.Load", VisibleInPanel = true });
            cfg.WebServerEnabled = true;   // 开关是开的

            var withoutDemand = HardwareUpdateRequirements.Compute(cfg, hasWebDataDemand: false, usePerformanceCounters: true);
            Assert.False(withoutDemand.ForceAll);
            Assert.True(withoutDemand.NeedCpu);    // 本地需求仍然成立
            Assert.False(withoutDemand.NeedBat);   // 本地没开的项不因为"Web 开关开着"而被打开

            // 有合法需求 → 才按全量普通指标更新
            var withDemand = HardwareUpdateRequirements.Compute(cfg, hasWebDataDemand: true, usePerformanceCounters: true);
            Assert.True(withDemand.ForceAll);
            Assert.True(withDemand.NeedBat);
        }

        [Fact]
        public void Web需求下单卡只更新一张卡_双卡配置更新两张卡()
        {
            // 用例 8：Web 全量需求**不绕过**显卡选择 —— 实际更新卡数由显卡配置决定。
            var gpu0 = new FakeGpuHardware("NVIDIA RTX 3080");
            var gpu1 = new FakeGpuHardware("NVIDIA RTX 3080 Ti");

            var cfg = new Settings();
            cfg.MonitorItems.Add(new MonitorItemConfig { Key = "GPU.0.Load", VisibleInPanel = true });

            // 有 Web 需求 → 显卡这一类确实需要更新
            var req = HardwareUpdateRequirements.Compute(cfg, hasWebDataDemand: true, usePerformanceCounters: true);
            Assert.True(req.ForceAll);
            Assert.True(req.NeedGpu);

            // 单卡配置：只有被监控的那张卡被更新
            var monitoredOne = new List<IHardware> { gpu0 };
            int updatedOne = 0;
            if (GpuUpdateSelection.ShouldUpdate(gpu0, gpu0, monitoredOne)) updatedOne++;
            if (GpuUpdateSelection.ShouldUpdate(gpu1, gpu0, monitoredOne)) updatedOne++;
            Assert.Equal(1, updatedOne);

            // 双卡配置：两张卡都被更新
            var monitoredTwo = new List<IHardware> { gpu0, gpu1 };
            int updatedTwo = 0;
            if (GpuUpdateSelection.ShouldUpdate(gpu0, gpu0, monitoredTwo)) updatedTwo++;
            if (GpuUpdateSelection.ShouldUpdate(gpu1, gpu0, monitoredTwo)) updatedTwo++;
            Assert.Equal(2, updatedTwo);
        }
    }
}
