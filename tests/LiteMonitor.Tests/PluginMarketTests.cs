using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 插件市场的安全加固测试。
    /// 覆盖：落盘名派生、id 校验、内置保护、L2 不覆盖规则、下载源白名单、
    /// SHA-256 校验、索引字段解析、卸载所有权判定。
    /// </summary>
    public class PluginMarketTests
    {
        // ==========================================================
        // 落盘文件名：只由 id 决定
        // ==========================================================

        [Theory]
        [InlineData("public_ip_info", "public_ip_info.json")]
        [InlineData("TokenTracker", "TokenTracker.json")]
        [InlineData("watercooler_manager_local_api_full", "watercooler_manager_local_api_full.json")]
        public void GetInstallFileName_由id派生_与索引的file字段无关(string id, string expected)
        {
            Assert.Equal(expected, PluginMarket.GetInstallFileName(id));
        }

        // ==========================================================
        // CanInstall：id 合法性 + 不撞内置
        // ==========================================================

        [Theory]
        [InlineData("public_ip_info")]
        [InlineData("TokenTracker")]
        [InlineData("a")]
        [InlineData("A-b_9")]
        public void CanInstall_合法id_放行(string id)
        {
            Assert.True(PluginMarket.CanInstall(id, out _));
        }

        [Theory]
        [InlineData("../evil")]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        [InlineData("a.b")]
        [InlineData("a b")]
        [InlineData("")]
        [InlineData("   ")]
        public void CanInstall_非法id_拒绝(string id)
        {
            Assert.False(PluginMarket.CanInstall(id, out _));
        }

        [Fact]
        public void CanInstall_尾部带换行的id_拒绝()
        {
            // ★ .NET 的 $（非 Multiline）会匹配「串尾或串尾换行之前」，
            //   所以 "abc\n" 能通过 ^..$ 校验 —— 必须用 \A..\z 才拦得住。
            //   Path.GetInvalidFileNameChars() 含 LF/CR，放行会导致落盘抛 ArgumentException。
            Assert.False(PluginMarket.CanInstall("abc\n", out _));
            Assert.False(PluginMarket.CanInstall("abc\r\n", out _));
        }

        [Fact]
        public void CanInstall_超长id_拒绝()
        {
            Assert.False(PluginMarket.CanInstall(new string('a', 65), out _));
            Assert.True(PluginMarket.CanInstall(new string('a', 64), out _));
        }

        [Theory]
        [InlineData("PublicIP")]
        [InlineData("publicip")]
        [InlineData("Weather")]
        [InlineData("WeiboHot")]
        public void CanInstall_撞内置插件id_拒绝(string id)
        {
            Assert.False(PluginMarket.CanInstall(id, out _));
        }

        // ==========================================================
        // 内置保护名单
        // ==========================================================

        [Fact]
        public void BuiltinPluginIds_包含全部12个内置插件()
        {
            string[] expected = {
                "Bilibili", "Crypto", "Exchange", "GitHub", "Hitokoto", "Holiday",
                "ProxyLatency", "PublicIP", "Stock", "UniversalAPI", "Weather", "WeiboHot"
            };
            foreach (var id in expected)
                Assert.Contains(id, PluginConstants.BuiltinPluginIds);
            Assert.Equal(expected.Length, PluginConstants.BuiltinPluginIds.Count);
        }

        [Fact]
        public void BuiltinPluginIds_大小写不敏感()
        {
            Assert.Contains("publicip", PluginConstants.BuiltinPluginIds);
            Assert.Contains("PUBLICIP", PluginConstants.BuiltinPluginIds);
        }

        // ==========================================================
        // 下载源白名单（S6）
        // ==========================================================

        [Fact]
        public void IsAllowedDownloadUrl_自家仓库raw地址_放行()
        {
            Assert.True(PluginMarket.IsAllowedDownloadUrl(
                "https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/public_ip_info/plugin.json"));
        }

        [Theory]
        [InlineData("https://evil.example.com/x.json")]
        [InlineData("http://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/x/plugin.json")]
        [InlineData("https://raw.githubusercontent.com/other/other/main/plugins/x/plugin.json")]
        [InlineData("")]
        [InlineData(null)]
        public void IsAllowedDownloadUrl_站外地址_拒绝(string url)
        {
            Assert.False(PluginMarket.IsAllowedDownloadUrl(url));
        }

        // ==========================================================
        // SHA-256 校验（字节口径）
        // ==========================================================

        [Fact]
        public void VerifySha256_字节匹配_通过()
        {
            byte[] raw = Encoding.UTF8.GetBytes("{\"id\":\"demo\"}");
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)).ToLowerInvariant();
            Assert.True(PluginMarket.VerifySha256(raw, hash));
        }

        [Fact]
        public void VerifySha256_大写十六进制也通过()
        {
            byte[] raw = Encoding.UTF8.GetBytes("{\"id\":\"demo\"}");
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw));
            Assert.True(PluginMarket.VerifySha256(raw, hash));
        }

        [Fact]
        public void VerifySha256_哈希不符_不通过()
        {
            byte[] raw = Encoding.UTF8.GetBytes("{\"id\":\"demo\"}");
            Assert.False(PluginMarket.VerifySha256(raw, new string('0', 64)));
        }

        [Fact]
        public void VerifySha256_老索引没有哈希_放行()
        {
            byte[] raw = Encoding.UTF8.GetBytes("{\"id\":\"demo\"}");
            Assert.True(PluginMarket.VerifySha256(raw, null));
            Assert.True(PluginMarket.VerifySha256(raw, ""));
        }

        [Fact]
        public void VerifySha256_对原始字节计算_与仓库侧口径一致()
        {
            // 仓库侧 gen_index.py 对 plugin.json 的原始文件字节算 SHA-256。
            // 客户端必须对 HTTP 响应的原始字节算，先取字符串再编码会得到不同结果。
            byte[] raw = Encoding.UTF8.GetBytes("{\"id\":\"demo\",\"name\":\"x\"}");
            string repoStyle = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)).ToLowerInvariant();

            string viaString = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(raw)))).ToLowerInvariant();

            Assert.Equal(repoStyle, viaString);   // 无 BOM 的 UTF-8 下两者一致
            Assert.True(PluginMarket.VerifySha256(raw, repoStyle));
        }

        // ==========================================================
        // 索引解析：7 个新字段 + schema
        // ==========================================================

        private const string SampleIndex = @"{
  ""schema"": 1,
  ""updated_at"": ""2026-10-20T05:00:00Z"",
  ""repo"": ""mojingjing-git/awesome-litemonitor-plugins"",
  ""total"": 1,
  ""plugins"": [
    {
      ""id"": ""public_ip_info"",
      ""original_id"": """",
      ""renamed"": false,
      ""name"": ""公网IP显示"",
      ""name_en"": ""Public IP"",
      ""description"": ""中文描述"",
      ""description_en"": ""English description"",
      ""author"": ""LiteMonitor"",
      ""version"": ""1.0"",
      ""file"": ""plugin.json"",
      ""download_url"": ""https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/public_ip_info/plugin.json"",
      ""readme_url"": ""https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/public_ip_info/README.md"",
      ""page_url"": ""https://github.com/mojingjing-git/awesome-litemonitor-plugins/tree/main/plugins/public_ip_info"",
      ""sha256"": ""3f786850e387550fdab836ed7e6dc881de23001b"",
      ""size"": 4148,
      ""status"": ""ok"",
      ""requires"": [],
      ""note"": """",
      ""verified"": false,
      ""source"": ""https://github.com/mojingjing-git/awesome-litemonitor-plugins/blob/main/plugins/public_ip_info/plugin.json""
    }
  ]
}";

        [Fact]
        public void 索引解析_七个新字段全部到位()
        {
            var index = JsonSerializer.Deserialize<MarketIndex>(SampleIndex,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            Assert.NotNull(index);
            Assert.Equal(1, index.Schema);
            Assert.Equal(1, index.Total);
            Assert.Single(index.Plugins);

            var e = index.Plugins[0];
            Assert.Equal("Public IP", e.NameEn);
            Assert.Equal("English description", e.DescriptionEn);
            Assert.Equal("3f786850e387550fdab836ed7e6dc881de23001b", e.Sha256);
            Assert.Equal(4148, e.Size);
            Assert.False(e.Verified);
            Assert.Equal("https://github.com/mojingjing-git/awesome-litemonitor-plugins/tree/main/plugins/public_ip_info", e.PageUrl);
            Assert.Equal("https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/public_ip_info/README.md", e.ReadmeUrl);

            // source 字段必须保留（市场页的「查看源码」链接读它）
            Assert.Contains("/blob/main/plugins/public_ip_info/plugin.json", e.Source);
        }

        [Fact]
        public void 索引解析_verified为true时正确读取()
        {
            var json = SampleIndex.Replace("\"verified\": false", "\"verified\": true");
            var index = JsonSerializer.Deserialize<MarketIndex>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.True(index.Plugins[0].Verified);
        }

        [Fact]
        public void 索引解析_缺少新字段时用默认值不抛异常()
        {
            const string minimal = @"{""schema"":1,""plugins"":[{""id"":""x"",""name"":""X"",""download_url"":""u""}]}";
            var index = JsonSerializer.Deserialize<MarketIndex>(minimal,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var e = index.Plugins[0];
            Assert.Equal("", e.NameEn);
            Assert.Equal("", e.DescriptionEn);
            Assert.Equal("", e.Sha256);
            Assert.Equal(0, e.Size);
            Assert.False(e.Verified);
            Assert.Equal("", e.PageUrl);
            Assert.Equal("", e.ReadmeUrl);
        }

        [Fact]
        public void 索引解析_插件id重复时能被检出()
        {
            // 索引自身一致性由仓库侧 CI 兜底，这里确认客户端能读出重复项以便提示
            const string dup = @"{""schema"":1,""plugins"":[{""id"":""x""},{""id"":""x""}]}";
            var index = JsonSerializer.Deserialize<MarketIndex>(dup,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.Equal(2, index.Plugins.Count(p => p.Id == "x"));
        }

        // ==========================================================
        // L2 规则：目标已存在且非本市场安装 → 拒绝
        // ==========================================================

        [Fact]
        public void IsTargetAvailable_目标不存在_放行()
        {
            // 用一个绝不会存在的 id，避免碰到真实的插件目录
            string id = "zz_test_nonexistent_" + Guid.NewGuid().ToString("N");
            Assert.True(PluginMarket.IsTargetAvailable(id, out _));
        }

        [Fact]
        public void IsTargetAvailable_目标存在但不在市场记录里_拒绝()
        {
            // 内置插件文件必然存在于 resources/plugins/ 下，且不在市场记录里
            // → L2 规则必须拒绝（这正是"不覆盖内置插件"的核心保障）
            Assert.False(PluginMarket.IsTargetAvailable("PublicIP", out string reason));
            Assert.False(string.IsNullOrEmpty(reason));
        }

        // ==========================================================
        // 卸载所有权判定
        // ==========================================================

        [Fact]
        public void Uninstall_没有安装记录_拒绝且不删文件()
        {
            var entry = new MarketPluginEntry { Id = "zz_not_installed_" + Guid.NewGuid().ToString("N") };
            Assert.False(PluginMarket.Uninstall(entry, out var reason));
            Assert.Equal(MarketErrorReason.NotManaged, reason);
        }

        [Fact]
        public void Uninstall_内置插件id_永远拒绝()
        {
            // 即便构造一条 id=PublicIP 的记录，内置插件永不可卸载
            var entry = new MarketPluginEntry { Id = "PublicIP" };
            bool ok = PluginMarket.Uninstall(entry, out var reason);

            // 没有市场记录时先被 NotManaged 拦下；
            // 有记录时（下一条测试覆盖）会被 BuiltinConflict 拦下
            Assert.False(ok);
            Assert.True(reason == MarketErrorReason.NotManaged || reason == MarketErrorReason.BuiltinConflict);
        }

        [Fact]
        public void Uninstall_ValidDirectChild_DeletesFileAndManagedRecord()
        {
            using var temp = new TemporaryDirectory("lm-plugin-uninstall-");
            string pluginsDir = temp.CreateSubdirectory("plugins");
            string filePath = Path.Combine(pluginsDir, "demo.json");
            File.WriteAllText(filePath, "{\"id\":\"demo\"}");

            var store = new InMemoryPluginRecordStore();
            store.Add(new MarketPluginRecord
            {
                Id = "demo",
                File = "demo.json"
            });

            bool removed = PluginMarketUninstallCore.Uninstall(
                new MarketPluginEntry { Id = "demo" },
                pluginsDir,
                store.Find,
                store.Remove,
                out var reason);

            Assert.True(removed, reason.ToString());
            Assert.False(File.Exists(filePath));
            Assert.Empty(store.Records);
            Assert.Equal(1, store.RemoveCalls);
        }

        [Fact]
        public void Uninstall_InvalidEntryId_StopsBeforeLookupOrDelete()
        {
            using var temp = new TemporaryDirectory("lm-plugin-invalid-entry-");
            string pluginsDir = temp.CreateSubdirectory("plugins");
            var store = new InMemoryPluginRecordStore();
            store.Add(new MarketPluginRecord
            {
                Id = "../demo",
                File = "../demo.json"
            });

            bool removed = PluginMarketUninstallCore.Uninstall(
                new MarketPluginEntry { Id = "../demo" },
                pluginsDir,
                store.Find,
                store.Remove,
                out var reason);

            Assert.False(removed);
            Assert.Equal(MarketErrorReason.InvalidId, reason);
            Assert.Equal(0, store.FindCalls);
            Assert.Equal(0, store.RemoveCalls);
            Assert.Single(store.Records);
        }

        [Theory]
        [InlineData("../corrupt", "demo.json")]
        [InlineData("other", "demo.json")]
        [InlineData("demo", "../plugins-evil/demo.json")]
        [InlineData("demo", "demo.json.txt")]
        public void Uninstall_CorruptRecord_LeavesFileAndRecordUntouched(
            string recordId,
            string recordFile)
        {
            using var temp = new TemporaryDirectory("lm-plugin-corrupt-record-");
            string pluginsDir = temp.CreateSubdirectory("plugins");
            string filePath = Path.Combine(pluginsDir, "demo.json");
            File.WriteAllText(filePath, "{\"id\":\"demo\"}");

            var store = new InMemoryPluginRecordStore();
            store.Add(new MarketPluginRecord
            {
                Id = recordId,
                File = recordFile
            }, lookupId: "demo");

            bool removed = PluginMarketUninstallCore.Uninstall(
                new MarketPluginEntry { Id = "demo" },
                pluginsDir,
                store.Find,
                store.Remove,
                out var reason);

            Assert.False(removed);
            Assert.Equal(MarketErrorReason.RecordCorrupted, reason);
            Assert.True(File.Exists(filePath));
            Assert.Single(store.Records);
            Assert.Equal(0, store.RemoveCalls);
        }

        [Fact]
        public void Uninstall_FileContentIdMismatch_LeavesFileAndRecordUntouched()
        {
            using var temp = new TemporaryDirectory("lm-plugin-content-id-");
            string pluginsDir = temp.CreateSubdirectory("plugins");
            string filePath = Path.Combine(pluginsDir, "demo.json");
            File.WriteAllText(filePath, "{\"id\":\"different\"}");

            var store = new InMemoryPluginRecordStore();
            store.Add(new MarketPluginRecord
            {
                Id = "demo",
                File = "demo.json"
            });

            bool removed = PluginMarketUninstallCore.Uninstall(
                new MarketPluginEntry { Id = "demo" },
                pluginsDir,
                store.Find,
                store.Remove,
                out var reason);

            Assert.False(removed);
            Assert.Equal(MarketErrorReason.RecordCorrupted, reason);
            Assert.True(File.Exists(filePath));
            Assert.Single(store.Records);
            Assert.Equal(0, store.RemoveCalls);
        }

        [Fact]
        public void Uninstall_BuiltinRecord_IsProtectedEvenWhenConsistent()
        {
            using var temp = new TemporaryDirectory("lm-plugin-builtin-");
            string pluginsDir = temp.CreateSubdirectory("plugins");
            string filePath = Path.Combine(pluginsDir, "PublicIP.json");
            File.WriteAllText(filePath, "{\"id\":\"PublicIP\"}");

            var store = new InMemoryPluginRecordStore();
            store.Add(new MarketPluginRecord
            {
                Id = "PublicIP",
                File = "PublicIP.json"
            });

            bool removed = PluginMarketUninstallCore.Uninstall(
                new MarketPluginEntry { Id = "PublicIP" },
                pluginsDir,
                store.Find,
                store.Remove,
                out var reason);

            Assert.False(removed);
            Assert.Equal(MarketErrorReason.BuiltinConflict, reason);
            Assert.True(File.Exists(filePath));
            Assert.Single(store.Records);
            Assert.Equal(0, store.RemoveCalls);
        }

        private sealed class InMemoryPluginRecordStore
        {
            internal Dictionary<string, MarketPluginRecord> Records { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            internal int FindCalls { get; private set; }
            internal int RemoveCalls { get; private set; }

            internal void Add(
                MarketPluginRecord record,
                string? lookupId = null)
            {
                Records[lookupId ?? record.Id] = record;
            }

            internal MarketPluginRecord? Find(string id)
            {
                FindCalls++;
                return Records.TryGetValue(id, out var record) ? record : null;
            }

            internal void Remove(string id)
            {
                RemoveCalls++;
                Records.Remove(id);
            }
        }

        private sealed class TemporaryDirectory : IDisposable
        {
            internal TemporaryDirectory(string prefix)
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    prefix + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            internal string CreateSubdirectory(string name)
            {
                string path = System.IO.Path.Combine(Path, name);
                Directory.CreateDirectory(path);
                return path;
            }

            public void Dispose()
            {
                try { Directory.Delete(Path, recursive: true); } catch { }
            }
        }
    }
}
