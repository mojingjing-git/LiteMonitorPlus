using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    public class HardwareRulesGpuNameTests
    {
        [Theory]
        [InlineData("NVIDIA GeForce RTX 3080 Ti", "3080 Ti")]
        [InlineData("NVIDIA GeForce RTX 3080", "3080")]
        [InlineData("AMD Radeon RX 7900 XTX", "7900 XTX")]
        [InlineData("AMD Radeon(TM) Graphics", "Graphics")]
        [InlineData("AMD Radeon(TM) RX Vega 8", "Vega 8")]
        [InlineData("AMD Radeon RX 6800 XT", "6800 XT")]
        [InlineData("Intel Arc B580", "B580")]
        [InlineData("Intel(R) UHD Graphics 770", "UHD Graphics 770")]
        [InlineData("Intel Arc Graphics", "Graphics")]
        public void GetShortGpuName_剥离厂商与系列前缀(string full, string expected)

        {
            Assert.Equal(expected, HardwareRules.GetShortGpuName(full));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void GetShortGpuName_空输入返回空串(string input)
        {
            Assert.Equal("", HardwareRules.GetShortGpuName(input));
        }
    }
}