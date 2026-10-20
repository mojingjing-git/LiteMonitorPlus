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

    public int Port { get; }
    public string Password { get; }

    /// <summary>HTTP 快照请求成功次数。</summary>
    public int HttpSnapshotOk { get; private set; }

    /// <summary>HTTP 快照请求失败次数。</summary>
    public int HttpSnapshotFailed { get; private set; }

    /// <summary>WebSocket 广播帧接收次数（证明连接是活的）。</summary>
    public int WebSocketFramesReceived { get; private set; }

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
                    if (await HttpSnapshotProbe.GetAsync(Port, Password)) HttpSnapshotOk++;
                    else HttpSnapshotFailed++;
                }
                catch
                {
                    HttpSnapshotFailed++;
                }
                try { await Task.Delay(intervalMs, _cts.Token); } catch { break; }
            }
        });
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["webSocketClients"] = _sockets.Count,
            ["httpSnapshotOk"] = HttpSnapshotOk,
            ["httpSnapshotFailed"] = HttpSnapshotFailed,
            ["webSocketFramesReceived"] = WebSocketFramesReceived,
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

/// <summary>最小 WebSocket 客户端：握手 + 持续读帧丢弃。</summary>
internal sealed class WebSocketProbeClient : IDisposable
{
    private readonly TcpClient _client = new();
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _reader;
    private int _frames;

    public WebSocketProbeClient(int port, string password)
    {
        _client.Connect("127.0.0.1", port);
        _stream = _client.GetStream();
        _stream.ReadTimeout = 5000;
        SendHandshake(port, password);
    }

    public int FramesReceived => Volatile.Read(ref _frames);

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
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            int n = _stream!.Read(buffer, 0, buffer.Length);
            if (n <= 0) return false;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            if (sb.ToString().Contains("\r\n\r\n")) break;
        }
        if (!sb.ToString().StartsWith("HTTP/1.1 101", StringComparison.OrdinalIgnoreCase)) return false;

        // 后台持续读帧，避免服务端发送阻塞
        _cts = new CancellationTokenSource();
        _reader = Task.Run(() => ReadLoop(_cts.Token));
        return true;
    }

    private void ReadLoop(CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int n = _stream!.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                Interlocked.Increment(ref _frames);
            }
        }
        catch
        {
            // 连接结束
        }
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
