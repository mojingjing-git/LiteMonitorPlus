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

        // settings.json / settings.json.bak 的沙箱（LM-077 的迁移用例要真的动文件）
        private static readonly string MainPath = SettingsHelper.FilePath;
        private static readonly string BackupPath = Path.Combine(
            Path.GetDirectoryName(SettingsHelper.FilePath) ?? ".", "settings.json.bak");
        private readonly string _sandbox;

        public BatchB1WebSessionCredentialTests()
        {
            _cfg = Settings.Load();
            _oldPassword = _cfg.WebServerPassword;
            _oldHash = _cfg.WebServerPasswordHash;
            _oldSalt = _cfg.WebServerPasswordSalt;
            _oldBindMode = _cfg.WebServerBindMode;
            _oldEnabled = _cfg.WebServerEnabled;
            _oldPort = _cfg.WebServerPort;

            _sandbox = Path.Combine(Path.GetTempPath(), "lmsettings-b1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sandbox);

            // 迁移类用例会真的改写 settings.json ⇒ 先把原文件备份到沙箱
            if (File.Exists(MainPath)) File.Copy(MainPath, Path.Combine(_sandbox, "main.json"), true);
            if (File.Exists(BackupPath)) File.Copy(BackupPath, Path.Combine(_sandbox, "backup.json"), true);
        }

        public void Dispose()
        {
            try
            {
                SettingsHelper.GlobalBlockSave = false;
                RestoreSettingsFiles();

                _cfg = Settings.Load(forceReload: true);
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
                try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, true); } catch { }
            }
        }

        /// <summary>把 settings.json / settings.json.bak 还原成用例开始前的样子。</summary>
        private void RestoreSettingsFiles()
        {
            string savedMain = Path.Combine(_sandbox, "main.json");
            string savedBackup = Path.Combine(_sandbox, "backup.json");

            try
            {
                if (File.Exists(savedMain)) File.Copy(savedMain, MainPath, true);
                else if (File.Exists(MainPath)) File.Delete(MainPath);
            }
            catch { }

            try
            {
                if (File.Exists(savedBackup)) File.Copy(savedBackup, BackupPath, true);
                else if (File.Exists(BackupPath)) File.Delete(BackupPath);
            }
            catch { }
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

        /// <summary>
        /// LM-075-④：IPv6 监听地址的显示文本必须带方括号，且端口正确。
        /// </summary>
        /// <remarks>
        /// ★ 局域网模式下 <c>LiteWebServer.Start</c> 建的是 <c>IPAddress.IPv6Any</c>（DualMode，
        ///   同时接受 IPv4 连接）⇒ 真实端点的地址是 <c>::</c>。
        /// ★ 不加方括号就会拼出 ":::8080" —— 用户一眼看过去像非法地址。
        ///   IPv4 维持原样（127.0.0.1:8080），不得回归。
        /// </remarks>
        [Fact]
        public void LM075_IPv6监听地址的显示文本必须带方括号且端口正确()
        {
            var v6 = new IPEndPoint(IPAddress.IPv6Any, 8080);
            string text6 = AppActions.DescribeListenAddress(
                endpoint: v6, running: true, configuredPort: 5000, isChinese: false);

            Assert.Contains("[::]:8080", text6, StringComparison.Ordinal);
            Assert.DoesNotContain(":::", text6, StringComparison.Ordinal);

            // ★ IPv4 的写法不得被牵连
            var v4 = new IPEndPoint(IPAddress.Loopback, 8080);
            string text4 = AppActions.DescribeListenAddress(
                endpoint: v4, running: true, configuredPort: 5000, isChinese: false);

            Assert.Contains("127.0.0.1:8080", text4, StringComparison.Ordinal);
            Assert.DoesNotContain("[", text4, StringComparison.Ordinal);
        }

        // ================================================================
        //  LM-076 · WS 连接不绑定会话 ⇒ 注销 / 过期后仍在收监控数据
        // ================================================================
        /// <summary>
        /// LM-076-①：注销之后，**已建立**的 WebSocket 不得再收到监控数据。
        /// </summary>
        /// <remarks>
        /// ★ 盯的缺陷：WS 连接只记"服务代次"，不记"属于哪个会话"；
        ///   <c>RevokeSession</c> 只删会话记录、<b>完全不动</b>已建立的 WS
        ///   ⇒ 注销只挡住了后续 HTTP 请求，挡不住已在推送的 WS 数据流。
        /// ★ 反证手法：注销**之前**必须真的收到过一帧（证明这条连接是活的），
        ///   否则"注销后没数据"可能只是因为它本来就没在推数据（假绿）。
        /// </remarks>
        [Fact]
        public async Task LM076_注销后已建立的WebSocket不再收到数据()
        {
            int port = FindFreePort();
            _cfg.WebServerEnabled = true;
            _cfg.WebServerPort = port;
            _cfg.WebServerBindMode = WebServerBindMode.Loopback;
            UsePassword("lm076-logout-password");

            using var server = new LiteWebServer(
                _cfg,
                samplingRequester: _ => Task.FromResult(true),
                demandApplier: _ => true);
            server.ListenAddress = IPAddress.Loopback;

            Assert.True(server.Start(out string err), "LiteWebServer 启动失败: " + err);
            try
            {
                var login = await LoginAsync(port, "lm076-logout-password");
                string ticket = await RequestTicketAsync(port, login.SessionId, login.Csrf);
                Assert.False(string.IsNullOrEmpty(ticket));

                using var ws = new WsClient();
                await ws.ConnectAsync(port, ticket);
                Assert.Contains("101", FirstLine(ws.HandshakeResponse));

                // ★ 反证：注销之前这条连接**真的在收监控数据**
                Assert.Equal(WsReadResult.Frame, await ws.ReadFrameAsync(15000));
                Assert.True(await WaitForAsync(() => server.AuthenticatedConnectionCount == 1, 5000),
                    "已鉴权的 WS 连接应当计入有效需求");

                // 注销
                string resp = await SendRawAsync(port, BuildLogoutRequest(port, login.SessionId, login.Csrf));
                Assert.Contains("204", FirstLine(resp));

                // ★ 已建立的 WS 必须被服务端断开（数据流停止）
                Assert.Equal(WsReadResult.Closed, await ReadUntilClosedAsync(ws, 15000));
                // ★ 断开之后不再有任何数据
                Assert.Equal(WsReadResult.Closed, await ws.ReadFrameAsync(3000));
                Assert.Equal(0, server.AuthenticatedConnectionCount);
            }
            finally
            {
                ResetLiteWebServerInstance();
            }
        }

        /// <summary>
        /// LM-076-②：会话**超过空闲期限**后，已建立的 WS 同样不得再收到监控数据。
        /// </summary>
        /// <remarks>
        /// ★ 会话过期同样会泄露：只补"注销"那一处是不够的。
        /// ★ 用**可注入的假时钟**制造过期（不靠长时间 Thread.Sleep）。
        /// </remarks>
        [Fact]
        public async Task LM076_会话空闲超过空闲期限后已建立的WebSocket不再收到数据()
        {
            using var fx = new ManagerFixture(providerThrows: false);
            UsePassword("lm076-idle-password");

            var login = await LoginAsync(fx.Port, "lm076-idle-password");
            string ticket = await RequestTicketAsync(fx.Port, login.SessionId, login.Csrf);

            using var ws = new WsClient();
            await ws.ConnectAsync(fx.Port, ticket);
            Assert.Contains("101", FirstLine(ws.HandshakeResponse));

            // ★ 反证：会话有效期间这条连接**真的在收监控数据**
            Assert.Equal(WsReadResult.Frame, await ws.ReadFrameAsync(15000));

            // ★ 假时钟跳过空闲 TTL（2 分钟）
            fx.Clock.Advance(TimeSpan.FromMinutes(3));

            // ★ 会话过期 ⇒ 已建立的 WS 必须被断开
            Assert.Equal(WsReadResult.Closed, await ReadUntilClosedAsync(ws, 20000));
            Assert.Equal(WsReadResult.Closed, await ws.ReadFrameAsync(3000));
            Assert.Equal(0, fx.Manager.AuthenticatedConnectionCount);
        }

        /// <summary>
        /// LM-076-③：会话**超过绝对期限**（30 分钟）后，已建立的 WS 同样不得再收到监控数据。
        /// </summary>
        /// <remarks>
        /// ★ 绝对 TTL 与空闲 TTL 是两条独立的到期路径，都要覆盖。
        /// ★ 现场刻意把"最后一次使用"放在离到期只有 1 分钟的位置：
        ///   这样到期的是**绝对 TTL**（创建至今 30 分钟），而不是空闲 TTL（距上次使用 1 分钟，未到期）。
        /// </remarks>
        [Fact]
        public async Task LM076_会话超过绝对期限后已建立的WebSocket不再收到数据()
        {
            using var fx = new ManagerFixture(providerThrows: false);
            UsePassword("lm076-absolute-password");

            var login = await LoginAsync(fx.Port, "lm076-absolute-password");
            string ticket = await RequestTicketAsync(fx.Port, login.SessionId, login.Csrf);

            using var ws = new WsClient();
            await ws.ConnectAsync(fx.Port, ticket);
            Assert.Contains("101", FirstLine(ws.HandshakeResponse));

            // ★ 反证：这条连接真的在收监控数据
            Assert.Equal(WsReadResult.Frame, await ws.ReadFrameAsync(15000));

            // ★ 把"最后一次使用"推到离绝对到期只有 1 分钟的位置
            await AdvanceClockAsync(fx.Clock, fx.Port, login.SessionId, TimeSpan.FromMinutes(29));

            // ★ 再走 1 分 1 秒：距上次使用只有 1 分 1 秒（空闲 TTL 未到），
            //   但距创建已满 30 分钟（**绝对 TTL** 到期）
            fx.Clock.Advance(TimeSpan.FromSeconds(61));

            Assert.Equal(WsReadResult.Closed, await ReadUntilClosedAsync(ws, 20000));
            Assert.Equal(WsReadResult.Closed, await ws.ReadFrameAsync(3000));
            Assert.Equal(0, fx.Manager.AuthenticatedConnectionCount);
        }

        /// <summary>
        /// 把假时钟往前推，每步**不超过 100 秒**，每步之后用一次 HTTP 快照请求刷新会话的"最后使用时刻"。
        /// </summary>
        /// <remarks>
        /// ★ 为什么不能一次跳到位：空闲 TTL 只有 2 分钟，一次大跳会把它一并跨过，
        ///   于是"到期"的就不是绝对 TTL 了（测的就不是想测的那条路径）。
        ///   每步 ≤ 100 秒 ⇒ 空闲 TTL 始终没到期；每步之后刷新一次"最后使用时刻"，
        ///   于是会话始终有效，"最后使用时刻"始终贴着当前假时钟。
        /// </remarks>
        private static async Task AdvanceClockAsync(FakeClock clock, int port, string sessionId, TimeSpan total)
        {
            var step = TimeSpan.FromSeconds(100);
            var remaining = total;
            while (remaining > TimeSpan.Zero)
            {
                TimeSpan chunk = remaining < step ? remaining : step;
                clock.Advance(chunk);
                remaining -= chunk;

                string resp = await SendRawAsync(port,
                    $"GET /api/snapshot HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                    $"Cookie: lmsid={sessionId}\r\nConnection: close\r\n\r\n");
                Assert.Contains("200", FirstLine(resp));
            }
        }

        /// <summary>
        /// LM-076-④：「清理过期会话」这条路径也必须断开对应的 WS。
        /// </summary>
        /// <remarks>
        /// ★ 现场刻意让**广播里的会话存活判定不执行**：数据提供器直接抛异常 ⇒
        ///   广播循环在"生产载荷"阶段就失败 ⇒ 永远走不到收件人枚举
        ///   （收件人枚举里的会话存活判定与它顺带刷新的空闲计时都在那之后）。
        ///   于是这里**唯一**能关掉这条 WS 的路径就是「清理过期会话」（PruneSessionsLocked）。
        /// ★ 触发方式：假时钟跳过空闲 TTL 后再登录一次 —— 签发新会话时必然清理过期会话。
        /// </remarks>
        [Fact]
        public async Task LM076_清理过期会话时已建立的WebSocket不再收到数据()
        {
            using var fx = new ManagerFixture(providerThrows: true);
            UsePassword("lm076-prune-password");

            var login = await LoginAsync(fx.Port, "lm076-prune-password");
            string ticket = await RequestTicketAsync(fx.Port, login.SessionId, login.Csrf);

            using var ws = new WsClient();
            await ws.ConnectAsync(fx.Port, ticket);
            Assert.Contains("101", FirstLine(ws.HandshakeResponse));
            Assert.True(await WaitForAsync(() => fx.Manager.AuthenticatedConnectionCount == 1, 5000),
                "已鉴权的 WS 连接应当计入有效需求");

            // ★ 假时钟跳过空闲 TTL ⇒ 该会话变成"过期会话"
            fx.Clock.Advance(TimeSpan.FromMinutes(3));

            // ★ 再登录一次 ⇒ 签发新会话时必然清理过期会话
            var second = await LoginAsync(fx.Port, "lm076-prune-password");
            Assert.False(string.IsNullOrEmpty(second.SessionId));

            // ★ 被清理掉的会话，它的 WS 必须一起断开
            Assert.Equal(WsReadResult.Closed, await ReadUntilClosedAsync(ws, 20000));
            Assert.Equal(WsReadResult.Closed, await ws.ReadFrameAsync(3000));
            Assert.Equal(0, fx.Manager.AuthenticatedConnectionCount);
        }

        // ================================================================
        //  LM-077 · 口令迁移在写盘失败时仍报成功（旧明文留在磁盘上）
        // ================================================================

        /// <summary>
        /// LM-077-①：**保存被禁止**时，口令迁移必须如实返回 false。
        /// </summary>
        /// <remarks>
        /// ★ 盯的缺陷：<c>Save</c> 在 <c>GlobalBlockSave</c> 为真时**直接返回**（void、无任何信号），
        ///   <c>EnsureWebPasswordMigrated</c> 随后无条件 <c>return true</c>
        ///   ⇒ 调用方（登录路径）以为迁移成功、放行登录，而**旧明文口令仍留在磁盘上**。
        /// ★ 断言不只看返回值：还要看磁盘 —— 迁移没发生 ⇒ 旧明文必须还在（不能出现"假成功"）。
        /// </remarks>
        [Fact]
        public void LM077_保存被禁止时口令迁移必须返回false()
        {
            WriteLegacyPlaintextSettings("legacy-plain-forbidden");

            Settings cfg = Settings.Load(forceReload: true);
            Assert.Equal("legacy-plain-forbidden", cfg.WebServerPassword);

            // ★ 禁止保存（模拟"配置暂时读不到"或"重置配置"兜底）
            SettingsHelper.GlobalBlockSave = true;

            bool migrated = cfg.EnsureWebPasswordMigrated();

            Assert.False(migrated,
                "保存被禁止时迁移必须如实返回 false（否则登录被放行，而旧明文口令仍留在磁盘上）");

            // ★ 迁移确实没有发生：旧明文仍在磁盘上
            Assert.Contains("legacy-plain-forbidden", File.ReadAllText(MainPath), StringComparison.Ordinal);
        }

        /// <summary>
        /// LM-077-②：**写盘失败**（IO 故障）时，口令迁移必须如实返回 false。
        /// </summary>
        /// <remarks>
        /// ★ 盯的缺陷：<c>Save</c> 把 IO 异常**吞掉**（catch 里只清理临时文件），
        ///   返回 void ⇒ 调用方无从得知写没写成 ⇒ 一律走 <c>return true</c>。
        /// ★ 现场用**独占锁**制造 IO 故障：主文件被独占 ⇒ 原子写入的最后一步（替换主文件）必然失败。
        /// </remarks>
        [Fact]
        public void LM077_写盘失败时口令迁移必须返回false()
        {
            WriteLegacyPlaintextSettings("legacy-plain-iofailure");

            Settings cfg = Settings.Load(forceReload: true);
            Assert.Equal("legacy-plain-iofailure", cfg.WebServerPassword);

            // ★ 独占锁住主文件 ⇒ 写盘必然失败
            using (new FileStream(MainPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                bool migrated = cfg.EnsureWebPasswordMigrated();

                Assert.False(migrated,
                    "写盘失败时迁移必须如实返回 false（否则登录被放行，而旧明文口令仍留在磁盘上）");
            }

            // ★ 迁移确实没有发生：旧明文仍在磁盘上
            Assert.Contains("legacy-plain-iofailure", File.ReadAllText(MainPath), StringComparison.Ordinal);
        }

        /// <summary>
        /// LM-077-③：迁移成功时返回 true，且**磁盘上没有旧明文**（主文件与备份都没有）。
        /// </summary>
        /// <remarks>
        /// ★ 这是"迁移到底干了什么"的正向断言：明文被转成哈希 + 盐落盘，
        ///   备份里那份旧明文也必须被清掉（只清主文件等于没清）。
        /// </remarks>
        [Fact]
        public void LM077_迁移成功时返回true且磁盘上没有旧明文()
        {
            WriteLegacyPlaintextSettings("legacy-plain-migrate");

            Settings cfg = Settings.Load(forceReload: true);
            Assert.Equal("legacy-plain-migrate", cfg.WebServerPassword);

            bool migrated = cfg.EnsureWebPasswordMigrated();
            Assert.True(migrated, "写盘成功时迁移必须返回 true");

            // ★ 主文件：没有旧明文，但有哈希 + 盐
            string main = File.ReadAllText(MainPath);
            Assert.DoesNotContain("legacy-plain-migrate", main, StringComparison.Ordinal);
            Assert.Contains("WebServerPasswordHash", main, StringComparison.Ordinal);
            Assert.Contains("WebServerPasswordSalt", main, StringComparison.Ordinal);

            // ★ 备份：同样不得留着旧明文（备份是"上一份有效内容"，它同样含明文）
            Assert.True(File.Exists(BackupPath), "前置条件不成立：本次落盘应当刷新出备份文件");
            Assert.DoesNotContain("legacy-plain-migrate", File.ReadAllText(BackupPath), StringComparison.Ordinal);
        }

        // ================================================================
        //  辅助
        // ================================================================

        /// <summary>申请一次性票据。</summary>
        private static async Task<string> RequestTicketAsync(int port, string sessionId, string csrf)
        {
            string resp = await SendRawAsync(port,
                $"POST /api/auth/ticket HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={sessionId}\r\n" +
                $"X-LiteMonitor-CSRF: {csrf}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            Assert.Contains("200", FirstLine(resp));

            using var doc = JsonDocument.Parse(ExtractBody(resp));
            return doc.RootElement.TryGetProperty("ticket", out var el) ? (el.GetString() ?? "") : "";
        }

        private static string BuildLogoutRequest(int port, string sessionId, string csrf)
        {
            return $"POST /api/auth/logout HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nCookie: lmsid={sessionId}\r\n" +
                   $"X-LiteMonitor-CSRF: {csrf}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        }

        /// <summary>读一帧服务端推来的数据的结局。</summary>
        private enum WsReadResult
        {
            /// <summary>收到一帧数据。</summary>
            Frame,

            /// <summary>连接已被服务端关闭（EOF / Close 帧 / 连接重置）。</summary>
            Closed,

            /// <summary>在期限内没有等到数据（连接仍然活着）。</summary>
            Timeout,
        }

        /// <summary>
        /// 一条真实的 WebSocket 连接：握手 + 读帧 + 等服务端关闭。
        /// </summary>
        /// <remarks>
        /// ★ 服务端 → 客户端的帧**不带掩码**，所以长度字节直接就是载荷长度。
        /// </remarks>
        private sealed class WsClient : IDisposable
        {
            private readonly TcpClient _client = new TcpClient();
            private NetworkStream? _stream;

            /// <summary>握手响应（HTTP 头部分）。</summary>
            public string HandshakeResponse = "";

            public async Task ConnectAsync(int port, string ticket)
            {
                await _client.ConnectAsync(IPAddress.Loopback, port);
                _stream = _client.GetStream();

                string req =
                    $"GET /ws?ticket={ticket} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                    "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                    "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
                byte[] data = Encoding.UTF8.GetBytes(req);
                await _stream.WriteAsync(data, 0, data.Length);
                await _stream.FlushAsync();

                var sb = new StringBuilder();
                var buf = new byte[1024];
                using var cts = new CancellationTokenSource(15000);
                while (sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
                {
                    int n = await _stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                    if (n <= 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
                HandshakeResponse = sb.ToString();
            }

            /// <summary>读一帧；返回 Frame / Closed / Timeout。</summary>
            public async Task<WsReadResult> ReadFrameAsync(int timeoutMs = 8000)
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                try
                {
                    var hdr = new byte[2];
                    if (!await ReadExactAsync(hdr, 2, cts.Token)) return WsReadResult.Closed;

                    int opcode = hdr[0] & 0x0F;
                    long len = hdr[1] & 0x7F;
                    if (len == 126)
                    {
                        var ext = new byte[2];
                        if (!await ReadExactAsync(ext, 2, cts.Token)) return WsReadResult.Closed;
                        len = (ext[0] << 8) | ext[1];
                    }
                    else if (len == 127)
                    {
                        var ext = new byte[8];
                        if (!await ReadExactAsync(ext, 8, cts.Token)) return WsReadResult.Closed;
                        len = BitConverter.ToInt64(ext, 0);
                    }

                    var payload = new byte[len];
                    if (len > 0 && !await ReadExactAsync(payload, (int)len, cts.Token)) return WsReadResult.Closed;

                    if (opcode == 0x08) return WsReadResult.Closed;   // 服务端的 Close 帧
                    return WsReadResult.Frame;
                }
                catch (OperationCanceledException) { return WsReadResult.Timeout; }
                catch (IOException) { return WsReadResult.Closed; }
                catch (SocketException) { return WsReadResult.Closed; }
                catch (ObjectDisposedException) { return WsReadResult.Closed; }
            }

            private async Task<bool> ReadExactAsync(byte[] buffer, int count, CancellationToken token)
            {
                int total = 0;
                while (total < count)
                {
                    int n = await _stream!.ReadAsync(buffer.AsMemory(total, count - total), token);
                    if (n <= 0) return false;   // EOF
                    total += n;
                }
                return true;
            }

            public void Dispose()
            {
                try { _client.Close(); } catch { }
            }
        }

        /// <summary>一直读到服务端关闭连接为止。</summary>
        private static async Task<WsReadResult> ReadUntilClosedAsync(WsClient ws, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) return WsReadResult.Timeout;

                WsReadResult r = await ws.ReadFrameAsync(Math.Min(remaining, 2000));
                if (r == WsReadResult.Closed) return WsReadResult.Closed;
                // Timeout = 暂时没数据但连接还活着 ⇒ 继续等
            }
        }

        /// <summary>轮询等待某个条件成立。</summary>
        private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(25);
            }
            return condition();
        }

        /// <summary>可注入的单调时钟（TTL 用例不靠 Thread.Sleep）。</summary>
        private sealed class FakeClock : TimeProvider
        {
            private long _ticks;
            public FakeClock(long startTicks = 0) { _ticks = startTicks; }
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => Interlocked.Read(ref _ticks);
            public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            public void Advance(TimeSpan delta)
                => Interlocked.Add(ref _ticks, (long)Math.Round(delta.TotalSeconds * TimestampFrequency));
        }

        /// <summary>
        /// 直接构造 <see cref="WebSessionManager"/> + 回环宿主（用于需要假时钟的 TTL 用例）。
        /// </summary>
        /// <remarks>
        /// ★ 必须调用 <c>Manager.Start()</c>：否则连接登记会被拒（<c>AddClient</c> 要求服务在运行），
        ///   广播循环也不会启动（收不到任何数据帧）。
        /// </remarks>
        private sealed class ManagerFixture : IDisposable
        {
            public FakeClock Clock { get; } = new FakeClock();
            public WebSessionManager Manager { get; }
            public LoopbackHost Host { get; }

            public int Port => Host.Port;

            public ManagerFixture(bool providerThrows)
            {
                Action<Utf8JsonWriter> provider = writer =>
                {
                    if (providerThrows)
                    {
                        // ★ 让广播循环在"生产载荷"阶段失败 ⇒ 永远走不到收件人枚举
                        throw new InvalidOperationException("测试用：快照提供器故意失败");
                    }
                    writer.WriteStartObject();
                    writer.WriteString("k", "test");
                    writer.WriteEndObject();
                };

                Manager = new WebSessionManager(provider, demandState: null, timeProvider: Clock);
                Manager.Start();
                Host = new LoopbackHost(Manager);
            }

            public void Dispose()
            {
                try { Manager.Stop(); } catch { }
                Host.Dispose();
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
                        TcpClient client = await _listener.AcceptTcpClientAsync();
                        _ = Task.Run(async () =>
                        {
                            try { await _manager.HandleClientAsync(client); }
                            catch { }
                        });
                    }
                }
                catch { /* 监听器已停止 */ }
            }

            public void Dispose()
            {
                try { _listener.Stop(); } catch { }
            }
        }


        /// <summary>设置口令（只写哈希 + 盐，内存里不留明文 —— 与迁移后的生产状态一致）。</summary>
        private void UsePassword(string plain)
        {
            _cfg.WebServerPasswordSalt = Settings.CreateWebPasswordSalt();
            _cfg.WebServerPasswordHash = Settings.ComputeWebPasswordHash(plain, _cfg.WebServerPasswordSalt);
            _cfg.WebServerPassword = "";
        }

        /// <summary>
        /// 造一份"旧配置"：口令仍以**明文**躺在 settings.json 里（迁移前的真实形态）。
        /// </summary>
        private static void WriteLegacyPlaintextSettings(string plain)
        {
            File.WriteAllText(MainPath,
                "{\"WebServerEnabled\":false,\"WebServerPort\":5000,\"WebServerPassword\":\"" + plain + "\"}");
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
