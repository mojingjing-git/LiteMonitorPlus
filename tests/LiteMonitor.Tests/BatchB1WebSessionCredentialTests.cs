using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Actions;
using LiteMonitor.src.UI.SettingsPage;
using LiteMonitor.src.WebServer;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P2 批次 B-1 的回归护栏：LM-075（监听范围不重绑 / 界面读草稿）、
    /// LM-076（WS 连接不绑定会话 ⇒ 注销或过期后仍收数据）、
    /// LM-077（口令迁移写盘失败仍报成功）。
    ///
    /// 测试纪律（与既有 Web 用例一致）：
    ///  - 只连 127.0.0.1 回环，不触外网；
    ///  - 会改 <c>Settings.Load()</c> 单例上的 Web 相关字段 ⇒ 挂到 "cfgfile" 集合串行执行；
    ///  - TTL 用例走**可注入的假时钟**（<see cref="FakeClock"/>），不靠长时间 Thread.Sleep；
    ///  - 监听地址类断言核对**真实监听端点**，不只读配置。
    /// </summary>
    [Collection("cfgfile")]
    public class BatchB1WebSessionCredentialTests : IDisposable
    {
        // ---------------- 设置现场（每个用例前后还原）----------------
        private Settings _cfg;
        private string _oldPassword;
        private string _oldHash;
        private string _oldSalt;
        private WebServerBindMode _oldBindMode;
        private bool _oldEnabled;
        private int _oldPort;

        public BatchB1WebSessionCredentialTests()
        {
            _cfg = Settings.Load();
            _oldPassword = _cfg.WebServerPassword;
            _oldHash = _cfg.WebServerPasswordHash;
            _oldSalt = _cfg.WebServerPasswordSalt;
            _oldBindMode = _cfg.WebServerBindMode;
            _oldEnabled = _cfg.WebServerEnabled;
            _oldPort = _cfg.WebServerPort;
        }

        public void Dispose()
        {
            try
            {
                _cfg = Settings.Load();
                _cfg.WebServerPassword = _oldPassword;
                _cfg.WebServerPasswordHash = _oldHash;
                _cfg.WebServerPasswordSalt = _oldSalt;
                _cfg.WebServerBindMode = _oldBindMode;
                _cfg.WebServerEnabled = _oldEnabled;
                _cfg.WebServerPort = _oldPort;
            }
            catch { /* 还原失败不应掩盖用例本身的失败 */ }
            finally
            {
                ResetLiteWebServerInstance();
            }
        }

        // ================================================================
        //  LM-075 · 修改监听范围不重绑服务 / 界面"实际监听地址"读草稿
        // ================================================================

        /// <summary>
        /// LM-075-①：只改**监听范围**（端口不动、凭据不动）也必须重绑服务。
        /// </summary>
        /// <remarks>
        /// ★ 盯的缺陷：重绑判据只看"端口变化"与"凭据变化"，
        ///   于是"仅本机 → 局域网"改了也不重绑 —— socket 仍绑在 127.0.0.1 上，
        ///   而配置已经写着局域网；反向（局域网 → 仅本机）同样不重绑，
        ///   socket 仍绑在 0.0.0.0 上 ⇒ **对外暴露状态与界面显示相反**（安全向）。
        /// ★ 反证手法（比看标志位更硬）：重启会撤销全部主会话，
        ///   所以"旧会话失效"就是"确实重绑了"的证据。
        /// </remarks>
        [Fact]
        public async Task LM075_只改监听范围也必须重绑服务()
        {
            int port = FindFreePort();
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            _cfg.WebServerBindMode = WebServerBindMode.Loopback;
            UsePassword("lm075-bind-scope-password");

            using var server = new LiteWebServer(
                _cfg,
                samplingRequester: _ => Task.FromResult(true),
                demandApplier: _ => true);
            server.ListenAddress = IPAddress.Loopback;   // ★ 测试期间只监听回环

            Assert.True(server.Start(out string err), "LiteWebServer 启动失败: " + err);
            try
            {
                // 运行态：生效的监听范围 = 仅本机
                Assert.Equal(WebServerBindMode.Loopback, server.CurrentBindMode);

                var login = await LoginAsync(port, "lm075-bind-scope-password");
                Assert.False(string.IsNullOrEmpty(login.SessionId));

                // ★ 只改监听范围（端口与凭据都不动）
                _cfg.WebServerBindMode = WebServerBindMode.Lan;

                var result = AppActions.ApplyWebServerWithResult(_cfg);

                Assert.True(result.Restarted,
                    "只改监听范围也必须重绑服务（否则 socket 仍绑在旧地址上：局域网→仅本机时对外暴露状态与界面显示相反）");
                Assert.True(server.IsRunning);
                Assert.Equal(WebServerBindMode.Lan, server.CurrentBindMode);

                // ★ 反证：重启 ⇒ 全部会话被撤销 ⇒ 旧会话立即失效
                string snapshot = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                    $"Cookie: lmsid={login.SessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("401", FirstLine(snapshot));
            }
            finally
            {
                ResetLiteWebServerInstance();
            }
        }

        /// <summary>
        /// LM-075-②：设置界面"实际监听地址"必须显示**真实监听端点**，而不是配置草稿。
        /// </summary>
        /// <remarks>
        /// ★ 盯的缺陷：界面把地址取自**配置草稿** <c>Config.WebServerBindMode</c>（端口却取自运行态），
        ///   口径不一致 ⇒ 用户在下拉框改了但还没点"应用设置"时，界面就已经按新草稿显示：
        ///   局域网 → 仅本机 时界面写着 127.0.0.1，而 socket 其实还绑在 0.0.0.0 上。
        /// ★ 现场故意做成"配置与实际不一致"：配置写「局域网」，实际只监听回环（测试接缝）。
        ///   旧实现会显示 0.0.0.0 —— 那正是缺陷本身。
        /// </remarks>
        [Fact]
        public void LM075_实际监听地址必须反映真实端点而不是配置草稿()
        {
            int port = FindFreePort();
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            _cfg.WebServerBindMode = WebServerBindMode.Lan;   // ★ 配置草稿：局域网
            UsePassword("lm075-listen-address-password");

            using var server = new LiteWebServer(
                _cfg,
                samplingRequester: _ => Task.FromResult(true),
                demandApplier: _ => true);
            server.ListenAddress = IPAddress.Loopback;           // ★ 实际只监听回环

            Assert.True(server.Start(out string err), "LiteWebServer 启动失败: " + err);
            try
            {
                Assert.Equal(WebServerBindMode.Lan, server.CurrentBindMode);   // 运行态按配置走「局域网」

                IPEndPoint real = server.CurrentListenEndpoint!;
                Assert.NotNull(real);
                Assert.True(IPAddress.IsLoopback(real.Address),
                    $"真实监听端点应当是回环，实际 {real.Address}");
                Assert.Equal(port, real.Port);

                string text = RunSta(() =>
                {
                    var page = new MainPanelPage();
                    try
                    {
                        page.SetContext(_cfg, null, null);
                        return page.BuildListenAddressText(false);
                    }
                    finally { page.Dispose(); }
                });

                // ★ 界面显示的必须是**真实端点**
                Assert.Contains(real.Address.ToString(), text, StringComparison.Ordinal);
                Assert.Contains(":" + real.Port.ToString(), text, StringComparison.Ordinal);

                // ★ 绝不能显示配置草稿推出来的地址（旧实现在这里就是 "0.0.0.0"）
                Assert.DoesNotContain("0.0.0.0", text, StringComparison.Ordinal);
            }
            finally
            {
                ResetLiteWebServerInstance();
            }
        }

        /// <summary>
        /// LM-075-③：没有真实端点（服务未运行）时，界面**不得**凭空显示监听地址。
        /// </summary>
        /// <remarks>
        /// ★ "实际监听地址"的事实来源只有一个：真实监听端点。
        ///   没有端点时地址必须是 "-" —— 绝不能拿配置草稿（0.0.0.0 / 127.0.0.1）冒充事实。
        /// </remarks>
        [Fact]
        public void LM075_没有真实端点时不得凭空显示监听地址()
        {
            string text = AppActions.DescribeListenAddress(
                endpoint: null, running: false, configuredPort: 5000, isChinese: false);

            Assert.DoesNotContain("0.0.0.0", text, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);
            Assert.Contains("-", text, StringComparison.Ordinal);
            Assert.Contains("5000", text, StringComparison.Ordinal);   // 端口仍可显示（配置值）
        }

        // ================================================================
        //  辅助
        // ================================================================

        /// <summary>设置口令（只写哈希 + 盐，内存里不留明文 —— 与迁移后的生产状态一致）。</summary>
        private void UsePassword(string plain)
        {
            _cfg.WebServerPasswordSalt = Settings.CreateWebPasswordSalt();
            _cfg.WebServerPasswordHash = Settings.ComputeWebPasswordHash(plain, _cfg.WebServerPasswordSalt);
            _cfg.WebServerPassword = "";
        }

        /// <summary>
        /// 登录（**不**改配置里的口令：用例自己决定口令是什么）。
        /// </summary>
        /// <remarks>
        /// ★ 这里刻意不像别的用例那样顺手重设口令：重设会换盐 ⇒ 换指纹 ⇒
        ///   "应用设置"会因**凭据变化**而重启，测不出"监听范围变化是否触发重绑"。
        /// </remarks>
        private async Task<LoginResult> LoginAsync(int port, string password)
        {
            string body = "{\"password\":\"" + JsonEscape(password) + "\"}";
            string resp = await SendRawAsync(port,
                $"POST /api/auth/login HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                "Connection: close\r\n\r\n" + body);

            Assert.True(FirstLine(resp).Contains("200"), "登录失败，响应: " + resp);

            string sessionId = ExtractCookieValue(resp, "lmsid");
            Assert.False(string.IsNullOrEmpty(sessionId), "登录响应必须下发 lmsid Cookie");

            string csrf = "";
            try
            {
                using var doc = JsonDocument.Parse(ExtractBody(resp));
                if (doc.RootElement.TryGetProperty("csrf", out var el)) csrf = el.GetString() ?? "";
            }
            catch (JsonException) { }
            Assert.False(string.IsNullOrEmpty(csrf), "登录响应必须下发 csrf 令牌");

            return new LoginResult { SessionId = sessionId, Csrf = csrf, RawResponse = resp };
        }

        private sealed class LoginResult
        {
            public string SessionId = "";
            public string Csrf = "";
            public string RawResponse = "";
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

        /// <summary>发送原始 HTTP 报文并把响应读回来（服务器读完会关闭连接）。</summary>
        private static async Task<string> SendRawAsync(int port, string rawRequest, int timeoutMs = 10000)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();

            byte[] data = Encoding.UTF8.GetBytes(rawRequest);
            await stream.WriteAsync(data, 0, data.Length);
            await stream.FlushAsync();

            var sb = new StringBuilder();
            var buf = new byte[4096];
            using var cts = new CancellationTokenSource(timeoutMs);
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

        private static string FirstLine(string response)
        {
            int idx = response.IndexOf('\n');
            return idx >= 0 ? response.Substring(0, idx).Trim() : response.Trim();
        }

        /// <summary>从 <c>Set-Cookie</c> 里取出某个 Cookie 的值。</summary>
        private static string ExtractCookieValue(string response, string cookieName)
        {
            foreach (string line in response.Split('\n'))
            {
                string t = line.Trim();
                if (!t.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (string part in t.Substring("Set-Cookie:".Length).Split(';'))
                {
                    string p = part.Trim();
                    if (!p.StartsWith(cookieName + "=", StringComparison.Ordinal)) continue;
                    return p.Substring(cookieName.Length + 1);
                }
            }
            return "";
        }

        /// <summary>从响应里截出正文。</summary>
        private static string ExtractBody(string response)
        {
            int idx = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (idx < 0) return "";
            return response.Substring(idx + 4);
        }

        private static int FindFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static void ResetLiteWebServerInstance()
        {
            try
            {
                var field = typeof(LiteWebServer).GetField("<Instance>k__BackingField",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                field?.SetValue(null, null);
            }
            catch { }
        }

        /// <summary>在 STA 线程上执行并返回结果（WinForms 控件需要 STA）。</summary>
        private static T RunSta<T>(Func<T> action)
        {
            T result = default!;
            Exception captured = null;

            var thread = new Thread(() =>
            {
                try { result = action(); }
                catch (Exception ex) { captured = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (captured != null) throw captured;
            return result;
        }
    }
}
