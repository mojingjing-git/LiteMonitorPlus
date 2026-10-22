using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [LM-045] 本地回环 HTTP 服务器（**支持重定向**）。
    ///
    /// ★ 为什么不直接复用 <c>PluginExecutorSandboxTests</c> 里的 <c>TinyHttpServer</c>：
    ///   ① 它是 <c>private sealed</c> **嵌套类**（<c>tests/LiteMonitor.Tests/PluginExecutorSandboxTests.cs:954</c>），
    ///      跨文件不可见，而本任务不允许改动其它测试文件；
    ///   ② 它的 <c>TestResponse</c>（同文件 <c>:941</c>）**没有 <c>Location</c> 头**，
    ///      写响应时只输出 Content-Type / Content-Length / Connection
    ///      （同文件 <c>WriteResponseAsync</c>，<c>:1092</c>）⇒ **根本无法表达重定向**。
    ///   因此这里按同一套设计（TcpListener + 自带请求解析与计数）另建一个**唯一命名**的辅助文件，
    ///   只补上重定向测试必需的能力：任意状态码 + 额外响应头（Location）+ 可绑定不同回环地址。
    ///
    /// ★ 只监听回环地址（127.0.0.1 / 127.0.0.2）：不触公网、不需要管理员权限。
    ///   两个地址用来表达"声明主机"与"未声明主机"（Windows 上 127.0.0.0/8 整体都是环回）。
    /// </summary>
    internal sealed class RedirectSandboxServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<RedirectSandboxRequest> _requests = new();
        private readonly object _sync = new();

        /// <summary>构造一台回环服务器。</summary>
        /// <param name="handler">请求 → 响应的处理函数。</param>
        /// <param name="host">绑定地址（默认 127.0.0.1；另一台用 127.0.0.2）。</param>
        public RedirectSandboxServer(Func<RedirectSandboxRequest, RedirectSandboxResponse> handler, string host = "127.0.0.1")
        {
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            Host = host;
            _listener = new TcpListener(IPAddress.Parse(host), 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public Func<RedirectSandboxRequest, RedirectSandboxResponse> Handler { get; set; }

        /// <summary>绑定的回环地址（127.0.0.1 或 127.0.0.2）。</summary>
        public string Host { get; }

        public int Port { get; }

        /// <summary>该服务器的基址（不含路径）。</summary>
        public string Url => "http://" + Host + ":" + Port;

        /// <summary>收到的请求数。</summary>
        public int RequestCount { get { lock (_sync) return _requests.Count; } }

        /// <summary>收到的请求（快照）。</summary>
        public List<RedirectSandboxRequest> Requests { get { lock (_sync) return new List<RedirectSandboxRequest>(_requests); } }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var req = await ReadRequestAsync(stream);
                    if (req == null) return;

                    lock (_sync) _requests.Add(req);

                    var resp = Handler(req);
                    await WriteResponseAsync(stream, req, resp);
                }
            }
            catch { /* 客户端提前放弃是预期内的 */ }
        }

        private static async Task<RedirectSandboxRequest> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var head = new List<byte>();
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0) return null;
                for (int i = 0; i < read; i++) head.Add(buffer[i]);
                headerEnd = IndexOfCrLfCrLf(head);
                if (head.Count > 64 * 1024) return null;
            }

            string text = Encoding.ASCII.GetString(head.ToArray(), 0, headerEnd);
            var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            var req = new RedirectSandboxRequest
            {
                Method = parts.Length > 0 ? parts[0] : "",
                Target = parts.Length > 1 ? parts[1] : ""
            };
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                req.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            if (req.Headers.TryGetValue("Content-Length", out var lenText) && int.TryParse(lenText, out int len) && len > 0)
            {
                var body = new byte[len];
                // ★ 正文可能与请求头在**同一个 TCP 段**里到达 —— 先把已经在缓冲区里的那部分取走，
                //   否则会在这里等一个永远不会到来的"更多数据"（客户端侧表现为 10 s 超时）。
                int bodyOffset = headerEnd + 4;   // "\r\n\r\n" 之后
                int copied = Math.Min(len, head.Count - bodyOffset);
                for (int i = 0; i < copied; i++) body[i] = head[bodyOffset + i];

                int offset = copied;
                while (offset < len)
                {
                    int read = await stream.ReadAsync(body, offset, len - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                req.Body = Encoding.UTF8.GetString(body, 0, offset);
            }
            return req;
        }

        private static int IndexOfCrLfCrLf(List<byte> bytes)
        {
            for (int i = 0; i + 3 < bytes.Count; i++)
            {
                if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n') return i;
            }
            return -1;
        }

        private static async Task WriteResponseAsync(NetworkStream stream, RedirectSandboxRequest req, RedirectSandboxResponse resp)
        {
            string reason = ReasonPhrase(resp.Status);
            var head = new StringBuilder();
            head.Append($"HTTP/1.1 {resp.Status} {reason}\r\n");
            head.Append($"Content-Type: {resp.ContentType}\r\n");
            foreach (var kv in resp.Headers)
            {
                head.Append($"{kv.Key}: {kv.Value}\r\n");
            }
            head.Append($"Content-Length: {resp.Body.Length}\r\n");
            head.Append("Connection: close\r\n\r\n");

            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            await stream.WriteAsync(headBytes, 0, headBytes.Length);
            if (req.Method != "HEAD" && resp.Body.Length > 0)
            {
                await stream.WriteAsync(resp.Body, 0, resp.Body.Length);
            }
            await stream.FlushAsync();
        }

        private static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 301: return "Moved Permanently";
                case 302: return "Found";
                case 303: return "See Other";
                case 307: return "Temporary Redirect";
                case 308: return "Permanent Redirect";
                default: return "Status";
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }

    /// <summary>[LM-045] 回环服务器收到的一个请求（方法 / 目标 / 头 / 正文）。</summary>
    internal sealed class RedirectSandboxRequest
    {
        public string Method = "";
        public string Target = "";
        public string Body = "";
        public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>请求目标里的**路径部分**（去掉查询串；经代理时目标本身是绝对 URL）。</summary>
        public string Path
        {
            get
            {
                string t = Target ?? "";
                int scheme = t.IndexOf("://", StringComparison.Ordinal);
                if (scheme >= 0)
                {
                    int slash = t.IndexOf('/', scheme + 3);
                    t = slash >= 0 ? t.Substring(slash) : "/";
                }
                int q = t.IndexOf('?');
                if (q >= 0) t = t.Substring(0, q);
                return t;
            }
        }

        /// <summary>请求目标里的**主机部分**（经代理时目标本身是绝对 URL）。</summary>
        public string TargetHost
        {
            get
            {
                string t = Target ?? "";
                int scheme = t.IndexOf("://", StringComparison.Ordinal);
                if (scheme < 0) return "";
                string rest = t.Substring(scheme + 3);
                int slash = rest.IndexOf('/');
                string authority = slash >= 0 ? rest.Substring(0, slash) : rest;
                int at = authority.LastIndexOf('@');
                if (at >= 0) authority = authority.Substring(at + 1);
                int colon = authority.IndexOf(':');
                if (colon >= 0) authority = authority.Substring(0, colon);
                return authority;
            }
        }

        /// <summary>请求头里是否出现过给定名字（值非空）。</summary>
        public bool HasHeader(string name)
            => Headers.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v);
    }

    /// <summary>[LM-045] 回环服务器的响应（状态码 / 正文 / 额外响应头，含 Location）。</summary>
    internal sealed class RedirectSandboxResponse
    {
        public int Status = 200;
        public byte[] Body = Array.Empty<byte>();
        public string ContentType = "text/plain; charset=utf-8";
        public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);

        public static RedirectSandboxResponse Text(string s)
            => new RedirectSandboxResponse { Body = Encoding.UTF8.GetBytes(s ?? "") };

        public static RedirectSandboxResponse Json(string s)
            => new RedirectSandboxResponse
            {
                ContentType = "application/json; charset=utf-8",
                Body = Encoding.UTF8.GetBytes(s ?? "")
            };

        /// <summary>构造一个重定向响应（Location 可以是相对地址或绝对地址）。</summary>
        public static RedirectSandboxResponse Redirect(int status, string location)
        {
            var r = new RedirectSandboxResponse { Status = status };
            r.Headers["Location"] = location;
            return r;
        }
    }
}
