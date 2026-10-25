using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────────────────────
// WebProbe —— 对 LiteMonitor 的 Web/WS 服务做协议级端到端探测。
//
// 用法: WebProbe <assemblyPath> <settingsDir> <label>
//   assemblyPath : 目标程序集（fork 的 LiteMonitor++.dll 或上游构建产物）
//   settingsDir  : settings.json 的沙箱目录
//   label        : 输出标签
//
// 设计要点：全部通过反射驱动**真实的产品代码**（Settings / LiteWebServer /
// WebSessionManager），只替换「用哪份配置」与「连哪个端口」两个接缝，
// 不复制、不改写任何产品逻辑。请求一律裸 TCP 手写字节，避开任何客户端库的
// 规范化行为，才能真正测到「头名大小写」「冒号后空格」这类差异。
// ─────────────────────────────────────────────────────────────────────────────

static class Probe
{
    static int _port;
    static string _label = "?";
    static int _caseNo;

    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: WebProbe <assemblyPath> <settingsDir> <label>");
            return 2;
        }

        string assemblyPath = Path.GetFullPath(args[0]);
        string settingsDir = Path.GetFullPath(args[1]);
        _label = args[2];

        Directory.CreateDirectory(settingsDir);
        _port = FreePort();

        Assembly asm = Assembly.LoadFrom(assemblyPath);
        Console.WriteLine($"### {_label}");
        Console.WriteLine($"assembly      : {assemblyPath}");
        Console.WriteLine($"baseDirectory : {AppContext.BaseDirectory}");
        Console.WriteLine($"port          : {_port}");

        // ── 接缝 1：配置目录 ────────────────────────────────────────────────
        // fork 有 SettingsHelper.PathOverride；上游没有，只能靠 AppContext.BaseDirectory。
        Type helperType = asm.GetType("LiteMonitor.SettingsHelper");
        bool hasOverride = false;
        if (helperType != null)
        {
            // PathOverride 可能是字段也可能是属性（fork 上是 internal static 属性）
            FieldInfo pf = helperType.GetField("PathOverride",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (pf != null)
            {
                pf.SetValue(null, settingsDir);
                hasOverride = true;
            }
            else
            {
                PropertyInfo pp = helperType.GetProperty("PathOverride",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (pp != null && pp.CanWrite)
                {
                    pp.SetValue(null, settingsDir);
                    hasOverride = true;
                }
            }
        }
        Console.WriteLine($"PathOverride  : {(hasOverride ? "已设置 -> " + settingsDir : "不存在（上游，走 BaseDirectory）")}");

        // ── 接缝 2：配置内容（只写目标 Settings 类型真实存在的字段）──────
        Type settingsType = asm.GetType("LiteMonitor.Settings");
        if (settingsType == null) { Console.Error.WriteLine("找不到 Settings 类型"); return 3; }

        var json = BuildSettingsJson(settingsType);
        File.WriteAllText(Path.Combine(settingsDir, "settings.json"), json, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "settings.json"), json, new UTF8Encoding(false));
        Console.WriteLine($"settings.json : {json.Replace("\r\n", " ").Replace("\n", " ")}");

        // ── 启动真实服务 ───────────────────────────────────────────────────
        object cfg = settingsType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
                                 .Invoke(null, new object[] { false });

        Type serverType = asm.GetType("LiteMonitor.src.WebServer.LiteWebServer");
        if (serverType == null) { Console.Error.WriteLine("找不到 LiteWebServer 类型"); return 3; }

        object server = Activator.CreateInstance(serverType, new object[] { cfg });
        MethodInfo start = serverType.GetMethod("Start");
        object[] startArgs = { null };
        bool started = (bool)start.Invoke(server, startArgs);
        string err = startArgs[0] as string;
        Console.WriteLine($"Start()       : {started}{(string.IsNullOrEmpty(err) ? "" : "  err=" + err)}");
        if (!started) return 4;

        try
        {
            RunMatrix();
        }
        finally
        {
            try { serverType.GetMethod("Stop")?.Invoke(server, null); } catch { }
            try { (server as IDisposable)?.Dispose(); } catch { }
        }

        Console.WriteLine($"### {_label} 结束");
        return 0;
    }

    // ── 矩阵 ───────────────────────────────────────────────────────────────
    static void RunMatrix()
    {
        Console.WriteLine();
        Console.WriteLine("case_id\tcase_note\tstatus_line");
        Console.WriteLine("---");

        // 基准：完全标准的握手
        {
            var raw = Send(Ws(HostHeader(), "Upgrade: websocket", "Sec-WebSocket-Key", StdKey(), null, false, true));
            Console.WriteLine("=== RAW C1 ===");
            Console.WriteLine(raw.full.Replace("\r\n", "\\n"));
            Console.WriteLine("=== END RAW ===");
        }
        Emit("C1", "基准（标准头）", Ws(HostHeader(), "Upgrade: websocket", "Sec-WebSocket-Key", StdKey(), null, false, true));
        // #589：Sec-WebSocket-Key 头名大小写
        Emit("C2", "#589 头名全小写", Ws(HostHeader(), "Upgrade: websocket", "sec-websocket-key", StdKey(), null, false, true));
        Emit("C3", "#589 头名全大写", Ws(HostHeader(), "Upgrade: websocket", "SEC-WEBSOCKET-KEY", StdKey(), null, false, true));
        Emit("C4", "#589 头名混合大小写", Ws(HostHeader(), "Upgrade: websocket", "Sec-Websocket-Key", StdKey(), null, false, true));
        // Upgrade 头的 OWS 容忍度（RFC 7230 允许冒号后 0..N 个空格）
        Emit("C5", "Upgrade 无空格", Ws(HostHeader(), "Upgrade:websocket", "Sec-WebSocket-Key", StdKey(), null, false, true));
        Emit("C6", "Upgrade 两个空格", Ws(HostHeader(), "Upgrade:  websocket", "Sec-WebSocket-Key", StdKey(), null, false, true));
        // #580：反向代理场景（Host 变成外部域名）
        Emit("C7", "#580 Host=隧道域名", Ws("tunnel.example.com", "Upgrade: websocket", "Sec-WebSocket-Key", StdKey(), null, false, true));
        // 跨源
        Emit("C8", "Origin=外部站点", Ws(HostHeader(), "Upgrade: websocket", "Sec-WebSocket-Key", StdKey(), "https://evil.example", false, true));
        // Key 形态非法 / 重复
        Emit("C9", "Key 形态非法", Ws(HostHeader(), "Upgrade: websocket", "Sec-WebSocket-Key", "not-base64!!", null, false, true));
        Emit("C10", "Key 头重复", Ws(HostHeader(), "Upgrade: websocket", "Sec-WebSocket-Key", StdKey(), null, true, true));
        // 票据缺失（fork 的 WS 强制一次性票据）
        Emit("C11", "无票据", Ws(HostHeader(), "Upgrade: websocket", "Sec-WebSocket-Key", StdKey(), null, false, false));
        // 代理头是否被采信
        Emit("C12", "X-Forwarded-Host=隧道域名", Ws(HostHeader(), "Upgrade: websocket", "Sec-WebSocket-Key", StdKey(), null, false, true, true));

        // 附：登录链路可用性（fork 的票据来源）
        var login = Http("POST", "/api/auth/login", HostHeader(), null, null, "{\"password\":\"\"}");
        Console.WriteLine($"LOGIN\t/api/auth/login\t{login.status}");

        // 附：Host 判定诊断（用普通 HTTP 路径，同一套 Host/Origin 校验）
        Console.WriteLine();
        Console.WriteLine("host_diag\tvalue\tstatus\tbody");
        foreach (var hv in new string[]
        {
            "127.0.0.1:" + _port, "127.0.0.1", "localhost:" + _port, "localhost",
            "[::1]:" + _port, "127.0.0.1:1", "127.0.0.1:9999", ""
        })
        {
            string req = $"GET /api/snapshot HTTP/1.1\r\n"
                       + (hv.Length > 0 ? $"Host: {hv}\r\n" : "")
                       + "Connection: close\r\n\r\n";
            var r = Send(req);
            string st = string.IsNullOrEmpty(r.status) ? "<无响应>" : r.status;
            Console.WriteLine($"H\t{hv}\t{st}\t{BodyOf(r.full)}");
        }

        // 附：WS 路径的 Host 变体（同样的头，只是走 Upgrade 分流）
        Console.WriteLine();
        Console.WriteLine("ws_host_diag\tvalue\tstatus\tbody");
        foreach (var hv in new string[]
        {
            "127.0.0.1:" + _port, "127.0.0.1", "localhost:" + _port, ""
        })
        {
            string req = $"GET /ws HTTP/1.1\r\n"
                       + (hv.Length > 0 ? $"Host: {hv}\r\n" : "")
                       + "Upgrade: websocket\r\n"
                       + "Connection: Upgrade\r\n"
                       + $"Sec-WebSocket-Key: {StdKey()}\r\n"
                       + "Sec-WebSocket-Version: 13\r\n\r\n";
            var r = Send(req);
            string st = string.IsNullOrEmpty(r.status) ? "<无响应>" : r.status;
            Console.WriteLine($"W\t{hv}\t{st}\t{BodyOf(r.full)}");
        }
    }

    // ── 请求构造 ───────────────────────────────────────────────────────────
    static string HostHeader() => "127.0.0.1:" + _port;

    static string StdKey() => "dGhlIHNhbXBsZSBub25jZQ=="; // RFC 6455 示例：16 字节 Base64

    static string Ws(string host, string upgradeLine, string keyHeader, string keyValue,
                     string origin, bool duplicateKey, bool withTicket, bool xfwd = false)
    {
        string ticket = withTicket ? AcquireTicket() : "";
        var sb = new StringBuilder();
        sb.Append($"GET /ws{(string.IsNullOrEmpty(ticket) ? "" : "?ticket=" + ticket)} HTTP/1.1\r\n");
        sb.Append($"Host: {host}\r\n");
        sb.Append($"{upgradeLine}\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append($"{keyHeader}: {keyValue}\r\n");
        if (duplicateKey) sb.Append($"{keyHeader}: {keyValue}\r\n");
        sb.Append("Sec-WebSocket-Version: 13\r\n");
        if (!string.IsNullOrEmpty(origin)) sb.Append($"Origin: {origin}\r\n");
        if (xfwd) sb.Append("X-Forwarded-Host: tunnel.example.com\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    // fork：登录 -> 取 CSRF -> 换一次性票据；上游没有这套，直接返回空。
    static string AcquireTicket()
    {
        var login = Http("POST", "/api/auth/login", HostHeader(), null, null, "{\"password\":\"\"}");
        if (login.status.Contains("200") == false) { Console.WriteLine($"  [ticket] login 非 200: {login.status}"); return ""; }

        string csrf = ExtractJson(login.body, "csrf");
        string cookie = ExtractSetCookie(login.full, "lmsid");
        Console.WriteLine($"  [ticket] login=200 body='{login.body.Trim()}' cookie='{cookie}' csrf='{csrf}'");
        if (string.IsNullOrEmpty(csrf) || string.IsNullOrEmpty(cookie)) { Console.WriteLine("  [ticket] csrf/cookie 解析失败"); return ""; }

        var t = Http("POST", "/api/auth/ticket", HostHeader(), cookie, csrf, null);
        Console.WriteLine($"  [ticket] ticket响应 status={t.status} body='{t.body.Trim()}'");
        if (t.status.Contains("200") == false) return "";
        return ExtractJson(t.body, "ticket");
    }

    static (string status, string full, string body) Http(string method, string path, string host,
        string cookie, string csrf, string body)
    {
        var sb = new StringBuilder();
        sb.Append($"{method} {path} HTTP/1.1\r\n");
        sb.Append($"Host: {host}\r\n");
        if (!string.IsNullOrEmpty(cookie)) sb.Append($"Cookie: {cookie}\r\n");
        if (!string.IsNullOrEmpty(csrf)) sb.Append($"X-LiteMonitor-CSRF: {csrf}\r\n");
        if (!string.IsNullOrEmpty(body))
        {
            sb.Append("Content-Type: application/json\r\n");
            sb.Append($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
        }
        sb.Append("Connection: close\r\n\r\n");
        if (!string.IsNullOrEmpty(body)) sb.Append(body);

        var res = Send(sb.ToString());
        int sep = res.full.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string payload = sep >= 0 ? res.full.Substring(sep + 4) : "";
        return (res.status, res.full, payload);
    }

    static void Emit(string id, string note, string requestText)
    {
        var res = Send(requestText);
        string status = string.IsNullOrEmpty(res.status) ? "<无响应/静默关闭>" : res.status;
        string body = BodyOf(res.full);
        if (body.Length > 120) body = body.Substring(0, 120) + "…";
        Console.WriteLine($"{id}\t{note}\t{status}\t{body}");
    }

    static string BodyOf(string full)
    {
        int sep = full.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (sep < 0) return "";
        return full.Substring(sep + 4).Replace("\r\n", " ").Replace("\n", " ").Trim();
    }

    // ── 裸 TCP 收发 ────────────────────────────────────────────────────────
    static (string status, string full) Send(string requestText)
    {
        var sb = new StringBuilder();
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, _port);
            client.ReceiveTimeout = 1200;
            client.SendTimeout = 1500;

            using NetworkStream ns = client.GetStream();
            byte[] payload = Encoding.UTF8.GetBytes(requestText);
            ns.Write(payload, 0, payload.Length);
            ns.Flush();

            var buf = new byte[8192];
            var acc = new StringBuilder();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool headersDone = false;
            try
            {
                while (sw.ElapsedMilliseconds < 3000)
                {
                    int n = ns.Read(buf, 0, buf.Length);
                    if (n <= 0) break;                 // 对端关闭
                    acc.Append(Encoding.UTF8.GetString(buf, 0, n));
                    if (!headersDone && acc.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        headersDone = true;
                        // 101 表示已升级，后续是二进制帧流，不再等正文
                        if (acc.ToString().StartsWith("HTTP/1.1 101", StringComparison.Ordinal)) break;
                    }
                }
            }
            catch (IOException) { /* 超时/断开：保留已读部分 */ }
            catch (SocketException) { }

            string full = acc.ToString();
            int eol = full.IndexOf("\r\n", StringComparison.Ordinal);
            string status = eol > 0 ? full.Substring(0, eol) : "";
            return (status, full);
        }
        catch (Exception ex)
        {
            sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            return ("<异常> " + sb, "");
        }
    }

    // ── 小工具 ─────────────────────────────────────────────────────────────
    static string ExtractJson(string body, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty(key, out var el)) return el.GetString() ?? "";
        }
        catch { }
        return "";
    }

    static string ExtractSetCookie(string full, string name)
    {
        foreach (string line in full.Split('\n'))
        {
            string t = line.Trim();
            if (!t.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase)) continue;
            int eq = t.IndexOf('=');
            if (eq < 0) continue;
            string k = t.Substring("Set-Cookie:".Length, eq - "Set-Cookie:".Length).Trim();
            if (!string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) continue;
            int semi = t.IndexOf(';', eq);
            string v = semi > 0 ? t.Substring(eq + 1, semi - eq - 1) : t.Substring(eq + 1);
            return k + "=" + v;   // ★ 必须是完整的 "name=value"，服务端按 Cookie 头解析
        }
        return "";
    }

    static string BuildSettingsJson(Type settingsType)
    {
        var parts = new List<string>();
        bool Has(string p) => settingsType.GetProperty(p, BindingFlags.Public | BindingFlags.Instance) != null;

        if (Has("WebServerEnabled")) parts.Add("\"WebServerEnabled\": true");
        if (Has("WebServerPort")) parts.Add($"\"WebServerPort\": {_port}");
        // 只有 fork 有 BindMode；Loopback = 1（避免上游/存量语义落到 Lan）
        if (Has("WebServerBindMode")) parts.Add("\"WebServerBindMode\": 1");
        if (Has("WebServerPassword")) parts.Add("\"WebServerPassword\": \"\"");
        return "{" + string.Join(",", parts) + "}";
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
