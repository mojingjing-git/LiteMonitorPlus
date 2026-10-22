using System.Collections.Generic;
using LiteMonitor.src.SystemServices;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// Intel 核显占用率恒为 0 修复的回归测试。
    ///
    /// 本测试工程只有 xunit, 没有 Mock 框架, 所以全部用例只测纯函数
    /// (GpuWarmup.NeedsWarmUp / SensorMatcher.MatchGpu), 不构造 IHardware / ISensor 桩。
    /// </summary>
    public class GpuWarmupTests
    {
        // ---------- GpuWarmup.NeedsWarmUp ----------

        [Fact]
        public void NeedsWarmUp_传感器集合为空时为true()
        {
            // 完全未初始化的极端情况
            Assert.True(GpuWarmup.NeedsWarmUp(new List<SensorType>()));
        }

        [Fact]
        public void NeedsWarmUp_存在Load传感器时为false()
        {
            // 独显 (NVIDIA / AMD / Intel 独显) 构造函数已自调 Update(), 传感器开箱即就绪
            var types = new List<SensorType> { SensorType.Load, SensorType.Temperature, SensorType.Power };
            Assert.False(GpuWarmup.NeedsWarmUp(types));
        }

        [Fact]
        public void NeedsWarmUp_有非Load传感器但无Load时为true()
        {
            // ★ Intel 核显的真实形态: 构造完成后 Sensors 里已经有
            //   Temperature / Clock / Voltage / Power, 唯独没有 Load。
            var types = new List<SensorType>
            {
                SensorType.Temperature, SensorType.Clock, SensorType.Voltage, SensorType.Power
            };
            Assert.True(GpuWarmup.NeedsWarmUp(types));
        }

        // ---------- SensorMatcher.MatchGpu: 核显负载传感器命名 ----------

        [Theory]
        [InlineData("D3D 3D")]        // Intel 核显的负载传感器
        [InlineData("GPU Core")]       // NVIDIA / AMD 的核心负载
        [InlineData("GPU D3D 3D")]     // 独显的 D3D 3D 通道
        public void MatchGpu_负载传感器映射到GpuLoad(string sensorName)
        {
            // 注意: 测试工程 csproj 是 <Nullable>disable</Nullable>,
            //       这里不要用 string? (会产生 CS8632 警告), 直接用 string 接收 null 即可。
            string key = SensorMatcher.MatchGpu(sensorName, SensorType.Load, true);
            Assert.Equal("GPU.Load", key);
        }

        [Fact]
        public void MatchGpu_核显共享显存映射到GpuVramUsed()
        {
            // 核显的 "D3D Shared Memory Used" 是 GPU.VRAM.Used 的来源
            string key = SensorMatcher.MatchGpu("D3D Shared Memory Used", SensorType.SmallData, true);
            Assert.Equal("GPU.VRAM.Used", key);
        }
    }
}
