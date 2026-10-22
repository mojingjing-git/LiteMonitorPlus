using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// LM-007 / LM-008 的回归测试。
    ///
    /// LM-007：下载地址必须经过 Uri 解析 + 精确 Host/路径前缀比对；每一跳重定向【之前】
    ///         重新校验；拒绝编码分隔符与 ".." 穿越；下载字节数有上限。
    /// LM-008：市场安装路径强制摘要；文件与安装记录同事务落盘（暂存 → 原子替换 → 落盘后写记录），
    ///         失败要回滚，进程被杀后启动要能恢复；用户自装插件不得被覆盖/误卸载。
    ///
    /// 每条用例都必须能证伪：把对应的校验临时去掉（或把缺陷改回去），该用例必须失败。
    /// </summary>
    public class PluginMarketPolicyTests
    {
        private const string AllowedBase =
            "https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/";

        private static readonly Uri AllowedUri = new Uri(AllowedBase + "demo/plugin.json");

        // ==========================================================
        // LM-007：地址白名单（解析 + 精确 Host + 路径前缀）
        // ==========================================================

        [Fact]
        public void 地址_白名单内的正常地址_放行()
        {
            Assert.True(PluginMarket.IsAllowedDownloadUrl(AllowedBase + "public_ip_info/plugin.json"));
        }

        [Theory]
        [InlineData("https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/../evil")]
        [InlineData("https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/../../evil/x.json")]
        [InlineData("https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/%2e%2e/evil/x.json")]
        [InlineData("https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/a/../../evil.json")]
        public void 地址_路径穿越_拒绝(string url)
        {
            // ★ 反证：这几条在旧的 StartsWith 整串前缀实现下全部【被放行】
            Assert.False(PluginMarket.IsAllowedDownloadUrl(url));
        }

        [Theory]
        [InlineData("https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/a%2fb/x.json")]
        [InlineData("https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/a%5cb/x.json")]
        [InlineData("https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/a%2Fb/x.json")]
        [InlineData("https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/a%5Cb/x.json")]
        public void 地址_编码分隔符_拒绝(string url)
        {
            Assert.False(PluginMarket.IsAllowedDownloadUrl(url));
        }

        [Theory]
        [InlineData("https://evil.example.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/x.json")]
        [InlineData("https://raw.githubusercontent.com.evil.example/mojingjing-git/awesome-litemonitor-plugins/main/plugins/x.json")]
        [InlineData("https://raw.githubusercontent.com/other/other/main/plugins/x.json")]
        [InlineData("https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins-evil/x.json")]
        [InlineData("https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/other/x.json")]
        public void 地址_非白名单主机或路径_拒绝(string url)
        {
            Assert.False(PluginMarket.IsAllowedDownloadUrl(url));
        }

        [Fact]
        public void 地址_非HTTPS_拒绝()
        {
            Assert.False(PluginMarket.IsAllowedDownloadUrl(
                "http://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/x.json"));
        }

        [Fact]
        public void 地址_带userinfo_拒绝()
        {
            Assert.False(PluginMarket.IsAllowedDownloadUrl(
                "https://user:pass@raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/x.json"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not a url")]
        [InlineData("/relative/path.json")]
        [InlineData("ftp://raw.githubusercontent.com/mojingjing-git/x.json")]
        [InlineData("file:///C:/temp/x.json")]
        [InlineData("https://")]
        [InlineData("://raw.githubusercontent.com/x.json")]
        public void 地址_畸形_拒绝(string url)
        {
            Assert.False(PluginMarket.IsAllowedDownloadUrl(url));
        }

        [Fact]
        public void 地址_null_拒绝()
        {
            Assert.False(PluginMarket.IsAllowedDownloadUrl(null));
        }

        [Fact]
        public void 地址_超长_拒绝()
        {
            string url = AllowedBase + new string('a', 4096);
            Assert.False(PluginMarket.IsAllowedDownloadUrl(url));
        }

        [Fact]
        public void 地址_非默认端口_拒绝()
        {
            Assert.False(PluginMarket.IsAllowedDownloadUrl(
                "https://raw.githubusercontent.com:8443/mojingjing-git/awesome-litemonitor-plugins/main/plugins/x.json"));
        }

        [Fact]
        public void 地址_空段_拒绝()
        {
            Assert.False(PluginMarket.IsAllowedDownloadUrl(
                "https://raw.githubusercontent.com/mojingjing-git//awesome-litemonitor-plugins/main/plugins/x.json"));
        }

        // ==========================================================
        // LM-007：重定向逐跳校验（校验发生在【发出下一跳请求之前】）
        // ==========================================================

        [Fact]
        public void 下载处理器_禁止自动跳转_否则来不及逐跳校验()
        {
            // ★ 自动跳转会让 HttpClient 内部直接跟到下一跳（请求已经发出去），
            //   来不及对下一跳做白名单校验 —— 生产处理器必须显式关掉。
            HttpMessageHandler handler = PluginMarket.CreateDownloadHandler();
            HttpClientHandler http = handler as HttpClientHandler;
            Assert.NotNull(http);
            Assert.False(http.AllowAutoRedirect);
        }

        [Fact]
        public async Task 下载_跳转到不允许的主机_拒绝且不向该主机发请求()
        {
            byte[] body = PluginBytes("demo");
            var handler = new ScriptedHandler((req, i) => i == 0 ? Redirect("https://evil.example.com/plugin.json") : Ok(body));
            using var scope = new MarketScope(handler);

            var ex = await Assert.ThrowsAsync<PluginMarketException>(
                async () => { await PluginMarket.DownloadBytesAsync(AllowedUri); });

            Assert.Equal(MarketErrorReason.BadSource, ex.Reason);

            // ★ 只发出了第一跳的请求；跳到 evil 的那一跳根本没有发出去
            Assert.Single(handler.RequestedUris);
            Assert.DoesNotContain("evil.example.com", string.Join(",", handler.RequestedUris));
        }

        [Fact]
        public async Task 下载_跳转到http_拒绝()
        {
            byte[] body = PluginBytes("demo");
            var handler = new ScriptedHandler((req, i) => i == 0
                ? Redirect("http://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/demo/plugin.json")
                : Ok(body));
            using var scope = new MarketScope(handler);

            var ex = await Assert.ThrowsAsync<PluginMarketException>(
                async () => { await PluginMarket.DownloadBytesAsync(AllowedUri); });

            Assert.Equal(MarketErrorReason.BadSource, ex.Reason);
            Assert.Single(handler.RequestedUris);
        }

        [Fact]
        public async Task 下载_跳转带userinfo_拒绝()
        {
            byte[] body = PluginBytes("demo");
            var handler = new ScriptedHandler((req, i) => i == 0
                ? Redirect("https://user:pass@raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/demo/plugin.json")
                : Ok(body));
            using var scope = new MarketScope(handler);

            var ex = await Assert.ThrowsAsync<PluginMarketException>(
                async () => { await PluginMarket.DownloadBytesAsync(AllowedUri); });

            Assert.Equal(MarketErrorReason.BadSource, ex.Reason);
            Assert.Single(handler.RequestedUris);
        }

        [Fact]
        public async Task 下载_跳转次数超限_拒绝()
        {
            var handler = new ScriptedHandler((req, i) => Redirect(AllowedUri.AbsoluteUri));
            using var scope = new MarketScope(handler);

            var ex = await Assert.ThrowsAsync<PluginMarketException>(
                async () => { await PluginMarket.DownloadBytesAsync(AllowedUri); });

            Assert.Equal(MarketErrorReason.BadSource, ex.Reason);
            Assert.Contains("重定向", ex.Message);
            Assert.True(handler.RequestedUris.Count <= PluginMarket.MaxDownloadRedirects + 1,
                "跳转次数没有上限，实际请求 " + handler.RequestedUris.Count + " 次");
        }

        [Fact]
        public async Task 下载_同源跳转_放行()
        {
            byte[] body = Encoding.UTF8.GetBytes("{\"id\":\"demo\"}");
            var handler = new ScriptedHandler((req, i) =>
                i == 0 ? Redirect(AllowedUri.AbsoluteUri) : Ok(body));
            using var scope = new MarketScope(handler);

            byte[] got = await PluginMarket.DownloadBytesAsync(AllowedUri);

            Assert.Equal(body, got);
            Assert.Equal(2, handler.RequestedUris.Count);
        }

        // ==========================================================
        // LM-007：下载字节上限
        // ==========================================================

        [Fact]
        public async Task 下载_声明长度超过上限_中止()
        {
            var big = new byte[PluginMarket.MaxDownloadBytes + 1024];
            var handler = new ScriptedHandler((req, i) => Ok(big));
            using var scope = new MarketScope(handler);

            var ex = await Assert.ThrowsAsync<PluginMarketException>(
                async () => { await PluginMarket.DownloadBytesAsync(AllowedUri); });

            Assert.Contains("上限", ex.Message);
        }

        [Fact]
        public async Task 下载_未声明长度但实际超过上限_中止()
        {
            var big = new byte[PluginMarket.MaxDownloadBytes + 1024];
            var handler = new ScriptedHandler((req, i) => OkChunked(big));
            using var scope = new MarketScope(handler);

            var ex = await Assert.ThrowsAsync<PluginMarketException>(
                async () => { await PluginMarket.DownloadBytesAsync(AllowedUri); });

            Assert.Contains("上限", ex.Message);
        }

        // ==========================================================
        // LM-008：摘要强制
        // ==========================================================

        [Fact]
        public async Task 安装_索引条目没有摘要_拒绝安装()
        {
            byte[] body = PluginBytes("demo_no_hash");
            var handler = new ScriptedHandler((req, i) => Ok(body));
            using var scope = new MarketScope(handler);

            var entry = new MarketPluginEntry
            {
                Id = "demo_no_hash",
                DownloadUrl = AllowedUri.AbsoluteUri,
                Sha256 = ""
            };

            var ex = await Assert.ThrowsAsync<PluginMarketException>(
                async () => { await PluginMarket.InstallAsync(entry); });

            // ★ 关键：不是"校验通过"，而是"拒绝安装"
            Assert.Equal(MarketErrorReason.MissingHash, ex.Reason);
            Assert.False(File.Exists(Path.Combine(scope.PluginsDir, "demo_no_hash.json")),
                "缺摘要却落盘了！");
        }

        [Fact]
        public async Task 安装_摘要不匹配_拒绝安装()
        {
            byte[] body = PluginBytes("demo_bad_hash");
            var handler = new ScriptedHandler((req, i) => Ok(body));
            using var scope = new MarketScope(handler);

            var entry = new MarketPluginEntry
            {
                Id = "demo_bad_hash",
                DownloadUrl = AllowedUri.AbsoluteUri,
                Sha256 = new string('0', 64)
            };

            var ex = await Assert.ThrowsAsync<PluginMarketException>(
                async () => { await PluginMarket.InstallAsync(entry); });

            Assert.Equal(MarketErrorReason.HashMismatch, ex.Reason);
            Assert.False(File.Exists(Path.Combine(scope.PluginsDir, "demo_bad_hash.json")),
                "摘要不匹配却落盘了！");
        }

        [Fact]
        public void 摘要_缺失或格式非法_判定为不可用()
        {
            Assert.False(PluginMarket.IsUsableSha256(null));
            Assert.False(PluginMarket.IsUsableSha256(""));
            Assert.False(PluginMarket.IsUsableSha256("   "));
            Assert.False(PluginMarket.IsUsableSha256("abc"));
            Assert.False(PluginMarket.IsUsableSha256(new string('z', 64)));
            Assert.True(PluginMarket.IsUsableSha256(new string('0', 64)));
            Assert.True(PluginMarket.IsUsableSha256(new string('a', 64).ToUpperInvariant()));
        }

        // ==========================================================
        // LM-008：事务落盘（失败回滚）
        // ==========================================================

        [Fact]
        public void 安装_替换之后失败_原文件与原记录均未损坏()
        {
            using var scope = new MarketScope();
            var store = new InMemoryPluginRecordStore();

            byte[] oldBytes = PluginBytes("demo_tx");
            string oldSha = PluginMarket.ComputeSha256(oldBytes);
            byte[] newBytes = PluginBytes("demo_tx_v2");
            string newSha = PluginMarket.ComputeSha256(newBytes);

            string target = Path.Combine(scope.PluginsDir, "demo_tx.json");
            File.WriteAllBytes(target, oldBytes);
            store.Add(new MarketPluginRecord { Id = "demo_tx", File = "demo_tx.json", Sha256 = oldSha, Version = "1.0" });

            var boom = new InvalidOperationException("boom");
            var ex = Assert.Throws<InvalidOperationException>(() =>
                PluginMarketInstallCore.CommitInstall(
                    scope.PluginsDir, "demo_tx", "2.0", newBytes, newSha,
                    store.Find, store.Upsert, store.Remove,
                    point => { if (point == "afterMove") throw boom; }));

            Assert.Same(boom, ex);

            // 原文件仍在、内容不变
            Assert.True(File.Exists(target));
            Assert.Equal(oldBytes, File.ReadAllBytes(target));

            // 原记录仍在、内容不变
            MarketPluginRecord record = store.Find("demo_tx");
            Assert.NotNull(record);
            Assert.Equal(oldSha, record.Sha256);
            Assert.Equal("1.0", record.Version);

            // 暂存/备份文件被清理，写前日志被清空
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.stage"));
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.bak"));
            Assert.False(File.Exists(Path.Combine(scope.PluginsDir, PluginMarketInstallCore.TransactionLogFileName)));
        }

        [Fact]
        public void 安装_写记录之后失败_文件与记录一起回滚()
        {
            using var scope = new MarketScope();
            var store = new InMemoryPluginRecordStore();

            byte[] oldBytes = PluginBytes("demo_tx2");
            string oldSha = PluginMarket.ComputeSha256(oldBytes);
            byte[] newBytes = PluginBytes("demo_tx2_v2");
            string newSha = PluginMarket.ComputeSha256(newBytes);

            string target = Path.Combine(scope.PluginsDir, "demo_tx2.json");
            File.WriteAllBytes(target, oldBytes);
            store.Add(new MarketPluginRecord { Id = "demo_tx2", File = "demo_tx2.json", Sha256 = oldSha, Version = "1.0" });

            var boom = new InvalidOperationException("boom");
            Assert.Throws<InvalidOperationException>(() =>
                PluginMarketInstallCore.CommitInstall(
                    scope.PluginsDir, "demo_tx2", "2.0", newBytes, newSha,
                    store.Find, store.Upsert, store.Remove,
                    point => { if (point == "afterRecord") throw boom; }));

            // 文件回滚到旧内容
            Assert.Equal(oldBytes, File.ReadAllBytes(target));

            // 记录回滚到旧记录（不能出现"记录说新版本、文件是旧内容"）
            MarketPluginRecord record = store.Find("demo_tx2");
            Assert.NotNull(record);
            Assert.Equal(oldSha, record.Sha256);
            Assert.Equal("1.0", record.Version);

            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.stage"));
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.bak"));
        }

        [Fact]
        public void 安装_暂存阶段失败_目标文件完全不受影响()
        {
            using var scope = new MarketScope();
            var store = new InMemoryPluginRecordStore();

            byte[] oldBytes = PluginBytes("demo_tx3");
            string oldSha = PluginMarket.ComputeSha256(oldBytes);
            byte[] newBytes = PluginBytes("demo_tx3_v2");
            string newSha = PluginMarket.ComputeSha256(newBytes);

            string target = Path.Combine(scope.PluginsDir, "demo_tx3.json");
            File.WriteAllBytes(target, oldBytes);
            store.Add(new MarketPluginRecord { Id = "demo_tx3", File = "demo_tx3.json", Sha256 = oldSha });

            var boom = new InvalidOperationException("boom");
            Assert.Throws<InvalidOperationException>(() =>
                PluginMarketInstallCore.CommitInstall(
                    scope.PluginsDir, "demo_tx3", "2.0", newBytes, newSha,
                    store.Find, store.Upsert, store.Remove,
                    point => { if (point == "afterStage") throw boom; }));

            Assert.Equal(oldBytes, File.ReadAllBytes(target));
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.stage"));
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.bak"));
        }

        [Fact]
        public void 安装_成功_先落盘再写记录且内容与摘要一致()
        {
            using var scope = new MarketScope();
            var store = new InMemoryPluginRecordStore();

            byte[] body = PluginBytes("demo_ok");
            string sha = PluginMarket.ComputeSha256(body);

            PluginMarketInstallCore.CommitInstall(
                scope.PluginsDir, "demo_ok", "1.2.3", body, sha,
                store.Find, store.Upsert, store.Remove);

            string target = Path.Combine(scope.PluginsDir, "demo_ok.json");
            Assert.True(File.Exists(target));
            Assert.Equal(body, File.ReadAllBytes(target));

            MarketPluginRecord record = store.Find("demo_ok");
            Assert.NotNull(record);
            Assert.Equal("demo_ok", record.Id);
            Assert.Equal("demo_ok.json", record.File);
            Assert.Equal(sha, record.Sha256);
            Assert.Equal("1.2.3", record.Version);

            // 落盘后不留暂存/备份/日志
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.stage"));
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.bak"));
            Assert.False(File.Exists(Path.Combine(scope.PluginsDir, PluginMarketInstallCore.TransactionLogFileName)));
        }

        // ==========================================================
        // LM-008：进程被杀后的启动恢复
        // ==========================================================

        [Fact]
        public void 启动恢复_只写了暂存文件_不留半截插件且记录与文件一致()
        {
            using var scope = new MarketScope();
            var store = new InMemoryPluginRecordStore();

            byte[] oldBytes = PluginBytes("demo_crash1");
            string oldSha = PluginMarket.ComputeSha256(oldBytes);
            byte[] newBytes = PluginBytes("demo_crash1_v2");
            string newSha = PluginMarket.ComputeSha256(newBytes);

            string target = Path.Combine(scope.PluginsDir, "demo_crash1.json");
            File.WriteAllBytes(target, oldBytes);
            store.Add(new MarketPluginRecord { Id = "demo_crash1", File = "demo_crash1.json", Sha256 = oldSha });

            // 模拟进程被杀：暂存写完就死（不回滚、不清日志）
            Assert.Throws<MarketInstallCrashException>(() =>
                PluginMarketInstallCore.CommitInstall(
                    scope.PluginsDir, "demo_crash1", "2.0", newBytes, newSha,
                    store.Find, store.Upsert, store.Remove,
                    point => { if (point == "afterStage") throw new MarketInstallCrashException(point); }));

            // 现场：暂存文件还在、写前日志还在
            Assert.NotEmpty(Directory.GetFiles(scope.PluginsDir, "*.stage"));
            Assert.True(File.Exists(Path.Combine(scope.PluginsDir, PluginMarketInstallCore.TransactionLogFileName)));

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, store.Find);
            Assert.Equal(1, handled);

            // 半截插件文件不留下
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.stage"));
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.bak"));

            // 原文件原样保留、记录与文件一致
            Assert.Equal(oldBytes, File.ReadAllBytes(target));
            MarketPluginRecord record = store.Find("demo_crash1");
            Assert.NotNull(record);
            Assert.Equal(oldSha, record.Sha256);
        }

        [Fact]
        public void 启动恢复_替换已发生但记录未更新_回滚到旧内容()
        {
            using var scope = new MarketScope();
            var store = new InMemoryPluginRecordStore();

            byte[] oldBytes = PluginBytes("demo_crash2");
            string oldSha = PluginMarket.ComputeSha256(oldBytes);
            byte[] newBytes = PluginBytes("demo_crash2_v2");
            string newSha = PluginMarket.ComputeSha256(newBytes);

            string target = Path.Combine(scope.PluginsDir, "demo_crash2.json");
            File.WriteAllBytes(target, oldBytes);
            store.Add(new MarketPluginRecord { Id = "demo_crash2", File = "demo_crash2.json", Sha256 = oldSha });

            Assert.Throws<MarketInstallCrashException>(() =>
                PluginMarketInstallCore.CommitInstall(
                    scope.PluginsDir, "demo_crash2", "2.0", newBytes, newSha,
                    store.Find, store.Upsert, store.Remove,
                    point => { if (point == "afterMove") throw new MarketInstallCrashException(point); }));

            // 现场：文件已经是新内容，记录还是旧的
            Assert.Equal(newBytes, File.ReadAllBytes(target));

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, store.Find);
            Assert.Equal(1, handled);

            // 回滚：文件恢复旧内容，记录保持旧记录 —— 两者一致
            Assert.Equal(oldBytes, File.ReadAllBytes(target));
            MarketPluginRecord record = store.Find("demo_crash2");
            Assert.NotNull(record);
            Assert.Equal(oldSha, record.Sha256);

            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.stage"));
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.bak"));
        }

        [Fact]
        public void 启动恢复_文件与记录都已更新_保留新内容并清理备份()
        {
            using var scope = new MarketScope();
            var store = new InMemoryPluginRecordStore();

            byte[] oldBytes = PluginBytes("demo_crash3");
            string oldSha = PluginMarket.ComputeSha256(oldBytes);
            byte[] newBytes = PluginBytes("demo_crash3_v2");
            string newSha = PluginMarket.ComputeSha256(newBytes);

            string target = Path.Combine(scope.PluginsDir, "demo_crash3.json");
            File.WriteAllBytes(target, oldBytes);
            store.Add(new MarketPluginRecord { Id = "demo_crash3", File = "demo_crash3.json", Sha256 = oldSha });

            Assert.Throws<MarketInstallCrashException>(() =>
                PluginMarketInstallCore.CommitInstall(
                    scope.PluginsDir, "demo_crash3", "2.0", newBytes, newSha,
                    store.Find, store.Upsert, store.Remove,
                    point => { if (point == "afterRecord") throw new MarketInstallCrashException(point); }));

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, store.Find);
            Assert.Equal(1, handled);

            // 这次安装实际已完成：保留新内容，清理备份
            Assert.Equal(newBytes, File.ReadAllBytes(target));
            MarketPluginRecord record = store.Find("demo_crash3");
            Assert.NotNull(record);
            Assert.Equal(newSha, record.Sha256);
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.bak"));
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.stage"));
        }

        [Fact]
        public void 启动恢复_公开入口_清理上次未完成的暂存文件()
        {
            using var scope = new MarketScope();

            byte[] oldBytes = PluginBytes("demo_crash4");
            byte[] newBytes = PluginBytes("demo_crash4_v2");
            string newSha = PluginMarket.ComputeSha256(newBytes);

            string target = Path.Combine(scope.PluginsDir, "demo_crash4.json");
            File.WriteAllBytes(target, oldBytes);

            Assert.Throws<MarketInstallCrashException>(() =>
                PluginMarketInstallCore.CommitInstall(
                    scope.PluginsDir, "demo_crash4", "2.0", newBytes, newSha,
                    id => null, record => { }, id => { },
                    point => { if (point == "afterStage") throw new MarketInstallCrashException(point); }));

            Assert.NotEmpty(Directory.GetFiles(scope.PluginsDir, "*.stage"));

            // 走 PluginMarket 自己的启动恢复入口
            PluginMarket.RecoverInterruptedInstalls();

            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.stage"));
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.bak"));
            Assert.Equal(oldBytes, File.ReadAllBytes(target));
        }

        [Fact]
        public void 启动恢复_没有事务时_不产生任何副作用()
        {
            using var scope = new MarketScope();

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, id => null);

            Assert.Equal(0, handled);
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.stage"));
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.bak"));
        }

        // ==========================================================
        // LM-008：用户自装插件不得被覆盖 / 误卸载
        // ==========================================================

        [Fact]
        public void 用户自装插件_市场不覆盖()
        {
            using var scope = new MarketScope();

            string target = Path.Combine(scope.PluginsDir, "user_plugin.json");
            File.WriteAllText(target, "{\"id\":\"user_plugin\"}");

            var store = new InMemoryPluginRecordStore();   // 没有任何市场安装记录

            Assert.False(PluginMarket.IsTargetAvailable("user_plugin", store.Find, out string reason));
            Assert.NotEqual("", reason);

            // 走公开入口也是同样结论
            Assert.False(PluginMarket.IsTargetAvailable("user_plugin", out string publicReason));
            Assert.NotEqual("", publicReason);

            // 文件原样保留
            Assert.Equal("{\"id\":\"user_plugin\"}", File.ReadAllText(target));
        }

        [Fact]
        public void 用户自装插件_市场不误卸载()
        {
            using var scope = new MarketScope();

            string target = Path.Combine(scope.PluginsDir, "user_plugin2.json");
            File.WriteAllText(target, "{\"id\":\"user_plugin2\"}");

            var entry = new MarketPluginEntry { Id = "user_plugin2" };
            bool removed = PluginMarket.Uninstall(entry, out MarketErrorReason reason);

            Assert.False(removed);
            Assert.Equal(MarketErrorReason.NotManaged, reason);
            Assert.True(File.Exists(target), "用户自装插件被误卸载了！");
        }

        [Fact]
        public void 用户改过的市场插件_市场不覆盖()
        {
            using var scope = new MarketScope();

            string target = Path.Combine(scope.PluginsDir, "edited_plugin.json");
            byte[] content = Encoding.UTF8.GetBytes("{\"id\":\"edited_plugin\"}");
            File.WriteAllBytes(target, content);

            var store = new InMemoryPluginRecordStore();
            store.Add(new MarketPluginRecord
            {
                Id = "edited_plugin",
                File = "edited_plugin.json",
                // 安装时记录的摘要与现在的文件内容不一致 ⇒ 用户改过
                Sha256 = PluginMarket.ComputeSha256(Encoding.UTF8.GetBytes("{\"id\":\"edited_plugin\",\"x\":1}"))
            });

            Assert.False(PluginMarket.IsTargetAvailable("edited_plugin", store.Find, out string reason));
            Assert.NotEqual("", reason);
            Assert.Equal(content, File.ReadAllBytes(target));
        }

        [Fact]
        public void 市场已装插件_内容与记录一致_允许更新()
        {
            using var scope = new MarketScope();

            byte[] content = PluginBytes("managed_plugin");
            string target = Path.Combine(scope.PluginsDir, "managed_plugin.json");
            File.WriteAllBytes(target, content);

            var store = new InMemoryPluginRecordStore();
            store.Add(new MarketPluginRecord
            {
                Id = "managed_plugin",
                File = "managed_plugin.json",
                Sha256 = PluginMarket.ComputeSha256(content)
            });

            Assert.True(PluginMarket.IsTargetAvailable("managed_plugin", store.Find, out string reason));
            Assert.Equal("", reason);
        }

        [Fact]
        public void 市场记录指向别的文件_拒绝覆盖()
        {
            using var scope = new MarketScope();

            string target = Path.Combine(scope.PluginsDir, "wrong_record.json");
            File.WriteAllText(target, "{\"id\":\"wrong_record\"}");

            var store = new InMemoryPluginRecordStore();
            store.Add(new MarketPluginRecord
            {
                Id = "wrong_record",
                File = "../elsewhere.json",
                Sha256 = PluginMarket.ComputeSha256(Encoding.UTF8.GetBytes("{\"id\":\"wrong_record\"}"))
            });

            Assert.False(PluginMarket.IsTargetAvailable("wrong_record", store.Find, out string reason));
            Assert.NotEqual("", reason);
        }

        // ==========================================================
        // 夹具
        // ==========================================================

        private static byte[] PluginBytes(string id)
            => Encoding.UTF8.GetBytes("{\"id\":\"" + id + "\",\"name\":\"demo\"}");

        private static HttpResponseMessage Ok(byte[] body)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(body);
            return response;
        }

        /// <summary>不声明 Content-Length 的响应体（走流式读取分支）</summary>
        private static HttpResponseMessage OkChunked(byte[] body)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ChunkedContent(body);
            return response;
        }

        private static HttpResponseMessage Redirect(string location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        }

        /// <summary>
        /// 把插件目录重定向到临时目录，并（可选）替换下载用的 HTTP 处理器。
        /// </summary>
        private sealed class MarketScope : IDisposable
        {
            private readonly string? _oldDir;
            private readonly HttpMessageHandler? _oldHandler;

            internal MarketScope(HttpMessageHandler? handler = null)
            {
                PluginsDir = Path.Combine(Path.GetTempPath(), "lm-market-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(PluginsDir);

                _oldDir = PluginMarket.PluginsDirOverride;
                _oldHandler = PluginMarket.HttpHandlerOverride;
                PluginMarket.PluginsDirOverride = PluginsDir;
                PluginMarket.HttpHandlerOverride = handler;
            }

            internal string PluginsDir { get; }

            public void Dispose()
            {
                PluginMarket.PluginsDirOverride = _oldDir;
                PluginMarket.HttpHandlerOverride = _oldHandler;
                try { Directory.Delete(PluginsDir, recursive: true); } catch { }
            }
        }

        private sealed class InMemoryPluginRecordStore
        {
            private readonly Dictionary<string, MarketPluginRecord> _records =
                new Dictionary<string, MarketPluginRecord>(StringComparer.OrdinalIgnoreCase);

            internal void Add(MarketPluginRecord record) => _records[record.Id] = record;

            internal MarketPluginRecord? Find(string id)
                => _records.TryGetValue(id, out MarketPluginRecord? record) ? record : null;

            internal void Upsert(MarketPluginRecord record) => _records[record.Id] = record;

            internal void Remove(string id) => _records.Remove(id);
        }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
            private readonly List<string> _requestedUris = new List<string>();

            internal ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            internal IReadOnlyList<string> RequestedUris
            {
                get { lock (_requestedUris) { return _requestedUris.ToList(); } }
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (_requestedUris)
                {
                    _requestedUris.Add(request.RequestUri?.AbsoluteUri ?? "");
                }

                HttpResponseMessage response = _responder(request, _requestedUris.Count - 1);
                response.RequestMessage = request;
                return Task.FromResult(response);
            }
        }

        private sealed class ChunkedContent : HttpContent
        {
            private readonly byte[] _data;

            internal ChunkedContent(byte[] data)
            {
                _data = data;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
                => stream.WriteAsync(_data, 0, _data.Length);

            protected override bool TryComputeLength(out long length)
            {
                length = -1;
                return false;
            }
        }
    }
}
