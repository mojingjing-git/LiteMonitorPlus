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
    private readonly WebProbeAuth _auth;
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
        _auth = new WebProbeAuth(port, password);
    }

    /// <summary>建立 N 个已鉴权的 WebSocket 连接（合法网页）。</summary>
    public int OpenWebSockets(int count)
    {
        int opened = 0;
        for (int i = 0; i < count; i++)
        {
            try
            {
                // ★ 新协议（S2.2 契约 W3 / W13.5）：WS 握手凭据是**一次性票据**
                //   （TTL 30 秒、用过即废、仅限 WS），票据由主会话换得 ——
                //   所以每个连接都要单独取一张票据，口令不再进 URL。
                string ticket = _auth.RequestTicket();
                var client = new WebSocketProbeClient(Port, ticket);
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
                    // ★ 新协议：快照接口一律要求**主会话 Cookie**（口令不再进 URL）
                    _auth.EnsureSession();
                    int status = await HttpSnapshotProbe.GetAsync(Port, _auth.SessionId);
                    if (status == 401)
                    {
                        // 会话可能已过期（空闲 2 分钟 / 绝对 30 分钟）⇒ 重新登录再试一次
                        _auth.ResetSession();
                        _auth.EnsureSession();
                        status = await HttpSnapshotProbe.GetAsync(Port, _auth.SessionId);
                    }
                    if (status == 200) Interlocked.Increment(ref _httpSnapshotOk);
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

    public WebSocketProbeClient(int port, string ticket)
    {
        _client.Connect("127.0.0.1", port);
        _stream = _client.GetStream();
        _stream.ReadTimeout = 5000;
        SendHandshake(port, ticket);
    }

    /// <summary>收到的文本帧数量（服务端每秒广播一次快照）。</summary>
    public int FramesReceived => Volatile.Read(ref _textFrames);

    /// <summary>收到的全部帧数量（含 ping/close 等控制帧）。</summary>
    public int AllFramesReceived => Volatile.Read(ref _frames);

    /// <summary>帧解析失步次数；正常情况恒为 0，非 0 说明帧计数不可信。</summary>
    public int Desyncs => Volatile.Read(ref _desyncs);

    private void SendHandshake(int port, string ticket)
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        // ★ 新协议：凭据是**一次性票据**（?ticket=），旧的 ?pwd= 已被服务端移除。
        string request = $"GET /ws?ticket={Uri.EscapeDataString(ticket ?? "")} HTTP/1.1\r\n" +
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

/// <summary>
/// 最小 HTTP 快照请求。
/// </summary>
/// <remarks>
/// ★ 新协议（S2.2 契约 W13.5）：凭据是**主会话 Cookie**（<c>lmsid</c>），口令不再进 URL。
/// </remarks>
internal static class HttpSnapshotProbe
{
    /// <summary>
    /// 发起一次快照请求。
    /// </summary>
    /// <returns>HTTP 状态码；连接失败返回 0。</returns>
    public static async Task<int> GetAsync(int port, string sessionId)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        using var stream = client.GetStream();
        stream.ReadTimeout = 5000;
        string request = "GET /api/snapshot HTTP/1.1\r\n" +
                        $"Host: 127.0.0.1:{port}\r\n" +
                        $"Cookie: lmsid={sessionId}\r\n" +
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
        return StatusCodeOf(sb.ToString());
    }

    /// <summary>从原始响应里取 HTTP 状态码（解析不出返回 0）。</summary>
    public static int StatusCodeOf(string response)
    {
        if (string.IsNullOrEmpty(response)) return 0;
        int sp = response.IndexOf(' ');
        if (sp < 0) return 0;
        int start = sp + 1;
        int end = start;
        while (end < response.Length && char.IsDigit(response[end])) end++;
        if (end == start) return 0;
        return int.TryParse(response.Substring(start, end - start), out int code) ? code : 0;
    }
}

/// <summary>
/// 新协议下的探针侧认证：登录换主会话（<c>lmsid</c> + CSRF 令牌），再按需换一次性票据。
/// </summary>
/// <remarks>
/// ★ 只依赖 BCL（探针的既有约束）：不引用主工程类型，自己按契约发报文。
/// ★ L0（未配置口令）同样要登录（空口令登录成功）—— 协议口径与服务端一致。
/// </remarks>
internal sealed class WebProbeAuth
{
    private readonly int _port;
    private readonly string _password;

    /// <summary>主会话 Cookie 值（HttpOnly，服务端会话表里的不透明 ID）。</summary>
    public string SessionId { get; private set; } = "";

    /// <summary>CSRF 令牌（登录响应体下发，只存在内存里）。</summary>
    public string Csrf { get; private set; } = "";

    public WebProbeAuth(int port, string password)
    {
        _port = port;
        _password = password ?? "";
    }

    /// <summary>确保手上有一份有效主会话（没有就登录一次）。</summary>
    public void EnsureSession()
    {
        if (!string.IsNullOrEmpty(SessionId)) return;
        Login();
    }

    /// <summary>丢弃当前会话（用于会话过期后重新登录）。</summary>
    public void ResetSession()
    {
        SessionId = "";
        Csrf = "";
    }

    /// <summary>换一张一次性票据（WS 握手用；用过即废 ⇒ 每次都要新取）。</summary>
    public string RequestTicket()
    {
        EnsureSession();

        string resp = WebProbeHttp.Send(_port, "POST", "/api/auth/ticket", "", SessionId, Csrf, null);
        if (HttpSnapshotProbe.StatusCodeOf(resp) == 401)
        {
            // 会话可能已过期（空闲 2 分钟 / 绝对 30 分钟）⇒ 重新登录再试一次
            ResetSession();
            EnsureSession();
            resp = WebProbeHttp.Send(_port, "POST", "/api/auth/ticket", "", SessionId, Csrf, null);
        }

        if (HttpSnapshotProbe.StatusCodeOf(resp) != 200)
            throw new InvalidOperationException("取票失败: " + FirstLine(resp));

        string ticket = JsonStringValue(WebProbeHttp.BodyOf(resp), "ticket");
        if (string.IsNullOrEmpty(ticket)) throw new InvalidOperationException("取票响应缺少 ticket");
        return ticket;
    }

    private void Login()
    {
        string body = "{\"password\":\"" + JsonEscape(_password) + "\"}";
        string resp = WebProbeHttp.Send(_port, "POST", "/api/auth/login", body, null, null, "application/json");
        if (HttpSnapshotProbe.StatusCodeOf(resp) != 200)
            throw new InvalidOperationException("探针登录失败: " + FirstLine(resp));

        string setCookie = WebProbeHttp.HeaderValue(resp, "Set-Cookie");
        int eq = setCookie.IndexOf('=');
        int semi = setCookie.IndexOf(';');
        SessionId = (eq >= 0 && semi > eq) ? setCookie.Substring(eq + 1, semi - eq - 1) : "";
        Csrf = JsonStringValue(WebProbeHttp.BodyOf(resp), "csrf");

        if (string.IsNullOrEmpty(SessionId)) throw new InvalidOperationException("登录响应缺少 lmsid Cookie");
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

    /// <summary>从 JSON 文本里取一个字符串字段（取不到返回空串）。</summary>
    private static string JsonStringValue(string json, string name)
    {
        if (string.IsNullOrEmpty(json)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String)
                return el.GetString() ?? "";
        }
        catch (System.Text.Json.JsonException) { }
        return "";
    }

    private static string FirstLine(string response)
    {
        int idx = (response ?? "").IndexOf('\n');
        return idx >= 0 ? response.Substring(0, idx).Trim() : (response ?? "").Trim();
    }
}

/// <summary>最小同步 HTTP 客户端（探针用；只依赖 BCL）。</summary>
internal static class WebProbeHttp
{
    /// <summary>发一次请求并把响应读回来（服务器读完会关闭连接）。</summary>
    public static string Send(int port, string method, string path, string body,
        string cookie, string csrf, string contentType)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        using var stream = client.GetStream();
        stream.ReadTimeout = 10000;
        stream.WriteTimeout = 10000;

        var head = new StringBuilder();
        head.Append($"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n");
        if (!string.IsNullOrEmpty(cookie)) head.Append($"Cookie: lmsid={cookie}\r\n");
        if (!string.IsNullOrEmpty(csrf)) head.Append($"X-LiteMonitor-CSRF: {csrf}\r\n");
        if (!string.IsNullOrEmpty(contentType)) head.Append($"Content-Type: {contentType}\r\n");
        head.Append($"Content-Length: {Encoding.UTF8.GetByteCount(body ?? "")}\r\n");
        head.Append("Connection: close\r\n\r\n");
        if (!string.IsNullOrEmpty(body)) head.Append(body);

        byte[] req = Encoding.UTF8.GetBytes(head.ToString());
        try
        {
            stream.Write(req, 0, req.Length);
            stream.Flush();
        }
        catch (IOException)
        {
            // 服务端可能已读完请求头就回了响应并关闭连接（L0 分支不读 body）——
            //   写失败不影响我们把响应读回来。
        }

        var sb = new StringBuilder();
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                int n = stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
                if (sb.Length > 1 << 20) break; // 防御：不让异常响应撑爆内存
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        return sb.ToString();
    }

    /// <summary>取某个响应头的值（找不到返回空串）。</summary>
    public static string HeaderValue(string response, string name)
    {
        foreach (string line in (response ?? "").Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                return t.Substring(name.Length + 1).Trim();
        }
        return "";
    }

    /// <summary>取响应正文（没有正文返回空串）。</summary>
    public static string BodyOf(string response)
    {
        int idx = (response ?? "").IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return idx < 0 ? "" : response.Substring(idx + 4);
    }
}
