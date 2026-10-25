using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// F17：插件市场安装事务与原子替换的并发冲突（确定性回归）。
    ///
    /// <para>
    /// 背景：Windows 上 <c>MoveFileEx(..., MOVEFILE_REPLACE_EXISTING)</c> 要求**目标文件当前没有任何打开的句柄**
    /// —— 实测即使对方以 <c>FILE_SHARE_DELETE</c> 共享，替换仍然返回 Win32 <c>ERROR_ACCESS_DENIED</c>
    /// （.NET 表现为 <see cref="UnauthorizedAccessException"/>）。
    /// 因此：只要有读者正拿着目标文件（插件加载器、恢复对账、可用性校验……），原子替换就会当场失败，
    /// 安装被静默打断并留下事务日志 ⇒ 下次启动被回滚 ⇒ 用户的安装丢失且没有明确报错。
    /// </para>
    ///
    /// <para>
    /// 本类的用例全部是**确定性**的：用显式的文件句柄 + 显式的重试闸门制造交错，
    /// 断言"要么安装成功、要么明确失败并回滚"，不依赖运气。
    /// </para>
    /// </summary>
    public class ReviewMarketInstallRaceRegressionTests
    {
        private const string PluginId = "f17_race_demo";
        private const string Version = "2.0";

        /// <summary>
        /// 旧实现必失败（第一次原子替换直接抛 UnauthorizedAccessException），
        /// 新实现必须成功：有界重试在占用解除后重试成功。
        /// </summary>
        [Fact]
        public void AtomicReplace_RetriesAndSucceedsOnceTheTransientLockIsReleased()
        {
            using var scope = new InstallScope();
            using var firstFailure = new ManualResetEventSlim();
            using var retryGate = new ManualResetEventSlim();

            // 独占式占住目标文件（不共享 Delete）：旧实现在这里必抛 UnauthorizedAccessException。
            FileStream blocker = new FileStream(
                scope.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
            Task<Exception> commit = null;
            try
            {
                scope.InstallPolicy = new MarketFileIo.ReplacePolicy
                {
                    Clock = scope.Clock,
                    // 等待不真睡：等测试放行才继续（测试里不做真实等待）。
                    Wait = _ => retryGate.Wait(TimeSpan.FromSeconds(15)),
                    OnRetry = (attempt, _) => firstFailure.Set(),
                };

                commit = StartWorker(() => Record.Exception(() => scope.Commit()));

                Assert.True(firstFailure.Wait(TimeSpan.FromSeconds(15)),
                    "第一次原子替换没有如期失败（用例前提不成立）。");

                // 占用解除 ⇒ 重试必须能成功。
                blocker.Dispose();
                blocker = null;
                retryGate.Set();

                Exception failure = commit.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                Assert.Null(failure);
            }
            finally
            {
                blocker?.Dispose();
                retryGate.Set();
            }

            scope.AssertInstalled();
        }

        /// <summary>
        /// 重试**次数**耗尽：必须抛可识别异常（目标文件被占用），并且事务回滚干净
        /// （不留 stage / backup，记录与文件保持一致）。
        /// </summary>
        [Fact]
        public void AtomicReplace_AttemptBudgetExhausted_ThrowsIdentifiableErrorAndRollsBackCleanly()
        {
            using var scope = new InstallScope();
            using var blocker = new FileStream(
                scope.Target, FileMode.Open, FileAccess.Read, FileShare.Read);

            scope.InstallPolicy = new MarketFileIo.ReplacePolicy
            {
                Clock = scope.Clock,
                // 不真睡：把单调时钟推进到"这次等待真的花了这么久"。
                Wait = delay => { scope.Clock.Advance(delay); return true; },
            };

            var blocked = Assert.Throws<MarketFileReplaceBlockedException>(() => scope.Commit());

            Assert.Equal(MarketFileIo.MaxAttempts, blocked.Attempts);
            Assert.Equal(scope.Target, blocked.DestinationPath);
            Assert.Contains("无法原子替换", blocked.Message, StringComparison.Ordinal);
            Assert.IsType<UnauthorizedAccessException>(blocked.InnerException);

            scope.AssertRolledBackToOldState();
        }

        /// <summary>
        /// 重试**时间预算**耗尽：单调时钟跳变后必须立刻收手（绝不无限循环），
        /// 同样抛可识别异常并回滚干净。
        /// </summary>
        [Fact]
        public void AtomicReplace_TimeBudgetExhausted_StopsRetryingAndRollsBackCleanly()
        {
            using var scope = new InstallScope();
            using var blocker = new FileStream(
                scope.Target, FileMode.Open, FileAccess.Read, FileShare.Read);

            scope.InstallPolicy = new MarketFileIo.ReplacePolicy
            {
                Clock = scope.Clock,
                // 模拟"机器卡住"：单调时钟一次跳过整个预算。
                Wait = _ => { scope.Clock.Advance(TimeSpan.FromSeconds(5)); return true; },
            };

            var blocked = Assert.Throws<MarketFileReplaceBlockedException>(() => scope.Commit());

            Assert.True(blocked.Attempts < MarketFileIo.MaxAttempts,
                "时间预算没有收住重试次数（attempts=" + blocked.Attempts + "）。");
            scope.AssertRolledBackToOldState();
        }

        /// <summary>
        /// 安装 × 恢复并发：最终必须一致 ——
        /// 要么安装成功（文件与记录都是新版本），要么明确失败并回滚（文件与记录都是旧版本）；
        /// **不允许**出现"文件已换但记录没换"或"记录已换但文件没换"。
        /// </summary>
        [Fact]
        public void InstallAndRecovery_RacingReplace_LeavesConsistentFinalState()
        {
            using var scope = new InstallScope();
            using var staged = new ManualResetEventSlim();
            using var releaseCommit = new ManualResetEventSlim();
            using var firstFailure = new ManualResetEventSlim();
            using var retryGate = new ManualResetEventSlim();

            Task<Exception> commit = null;
            Task<int> recovery = null;
            FileStream blocker = null;
            try
            {
                scope.InstallPolicy = new MarketFileIo.ReplacePolicy
                {
                    Clock = scope.Clock,
                    Wait = _ => retryGate.Wait(TimeSpan.FromSeconds(15)),
                    OnRetry = (attempt, _) => firstFailure.Set(),
                };

                commit = StartWorker(() => Record.Exception(() => scope.Commit(point =>
                {
                    if (point == "afterStage")
                    {
                        staged.Set();
                        Assert.True(releaseCommit.Wait(TimeSpan.FromSeconds(15)),
                            "提交线程没有如期到达替换步骤。");
                    }
                })));

                Assert.True(staged.Wait(TimeSpan.FromSeconds(15)), "提交线程没有如期暂存。");

                // 恢复线程必须被事务闸门挡住（一个事务只允许一个写者）。
                recovery = StartWorker(() => PluginMarketInstallCore.RecoverInterruptedInstalls(
                    scope.PluginsDir, scope.Records.Find));

                // 目标文件被独占式占住 ⇒ 安装的原子替换第一次必失败。
                blocker = new FileStream(scope.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
                releaseCommit.Set();
                Assert.True(firstFailure.Wait(TimeSpan.FromSeconds(15)),
                    "第一次原子替换没有如期失败（用例前提不成立）。");

                blocker.Dispose();
                blocker = null;
                retryGate.Set();

                Exception failure = commit.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                Assert.Null(failure);

                int handled = recovery.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                Assert.True(handled == 0 || handled == 1);
            }
            finally
            {
                blocker?.Dispose();
                releaseCommit.Set();
                retryGate.Set();
            }

            scope.AssertInstalled();
        }

        /// <summary>
        /// 读侧（共享 Delete）：读者不再挡住写侧把文件改名挪走（rename-away）。
        /// ★ 边界要说准：共享 Delete 解的是 <b>rename-away</b>（目标不存在）；
        /// 它<b>不能</b>让 replace-existing 成功 —— Windows 的 replace 要求目标无任何打开的句柄，
        /// replace 的出路是 <c>MarketFileIo.MoveAtomic</c> 的有界重试。
        /// </summary>
        [Fact]
        public void SharedRead_DoesNotBlockAWriterFromRenamingTheTargetAway()
        {
            using var scope = new InstallScope();

            // 旧读法：不共享 Delete ⇒ 写侧连改名都做不了。
            using (var legacy = new FileStream(scope.Target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.ThrowsAny<IOException>(() =>
                    File.Move(scope.Target, scope.RetiredPath, overwrite: true));
            }

            // 新读法：共享 Delete ⇒ 写侧可以把文件改名挪走。
            using (var shared = new FileStream(
                scope.Target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                File.Move(scope.Target, scope.RetiredPath, overwrite: true);
            }

            Assert.False(File.Exists(scope.Target));
            Assert.True(File.Exists(scope.RetiredPath));
        }

        // ================================================================
        // 辅助
        // ================================================================

        private static Task<T> StartWorker<T>(Func<T> body)
            => Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        /// <summary>
        /// 手动的单调时钟：只在用例显式推进时前进 ⇒ 预算判定完全确定，不依赖真实等待。
        /// </summary>
        private sealed class ManualClock : TimeProvider
        {
            private long _now;

            internal ManualClock() { _now = 0; }

            internal void Advance(TimeSpan delta)
                => _now += (long)(delta.TotalSeconds * TimestampFrequency);

            public override long GetTimestamp() => _now;
        }

        /// <summary>一次安装事务的沙箱（自带插件目录、旧文件与旧记录）。</summary>
        private sealed class InstallScope : IDisposable
        {
            private readonly string _root;
            private readonly MarketFileIo.ReplacePolicy? _savedPolicy;

            internal InstallScope()
            {
                _root = Path.Combine(AppContext.BaseDirectory, "f17-race-" + Guid.NewGuid().ToString("N"));
                PluginsDir = Path.Combine(_root, "plugins");
                Directory.CreateDirectory(PluginsDir);

                OldBytes = Encoding.UTF8.GetBytes("{\"id\":\"" + PluginId + "\",\"meta\":{\"version\":\"1.0\"}}");
                NewBytes = Encoding.UTF8.GetBytes("{\"id\":\"" + PluginId + "\",\"meta\":{\"version\":\"2.0\"}}");
                File.WriteAllBytes(Target, OldBytes);

                Records = new RecordStore();
                Records.Add(new MarketPluginRecord
                {
                    Id = PluginId,
                    File = PluginId + ".json",
                    Version = "1.0",
                    Sha256 = PluginMarket.ComputeSha256(OldBytes),
                    InstalledAt = "2020-01-02T03:04:05Z",
                });

                _savedPolicy = MarketFileIo.Policy.Value;
                InstallPolicy = null;
            }

            internal string PluginsDir { get; }
            internal string Target => Path.Combine(PluginsDir, PluginId + ".json");
            internal string RetiredPath => Path.Combine(PluginsDir, PluginId + ".retired");
            internal byte[] OldBytes { get; }
            internal byte[] NewBytes { get; }
            internal RecordStore Records { get; }
            internal ManualClock Clock { get; } = new ManualClock();

            /// <summary>本作用域的重试策略（null = 生产默认）。</summary>
            internal MarketFileIo.ReplacePolicy? InstallPolicy
            {
                get => MarketFileIo.Policy.Value;
                set => MarketFileIo.Policy.Value = value;
            }

            internal void Commit(Action<string>? fault = null)
                => PluginMarketInstallCore.CommitInstall(
                    PluginsDir, PluginId, Version, NewBytes,
                    PluginMarket.ComputeSha256(NewBytes),
                    Records.Find, Records.Upsert, Records.Remove, fault);

            /// <summary>安装成功：文件与记录都是新版本，且没有残留。</summary>
            internal void AssertInstalled()
            {
                Assert.Equal(NewBytes, File.ReadAllBytes(Target));
                Assert.Equal(Version, Records.Find(PluginId)?.Version);
                Assert.Empty(Directory.GetFiles(PluginsDir, "*.stage"));
                Assert.Empty(Directory.GetFiles(PluginsDir, "*.bak"));
                Assert.False(File.Exists(Path.Combine(PluginsDir, PluginMarketInstallCore.TransactionLogFileName)));
            }

            /// <summary>回滚干净：文件与记录都还是旧版本，且没有残留。</summary>
            internal void AssertRolledBackToOldState()
            {
                Assert.Equal(OldBytes, File.ReadAllBytes(Target));
                Assert.Equal("1.0", Records.Find(PluginId)?.Version);
                Assert.Empty(Directory.GetFiles(PluginsDir, "*.stage"));
                Assert.Empty(Directory.GetFiles(PluginsDir, "*.bak"));
                string journal = Path.Combine(PluginsDir, PluginMarketInstallCore.TransactionLogFileName);
                Assert.False(File.Exists(journal), "回滚后仍留下事务日志：" + journal);
            }

            public void Dispose()
            {
                MarketFileIo.Policy.Value = _savedPolicy;
                try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
                catch { }
            }
        }

        /// <summary>内存里的安装记录（不碰 Settings 单例，避免与其它用例串台）。</summary>
        private sealed class RecordStore
        {
            private readonly List<MarketPluginRecord> _records = new List<MarketPluginRecord>();

            internal void Add(MarketPluginRecord record) => _records.Add(record);

            internal MarketPluginRecord? Find(string id)
                => _records.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

            internal void Upsert(MarketPluginRecord record)
            {
                _records.RemoveAll(r => string.Equals(r.Id, record.Id, StringComparison.OrdinalIgnoreCase));
                _records.Add(record);
            }

            internal void Remove(string id)
                => _records.RemoveAll(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }
}
