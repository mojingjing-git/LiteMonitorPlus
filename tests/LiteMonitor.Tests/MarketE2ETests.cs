using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 端到端验证：真的从 GitHub 拉索引 → 安装 → 校验 → 卸载。
    ///
    /// 需要联网。默认跳过；设置环境变量后运行：
    ///   $env:LITEMONITOR_E2E = '1'
    ///   dotnet test --filter "FullyQualifiedName~MarketE2ETests"
    ///
    /// 覆盖真实链路：索引下载 → SHA-256 校验 → id 一致性 → 落盘 → 安装记录 →
    /// 重复安装（更新流程）→ 卸载 → 记录清理，并确认内置插件全程未被覆盖。
    /// </summary>
    public class MarketE2ETests
    {
        [E2EFact]
        public async Task 端到端_拉索引_安装_校验_卸载()
        {

            string pluginsDir = PluginMarket.PluginsDir;
            string builtin = Path.Combine(pluginsDir, "PublicIP.json");
            string builtinBackup = builtin + ".e2e-backup";
            bool builtinExisted = File.Exists(builtin);
            if (builtinExisted) File.Copy(builtin, builtinBackup, true);

            // 1. 拉索引
            var index = await PluginMarket.FetchIndexAsync(PluginMarket.DefaultIndexUrl);
            Assert.Equal(1, index.Schema);
            Assert.NotEmpty(index.Plugins);
            Console.WriteLine($"[1] 索引拉取成功：{index.Plugins.Count} 个插件，repo={index.Repo}");

            // 2. 选一个插件
            var entry = index.Plugins.First(e => e.Id == "public_ip_info");
            Console.WriteLine($"[2] 目标插件：{entry.Id} v{entry.Version}  sha256={entry.Sha256}");

            // 3. 安装
            string installed = Path.Combine(pluginsDir, PluginMarket.GetInstallFileName(entry.Id));
            if (File.Exists(installed)) File.Delete(installed);

            await PluginMarket.InstallAsync(entry);
            Assert.True(File.Exists(installed), "安装后文件未落盘");
            Console.WriteLine($"[3] 安装成功，落盘于 {installed}");

            // 4. 校验落盘内容
            using (var doc = JsonDocument.Parse(File.ReadAllText(installed)))
            {
                Assert.True(doc.RootElement.TryGetProperty("id", out var idEl));
                Assert.Equal(entry.Id, idEl.GetString());
            }
            Console.WriteLine("[4] 落盘内容 id 校验通过（SHA-256 与 id 一致性都在 InstallAsync 内已完成）");

            // 5. 安装记录
            var record = PluginMarketState.Find(entry.Id);
            Assert.NotNull(record);
            Assert.Equal(entry.Id, record.Id);
            Assert.Equal(PluginMarket.GetInstallFileName(entry.Id), record.File);
            Assert.Equal(entry.Sha256, record.Sha256);
            Console.WriteLine($"[5] 安装记录已写入：id={record.Id} file={record.File} installedAt={record.InstalledAt}");

            // 6. 内置插件未被影响
            Assert.True(File.Exists(builtin), "内置 PublicIP.json 不见了！");
            Console.WriteLine("[6] 内置 PublicIP.json 完好，未被覆盖");

            // 7. 幂等：再装一次（更新流程）应被允许
            await PluginMarket.InstallAsync(entry);
            Assert.True(File.Exists(installed));
            Console.WriteLine("[7] 重复安装（更新流程）被正确放行");

            // 8. 卸载
            bool ok = PluginMarket.Uninstall(entry, out var reason);
            Assert.True(ok, "卸载失败: " + reason);
            Assert.False(File.Exists(installed), "卸载后文件仍在");
            Assert.Null(PluginMarketState.Find(entry.Id));
            Console.WriteLine("[8] 卸载成功：文件已删除、安装记录已清除");

            // 9. 内置插件依旧完好
            Assert.True(File.Exists(builtin), "内置 PublicIP.json 不见了！");
            Console.WriteLine("[9] 卸载后内置 PublicIP.json 依旧完好");

            // 10. 内置 id 不可被市场安装
            var evil = new MarketPluginEntry
            {
                Id = "PublicIP",
                DownloadUrl = entry.DownloadUrl,
                Sha256 = entry.Sha256
            };
            var ex = await Assert.ThrowsAsync<PluginMarketException>(() => PluginMarket.InstallAsync(evil));
            Assert.Equal(MarketErrorReason.BuiltinConflict, ex.Reason);
            Console.WriteLine($"[10] 安装撞内置 id 的插件被拒绝：{ex.Reason}");

            // 11. 站外下载地址被拒
            var evil2 = new MarketPluginEntry
            {
                Id = "some_plugin",
                DownloadUrl = "https://evil.example.com/x.json"
            };
            var ex2 = await Assert.ThrowsAsync<PluginMarketException>(() => PluginMarket.InstallAsync(evil2));
            Assert.Equal(MarketErrorReason.BadSource, ex2.Reason);
            Console.WriteLine($"[11] 站外下载地址被拒绝：{ex2.Reason}");

            // 12. 篡改哈希必须被拒
            var tampered = new MarketPluginEntry
            {
                Id = "tampered_plugin",
                DownloadUrl = entry.DownloadUrl,
                Sha256 = new string('0', 64)
            };
            var ex3 = await Assert.ThrowsAsync<PluginMarketException>(() => PluginMarket.InstallAsync(tampered));
            Assert.Equal(MarketErrorReason.HashMismatch, ex3.Reason);
            Assert.False(File.Exists(Path.Combine(pluginsDir, "tampered_plugin.json")), "哈希不匹配却落盘了！");
            Console.WriteLine($"[11] 篡改 sha256 被拒绝且未落盘：{ex3.Reason}");

            // 清理
            if (File.Exists(installed)) File.Delete(installed);
            if (builtinExisted)
            {
                if (File.Exists(builtin)) File.Delete(builtin);
                File.Copy(builtinBackup, builtin, true);
                File.Delete(builtinBackup);
            }
            Console.WriteLine("=== 端到端验证全部通过 ===");
        }
    }
}
