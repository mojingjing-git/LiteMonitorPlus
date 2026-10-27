using System;
using System.Collections.Generic;
using System.IO;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [S03] 卸载事务化（写前日志 → 备份 → 删载荷 → 删记录提交点 → 清理）
    /// 与启动恢复对账的行为合同：五个强杀边界各注入
    /// <see cref="MarketInstallCrashException"/>，随后
    /// <see cref="PluginMarketInstallCore.RecoverInterruptedInstalls"/> 对账，
    /// 每场景跑两次验证幂等。提交点 = 安装记录持久化移除：
    /// 提交点前 ⇒ 恢复载荷；提交点后 ⇒ 完成卸载。
    /// </summary>
    public class MarketUninstallRecoveryTests
    {
        private const string FakeSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string PayloadText = "{\"id\":\"demo\"}";

        [Fact]
        public void Uninstall_HappyPath_RemovesPayloadRecordAndJournal()
        {
            using var fx = new UninstallFixture();

            bool removed = fx.RunUninstall(out string crashPoint, fault: null);

            Assert.True(removed, crashPoint);
            Assert.False(File.Exists(fx.PayloadPath));
            Assert.Null(fx.Store.Find("demo"));
            Assert.False(File.Exists(fx.JournalPath), "成功卸载后写前日志必须消失");
        }

        [Theory]
        [InlineData("afterJournal", true)]
        [InlineData("afterBackup", true)]
        [InlineData("afterPayloadDelete", true)]
        [InlineData("afterRecord", false)]
        [InlineData("afterBackupDelete", false)]
        public void Crash_AtBoundary_ReconcilesToConsistentState_Twice(string boundary, bool beforeCommit)
        {
            using var fx = new UninstallFixture();

            bool removed = fx.RunUninstall(out string crashPoint, stage =>
            {
                if (string.Equals(stage, boundary, StringComparison.Ordinal))
                    throw new MarketInstallCrashException(stage);
            });

            Assert.False(removed, "强杀注入必须让 Uninstall 以异常结束");
            Assert.Contains(boundary, crashPoint);
            // 强杀瞬间：记录是否已移除与边界一致（提交点语义）。
            Assert.Equal(beforeCommit, fx.Store.Find("demo") != null);

            int first = fx.Recover();
            Assert.Equal(1, first);
            Assert.False(File.Exists(fx.JournalPath), "对账完成后在途日志必须清空");

            if (beforeCommit)
            {
                Assert.True(File.Exists(fx.PayloadPath), boundary + "：提交点前恢复必须把载荷带回来");
                Assert.Equal(PayloadText, File.ReadAllText(fx.PayloadPath));
            }
            else
            {
                Assert.False(File.Exists(fx.PayloadPath), boundary + "：提交点后必须完成卸载");
                // 残留备份是垃圾，恢复后不得存在任何 .bak
                Assert.Empty(Directory.GetFiles(fx.PluginsDir, "*.bak"));
            }

            // 幂等：第二次恢复无事可做。
            int second = fx.Recover();
            Assert.Equal(0, second);
            Assert.Equal(beforeCommit, fx.Store.Find("demo") != null);
        }

        [Fact]
        public void Reconcile_UninstallRecordChanged_ThrowsAndKeepsJournalForRetry()
        {
            using var fx = new UninstallFixture();

            fx.RunUninstall(out _, stage =>
            {
                if (string.Equals(stage, "afterPayloadDelete", StringComparison.Ordinal))
                    throw new MarketInstallCrashException(stage);
            });

            // 崩溃窗口内记录被换成不同摘要：对账必须拒绝恢复/删除，
            // 且该事务保留在日志里（单笔失败不影响整体恢复流程）。
            fx.Store.Replace("demo", new MarketPluginRecord
            {
                Id = "demo",
                File = "demo.json",
                Sha256 = new string('f', 64)
            });

            Assert.Equal(0, fx.Recover());
            Assert.True(File.Exists(fx.JournalPath), "对账失败时在途日志必须保留待下次重试");
        }

        [Fact]
        public void Uninstall_RecordRemovalFails_RollsBackPayloadAndJournal()
        {
            using var fx = new UninstallFixture();
            var throwing = new ThrowingRemoveStore(fx.Store);

            Assert.Throws<IOException>(() => PluginMarketUninstallCore.Uninstall(
                new MarketPluginEntry { Id = "demo" },
                fx.PluginsDir,
                throwing.Find,
                throwing.Remove,
                builtinIds: null,
                contentIdentityMatches: (_, _) => true,
                out _,
                faultInjector: null));

            Assert.True(File.Exists(fx.PayloadPath), "记录移除失败时载荷必须回滚恢复");
            Assert.Equal(PayloadText, File.ReadAllText(fx.PayloadPath));
            Assert.NotNull(throwing.Find("demo"));
            Assert.False(File.Exists(fx.JournalPath), "回滚完成后在途日志必须清空");
        }

        // ================================================================
        // 【S03 §5.3 边界】卸载前载荷已缺失 / 备份损坏 / 记录不可用 / 目标被替换：
        // 只允许"完成可证明的卸载"或"保留待处理"，不得伪造载荷或报告成功。
        // ================================================================

        [Fact]
        public void Uninstall_PayloadAlreadyMissing_CompletesCleanlyWithoutFabrication()
        {
            using var fx = new UninstallFixture();
            File.Delete(fx.PayloadPath); // 卸载前载荷已缺失（HadTarget=false 语义）

            bool removed = fx.RunUninstall(out string crashPoint, fault: null);

            Assert.True(removed, crashPoint);
            Assert.Null(fx.Store.Find("demo"));
            Assert.False(File.Exists(fx.JournalPath));
            Assert.Empty(Directory.GetFiles(fx.PluginsDir, "*.bak"));
        }

        [Fact]
        public void Crash_BeforeCommit_CorruptBackup_ReconcileKeepsPendingAndDoesNotFabricate()
        {
            using var fx = new UninstallFixture();

            fx.RunUninstall(out _, stage =>
            {
                if (string.Equals(stage, "afterPayloadDelete", StringComparison.Ordinal))
                    throw new MarketInstallCrashException(stage);
            });

            // 备份在崩溃后被损坏：对账无法证明载荷内容 ⇒ 拒绝该事务、保留待处理，
            // 既不伪造载荷也不谎报完成。
            string[] backups = Directory.GetFiles(fx.PluginsDir, "*.bak");
            Assert.True(backups.Length == 1, "强杀在备份完成后，备份必须存在");
            File.WriteAllText(backups[0], "corrupted-backup-content");

            Assert.Equal(0, fx.Recover());
            Assert.True(File.Exists(fx.JournalPath), "备份损坏的事务必须保留在日志中待处理");
            Assert.False(File.Exists(fx.PayloadPath), "不得用损坏备份伪造载荷");
            Assert.NotNull(fx.Store.Find("demo"));
        }

        [Fact]
        public void Uninstall_CorruptedRecord_IsRejectedWithoutSideEffects()
        {
            using var fx = new UninstallFixture();
            fx.Store.Replace("demo", new MarketPluginRecord
            {
                Id = "demo",
                File = "demo.json",
                Sha256 = "not-a-usable-sha" // 记录不可用（摘要损坏）
            });

            bool removed = fx.RunUninstall(out _, fault: null);

            Assert.False(removed, "记录不可用的卸载必须被拒绝");
            Assert.True(File.Exists(fx.PayloadPath), "拒绝时载荷必须原样保留");
            Assert.Equal(PayloadText, File.ReadAllText(fx.PayloadPath));
            Assert.False(File.Exists(fx.JournalPath), "拒绝路径不得留下在途日志");
        }

        [Fact]
        public void Crash_BeforeCommit_TargetReplacedExternally_BackupRestoresTruePayload()
        {
            using var fx = new UninstallFixture();

            fx.RunUninstall(out _, stage =>
            {
                if (string.Equals(stage, "afterPayloadDelete", StringComparison.Ordinal))
                    throw new MarketInstallCrashException(stage);
            });

            // 提交点前目标位置出现外部重建文件：对账必须以备份（真载荷）为准，
            // 恢复后目标内容与旧记录摘要一致，而不是留下被替换的外部内容。
            File.WriteAllText(fx.PayloadPath, "external-replacement");

            Assert.Equal(1, fx.Recover());

            Assert.True(File.Exists(fx.PayloadPath));
            Assert.Equal(PayloadText, File.ReadAllText(fx.PayloadPath));
            Assert.NotNull(fx.Store.Find("demo"));
            Assert.False(File.Exists(fx.JournalPath));
            Assert.Empty(Directory.GetFiles(fx.PluginsDir, "*.bak"));
        }

        private sealed class UninstallFixture : IDisposable
        {
            internal UninstallFixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "lm-uninstall-recovery-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                PluginsDir = Path.Combine(Root, "plugins");
                Directory.CreateDirectory(PluginsDir);
                PayloadPath = Path.Combine(PluginsDir, "demo.json");
                File.WriteAllText(PayloadPath, PayloadText);
                Store.Add(new MarketPluginRecord { Id = "demo", File = "demo.json", Sha256 = FakeSha });
            }

            internal string Root { get; }
            internal string PluginsDir { get; }
            internal string PayloadPath { get; }
            internal string JournalPath => Path.Combine(PluginsDir, PluginMarketInstallCore.TransactionLogFileName);
            internal UninstallRecordStore Store { get; } = new();

            internal bool RunUninstall(out string crashPoint, Action<string>? fault)
            {
                crashPoint = "";
                try
                {
                    return PluginMarketUninstallCore.Uninstall(
                        new MarketPluginEntry { Id = "demo" },
                        PluginsDir,
                        Store.Find,
                        Store.Remove,
                        builtinIds: null,
                        contentIdentityMatches: (_, _) => true,
                        out _,
                        faultInjector: fault);
                }
                catch (MarketInstallCrashException crash)
                {
                    crashPoint = crash.Message;
                    return false;
                }
            }

            internal int Recover() => PluginMarketInstallCore.RecoverInterruptedInstalls(PluginsDir, Store.Find);

            public void Dispose()
            {
                try { Directory.Delete(Root, recursive: true); } catch { }
            }
        }

        private sealed class UninstallRecordStore
        {
            private readonly Dictionary<string, MarketPluginRecord> _records =
                new(StringComparer.OrdinalIgnoreCase);

            internal MarketPluginRecord? Find(string id)
                => _records.TryGetValue(id, out var record) ? record : null;

            internal void Add(MarketPluginRecord record) => _records[record.Id] = record;

            internal void Remove(string id) => _records.Remove(id);

            internal void Replace(string id, MarketPluginRecord record) => _records[id] = record;
        }

        private sealed class ThrowingRemoveStore
        {
            private readonly UninstallRecordStore _inner;

            internal ThrowingRemoveStore(UninstallRecordStore inner) => _inner = inner;

            internal MarketPluginRecord? Find(string id) => _inner.Find(id);

            internal void Remove(string id) => throw new IOException("simulated record save failure");
        }
    }

    /// <summary>
    /// [S03] 子进程强杀测试：真实独立进程在卸载事务边界"被杀"
    /// （tools/MarketUninstallCrashProbe 注入 MarketInstallCrashException 后
    /// 非零退出），测试进程扮演"下次启动"，从持久化记录文件出发做对账，
    /// 验证重启后状态与合同一致，且重复恢复幂等。
    /// </summary>
    public class MarketUninstallSubprocessRecoveryTests
    {
        private const string FakeSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string PayloadText = "{\"id\":\"demo\"}";

        [Theory]
        [InlineData("afterJournal", true)]
        [InlineData("afterBackup", true)]
        [InlineData("afterPayloadDelete", true)]
        [InlineData("afterRecord", false)]
        [InlineData("afterBackupDelete", false)]
        public void SubprocessCrash_AtBoundary_NextStartReconciles_Twice(string boundary, bool beforeCommit)
        {
            using var fx = new SubprocessFixture();
            fx.WriteRecord(new MarketPluginRecord { Id = "demo", File = "demo.json", Sha256 = FakeSha });

            int exitCode = fx.RunCrashProbe(boundary, out string output);

            // 探针在注入边界以非零退出 = 进程"被杀"在事务中途；
            // 记录文件的存留必须与提交点语义一致。
            Assert.True(exitCode != 0, $"探针应在边界 {boundary} 崩溃退出，实际 exit={exitCode} output={output}");
            Assert.Equal(beforeCommit, fx.RecordFileExists());
            Assert.True(File.Exists(fx.JournalPath), "强杀后写前日志必须保留");

            int first = fx.RecoverFromPersistedRecord();
            Assert.Equal(1, first);
            Assert.False(File.Exists(fx.JournalPath), "对账完成后在途日志必须清空");
            Assert.Equal(beforeCommit, fx.RecordFileExists());

            if (beforeCommit)
            {
                Assert.True(File.Exists(fx.PayloadPath), boundary + "：提交点前恢复必须把载荷带回来");
                Assert.Equal(PayloadText, File.ReadAllText(fx.PayloadPath));
                Assert.Empty(Directory.GetFiles(fx.PluginsDir, "*.bak"));
            }
            else
            {
                Assert.False(File.Exists(fx.PayloadPath), boundary + "：提交点后必须完成卸载");
                Assert.Empty(Directory.GetFiles(fx.PluginsDir, "*.bak"));
            }

            int second = fx.RecoverFromPersistedRecord();
            Assert.Equal(0, second);
        }

        /// <summary>最危险窗口单列验证：File.Delete(target) 已返回、removeRecord 未执行时强杀
        /// ⇒ 恢复后"记录在 ⇒ 载荷在"且载荷摘要与旧记录一致；重复恢复一次仍成立。</summary>
        [Fact]
        public void SubprocessCrash_InDeadliestWindow_RestoresPayloadMatchingOldRecord()
        {
            using var fx = new SubprocessFixture();
            fx.WriteRecord(new MarketPluginRecord { Id = "demo", File = "demo.json", Sha256 = FakeSha });

            int exitCode = fx.RunCrashProbe("afterPayloadDelete", out _);
            Assert.True(exitCode != 0);

            // 恢复前：记录在、载荷缺失——对账必须从备份把载荷原样带回来。
            Assert.True(fx.RecordFileExists());
            Assert.False(File.Exists(fx.PayloadPath));

            Assert.Equal(1, fx.RecoverFromPersistedRecord());

            Assert.True(File.Exists(fx.PayloadPath));
            Assert.Equal(PayloadText, File.ReadAllText(fx.PayloadPath));
            Assert.True(fx.RecordFileExists());
            Assert.False(File.Exists(fx.JournalPath));

            // 重复恢复一次仍成立（幂等）。
            Assert.Equal(0, fx.RecoverFromPersistedRecord());
            Assert.True(File.Exists(fx.PayloadPath));
            Assert.Equal(PayloadText, File.ReadAllText(fx.PayloadPath));
        }

        private sealed class SubprocessFixture : IDisposable
        {
            internal SubprocessFixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "lm-uninstall-subproc-" + Guid.NewGuid().ToString("N"));
                PluginsDir = Path.Combine(Root, "plugins");
                ConfigDir = Path.Combine(Root, "config");
                Directory.CreateDirectory(PluginsDir);
                Directory.CreateDirectory(ConfigDir);
                PayloadPath = Path.Combine(PluginsDir, "demo.json");
                File.WriteAllText(PayloadPath, PayloadText);
            }

            internal string Root { get; }
            internal string PluginsDir { get; }
            internal string ConfigDir { get; }
            internal string PayloadPath { get; }
            internal string RecordPath => Path.Combine(ConfigDir, "probe-record.json");
            internal string JournalPath => Path.Combine(PluginsDir, PluginMarketInstallCore.TransactionLogFileName);

            internal void WriteRecord(MarketPluginRecord record)
            {
                string json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    record.Id,
                    record.File,
                    record.Sha256
                });
                File.WriteAllText(RecordPath, json);
            }

            internal bool RecordFileExists() => File.Exists(RecordPath);

            internal int RunCrashProbe(string boundary, out string output)
            {
                string probeExe = Path.Combine(AppContext.BaseDirectory, "MarketUninstallCrashProbe.exe");
                Assert.True(File.Exists(probeExe), "子进程探针未随测试工程编译输出: " + probeExe);

                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = probeExe,
                        Arguments = $"crash \"{PluginsDir}\" \"{ConfigDir}\" \"{boundary}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                // 有界超时 + 回收整个进程（探针自身不派生子进程，无进程树残留）。
                if (!process.WaitForExit(20_000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                    output = "TIMEOUT";
                    return -1;
                }
                output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                return process.ExitCode;
            }

            /// <summary>以"持久化记录文件"为唯一记录事实源执行启动恢复（与真实
            /// 重启后 Settings.Load → findRecord 对齐）。</summary>
            internal int RecoverFromPersistedRecord()
                => PluginMarketInstallCore.RecoverInterruptedInstalls(PluginsDir, FindPersistedRecord);

            private MarketPluginRecord? FindPersistedRecord(string id)
            {
                if (!File.Exists(RecordPath)) return null;
                var record = System.Text.Json.JsonSerializer.Deserialize<ProbeRecordDto>(File.ReadAllText(RecordPath));
                return record != null &&
                       string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase)
                    ? new MarketPluginRecord { Id = record.Id, File = record.File, Sha256 = record.Sha256 }
                    : null;
            }

            private sealed class ProbeRecordDto
            {
                public string Id { get; set; } = "";
                public string File { get; set; } = "";
                public string Sha256 { get; set; } = "";
            }

            public void Dispose()
            {
                try { Directory.Delete(Root, recursive: true); } catch { }
            }
        }
    }
}
