using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// R04：插件市场安装事务的**恢复日志**必须被限制在插件目录之内。
    ///
    /// 缺陷（修复前）：写前日志里的 Target / Stage / Backup 被直接 Path.Combine 到插件目录，
    ///   而 Path.Combine 遇到绝对路径会**直接返回它**、遇到 ".." 会**越出**插件目录；
    ///   于是被篡改的恢复日志可以让程序删除 / 覆盖插件目录之外的文件。
    ///
    /// 修复后：
    ///   ① 校验插件 id 与事务 id，按二者**重建**三个直接子文件名，不信任日志里的路径字符串；
    ///   ② 拒绝绝对路径 / 含目录分隔符的路径 / 越界路径 / 不受信任的重解析点；
    ///   ③ 坏日志**隔离留证**（原始内容逐字节保留），其中的路径一个都不执行；
    ///   ④ 正常安装 / 卸载 / 回滚路径的行为不变（用例里逐字节比对目录清单）。
    ///
    /// 每条用例都必须能证伪：把对应的边界校验临时去掉（或改回信任日志字段），该用例必须失败。
    /// </summary>
    public class PluginMarketRecoveryBoundaryTests
    {
        private const string TxId = "0123456789abcdef0123456789abcdef";

        // ==========================================================
        // ① Stage 含目录分隔符 ⇒ 不得删除插件目录之外的文件
        // ==========================================================

        [Fact]
        public void 恢复日志_Stage含目录分隔符_插件目录外的文件不被删除()
        {
            using var scope = new RecoveryScope();

            // 受害者：pluginsDir\..\..\settings.json ⇒ 落在插件目录之外
            string victim = scope.OutsideVictim;
            byte[] victimBytes = Encoding.UTF8.GetBytes("{\"secret\":\"do-not-touch\"}");
            File.WriteAllBytes(victim, victimBytes);
            string victimSha = Sha256File(victim);

            WriteCraftedLog(scope.PluginsDir, new CraftedTx
            {
                Target = "demo_plugin.json",
                Stage = "..\\..\\settings.json",
                Backup = "demo_plugin.json." + TxId + ".bak"
            });

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, id => null);

            Assert.Equal(0, handled);
            Assert.True(File.Exists(victim), "插件目录之外的文件被恢复日志删除了！");
            Assert.Equal(victimSha, Sha256File(victim));
            Assert.Equal(victimBytes, File.ReadAllBytes(victim));
        }

        // ==========================================================
        // ② Target 是绝对路径 ⇒ 拒绝，不得删除该绝对路径上的文件
        // ==========================================================

        [Fact]
        public void 恢复日志_Target为绝对路径_绝对路径上的文件不被删除()
        {
            using var scope = new RecoveryScope();

            string victim = Path.Combine(scope.Root, "absolute-victim.txt");
            byte[] victimBytes = Encoding.UTF8.GetBytes("victim-bytes");
            File.WriteAllBytes(victim, victimBytes);
            string victimSha = Sha256File(victim);

            WriteCraftedLog(scope.PluginsDir, new CraftedTx
            {
                Target = victim,
                Stage = victim,
                Backup = victim,
                // 摘要填成受害者的真实摘要 ⇒ 旧实现会认为"目标已经是新内容"从而删掉它
                Sha256 = victimSha
            });

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, id => null);

            Assert.Equal(0, handled);
            Assert.True(File.Exists(victim), "绝对路径上的文件被恢复日志删除了！");
            Assert.Equal(victimSha, Sha256File(victim));
            Assert.Equal(victimBytes, File.ReadAllBytes(victim));
        }

        // ==========================================================
        // ③ 含目录分隔符 / 嵌套路径 / 绝对路径 ⇒ 全部拒绝
        // ==========================================================

        [Theory]
        [InlineData("..\\..\\settings.json")]
        [InlineData("..\\settings.json")]
        [InlineData("sub\\dir.json")]
        [InlineData("sub/dir.json")]
        [InlineData("C:\\Windows\\x")]
        [InlineData("\\\\server\\share\\x.json")]
        [InlineData("/etc/passwd")]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("")]
        public void 恢复日志_路径字段含目录分隔符或绝对路径_全部拒绝并隔离(string badPath)
        {
            using var scope = new RecoveryScope();

            string victim = scope.OutsideVictim;
            File.WriteAllText(victim, "victim");
            string victimSha = Sha256File(victim);

            WriteCraftedLog(scope.PluginsDir, new CraftedTx
            {
                Target = badPath,
                Stage = badPath,
                Backup = badPath
            });

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, id => null);

            Assert.Equal(0, handled);
            Assert.True(File.Exists(victim), "插件目录之外的文件被动过了！");
            Assert.Equal(victimSha, Sha256File(victim));
            Assert.True(File.Exists(scope.QuarantinePath), "坏日志没有隔离留证！");
        }

        // ==========================================================
        // ④ 插件 id / 事务 id / 字段一致性 / 摘要 ⇒ 校验不过就整笔拒绝
        // ==========================================================

        [Fact]
        public void 恢复日志_插件id非法_整笔拒绝且不删除插件目录内的文件()
        {
            using var scope = new RecoveryScope();

            // 插件目录内的合法文件：旧实现会拿日志里的 Target 把它删掉
            string inside = Path.Combine(scope.PluginsDir, "demo_plugin.json");
            byte[] insideBytes = Encoding.UTF8.GetBytes("{\"id\":\"demo_plugin\"}");
            File.WriteAllBytes(inside, insideBytes);
            string insideSha = Sha256File(inside);

            WriteCraftedLog(scope.PluginsDir, new CraftedTx
            {
                PluginId = "..\\..\\evil",     // 非法 id
                Target = "demo_plugin.json",
                Stage = "demo_plugin.json." + TxId + ".stage",
                Backup = "demo_plugin.json." + TxId + ".bak",
                Sha256 = insideSha
            });

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, id => null);

            Assert.Equal(0, handled);
            Assert.True(File.Exists(inside), "插件目录内的文件被篡改日志删掉了！");
            Assert.Equal(insideSha, Sha256File(inside));
        }

        [Fact]
        public void 恢复日志_事务id非法_整笔拒绝且不删除插件目录内的文件()
        {
            using var scope = new RecoveryScope();

            string inside = Path.Combine(scope.PluginsDir, "demo_plugin.json");
            byte[] insideBytes = Encoding.UTF8.GetBytes("{\"id\":\"demo_plugin\"}");
            File.WriteAllBytes(inside, insideBytes);
            string insideSha = Sha256File(inside);

            WriteCraftedLog(scope.PluginsDir, new CraftedTx
            {
                TxId = "not-a-guid",
                Target = "demo_plugin.json",
                Stage = "demo_plugin.json.not-a-guid.stage",
                Backup = "demo_plugin.json.not-a-guid.bak",
                Sha256 = insideSha
            });

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, id => null);

            Assert.Equal(0, handled);
            Assert.True(File.Exists(inside), "插件目录内的文件被篡改日志删掉了！");
            Assert.Equal(insideSha, Sha256File(inside));
        }

        [Fact]
        public void 恢复日志_路径字段与按id重建的文件名不一致_整笔拒绝()
        {
            using var scope = new RecoveryScope();

            // 与重建名不一致、但仍是合法叶子名 —— 只有"按 id 重建"才拦得住
            string other = Path.Combine(scope.PluginsDir, "other.json");
            byte[] otherBytes = Encoding.UTF8.GetBytes("{\"id\":\"other\"}");
            File.WriteAllBytes(other, otherBytes);
            string otherSha = Sha256File(other);

            WriteCraftedLog(scope.PluginsDir, new CraftedTx
            {
                Target = "other.json",
                Stage = "other.json." + TxId + ".stage",
                Backup = "other.json." + TxId + ".bak",
                Sha256 = otherSha
            });

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, id => null);

            Assert.Equal(0, handled);
            Assert.True(File.Exists(other), "与 id 不匹配的文件被删除了！");
            Assert.Equal(otherSha, Sha256File(other));
        }

        [Fact]
        public void 恢复日志_摘要非法_整笔拒绝()
        {
            using var scope = new RecoveryScope();

            string inside = Path.Combine(scope.PluginsDir, "demo_plugin.json");
            File.WriteAllText(inside, "{\"id\":\"demo_plugin\"}");

            WriteCraftedLog(scope.PluginsDir, new CraftedTx
            {
                Sha256 = "not-a-sha"
            });

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, id => null);

            Assert.Equal(0, handled);
            Assert.True(File.Exists(inside));
            Assert.True(File.Exists(scope.QuarantinePath), "坏日志没有隔离留证！");
        }

        // ==========================================================
        // ⑤ 坏日志隔离留证：原始内容逐字节保留
        // ==========================================================

        [Fact]
        public void 坏日志_隔离留证_原始内容逐字节保留()
        {
            using var scope = new RecoveryScope();

            string raw = BuildLog(new CraftedTx
            {
                Target = "demo_plugin.json",
                Stage = "..\\..\\settings.json",
                Backup = "demo_plugin.json." + TxId + ".bak"
            });
            byte[] rawBytes = new UTF8Encoding(false).GetBytes(raw);
            File.WriteAllBytes(scope.LogPath, rawBytes);

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, id => null);

            Assert.Equal(0, handled);

            // 隔离文件存在，且**逐字节**包含原始日志全文
            Assert.True(File.Exists(scope.QuarantinePath), "坏日志没有隔离留证！");
            byte[] quarantine = File.ReadAllBytes(scope.QuarantinePath);
            Assert.True(IndexOfSubsequence(quarantine, rawBytes) >= 0,
                "隔离文件里没有逐字节保留原始日志内容");

            // 隔离文件不得被插件加载器当成插件
            Assert.NotEqual(".json", Path.GetExtension(scope.QuarantinePath));

            // 拒绝的条目不得再留在活动日志里（否则每次启动都会重试）
            Assert.False(File.Exists(scope.LogPath), "被拒绝的条目仍留在活动日志里！");
        }

        [Fact]
        public void 坏日志_无法解析成JSON_也被隔离留证且不执行()
        {
            using var scope = new RecoveryScope();

            byte[] garbage = Encoding.UTF8.GetBytes("this is not a json log   <not-json>");
            File.WriteAllBytes(scope.LogPath, garbage);

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, id => null);

            Assert.Equal(0, handled);
            Assert.True(File.Exists(scope.QuarantinePath), "坏日志没有隔离留证！");
            Assert.True(IndexOfSubsequence(File.ReadAllBytes(scope.QuarantinePath), garbage) >= 0,
                "隔离文件里没有逐字节保留原始日志内容");
            Assert.False(File.Exists(scope.LogPath));
        }

        [Fact]
        public void 坏日志_合法与非法条目并存_只执行合法的那条()
        {
            using var scope = new RecoveryScope();

            // 合法条目：暂存文件还在 ⇒ 恢复时应当清掉暂存/备份，保留目标文件
            string stage = Path.Combine(scope.PluginsDir, "good_plugin.json." + TxId + ".stage");
            string target = Path.Combine(scope.PluginsDir, "good_plugin.json");
            File.WriteAllText(stage, "staged");
            File.WriteAllText(target, "{\"id\":\"good_plugin\"}");

            WriteCraftedLog(scope.PluginsDir,
                new CraftedTx
                {
                    PluginId = "good_plugin",
                    Target = "good_plugin.json",
                    Stage = "good_plugin.json." + TxId + ".stage",
                    Backup = "good_plugin.json." + TxId + ".bak",
                    Sha256 = Sha256Bytes(Encoding.UTF8.GetBytes("staged"))
                },
                new CraftedTx
                {
                    PluginId = "evil_plugin",
                    Target = "..\\..\\settings.json",
                    Stage = "..\\..\\settings.json",
                    Backup = "..\\..\\settings.json"
                });

            string victim = scope.OutsideVictim;
            File.WriteAllText(victim, "victim");
            string victimSha = Sha256File(victim);

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, id => null);

            // 只有合法那笔被对账
            Assert.Equal(1, handled);
            Assert.False(File.Exists(stage), "合法事务的暂存文件没有被清理！");
            Assert.True(File.Exists(target));
            Assert.Equal("{\"id\":\"good_plugin\"}", File.ReadAllText(target));

            // 非法那笔：路径一个都不执行
            Assert.True(File.Exists(victim), "插件目录之外的文件被删除了！");
            Assert.Equal(victimSha, Sha256File(victim));

            // 非法那笔被隔离留证，且不再留在活动日志里
            Assert.True(File.Exists(scope.QuarantinePath), "坏日志没有隔离留证！");
            Assert.False(File.Exists(scope.LogPath));
        }

        // ==========================================================
        // ⑥ 不受信任的重解析点 ⇒ 整笔拒绝
        // ==========================================================

        [Fact]
        public void 恢复日志_目标是重解析点_整笔拒绝且不执行()
        {
            using var scope = new RecoveryScope();

            string outside = Path.Combine(scope.Root, "outside");
            Directory.CreateDirectory(outside);
            string victim = Path.Combine(outside, "keep.txt");
            File.WriteAllText(victim, "keep-me");
            string victimSha = Sha256File(victim);

            // 插件目录里放一个指向外部目录的联接（目录联接不需要管理员权限）
            string junction = Path.Combine(scope.PluginsDir, "demo_plugin.json");
            CreateDirectoryJunction(junction, outside);

            try
            {
                WriteCraftedLog(scope.PluginsDir, new CraftedTx());

                int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, id => null);

                Assert.Equal(0, handled);
                Assert.True(File.Exists(victim), "重解析点指向的文件被删除了！");
                Assert.Equal(victimSha, Sha256File(victim));
                Assert.True(File.Exists(scope.QuarantinePath), "坏日志没有隔离留证！");
            }
            finally
            {
                // 只删联接本身，不动它指向的目录
                try { Directory.Delete(junction); } catch { }
            }
        }

        // ==========================================================
        // ⑦ 正常事务：行为与修复前逐字节一致
        // ==========================================================

        [Fact]
        public void 写前日志_路径字段与按id重建的文件名逐字节一致()
        {
            using var scope = new RecoveryScope();
            var store = new InMemoryPluginRecordStore();

            byte[] body = Encoding.UTF8.GetBytes("{\"id\":\"demo_naming\"}");
            string sha = Sha256Bytes(body);

            Assert.Throws<MarketInstallCrashException>(() =>
                PluginMarketInstallCore.CommitInstall(
                    scope.PluginsDir, "demo_naming", "1.0", body, sha,
                    store.Find, store.Upsert, store.Remove,
                    point => { if (point == "afterStage") throw new MarketInstallCrashException(point); }));

            // 修复所依赖的不变式：日志字段必须等于按【插件 id + 事务 id】重建的文件名
            using var doc = JsonDocument.Parse(File.ReadAllText(scope.LogPath));
            JsonElement tx = doc.RootElement.GetProperty("Pending")[0];
            string txId = tx.GetProperty("TxId").GetString();
            Assert.NotNull(txId);
            Assert.Equal(32, txId.Length);
            Assert.Equal("demo_naming.json", tx.GetProperty("Target").GetString());
            Assert.Equal("demo_naming.json." + txId + ".stage", tx.GetProperty("Stage").GetString());
            Assert.Equal("demo_naming.json." + txId + ".bak", tx.GetProperty("Backup").GetString());
        }

        [Fact]
        public void 正常事务_替换后崩溃_恢复后目录内容与修复前逐字节一致()
        {
            using var scope = new RecoveryScope();
            var store = new InMemoryPluginRecordStore();

            byte[] oldBytes = Encoding.UTF8.GetBytes("{\"id\":\"demo_tx\",\"v\":1}");
            string oldSha = Sha256Bytes(oldBytes);
            byte[] newBytes = Encoding.UTF8.GetBytes("{\"id\":\"demo_tx\",\"v\":2}");
            string newSha = Sha256Bytes(newBytes);

            string target = Path.Combine(scope.PluginsDir, "demo_tx.json");
            File.WriteAllBytes(target, oldBytes);
            store.Add(new MarketPluginRecord { Id = "demo_tx", File = "demo_tx.json", Sha256 = oldSha, Version = "1.0" });

            Assert.Throws<MarketInstallCrashException>(() =>
                PluginMarketInstallCore.CommitInstall(
                    scope.PluginsDir, "demo_tx", "2.0", newBytes, newSha,
                    store.Find, store.Upsert, store.Remove,
                    point => { if (point == "afterMove") throw new MarketInstallCrashException(point); }));

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, store.Find);

            Assert.Equal(1, handled);

            // 目录内容逐字节一致：只剩目标文件，内容是旧内容
            AssertManifest(scope.PluginsDir, "demo_tx.json", oldSha);

            // 记录也回滚到旧记录
            MarketPluginRecord record = store.Find("demo_tx");
            Assert.NotNull(record);
            Assert.Equal(oldSha, record.Sha256);
            Assert.Equal("1.0", record.Version);

            // 正常事务不该产生隔离文件
            Assert.False(File.Exists(scope.QuarantinePath), "正常事务不该产生隔离文件！");
        }

        [Fact]
        public void 正常事务_暂存阶段崩溃_恢复后目录内容与修复前逐字节一致()
        {
            using var scope = new RecoveryScope();
            var store = new InMemoryPluginRecordStore();

            byte[] oldBytes = Encoding.UTF8.GetBytes("{\"id\":\"demo_tx2\",\"v\":1}");
            string oldSha = Sha256Bytes(oldBytes);
            byte[] newBytes = Encoding.UTF8.GetBytes("{\"id\":\"demo_tx2\",\"v\":2}");
            string newSha = Sha256Bytes(newBytes);

            string target = Path.Combine(scope.PluginsDir, "demo_tx2.json");
            File.WriteAllBytes(target, oldBytes);
            store.Add(new MarketPluginRecord { Id = "demo_tx2", File = "demo_tx2.json", Sha256 = oldSha, Version = "1.0" });

            Assert.Throws<MarketInstallCrashException>(() =>
                PluginMarketInstallCore.CommitInstall(
                    scope.PluginsDir, "demo_tx2", "2.0", newBytes, newSha,
                    store.Find, store.Upsert, store.Remove,
                    point => { if (point == "afterStage") throw new MarketInstallCrashException(point); }));

            // 现场：暂存与备份文件都在
            Assert.NotEmpty(Directory.GetFiles(scope.PluginsDir, "*.stage"));
            Assert.NotEmpty(Directory.GetFiles(scope.PluginsDir, "*.bak"));

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, store.Find);

            Assert.Equal(1, handled);
            AssertManifest(scope.PluginsDir, "demo_tx2.json", oldSha);

            MarketPluginRecord record = store.Find("demo_tx2");
            Assert.NotNull(record);
            Assert.Equal(oldSha, record.Sha256);
            Assert.False(File.Exists(scope.QuarantinePath), "正常事务不该产生隔离文件！");
        }

        [Fact]
        public void 正常事务_写记录后崩溃_恢复后目录内容与修复前逐字节一致()
        {
            using var scope = new RecoveryScope();
            var store = new InMemoryPluginRecordStore();

            byte[] oldBytes = Encoding.UTF8.GetBytes("{\"id\":\"demo_tx3\",\"v\":1}");
            string oldSha = Sha256Bytes(oldBytes);
            byte[] newBytes = Encoding.UTF8.GetBytes("{\"id\":\"demo_tx3\",\"v\":2}");
            string newSha = Sha256Bytes(newBytes);

            string target = Path.Combine(scope.PluginsDir, "demo_tx3.json");
            File.WriteAllBytes(target, oldBytes);
            store.Add(new MarketPluginRecord { Id = "demo_tx3", File = "demo_tx3.json", Sha256 = oldSha, Version = "1.0" });

            Assert.Throws<MarketInstallCrashException>(() =>
                PluginMarketInstallCore.CommitInstall(
                    scope.PluginsDir, "demo_tx3", "2.0", newBytes, newSha,
                    store.Find, store.Upsert, store.Remove,
                    point => { if (point == "afterRecord") throw new MarketInstallCrashException(point); }));

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(scope.PluginsDir, store.Find);

            Assert.Equal(1, handled);

            // 这次安装实际已完成：保留新内容、清掉备份
            AssertManifest(scope.PluginsDir, "demo_tx3.json", newSha);

            MarketPluginRecord record = store.Find("demo_tx3");
            Assert.NotNull(record);
            Assert.Equal(newSha, record.Sha256);
            Assert.False(File.Exists(scope.QuarantinePath), "正常事务不该产生隔离文件！");
        }

        [Fact]
        public void 正常事务_安装失败回滚_目录内容与修复前逐字节一致()
        {
            using var scope = new RecoveryScope();
            var store = new InMemoryPluginRecordStore();

            byte[] oldBytes = Encoding.UTF8.GetBytes("{\"id\":\"demo_tx4\",\"v\":1}");
            string oldSha = Sha256Bytes(oldBytes);
            byte[] newBytes = Encoding.UTF8.GetBytes("{\"id\":\"demo_tx4\",\"v\":2}");
            string newSha = Sha256Bytes(newBytes);

            string target = Path.Combine(scope.PluginsDir, "demo_tx4.json");
            File.WriteAllBytes(target, oldBytes);
            store.Add(new MarketPluginRecord { Id = "demo_tx4", File = "demo_tx4.json", Sha256 = oldSha, Version = "1.0" });

            var boom = new InvalidOperationException("boom");
            Assert.Throws<InvalidOperationException>(() =>
                PluginMarketInstallCore.CommitInstall(
                    scope.PluginsDir, "demo_tx4", "2.0", newBytes, newSha,
                    store.Find, store.Upsert, store.Remove,
                    point => { if (point == "afterMove") throw boom; }));

            // 回滚后目录里只剩旧内容
            AssertManifest(scope.PluginsDir, "demo_tx4.json", oldSha);

            MarketPluginRecord record = store.Find("demo_tx4");
            Assert.NotNull(record);
            Assert.Equal(oldSha, record.Sha256);
            Assert.Equal("1.0", record.Version);
            Assert.False(File.Exists(scope.QuarantinePath), "正常事务不该产生隔离文件！");
        }

        // ==========================================================
        // 夹具
        // ==========================================================

        /// <summary>目录内容清单（相对路径 → SHA-256），用于"逐字节一致"的比对</summary>
        private static Dictionary<string, string> Snapshot(string dir)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Directory.Exists(dir)) return map;
            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                map[rel] = Sha256File(file);
            }
            return map;
        }

        /// <summary>断言目录内容恰好是期望的那几个文件，且每个文件的内容摘要一致</summary>
        private static void AssertManifest(string dir, string expectedRelativePath, string expectedSha)
        {
            Dictionary<string, string> actual = Snapshot(dir);
            string onlyKey = Assert.Single(actual.Keys);
            Assert.Equal(expectedRelativePath, onlyKey);
            Assert.Equal(expectedSha, actual[onlyKey]);
        }

        private static string Sha256File(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }

        private static string Sha256Bytes(byte[] bytes)
            => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static int IndexOfSubsequence(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0) return 0;
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool matched = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { matched = false; break; }
                }
                if (matched) return i;
            }
            return -1;
        }

        /// <summary>一条被"攻击者"写进写前日志的事务条目</summary>
        private sealed class CraftedTx
        {
            internal string TxId = PluginMarketRecoveryBoundaryTests.TxId;
            internal string PluginId = "demo_plugin";
            internal string Target = "demo_plugin.json";
            internal string Stage = "demo_plugin.json." + PluginMarketRecoveryBoundaryTests.TxId + ".stage";
            internal string Backup = "demo_plugin.json." + PluginMarketRecoveryBoundaryTests.TxId + ".bak";
            internal string Sha256 = new string('a', 64);
        }

        private static void WriteCraftedLog(string pluginsDir, params CraftedTx[] txs)
        {
            string json = BuildLog(txs);
            File.WriteAllText(
                Path.Combine(pluginsDir, PluginMarketInstallCore.TransactionLogFileName),
                json, new UTF8Encoding(false));
        }

        private static string BuildLog(params CraftedTx[] txs)
        {
            var sb = new StringBuilder();
            sb.Append("{\"Version\":1,\"Pending\":[");
            for (int i = 0; i < txs.Length; i++)
            {
                if (i > 0) sb.Append(',');
                CraftedTx t = txs[i];
                sb.Append("{\"TxId\":").Append(JsonString(t.TxId))
                  .Append(",\"PluginId\":").Append(JsonString(t.PluginId))
                  .Append(",\"Target\":").Append(JsonString(t.Target))
                  .Append(",\"Stage\":").Append(JsonString(t.Stage))
                  .Append(",\"Backup\":").Append(JsonString(t.Backup))
                  .Append(",\"Sha256\":").Append(JsonString(t.Sha256))
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string JsonString(string value)
        {
            var sb = new StringBuilder("\"");
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
            sb.Append('"');
            return sb.ToString();
        }

        private sealed class InMemoryPluginRecordStore
        {
            private readonly Dictionary<string, MarketPluginRecord> _records =
                new Dictionary<string, MarketPluginRecord>(StringComparer.OrdinalIgnoreCase);

            internal void Add(MarketPluginRecord record) => _records[record.Id] = record;

            internal MarketPluginRecord Find(string id)
                => _records.TryGetValue(id, out MarketPluginRecord record) ? record : null;

            internal void Upsert(MarketPluginRecord record) => _records[record.Id] = record;

            internal void Remove(string id) => _records.Remove(id);
        }

        /// <summary>
        /// 隔离出来的临时目录树：
        ///   Root\app\plugins   ← 插件目录（两级 ⇒ pluginsDir\..\..\settings.json == Root\settings.json）
        ///   Root\settings.json ← 插件目录之外的受害者
        /// </summary>
        private sealed class RecoveryScope : IDisposable
        {
            internal RecoveryScope()
            {
                Root = Path.Combine(Path.GetTempPath(), "lm-r04-" + Guid.NewGuid().ToString("N"));
                PluginsDir = Path.Combine(Root, "app", "plugins");
                Directory.CreateDirectory(PluginsDir);
            }

            internal string Root { get; }
            internal string PluginsDir { get; }

            /// <summary>插件目录之外的受害者文件</summary>
            internal string OutsideVictim => Path.Combine(Root, "settings.json");

            internal string LogPath
                => Path.Combine(PluginsDir, PluginMarketInstallCore.TransactionLogFileName);

            internal string QuarantinePath
                => Path.Combine(PluginsDir, PluginMarketInstallCore.QuarantineLogFileName);

            public void Dispose()
            {
                try { Directory.Delete(Root, recursive: true); } catch { }
            }
        }

        // ==========================================================
        // 目录联接（junction）—— Windows 的重解析点，且不需要管理员权限
        // ==========================================================

        private const uint IoReparseTagMountPoint = 0xA0000003;
        private const uint FsctlSetReparsePoint = 0x000900A4;
        private const uint GenericWrite = 0x40000000;
        private const uint OpenExisting = 3;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagBackupSemantics = 0x02000000;

        /// <summary>
        /// 创建一个指向目标目录的目录联接（junction）。
        /// 联接是重解析点的一种，而且**不需要** SeCreateSymbolicLinkPrivilege，
        /// 因此在任何 Windows 机器上都能稳定复现（符号链接则需要开发者模式/管理员）。
        ///
        /// 数据布局按 Windows 实际写出的挂载点格式（实测 fsutil reparsepoint query 的十六进制转储）：
        ///   USHORT SubstituteNameOffset | USHORT SubstituteNameLength |
        ///   USHORT PrintNameOffset      | USHORT PrintNameLength     | PathBuffer
        /// （挂载点格式**没有** Flags 字段；那是符号链接格式才有的。）
        /// </summary>
        private static void CreateDirectoryJunction(string junctionPath, string targetDirectory)
        {
            string target = Path.GetFullPath(targetDirectory);
            if (!target.EndsWith("\\", StringComparison.Ordinal)) target += "\\";

            byte[] substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
            byte[] print = Encoding.Unicode.GetBytes(target);

            int printOffset = substitute.Length + 2;
            int pathLength = substitute.Length + 2 + print.Length + 2;
            int dataLength = 8 + pathLength;   // 8 = 4 个 USHORT

            byte[] buffer = new byte[8 + dataLength];
            BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);                    // SubstituteNameOffset
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);   // SubstituteNameLength
            BitConverter.GetBytes((ushort)printOffset).CopyTo(buffer, 12);         // PrintNameOffset
            BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);         // PrintNameLength
            Array.Copy(substitute, 0, buffer, 16, substitute.Length);
            Array.Copy(print, 0, buffer, 16 + substitute.Length + 2, print.Length);

            Directory.CreateDirectory(junctionPath);

            IntPtr handle = CreateFileW(
                junctionPath,
                GenericWrite,
                0,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle == IntPtr.Zero || handle.ToInt64() == -1)
                throw new IOException("CreateFileW 失败：" + Marshal.GetLastWin32Error());

            try
            {
                int bytesReturned;
                bool ok = DeviceIoControl(handle, FsctlSetReparsePoint,
                    buffer, buffer.Length, null, 0, out bytesReturned, IntPtr.Zero);
                if (!ok)
                    throw new IOException("FSCTL_SET_REPARSE_POINT 失败：" + Marshal.GetLastWin32Error());
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            int nInBufferSize,
            byte[] lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
