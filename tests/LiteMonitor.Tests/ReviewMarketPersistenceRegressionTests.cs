using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    [Collection("cfgfile")]
    public class ReviewMarketPersistenceRegressionTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void InitialRecordFailure_RestoresOnlyTouchedRecordWithoutRetry(bool update, bool blockSave)
        {
            using var scope = new PersistenceScope(update);
            int changes = 0;
            SettingsHelper.MarketRecordMutationForTests = id =>
            {
                changes++;
                scope.Config.PreferredDisk = "concurrent-preference";
                scope.Config.MarketPlugins.Add(scope.OtherRecord("concurrent-record"));
            };

            FileStream exclusive = null;
            try
            {
                if (blockSave) SettingsHelper.GlobalBlockSave = true;
                else exclusive = LockSettings();

                Assert.Throws<MarketRecordPersistenceException>(() => scope.Commit());
                Assert.Equal(1, changes); // No futile compensating save after strict failure.
                Assert.Same(scope.Config, Settings.Load());
                scope.AssertMemory(update);
                Assert.Equal("concurrent-preference", scope.Config.PreferredDisk);
                Assert.Contains(scope.Config.MarketPlugins, record => record.Id == "concurrent-record");
                scope.AssertPayload(update);
                scope.AssertClean();
            }
            finally
            {
                exclusive?.Dispose();
                SettingsHelper.GlobalBlockSave = false;
                SettingsHelper.MarketRecordMutationForTests = null;
            }

            scope.AssertDisk(update);
            scope.DiscardAndReload();
            scope.AssertMemory(update);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SuccessfulInstallAndUpdate_PersistRecordAndPayload(bool update)
        {
            using var scope = new PersistenceScope(update);
            scope.Commit();
            Assert.Same(scope.Config, Settings.Load());
            Assert.Equal("2.0", PluginMarketState.Find(PersistenceScope.Id).Version);
            Assert.Equal("2.0", PluginMarketState.FindPersisted(PersistenceScope.Id).Version);
            Assert.Equal(scope.NewBytes, File.ReadAllBytes(scope.Target));
            if (update)
                Assert.Equal(PersistenceScope.InstalledAt, PluginMarketState.FindPersisted(PersistenceScope.Id).InstalledAt);
            scope.AssertClean();

            scope.DiscardAndReload();
            Assert.Equal("2.0", PluginMarketState.Find(PersistenceScope.Id).Version);
        }

        [Fact]
        public void GeneralSave_PreservesSilentAndBooleanFailureContracts()
        {
            using var scope = new PersistenceScope(update: true);
            using (LockSettings())
            {
                Assert.False(scope.Config.TrySave());
                Assert.Null(Record.Exception(() => scope.Config.Save()));
                PersistenceScope.AssertNoTempFile();
            }

            SettingsHelper.GlobalBlockSave = true;
            try
            {
                Assert.False(scope.Config.TrySave());
                Assert.Null(Record.Exception(() => scope.Config.Save()));
            }
            finally { SettingsHelper.GlobalBlockSave = false; }

            scope.Config.RecordedMaxCpuPower = float.NaN; // Real serializer failure.
            Assert.False(scope.Config.TrySave());
            Assert.Null(Record.Exception(() => scope.Config.Save()));
            PersistenceScope.AssertNoTempFile();
            scope.AssertDisk(oldRecord: true);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConcurrentPublicSave_SerializesAfterMarketRestoration(bool update)
        {
            using var scope = new PersistenceScope(update);
            using var mutated = new ManualResetEventSlim();
            using var allowMarketFailure = new ManualResetEventSlim();
            using var publicSaveRequested = new ManualResetEventSlim();
            using var publicSaveSerializing = new ManualResetEventSlim();
            using var allowPublicWrite = new ManualResetEventSlim();
            FileStream exclusive = LockSettings();
            Task<Exception> market = null;
            Task publicSave = null;
            int saveThread = 0;
            MarketPluginRecord observed = null;
            string observedPreference = null;
            bool observedOtherRecord = false;

            try
            {
                SettingsHelper.MarketRecordMutationForTests = id =>
                {
                    mutated.Set();
                    WaitFor(allowMarketFailure);
                };
                SettingsHelper.BeforeSaveGateForTests = () => publicSaveRequested.Set();
                SettingsHelper.BeforeSettingsSerializeForTests = () =>
                {
                    if (Environment.CurrentManagedThreadId != Volatile.Read(ref saveThread)) return;
                    observed = PluginMarketState.Find(PersistenceScope.Id);
                    observedPreference = scope.Config.PreferredDisk;
                    observedOtherRecord = scope.Config.MarketPlugins.Any(record => record.Id == "concurrent-record");
                    PersistenceScope.AssertNoTempFile();
                    publicSaveSerializing.Set();
                    WaitFor(allowPublicWrite);
                };

                market = StartWorker(() => Record.Exception(() => scope.Commit()));
                WaitFor(mutated);
                scope.Config.PreferredDisk = "concurrent-preference";
                scope.Config.MarketPlugins.Add(scope.OtherRecord("concurrent-record"));
                publicSave = StartWorker(() =>
                {
                    Volatile.Write(ref saveThread, Environment.CurrentManagedThreadId);
                    scope.Config.Save(); // Actual public, silent compatibility entry.
                });
                WaitFor(publicSaveRequested);
                allowMarketFailure.Set();
                Assert.IsType<MarketRecordPersistenceException>(await market.WaitAsync(TimeSpan.FromSeconds(15)));
                WaitFor(publicSaveSerializing);
                scope.AssertPayload(update);

                // The public writer now owns the save gate. Release the actual
                // settings lock only after the failed market write is restored.
                exclusive.Dispose();
                exclusive = null;
                allowPublicWrite.Set();
                await publicSave.WaitAsync(TimeSpan.FromSeconds(15));

                Assert.Equal(update ? "1.0" : null, observed?.Version);
                Assert.Equal("concurrent-preference", observedPreference);
                Assert.True(observedOtherRecord);
                Assert.Same(scope.Config, Settings.Load());
                scope.AssertMemory(update);
                scope.AssertDisk(update);
                var disk = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsHelper.FilePath));
                Assert.Equal("concurrent-preference", disk.PreferredDisk);
                Assert.Contains(disk.MarketPlugins, record => record.Id == "concurrent-record");
                scope.AssertClean();
            }
            finally
            {
                exclusive?.Dispose();
                allowMarketFailure.Set();
                allowPublicWrite.Set();
                await Drain(market, publicSave);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void AfterRecordCompensationFailure_PreservesCommittedPayloadAndRecoversFromDisk(
            bool update, bool blockCompensation)
        {
            using var scope = new PersistenceScope(update);
            FileStream exclusive = null;
            var failure = new IOException("afterRecord failure");
            try
            {
                var actual = Assert.Throws<IOException>(() => scope.Commit(point =>
                {
                    if (point != "afterRecord") return;
                    Assert.Equal("2.0", PluginMarketState.FindPersisted(PersistenceScope.Id).Version);
                    if (blockCompensation) SettingsHelper.GlobalBlockSave = true;
                    else exclusive = LockSettings();
                    throw failure;
                }));
                Assert.Same(failure, actual);
                Assert.Equal("2.0", PluginMarketState.Find(PersistenceScope.Id).Version);
                Assert.Equal(scope.NewBytes, File.ReadAllBytes(scope.Target));
                scope.AssertPending(update);

                // Empty/divergent live memory must never be the recovery authority.
                scope.DiscardMemory();
                if (!blockCompensation)
                {
                    PluginMarket.RecoverInterruptedInstallsIn(scope.PluginsDir);
                    scope.AssertPending(update); // Locked primary is pending, not no record.
                    Assert.Equal(scope.NewBytes, File.ReadAllBytes(scope.Target));
                }
            }
            finally
            {
                exclusive?.Dispose();
                SettingsHelper.GlobalBlockSave = false;
            }

            Settings replacement = Settings.Load();
            PluginMarket.RecoverInterruptedInstallsIn(scope.PluginsDir);
            Assert.Same(replacement, Settings.Load());
            Assert.Equal("2.0", PluginMarketState.Find(PersistenceScope.Id).Version);
            Assert.Equal("2.0", PluginMarketState.FindPersisted(PersistenceScope.Id).Version);
            Assert.Equal(scope.NewBytes, File.ReadAllBytes(scope.Target));
            scope.AssertClean();
            scope.DiscardAndReload();
            Assert.Equal("2.0", PluginMarketState.Find(PersistenceScope.Id).Version);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AfterRecordFileRestorationFailure_RemainsPendingUntilRequiredMoveOrDeleteSucceeds(bool update)
        {
            using var scope = new PersistenceScope(update);
            FileStream payloadLock = null;
            var failure = new IOException("afterRecord failure");
            try
            {
                Assert.Same(failure, Assert.Throws<IOException>(() => scope.Commit(point =>
                {
                    if (point != "afterRecord") return;
                    // Readers are allowed, but the actual Move/Delete must fail.
                    payloadLock = new FileStream(scope.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw failure;
                })));
                scope.AssertMemory(update);
                scope.AssertDisk(update); // Record compensation really persisted first.
                Assert.Equal(scope.NewBytes, File.ReadAllBytes(scope.Target));
                scope.AssertPending(update);

                scope.DiscardMemory();
                Settings.Load().MarketPlugins.Add(scope.NewRecord()); // Deliberately wrong live authority.
                Assert.Equal(0, PluginMarketInstallCore.RecoverInterruptedInstalls(
                    scope.PluginsDir, PluginMarketState.FindPersisted));
                scope.AssertPending(update); // Failed delete must not count as handled.
            }
            finally
            {
                payloadLock?.Dispose();
            }

            scope.DiscardMemory();
            PluginMarket.RecoverInterruptedInstallsIn(scope.PluginsDir);
            scope.AssertMemory(update);
            scope.AssertPayload(update);
            scope.AssertDisk(update);
            scope.AssertClean();
            scope.DiscardAndReload();
            scope.AssertMemory(update);
        }

        [Fact]
        public void Recovery_CorruptPrimaryDoesNotUseBackupOrLiveRecordAsCommitEvidence()
        {
            using var scope = new PersistenceScope(update: true);
            Assert.Throws<MarketInstallCrashException>(() => scope.Commit(point =>
            {
                if (point == "afterRecord") throw new MarketInstallCrashException(point);
            }));
            string primary = File.ReadAllText(SettingsHelper.FilePath);
            try
            {
                File.WriteAllText(SettingsHelper.FilePath, "{ corrupt primary");
                Assert.Equal(0, PluginMarketInstallCore.RecoverInterruptedInstalls(
                    scope.PluginsDir, PluginMarketState.FindPersistedForRecovery));
                scope.AssertPending(backup: true);
                Assert.Equal(scope.NewBytes, File.ReadAllBytes(scope.Target));
                Assert.Same(scope.Config, Settings.Load());
                Assert.Equal("2.0", PluginMarketState.Find(PersistenceScope.Id).Version);
                Assert.Equal("{ corrupt primary", File.ReadAllText(SettingsHelper.FilePath));
            }
            finally { File.WriteAllText(SettingsHelper.FilePath, primary); }

            PluginMarket.RecoverInterruptedInstallsIn(scope.PluginsDir);
            Assert.Equal(scope.NewBytes, File.ReadAllBytes(scope.Target));
            Assert.Equal("2.0", PluginMarketState.FindPersisted(PersistenceScope.Id).Version);
            scope.AssertClean();
        }

        [Fact]
        public void Recovery_LockedJournalRemainsPending()
        {
            using var scope = new PersistenceScope(update: true);
            Assert.Throws<MarketInstallCrashException>(() => scope.Commit(point =>
            {
                if (point == "afterMove") throw new MarketInstallCrashException(point);
            }));
            using (new FileStream(scope.Journal, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(0, PluginMarketInstallCore.RecoverInterruptedInstalls(
                    scope.PluginsDir, PluginMarketState.FindPersistedForRecovery));
                scope.AssertPending(backup: true);
            }
            scope.DiscardMemory();
            PluginMarket.RecoverInterruptedInstallsIn(scope.PluginsDir);
            scope.AssertMemory(oldRecord: true);
            scope.AssertDisk(oldRecord: true);
            scope.AssertPayload(oldPayload: true);
            scope.AssertClean();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GenericCallback_MutatesMemoryThenThrows_IsCompensated(bool update)
        {
            using var scope = new PersistenceScope(update);
            bool first = true;
            var failure = new InvalidOperationException("mutate then throw");
            var actual = Assert.Throws<InvalidOperationException>(() => PluginMarketInstallCore.CommitInstall(
                scope.PluginsDir, PersistenceScope.Id, "2.0", scope.NewBytes,
                PluginMarket.ComputeSha256(scope.NewBytes), PluginMarketState.Find,
                record =>
                {
                    if (!first) { PluginMarketState.Upsert(record); return; }
                    first = false;
                    scope.Config.MarketPlugins.RemoveAll(item => item.Id == record.Id);
                    scope.Config.MarketPlugins.Add(record);
                    throw failure;
                },
                PluginMarketState.Remove));
            Assert.Same(failure, actual);
            scope.AssertMemory(update);
            scope.AssertDisk(update);
            scope.AssertPayload(update);
            scope.AssertClean();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task CommitAndRecovery_UseOneTransactionGate(bool update, bool crash)
        {
            using var scope = new PersistenceScope(update);
            using var staged = new ManualResetEventSlim();
            using var releaseCommit = new ManualResetEventSlim();
            using var recoveryRequested = new ManualResetEventSlim();
            Task<Exception> commit = null;
            Task<int> recovery = null;
            bool recoverySawBusyTransaction = false;
            try
            {
                commit = StartWorker(() => Record.Exception(() => scope.Commit(point =>
                {
                    if (point == "afterStage")
                    {
                        staged.Set();
                        WaitFor(releaseCommit);
                    }
                    if (point == "afterMove" && crash) throw new MarketInstallCrashException(point);
                })));
                WaitFor(staged);
                PluginMarketInstallCore.BeforeRecoveryGateForTests = () =>
                {
                    bool entered = Monitor.TryEnter(PluginMarketInstallCore.TransactionGate);
                    recoverySawBusyTransaction = !entered;
                    if (entered) Monitor.Exit(PluginMarketInstallCore.TransactionGate);
                    recoveryRequested.Set();
                };
                recovery = StartWorker(() => PluginMarketInstallCore.RecoverInterruptedInstalls(
                    scope.PluginsDir,
                    id =>
                    {
                        Assert.True(Monitor.IsEntered(PluginMarketInstallCore.TransactionGate));
                        return PluginMarketState.FindPersisted(id);
                    }));
                WaitFor(recoveryRequested);
                Assert.True(recoverySawBusyTransaction);
                Assert.False(recovery.IsCompleted);
                Assert.Single(Directory.GetFiles(scope.PluginsDir, "*.stage"));
                releaseCommit.Set();

                Exception result = await commit.WaitAsync(TimeSpan.FromSeconds(15));
                if (crash) Assert.IsType<MarketInstallCrashException>(result);
                else Assert.Null(result);
                Assert.Equal(crash ? 1 : 0, await recovery.WaitAsync(TimeSpan.FromSeconds(15)));
                if (crash)
                {
                    scope.AssertPayload(update);
                    scope.AssertDisk(update);
                }
                else
                {
                    Assert.Equal(scope.NewBytes, File.ReadAllBytes(scope.Target));
                    Assert.Equal("2.0", PluginMarketState.FindPersisted(PersistenceScope.Id).Version);
                }
                scope.AssertClean();
            }
            finally
            {
                releaseCommit.Set();
                await Drain(commit, recovery);
            }
        }

        [Theory]
        [InlineData("RecoveryVersion", "2")]
        [InlineData("PreviousFileSha256", "\"not-a-hash\"")]
        [InlineData("PreviousFileSha256", "null")]
        [InlineData("PreviousRecordSha256", "\"../outside.json\"")]
        [InlineData("HadTarget", "false")]
        public void ExtendedJournalMetadata_IsValidatedBeforeRecovery(string field, string valueJson)
        {
            using var scope = new PersistenceScope(update: true);
            Assert.Throws<MarketInstallCrashException>(() => scope.Commit(point =>
            {
                if (point == "afterMove") throw new MarketInstallCrashException(point);
            }));
            JsonNode log = JsonNode.Parse(File.ReadAllText(scope.Journal));
            log["Pending"][0][field] = JsonNode.Parse(valueJson);
            File.WriteAllText(scope.Journal, log.ToJsonString());
            string backup = Assert.Single(Directory.GetFiles(scope.PluginsDir, "*.bak"));
            byte[] savedBackup = File.ReadAllBytes(backup);

            Assert.Equal(0, PluginMarketInstallCore.RecoverInterruptedInstalls(
                scope.PluginsDir, PluginMarketState.FindPersisted));
            Assert.Equal(scope.NewBytes, File.ReadAllBytes(scope.Target));
            Assert.Equal(savedBackup, File.ReadAllBytes(backup));
            Assert.True(File.Exists(Path.Combine(scope.PluginsDir, PluginMarketInstallCore.QuarantineLogFileName)));
            scope.AssertDisk(oldRecord: true);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Uninstall_StrictRecordFailureRestoresPayload(bool blockSave)
        {
            using var scope = new PersistenceScope(update: true);
            FileStream exclusive = null;
            try
            {
                if (blockSave) SettingsHelper.GlobalBlockSave = true;
                else exclusive = LockSettings();
                Assert.Throws<MarketRecordPersistenceException>(() => PluginMarketUninstallCore.Uninstall(
                    new MarketPluginEntry { Id = PersistenceScope.Id }, scope.PluginsDir,
                    PluginMarketState.Find, PluginMarketState.Remove, out _));
                scope.AssertMemory(oldRecord: true);
                scope.AssertPayload(oldPayload: true);
                Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.uninstall"));
            }
            finally
            {
                exclusive?.Dispose();
                SettingsHelper.GlobalBlockSave = false;
            }
            scope.AssertDisk(oldRecord: true);
        }

        [Fact]
        public void Uninstall_SucceedsWhenRecordRemovalPersists()
        {
            using var scope = new PersistenceScope(update: true);
            Assert.True(PluginMarketUninstallCore.Uninstall(
                new MarketPluginEntry { Id = PersistenceScope.Id }, scope.PluginsDir,
                PluginMarketState.Find, PluginMarketState.Remove, out _));
            scope.AssertMemory(oldRecord: false);
            scope.AssertDisk(oldRecord: false);
            scope.AssertPayload(oldPayload: false);
            Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.uninstall"));
        }

        [Fact]
        public void Uninstall_CannotConsumePayloadOwnedByPendingCommittedUpdate()
        {
            using var scope = new PersistenceScope(update: true);
            FileStream heldBackup = null;
            try
            {
                scope.Commit(point =>
                {
                    if (point == "afterRecord")
                        heldBackup = new FileStream(Assert.Single(Directory.GetFiles(scope.PluginsDir, "*.bak")),
                            FileMode.Open, FileAccess.Read, FileShare.Read);
                });
                scope.AssertPending(backup: true);
                byte[] pending = File.ReadAllBytes(scope.Journal);

                // The public entry first attempts recovery. A locked backup keeps
                // it pending, so uninstall must respect that transaction's ownership.
                Assert.Throws<IOException>(() => PluginMarket.Uninstall(
                    new MarketPluginEntry { Id = PersistenceScope.Id }, out _));
                Assert.Equal(scope.NewBytes, File.ReadAllBytes(scope.Target));
                Assert.Equal("2.0", PluginMarketState.FindPersisted(PersistenceScope.Id).Version);
                Assert.Equal(pending, File.ReadAllBytes(scope.Journal));
                scope.AssertPending(backup: true);
                Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.uninstall"));
            }
            finally { heldBackup?.Dispose(); }

            PluginMarket.RecoverInterruptedInstallsIn(scope.PluginsDir);
            scope.AssertClean();
            Assert.True(PluginMarket.Uninstall(new MarketPluginEntry { Id = PersistenceScope.Id }, out _));
            Assert.False(File.Exists(scope.Target));
            Assert.Null(PluginMarketState.FindPersisted(PersistenceScope.Id));
            scope.Commit(); // Recovery and uninstall must not permanently block a later install.
            Assert.Equal(scope.NewBytes, File.ReadAllBytes(scope.Target));
            Assert.Equal("2.0", PluginMarketState.FindPersisted(PersistenceScope.Id).Version);
            scope.AssertClean();
        }

        [Fact]
        public void Uninstall_CannotTreatUnreadablePendingJournalAsAbsent()
        {
            using var scope = new PersistenceScope(update: true);
            Assert.Throws<MarketInstallCrashException>(() => scope.Commit(point =>
            {
                if (point == "afterRecord") throw new MarketInstallCrashException(point);
            }));
            byte[] pending = File.ReadAllBytes(scope.Journal);
            using (var locked = new FileStream(scope.Journal, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<IOException>(() => PluginMarket.Uninstall(
                    new MarketPluginEntry { Id = PersistenceScope.Id }, out _));
                Assert.Equal(scope.NewBytes, File.ReadAllBytes(scope.Target));
                Assert.Equal("2.0", PluginMarketState.FindPersisted(PersistenceScope.Id).Version);
                Assert.Empty(Directory.GetFiles(scope.PluginsDir, "*.uninstall"));
            }
            Assert.Equal(pending, File.ReadAllBytes(scope.Journal));
            PluginMarket.RecoverInterruptedInstallsIn(scope.PluginsDir);
            scope.AssertClean();
            Assert.True(PluginMarket.Uninstall(new MarketPluginEntry { Id = PersistenceScope.Id }, out _));
        }

        private static FileStream LockSettings()
            => new FileStream(SettingsHelper.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        private static void WaitFor(ManualResetEventSlim signal)
            => Assert.True(signal.Wait(TimeSpan.FromSeconds(15)), "The deterministic test barrier was not reached.");

        private static Task<T> StartWorker<T>(Func<T> body)
            => Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private static Task StartWorker(Action body)
            => Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private static async Task Drain(params Task[] workers)
        {
            foreach (Task worker in workers)
            {
                if (worker == null) continue;
                try { await worker.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch { /* The test's primary assertion reports failure. */ }
            }
        }

        private sealed class PersistenceScope : IDisposable
        {
            internal const string Id = "review_market_persistence";
            internal const string InstalledAt = "2020-01-02T03:04:05Z";
            private static readonly FieldInfo Instance = typeof(Settings).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);
            private static readonly FieldInfo BlockOwner = typeof(SettingsHelper).GetField("_saveBlockOwner", BindingFlags.NonPublic | BindingFlags.Static);
            private readonly object _savedInstance;
            private readonly bool _savedBlock;
            private readonly object _savedBlockOwner;
            private readonly string _savedPluginsDir;
            private readonly string _savedCurrentDirectory;
            private readonly Action _savedBeforeSave;
            private readonly Action _savedBeforeSerialize;
            private readonly Action<string> _savedMutation;
            private readonly Action _savedBeforeRecovery;
            private readonly Dictionary<string, byte[]> _savedFiles = new(StringComparer.OrdinalIgnoreCase);
            private readonly string? _savedPathOverride;
            private readonly string _root;

            internal PersistenceScope(bool update)
            {
                // 【C-1 / 评审阻断 1】本类此前刻意钉在**测试程序集 bin 的真实 settings 三件套**上
                // （旧断言要求 FilePath 与 Assembly.Location 同目录）。env 激活的
                // MarketIndexContractTests 不在 cfgfile 集合，与本类的任何文件状态都可能串台
                // ⇒ "单跑全绿、全量间歇挂"（评审同机三次复现三条不同用例）。
                // 现在整体重定向到本沙箱目录：主/备/tmp 全在 _root，与真实路径和其他用例彻底隔离。
                _root = Path.Combine(AppContext.BaseDirectory, "review-market-" + Guid.NewGuid().ToString("N"));
                _savedPathOverride = SettingsHelper.PathOverride;
                SettingsHelper.PathOverride = _root;
                Directory.CreateDirectory(_root);
                Assert.Equal(_root, Path.GetDirectoryName(SettingsHelper.FilePath));
                _savedInstance = Instance.GetValue(null);
                _savedBlock = SettingsHelper.GlobalBlockSave;
                _savedBlockOwner = BlockOwner.GetValue(null);
                _savedPluginsDir = PluginMarket.PluginsDirOverride;
                _savedCurrentDirectory = Environment.CurrentDirectory;
                _savedBeforeSave = SettingsHelper.BeforeSaveGateForTests;
                _savedBeforeSerialize = SettingsHelper.BeforeSettingsSerializeForTests;
                _savedMutation = SettingsHelper.MarketRecordMutationForTests;
                _savedBeforeRecovery = PluginMarketInstallCore.BeforeRecoveryGateForTests;
                PluginsDir = Path.Combine(_root, "plugins");
                Config = new Settings { PreferredDisk = "baseline-preference" };
                OldBytes = Encoding.UTF8.GetBytes("{\"id\":\"" + Id + "\",\"meta\":{\"version\":\"1.0\"}}");
                NewBytes = Encoding.UTF8.GetBytes("{\"id\":\"" + Id + "\",\"meta\":{\"version\":\"2.0\"}}");

                foreach (string path in SettingsPaths())
                    _savedFiles[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
                try
                {
                    Directory.CreateDirectory(PluginsDir);
                    foreach (string path in SettingsPaths()) File.Delete(path);
                    SettingsHelper.GlobalBlockSave = false;
                    SettingsHelper.BeforeSaveGateForTests = null;
                    SettingsHelper.BeforeSettingsSerializeForTests = null;
                    SettingsHelper.MarketRecordMutationForTests = null;
                    PluginMarketInstallCore.BeforeRecoveryGateForTests = null;
                    PluginMarket.PluginsDirOverride = PluginsDir;
                    Instance.SetValue(null, Config);
                    Config.MarketPlugins.Add(OtherRecord("unrelated-record"));
                    if (update)
                    {
                        File.WriteAllBytes(Target, OldBytes);
                        Config.MarketPlugins.Add(new MarketPluginRecord
                        {
                            Id = Id, File = Id + ".json", Version = "1.0",
                            Sha256 = PluginMarket.ComputeSha256(OldBytes), InstalledAt = InstalledAt
                        });
                    }
                    Assert.True(Config.TrySave());
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal Settings Config { get; }
            internal string PluginsDir { get; }
            internal string Target => Path.Combine(PluginsDir, Id + ".json");
            internal string Journal => Path.Combine(PluginsDir, PluginMarketInstallCore.TransactionLogFileName);
            internal byte[] OldBytes { get; }
            internal byte[] NewBytes { get; }

            internal MarketPluginRecord OtherRecord(string id)
                => new MarketPluginRecord { Id = id, File = id + ".json", Sha256 = new string('a', 64), Version = "unrelated" };

            internal MarketPluginRecord NewRecord()
                => new MarketPluginRecord { Id = Id, File = Id + ".json", Version = "2.0", Sha256 = PluginMarket.ComputeSha256(NewBytes) };

            internal void Commit(Action<string> fault = null)
                => PluginMarketInstallCore.CommitInstall(PluginsDir, Id, "2.0", NewBytes,
                    PluginMarket.ComputeSha256(NewBytes), PluginMarketState.Find,
                    PluginMarketState.Upsert, PluginMarketState.Remove, fault);

            internal void AssertMemory(bool oldRecord)
            {
                Assert.Equal(oldRecord ? "1.0" : null, PluginMarketState.Find(Id)?.Version);
                Assert.Contains(Settings.Load().MarketPlugins, record => record.Id == "unrelated-record" && record.Version == "unrelated");
            }

            internal void AssertDisk(bool oldRecord)
                => Assert.Equal(oldRecord ? "1.0" : null, PluginMarketState.FindPersisted(Id)?.Version);

            internal void AssertPayload(bool oldPayload)
            {
                if (oldPayload) Assert.Equal(OldBytes, File.ReadAllBytes(Target));
                else Assert.False(File.Exists(Target));
            }

            internal void AssertPending(bool backup)
            {
                Assert.True(File.Exists(Journal));
                Assert.Equal(backup ? 1 : 0, Directory.GetFiles(PluginsDir, "*.bak").Length);
            }

            internal void AssertClean()
            {
                Assert.False(File.Exists(Journal));
                Assert.Empty(Directory.GetFiles(PluginsDir, "*.stage"));
                Assert.Empty(Directory.GetFiles(PluginsDir, "*.bak"));
            }

            internal void DiscardMemory()
            {
                var replacement = new Settings { PreferredDisk = "recovery-session-preference" };
                replacement.MarketPlugins.Add(OtherRecord("unrelated-record"));
                Instance.SetValue(null, replacement);
            }
            internal void DiscardAndReload()
            {
                Instance.SetValue(null, null);
                Settings.Load(forceReload: true);
            }

            internal static void AssertNoTempFile()
            {
                // 【C-1】tmp 现在带进程号（settings.json.{pid}.tmp）——按模式断言，别再盯固定名。
                string dir = Path.GetDirectoryName(SettingsHelper.FilePath)!;
                Assert.Empty(Directory.GetFiles(dir, "settings.json.*.tmp"));
            }

            private static string[] SettingsPaths()
            {
                // 【C-1】tmp 带进程号 ⇒ 主/备按名，tmp 按模式收集（沙箱内）。
                var paths = new List<string>
                {
                    SettingsHelper.FilePath,
                    SettingsHelper.FilePath + ".bak",
                };
                string dir = Path.GetDirectoryName(SettingsHelper.FilePath)!;
                if (Directory.Exists(dir))
                {
                    paths.AddRange(Directory.GetFiles(dir, "settings.json.*.tmp"));
                }
                return paths.ToArray();
            }

            public void Dispose()
            {
                try
                {
                    foreach (var pair in _savedFiles)
                    {
                        File.Delete(pair.Key);
                        if (pair.Value != null) File.WriteAllBytes(pair.Key, pair.Value);
                    }
                }
                finally
                {
                    try
                    {
                        Instance.SetValue(null, _savedInstance);
                        SettingsHelper.GlobalBlockSave = _savedBlock;
                        SettingsHelper.PathOverride = _savedPathOverride;
                        BlockOwner.SetValue(null, _savedBlockOwner);
                        PluginMarket.PluginsDirOverride = _savedPluginsDir;
                        SettingsHelper.BeforeSaveGateForTests = _savedBeforeSave;
                        SettingsHelper.BeforeSettingsSerializeForTests = _savedBeforeSerialize;
                        SettingsHelper.MarketRecordMutationForTests = _savedMutation;
                        PluginMarketInstallCore.BeforeRecoveryGateForTests = _savedBeforeRecovery;
                        var recoveryLock = typeof(PluginMarket).GetField("_recoveryLock", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                        lock (recoveryLock)
                        {
                            var recovered = (HashSet<string>)typeof(PluginMarket).GetField("_recoveredDirs", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                            recovered.Remove(PluginsDir);
                        }
                        Environment.CurrentDirectory = _savedCurrentDirectory;
                    }
                    finally
                    {
                        string root = Path.GetFullPath(_root);
                        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)) + Path.DirectorySeparatorChar;
                        Assert.StartsWith(output, root, StringComparison.OrdinalIgnoreCase);
                        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                    }
                }
            }
        }
    }
}
