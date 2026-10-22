using System;
using System.Reflection;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// #137「内存已提交」原生采集层与纯计算层的测试。
    ///
    /// 覆盖计划 §5 的硬门槛：
    ///   1) 分母必须是**提交上限**，不能是物理内存总量（48/64 GiB 必须算 75%，不是 150%）；
    ///   2) 提交上限每轮重读（分页文件会变），不能缓存成启动时的物理内存大小；
    ///   3) 字节量全程 64 位，不能经过 float 丢精度；
    ///   4) "缺失"和"有效的 0"必须能区分；
    ///   5) 新路径不依赖性能计数器（开关关闭 / 计数器未初始化时照样可用）；
    ///   6) 同一采集代次内多次读取只发生一次原生读取。
    ///
    /// 这些用例**不碰真实硬件、不改系统设置**：原生数据源全部是注入的假实现。
    /// 唯一的例外是 <see cref="原生数据源在本机可以读到合理的提交量"/>，它只读不写。
    /// </summary>
    public class CommittedMemoryReaderTests
    {
        private const uint PageSize = 4096;

        private static long GiB(long gib) => gib * 1024L * 1024L * 1024L;

        private static ulong PagesOf(long bytes) => (ulong)(bytes / PageSize);

        /// <summary>固定页大小的原始读数。</summary>
        private static CommittedMemoryRaw Raw(long committedBytes, long limitBytes, long physicalTotalBytes = 0)
        {
            return new CommittedMemoryRaw(
                pageSize: PageSize,
                commitTotalPages: PagesOf(committedBytes),
                commitLimitPages: PagesOf(limitBytes),
                physicalTotalPages: physicalTotalBytes == 0 ? 0 : PagesOf(physicalTotalBytes));
        }

        /// <summary>固定时间戳的假时钟（快照时间戳必须是单调的，不能受墙上时间影响）。</summary>
        private sealed class FakeTimeProvider : TimeProvider
        {
            private long _ticks = 1000;

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => _ticks;
            public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

            public void Advance(TimeSpan delta) => _ticks += delta.Ticks;
        }

        /// <summary>
        /// 假原生数据源：可注入固定读数、可模拟 API 失败 / 异常，并统计原生读取次数。
        /// </summary>
        private sealed class FakeCommittedMemorySource : ICommittedMemorySource
        {
            public int ReadCount { get; private set; }

            /// <summary>本次读取要返回的原始读数（读取时取当前值，因此外部可以随时改）。</summary>
            public CommittedMemoryRaw Raw { get; set; }

            /// <summary>是否模拟 API 失败。</summary>
            public bool ShouldFail { get; set; }

            /// <summary>模拟 API 抛异常。</summary>
            public bool ThrowOnRead { get; set; }

            /// <summary>失败时上报的状态。</summary>
            public CommittedMemoryStatus FailStatus { get; set; } = CommittedMemoryStatus.ApiFailed;

            public string? FailError { get; set; } = "模拟 GetPerformanceInfo 失败";

            public CommittedMemoryStatus LastStatus => ShouldFail ? FailStatus : CommittedMemoryStatus.Ok;

            public string? LastError => ShouldFail ? FailError : null;

            public bool TryRead(out CommittedMemoryRaw raw)
            {
                ReadCount++;
                if (ThrowOnRead) throw new InvalidOperationException("模拟原生调用异常");
                if (ShouldFail)
                {
                    raw = default;
                    return false;
                }
                raw = Raw;
                return true;
            }
        }

        private static (CommittedMemoryReader reader, FakeCommittedMemorySource source, FakeTimeProvider clock) NewReader(
            CommittedMemoryRaw raw)
        {
            var source = new FakeCommittedMemorySource { Raw = raw };
            var clock = new FakeTimeProvider();
            return (new CommittedMemoryReader(source, clock), source, clock);
        }

        // =========================================================
        // 1) 分母是提交上限，不是物理内存总量
        // =========================================================

        [Fact]
        public void 提交比例的分母是提交上限而不是物理内存总量()
        {
            // 上下文：物理内存 32 GiB，提交量 48 GiB，提交上限 64 GiB
            var (reader, _, _) = NewReader(Raw(GiB(48), GiB(64), GiB(32)));

            var snap = reader.Capture(1);

            Assert.True(snap.IsValid);
            Assert.Equal(75.0, snap.LoadPercent, 6);
            // ★ 关键断言：用物理内存当分母会算成 48/32 = 150%
            Assert.NotEqual(150.0, snap.LoadPercent, 6);
            Assert.Equal(GiB(48), snap.CommittedBytes);
            Assert.Equal(GiB(64), snap.LimitBytes);
        }

        [Fact]
        public void 提交量小于物理内存时比例不会超过100()
        {
            // 提交量 16 GiB / 上限 64 GiB（物理内存 32 GiB 无关）
            var (reader, _, _) = NewReader(Raw(GiB(16), GiB(64), GiB(32)));

            var snap = reader.Capture(1);

            Assert.Equal(25.0, snap.LoadPercent, 6);
            Assert.True(snap.LoadPercent <= 100.0);
        }

        // =========================================================
        // 2) 提交上限每轮重读（分页文件会动态变化）
        // =========================================================

        [Fact]
        public void 提交上限变化时下一代次的分母和比例同步变化()
        {
            var (reader, source, _) = NewReader(Raw(GiB(48), GiB(64)));

            var first = reader.Capture(1);
            Assert.Equal(75.0, first.LoadPercent, 6);
            Assert.Equal(GiB(64), first.LimitBytes);
            Assert.Equal(1, first.Generation);

            // 提交上限变大（分页文件变大），提交量不变
            source.Raw = Raw(GiB(48), GiB(80));
            var second = reader.Capture(2);

            Assert.Equal(60.0, second.LoadPercent, 6);
            Assert.Equal(GiB(80), second.LimitBytes);
            Assert.Equal(GiB(48), second.CommittedBytes);
            Assert.Equal(2, second.Generation);
        }

        [Fact]
        public void 提交上限不是被缓存成启动时的物理内存大小()
        {
            // 第一轮的上限恰好等于物理内存大小（最容易和"缓存了物理内存"混淆）
            var (reader, source, _) = NewReader(Raw(GiB(40), GiB(32), GiB(32)));

            var first = reader.Capture(1);
            Assert.Equal(GiB(32), first.LimitBytes);

            // 第二轮上限变了 -> 必须跟着变，说明上限是每轮重读的
            source.Raw = Raw(GiB(40), GiB(48), GiB(32));
            var second = reader.Capture(2);

            Assert.Equal(GiB(48), second.LimitBytes);
            Assert.NotEqual(GiB(32), second.LimitBytes);
        }

        // =========================================================
        // 3) 64 位换算，不经过 float
        // =========================================================

        [Fact]
        public void 页数很大时按64位换算且不经过float()
        {
            // 572,662,307 页需要 30 位有效位 —— float 只有 24 位，float 表示不了这个数
            const ulong commitPages = 572662307UL;
            const ulong limitPages = 1145324614UL;

            var source = new FakeCommittedMemorySource
            {
                Raw = new CommittedMemoryRaw(PageSize, commitPages, limitPages)
            };
            var reader = new CommittedMemoryReader(source, new FakeTimeProvider());

            var snap = reader.Capture(1);

            long expectedCommitted = (long)commitPages * PageSize;   // 2,345,624,809,472
            long expectedLimit = (long)limitPages * PageSize;

            Assert.True(snap.IsValid);
            Assert.Equal(expectedCommitted, snap.CommittedBytes);
            Assert.Equal(expectedLimit, snap.LimitBytes);
            Assert.Equal(50.0, snap.LoadPercent, 6);

            // ★ 证明 float 真的会丢这个数：所以字节量必须全程保持 long，
            //   只有百分比在最后才适配成 float。
            Assert.Equal(50.0, snap.LoadPercentAsFloat, 3);
            Assert.NotEqual(expectedCommitted, (long)(float)expectedCommitted);
        }

        [Fact]
        public void 大数值下百分比仍然精确()
        {
            // 3 TiB 提交量 / 6 TiB 上限，页大小 4096
            long tib = 1024L * 1024L * 1024L * 1024L;
            var (reader, _, _) = NewReader(Raw(3 * tib, 6 * tib));

            var snap = reader.Capture(1);

            Assert.Equal(3L * tib, snap.CommittedBytes);
            Assert.Equal(6L * tib, snap.LimitBytes);
            Assert.Equal(50.0, snap.LoadPercent, 9);
        }

        // =========================================================
        // 4) 有效的 0 与缺失必须能区分
        // =========================================================

        [Fact]
        public void 提交量为0是有效值不是缺失()
        {
            var (reader, _, _) = NewReader(Raw(0, GiB(64)));

            var snap = reader.Capture(1);

            Assert.True(snap.IsValid);
            Assert.False(snap.IsMissing);
            Assert.Equal(0L, snap.CommittedBytes);
            Assert.Equal(0.0, snap.LoadPercent, 6);
            Assert.Equal(0.0, snap.LoadPercentAsFloat, 6);
            // 有效 0 绝不能被当成缺失：发布出去的快照必须存在
            Assert.NotNull(reader.TryGetPublished(1));
        }

        [Fact]
        public void 有效0与缺失必须能被区分开()
        {
            var (reader, source, _) = NewReader(Raw(0, GiB(64)));
            var valid = reader.Capture(1);
            Assert.NotNull(reader.TryGetPublished(1));

            // 换成失败的数据源，进入新代次
            source.ShouldFail = true;
            var missing = reader.Capture(2);

            Assert.True(valid.IsValid);
            Assert.False(valid.IsMissing);
            Assert.True(missing.IsMissing);
            Assert.False(missing.IsValid);
            Assert.Null(reader.TryGetPublished(2));
        }

        // =========================================================
        // 5) 失败一律判缺失，绝不伪造 0
        // =========================================================

        [Fact]
        public void API失败时返回缺失而不是伪造0()
        {
            var (reader, _, _) = NewReader(Raw(GiB(48), GiB(64)));
            // 让数据源失败
            var failing = new FakeCommittedMemorySource { ShouldFail = true };
            var reader2 = new CommittedMemoryReader(failing, new FakeTimeProvider());

            var snap = reader2.Capture(1);

            Assert.False(snap.IsValid);
            Assert.True(snap.IsMissing);
            Assert.Equal(CommittedMemoryStatus.ApiFailed, snap.Status);
            Assert.Null(reader2.TryGetPublished(1));
            // 缺失绝不能被当成 0% 上报
            Assert.Equal(0.0, snap.LoadPercent, 6);
            Assert.False(snap.IsValid);
        }

        [Fact]
        public void 原生调用抛异常时返回缺失()
        {
            var source = new FakeCommittedMemorySource { ThrowOnRead = true };
            var reader = new CommittedMemoryReader(source, new FakeTimeProvider());

            var snap = reader.Capture(1);

            Assert.True(snap.IsMissing);
            Assert.Equal(CommittedMemoryStatus.ApiFailed, snap.Status);
            Assert.False(string.IsNullOrEmpty(snap.ErrorMessage));
        }

        [Fact]
        public void 提交上限为0时返回缺失()
        {
            var (reader, _, _) = NewReader(Raw(GiB(48), 0));

            var snap = reader.Capture(1);

            Assert.True(snap.IsMissing);
            Assert.Equal(CommittedMemoryStatus.InvalidLimit, snap.Status);
            Assert.Null(reader.TryGetPublished(1));
        }

        [Fact]
        public void 页大小为0时返回缺失()
        {
            var source = new FakeCommittedMemorySource
            {
                Raw = new CommittedMemoryRaw(pageSize: 0, commitTotalPages: 100, commitLimitPages: 200)
            };
            var reader = new CommittedMemoryReader(source, new FakeTimeProvider());

            var snap = reader.Capture(1);

            Assert.True(snap.IsMissing);
            Assert.Equal(CommittedMemoryStatus.InvalidPageSize, snap.Status);
            Assert.Null(reader.TryGetPublished(1));
        }

        [Fact]
        public void 页数换算溢出时返回缺失()
        {
            var source = new FakeCommittedMemorySource
            {
                Raw = new CommittedMemoryRaw(PageSize, ulong.MaxValue, 1024)
            };
            var reader = new CommittedMemoryReader(source, new FakeTimeProvider());

            var snap = reader.Capture(1);

            Assert.True(snap.IsMissing);
            Assert.Equal(CommittedMemoryStatus.Overflow, snap.Status);
            Assert.Null(reader.TryGetPublished(1));
        }

        [Fact]
        public void 缺失时不会套用物理内存占用率()
        {
            // 物理内存 32 GiB、提交 48 GiB；API 失败后绝不能退化成 48/32 = 150%
            var source = new FakeCommittedMemorySource { ShouldFail = true };
            var reader = new CommittedMemoryReader(source, new FakeTimeProvider());

            var snap = reader.Capture(1);

            Assert.True(snap.IsMissing);
            Assert.NotEqual(150.0, snap.LoadPercent, 6);
        }

        // =========================================================
        // 6) 不依赖性能计数器
        // =========================================================

        [Fact]
        public void 计数器未初始化且开关关闭时新路径仍然可用()
        {
            // 计数器管理器确实没初始化
            var perf = new PerformanceCounterManager();
            Assert.False(perf.IsInitialized);

            // "优先使用系统计数器"开关关闭
            var cfg = new Settings { UseWinPerCounters = false };
            Assert.False(cfg.UseWinPerCounters);

            var (reader, _, _) = NewReader(Raw(GiB(48), GiB(64)));

            var snap = reader.Capture(1);

            Assert.True(snap.IsValid);
            Assert.Equal(75.0, snap.LoadPercent, 6);
        }

        [Fact]
        public void 新读取链路没有任何地方引用性能计数器管理器()
        {
            Type[] types =
            {
                typeof(CommittedMemoryReader),
                typeof(CommittedMemoryCalculator),
                typeof(CommittedMemorySnapshot),
                typeof(WindowsCommittedMemorySource),
                typeof(CommittedMemoryRaw),
            };

            foreach (Type t in types)
            {
                foreach (ConstructorInfo ctor in t.GetConstructors())
                {
                    foreach (ParameterInfo p in ctor.GetParameters())
                    {
                        Assert.NotEqual(typeof(PerformanceCounterManager), p.ParameterType);
                    }
                }

                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    Assert.NotEqual(typeof(PerformanceCounterManager), f.FieldType);
                }

                foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    Assert.NotEqual(typeof(PerformanceCounterManager), p.PropertyType);
                }
            }
        }

        // =========================================================
        // 7) 采集代次：同代次只读一次
        // =========================================================

        [Fact]
        public void 同一采集代次内多次读取只发生一次原生读取()
        {
            var (reader, source, _) = NewReader(Raw(GiB(48), GiB(64)));

            var a = reader.Capture(1);
            var b = reader.Capture(1);
            var c = reader.Capture(1);

            Assert.Equal(1, source.ReadCount);
            Assert.Same(a, b);
            Assert.Same(b, c);
            Assert.Equal(1, a.Generation);

            // 进入新代次 -> 再读一次
            reader.Capture(2);
            Assert.Equal(2, source.ReadCount);
        }

        [Fact]
        public void 取已发布快照不会触发原生读取()
        {
            var (reader, source, _) = NewReader(Raw(GiB(48), GiB(64)));
            reader.Capture(1);

            reader.TryGetPublished(1);
            reader.TryGetPublished(1);
            reader.TryGetPublished(1);

            // 消费者反复取快照不能让系统调用次数涨上去
            Assert.Equal(1, source.ReadCount);
        }

        [Fact]
        public void 同代次内失败也不会重复读取()
        {
            var source = new FakeCommittedMemorySource { ShouldFail = true };
            var reader = new CommittedMemoryReader(source, new FakeTimeProvider());

            reader.Capture(1);
            reader.Capture(1);
            reader.Capture(1);

            // 失败同样要按代次缓存，否则同代次的多个消费者会把失败路径反复重试
            Assert.Equal(1, source.ReadCount);
        }

        [Fact]
        public void 旧采集代次的回调不会覆盖新代次()
        {
            var (reader, source, _) = NewReader(Raw(GiB(48), GiB(64)));

            var gen2 = reader.Capture(2);
            var stale = reader.Capture(1);

            Assert.Equal(CommittedMemoryStatus.StaleGeneration, stale.Status);
            Assert.True(stale.IsMissing);
            // 旧代次既不触发原生读取，也不改写已发布的新代次快照
            Assert.Equal(1, source.ReadCount);
            var published = reader.TryGetPublished(2);
            Assert.NotNull(published);
            Assert.Equal(2, published!.Generation);
            Assert.Equal(gen2.LoadPercent, published.LoadPercent, 9);
        }

        [Fact]
        public void 未发布当前代次时取不到快照()
        {
            var (reader, _, _) = NewReader(Raw(GiB(48), GiB(64)));

            // 还没有任何采集发生
            Assert.Null(reader.TryGetPublished(1));

            reader.Capture(1);
            Assert.NotNull(reader.TryGetPublished(1));

            // 代次对不上（消费者拿着上一代的代次来取）
            Assert.Null(reader.TryGetPublished(2));
        }

        // =========================================================
        // 8) 异常样本：保留真实值，只钳制展示
        // =========================================================

        [Fact]
        public void 提交量超过上限时保留真实字节值只钳制展示比例()
        {
            var (reader, _, _) = NewReader(Raw(GiB(80), GiB(64)));

            var snap = reader.Capture(1);

            Assert.True(snap.IsValid);
            Assert.True(snap.IsAnomalous);
            // 真实字节值不被覆盖
            Assert.Equal(GiB(80), snap.CommittedBytes);
            Assert.Equal(GiB(64), snap.LimitBytes);
            Assert.Equal(125.0, snap.LoadPercent, 6);
            // 展示按项目现有进度条规则钳制到 [0,100]
            Assert.Equal(100.0, snap.DisplayLoadPercent, 6);
            Assert.Equal(100.0, (double)snap.DisplayLoadPercentAsFloat, 3);
        }

        // =========================================================
        // 9) 快照自检
        // =========================================================

        [Fact]
        public void 快照携带采集代次和单调时间戳()
        {
            var (reader, _, clock) = NewReader(Raw(GiB(48), GiB(64)));

            var first = reader.Capture(7);
            long t1 = first.Timestamp;

            clock.Advance(TimeSpan.FromMilliseconds(500));
            var second = reader.Capture(8);
            long t2 = second.Timestamp;

            Assert.Equal(7, first.Generation);
            Assert.Equal(8, second.Generation);
            Assert.True(t2 > t1);
        }

        [Fact]
        public void 快照是只读的()
        {
            var (reader, _, _) = NewReader(Raw(GiB(48), GiB(64)));
            var snap = reader.Capture(1);

            // 所有公开属性都只有 getter：快照被多个消费者共享，不能被谁改掉
            foreach (PropertyInfo p in typeof(CommittedMemorySnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.NotNull(p.GetGetMethod());
                Assert.Null(p.GetSetMethod());
            }
        }

        [Fact]
        public void 原生结构体大小必须与期望一致()
        {
            // 64 位: 104, 32 位: 56。结构布局对不上时原生 API 会直接失败，
            // 所以这里把"实际 marshaled 出来的大小"和"期望值"都暴露出来做自检。
            int expected = IntPtr.Size == 8 ? 104 : 56;
            Assert.Equal(expected, WindowsCommittedMemorySource.ExpectedPerformanceInformationSize);
            Assert.Equal(expected, WindowsCommittedMemorySource.ActualStructureSize);
        }

        // =========================================================
        // 10) 阳性对照：旧路径行为不变
        // =========================================================

        [Fact]
        public void 阳性对照_物理内存路径行为不变()
        {
            var perf = new PerformanceCounterManager();   // 未初始化
            var (load, usedGb) = perf.GetMemoryData();

            // 计数器没初始化时，物理内存路径仍然是"取不到"（而不是被新链路影响）
            Assert.Null(load);
            Assert.Null(usedGb);
        }

        [Fact]
        public void 阳性对照_显存路径行为不变()
        {
            var (used, total) = GpuVramSource.Select(
                vendorUsedMB: 6495f,
                vendorTotalMB: 20480f,
                d3dUsedMB: 4.1f,
                d3dTotalMB: null,
                realTotalMB: 20480f,
                useD3D: false);

            Assert.Equal(6495f, used!.Value, 3);
            Assert.Equal(20480f, total!.Value, 3);
        }

        [Fact]
        public void 阳性对照_物理内存比例仍然是按物理内存算的()
        {
            // 新链路不得影响旧口径：物理内存 16/32 GiB = 50%
            var (reader, _, _) = NewReader(Raw(GiB(16), GiB(64), GiB(32)));
            var snap = reader.Capture(1);

            // 提交链路自己的比例是 16/64 = 25%
            Assert.Equal(25.0, snap.LoadPercent, 6);
            // 物理内存总量仍然只是诊断信息，不会被当成上限
            Assert.Equal(GiB(64), snap.LimitBytes);
        }

        // =========================================================
        // 11) 真机只读冒烟（不写任何系统设置）
        // =========================================================

        [Fact]
        public void 原生数据源在本机可以读到合理的提交量()
        {
            if (!OperatingSystem.IsWindows()) return;

            var source = new WindowsCommittedMemorySource();
            if (!source.TryRead(out var raw)) return;   // 受限环境读不到，跳过

            Assert.True(raw.PageSize >= 4096);
            Assert.True(raw.CommitLimitPages > 0);
            Assert.True(raw.PhysicalTotalPages > 0);

            var snap = CommittedMemoryCalculator.Compute(raw, 1, 0);
            Assert.True(snap.IsValid);
            Assert.True(snap.CommittedBytes >= 0);
            Assert.True(snap.LimitBytes > 0);
            Assert.True(snap.LoadPercent >= 0.0);
        }
    }
}
