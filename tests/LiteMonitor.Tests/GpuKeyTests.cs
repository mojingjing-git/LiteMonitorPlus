using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    public class GpuKeyTests
    {
        [Theory]
        [InlineData("GPU.0.Temp", 0, "Temp")]
        [InlineData("GPU.1.Temp", 1, "Temp")]
        [InlineData("GPU.10.VRAM", 10, "VRAM")]
        [InlineData("gpu.3.power", 3, "power")]
        public void TryParse_带序号的Key_解析出序号与指标(string key, int idx, string metric)
        {
            bool ok = GpuKey.TryParse(key, out int index, out string m);
            Assert.True(ok);
            Assert.Equal(idx, index);
            Assert.Equal(metric, m);
        }

        [Theory]
        [InlineData("GPU.Temp")]
        [InlineData("GPU.VRAM")]
        [InlineData("CPU.Temp")]
        [InlineData("GPU.")]
        [InlineData("GPU.-1.Temp")]
        [InlineData("GPU.x.Temp")]
        [InlineData("")]
        [InlineData(null)]
        public void TryParse_非带序号或非法Key_返回false(string key)
        {
            Assert.False(GpuKey.TryParse(key, out _, out _));
        }

        [Fact]
        public void Build_拼接出带序号Key()
        {
            Assert.Equal("GPU.2.Power", GpuKey.Build(2, "Power"));
        }

        [Fact]
        public void BaseKey_还原基础Key()
        {
            Assert.Equal("GPU.Temp", GpuKey.BaseKey("Temp"));
        }

        [Theory]
        [InlineData("GPU.1.Temp", "GPU.Temp")]
        [InlineData("GPU.Temp", "GPU.Temp")]
        [InlineData("CPU.Power", "CPU.Power")]
        public void ToBaseKey_归一化到基础Key(string input, string expected)
        {
            Assert.Equal(expected, GpuKey.ToBaseKey(input));
        }
    }
}