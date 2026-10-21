using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 客户端与插件仓库之间的【契约测试】。
    ///
    /// 仓库产物 index.json 必须能被客户端的 MarketIndex 正确解析，
    /// 且每条记录的 sha256 / size 必须与仓库里 plugin.json 的原始字节一致 ——
    /// 口径不一致会在安装时被误判成"下载内容校验失败"。
    ///
    /// 仓库路径通过环境变量 LITEMONITOR_PLUGIN_REPO 指定：
    ///   $env:LITEMONITOR_PLUGIN_REPO = 'G:\TOOL\awesome-litemonitor-plugins'
    /// 未设置时整组测试跳过（CI 里没有仓库副本也不该失败）。
    /// </summary>
    public class MarketIndexContractTests
    {
        private static string RepoRoot =>
            Environment.GetEnvironmentVariable("LITEMONITOR_PLUGIN_REPO");

        private static bool TryLoadIndex(out MarketIndex index, out string repoRoot)
        {
            index = null;
            repoRoot = RepoRoot;
            if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot)) return false;

            string path = Path.Combine(repoRoot, "index.json");
            if (!File.Exists(path)) return false;

            index = JsonSerializer.Deserialize<MarketIndex>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return index != null;
        }

        [PluginRepoFact]
        public void 索引能被客户端模型完整解析()
        {
            if (!TryLoadIndex(out var index, out string repoRoot))
            {
                // 门控已由 [PluginRepoFact] 在特性层处理；走到这里说明环境在特性检查之后被改动过。
                Assert.Fail("index.json 加载失败（特性已确认为可用环境，环境可能在执行期间被改动）");
            }

            Assert.Equal(1, index.Schema);
            Assert.Equal("mojingjing-git/awesome-litemonitor-plugins", index.Repo);
            Assert.NotEmpty(index.Plugins);
            Assert.Equal(index.Total, index.Plugins.Count);

            foreach (var e in index.Plugins)
            {
                Assert.False(string.IsNullOrEmpty(e.Id), repoRoot + " 有条目缺 id");
                Assert.False(string.IsNullOrEmpty(e.Name), e.Id + " 缺 name");
                Assert.False(string.IsNullOrEmpty(e.NameEn), e.Id + " 缺 name_en");
                Assert.False(string.IsNullOrEmpty(e.Description), e.Id + " 缺 description");
                Assert.False(string.IsNullOrEmpty(e.DescriptionEn), e.Id + " 缺 description_en");
                Assert.False(string.IsNullOrEmpty(e.Author), e.Id + " 缺 author");
                Assert.False(string.IsNullOrEmpty(e.Version), e.Id + " 缺 version");
                Assert.False(string.IsNullOrEmpty(e.DownloadUrl), e.Id + " 缺 download_url");
                Assert.False(string.IsNullOrEmpty(e.ReadmeUrl), e.Id + " 缺 readme_url");
                Assert.False(string.IsNullOrEmpty(e.PageUrl), e.Id + " 缺 page_url");
                Assert.False(string.IsNullOrEmpty(e.Source), e.Id + " 缺 source");
                Assert.False(string.IsNullOrEmpty(e.Sha256), e.Id + " 缺 sha256");
                Assert.True(e.Size > 0, e.Id + " 的 size 为 0");
            }
        }

        [PluginRepoFact]
        public void 索引里的sha256与size_与仓库文件原始字节一致()
        {
            if (!TryLoadIndex(out var index, out string repoRoot))
            {
                // 门控已由 [PluginRepoFact] 在特性层处理；走到这里说明环境在特性检查之后被改动过。
                Assert.Fail("index.json 加载失败（特性已确认为可用环境，环境可能在执行期间被改动）");
            }

            foreach (var e in index.Plugins)
            {
                string path = Path.Combine(repoRoot, "plugins", e.Id, "plugin.json");
                Assert.True(File.Exists(path), "仓库缺少 " + e.Id + "/plugin.json");

                byte[] raw = File.ReadAllBytes(path);

                // 客户端与仓库必须用同一套字节口径
                string hash = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
                Assert.Equal(e.Sha256, hash);
                Assert.Equal(e.Size, raw.Length);
                Assert.True(PluginMarket.VerifySha256(raw, e.Sha256), e.Id + " 的 sha256 校验不通过");
            }
        }

        [PluginRepoFact]
        public void 索引里的下载地址_都落在允许的源范围内()
        {
            if (!TryLoadIndex(out var index, out _))
            {
                // 门控已由 [PluginRepoFact] 在特性层处理；走到这里说明环境在特性检查之后被改动过。
                Assert.Fail("index.json 加载失败（特性已确认为可用环境，环境可能在执行期间被改动）");
            }

            foreach (var e in index.Plugins)
            {
                Assert.True(PluginMarket.IsAllowedDownloadUrl(e.DownloadUrl),
                    e.Id + " 的下载地址不在允许的源范围内: " + e.DownloadUrl);
            }
        }

        [PluginRepoFact]
        public void 索引里的插件id_都能通过客户端的安装校验()
        {
            if (!TryLoadIndex(out var index, out _))
            {
                // 门控已由 [PluginRepoFact] 在特性层处理；走到这里说明环境在特性检查之后被改动过。
                Assert.Fail("index.json 加载失败（特性已确认为可用环境，环境可能在执行期间被改动）");
            }

            foreach (var e in index.Plugins)
            {
                Assert.True(PluginMarket.CanInstall(e.Id, out string reason),
                    e.Id + " 未通过 CanInstall: " + reason);
            }
        }

        [PluginRepoFact]
        public void 索引里的插件id_不与内置插件重名()
        {
            if (!TryLoadIndex(out var index, out _))
            {
                // 门控已由 [PluginRepoFact] 在特性层处理；走到这里说明环境在特性检查之后被改动过。
                Assert.Fail("index.json 加载失败（特性已确认为可用环境，环境可能在执行期间被改动）");
            }

            foreach (var e in index.Plugins)
            {
                Assert.False(PluginConstants.BuiltinPluginIds.Contains(e.Id),
                    e.Id + " 与内置插件重名 —— 市场插件不得占用内置 id");
            }
        }

        [PluginRepoFact]
        public void 仓库里每个插件目录下的plugin_json_内容id_与目录名一致()
        {
            if (!TryLoadIndex(out var index, out string repoRoot))
            {
                // 门控已由 [PluginRepoFact] 在特性层处理；走到这里说明环境在特性检查之后被改动过。
                Assert.Fail("index.json 加载失败（特性已确认为可用环境，环境可能在执行期间被改动）");
            }

            foreach (var e in index.Plugins)
            {
                string path = Path.Combine(repoRoot, "plugins", e.Id, "plugin.json");
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                Assert.True(doc.RootElement.TryGetProperty("id", out var idEl), e.Id + " 的 plugin.json 缺 id");
                Assert.Equal(e.Id, idEl.GetString());
            }
        }
    }
}
