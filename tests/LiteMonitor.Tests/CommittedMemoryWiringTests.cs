using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// #137「内存已提交」的**接线**测试：采集入口、取值入口、发布点、配置迁移、需求解耦。
    ///
    /// 覆盖计划 §5 的接线类硬门槛：
    ///   1) 只开启 "MEM.Commit.Load" 时**不触发**多余的 LHM 内存采集（IsAnyEnabled 前缀匹配解耦）；
    ///   2) 提交路径**不依赖**性能计数器（开关关闭 / 计数器未初始化时依然可用）；
    ///   3) 同一采集代次内只发生**一次**原生读取（UI 100ms + 多个 Web 读者）；
    ///   4) 面板 / 任务栏 / Web 读到的必须是**同一份已发布快照**，缺失时都显示缺失而不是 0；
    ///   5) 旧配置重复迁移时新项**只出现一次**，旧项与排序不变；
    ///   6) 新项默认**隐藏**。
    ///
    /// 这些用例**不打开 LHM、不碰驱动**：只构造一个没有打开的 Computer（硬件列表为空）。
    /// </summary>
    [Collection("cfgfile")]
    public class CommittedMemoryWiringTests : IDisposable
    {
        private const string Key = "MEM.Commit.Load";

        public CommittedMemoryWiringTests()
        {
            CommittedMemoryPublisher.Clear();
        }

        public void Dispose() => CommittedMemoryPublisher.Clear();

        private static long GiB(long gib) => gib * 1024L * 1024L * 1024L;

        /// <summary>
        /// 假原生数据源：可注入固定读数，并统计**原生读取次数**。
        /// </summary>
        private sealed class CountingSource : ICommittedMemorySource
        {
            public int ReadCount { get; private set; }

            public CommittedMemoryRaw Raw { get; set; }

            public bool ShouldFail { get; set; }

            public CommittedMemoryStatus LastStatus => ShouldFail ? CommittedMemoryStatus.ApiFailed : CommittedMemoryStatus.Ok;

            public string? LastError => ShouldFail ? "模拟 GetPerformanceInfo 失败" : null;

            public bool TryRead(out CommittedMemoryRaw raw)
            {
                ReadCount++;
                if (ShouldFail)
                {
                    raw = default;
                    return false;
                }
                raw = Raw;
                return true;
            }
        }

        private static CommittedMemoryRaw RawOf(long committedBytes, long limitBytes)
        {
            const uint pageSize = 4096;
            return new CommittedMemoryRaw(
                pageSize: pageSize,
                commitTotalPages: (ulong)(committedBytes / pageSize),
                commitLimitPages: (ulong)(limitBytes / pageSize));
        }

        /// <summary>发布一次有效快照（字节口径）。</summary>
        private static void PublishBytes(long committedBytes, long limitBytes, int generation = 1)
        {
            CommittedMemorySnapshot snapshot =
                CommittedMemoryCalculator.Compute(RawOf(committedBytes, limitBytes), generation, 1000);
            CommittedMemoryPublisher.Publish(generation, snapshot);
        }

        // =========================================================
        // 1) IsAnyEnabled("MEM") 的前缀匹配解耦
        // =========================================================

        /// <summary>造一份配置：只开启指定的监控项（其余全部关闭）。</summary>
        private static Settings ConfigWithOnly(params string[] enabledKeys)
        {
            var cfg = new Settings();
            cfg.InitDefaultItems();

            foreach (var item in cfg.MonitorItems)
            {
                bool enabled = enabledKeys.Any(k => string.Equals(k, item.Key, StringComparison.OrdinalIgnoreCase));
                item.VisibleInPanel = enabled;
                item.VisibleInTaskbar = enabled;
            }
            return cfg;
        }

        [Fact]
        public void 只开启提交内存时不会触发多余的LHM内存采集()
        {
            Settings cfg = ConfigWithOnly(Key);

            // 不走性能计数器时，物理内存需求为假 → LHM 内存传感器不必轮询
            var req = HardwareUpdateRequirements.Compute(cfg, hasWebDataDemand: false, usePerformanceCounters: false);

            Assert.False(req.NeedMem, "只开启提交内存时不应拉起 LHM 内存采集");
        }

        [Fact]
        public void 只开启物理内存时仍然需要LHM内存采集()
        {
            Settings cfg = ConfigWithOnly("MEM.Load");

            var req = HardwareUpdateRequirements.Compute(cfg, hasWebDataDemand: false, usePerformanceCounters: false);

            Assert.True(req.NeedMem, "只开启物理内存时 LHM 内存采集必须照旧");
        }

        [Fact]
        public void 前缀匹配仍然命中提交内存_但物理内存需求已解耦()
        {
            Settings cfg = ConfigWithOnly(Key);

            // 既有行为不变：IsAnyEnabled 是前缀匹配，"MEM.Commit.Load" 仍然命中 "MEM"
            Assert.True(cfg.IsAnyEnabled("MEM"), "IsAnyEnabled 的前缀匹配语义必须保持不变");

            // 但物理内存需求必须解耦
            Assert.False(cfg.IsAnyPhysicalMemoryEnabled());
        }

        [Fact]
        public void 提交内存不受性能计数器开关影响()
        {
            Settings cfg = ConfigWithOnly(Key);

            var withCounters = HardwareUpdateRequirements.Compute(cfg, hasWebDataDemand: false, usePerformanceCounters: true);
            var withoutCounters = HardwareUpdateRequirements.Compute(cfg, hasWebDataDemand: false, usePerformanceCounters: false);

            // 两种开关状态下都不需要 LHM 内存：提交内存走独立通路
            Assert.False(withCounters.NeedMem);
            Assert.False(withoutCounters.NeedMem);
        }

        // =========================================================
        // 2) 取值入口：不依赖性能计数器，缺失返回 null
        // =========================================================

        private static HardwareValueProvider NewProvider(Settings cfg)
        {
            return new HardwareValueProvider(
                new Computer(),
                cfg,
                new SensorMap(),
                new NetworkManager(),
                new DiskManager(),
                null,                       // 不需要 FPS
                new PerformanceCounterManager(),   // 未初始化（IsInitialized == false）
                new object(),
                new Dictionary<string, float>());
        }

        [Fact]
        public void 计数器未初始化且开关关闭时提交内存依然可取值()
        {
            PublishBytes(GiB(48), GiB(64), generation: 1);

            var cfg = new Settings { UseWinPerCounters = false };
            using var provider = NewProvider(cfg);

            float? value = provider.GetValue(Key);

            Assert.True(value.HasValue, "计数器开关关闭时提交内存必须仍然可取值");
            Assert.Equal(75f, value!.Value, 3);
        }

        [Fact]
        public void 取值层在快照缺失时返回null而不是零()
        {
            CommittedMemoryPublisher.Publish(
                1,
                CommittedMemorySnapshot.Missing(CommittedMemoryStatus.ApiFailed, "模拟失败", 1, 1000));

            var cfg = new Settings { UseWinPerCounters = false };
            using var provider = NewProvider(cfg);

            float? value = provider.GetValue(Key);

            Assert.Null(value);
        }

        [Fact]
        public void 取值层在快照尚未就绪时返回null而不是零()
        {
            CommittedMemoryPublisher.Clear();

            var cfg = new Settings { UseWinPerCounters = false };
            using var provider = NewProvider(cfg);

            Assert.Null(provider.GetValue(Key));
        }

        // =========================================================
        // 3) 采集入口：每代次只发生一次原生读取
        // =========================================================

        [Fact]
        public void 同一采集代次内只发生一次原生读取()
        {
            var source = new CountingSource { Raw = RawOf(GiB(48), GiB(64)) };
            var reader = new CommittedMemoryReader(source);

            HardwareMonitor.PublishCommittedMemory(reader, 1);
            Assert.Equal(1, source.ReadCount);

            // 同代次再采一次：复用本代次结果，不再触发原生调用
            HardwareMonitor.PublishCommittedMemory(reader, 1);
            Assert.Equal(1, source.ReadCount);

            // 消费者反复读"已发布"状态：绝不触发任何原生调用
            for (int i = 0; i < 50; i++)
            {
                reader.TryGetPublished(1);
            }
            Assert.Equal(1, source.ReadCount);

            // 新代次：重新读一次（提交上限必须每轮重读）
            HardwareMonitor.PublishCommittedMemory(reader, 2);
            Assert.Equal(2, source.ReadCount);
        }

        [Fact]
        public void UI高频刷新加多个Web读者在同一代次只读一次()
        {
            var source = new CountingSource { Raw = RawOf(GiB(48), GiB(64)) };
            var reader = new CommittedMemoryReader(source);

            // 采集层：这一代采一次
            HardwareMonitor.PublishCommittedMemory(reader, 7);

            // UI 每 100ms 取一次（10 次）+ 5 个 Web 读者各取一次
            for (int tick = 0; tick < 10; tick++)
            {
                reader.TryGetPublished(7);
            }
            for (int web = 0; web < 5; web++)
            {
                reader.TryGetPublished(7);
            }

            Assert.Equal(1, source.ReadCount);
            Assert.Equal(7, CommittedMemoryPublisher.Current!.Generation);
        }

        [Fact]
        public void 发布点发布的快照带正确的提交量与上限()
        {
            var source = new CountingSource { Raw = RawOf(GiB(48), GiB(64)) };
            var reader = new CommittedMemoryReader(source);

            CommittedMemoryDisplay display = HardwareMonitor.PublishCommittedMemory(reader, 3);

            Assert.Equal(3, display.Generation);
            Assert.False(display.IsMissing);
            Assert.Equal(GiB(48), display.CommittedBytes);
            Assert.Equal(GiB(64), display.LimitBytes);
            Assert.Equal(75d, display.LoadPercent, 6);
        }

        [Fact]
        public void 原生读取失败时发布的是缺失状态而不是零()
        {
            var source = new CountingSource { ShouldFail = true };
            var reader = new CommittedMemoryReader(source);

            CommittedMemoryDisplay display = HardwareMonitor.PublishCommittedMemory(reader, 1);

            Assert.True(display.IsMissing);
            Assert.Null(CommittedMemoryPublisher.TryGetLoadPercent());
        }

        // =========================================================
        // 4) 面板 / 任务栏 / Web：同一快照口径一致
        // =========================================================

        [Fact]
        public void 面板任务栏与Web读到的都是同一份已发布快照()
        {
            PublishBytes(GiB(48), GiB(64), generation: 5);

            var cfg = Settings.Load();
            int oldMode = cfg.MemoryDisplayMode;
            try
            {
                cfg.MemoryDisplayMode = 1;

                // 面板
                string panel = MetricUtils.GetValueStr(Key, 75f, false, null)
                             + MetricUtils.GetDisplayUnit(Key, MetricUtils.GetUnitStr(Key, 75f, MetricUtils.UnitContext.Panel), null);

                // 任务栏
                string taskbar = MetricUtils.GetValueStr(Key, 75f, true, null)
                              + MetricUtils.GetDisplayUnit(Key, MetricUtils.GetUnitStr(Key, 75f, MetricUtils.UnitContext.Taskbar), null);

                // Web（网页快照走的是同一套 formatter）
                string web = MetricUtils.GetValueStr(Key, 75f, false)
                           + MetricUtils.GetDisplayUnit(Key, MetricUtils.GetUnitStr(Key, 75f, MetricUtils.UnitContext.Panel), null);

                Assert.Equal("48.0 / 64.0GB", panel);
                Assert.Equal("48.0 / 64.0GB", taskbar);
                Assert.Equal("48.0 / 64.0GB", web);
            }
            finally
            {
                cfg.MemoryDisplayMode = oldMode;
            }
        }

        [Fact]
        public void 缺失时三个出口都显示缺失而不是零()
        {
            CommittedMemoryPublisher.Publish(
                1,
                CommittedMemorySnapshot.Missing(CommittedMemoryStatus.InvalidLimit, "模拟上限为 0", 1, 1000));

            var cfg = Settings.Load();
            int oldMode = cfg.MemoryDisplayMode;
            try
            {
                cfg.MemoryDisplayMode = 1;

                string panel = MetricUtils.GetValueStr(Key, 75f, false, null)
                             + MetricUtils.GetDisplayUnit(Key, MetricUtils.GetUnitStr(Key, 75f, MetricUtils.UnitContext.Panel), null);
                string taskbar = MetricUtils.GetValueStr(Key, 75f, true, null)
                              + MetricUtils.GetDisplayUnit(Key, MetricUtils.GetUnitStr(Key, 75f, MetricUtils.UnitContext.Taskbar), null);
                string web = MetricUtils.GetValueStr(Key, 75f, false)
                           + MetricUtils.GetDisplayUnit(Key, MetricUtils.GetUnitStr(Key, 75f, MetricUtils.UnitContext.Panel), null);

                Assert.Equal("--", panel);
                Assert.Equal("--", taskbar);
                Assert.Equal("--", web);
            }
            finally
            {
                cfg.MemoryDisplayMode = oldMode;
            }
        }

        // =========================================================
        // 5) 配置迁移：幂等 + 旧项排序不变
        // =========================================================

        [Fact]
        public void 新监控项默认隐藏()
        {
            var cfg = new Settings();
            cfg.InitDefaultItems();

            var item = cfg.MonitorItems.FirstOrDefault(x => x.Key == Key);

            Assert.NotNull(item);
            Assert.False(item!.VisibleInPanel, "新项默认在面板隐藏");
            Assert.False(item.VisibleInTaskbar, "新项默认在任务栏隐藏");
        }

        [Fact]
        public void 重复迁移时新项只出现一次且旧项排序不变()
        {
            var cfg = new Settings();
            cfg.InitDefaultItems();

            // 模拟旧配置：没有新项，且已有 TaskbarSortIndex（非遗留配置走 CheckAndAppendMissingItems）
            cfg.MonitorItems.RemoveAll(x => x.Key == Key);
            foreach (var item in cfg.MonitorItems) item.TaskbarSortIndex = item.SortIndex;

            List<string> before = cfg.MonitorItems
                .OrderBy(x => x.SortIndex)
                .Select(x => x.Key)
                .ToList();

            // 第一次迁移
            cfg.CheckAndAppendMissingItems();
            Assert.Equal(1, cfg.MonitorItems.Count(x => x.Key == Key));

            // 重复迁移（幂等）
            cfg.CheckAndAppendMissingItems();
            cfg.CheckAndAppendMissingItems();
            Assert.Equal(1, cfg.MonitorItems.Count(x => x.Key == Key));

            // 旧项**相对排序**不变
            List<string> after = cfg.MonitorItems
                .Where(x => x.Key != Key)
                .OrderBy(x => x.SortIndex)
                .Select(x => x.Key)
                .ToList();

            Assert.Equal(before, after);

            // 新项紧挨既有内存项（MEM.Load 之后）
            List<string> ordered = cfg.MonitorItems.OrderBy(x => x.SortIndex).Select(x => x.Key).ToList();
            int memIdx = ordered.IndexOf("MEM.Load");
            Assert.True(memIdx >= 0);
            Assert.Equal(Key, ordered[memIdx + 1]);
        }

        [Fact]
        public void 迁移保留用户自定义标签与可见性()
        {
            var cfg = new Settings();
            cfg.InitDefaultItems();

            var memItem = cfg.MonitorItems.First(x => x.Key == "MEM.Load");
            memItem.UserLabel = "我的内存标签";
            memItem.VisibleInPanel = false;

            cfg.MonitorItems.RemoveAll(x => x.Key == Key);
            cfg.CheckAndAppendMissingItems();

            var migrated = cfg.MonitorItems.First(x => x.Key == "MEM.Load");
            Assert.Equal("我的内存标签", migrated.UserLabel);
            Assert.False(migrated.VisibleInPanel);
        }

        [Fact]
        public void 遗留配置重建路径下新项同样只出现一次()
        {
            var cfg = new Settings();
            cfg.InitDefaultItems();

            // 遗留配置：全部 TaskbarSortIndex == 0（走 RebuildAndMigrateSettings）
            foreach (var item in cfg.MonitorItems) item.TaskbarSortIndex = 0;
            cfg.MonitorItems.RemoveAll(x => x.Key == Key);

            cfg.RebuildAndMigrateSettings();

            Assert.Equal(1, cfg.MonitorItems.Count(x => x.Key == Key));

            // 重复执行仍然只有一项
            cfg.RebuildAndMigrateSettings();
            Assert.Equal(1, cfg.MonitorItems.Count(x => x.Key == Key));
        }
    }
}
