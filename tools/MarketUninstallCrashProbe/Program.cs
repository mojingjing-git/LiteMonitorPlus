using System;
using System.IO;
using System.Text.Json;
using LiteMonitor.src.Plugins;

namespace MarketUninstallCrashProbe
{
    /// <summary>
    /// [S03] 卸载强杀子进程探针。
    /// 用法：MarketUninstallCrashProbe crash &lt;pluginsDir&gt; &lt;configDir&gt; &lt;boundary&gt;
    /// 记录存放在 &lt;configDir&gt;/probe-record.json（{"Id","File","Sha256"}）。
    /// 在卸载事务的 &lt;boundary&gt;（afterJournal/afterBackup/afterPayloadDelete/
    /// afterRecord/afterBackupDelete）注入 <see cref="MarketInstallCrashException"/>
    /// 后立即以非零退出码结束，不回滚、不清理——磁盘留下"被杀瞬间"的状态。
    /// </summary>
    internal static class Program
    {
        private sealed class ProbeRecord
        {
            public string Id { get; set; } = "";
            public string File { get; set; } = "";
            public string Sha256 { get; set; } = "";
        }

        private static int Main(string[] args)
        {
            if (args.Length != 4 || !string.Equals(args[0], "crash", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("usage: MarketUninstallCrashProbe crash <pluginsDir> <configDir> <boundary>");
                return 2;
            }
            return RunCrash(args[1], args[2], args[3]);
        }

        private static int RunCrash(string pluginsDir, string configDir, string boundary)
        {
            ProbeRecord? ReadRecord()
            {
                string path = Path.Combine(configDir, "probe-record.json");
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<ProbeRecord>(File.ReadAllText(path));
            }

            MarketPluginRecord? FindRecord(string id)
            {
                ProbeRecord? record = ReadRecord();
                return record != null &&
                       string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase)
                    ? new MarketPluginRecord { Id = record.Id, File = record.File, Sha256 = record.Sha256 }
                    : null;
            }

            // 单记录协议：记录的"持久化移除"= 删除记录文件。removeRecord 返回后
            // 该移除即已落盘，与真实 Settings 持久化语义对齐。
            void RemoveRecord(string id)
            {
                ProbeRecord? record = ReadRecord();
                if (record == null ||
                    !string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("probe record missing on remove");
                File.Delete(Path.Combine(configDir, "probe-record.json"));
            }

            ProbeRecord? entryRecord = ReadRecord();
            if (entryRecord == null)
            {
                Console.Error.WriteLine("probe-record.json missing");
                return 2;
            }

            try
            {
                bool removed = PluginMarketUninstallCore.Uninstall(
                    new MarketPluginEntry { Id = entryRecord.Id },
                    pluginsDir,
                    FindRecord,
                    RemoveRecord,
                    builtinIds: null,
                    contentIdentityMatches: (_, _) => true,
                    out _,
                    faultInjector: stage =>
                    {
                        if (string.Equals(stage, boundary, StringComparison.Ordinal))
                            throw new MarketInstallCrashException(stage);
                    });
                Console.Out.WriteLine(removed ? "COMPLETED" : "NOT_REMOVED");
                return removed ? 0 : 3;
            }
            catch (MarketInstallCrashException crash)
            {
                // 模拟强杀的进程内投影：不回滚、不清理，直接退出，
                // 让 journal 与磁盘残留保持"被杀瞬间"的样子。
                Console.Error.WriteLine("CRASHED_AT:" + crash.Message);
                return 4;
            }
        }
    }
}
