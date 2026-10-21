using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        [Fact]
        public async Task StopAtHttpGrantBoundaryDoesNotRestoreDemand()
        {
            var fakeTime = new FakeTimeProvider();
            var demand = new WebDemandState(fakeTime, TimeSpan.FromSeconds(2));
            var manager = NewManager(
                demandState: demand,
                samplingRequester: _ => Task.FromResult(false));
            var sink = new RecordingDemandSink();
            var sync = new WebDemandSynchronizer(() => manager.HasWebDataDemand, sink.Apply);
            manager.DemandChanged = () => sync.SyncNow();

            var atGrantBoundary = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseGrant = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            // Pause after the running/generation checks while holding clientsGate.
            manager.HttpLeaseGrantTestHook = () =>
            {
                atGrantBoundary.TrySetResult(true);
                releaseGrant.Task.GetAwaiter().GetResult();
            };

            manager.Start();
            using var host = new LoopbackHost(manager);
            Task<string> pendingResponse = SendHttpAsync(host.Port, "/api/snapshot");

            try
            {
                await atGrantBoundary.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var stopAttempted = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Task stopTask = Task.Run(() =>
                {
                    stopAttempted.TrySetResult(true);
                    manager.Stop();
                });
                await stopAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(stopTask.IsCompleted);

                releaseGrant.TrySetResult(true);
                await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.False(manager.HasHttpLease);
                Assert.False(manager.HasWebDataDemand);
                Assert.True(sink.Count > 0);
                Assert.False(sink.Last);

                string response = await pendingResponse.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Contains("200", response);
                Assert.False(manager.HasHttpLease);
                Assert.False(manager.HasWebDataDemand);
                Assert.False(sink.Last);

                fakeTime.Advance(TimeSpan.FromSeconds(3));
                sync.SyncNow();
                Assert.False(manager.HasHttpLease);
                Assert.False(manager.HasWebDataDemand);
                Assert.False(sink.Last);
            }
            finally
            {
                releaseGrant.TrySetResult(true);
                manager.Stop();
            }
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

        /// <summary>
        /// 从（已去注释的）源码里截取某个**方法声明**的方法体（大括号配对，跳过字符串/字符字面量）。
        /// </summary>
        /// <remarks>
        /// 只认"方法声明"（名字后面紧跟 '('、且不是成员访问），因此不会命中同名调用点
        /// （例如构造函数体里的 <c>?? ApplyWebDataDemandToHardware</c> 不会被当成方法体）。
        /// </remarks>
        private static string ExtractMethodBody(string code, string methodName)
        {
            int search = 0;
            while (search <= code.Length)
            {
                int idx = code.IndexOf(methodName, search, StringComparison.Ordinal);
                if (idx < 0) return null;

                // 跳过成员访问（xxx.MethodName）与标识符的一部分（MyMethodName）
                if (idx > 0 && (code[idx - 1] == '.' || char.IsLetterOrDigit(code[idx - 1]) || code[idx - 1] == '_'))
                {
                    search = idx + methodName.Length;
                    continue;
                }

                // 必须是声明：方法名后面紧跟 '('
                int p = idx + methodName.Length;
                while (p < code.Length && char.IsWhiteSpace(code[p])) p++;
                if (p >= code.Length || code[p] != '(')
                {
                    search = idx + methodName.Length;
                    continue;
                }

                int close = FindMatchingParen(code, p);
                if (close < 0) return null;
                int open = code.IndexOf('{', close);
                if (open < 0) return null;
                return ScanBracedBlock(code, open);
            }
            return null;
        }

        /// <summary>找到与 openIndex 处 '(' 配对的 ')'（跳过字符串/字符字面量）。</summary>
        private static int FindMatchingParen(string code, int openIndex)
        {
            int depth = 0;
            for (int i = openIndex; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    i++;
                    while (i < code.Length && code[i] != quote)
                    {
                        if (code[i] == '\\') i++;
                        i++;
                    }
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>截取大括号块的内容（跳过字符串/字符字面量）。</summary>
        private static string ScanBracedBlock(string code, int openIndex)
        {
            int depth = 0;
            for (int i = openIndex; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    i++;
                    while (i < code.Length && code[i] != quote)
                    {
                        if (code[i] == '\\') i++;
                        i++;
                    }
                    continue;
                }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return code.Substring(openIndex + 1, i - openIndex - 1);
                }
            }
            return null;
        }

        /// <summary>
        /// 找到第 n 个（0 基）形如 "Name(" 的**声明**位置（跳过成员访问与标识符的一部分）。
        /// </summary>
        /// <remarks>
        /// 用于区分同一个类里的**多个构造函数**：例如 <c>LiteWebServer</c> 的
        /// 第 0 个声明是 1 参 ctor，第 1 个是 3 参 ctor
        /// （类声明 <c>class LiteWebServer : IDisposable</c> 后面不是 '('，会被跳过）。
        /// </remarks>
        private static int FindNthDeclaration(string code, string name, int n)
        {
            int search = 0;
            int count = 0;
            while (search <= code.Length)
            {
                int idx = code.IndexOf(name, search, StringComparison.Ordinal);
                if (idx < 0) return -1;

                if (idx > 0 && (code[idx - 1] == '.' || char.IsLetterOrDigit(code[idx - 1]) || code[idx - 1] == '_'))
                {
                    search = idx + name.Length;
                    continue;
                }

                int p = idx + name.Length;
                while (p < code.Length && char.IsWhiteSpace(code[p])) p++;
                if (p >= code.Length || code[p] != '(')
                {
                    search = idx + name.Length;
                    continue;
                }

                if (count == n) return idx;
                count++;
                search = idx + name.Length;
            }
            return -1;
        }

        /// <summary>
        /// 取某个构造函数的**初始化器**（参数列表右括号之后、方法体左括号之前的那段文本）。
        /// </summary>
        /// <remarks>
        /// 例如 <c>public LiteWebServer(Settings cfg) : this(cfg, A, B) { }</c> 的初始化器是
        /// <c>: this(cfg, A, B)</c>。构造器初始化器**不在**方法体里，必须单独取。
        /// </remarks>
        private static string ExtractConstructorInitializer(string code, int declIndex)
        {
            if (declIndex < 0) return null;
            int open = code.IndexOf('(', declIndex);
            if (open < 0) return null;
            int close = FindMatchingParen(code, open);
            if (close < 0) return null;
            int bodyOpen = code.IndexOf('{', close);
            if (bodyOpen < 0) return null;
            return code.Substring(close + 1, bodyOpen - close - 1);
        }

        /// <summary>
        /// 取某个构造函数的**方法体**（大括号配对，跳过字符串/字符字面量）。
        /// </summary>
        private static string ExtractConstructorBody(string code, int declIndex)
        {
            if (declIndex < 0) return null;
            int open = code.IndexOf('(', declIndex);
            if (open < 0) return null;
            int close = FindMatchingParen(code, open);
            if (close < 0) return null;
            int bodyOpen = code.IndexOf('{', close);
            if (bodyOpen < 0) return null;
            return ScanBracedBlock(code, bodyOpen);
        }

        /// <summary>
        /// 截取方法体里**最外层**的 catch 块（深度 0 的那个 catch 关键字起，到方法体结束）。
        /// </summary>
        /// <remarks>
        /// 不能用"最后一个 catch"：方法体里还有 <c>try { ... } catch { }</c> 这种内联语句，
        /// 它们的 catch 关键字位置更靠后，会把真正的外层 catch 盖掉。
        /// </remarks>
        private static string ExtractOuterCatchBlock(string body)
        {
            if (body == null) return null;
            int depth = 0;
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    i++;
                    while (i < body.Length && body[i] != quote)
                    {
                        if (body[i] == '\\') i++;
                        i++;
                    }
                    continue;
                }
                if (c == '{') { depth++; continue; }
                if (c == '}') { depth--; continue; }
                if (depth == 0 && body.AsSpan(i).StartsWith("catch"))
                {
                    return body.Substring(i);
                }
            }
            return null;
        }

        /// <summary>取一个当前空闲的回环端口（避免与真实实例 / 其它测试抢端口）。</summary>
        private static int FindFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        /// <summary>
        /// 测试专用强口令（每次运行随机生成）。
        /// </summary>
        /// <remarks>
        /// ★ 不用仓库里写死的弱口令：即使监听地址被误改成"所有网卡"，
        /// 也不该有一个人人可猜的口令留在源码里。
        /// 只用十六进制字符 → 可以直接拼进 URL 查询串，不需要 URL 编码。
        /// </remarks>
        private static string NewStrongPassword() => Guid.NewGuid().ToString("N");

        /// <summary>
        /// 让被测服务器**只监听回环地址**（F7 卫生：测试期间不在局域网上暴露服务）。
        /// </summary>
        /// <remarks>
        /// <c>LiteWebServer.Start()</c> 硬编码监听 <c>IPAddress.IPv6Any</c>（所有网卡），
        /// 生产代码提供了一个 <c>internal</c> 接缝 <c>ListenAddress</c>；
        /// 测试通过反射设置它（internal 成员对测试程序集不可见）。
        /// 接缝找不到时**响亮失败**，不能静默退回"监听所有网卡"。
        /// </remarks>
        private static void UseLoopbackListener(LiteWebServer server)
        {
            var prop = typeof(LiteWebServer).GetProperty("ListenAddress",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(prop != null,
                "LiteWebServer 必须提供 internal ListenAddress 测试接缝（否则测试会监听所有网卡）");
            var setter = prop.GetSetMethod(nonPublic: true);
            Assert.True(setter != null, "LiteWebServer.ListenAddress 必须可写");
            setter.Invoke(server, new object[] { IPAddress.Loopback });
        }

        /// <summary>
        /// 复位 <c>LiteWebServer.Instance</c>（生产代码本来就不复位，测试里显式复位）。
        /// </summary>
        /// <remarks>
        /// ★ 生产代码**不能**在 Stop()/Dispose() 里复位：<c>MainForm_Transparent</c> 正是靠
        /// <c>Instance</c> 反复 Start/Stop，复位会让它再也起不来。
        /// 但测试构造完服务器后必须复位，否则静态字段里会留着一个已 Dispose 的实例。
        /// </remarks>
        private static void ResetLiteWebServerInstance()
        {
            var prop = typeof(LiteWebServer).GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static);
            var setter = prop?.GetSetMethod(nonPublic: true);
            setter?.Invoke(null, new object[] { null });
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

            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 2, 5000),
                "两个连接都应被登记为有效需求");
            Assert.True(await WaitUntilAsync(() => sink.Last, 5000),
                "有需求后必须把 true 送达采集层");

            c1.Close();
            Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 1, 5000),
                "断开一个连接后计数应减到 1");
            Assert.True(manager.HasWebDataDemand, "断开一个后仍有需求");
            Assert.True(sink.Last, "需求仍为真，不应被回收");

            c2.Close();
            Assert.True(await WaitUntilAsync(() => !manager.HasWebDataDemand, 5000),
                "最后一个连接断开后需求必须被回收");
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
            Assert.True(await WaitUntilAsync(() => sink.Last, 5000),
                "连接建立后需求应被送达采集层");

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
                Assert.True(await WaitUntilAsync(() => manager.AuthenticatedConnectionCount == 3, 5000),
                    "三个网页连接都应被登记为有效需求");

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
            // ★ 等价全量入口必须**一并**纳入黑名单（次要#8）：
            //   RequestCollection() / RequestCollectionAsync() 与 UpdateAll() 走同一条采集通道，
            //   漏掉它们就能照样触发全量采集而不让本用例变红。
            //   注意：UpdateIfDueAsync( 是**允许**的 —— 它就是共享限频采样的唯一入口。
            //   行为侧的对应护栏见「两种消费者共用采样_并发HTTP与网页不叠加真实采集」
            //   （用真实的 HardwareSamplingCoordinator 统计真实采集轮数）。
            string[] files =
            {
                Path.Combine("src", "System", "WebServer", "LiteWebServer.cs"),
                Path.Combine("src", "System", "WebServer", "WebSessionManager.cs")
            };

            string[] forbidden =
            {
                "UpdateAll(",        // 全量采集入口
                ".Update(",          // 直接调用硬件的 Update()
                "hw.Update(",        // 直接调用硬件的 hw.Update()
                "RequestCollection"  // 等价全量入口：RequestCollection() / RequestCollectionAsync()
            };

            foreach (var rel in files)
            {
                string path = FindRepoFile(rel);
                Assert.True(path != null && File.Exists(path), "找不到源文件（仓库根定位失败）: " + rel);
                string code = StripComments(File.ReadAllText(path));

                foreach (var token in forbidden)
                {
                    Assert.False(code.Contains(token), rel + " 不得调用 " + token + "（等价全量采集入口）");
                }
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
            Assert.True(await WaitUntilAsync(() => sink.Last, 5000),
                "连接建立后需求应被送达采集层");

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

        // ================================================================
        // 5. 生产接线回归护栏（B2 修复轮 2）
        //    覆盖复审"重要#1"：生产默认需求传播路径此前**零回归覆盖** ——
        //    把默认 ApplyWebDataDemandToHardware 改成 no-op，27/27 仍然全绿。
        // ================================================================

        [Fact]
        public void LiteWebServer保留1参构造函数_二进制兼容()
        {
            // ★ 次要#3 的护栏：1 参 ctor 的**元数据签名**不得消失
            //   （源码兼容 ✓、二进制不兼容 ✘ —— 简报要求"只许新增、不许改既有成员签名"）。
            //   既有 3 个调用点（MainForm_Transparent / FpsStateProbe / ProbeEnvironment）
            //   写的是 new LiteWebServer(cfg) → 必须仍然存在并保持 public。
            var oneArg = typeof(LiteWebServer).GetConstructor(new[] { typeof(Settings) });
            Assert.True(oneArg != null && oneArg.IsPublic,
                "LiteWebServer 必须保留 public LiteWebServer(Settings) 构造函数（既有 1 参 ctor 不得被改签名）");

            // 新增的重载也必须还在（"保留 + 新增"形态）
            //   注意 typeof(...) 是表达式上下文，">>"会被词法分析成移位运算符 → 泛型闭合要留空格
            var threeArg = typeof(LiteWebServer).GetConstructor(new[]
            {
                typeof(Settings),
                typeof(Func<CancellationToken, Task<bool> >),
                typeof(Func<bool, bool>)
            });
            Assert.True(threeArg != null && threeArg.IsPublic,
                "LiteWebServer 必须保留新增的 3 参构造函数（可注入采样入口与需求写入端）");
        }

        [Fact]
        public void 单参构造函数显式转发到生产默认实现()
        {
            // ★ F1 的护栏（M1c 变异必须让本用例变红）。
            //   变异：把 1 参 ctor 的委派改成 this(cfg, null, (bool __h) => true)
            //        → 之前 34/34 全绿（"生产默认需求传播路径"零覆盖）。
            //   ★ 只加断言**不**改委派是不够的（M1c 仍然全绿）；
            //     只改委派**不**加断言也一样危险（谁改回 no-op 都没人发现）→ 两者都要。
            //   生产代码已改成显式转发：public LiteWebServer(Settings cfg)
            //       : this(cfg, DefaultSamplingRequester, ApplyWebDataDemandToHardware)
            //   （行为等价：与"传 null 再走 ??"最终得到的委托目标完全相同 → 零行为变化）
            string path = FindRepoFile(Path.Combine("src", "System", "WebServer", "LiteWebServer.cs"));
            Assert.True(path != null && File.Exists(path), "找不到 LiteWebServer.cs（仓库根定位失败）");
            string code = StripComments(File.ReadAllText(path));

            // 第 0 个 "LiteWebServer(" 声明 = 1 参 ctor（类声明后面不是 '('，会被跳过）
            int oneArgIdx = FindNthDeclaration(code, "LiteWebServer", 0);
            Assert.True(oneArgIdx >= 0, "LiteWebServer.cs 里找不到 1 参构造函数声明");
            string init = ExtractConstructorInitializer(code, oneArgIdx);
            Assert.True(init != null, "取不到 1 参构造函数的初始化器（构造器初始化器不在方法体里）");

            // ★ 初始化器必须**同时**显式转发两个生产默认实现
            Assert.True(init.Contains("DefaultSamplingRequester"),
                "1 参构造函数必须显式转发默认采样入口 DefaultSamplingRequester（否则生产路径无覆盖）");
            Assert.True(init.Contains("ApplyWebDataDemandToHardware"),
                "1 参构造函数必须显式转发默认需求写入 ApplyWebDataDemandToHardware（否则 Web 需求永远到不了采集层）");

            // 3 参 ctor 的 ?? 兜底必须保留（直接传 null 的调用方仍要拿到默认实现）
            int threeArgIdx = FindNthDeclaration(code, "LiteWebServer", 1);
            Assert.True(threeArgIdx >= 0, "LiteWebServer.cs 里找不到 3 参构造函数声明");
            string threeArgBody = ExtractConstructorBody(code, threeArgIdx);
            Assert.True(threeArgBody != null, "取不到 3 参构造函数的方法体");
            Assert.True(threeArgBody.Contains("?? ApplyWebDataDemandToHardware"),
                "3 参构造函数必须保留 ?? ApplyWebDataDemandToHardware 兜底");
            Assert.True(threeArgBody.Contains("?? DefaultSamplingRequester"),
                "3 参构造函数必须保留 ?? DefaultSamplingRequester 兜底");
        }

        [Fact]
        public void Start方法启动需求监视()
        {
            // ★ F2① 的护栏（M9 / M15 变异必须让本用例变红）：
            //   变异：删掉 Start() 里的 _demandSync.Start();  → 之前 34/34 全绿。
            //   200ms 周期监视是**唯一**能发现 HTTP 租期到期的机制
            //   （租期到期不触发任何事件；DemandChanged 只在连接增删 / 租期授予时触发）
            //   → 删掉它 = 采集层需求**永久停在 true** = 永久白采集（不崩不红无日志）。
            // ★ 断言必须精确到 "_demandSync.Start()"：
            //   Start() 里同时有 _demandSync.Start(); 与 _demandSync.SyncNow();
            //   用更宽松的匹配会把两者一起匹配上 → M9 变异仍可能全绿。
            string path = FindRepoFile(Path.Combine("src", "System", "WebServer", "LiteWebServer.cs"));
            Assert.True(path != null && File.Exists(path), "找不到 LiteWebServer.cs（仓库根定位失败）");
            string code = StripComments(File.ReadAllText(path));

            string startBody = ExtractMethodBody(code, "Start");
            Assert.True(startBody != null, "LiteWebServer.cs 里找不到 Start 方法");
            Assert.True(startBody.Contains("_demandSync.Start()"),
                "Start() 必须启动需求监视 _demandSync.Start()（HTTP 租期到期只能靠它发现）");
        }

        [Fact]
        public void 构造函数挂上需求变化钩子()
        {
            // ★ F3 的护栏（M10 变异必须让本用例变红）：
            //   变异：删掉构造函数里的 _sessionManager.DemandChanged = () => _demandSync.SyncNow();
            //   ★ 两条需求送达机制**互为掩蔽**：
            //     事件驱动（DemandChanged 钩子）与周期监视（_demandSync.Start()）
            //     之前只有"两者至少有一个活着"的护栏（M10+M15 同删才红），
            //     各自单删都全绿 → 必须**各自独立**地锁住。
            string path = FindRepoFile(Path.Combine("src", "System", "WebServer", "LiteWebServer.cs"));
            Assert.True(path != null && File.Exists(path), "找不到 LiteWebServer.cs（仓库根定位失败）");
            string code = StripComments(File.ReadAllText(path));

            // 第 1 个 "LiteWebServer(" 声明 = 3 参 ctor（钩子挂在这里）
            int threeArgIdx = FindNthDeclaration(code, "LiteWebServer", 1);
            Assert.True(threeArgIdx >= 0, "LiteWebServer.cs 里找不到 3 参构造函数声明");
            string body = ExtractConstructorBody(code, threeArgIdx);
            Assert.True(body != null, "取不到 3 参构造函数的方法体");
            Assert.True(body.Contains("_sessionManager.DemandChanged ="),
                "构造函数必须挂上需求变化钩子 _sessionManager.DemandChanged（连接增删 / 租期授予要立刻同步）");
        }

        [Fact]
        public void 生产默认需求写入路径真的调用SetWebDataDemand()
        {
            // ★ 复审重要#1 的护栏（M1 变异必须让本用例变红）。
            //   变异：把默认 ApplyWebDataDemandToHardware 改成 no-op
            //        （注释掉 hw.SetWebDataDemand(hasDemand);）→ 本用例必须红。
            //   为什么用源码级不变量：HardwareMonitor 的构造函数会 Computer.Open()（会碰驱动），
            //   单测里无法实例化它 → 默认实现无法在运行时被直接调用（只能用源码扫描锁死）。
            string path = FindRepoFile(Path.Combine("src", "System", "WebServer", "LiteWebServer.cs"));
            Assert.True(path != null && File.Exists(path), "找不到 LiteWebServer.cs（仓库根定位失败）");
            string code = StripComments(File.ReadAllText(path));

            // ① 默认需求写入实现必须真的把需求写进采集层
            string applier = ExtractMethodBody(code, "ApplyWebDataDemandToHardware");
            Assert.True(applier != null,
                "LiteWebServer.cs 里找不到默认需求写入实现 ApplyWebDataDemandToHardware");
            Assert.True(applier.Contains("SetWebDataDemand("),
                "默认需求写入实现必须调用 SetWebDataDemand（否则 Web 需求永远不会送达采集层）");

            // ② 构造函数必须把默认实现接成兜底（注入为空时用默认实现，不能换成 no-op）
            Assert.True(code.Contains("?? ApplyWebDataDemandToHardware"),
                "构造函数必须把 ApplyWebDataDemandToHardware 作为 demandApplier 的默认实现");
            Assert.True(code.Contains("?? DefaultSamplingRequester"),
                "构造函数必须把 DefaultSamplingRequester 作为 samplingRequester 的默认实现");

            // ③ 默认采样入口必须走共享限频采样（不是各自为政的全量采集）
            string requester = ExtractMethodBody(code, "DefaultSamplingRequester");
            Assert.True(requester != null,
                "LiteWebServer.cs 里找不到默认采样入口 DefaultSamplingRequester");
            Assert.True(requester.Contains("UpdateIfDueAsync("),
                "默认采样入口必须走 HardwareMonitor.UpdateIfDueAsync（共享限频采样）");

            // ④ ★ F6：语义必须正确 —— 把"是否有需求"**原样**写进采集层。
            //   之前的护栏只查 token 存在性，变异 M11（SetWebDataDemand(!hasDemand) 语义反转）
            //   仍然 34/34 全绿。这里查的是**实参**，不是 API 名字。
            //   为什么必须是源码级：SetWebDataDemand 全工程只有 LiteWebServer.cs:122 一个调用点，
            //   它在**默认** applier 里，而默认 applier 的目标是 HardwareMonitor.Instance ——
            //   那个类是 sealed、构造函数会建 LHM Computer（碰驱动）、静态 Instance 是私有 setter，
            //   单测里**无法实例化也无法注入** → 默认 applier 的运行时取值在单测中根本观测不到。
            //   （行为侧的"applier 收到的值"由 LiteWebServer生产接线把Web需求送达采集层 覆盖，
            //     两者互补：行为级盯"接线"，源码级盯"默认实现的语义"。）
            Assert.False(Regex.IsMatch(applier, @"SetWebDataDemand\(\s*!"),
                "默认需求写入不得对需求取反（SetWebDataDemand(!hasDemand) 会让语义完全颠倒：有需求时反而缩小采集）");
            Assert.True(Regex.IsMatch(applier, @"SetWebDataDemand\(\s*[A-Za-z_][A-Za-z0-9_]*\s*\)"),
                "默认需求写入必须把需求值原样传给 SetWebDataDemand（不能取反、不能传常量）");
        }

        [Fact]
        public async Task LiteWebServer生产接线把Web需求送达采集层()
        {
            // ★ 复审重要#1 的**行为**护栏：此前**没有任何用例构造过 LiteWebServer**，
            //   生产接线（LiteWebServer → WebDemandSynchronizer → 采集层）零覆盖。
            //   这里构造真实的 LiteWebServer（只替换"写进采集层"的那一端），
            //   走真实的监听 / 握手 / 需求传播链路，断言 Web 需求真的被送达采集层。
            // ★ F6：不只断言"applier 被调用过"，还要断言它收到的**值本身**
            //   （无需求 → false；有需求 → true）。
            // ★ F7 卫生：只监听**回环**、随机强口令、finally 复位 LiteWebServer.Instance。
            int port = FindFreePort();
            var sink = new RecordingDemandSink();
            int samplingCalls = 0;

            bool oldEnabled = _cfg.WebServerEnabled;
            int oldPort = _cfg.WebServerPort;
            string oldPassword = _cfg.WebServerPassword;
            string localPassword = NewStrongPassword();
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            _cfg.WebServerPassword = localPassword;
            try
            {
                using var server = new LiteWebServer(
                    _cfg,
                    samplingRequester: ct => { Interlocked.Increment(ref samplingCalls); return Task.FromResult(true); },
                    demandApplier: sink.Apply);
                UseLoopbackListener(server);   // ★ F7：只监听回环，不在局域网上暴露服务

                Assert.True(server.Start(out string err), "LiteWebServer 启动失败: " + err);
                Assert.True(server.IsRunning, "启动成功后 IsRunning 必须为 true");

                // ★ 无需求 → applier 收到 false
                Assert.True(sink.Count >= 1, "启动后必须至少同步过一次需求");
                Assert.False(sink.Last, "没有 Web 需求时，采集层必须收到 false（applier 收到的值本身）");

                using (var client = await OpenWebSocketAsync(port, "?pwd=" + localPassword))
                {
                    Assert.True(await WaitUntilAsync(() => sink.Last, 5000),
                        "WebSocket 连接建立后，Web 需求必须被送达采集层（生产接线回归护栏）");
                    // ★ 有需求 → applier 收到 true
                    Assert.True(sink.Last, "存在 Web 需求时，采集层必须收到 true（applier 收到的值本身）");
                    Assert.True(sink.Snapshot().Contains(true),
                        "采集层必须收到过 true（不能只收到 false 或常量）");
                    Assert.True(server.HasWebDataDemand, "服务端应报告存在 Web 需求");
                    Assert.Equal(1, server.AuthenticatedConnectionCount);

                    // 广播路径应当也走共享采样入口（连接还在时才有广播）
                    Assert.True(await WaitUntilAsync(() => Volatile.Read(ref samplingCalls) > 0, 8000),
                        "广播路径应当走过共享采样入口");
                }

                Assert.True(await WaitUntilAsync(() => sink.Count >= 3 && !sink.Last, 5000),
                    "最后一个连接断开后，必须把 false 送达采集层");
                Assert.False(sink.Last, "连接断开后，最后一次写入的需求值必须是 false");
                Assert.False(server.HasWebDataDemand, "连接断开后不应再有 Web 需求");
            }
            finally
            {
                _cfg.WebServerEnabled = oldEnabled;
                _cfg.WebServerPort = oldPort;
                _cfg.WebServerPassword = oldPassword;
                ResetLiteWebServerInstance();   // ★ F7：不要把已 Dispose 的实例留在静态字段里
            }
        }

        [Fact]
        public async Task HTTP租期到期后采集层最终收到false()
        {
            // ★ F2② 的**行为级**护栏（M9 / M15 变异必须让本用例变红）：
            //   HTTP 租期到期**没有任何事件可挂靠**（DemandChanged 只在连接增删 / 租期授予时触发），
            //   唯一能发现它的机制就是 Start() 里的 _demandSync.Start()（200ms 周期监视）。
            //   删掉它 → 租期到期无人发现 → 采集层需求**永久停在 true** → 永久白采集
            //   （不崩、不红、无日志 —— 与上一轮重要#1 同类的静默失效）。
            // ★ 这里用**真实**的 LiteWebServer（走真实 Start() 接线）+ **HTTP-only** 客户端
            //   （不建立 WebSocket 连接 → 没有任何事件路径能发现租期到期），
            //   静默等到租期过期（默认 2 秒），断言采集层最终收到 false。
            int port = FindFreePort();
            var sink = new RecordingDemandSink();

            bool oldEnabled = _cfg.WebServerEnabled;
            int oldPort = _cfg.WebServerPort;
            string oldPassword = _cfg.WebServerPassword;
            string localPassword = NewStrongPassword();
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            _cfg.WebServerPassword = localPassword;
            try
            {
                using var server = new LiteWebServer(
                    _cfg,
                    samplingRequester: ct => Task.FromResult(true),
                    demandApplier: sink.Apply);
                UseLoopbackListener(server);   // ★ F7：只监听回环

                Assert.True(server.Start(out string err), "LiteWebServer 启动失败: " + err);

                // 无需求 → 先收到 false
                Assert.True(sink.Count >= 1, "启动后必须至少同步过一次需求");
                Assert.False(sink.Last, "没有 Web 需求时，采集层必须收到 false");

                // ★ HTTP-only 请求一次（不建立 WebSocket 连接）
                string resp = await SendHttpAsync(port, "/api/snapshot?pwd=" + localPassword);
                Assert.Contains("200", resp);

                // 租期授予 → 采集层收到 true
                Assert.True(await WaitUntilAsync(() => sink.Last, 5000),
                    "HTTP 租期授予后必须把 true 送达采集层");
                int countAtTrue = sink.Count;

                // ★ 静默等到租期过期 → 周期监视必须把 false 送达采集层
                Assert.True(await WaitUntilAsync(() => sink.Count > countAtTrue && !sink.Last, 15000),
                    "HTTP 租期到期后必须把 false 送达采集层（否则需求永久停在 true → 永久白采集）");
                Assert.False(server.HasWebDataDemand, "租期到期后服务端不应再有 Web 需求");
            }
            finally
            {
                _cfg.WebServerEnabled = oldEnabled;
                _cfg.WebServerPort = oldPort;
                _cfg.WebServerPassword = oldPassword;
                ResetLiteWebServerInstance();   // ★ F7
            }
        }

        [Fact]
        public async Task HTTP租期在等待采样期间就已授予()
        {
            // ★ 次要#2 的护栏（M6 变异必须让本用例变红）。
            //   变异：把"授予 HTTP 租期"挪到"共享采样"之后 → 本用例必须红。
            //   原因：等待采样期间如果还没有需求，采集层会误判成"没人要数据"，
            //   采样请求就可能落在"无需求"的采集范围上（拿到旧值/空值）。
            var fakeTime = new FakeTimeProvider();
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int samplingEntered = 0;

            var manager = NewManager(
                demandState: new WebDemandState(fakeTime, TimeSpan.FromSeconds(2)),
                timeProvider: fakeTime,
                // ★ 阻塞式采样入口：让请求停在"等待采样"这一步
                samplingRequester: ct => { Interlocked.Increment(ref samplingEntered); return gate.Task; });
            manager.Start();

            using var host = new LoopbackHost(manager);
            Task<string> http = SendHttpAsync(host.Port, "/api/snapshot");

            try
            {
                Assert.True(await WaitUntilAsync(() => Volatile.Read(ref samplingEntered) > 0, 5000),
                    "请求应当进入共享采样等待（否则本用例的前提不成立）");

                // ★ 关键断言：等待采样期间租期已经授予
                Assert.True(manager.HasHttpLease,
                    "等待采样期间必须已经授予 HTTP 租期（授权点必须在采样之前）");
                Assert.True(manager.HasWebDataDemand, "等待采样期间必须存在 Web 需求");

                gate.TrySetResult(true);
                string resp = await http;
                Assert.Contains("200", resp);
            }
            finally
            {
                gate.TrySetResult(false);
                manager.Stop();
            }
        }

        [Fact]
        public async Task 并发同步不会写入陈旧需求值()
        {
            // ★ 次要#4 的护栏：WebDemandSynchronizer.SyncNow() 原来"读在锁外、写在锁内"，
            //   事件线程与 200ms 监视线程可能读到不同的需求值并**乱序写入**
            //   （陈旧的 true 落在 false 之后）→ 采集层瞬时过度采集。
            //   场景：第一次同步卡在"写入"内部，需求 meanwhile 变成 false，第二次同步并发进入。
            int demandFlag = 1;   // 1 = 有需求
            var firstWriteEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondWriteDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int writeCount = 0;
            var written = new List<bool>();

            var sync = new WebDemandSynchronizer(
                demandProvider: () => Volatile.Read(ref demandFlag) == 1,
                applyDemand: v =>
                {
                    int n = Interlocked.Increment(ref writeCount);
                    if (n == 1)
                    {
                        // 第一次写入卡住（模拟"读到了旧需求、还没写下去"的窗口）
                        firstWriteEntered.TrySetResult(true);
                        releaseFirstWrite.Task.GetAwaiter().GetResult();
                    }
                    else
                    {
                        secondWriteDone.TrySetResult(true);
                    }
                    // ★ F4：记录必须放在**阻塞点之后**。
                    //   放在之前时，无论实现修没修好，记录顺序恒为 [true, false]，
                    //   下面的 Assert.False(last) 恒成立 → 护栏是恒真断言、不承重。
                    //   放在之后：未修好时（写入发生在锁外）第二次写会**先**完成 → 顺序 [false, true] → 变红。
                    lock (written) written.Add(v);
                    return true;
                });

            var first = Task.Run(() => sync.SyncNow());
            await firstWriteEntered.Task;

            // 需求消失
            Volatile.Write(ref demandFlag, 0);

            // 第二次同步并发进入
            var second = Task.Run(() => sync.SyncNow());

            // 修好后第二次同步会被锁挡住（最多等 1 秒）；未修好时它会立刻写完
            await Task.WhenAny(secondWriteDone.Task, Task.Delay(1000));

            // ★ F4：修好后第二次写入必须**仍然被锁挡住**（读 + 比较 + 写在同一把锁里）
            Assert.False(secondWriteDone.Task.IsCompleted,
                "第二次同步必须被同步器的锁挡住（读 + 比较 + 写必须在同一把锁里）");

            releaseFirstWrite.TrySetResult(true);
            await Task.WhenAll(first, second);

            Assert.Equal(2, writeCount);
            bool last = false;
            lock (written) last = written[written.Count - 1];
            Assert.False(last,
                "最后写入的需求值必须等于当前需求（不能写入陈旧值：读到的必须是写入时的需求）");
        }

        [Fact]
        public void Web服务启动失败时不留下运行中的会话管理器()
        {
            // ★ 次要#5 的护栏：Start() 的 catch 原来只复位 _isRunning、停 listener，
            //   **没有**停 _sessionManager → 异常发生在 _sessionManager.Start() 之后时
            //   会留下一个仍在跑的广播循环，而需求监视又没启动
            //   → HTTP 租期到期没人发现 → 采集层需求**永久停在 true**（永久白采集）。
            //   为什么用源码级不变量：Start() 在 _sessionManager.Start() 之后没有可注入的失败点，
            //   单测无法让"启动失败"正好落在那个窗口（不为此改生产启动顺序）。
            string path = FindRepoFile(Path.Combine("src", "System", "WebServer", "LiteWebServer.cs"));
            Assert.True(path != null && File.Exists(path), "找不到 LiteWebServer.cs（仓库根定位失败）");
            string code = StripComments(File.ReadAllText(path));

            string startBody = ExtractMethodBody(code, "Start");
            Assert.True(startBody != null, "LiteWebServer.cs 里找不到 Start 方法");
            string catchBlock = ExtractOuterCatchBlock(startBody);
            Assert.True(catchBlock != null, "Start 方法里找不到最外层的 catch 块");

            Assert.True(catchBlock.Contains("_sessionManager.Stop()"),
                "Start 失败时必须停掉会话管理器（否则留下仍在跑的广播循环 → 需求永久停在 true）");
            Assert.True(catchBlock.Contains("_demandSync.Stop()"),
                "Start 失败时必须停掉需求监视（否则失败后监视线程还在跑）");

            // 清理必须发生在"已经启动之后"（否则这段代码永远走不到）
            int startIdx = startBody.IndexOf("_sessionManager.Start()", StringComparison.Ordinal);
            int stopIdx = catchBlock.IndexOf("_sessionManager.Stop()", StringComparison.Ordinal);
            Assert.True(startIdx >= 0 && stopIdx >= 0, "Start 里应当能找到会话管理器的启动与停止调用");
            Assert.True(startBody.IndexOf("_sessionManager.Stop()", StringComparison.Ordinal) > startIdx,
                "会话管理器的停止必须排在启动之后（失败清理路径）");
        }

        [Fact]
        public async Task 快速重启后在途HTTP请求仍能得到响应()
        {
            // ★ 次要#6 的护栏 —— 它真正防的是"**快速重启丢响应**"：
            //   Stop → Start 之后，之前连上但还没发请求的客户端再发请求时，
            //   必须仍然收到 200（而不是被静默吞掉、连一个响应都没有）。
            // ★ 定位更正（B2 修复轮 3 / F5）：上一轮把它写成"防 ObjectDisposedException 回归"是**错的** ——
            //   .NET 8 上（目标框架 net8.0-windows / SDK 8.0.426）实测 4 种组合
            //   （Cancel→Dispose + CreateLinkedTokenSource / Dispose only + CreateLinkedTokenSource /
            //     Dispose only + token.Register / Cancel→Dispose + token.Register）**全部不抛 ODE**
            //   （CTS 先 Cancel → token 已取消 → 注册走"立即回调"分支）。
            //   上一轮的实现者是被自己代码注释里那句未经证实的断言带偏的。
            //   生产代码"退役 CTS 只 Cancel 不 Dispose"仍然保留，
            //   但它的理由是"不指向已释放对象"这个确定性，**不是**防 ODE。
            var manager = NewManager(samplingRequester: ct => Task.FromResult(false));
            manager.Start();

            using var host = new LoopbackHost(manager);

            // 先连上但**不发送请求**：让服务器先捕获 lifetime.Token
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, host.Port);
            await Task.Delay(300);   // 等服务器 accept 并捕获令牌

            // 快速 Stop → Start（复现"上一轮 CTS 被释放"的窗口）
            manager.Stop();
            manager.Start();

            // 现在才发请求：服务器用的是 Stop 之前捕获的令牌
            string resp;
            using (var stream = client.GetStream())
            {
                var req = Encoding.ASCII.GetBytes(
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{host.Port}\r\nConnection: close\r\n\r\n");
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

                resp = sb.ToString();
            }

            // 前提校验：连接确实是在 Stop 之前被接收的
            //   （跨越 Stop 边界的在途请求按设计不授予租期 → HasHttpLease 必须为 false）
            Assert.False(manager.HasHttpLease,
                "连接必须在 Stop 之前被服务器接收（否则本用例的前提不成立）");

            Assert.Contains("200", resp);

            manager.Stop();
        }

        // ================================================================
        // 8. 广播代次隔离（复审 R3 的常驻回归测试）
        // ================================================================

        /// <summary>
        /// 假时钟：单调时间戳恒定，且**计时器永不触发**。
        ///
        /// ★ 为什么必须"计时器永不触发"：
        ///   <c>RequestSharedSamplingWithTimeoutAsync</c> 用
        ///   <c>Task.Delay(SamplingWaitTimeout, _time)</c>（2 秒）做**超时降级**
        ///   （超时后照常序列化缓存值并发送）。
        ///   基类 <see cref="TimeProvider"/> 的 <c>CreateTimer</c> 走的是**真实**系统计时器，
        ///   所以"注入假时钟"并**不能**让这个超时失效 ——
        ///   机器一忙，2 秒一到新代次的采样请求就降级、照常序列化并发送，
        ///   用例会变成**依赖真实时间**的 flaky 测试
        ///   （本轮实际踩到：单独跑绿、全量跑红，收到 14 字节）。
        ///   本类让该超时**永不**发生，把用例从真实时间里彻底解耦。
        /// </summary>
        private sealed class NeverFiringTimeProvider : TimeProvider
        {
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;

            public override long GetTimestamp() => 0;

            public override DateTimeOffset GetUtcNow()
                => new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

            /// <summary>
            /// 4 参重载（.NET 8 <see cref="TimeProvider"/> 上唯一可重写的计时器入口）：
            /// <c>Task.Delay(TimeSpan, TimeProvider)</c> 内部（<c>DelayPromise</c>）
            /// 走的就是这一个，**必须**覆盖它，否则采样等待的 2 秒超时仍会按真实时间触发。
            /// ★ 实测（net8.0.32 探针）：覆盖后 <c>Task.Delay</c> 挂起且**永不完成**，
            ///   等价于"超时降级"这条路径被彻底关掉。
            /// </summary>
            public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
                => new NeverFiringTimer();

            private sealed class NeverFiringTimer : ITimer
            {
                public bool Change(TimeSpan dueTime, TimeSpan period) => true;
                public void Dispose() { }
                public ValueTask DisposeAsync() => default;
            }
        }

        /// <summary>
        /// 复审 R3：旧代次的广播**不得**跨过 Stop → Start 边界，
        /// 把帧发给"重启之后才连上"的新连接。
        ///
        /// ★ 修复前这条用例是**红的**（复审实测新连接收到 15 字节旧帧）：
        ///   广播循环只在 while 入口校验服务代次；循环体里 await 共享采样期间
        ///   发生 Stop → Start 时，**采集方不响应取消**、等待包装又把取消
        ///   **转成 false**（不抛异常）→ 旧循环"正常"恢复 → 继续序列化 →
        ///   枚举共享的连接字典 → 把旧帧发给新代次的连接。
        ///
        /// ★ 构造方式：第 1 次采样请求挂起 → Stop → Start → 用私有接缝按
        ///   **新代次**注册连接 → 放行第 1 次请求 → 旧循环恢复。
        ///   之后的所有采样请求一律挂起 → 新代次的广播循环停在采样处，
        ///   于是"新连接收到的字节"只可能来自**旧**循环。
        /// </summary>
        [Fact]
        public async Task 旧代次广播不得发给重启之后才连上的新连接()
        {
            var firstRequestEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockLaterRequests = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int requestCount = 0;

            var manager = NewManager(
                timeProvider: new NeverFiringTimeProvider(),
                samplingRequester: ct =>
                {
                    if (Interlocked.Increment(ref requestCount) == 1)
                    {
                        firstRequestEntered.TrySetResult(true);
                        return releaseFirstRequest.Task;
                    }
                    return blockLaterRequests.Task;
                });

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            async Task<(TcpClient Client, TcpClient Server)> AcceptPairAsync()
            {
                var client = new TcpClient();
                var accept = listener.AcceptTcpClientAsync();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                return (client, await accept);
            }

            TcpClient client2 = null;
            TcpClient server2 = null;
            try
            {
                var pair1 = await AcceptPairAsync();
                manager.Start();
                AddClientViaPrivateSeam(manager, pair1.Server);

                Assert.True(await firstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)),
                    "旧代次的第一次采样请求必须已经进入挂起状态（前提不成立）");

                // 跨过 Stop → Start 边界
                manager.Stop();
                manager.Start();
                int newGeneration = manager.Generation;

                // 新连接在**新代次**注册
                var pair2 = await AcceptPairAsync();
                client2 = pair2.Client;
                server2 = pair2.Server;
                AddClientViaPrivateSeam(manager, server2);

                // ★ 防"空断言恒真"：必须确认新连接**确实已按新代次登记**，
                //   否则"收到 0 字节"可能只是"根本没有收件人"，用例就白绿了。
                Assert.Equal(newGeneration, manager.Generation);
                Assert.True(CurrentClientCount(manager) >= 1,
                    "新连接必须已经登记在管理器里（否则本用例会变成空断言恒真）");

                // 放行旧代次的采样请求 → 旧循环恢复
                releaseFirstRequest.TrySetResult(true);

                int received = await ReadBytesAsync(client2, TimeSpan.FromMilliseconds(700));
                Assert.Equal(0, received);
            }
            finally
            {
                manager.Stop();
                releaseFirstRequest.TrySetResult(false);
                blockLaterRequests.TrySetResult(false);
                listener.Stop();
                client2?.Dispose();
                server2?.Dispose();
            }
        }

        /// <summary>
        /// 正向对照（防"空断言恒真"）：与上一条**同一套装置**，但**不**跨 Stop → Start 边界。
        /// 此时广播**必须**能送达客户端 —— 若这条用例变红，说明上一条"收到 0 字节"
        /// 只是因为装置看不见帧，而不是因为代次隔离生效。
        /// </summary>
        [Fact]
        public async Task 未跨重启边界时广播必须能送达客户端_正向对照()
        {
            var firstRequestEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockLaterRequests = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int requestCount = 0;

            var manager = NewManager(
                timeProvider: new NeverFiringTimeProvider(),
                samplingRequester: ct =>
                {
                    if (Interlocked.Increment(ref requestCount) == 1)
                    {
                        firstRequestEntered.TrySetResult(true);
                        return releaseFirstRequest.Task;
                    }
                    return blockLaterRequests.Task;
                });

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            TcpClient client = null;
            TcpClient server = null;
            try
            {
                var clientSide = new TcpClient();
                var accept = listener.AcceptTcpClientAsync();
                await clientSide.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                client = clientSide;
                server = await accept;

                manager.Start();
                AddClientViaPrivateSeam(manager, server);

                Assert.True(await firstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)),
                    "第一次采样请求必须已经进入挂起状态（前提不成立）");
                Assert.True(CurrentClientCount(manager) >= 1, "连接必须已经登记");

                releaseFirstRequest.TrySetResult(true);

                int received = await ReadBytesAsync(client, TimeSpan.FromSeconds(5));
                Assert.True(received > 0,
                    "未跨重启边界时广播必须能送达客户端（否则上一条用例的 0 字节毫无意义）");
            }
            finally
            {
                manager.Stop();
                releaseFirstRequest.TrySetResult(false);
                blockLaterRequests.TrySetResult(false);
                listener.Stop();
                client?.Dispose();
                server?.Dispose();
            }
        }

        /// <summary>
        /// 用私有接缝 <c>AddClient</c> 登记一个**已建立的 TCP 连接**，
        /// 并把该连接绑定到**当前**服务代次。
        ///
        /// ★ 为什么走私有接缝而不是 <c>HandleClientAsync</c>：
        ///   本用例要精确控制"连接属于哪个服务代次"，
        ///   而 <c>HandleClientAsync</c> 会做完整 WebSocket 握手、
        ///   登记时机由服务端决定，**无法稳定构造**"Stop → Start 之后才连上"这一时刻。
        ///   本文件已有同类先例（<c>UseLoopbackListener</c> / <c>ResetLiteWebServerInstance</c>）
        ///   同样用反射访问 internal / 私有接缝。
        ///   ★ 它**没有**绕过被测代码：广播的采集、序列化、发送路径全部照常执行。
        /// </summary>
        private static void AddClientViaPrivateSeam(WebSessionManager manager, TcpClient server)
        {
            var method = typeof(WebSessionManager).GetMethod("AddClient",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(method != null,
                "WebSessionManager 必须保留 private AddClient 接缝（否则无法构造'按代次登记'的场景）");
            method.Invoke(manager, new object[] { server, manager.Generation });
        }

        /// <summary>当前登记的 WebSocket 客户端数量（用于断言用例前提真的成立）。</summary>
        private static int CurrentClientCount(WebSessionManager manager)
        {
            var field = typeof(WebSessionManager).GetField("_wsClients",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(field != null, "WebSessionManager 必须保留 _wsClients 字段");
            var value = field.GetValue(manager) as System.Collections.ICollection;
            return value?.Count ?? 0;
        }

        /// <summary>在给定时间内读取客户端收到的字节数（0 = 没收到任何字节）。</summary>
        private static async Task<int> ReadBytesAsync(TcpClient client, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var buffer = new byte[512];
                return await client.GetStream().ReadAsync(buffer, cts.Token);
            }
            catch (OperationCanceledException) { return 0; }
            catch (IOException) { return 0; }
            catch (ObjectDisposedException) { return 0; }
        }
    }
}
