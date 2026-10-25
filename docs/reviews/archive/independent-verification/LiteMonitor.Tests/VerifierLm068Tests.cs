using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// ★ **独立复核 · LM-068 / R04 · 插件恢复日志路径越界 ⇒ 目录外写删**
/// <para>复核者自写用例。判据与实现者的 <c>PluginMarketRecoveryBoundaryTests</c> **不同**：
/// 本文件的"正常事务行为不变"不是靠断言，而是靠**跨实现对拍** ——
/// 用同一份场景脚本分别跑**修复前**（<c>3494fbc</c>）与**修复后**（<c>9e3ba32</c>）两个 worktree，
/// 把恢复后的目录清单（文件名 + 大小 + SHA-256）逐字节比对。</para>
/// </summary>
public sealed class VerifierLm068Tests
{
    private const string ValidTxId = "0123456789abcdef0123456789abcdef";
    private const string PluginId = "verifier_plugin";

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            }
            if (hit) return true;
        }
        return false;
    }

    private static MarketInstallTransaction NormalTransaction() => new()
    {
        TxId = ValidTxId,
        PluginId = PluginId,
        Target = PluginMarket.GetInstallFileName(PluginId),
        Stage = PluginMarket.GetInstallFileName(PluginId) + "." + ValidTxId + ".stage",
        Backup = PluginMarket.GetInstallFileName(PluginId) + "." + ValidTxId + ".bak",
        Sha256 = new string('a', 64),
    };

    private static bool Resolve(string pluginsDir, MarketInstallTransaction tx, out string reason)
    {
        bool ok = PluginMarketInstallCore.TryResolveTransactionPaths(
            pluginsDir, tx, out PluginMarketInstallCore.TransactionPaths paths, out reason);
        _ = paths;
        return ok;
    }

    // ---------------------------------------------------------------- V1

    /// <summary>
    /// V1（**载荷用例**）：绝对路径 / 含目录分隔符 / <c>..</c> / 字段与重建值不一致 /
    /// 非法事务 id / 非法插件 id —— 一律**整笔拒绝**。
    /// </summary>
    [Fact]
    public void V1_越界与畸形事务一律整笔拒绝()
    {
        string root = Path.Combine(Path.GetTempPath(), "verifier-lm068-" + Guid.NewGuid().ToString("N"));
        string pluginsDir = Path.Combine(root, "plugins");
        Directory.CreateDirectory(pluginsDir);
        try
        {
            // ① 正常事务必须被接受（用例前提）
            Assert.True(Resolve(pluginsDir, NormalTransaction(), out string okReason),
                "正常事务必须被接受 —— 用例前提被破坏：" + okReason);

            // ② 绝对路径的 Target
            var absolute = NormalTransaction();
            absolute.Target = Path.Combine(root, "victim.json");
            Assert.False(Resolve(pluginsDir, absolute, out _), "绝对路径的 Target 必须被拒绝。");

            // ③ 含目录分隔符（子目录）
            var subdir = NormalTransaction();
            subdir.Target = "sub/evil.json";
            Assert.False(Resolve(pluginsDir, subdir, out _), "含目录分隔符的 Target 必须被拒绝。");

            // ④ 含目录分隔符（Windows 反斜杠）
            var backslash = NormalTransaction();
            backslash.Stage = "..\\..\\evil.stage";
            Assert.False(Resolve(pluginsDir, backslash, out _), "含 `..\\` 的 Stage 必须被拒绝。");

            // ⑤ 父目录穿越
            var traversal = NormalTransaction();
            traversal.Backup = ".." + Path.DirectorySeparatorChar + "evil.bak";
            Assert.False(Resolve(pluginsDir, traversal, out _), "`..` 穿越必须被拒绝。");

            // ⑥ 相对目录标记
            var dot = NormalTransaction();
            dot.Target = ".";
            Assert.False(Resolve(pluginsDir, dot, out _), "`.` 必须被拒绝。");

            var dotdot = NormalTransaction();
            dotdot.Target = "..";
            Assert.False(Resolve(pluginsDir, dotdot, out _), "`..` 必须被拒绝。");

            // ⑦ 事务 id 不是 32 位十六进制
            var badTx = NormalTransaction();
            badTx.TxId = "NOT-HEX";
            Assert.False(Resolve(pluginsDir, badTx, out _), "非法事务 id 必须被拒绝。");

            var shortTx = NormalTransaction();
            shortTx.TxId = "0123";
            Assert.False(Resolve(pluginsDir, shortTx, out _), "过短的事务 id 必须被拒绝。");

            // ⑧ 插件 id 非法（带路径成分）
            var badId = NormalTransaction();
            badId.PluginId = "../evil";
            Assert.False(Resolve(pluginsDir, badId, out _), "非法插件 id 必须被拒绝。");

            // ⑨ 字段与重建值不一致（Target 指向别的插件）
            var mismatched = NormalTransaction();
            mismatched.Target = "other_plugin.json";
            Assert.False(Resolve(pluginsDir, mismatched, out _), "与重建值不一致的字段必须被拒绝。");

            // ⑩ 摘要缺失 / 非法
            var noSha = NormalTransaction();
            noSha.Sha256 = "";
            Assert.False(Resolve(pluginsDir, noSha, out _), "摘要缺失必须被拒绝。");

            // ⑪ 空条目
            Assert.False(Resolve(pluginsDir, null!, out _), "空条目必须被拒绝。");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- V2

    /// <summary>
    /// V2（**载荷用例**）：被接受的路径**只能**是"按插件 id + 事务 id 重建出来的直接子文件名"。
    /// <para>判据由复核者**独立重算**：父目录必须**恰好**是插件目录，叶子名不得含分隔符。</para>
    /// </summary>
    [Fact]
    public void V2_被接受的路径只能是插件目录的直接子文件()
    {
        string root = Path.Combine(Path.GetTempPath(), "verifier-lm068-" + Guid.NewGuid().ToString("N"));
        string pluginsDir = Path.Combine(root, "plugins");
        Directory.CreateDirectory(pluginsDir);
        try
        {
            MarketInstallTransaction tx = NormalTransaction();

            bool ok = PluginMarketInstallCore.TryResolveTransactionPaths(
                pluginsDir, tx, out PluginMarketInstallCore.TransactionPaths paths, out string reason);
            Assert.True(ok, "正常事务必须被接受：" + reason);

            string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsDir));

            var expected = new[]
            {
                Path.Combine(fullRoot, PluginMarket.GetInstallFileName(PluginId)),
                Path.Combine(fullRoot, PluginMarket.GetInstallFileName(PluginId) + "." + ValidTxId + ".stage"),
                Path.Combine(fullRoot, PluginMarket.GetInstallFileName(PluginId) + "." + ValidTxId + ".bak"),
            };

            var actual = new[] { paths.TargetPath, paths.StagePath, paths.BackupPath };

            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(expected[i], actual[i]);

                string parent = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(actual[i])!);
                Assert.True(
                    string.Equals(fullRoot, parent, StringComparison.Ordinal),
                    "被执行的路径的父目录必须**就是**插件目录（复核者独立重算）：" + actual[i]);

                string leaf = Path.GetFileName(actual[i]);
                Assert.DoesNotContain("/", leaf, StringComparison.Ordinal);
                Assert.DoesNotContain("\\", leaf, StringComparison.Ordinal);
                Assert.NotEqual(".", leaf);
                Assert.NotEqual("..", leaf);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- V3

    /// <summary>
    /// V3（**载荷用例**）：坏日志**隔离留证**（原始内容逐字节留存）且**不执行**其中任何路径。
    /// </summary>
    [Theory]
    [InlineData("relative-traversal")]
    [InlineData("absolute")]
    public void V3_坏日志被隔离留证且其中路径一个都不执行(string mode)
    {
        string root = Path.Combine(Path.GetTempPath(), "verifier-lm068-" + Guid.NewGuid().ToString("N"));
        string pluginsDir = Path.Combine(root, "plugins");
        string outsideDir = Path.Combine(root, "outside");
        Directory.CreateDirectory(pluginsDir);
        Directory.CreateDirectory(outsideDir);
        try
        {
            string victimPath = Path.Combine(outsideDir, "victim.json");
            byte[] victimBytes = Encoding.UTF8.GetBytes("{\"secret\":\"do-not-touch\"}");
            File.WriteAllBytes(victimPath, victimBytes);
            string victimHashBefore = Sha256Hex(File.ReadAllBytes(victimPath));

            MarketInstallTransaction tx = NormalTransaction();
            if (mode == "relative-traversal")
            {
                tx.Target = "..\\outside\\victim.json";
                tx.Stage = "..\\outside\\victim.json.stage";
                tx.Backup = "..\\outside\\victim.json.bak";
            }
            else
            {
                tx.Target = victimPath;
                tx.Stage = victimPath + ".stage";
                tx.Backup = victimPath + ".bak";
            }

            string logPath = Path.Combine(pluginsDir, PluginMarketInstallCore.TransactionLogFileName);
            string logJson = System.Text.Json.JsonSerializer.Serialize(
                new { Version = 1, Pending = new[] { tx } });
            byte[] logBytes = Encoding.UTF8.GetBytes(logJson);
            File.WriteAllBytes(logPath, logBytes);

            int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(pluginsDir, _ => null);

            Assert.True(handled == 0, "坏日志里的事务必须**整笔拒绝**（handled 必须为 0）。");

            // 目录外的文件一个字节都不能被动
            Assert.True(File.Exists(victimPath), "目录外的受害文件不得被删除。");
            Assert.True(
                string.Equals(victimHashBefore, Sha256Hex(File.ReadAllBytes(victimPath)), StringComparison.Ordinal),
                "目录外的受害文件内容不得被改写。");
            Assert.False(File.Exists(victimPath + ".stage"), "不得在目录外创建暂存文件。");
            Assert.False(File.Exists(victimPath + ".bak"), "不得在目录外创建备份文件。");

            // 坏日志隔离留证：原始内容**逐字节**留存
            string quarantine = Path.Combine(pluginsDir, PluginMarketInstallCore.QuarantineLogFileName);
            Assert.True(File.Exists(quarantine), "坏日志必须被隔离留证（" + quarantine + "）。");

            byte[] quarantineBytes = File.ReadAllBytes(quarantine);
            Assert.True(
                ContainsBytes(quarantineBytes, logBytes),
                "隔离文件里必须**逐字节**保留原始日志内容。");

            // 隔离文件不得以 .json 结尾（否则会被插件加载器扫到）
            Assert.False(
                Path.GetFileName(quarantine).EndsWith(".json", StringComparison.OrdinalIgnoreCase),
                "隔离文件不得以 .json 结尾（否则会被插件加载器扫到）。");

            // 被拒条目不再留在活动日志里
            Assert.False(File.Exists(logPath), "被拒条目不得留在活动日志里（不再重试）。");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- V4

    /// <summary>
    /// V4（**载荷用例**）：正常事务的恢复行为与**修复前逐字节一致**。
    /// <para>本用例把恢复后的目录清单（文件名 + 大小 + SHA-256）写到一个由环境变量
    /// <c>VERIFIER_LM068_PARITY_OUT</c> 指定的文件里；复核者把**同一份**用例复制到
    /// 修复前的 worktree（<c>3494fbc</c>）跑一遍，然后逐字节 diff 两份输出。</para>
    /// </summary>
    [Fact]
    public void V4_正常事务恢复行为与修复前逐字节一致()
    {
        string outPath = Environment.GetEnvironmentVariable("VERIFIER_LM068_PARITY_OUT");
        if (string.IsNullOrWhiteSpace(outPath))
        {
            // 没有指定输出路径时只跑行为，不产出对拍文件
            outPath = Path.Combine(Path.GetTempPath(), "verifier-lm068-parity-" + Guid.NewGuid().ToString("N") + ".txt");
        }

        var report = new StringBuilder();

        foreach (string scenario in new[] { "rollback-still-staged", "rollback-record-behind", "finish-record-updated" })
        {
            string root = Path.Combine(Path.GetTempPath(), "verifier-lm068-" + Guid.NewGuid().ToString("N"));
            string pluginsDir = Path.Combine(root, "plugins");
            Directory.CreateDirectory(pluginsDir);
            try
            {
                byte[] oldContent = Encoding.UTF8.GetBytes("{\"version\":\"1.0.0\",\"old\":true}");
                byte[] newContent = Encoding.UTF8.GetBytes("{\"version\":\"2.0.0\",\"new\":true}");
                string newSha = Sha256Hex(newContent);

                MarketInstallTransaction tx = NormalTransaction();
                tx.Sha256 = newSha;

                string targetPath = Path.Combine(pluginsDir, tx.Target);
                string stagePath = Path.Combine(pluginsDir, tx.Stage);
                string backupPath = Path.Combine(pluginsDir, tx.Backup);

                MarketPluginRecord? record = null;

                switch (scenario)
                {
                    case "rollback-still-staged":
                        // 暂存文件还在 ⇒ 原子替换从未发生 ⇒ 目标保持旧内容，暂存/备份被清理
                        File.WriteAllBytes(targetPath, oldContent);
                        File.WriteAllBytes(stagePath, newContent);
                        File.WriteAllBytes(backupPath, oldContent);
                        break;

                    case "rollback-record-behind":
                        // 目标已是新内容、记录没跟上 ⇒ 回滚（备份覆盖目标）
                        File.WriteAllBytes(targetPath, newContent);
                        File.WriteAllBytes(backupPath, oldContent);
                        break;

                    case "finish-record-updated":
                        // 目标已是新内容、记录也更新了 ⇒ 只清理备份
                        File.WriteAllBytes(targetPath, newContent);
                        File.WriteAllBytes(backupPath, oldContent);
                        record = new MarketPluginRecord
                        {
                            Id = PluginId,
                            File = tx.Target,
                            Version = "2.0.0",
                            Sha256 = newSha,
                            InstalledAt = "2026-01-01T00:00:00Z",
                        };
                        break;
                }

                string logPath = Path.Combine(pluginsDir, PluginMarketInstallCore.TransactionLogFileName);
                File.WriteAllText(logPath, System.Text.Json.JsonSerializer.Serialize(
                    new { Version = 1, Pending = new[] { tx } }));

                int handled = PluginMarketInstallCore.RecoverInterruptedInstalls(
                    pluginsDir, id => id == PluginId ? record : null);

                report.Append("=== scenario: ").Append(scenario).Append(" ===\n");
                report.Append("handled: ").Append(handled).Append('\n');
                report.Append("logRemains: ").Append(File.Exists(logPath)).Append('\n');

                foreach (string file in Directory.GetFiles(pluginsDir).OrderBy(x => x, StringComparer.Ordinal))
                {
                    string name = Path.GetFileName(file);
                    byte[] bytes = File.ReadAllBytes(file);
                    report.Append(name).Append('\t')
                          .Append(bytes.Length).Append('\t')
                          .Append(Sha256Hex(bytes)).Append('\n');
                }
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));

        // 顺手断言正常路径下**不会**产生隔离文件（隔离只针对坏日志）
        Assert.False(
            report.ToString().Contains("quarantine", StringComparison.OrdinalIgnoreCase),
            "正常事务路径下不得产生隔离文件。");
    }
}