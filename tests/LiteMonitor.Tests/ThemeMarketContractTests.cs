using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 客户端与主题仓库之间的【契约测试】（主题市场，M5）。
    ///
    /// 跑在 <c>tests/fixtures/theme-repo</c> 夹具上（1 GPU + 1 legacy + 负例），
    /// 不依赖网络/环境变量 —— 仓库侧 themes/ 目录（计划 §6）落地前契约就已固化。
    /// 设置 LITEMONITOR_THEME_REPO 指向真实仓库时可叠加验证（见 LiveRepo 标注的用例）。
    ///
    /// 镜像插件市场 6 条契约（MarketIndexContractTests）的主题版：
    /// 解析 / sha256 与 size / 白名单 / 安装校验 / 不与内置重名 / route·material 与内容互验。
    /// 另加 D3 红线断言：顶层 schema 必须仍是 1。
    /// </summary>
    public class ThemeMarketContractTests
    {
        private static string FixtureRoot
        {
            get
            {
                // bin/{Debug|Release}/{tfm} → 仓库根：向上找含 tests/fixtures/theme-repo 的目录
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    string candidate = Path.Combine(dir.FullName, "tests", "fixtures", "theme-repo");
                    if (Directory.Exists(candidate)) return candidate;
                    dir = dir.Parent;
                }
                return "";
            }
        }

        private static bool TryLoadIndex(string root, out MarketIndex index)
        {
            index = new MarketIndex();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
            string path = Path.Combine(root, "index.json");
            if (!File.Exists(path)) return false;
            index = JsonSerializer.Deserialize<MarketIndex>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            return index != null;
        }

        private static MarketIndex LoadFixtureIndex()
        {
            Assert.True(TryLoadIndex(FixtureRoot, out MarketIndex index),
                "theme-repo 夹具缺失（tests/fixtures/theme-repo/index.json）");
            return index;
        }

        // ==========================================================
        // 1. 索引解析 + D3 红线：顶层 schema 必须仍是 1
        // ==========================================================

        [Fact]
        public void 索引能被客户端模型完整解析_含themes附加段()
        {
            var index = LoadFixtureIndex();

            // ★ D3 红线：顶层 schema 升 2 会整份打死已发布客户端，永远不得发生
            Assert.Equal(1, index.Schema);
            Assert.NotEmpty(index.Themes);
            Assert.Equal(index.ThemesTotal, index.Themes.Count);
            // 附加段共存：plugins 段原样可解析（老客户端口径）
            Assert.NotNull(index.Plugins);

            foreach (var e in index.Themes)
            {
                Assert.False(string.IsNullOrEmpty(e.Id), "有条目缺 id");
                Assert.False(string.IsNullOrEmpty(e.Name), e.Id + " 缺 name");
                Assert.False(string.IsNullOrEmpty(e.Description), e.Id + " 缺 description");
                Assert.False(string.IsNullOrEmpty(e.Author), e.Id + " 缺 author");
                Assert.False(string.IsNullOrEmpty(e.Version), e.Id + " 缺 version");
                Assert.False(string.IsNullOrEmpty(e.DownloadUrl), e.Id + " 缺 download_url");
                Assert.False(string.IsNullOrEmpty(e.PageUrl), e.Id + " 缺 page_url");
                Assert.False(string.IsNullOrEmpty(e.Sha256), e.Id + " 缺 sha256");
                Assert.True(e.Size > 0, e.Id + " 的 size 为 0");
                Assert.True(
                    string.Equals(e.Route, "gpu", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.Route, "legacy", StringComparison.OrdinalIgnoreCase),
                    e.Id + " 的 route 非法（必须是 gpu | legacy）");
                Assert.True(
                    string.Equals(e.Material, ThemeRenderingConfig.LiquidGlassMaterial, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.Material, ThemeRenderingConfig.CyberPunkMaterial, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.Material, ThemeRenderingConfig.LegacyMaterial, StringComparison.OrdinalIgnoreCase),
                    e.Id + " 的 material 非法");
            }
        }

        // ==========================================================
        // 2. sha256 / size 与仓库文件原始字节一致（与插件同一口径）
        // ==========================================================

        [Fact]
        public void themes的sha256与size_与夹具文件原始字节一致()
        {
            var index = LoadFixtureIndex();

            foreach (var e in index.Themes)
            {
                string path = Path.Combine(FixtureRoot, "themes", e.File);
                Assert.True(File.Exists(path), "夹具缺少 " + e.File);

                byte[] raw = File.ReadAllBytes(path);
                string hash = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
                Assert.Equal(e.Sha256, hash);
                Assert.Equal(e.Size, raw.Length);
                Assert.True(PluginMarket.VerifySha256(raw, e.Sha256), e.Id + " 的 sha256 校验不通过");
            }
        }

        // ==========================================================
        // 3. 下载地址白名单：themes/ 前缀变体命中；且与插件前缀互相隔离
        // ==========================================================

        [Fact]
        public void themes的下载地址_命中主题前缀_且与插件前缀隔离()
        {
            var index = LoadFixtureIndex();

            foreach (var e in index.Themes)
            {
                Assert.True(
                    PluginMarket.TryResolveAllowedDownloadUri(e.DownloadUrl, PluginMarket.ThemesAssetPrefix, out _, out _),
                    e.Id + " 的下载地址不在允许的主题源范围内: " + e.DownloadUrl);

                // ★ 资产隔离：主题地址不得被插件市场当合法源接受（反之亦然）
                Assert.False(
                    PluginMarket.TryResolveAllowedDownloadUri(e.DownloadUrl, PluginMarket.PluginsAssetPrefix, out _, out _),
                    e.Id + " 的主题地址竟能通过插件前缀校验 —— 资产前缀隔离被破坏");
            }
        }

        // ==========================================================
        // 4. 安装校验（id 字符集；"不与内置重名"由目标可用性口径覆盖）
        // ==========================================================

        [Fact]
        public void themes的id_都能通过安装校验()
        {
            var index = LoadFixtureIndex();

            foreach (var e in index.Themes)
            {
                Assert.True(PluginMarket.CanInstallTheme(e.Id, out string reason),
                    e.Id + " 未通过 CanInstallTheme: " + reason);
            }
        }

        [Fact]
        public void themes的id_不与内置主题重名()
        {
            var index = LoadFixtureIndex();
            var builtin = GetBuiltinThemeNames();

            Assert.NotEmpty(builtin);
            foreach (var e in index.Themes)
            {
                Assert.False(builtin.Contains(e.Id, StringComparer.OrdinalIgnoreCase),
                    e.Id + " 与内置主题重名 —— 市场主题不得占用内置主题名");
            }
        }

        // ==========================================================
        // 5. route / material 与主题 JSON 内容互验（Validate 全规则集，F16）
        // ==========================================================

        [Fact]
        public void themes的route与material_与主题JSON内容互验()
        {
            var index = LoadFixtureIndex();

            foreach (var e in index.Themes)
            {
                string path = Path.Combine(FixtureRoot, "themes", e.File);
                var theme = JsonSerializer.Deserialize<Theme>(
                    File.ReadAllText(path), ThemeMarket.ThemeJsonOpts);
                Assert.NotNull(theme);

                if (theme!.Rendering == null)
                {
                    // 缺 rendering = 纯 legacy 主题（12 个内置 legacy 主题的口径，F16）
                    Assert.True(string.Equals(e.Route, ThemeRenderingConfig.LegacyBackend, StringComparison.OrdinalIgnoreCase),
                        e.Id + " 内容缺 rendering，索引必须声明 legacy 路线");
                    Assert.True(string.Equals(e.Material, ThemeRenderingConfig.LegacyMaterial, StringComparison.OrdinalIgnoreCase),
                        e.Id + " 内容缺 rendering，索引必须声明 legacy 材质");
                    continue;
                }

                Assert.True(string.Equals(theme.Rendering.Backend, e.Route, StringComparison.OrdinalIgnoreCase),
                    e.Id + " 的 rendering.backend 与索引 route 不一致");
                Assert.True(string.Equals(theme.Rendering.Material, e.Material, StringComparison.OrdinalIgnoreCase),
                    e.Id + " 的 rendering.material 与索引 material 不一致");
                Assert.Empty(theme.Rendering.Validate());
            }
        }

        // ==========================================================
        // 6. 夹具负例本身必须站得住：远程引用可被检出、越界参数必须 Validate 失败
        // ==========================================================

        [Fact]
        public void 夹具负例_远程引用与越界参数_必须被检出()
        {
            string evilPath = Path.Combine(FixtureRoot, "invalid-examples", "market-evil-remote.json");
            string badPath = Path.Combine(FixtureRoot, "invalid-examples", "market-bad-params.json");
            Assert.True(File.Exists(evilPath), "缺远程引用负例夹具");
            Assert.True(File.Exists(badPath), "缺越界参数负例夹具");

            string evilText = File.ReadAllText(evilPath);
            Assert.True(ThemeMarket.ContainsRemoteReference(evilText),
                "远程引用负例未被 ContainsRemoteReference 检出");

            var bad = JsonSerializer.Deserialize<Theme>(File.ReadAllText(badPath), ThemeMarket.ThemeJsonOpts);
            Assert.NotNull(bad);
            Assert.NotEmpty(bad!.Rendering!.Validate());
        }

        // ==========================================================
        // 7.（可选叠加）真实仓库副本：设 LITEMONITOR_THEME_REPO 后同一组断言再跑一遍
        // ==========================================================

        [Fact]
        public void 真实仓库_设置环境变量后_同口径断言()
        {
            string? live = Environment.GetEnvironmentVariable("LITEMONITOR_THEME_REPO");
            if (string.IsNullOrEmpty(live) || !Directory.Exists(live))
            {
                // 与 MarketIndexContractTests 相同的门控口径：环境未提供则跳过断言
                return;
            }
            if (!TryLoadIndex(live, out MarketIndex index) || index.Themes.Count == 0)
            {
                // 仓库侧 themes/ 段尚未上线（计划 §6）——不视为失败
                return;
            }

            Assert.Equal(1, index.Schema);   // D3 红线同样约束真实仓库
            foreach (var e in index.Themes)
            {
                Assert.True(PluginMarket.CanInstallTheme(e.Id, out _), e.Id + " 安装校验不过");
                Assert.True(
                    PluginMarket.TryResolveAllowedDownloadUri(e.DownloadUrl, PluginMarket.ThemesAssetPrefix, out _, out _),
                    e.Id + " 下载地址不在主题源白名单内");
            }
        }

        /// <summary>内置主题名 = resources/themes 下的既有文件名（D6：内置 = ThemeDir 既有文件）</summary>
        private static IReadOnlyCollection<string> GetBuiltinThemeNames()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "themes");
                if (Directory.Exists(candidate))
                {
                    return Directory.EnumerateFiles(candidate, "*.json")
                        .Select(Path.GetFileNameWithoutExtension)
                        .ToList();
                }
                dir = dir.Parent;
            }
            return Array.Empty<string>();
        }
    }
}
