using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 主题市场策略测试（M5）：安装事务、拒收负例与 D7 卸载守卫。
    ///
    /// 沙箱纪律（C-1 延续）：
    /// - 会读写 settings.json（ThemeMarketState 记录 / Skin）⇒ 挂 <c>[Collection("cfgfile")]</c> 串行；
    /// - 文件落盘走 <c>ThemeMarket.ThemesDirOverride</c> 临时目录；
    /// - 下载走 <c>PluginMarket.HttpHandlerOverride</c> 测试替身（不访问网络）。
    /// </summary>
    [Collection("cfgfile")]
    public class ThemeMarketPolicyTests : IDisposable
    {
        private readonly ThemeMarketScope _scope;
        private readonly string? _oldSkin;

        public ThemeMarketPolicyTests()
        {
            _scope = new ThemeMarketScope(new ScriptedHandler(ThemeMarketPolicyTests.ServeFixture));
            _oldSkin = Settings.Load().Skin;
        }

        public void Dispose()
        {
            try { Settings.Load().Skin = _oldSkin; } catch { }
            foreach (string id in new[] { "market-aurora-gpu", "market-paper-legacy", "market-evil-remote" })
            {
                try { ThemeMarketState.Remove(id); } catch { }
            }
            _scope.Dispose();
        }

        // ==========================================================
        // 安装事务：落盘 + 记录 + 目标保护
        // ==========================================================

        [Fact]
        public async Task 安装_成功落盘并写记录_重复安装走目标保护()
        {
            MarketThemeEntry gpu = LoadFixtureEntry("market-aurora-gpu");

            await ThemeMarket.InstallAsync(gpu);

            string path = Path.Combine(_scope.Dir, gpu.Id + ".json");
            Assert.True(File.Exists(path), "安装后文件未落盘");
            var record = ThemeMarketState.Find(gpu.Id);
            Assert.NotNull(record);
            Assert.Equal(gpu.Id, record!.Id);
            Assert.Equal(gpu.Id + ".json", record.File);
            Assert.Equal(gpu.Sha256, record.Sha256);

            // 目标已存在且是本市场安装、内容未改 ⇒ 更新流程放行（重复安装成功）
            await ThemeMarket.InstallAsync(gpu);
            Assert.True(File.Exists(path));

            // 用户改过文件 ⇒ 拒绝静默覆盖
            File.WriteAllText(path, "{ \"tampered\": true }");
            var ex = await Assert.ThrowsAsync<PluginMarketException>(() => ThemeMarket.InstallAsync(gpu));
            Assert.Equal(MarketErrorReason.TargetExists, ex.Reason);
        }

        [Fact]
        public async Task 安装_legacy主题_无rendering块_按legacy口径放行()
        {
            // F16：12 个内置 legacy 主题没有 rendering 块 —— 不得被误拒收
            MarketThemeEntry legacy = LoadFixtureEntry("market-paper-legacy");
            await ThemeMarket.InstallAsync(legacy);
            Assert.True(File.Exists(Path.Combine(_scope.Dir, legacy.Id + ".json")));
        }

        // ==========================================================
        // 拒收负例：远程引用 / 越界参数 / route 与内容不一致
        // ==========================================================

        [Fact]
        public async Task 拒收_远程引用()
        {
            MarketThemeEntry evil = LoadFixtureEntry("market-aurora-gpu");
            evil.Id = "market-evil-remote";
            evil.File = "invalid-examples/market-evil-remote.json";
            // ★ 下载地址必须指向负例文件本体，替身按 URL 路径回送字节
            evil.DownloadUrl = ThemeFixtureUrl(evil.File);
            SetShaAndSize(evil, Path.Combine(FixtureRoot(), "invalid-examples", "market-evil-remote.json"));

            var ex = await Assert.ThrowsAsync<PluginMarketException>(() => ThemeMarket.InstallAsync(evil));
            Assert.Equal(MarketErrorReason.ThemeContentRejected, ex.Reason);
            Assert.False(File.Exists(Path.Combine(_scope.Dir, evil.Id + ".json")), "被拒收的主题不得落盘");
        }

        [Fact]
        public async Task 拒收_越界参数()
        {
            MarketThemeEntry bad = LoadFixtureEntry("market-aurora-gpu");
            bad.Id = "market-bad-params";
            bad.File = "invalid-examples/market-bad-params.json";
            bad.DownloadUrl = ThemeFixtureUrl(bad.File);
            SetShaAndSize(bad, Path.Combine(FixtureRoot(), "invalid-examples", "market-bad-params.json"));

            var ex = await Assert.ThrowsAsync<PluginMarketException>(() => ThemeMarket.InstallAsync(bad));
            Assert.Equal(MarketErrorReason.ThemeContentRejected, ex.Reason);
        }

        [Fact]
        public async Task 拒收_route与内容不一致()
        {
            // 声明 legacy 但内容是 GPU（backend=gpu）
            MarketThemeEntry wrong = LoadFixtureEntry("market-aurora-gpu");
            wrong.Route = "legacy";
            wrong.Material = "legacy";
            var ex = await Assert.ThrowsAsync<PluginMarketException>(() => ThemeMarket.InstallAsync(wrong));
            Assert.Equal(MarketErrorReason.ThemeContentRejected, ex.Reason);

            // 声明 gpu 但内容没有 rendering 块（纯 legacy 主题）
            MarketThemeEntry wrong2 = LoadFixtureEntry("market-paper-legacy");
            wrong2.Route = "gpu";
            wrong2.Material = "liquid_glass";
            var ex2 = await Assert.ThrowsAsync<PluginMarketException>(() => ThemeMarket.InstallAsync(wrong2));
            Assert.Equal(MarketErrorReason.ThemeContentRejected, ex2.Reason);
        }

        // ==========================================================
        // D7 卸载守卫：当前主题先切默认再删；切换失败/缺失不删
        // ==========================================================

        [Fact]
        public async Task 卸载守卫_当前主题先切走再删_失败如实报错()
        {
            MarketThemeEntry gpu = LoadFixtureEntry("market-aurora-gpu");
            await ThemeMarket.InstallAsync(gpu);
            string path = Path.Combine(_scope.Dir, gpu.Id + ".json");
            Assert.True(File.Exists(path));

            // ① 目标不是当前主题：不需要切换，直接卸载成功
            Settings.Load().Skin = "DarkFlat_Classic";
            bool switchedUnused = false;
            Assert.True(ThemeMarket.TryUninstall(gpu, () => switchedUnused = true, out _));
            Assert.False(switchedUnused, "非当前主题不该触发切换");
            Assert.False(File.Exists(path), "卸载后文件应被删除");
            Assert.Null(ThemeMarketState.Find(gpu.Id));

            // ② 目标是当前主题：先切走（守卫成功路径），再删
            await ThemeMarket.InstallAsync(gpu);
            Settings.Load().Skin = gpu.Id;
            bool switched = false;
            Assert.True(ThemeMarket.TryUninstall(gpu, () => switched = true, out _));
            Assert.True(switched, "当前主题卸载必须先触发切换");
            Assert.False(File.Exists(path));
            Assert.Null(ThemeMarketState.Find(gpu.Id));

            // ③ 目标是当前主题但没有切换手段：如实失败，文件与记录保持
            await ThemeMarket.InstallAsync(gpu);
            Settings.Load().Skin = gpu.Id;
            Assert.False(ThemeMarket.TryUninstall(gpu, null, out _), "无切换手段时必须失败");
            Assert.True(File.Exists(path), "卸载失败不得删文件");
            Assert.NotNull(ThemeMarketState.Find(gpu.Id));

            // ④ 切换抛异常：同样不删
            Assert.False(ThemeMarket.TryUninstall(gpu, () => throw new InvalidOperationException("切换失败"), out _));
            Assert.True(File.Exists(path));
            Assert.NotNull(ThemeMarketState.Find(gpu.Id));
        }

        // ==========================================================
        // 夹具与替身
        // ==========================================================

        private static string FixtureRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "tests", "fixtures", "theme-repo");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("theme-repo 夹具缺失");
        }

        private static MarketThemeEntry LoadFixtureEntry(string id)
        {
            string indexPath = Path.Combine(FixtureRoot(), "index.json");
            var index = System.Text.Json.JsonSerializer.Deserialize<MarketIndex>(
                File.ReadAllText(indexPath),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var entry = index.Themes.First(t => t.Id == id);
            return new MarketThemeEntry
            {
                Id = entry.Id, Name = entry.Name, NameEn = entry.NameEn,
                Description = entry.Description, DescriptionEn = entry.DescriptionEn,
                Author = entry.Author, Version = entry.Version, File = entry.File,
                DownloadUrl = entry.DownloadUrl, PageUrl = entry.PageUrl,
                Sha256 = entry.Sha256, Size = entry.Size,
                Route = entry.Route, Material = entry.Material,
                Status = entry.Status, Verified = entry.Verified
            };
        }

        /// <summary>夹具文件的合法 themes 直链（白名单内，替身按路径回送夹具字节）。</summary>
        private static string ThemeFixtureUrl(string fileRelPath)
            => PluginMarket.DefaultIndexUrl.Replace("index.json", "themes/" + fileRelPath);

        private static void SetShaAndSize(MarketThemeEntry entry, string filePath)
        {
            byte[] raw = File.ReadAllBytes(filePath);
            entry.Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)).ToLowerInvariant();
            entry.Size = raw.Length;
        }

        /// <summary>测试替身：把下载地址映射到夹具文件的原始字节（不访问网络）。</summary>
        private static HttpResponseMessage ServeFixture(HttpRequestMessage request)
        {
            string url = request.RequestUri!.AbsoluteUri;
            // raw URL 形如 .../main/themes/{rel};正例在夹具 themes/ 下,负例在 invalid-examples/ 下
            int idx = url.IndexOf("/main/themes/", StringComparison.Ordinal);
            if (idx < 0) return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            string rel = url[(idx + "/main/themes/".Length)..].Replace('/', Path.DirectorySeparatorChar);
            string path = Path.Combine(FixtureRoot(), "themes", rel);
            if (!File.Exists(path)) path = Path.Combine(FixtureRoot(), rel);
            if (!File.Exists(path)) return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(File.ReadAllBytes(path))
            };
        }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            internal ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(_responder(request));
        }

        /// <summary>沙箱：主题目录重定向 + HTTP 替身，用完恢复并清理。</summary>
        private sealed class ThemeMarketScope : IDisposable
        {
            private readonly string? _oldDir;
            private readonly HttpMessageHandler? _oldHandler;

            internal string Dir { get; }

            internal ThemeMarketScope(HttpMessageHandler? handler = null)
            {
                _oldDir = ThemeMarket.ThemesDirOverride;
                _oldHandler = PluginMarket.HttpHandlerOverride;
                Dir = Path.Combine(Path.GetTempPath(), "litemonitor-theme-mkt-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Dir);
                ThemeMarket.ThemesDirOverride = Dir;
                PluginMarket.HttpHandlerOverride = handler;
            }

            public void Dispose()
            {
                ThemeMarket.ThemesDirOverride = _oldDir;
                PluginMarket.HttpHandlerOverride = _oldHandler;
                try { Directory.Delete(Dir, recursive: true); } catch { }
            }
        }
    }
}
