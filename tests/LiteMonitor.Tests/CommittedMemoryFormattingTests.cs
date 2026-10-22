using System;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// #137「内存已提交」的**格式分支**测试。
    ///
    /// 覆盖计划 §5 的格式类硬门槛：
    ///   1) 分母是**提交上限**（48/64 GiB → 75%，绝不能被当成 150%）；
    ///   2) 容量模式显示「提交量 / 当前提交上限」，分子分母**同倍率同快照**；
    ///   3) 缺失（未就绪 / API 失败 / 上限为 0）显示缺失，**不是 0**，也不回退物理内存率；
    ///   4) 合法的提交量 0 是**有效值**，必须正常显示 0；
    ///   5) 单项 % 覆盖 / 隐藏单位 / 自动单位三种情况下数值与单位必须配对；
    ///   6) 阳性对照：MEM.Load 与 GPU.VRAM 的既有格式**完全不变**。
    ///
    /// 本类会改进程级状态（Settings 单例的显示口径 + 提交内存发布点），
    /// 因此与其它读写 settings.json 的用例串行（"cfgfile" 集合）。
    /// </summary>
    [Collection("cfgfile")]
    public class CommittedMemoryFormattingTests : IDisposable
    {
        private const string Key = "MEM.Commit.Load";
        private const uint PageSize = 4096;

        /// <summary>物理内存 32 GiB（用于证明"分母不是物理内存总量"）。</summary>
        private const float RamTotalGB = 32f;

        public CommittedMemoryFormattingTests()
        {
            CommittedMemoryPublisher.Clear();
        }

        public void Dispose() => CommittedMemoryPublisher.Clear();

        private static long GiB(long gib) => gib * 1024L * 1024L * 1024L;

        /// <summary>发布一次"有效"的提交量快照（字节口径）。</summary>
        private static void PublishBytes(long committedBytes, long limitBytes, int generation = 1)
        {
            var raw = new CommittedMemoryRaw(
                pageSize: PageSize,
                commitTotalPages: (ulong)(committedBytes / PageSize),
                commitLimitPages: (ulong)(limitBytes / PageSize));

            CommittedMemorySnapshot snapshot = CommittedMemoryCalculator.Compute(raw, generation, 1000);
            CommittedMemoryPublisher.Publish(generation, snapshot);
        }

        /// <summary>发布一次"缺失"的快照。</summary>
        private static void PublishMissing(CommittedMemoryStatus status, int generation = 1)
        {
            CommittedMemoryPublisher.Publish(
                generation,
                CommittedMemorySnapshot.Missing(status, "模拟缺失", generation, 1000));
        }

        /// <summary>
        /// 按 MetricItem 的真实调用链拼出最终显示文本：
        /// GetValueStr(取值) + GetUnitStr(按上下文) + GetDisplayUnit(按单项格式处理单位)。
        /// </summary>
        private static string ComposeText(
            float value,
            string? userFormat,
            MetricUtils.UnitContext context = MetricUtils.UnitContext.Panel)
        {
            string val = MetricUtils.GetValueStr(Key, value, false, userFormat);
            string unit = MetricUtils.GetUnitStr(Key, value, context);
            return val + MetricUtils.GetDisplayUnit(Key, unit, userFormat);
        }

        /// <summary>设置全局口径后执行断言，无论成败都还原，避免污染同进程里的其它用例。</summary>
        private static void WithGlobalSettings(int memoryDisplayMode, float ramTotalGb, Action body)
        {
            var cfg = Settings.Load();
            int oldMode = cfg.MemoryDisplayMode;
            float oldRam = Settings.DetectedRamTotalGB;
            float oldVram = Settings.DetectedGpuVramTotalGB;

            try
            {
                cfg.MemoryDisplayMode = memoryDisplayMode;
                Settings.DetectedRamTotalGB = ramTotalGb;
                Settings.DetectedGpuVramTotalGB = 0f;
                Settings.DetectedGpuVramTotalGBByKey.Clear();
                body();
            }
            finally
            {
                cfg.MemoryDisplayMode = oldMode;
                Settings.DetectedRamTotalGB = oldRam;
                Settings.DetectedGpuVramTotalGB = oldVram;
                Settings.DetectedGpuVramTotalGBByKey.Clear();
            }
        }

        // =========================================================
        // 1) 分母是提交上限，不是物理内存总量
        // =========================================================

        [Fact]
        public void 提交比例以提交上限为分母_物理内存更小时不会变成150百分比()
        {
            // 物理内存 32 GiB；提交量 48 GiB；提交上限 64 GiB
            PublishBytes(GiB(48), GiB(64));

            WithGlobalSettings(0, RamTotalGB, () =>
            {
                string text = ComposeText(75f, null);

                Assert.Equal("75.0%", text);
                // ★ 用物理内存当分母会算成 48/32 = 150%
                Assert.DoesNotContain("150", text, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void 容量模式显示提交量与当前提交上限()
        {
            PublishBytes(GiB(48), GiB(64));

            WithGlobalSettings(1, RamTotalGB, () =>
            {
                Assert.Equal("48.0 / 64.0GB", ComposeText(75f, null));
                Assert.Equal("GB", MetricUtils.GetUnitStr(Key, 75f, MetricUtils.UnitContext.Panel));
            });
        }

        [Fact]
        public void 提交上限变化时比例与分母同步变化()
        {
            // 第一代：提交 48 GiB / 上限 64 GiB
            PublishBytes(GiB(48), GiB(64), generation: 1);
            WithGlobalSettings(1, RamTotalGB, () =>
                Assert.Equal("48.0 / 64.0GB", ComposeText(75f, null)));

            // 第二代：提交量不变，分页文件变大 → 上限 80 GiB
            PublishBytes(GiB(48), GiB(80), generation: 2);
            WithGlobalSettings(1, RamTotalGB, () =>
                Assert.Equal("48.0 / 80.0GB", ComposeText(60f, null)));

            // 百分比口径同步变化：48/80 = 60%
            WithGlobalSettings(0, RamTotalGB, () =>
                Assert.Equal("60.0%", ComposeText(60f, null)));
        }

        // =========================================================
        // 2) 缺失不是 0
        // =========================================================

        [Fact]
        public void 提交量为零是有效读数_显示零而不是缺失()
        {
            PublishBytes(0L, GiB(64));

            Assert.NotNull(CommittedMemoryPublisher.Current);
            Assert.False(CommittedMemoryPublisher.Current!.IsMissing);

            WithGlobalSettings(1, RamTotalGB, () =>
            {
                string text = ComposeText(0f, null);
                Assert.NotEqual("--", text);
                Assert.Contains("0.00", text, StringComparison.Ordinal);
                Assert.Contains("64.0", text, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void 快照缺失时显示缺失_不显示零也不回退物理内存率()
        {
            PublishMissing(CommittedMemoryStatus.ApiFailed);

            WithGlobalSettings(1, RamTotalGB, () =>
            {
                string text = ComposeText(75f, null);

                Assert.Equal("--", text);
                Assert.DoesNotContain("0", text, StringComparison.Ordinal);
                // 缺失时单位也必须为空，否则会显示成 "--GB"
                Assert.Equal("", MetricUtils.GetUnitStr(Key, 75f, MetricUtils.UnitContext.Panel));
            });
        }

        [Fact]
        public void 提交上限为零时显示缺失()
        {
            // 上限为 0 → 计算层判定为 InvalidLimit（缺失，不是 0%）
            PublishBytes(GiB(48), 0L);

            Assert.True(CommittedMemoryPublisher.Current!.IsMissing);
            Assert.Equal(CommittedMemoryStatus.InvalidLimit, CommittedMemoryPublisher.Current!.Status);

            WithGlobalSettings(1, RamTotalGB, () => Assert.Equal("--", ComposeText(75f, null)));
        }

        [Fact]
        public void 快照尚未就绪时显示缺失()
        {
            CommittedMemoryPublisher.Clear();

            WithGlobalSettings(1, RamTotalGB, () =>
            {
                Assert.Equal("--", ComposeText(75f, null));
                Assert.Equal("--", ComposeText(75f, null, MetricUtils.UnitContext.Taskbar));
            });
        }

        // =========================================================
        // 3) 单项单位：% 覆盖 / 隐藏单位 / 自动单位
        // =========================================================

        [Fact]
        public void 容量模式下单项单位显式写百分比_数值保持百分比()
        {
            PublishBytes(GiB(48), GiB(64));

            WithGlobalSettings(1, RamTotalGB, () =>
                Assert.Equal("75.0%", ComposeText(75f, "%")));
        }

        [Fact]
        public void 容量模式下单项单位为空_隐藏单位但保持容量数值()
        {
            PublishBytes(GiB(48), GiB(64));

            WithGlobalSettings(1, RamTotalGB, () =>
                Assert.Equal("48.0 / 64.0", ComposeText(75f, "")));
        }

        [Fact]
        public void 容量模式下自动单位_数值与单位配对()
        {
            PublishBytes(GiB(48), GiB(64));

            WithGlobalSettings(1, RamTotalGB, () =>
            {
                Assert.Equal("48.0 / 64.0GB", ComposeText(75f, null));
                Assert.Equal("48.0 / 64.0GB", ComposeText(75f, "{u}"));
            });
        }

        [Fact]
        public void 百分比模式单位是百分号()
        {
            PublishBytes(GiB(48), GiB(64));

            WithGlobalSettings(0, RamTotalGB, () =>
            {
                Assert.Equal("75.0%", ComposeText(75f, null));
                Assert.Equal("%", MetricUtils.GetUnitStr(Key, 75f, MetricUtils.UnitContext.Panel));
            });
        }

        [Fact]
        public void 容量模式分子分母同倍率_不会出现一个GB一个MB()
        {
            // 分子刻意取很小的量级（0.5 GiB），分母取 96 GiB：
            // 如果分子分母各自自动换算，就会出现 "512.0 MB / 96.0 GB" 这种倍率不一致。
            PublishBytes((long)(0.5 * GiB(1)), GiB(96));

            WithGlobalSettings(1, RamTotalGB, () =>
            {
                string val = MetricUtils.GetValueStr(Key, 0.52f, false, null);
                string unit = MetricUtils.GetUnitStr(Key, 0.52f, MetricUtils.UnitContext.Panel);

                Assert.Equal("0.50 / 96.0", val);
                Assert.Equal("GB", unit);
                Assert.DoesNotContain("MB", val, StringComparison.Ordinal);
                Assert.DoesNotContain("MB", unit, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void 任务栏上下文与主界面口径一致()
        {
            PublishBytes(GiB(48), GiB(64));

            WithGlobalSettings(1, RamTotalGB, () =>
            {
                Assert.Equal("48.0 / 64.0GB", ComposeText(75f, null, MetricUtils.UnitContext.Taskbar));
                Assert.Equal("48.0 / 64.0GB", ComposeText(75f, null, MetricUtils.UnitContext.Panel));
            });
        }

        [Fact]
        public void 设置界面上下文用自动单位占位符()
        {
            PublishBytes(GiB(48), GiB(64));

            WithGlobalSettings(1, RamTotalGB, () =>
            {
                Assert.Equal("{u}", MetricUtils.GetUnitStr(Key, 0f, MetricUtils.UnitContext.SettingsPanel));
                Assert.Equal("{u}", MetricUtils.GetUnitStr(Key, 0f, MetricUtils.UnitContext.SettingsTaskbar));
            });
        }

        // =========================================================
        // 4) 阳性对照：既有 MEM.Load / GPU.VRAM 行为完全不变
        // =========================================================

        [Fact]
        public void 阳性对照_物理内存容量格式不变()
        {
            WithGlobalSettings(1, RamTotalGB, () =>
            {
                string val = MetricUtils.GetValueStr("MEM.Load", 50f, false, null);
                string unit = MetricUtils.GetUnitStr("MEM.Load", 50f, MetricUtils.UnitContext.Panel);

                Assert.Equal("16.0", val);
                Assert.Equal("GB", unit);
            });
        }

        [Fact]
        public void 阳性对照_物理内存百分比格式不变()
        {
            WithGlobalSettings(0, RamTotalGB, () =>
            {
                string val = MetricUtils.GetValueStr("MEM.Load", 50f, false, null);
                string unit = MetricUtils.GetUnitStr("MEM.Load", 50f, MetricUtils.UnitContext.Panel);

                Assert.Equal("50.0", val);
                Assert.Equal("%", unit);
            });
        }

        [Fact]
        public void 阳性对照_显存容量格式不变()
        {
            var cfg = Settings.Load();
            int oldMode = cfg.MemoryDisplayMode;
            try
            {
                cfg.MemoryDisplayMode = 1;
                Settings.DetectedGpuVramTotalGB = 8f;
                Settings.DetectedGpuVramTotalGBByKey.Clear();

                string val = MetricUtils.GetValueStr("GPU.VRAM", 50f, false, null);
                string unit = MetricUtils.GetUnitStr("GPU.VRAM", 50f, MetricUtils.UnitContext.Panel);

                // 8 GB 显存的 50% = 4 GB
                Assert.Equal("4.00", val);
                Assert.Equal("GB", unit);
            }
            finally
            {
                cfg.MemoryDisplayMode = oldMode;
                Settings.DetectedGpuVramTotalGB = 0f;
                Settings.DetectedGpuVramTotalGBByKey.Clear();
            }
        }

        [Fact]
        public void 阳性对照_提交内存项不会被当成普通MEM项乘物理内存总量()
        {
            // 如果新分支没生效，48 GiB 提交量会按 (75/100)*32 GB 换算成 "24.0" —— 那就错了。
            PublishBytes(GiB(48), GiB(64));

            WithGlobalSettings(1, RamTotalGB, () =>
            {
                string val = MetricUtils.GetValueStr(Key, 75f, false, null);
                Assert.Equal("48.0 / 64.0", val);
                Assert.DoesNotContain("24.0", val, StringComparison.Ordinal);
            });
        }
    }
}
