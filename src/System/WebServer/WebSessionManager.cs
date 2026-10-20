using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.WebServer
{
    /// <summary>
    /// Web 有效数据需求的一次性快照（同一把锁内读取，保证连接数与租期状态不撕裂）。
    /// </summary>
    public readonly struct WebDemandSnapshot
    {
        /// <summary>已鉴权且尚未关闭的 WebSocket 连接数。</summary>
        public int AuthenticatedConnections { get; }

        /// <summary>已鉴权的 /api/snapshot 请求持有的短需求租期是否仍然有效。</summary>
        public bool HasHttpLease { get; }

        public WebDemandSnapshot(int authenticatedConnections, bool hasHttpLease)
        {
            AuthenticatedConnections = authenticatedConnections;
            HasHttpLease = hasHttpLease;
        }

        /// <summary>是否存在合法的 Web 数据需求（连接或 HTTP 租期任一成立）。</summary>
        public bool HasAnyDemand => AuthenticatedConnections > 0 || HasHttpLease;
    }

    /// <summary>
    /// Web 有效数据需求状态（线程安全）。
    ///
    /// 语义（计划 §4.4）：
    ///  - **已鉴权且尚未关闭的 WebSocket 连接**计入需求；连接数变化不按浏览器发送频率增加采样频率；
    ///  - **已鉴权的 /api/snapshot 请求**获得一个短需求租期（默认 2 秒），连续轮询会不断延长租期；
    ///  - 未鉴权请求、仅取 HTML / favicon 的请求**不**产生任何硬件需求；
    ///  - 断开、写失败、异常、Stop / 重启都必须正确回收这些状态。
    ///
    /// 租期用**单调时钟**（TimeProvider）判断，墙上时间回拨不会让租期提前失效或一直有效。
    /// 状态全部由实例持有（不做全局静态时间戳），便于用假时钟做单元测试。
    /// </summary>
    public sealed class WebDemandState
    {
        /// <summary>HTTP 快照需求的默认租期（2 秒）。</summary>
        public static readonly TimeSpan DefaultHttpLease = TimeSpan.FromSeconds(2);

        private readonly object _gate = new object();
        private readonly TimeProvider _time;
        private readonly long _httpLeaseTicks;   // 租期长度（单调时钟刻度）

        private int _authenticatedConnections;
        private long _httpLeaseExpiry;          // 租期到期时刻（单调时钟刻度），0 = 没有租期

        /// <param name="timeProvider">可注入的单调时钟，默认 <see cref="TimeProvider.System"/>。</param>
        /// <param name="httpLease">HTTP 快照租期长度，默认 <see cref="DefaultHttpLease"/>。</param>
        public WebDemandState(TimeProvider? timeProvider = null, TimeSpan? httpLease = null)
        {
            _time = timeProvider ?? TimeProvider.System;

            TimeSpan lease = httpLease ?? DefaultHttpLease;
            if (lease < TimeSpan.Zero) lease = TimeSpan.Zero;

            long ticks = (long)Math.Round(_time.TimestampFrequency * lease.TotalSeconds);
            _httpLeaseTicks = ticks > 0 ? ticks : 0;
        }

        /// <summary>已鉴权且尚未关闭的 WebSocket 连接数。</summary>
        public int AuthenticatedConnectionCount
        {
            get { lock (_gate) return _authenticatedConnections; }
        }

        /// <summary>HTTP 快照租期是否仍然有效（已过期的租期会被顺手清掉）。</summary>
        public bool HasHttpLease
        {
            get { lock (_gate) return LeaseValidLocked(); }
        }

        /// <summary>当前是否存在合法的 Web 数据需求。</summary>
        public bool HasAnyDemand
        {
            get { lock (_gate) return _authenticatedConnections > 0 || LeaseValidLocked(); }
        }

        /// <summary>一次性读取全部需求状态（同一把锁内读取，连接数与租期状态不会撕裂）。</summary>
        public WebDemandSnapshot GetSnapshot()
        {
            lock (_gate)
            {
                bool lease = LeaseValidLocked();
                return new WebDemandSnapshot(_authenticatedConnections, lease);
            }
        }

        /// <summary>登记一个已鉴权的 WebSocket 连接。</summary>
        public void AddAuthenticatedConnection()
        {
            lock (_gate) _authenticatedConnections++;
        }

        /// <summary>回收一个 WebSocket 连接（计数不会变成负数）。</summary>
        public void RemoveAuthenticatedConnection()
        {
            lock (_gate)
            {
                if (_authenticatedConnections > 0) _authenticatedConnections--;
            }
        }

        /// <summary>授予一次 HTTP 快照需求租期（从此刻起算，连续轮询会不断延长）。</summary>
        public void GrantHttpLease()
        {
            if (_httpLeaseTicks <= 0) return;
            lock (_gate)
            {
                _httpLeaseExpiry = _time.GetTimestamp() + _httpLeaseTicks;
            }
        }

        /// <summary>清空全部需求（服务 Stop / 重启时调用）。</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _authenticatedConnections = 0;
                _httpLeaseExpiry = 0;
            }
        }

        /// <summary>租期是否有效（调用方必须已持有 _gate）。</summary>
        private bool LeaseValidLocked()
        {
            long expiry = _httpLeaseExpiry;
            if (expiry == 0) return false;
            if (_time.GetTimestamp() < expiry) return true;

            // 已过期：顺手清零，避免过期状态一直挂着
            _httpLeaseExpiry = 0;
            return false;
        }
    }

    public class WebSessionManager
    {
        private readonly ConcurrentDictionary<TcpClient, bool> _wsClients = new();
        private readonly Action<Utf8JsonWriter> _dataProvider;
        private readonly WebDemandState _demand;

        // 连接集合与需求计数的联动锁：保证"加入连接"与"清空全部需求"不会交错出脏状态
        private readonly object _clientsGate = new object();

        private volatile bool _isRunning = false;
        private int _generation = 0;                       // Start/Stop 代次：用来区分 Stop 前后的广播任务
        private CancellationTokenSource? _broadcastCts;

        /// <param name="dataProvider">快照序列化回调。</param>
        /// <param name="demandState">需求状态实例（默认新建一个）。</param>
        /// <param name="timeProvider">需求租期使用的单调时钟（默认 <see cref="TimeProvider.System"/>）。</param>
        public WebSessionManager(Action<Utf8JsonWriter> dataProvider, WebDemandState? demandState = null, TimeProvider? timeProvider = null)
        {
            _dataProvider = dataProvider;
            _demand = demandState ?? new WebDemandState(timeProvider);
        }

        /// <summary>Web 有效数据需求状态（线程安全）。</summary>
        public WebDemandState Demand => _demand;

        /// <summary>已鉴权且尚未关闭的 WebSocket 连接数。</summary>
        public int AuthenticatedConnectionCount => _demand.AuthenticatedConnectionCount;

        /// <summary>已鉴权的 HTTP 快照租期是否有效。</summary>
        public bool HasHttpLease => _demand.HasHttpLease;

        /// <summary>当前是否存在合法的 Web 数据需求（已鉴权连接或未过期的 HTTP 租期）。</summary>
        public bool HasWebDataDemand => _demand.HasAnyDemand;

        /// <summary>一次性读取需求状态（连接数 + 租期，不撕裂）。</summary>
        public WebDemandSnapshot GetDemandSnapshot() => _demand.GetSnapshot();

        /// <summary>
        /// 当前运行代次（每次 Start / Stop 都会 +1）。
        /// 用来识别"跨越 Stop 边界"的连接与请求：它们不再登记为需求。
        /// </summary>
        public int Generation => Volatile.Read(ref _generation);

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            // ★ 用代次 + 取消令牌区分 Stop 前后的广播任务：
            //   快速 Stop/Start 后，旧的广播循环必须自己退出，不能复活成第二个广播者。
            int generation = Interlocked.Increment(ref _generation);

            var cts = new CancellationTokenSource();
            // 上一次的 CTS 已经被 Cancel 过（延迟任务已结束），这里才安全释放
            var old = Interlocked.Exchange(ref _broadcastCts, cts);
            try { old?.Dispose(); } catch { }

            // ★ 先把 token 取出来再进任务体：
            //   cts.Token 是在 lambda **执行时**才求值的，
            //   如果任务体在 Start() 释放旧 CTS 之后才启动，就会读到已释放的 CTS -> ObjectDisposedException。
            var token = cts.Token;
            Task.Run(() => BroadcastLoop(generation, token));
        }

        public void Stop()
        {
            _isRunning = false;

            // 让旧广播循环立刻退出（代次 + 取消）
            Interlocked.Increment(ref _generation);
            try { _broadcastCts?.Cancel(); } catch { }
            // 这里不 Dispose：旧循环可能正拿着这个 token 收尾，
            // 交给下一次 Start() 释放，避免 ObjectDisposedException。

            lock (_clientsGate)
            {
                foreach (var client in _wsClients.Keys)
                {
                    try { client.Close(); } catch { }
                }
                _wsClients.Clear();

                // ★ 回收全部旧需求：连接与 HTTP 租期都不留给下一次启动
                _demand.Clear();
            }
        }

        /// <summary>
        /// 统一处理客户端连接（自动识别 WebSocket 或 HTTP）
        /// </summary>
        public async Task HandleClientAsync(TcpClient client)
        {
            // ★ 捕获本次连接的代次。
            //   Stop 之后才完成的握手 / 才到达的请求一律不再登记需求（见 AddClient / HandleHttpRequest）。
            int connectionGeneration = Volatile.Read(ref _generation);

            byte[] buffer = null;
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 5000; // 防恶意连接

                // 1. 读取请求头 (最多 8KB)
                buffer = ArrayPool<byte>.Shared.Rent(8192);
                int bytesRead = await ReadHeaderAsync(stream, buffer);
                
                if (bytesRead == 0) { client.Close(); return; }

                string requestStr = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // 2. 协议分流
                if (IsWebSocketUpgrade(requestStr))
                {
                    if (Handshake(stream, requestStr))
                    {
                        // 握手成功 = 已鉴权，登记为有效 Web 需求
                        AddClient(client, connectionGeneration);
                        await ReceiveLoop(client); // 进入 WS 循环
                    }
                }
                else
                {
                    HandleHttpRequest(stream, requestStr, connectionGeneration);
                    client.Close(); // HTTP 是短连接
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Client Error: {ex.Message}");
                // 异常路径：连接可能已经登记过，必须回收需求
                RemoveClient(client);
                try { client.Close(); } catch { }
            }
            finally
            {
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // --- 连接与需求的联动管理 ---

        /// <summary>
        /// 登记一个已鉴权的 WebSocket 连接（同时计入需求）。
        /// 与 <see cref="Stop"/> 的清空动作互斥，避免"刚 Clear 又被加回来"的脏状态。
        /// </summary>
        /// <param name="client">要登记的连接。</param>
        /// <param name="connectionGeneration">该连接建立时的运行代次（由 HandleClientAsync 捕获）。</param>
        private void AddClient(TcpClient client, int connectionGeneration)
        {
            lock (_clientsGate)
            {
                // ★ Stop 之后不再登记连接。
                //   Stop 已经清空过集合，不会再遍历一次去 Close 这个客户端：
                //   如果这里仍然把它加回来，客户端会一直挂在 ReceiveLoop 上，
                //   需求计数长期为 1，硬件采集永远进不了空闲停止（§4.4「Stop 清理全部旧需求」）。
                if (!_isRunning || connectionGeneration != Volatile.Read(ref _generation))
                {
                    try { client.Close(); } catch { }
                    return;
                }

                if (_wsClients.TryAdd(client, true))
                {
                    _demand.AddAuthenticatedConnection();
                }
            }
        }

        /// <summary>
        /// 移除一个 WebSocket 连接并回收它的需求。
        /// 只有真正从集合里移除成功才减计数，重复移除不会把计数减成负数。
        /// </summary>
        private void RemoveClient(TcpClient client)
        {
            lock (_clientsGate)
            {
                if (_wsClients.TryRemove(client, out _))
                {
                    _demand.RemoveAuthenticatedConnection();
                }
            }
        }

        // --- 内部网络辅助方法 ---

        private async Task<int> ReadHeaderAsync(NetworkStream stream, byte[] buffer)
        {
            int totalBytes = 0;
            while (totalBytes < buffer.Length)
            {
                // 预留空间检查
                if (totalBytes >= buffer.Length) break;

                int read = await stream.ReadAsync(buffer, totalBytes, buffer.Length - totalBytes);
                if (read == 0) break;
                
                totalBytes += read;
                if (FindHeaderEnd(buffer, totalBytes) >= 0) return totalBytes;
            }
            return totalBytes;
        }

        private int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i < length - 3; i++)
            {
                if (buffer[i] == 13 && buffer[i+1] == 10 && buffer[i+2] == 13 && buffer[i+3] == 10)
                    return i + 4; // 返回包含结束符的长度
            }
            return -1;
        }

        private bool IsWebSocketUpgrade(string req)
        {
            return Regex.IsMatch(req, "GET", RegexOptions.IgnoreCase) && 
                   Regex.IsMatch(req, "Upgrade: websocket", RegexOptions.IgnoreCase);
        }

        private void HandleHttpRequest(NetworkStream stream, string request, int connectionGeneration)
        {
            string[] lines = request.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            
            string path = lines[0].Split(' ').Length > 1 ? lines[0].Split(' ')[1] : "/";
            
            if (path.StartsWith("/api/snapshot"))
            {
                if (!CheckAuth(request))
                {
                    // 未鉴权：不产生任何硬件需求
                    SendHttpResponse(stream, 401, "text/plain", Encoding.UTF8.GetBytes("Unauthorized"), 12);
                    return;
                }

                // ★ 已鉴权的快照请求获得一个短需求租期（默认 2 秒）。
                //   连续 HTTP 轮询会不断延长租期；未鉴权请求 / HTML / favicon 都不会走到这里。
                //   跨越 Stop 边界的在途请求不再授予租期：Stop 已经清空过全部需求，
                //   再补一个 2 秒租期等于把已停止的服务又变成"有需求"。
                if (connectionGeneration == Volatile.Read(ref _generation))
                {
                    _demand.GrantHttpLease();
                }

                using var ms = new MemoryStream();
                using var writer = new Utf8JsonWriter(ms);
                _dataProvider(writer);
                writer.Flush();
                SendHttpResponse(stream, 200, "application/json", ms.GetBuffer(), (int)ms.Length);
            }
            else if (path == "/favicon.ico")
            {
                // 仅取图标：不产生硬件需求
                SendHttpResponse(stream, 404, "text/html", Array.Empty<byte>(), 0);
            }
            else
            {
                // 默认返回首页（仅取 HTML：不产生硬件需求）
                string iconBase64 = WebPageContent.GetAppIconBase64();
                bool authRequired = !string.IsNullOrEmpty(Settings.Load().WebServerPassword);
                
                string html = WebPageContent.IndexHtml
                    .Replace("{{FAVICON}}", !string.IsNullOrEmpty(iconBase64) ? $"<link rel='icon' type='image/png' href='data:image/png;base64,{iconBase64}'>" : "")
                    .Replace("{{AUTH_REQUIRED}}", authRequired.ToString().ToLower());
                
                byte[] data = Encoding.UTF8.GetBytes(html);
                SendHttpResponse(stream, 200, "text/html; charset=utf-8", data, data.Length);
            }
        }

        private bool CheckAuth(string requestHeader)
        {
            var pwd = Settings.Load().WebServerPassword;
            if (string.IsNullOrEmpty(pwd)) return true;

            var firstLine = requestHeader.Split('\n')[0]; 
            var match = Regex.Match(firstLine, @"[?&]pwd=([^&\s]+)");
            if (match.Success)
            {
                return match.Groups[1].Value == pwd;
            }
            return false;
        }

        private void SendHttpResponse(NetworkStream stream, int code, string type, byte[] body, int len)
        {
            string headers = $"HTTP/1.1 {code} OK\r\nContent-Type: {type}\r\nContent-Length: {len}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            byte[] hBytes = Encoding.UTF8.GetBytes(headers);
            stream.Write(hBytes, 0, hBytes.Length);
            if (len > 0) stream.Write(body, 0, len);
        }

        // --- WebSocket 核心逻辑 ---

        private async Task BroadcastLoop(int generation, CancellationToken token)
        {
            try
            {
                // ★ 代次校验：Stop 之后（或再次 Start 之后）本循环必须退出，
                //   防止快速 Stop/Start 复活出第二个广播者。
                while (_isRunning && generation == Volatile.Read(ref _generation))
                {
                    // [Optimization] 如果没有任何客户端连接，降低循环频率以节省资源
                    if (_wsClients.IsEmpty)
                    {
                        await Task.Delay(2000, token).ConfigureAwait(false);
                        continue;
                    }

                    byte[] buffer = null;
                    try
                    {

                        buffer = ArrayPool<byte>.Shared.Rent(32 * 1024); 
                        
                        int payloadLen;
                        using (var ms = new System.IO.MemoryStream(buffer))
                        using (var writer = new Utf8JsonWriter(ms))
                        {
                            _dataProvider(writer);
                            writer.Flush();
                            payloadLen = (int)ms.Position;
                        }

                        int headerLen = 2;
                        if (payloadLen >= 65536) headerLen += 8;
                        else if (payloadLen >= 126) headerLen += 2;
                        int totalLen = headerLen + payloadLen;

                        // Rent a target buffer for the frame
                        byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(totalLen);
                        try
                        {
                            int payloadOffset = EncodeFrameHeader(payloadLen, frameBuffer);
                            Buffer.BlockCopy(buffer, 0, frameBuffer, payloadOffset, payloadLen);

                            // Now we can return the "JSON Scratchpad" buffer IMMEDIATELY!
                            ArrayPool<byte>.Shared.Return(buffer);
                            buffer = null; // Prevent double return in finally

                            // Broadcast using frameBuffer
                            var tasks = new List<Task>();
                            // Capture the buffer and length for the closure
                            var bufToSend = frameBuffer; 
                            var lenToSend = totalLen;

                            foreach (var client in _wsClients.Keys)
                            {
                                if (!client.Connected)
                                {
                                    // 已断开的连接：回收需求
                                    RemoveClient(client);
                                    continue;
                                }
                                // We must NOT return frameBuffer until ALL sends are done.
                                // But since we await Task.WhenAll, it's fine.
                                tasks.Add(SendFrameSafeAsync(client, bufToSend, lenToSend));
                            }

                            if (tasks.Count > 0)
                            {
                                await Task.WhenAll(tasks);
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(frameBuffer);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Broadcast Error: {ex.Message}");
                    }
                    finally
                    {
                        if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
                    }

                    // 正常广播频率：每秒一次
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Stop 时的正常退出
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Broadcast Loop Error: {ex.Message}");
            }
        }

        private int EncodeFrameHeader(int payloadLen, byte[] buffer)
        {
            int offset = 0;

            // FIN(1) + RSV(000) + OpCode(1=Text) => 10000001 => 0x81
            buffer[offset++] = 0x81;

            // Mask(0) + Length
            if (payloadLen < 126)
            {
                buffer[offset++] = (byte)payloadLen;
            }
            else if (payloadLen <= 65535)
            {
                buffer[offset++] = 126;
                buffer[offset++] = (byte)((payloadLen >> 8) & 0xFF);
                buffer[offset++] = (byte)(payloadLen & 0xFF);
            }
            else
            {
                buffer[offset++] = 127;
                // 64-bit length
                buffer[offset++] = 0; buffer[offset++] = 0; buffer[offset++] = 0; buffer[offset++] = 0;
                buffer[offset++] = (byte)((payloadLen >> 24) & 0xFF);
                buffer[offset++] = (byte)((payloadLen >> 16) & 0xFF);
                buffer[offset++] = (byte)((payloadLen >> 8) & 0xFF);
                buffer[offset++] = (byte)(payloadLen & 0xFF);
            }
            return offset;
        }

        private async Task SendFrameSafeAsync(TcpClient client, byte[] buffer, int length)
        {
            try
            {
                var stream = client.GetStream();
                // [Fix] Add timeout to prevent memory leak from stuck tasks
                using var cts = new CancellationTokenSource(2000); // 2s timeout
                await stream.WriteAsync(buffer, 0, length, cts.Token);
            }
            catch
            {
                // Remove dead client (写失败：回收需求)
                RemoveClient(client);
                try { client.Close(); } catch { }
            }
        }


        private async Task ReceiveLoop(TcpClient client)
        {
            byte[] buffer = null;
            try
            {
                var stream = client.GetStream();
                // [Optimization] Use ArrayPool for receive buffer
                buffer = ArrayPool<byte>.Shared.Rent(1024);

                while (client.Connected && _wsClients.ContainsKey(client))
                {
                    // 阻塞读取，如果断开会抛出异常或返回0
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break; // 客户端关闭连接

                    // 解析帧头判断是否为 Close 帧 (OpCode = 8)
                    // 0x88 = 1000 1000 = FIN + Close
                    if ((buffer[0] & 0x0F) == 0x08)
                    {
                        break; // 收到关闭帧
                    }
                }
            }
            catch { }
            finally
            {
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
                // 正常断开 / 异常退出：回收需求
                RemoveClient(client);
                try { client.Close(); } catch { }
            }
        }

        private bool Handshake(NetworkStream stream, string request)
        {
            try
            {
                if (!CheckAuth(request))
                {
                    SendHttpResponse(stream, 401, "text/plain", Encoding.UTF8.GetBytes("Unauthorized"), 12);
                    return false;
                }

                // 1. 提取 Sec-WebSocket-Key
                var match = Regex.Match(request, "Sec-WebSocket-Key: (.*)");
                if (!match.Success) return false;

                string key = match.Groups[1].Value.Trim();

                // 2. 生成 Accept Key (Magic String: 258EAFA5-E914-47DA-95CA-C5AB0DC85B11)
                string magic = key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                byte[] hash = SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(magic));
                string acceptKey = Convert.ToBase64String(hash);

                // 3. 发送握手响应
                var response = new StringBuilder();
                response.Append("HTTP/1.1 101 Switching Protocols\r\n");
                response.Append("Connection: Upgrade\r\n");
                response.Append("Upgrade: websocket\r\n");
                response.Append($"Sec-WebSocket-Accept: {acceptKey}\r\n");
                response.Append("\r\n");

                byte[] respBytes = Encoding.UTF8.GetBytes(response.ToString());
                stream.Write(respBytes, 0, respBytes.Length);
                return true;
            }
            catch { return false; }
        }
    }
}
