using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    // 本类会调 Settings.Load() (读写 settings.json), 必须与其它读同一文件的用例串行。
    [Collection("cfgfile")]
    public class MetricUtilsGpuKeyTests
    {
        [Theory]
        [InlineData("GPU.0.Temp", MetricType.Temperature)]
        [InlineData("GPU.1.Temp", MetricType.Temperature)]
        [InlineData("GPU.1.VRAM", MetricType.Memory)]
        [InlineData("GPU.1.Load", MetricType.Percent)]
        [InlineData("GPU.1.Power", MetricType.Power)]
        [InlineData("GPU.1.Clock", MetricType.Frequency)]
        [InlineData("GPU.1.Fan", MetricType.RPM)]
        public void GetType_带序号的Key_类型判定不变(string key, MetricType expected)
        {
            Assert.Equal(expected, MetricUtils.GetType(key));
        }

        [Fact]
        public void GetAdaptivePercentage_带序号的Key_使用该卡自己的峰值()
        {
            var cfg = Settings.Load();

            // Settings.Load() 返回的是单例, 这里改的是全局状态, 用完必须还原,
            // 否则会污染同一测试进程里的其它用例。
            float oldGlobalPower = cfg.RecordedMaxGpuPower;
            bool hadCardPeak = cfg.RecordedMaxGpuDynamic.TryGetValue("GPU.1.Power", out float oldCardPeak);
            bool hadOtherPeak = cfg.RecordedMaxGpuDynamic.TryGetValue("GPU.2.Power", out float oldOtherPeak);

            try
            {
                cfg.RecordedMaxGpuDynamic["GPU.1.Power"] = 200f;

                double pct = MetricUtils.GetAdaptivePercentage("GPU.1.Power", 100.0);
                Assert.Equal(0.5, pct, 3);

                // 另一张卡没有单独峰值时, 走全局 RecordedMaxGpuPower
                cfg.RecordedMaxGpuDynamic.Remove("GPU.2.Power");
                cfg.RecordedMaxGpuPower = 400f;
                double pct2 = MetricUtils.GetAdaptivePercentage("GPU.2.Power", 100.0);
                Assert.Equal(0.25, pct2, 3);
            }
            finally
            {
                cfg.RecordedMaxGpuPower = oldGlobalPower;
                if (hadCardPeak) cfg.RecordedMaxGpuDynamic["GPU.1.Power"] = oldCardPeak;
                else cfg.RecordedMaxGpuDynamic.Remove("GPU.1.Power");
                if (hadOtherPeak) cfg.RecordedMaxGpuDynamic["GPU.2.Power"] = oldOtherPeak;
            }
        }
    }
}