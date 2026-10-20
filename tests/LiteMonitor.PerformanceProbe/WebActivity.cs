using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LiteMonitor.PerformanceProbe;

/// <summary>
/// 探针自带的最小 Web 客户端：只依赖 BCL，不引入任何新包。
///
/// 用途：复现 §6.2 的 D/E/F/H 组（Web 空闲 / 单个合法网页 / 多网页 + HTTP 轮询）。
/// 客户端只负责建立连接、发送快照请求并把数据读掉，不解析业务内容。
/// </summary>
internal sealed class WebActivity : IDisposable
{
    private readonly List<WebSocketProbeClient> _sockets = new();
    private CancellationTokenSource? _cts;
    private Task? _httpTask;
    private int _httpSnapshotOk;
    private int _httpSnapshotFailed;

    public int Port { get; }
    public string Password { get; }

    /// <summary>HTTP 快照请求成功次数（后台线程写、主线程读，用 Interlocked 同步）。</summary>
    public int HttpSnapshotOk => Volatile.Read(ref _httpSnapshotOk);

    /// <summary>HTTP 快照请求失败次数。</summary>
    public int HttpSnapshotFailed => Volatile.Read(ref _httpSnapshotFailed);

    /// <summary>
    /// WebSocket 广播帧接收次数（证明"活动"这一侧的数据真的在流动）。
    /// 数值来自各连接自己的计数器，不是独立字段，避免出现"恒为 0 的死计数器"。
    /// </summary>
    public int WebSocketFramesReceived
    {
        get
        {
            int total = 0;
            foreach (var s in _sockets) total += s.FramesReceived;
            return total;
        }
    }

    public WebActivity(int port, string password)
    {
        Port = port;
        Password = password;
    }

    /// <summary>建立 N 个已鉴权的 WebSocket 连接（合法网页）。</summary>
    public int OpenWebSockets(int count)
    {
        int opened = 0;
        for (int i = 0; i < count; i++)
        {
            try
            {
                var client = new WebSocketProbeClient(Port, Password);
                if (client.Handshake()) { _sockets.Add(client); opened++; }
                else client.Dispose();
            }
            catch
            {
                // 连接失败不编造计数
            }
        }
        return opened;
    }

    /// <summary>后台按固定频率轮询 /api/snapshot。</summary>
    public void StartHttpPolling(double requestsPerSecond)
    {
        _cts = new CancellationTokenSource();
        int intervalMs = requestsPerSecond <= 0 ? 1000 : (int)Math.Round(1000.0 / requestsPerSecond);
        _httpTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (await HttpSnapshotProbe.GetAsync(Port, Password)) Interlocked.Increment(ref _httpSnapshotOk);
                    else Interlocked.Increment(ref _httpSnapshotFailed);
                }
                catch
                {
                    Interlocked.Increment(ref _httpSnapshotFailed);
                }
                try { await Task.Delay(intervalMs, _cts.Token); } catch { break; }
            }
        });
    }

    public JsonObject ToJson()
    {
        var perSocket = new JsonArray();
        int desyncs = 0;
        foreach (var s in _sockets)
        {
            desyncs += s.Desyncs;
            perSocket.Add(new JsonObject
            {
                ["textFrames"] = s.FramesReceived,
                ["allFrames"] = s.AllFramesReceived,
                ["desyncs"] = s.Desyncs,
            });
        }

        return new JsonObject
        {
            ["webSocketClients"] = _sockets.Count,
            ["httpSnapshotOk"] = HttpSnapshotOk,
            ["httpSnapshotFailed"] = HttpSnapshotFailed,
            ["webSocketFramesReceived"] = WebSocketFramesReceived,
            ["webSocketFramesPerClient"] = perSocket,
            ["webSocketDesyncs"] = desyncs,
            ["webSocketNote"] = "webSocketFramesReceived 是各连接实际收到的**广播文本帧数**；"
                              + "服务端每 1 秒广播一次，60 秒稳态下每个连接应在 60 帧左右。"
                              + "为 0 说明连接没真正收到数据（不是'没数据可发'）。"
                              + "webSocketDesyncs 应恒为 0，非 0 表示帧解析失步、帧计数不可信。",
        };
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _httpTask?.Wait(2000); } catch { }
        _cts?.Dispose();
        foreach (var s in _sockets) s.Dispose();
        _sockets.Clear();
    }
}

/// <summary>最小 WebSocket 客户端：握手 + 持续解析并丢弃收到的帧。</summary>
internal sealed class WebSocketProbeClient : IDisposable
{
    private readonly TcpClient _client = new();
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _reader;
    private int _frames;        // 收到的全部帧
    private int _textFrames;    // 收到的文本帧（服务端广播用的就是文本帧）
    private int _desyncs;       // 帧解析失步次数（应当为 0）
    private byte[] _pending = Array.Empty<byte>();
    private int _pendingLen;

    public WebSocketProbeClient(int port, string password)
    {
        _client.Connect("127.0.0.1", port);
        _stream = _client.GetStream();
        _stream.ReadTimeout = 5000;
        SendHandshake(port, password);
    }

    /// <summary>收到的文本帧数量（服务端每秒广播一次快照）。</summary>
    public int FramesReceived => Volatile.Read(ref _textFrames);

    /// <summary>收到的全部帧数量（含 ping/close 等控制帧）。</summary>
    public int AllFramesReceived => Volatile.Read(ref _frames);

    /// <summary>帧解析失步次数；正常情况恒为 0，非 0 说明帧计数不可信。</summary>
    public int Desyncs => Volatile.Read(ref _desyncs);

    private void SendHandshake(int port, string password)
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        string request = $"GET /?pwd={password} HTTP/1.1\r\n" +
                        $"Host: 127.0.0.1:{port}\r\n" +
                        "Upgrade: websocket\r\n" +
                        "Connection: Upgrade\r\n" +
                        $"Sec-WebSocket-Key: {Convert.ToBase64String(key)}\r\n" +
                        "Sec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        _stream!.Write(bytes, 0, bytes.Length);
        _stream.Flush();
    }

    /// <summary>读取握手响应，确认服务端返回 101 Switching Protocols。</summary>
    public bool Handshake()
    {
        var buffer = new byte[4096];
        var raw = new List<byte>();
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        int headerEnd = -1;
        while (DateTime.UtcNow < deadline)
        {
            int n = _stream!.Read(buffer, 0, buffer.Length);
            if (n <= 0) return false;
            for (int i = 0; i < n; i++) raw.Add(buffer[i]);
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            int idx = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (idx >= 0) { headerEnd = idx + 4; break; }
        }
        if (headerEnd < 0) return false;
        if (!sb.ToString().StartsWith("HTTP/1.1 101", StringComparison.OrdinalIgnoreCase)) return false;

        // ★ 握手响应后面可能已经跟着第一帧广播数据，
        //   必须把它接回帧解析缓冲，否则解析器会从半帧中间开始、失步。
        if (raw.Count > headerEnd)
        {
            int leftover = raw.Count - headerEnd;
            EnsurePendingCapacity(leftover);
            for (int i = 0; i < leftover; i++) _pending[i] = raw[headerEnd + i];
            _pendingLen = leftover;
        }

        // 后台持续读帧，避免服务端发送阻塞
        _cts = new CancellationTokenSource();
        _reader = Task.Run(() => ReadLoop(_cts.Token));
        return true;
    }

    private void ReadLoop(CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int n = _stream!.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;

                // 把读到的字节接在未解析的尾巴后面，再逐帧切出来。
                // 计数的是 **WebSocket 帧**，不是 TCP 读次数。
                EnsurePendingCapacity(_pendingLen + n);
                Buffer.BlockCopy(buffer, 0, _pending, _pendingLen, n);
                _pendingLen += n;

                ParsePending();
            }
        }
        catch
        {
            // 连接结束
        }
    }

    /// <summary>
    /// 从 _pending[0] 开始逐帧切出并计数。
    /// 每切掉一帧就把解析位置往前推，绝不重复解析同一帧（否则会空转计数）。
    /// </summary>
    private void ParsePending()
    {
        int consumed = 0;
        while (TryReadFrame(consumed, out int frameLength, out int opcode))
        {
            if (frameLength <= 0) break;   // 防御：没有进展就退出，绝不空转
            Interlocked.Increment(ref _frames);
            if (opcode == 0x1) Interlocked.Increment(ref _textFrames);
            consumed += frameLength;
        }
        if (consumed > 0)
        {
            int remain = _pendingLen - consumed;
            if (remain > 0) Buffer.BlockCopy(_pending, consumed, _pending, 0, remain);
            _pendingLen = remain > 0 ? remain : 0;
        }
    }

    private void EnsurePendingCapacity(int needed)
    {
        if (_pending.Length >= needed) return;
        int size = Math.Max(needed, Math.Max(_pending.Length * 2, 64 * 1024));
        Array.Resize(ref _pending, size);
    }

    /// <summary>
    /// 从 _pending 的 offset 处尝试切出一帧。
    /// 返回 false 表示数据还不够（等下次读），此时不消费任何字节。
    /// 服务端发的是未掩码帧，掩码位按标准处理。
    /// 长度字段异常时判定为**失步**：丢弃缓冲并计数，绝不把垃圾长度当成帧。
    /// </summary>
    private bool TryReadFrame(int offset, out int frameLength, out int opcode)
    {
        frameLength = 0;
        opcode = 0;
        int available = _pendingLen - offset;
        if (available < 2) return false;

        byte b0 = _pending[offset];
        byte b1 = _pending[offset + 1];
        opcode = b0 & 0x0F;

        long payloadLen = b1 & 0x7F;
        int headerLen = 2;
        if (payloadLen == 126)
        {
            if (available < 4) return false;
            payloadLen = (_pending[offset + 2] << 8) | _pending[offset + 3];
            headerLen = 4;
        }
        else if (payloadLen == 127)
        {
            if (available < 10) return false;
            payloadLen = 0;
            for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | _pending[offset + 2 + i];
            headerLen = 10;
        }
        if ((b1 & 0x80) != 0) headerLen += 4;   // 掩码键（服务端实际不掩码）

        long total = headerLen + payloadLen;
        // 长度字段不合理 = 解析失步。丢弃缓冲并计数，避免负长度导致空转。
        if (payloadLen < 0 || total < headerLen || total > 16 * 1024 * 1024)
        {
            Interlocked.Increment(ref _desyncs);
            _pendingLen = 0;
            return false;
        }
        if (available < total) return false;

        frameLength = (int)total;
        return true;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _client.Close(); } catch { }
        _cts?.Dispose();
    }
}

/// <summary>最小 HTTP 快照请求。</summary>
internal static class HttpSnapshotProbe
{
    public static async Task<bool> GetAsync(int port, string password)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        using var stream = client.GetStream();
        stream.ReadTimeout = 5000;
        string request = $"GET /api/snapshot?pwd={password} HTTP/1.1\r\n" +
                        $"Host: 127.0.0.1:{port}\r\n" +
                        "Connection: close\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        var buffer = new byte[16 * 1024];
        var sb = new StringBuilder();
        while (true)
        {
            int n = await stream.ReadAsync(buffer);
            if (n <= 0) break;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            if (sb.Length > 1 << 20) break; // 防御：不让异常响应撑爆内存
        }
        return sb.ToString().StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase);
    }
}
