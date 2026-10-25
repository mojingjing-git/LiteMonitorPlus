using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// ★ **独立复核 · LM-068 / R04 · 正常事务恢复行为与修复前逐字节一致（跨实现对拍）**
/// <para>本文件**故意只使用修复前后都存在的那部分 API**，因此可以把**同一份文件**
/// 分别放进【修复前】(3494fbc) 与【修复后】(9e3ba32) 两个 worktree 跑，
/// 然后把产出的"恢复后目录清单"逐字节 diff。</para>
/// <para>产出文件路径由环境变量 <c>VERIFIER_LM068_PARITY_OUT</c> 指定。</para>
/// </summary>
public sealed class VerifierLm068ParityTests
{
    private const string ValidTxId = "0123456789abcdef0123456789abcdef";
    private const string PluginId = "verifier_plugin";

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public void V4_正常事务恢复行为与修复前逐字节一致()
    {
        string outPath = Environment.GetEnvironmentVariable("VERIFIER_LM068_PARITY_OUT");
        if (string.IsNullOrWhiteSpace(outPath))
        {
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

                var tx = new MarketInstallTransaction
                {
                    TxId = ValidTxId,
                    PluginId = PluginId,
                    Target = PluginMarket.GetInstallFileName(PluginId),
                    Stage = PluginMarket.GetInstallFileName(PluginId) + "." + ValidTxId + ".stage",
                    Backup = PluginMarket.GetInstallFileName(PluginId) + "." + ValidTxId + ".bak",
                    Sha256 = newSha,
                };

                string targetPath = Path.Combine(pluginsDir, tx.Target);
                string stagePath = Path.Combine(pluginsDir, tx.Stage);
                string backupPath = Path.Combine(pluginsDir, tx.Backup);

                MarketPluginRecord? record = null;

                switch (scenario)
                {
                    case "rollback-still-staged":
                        File.WriteAllBytes(targetPath, oldContent);
                        File.WriteAllBytes(stagePath, newContent);
                        File.WriteAllBytes(backupPath, oldContent);
                        break;

                    case "rollback-record-behind":
                        File.WriteAllBytes(targetPath, newContent);
                        File.WriteAllBytes(backupPath, oldContent);
                        break;

                    case "finish-record-updated":
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
                    byte[] bytes = File.ReadAllBytes(file);
                    report.Append(Path.GetFileName(file)).Append('\t')
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

        Assert.False(
            report.ToString().Contains("quarantine", StringComparison.OrdinalIgnoreCase),
            "正常事务路径下不得产生隔离文件。");
    }
}