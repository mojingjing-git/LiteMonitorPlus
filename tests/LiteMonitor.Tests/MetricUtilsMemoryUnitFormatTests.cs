using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    // 本类会调 Settings.Load() 并临时改全局单例的显示口径,
    // 必须与其它读写 settings.json 的用例串行。
    [Collection("cfgfile")]
    public class MetricUtilsMemoryUnitFormatTests
    {
        private const string MemKey = "MEM.Load";

        // 一半占用 + 32GB 内存：容量口径下应显示 16.0GB，百分比口径下应显示 50.0%。
        // 修复前的缺陷正是二者混在一起，显示成 "16.0%"。
        private const float HalfPercent = 50f;
        private const float RamTotalGB = 32f;

        /// <summary>
        /// 按 MetricItem 的真实调用链拼出最终显示文本：
        /// GetValueStr(取值) + GetDisplayUnit(按单项格式处理单位)。
        /// </summary>
        private static string ComposeText(string? userFormat)
        {
            string val = MetricUtils.GetValueStr(MemKey, HalfPercent, false, userFormat);
            string unit = MetricUtils.GetUnitStr(MemKey, HalfPercent, MetricUtils.UnitContext.Panel);
            return val + MetricUtils.GetDisplayUnit(MemKey, unit, userFormat);
        }

        /// <summary>
        /// 设置全局口径后执行断言，无论成败都还原，避免污染同进程里的其它用例。
        /// </summary>
        private static void WithGlobalSettings(int memoryDisplayMode, float ramTotalGb, Action body)
        {
            var cfg = Settings.Load();
            int oldMode = cfg.MemoryDisplayMode;
            float oldRam = Settings.DetectedRamTotalGB;

            try
            {
                cfg.MemoryDisplayMode = memoryDisplayMode;
                Settings.DetectedRamTotalGB = ramTotalGb;
                body();
            }
            finally
            {
                cfg.MemoryDisplayMode = oldMode;
                Settings.DetectedRamTotalGB = oldRam;
            }
        }

        [Fact]
        public void 容量模式下单项单位显式写百分比_数值保持百分比不换算成容量()
        {
            WithGlobalSettings(1, RamTotalGB, () =>
                Assert.Equal("50.0%", ComposeText("%")));
        }

        [Fact]
        public void 容量模式下单项单位未指定_按容量换算()
        {
            WithGlobalSettings(1, RamTotalGB, () =>
                Assert.Equal("16.0GB", ComposeText(null)));
        }

        [Fact]
        public void 容量模式下单项单位含占位符_仍按容量换算并替换单位()
        {
            WithGlobalSettings(1, RamTotalGB, () =>
                Assert.Equal("16.0GB", ComposeText("{u}")));
        }

        [Fact]
        public void 容量模式下单项单位为空_隐藏单位但保持容量数值()
        {
            // 空格式的语义是「隐藏单位」，不是「要百分比」，所以数值仍应走容量换算。
            WithGlobalSettings(1, RamTotalGB, () =>
                Assert.Equal("16.0", ComposeText("")));
        }

        [Fact]
        public void 容量模式与百分比同时出现_含占位符时让位给全局口径()
        {
            // 含 {u} 说明用户要的是「在自动单位后拼接」，此时全局容量口径优先。
            WithGlobalSettings(1, RamTotalGB, () =>
                Assert.Equal("16.0GB%", ComposeText("{u}%")));
        }

        [Fact]
        public void 百分比模式不受单项格式影响()
        {
            WithGlobalSettings(0, RamTotalGB, () =>
            {
                Assert.Equal("50.0%", ComposeText("%"));
                Assert.Equal("50.0%", ComposeText(null));
            });
        }

        [Fact]
        public void 容量未知时数值与单位同时回退百分比()
        {
            // totalGB <= 0 时 GetUnitStr 回退 "%"，GetValueStr 也必须回退百分比数值，
            // 否则会出现「16.0 %」这种数值是容量、单位是百分比的读数。
            WithGlobalSettings(1, 0f, () =>
                Assert.Equal("50.0%", ComposeText(null)));
        }
    }
}
