using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 显存串卡修复的回归测试。
    ///
    /// 本测试工程只有 xunit, 没有 Mock 框架, 所以全部用例都只测纯函数
    /// (SensorMatcher.MatchGpu / GpuVramSource / HardwareRules.UsesDedicatedVramName),
    /// 不构造 IHardware / ISensor 桩。
    /// </summary>
    public class GpuVramTests
    {
        // 模拟注册表里的适配器容量集合: 3080 Ti = 12288MB, 3080 = 20480MB
        private static readonly float[] RealAdapterTotals = { 12288f, 20480f };

        // ---------- GpuVramCapacity.NormalizeName ----------

        [Theory]
        [InlineData("NVIDIA GeForce RTX 3080", "NVIDIA GEFORCE RTX 3080")]
        [InlineData("NVIDIA GeForce RTX 3080 ", "NVIDIA GEFORCE RTX 3080")]
        [InlineData("Intel(R) Arc(TM) A770 Graphics", "INTEL ARC A770 GRAPHICS")]
        [InlineData("  AMD   Radeon RX 7900 XTX ", "AMD RADEON RX 7900 XTX")]
        public void NormalizeName_归一化后去掉商标并压缩空白(string input, string expected)
        {
            Assert.Equal(expected, GpuVramCapacity.NormalizeName(input));
        }

        [Fact]
        public void NormalizeName_空输入返回空串()
        {
            Assert.Equal("", GpuVramCapacity.NormalizeName(null));
            Assert.Equal("", GpuVramCapacity.NormalizeName("   "));
        }

        [Fact]
        public void GetTotalMB_不存在的卡返回null()
        {
            Assert.Null(GpuVramCapacity.GetTotalMB("根本不存在的显卡型号 XXXX"));
        }

        [Fact]
        public void GetTotalMB_空输入返回null()
        {
            Assert.Null(GpuVramCapacity.GetTotalMB(""));
            Assert.Null(GpuVramCapacity.GetTotalMB(null));
        }

        [Theory]
        [InlineData("NVIDIA GeForce RTX 3080", 20480f)]
        [InlineData("NVIDIA GeForce RTX 3080 Ti", 12288f)]
        public void GetTotalMB_本机实测的独显容量(string name, float expectedMB)
        {
            float? actual = GpuVramCapacity.GetTotalMB(name);
            if (!actual.HasValue) return;   // 别的机器上没有这张卡, 跳过
            Assert.Equal(expectedMB, actual.Value, 1);
        }

        [Fact]
        public void GetAllTotalsMB_读到的容量都是合理量级()
        {
            var all = GpuVramCapacity.GetAllTotalsMB();
            Assert.NotNull(all);
            if (all.Count == 0) return;     // 受限环境读不到, 跳过
            Assert.Contains(all, v => v >= 512f);
        }

        // ---------- GpuVramSource.SameCapacity ----------

        [Theory]
        [InlineData(12288f, 12288f, true)]
        [InlineData(20480f, 20480f, true)]
        [InlineData(20470f, 20480f, true)]   // 10MB 差, 在 1% 相对容差(204MB)内
        [InlineData(20250f, 20480f, false)]  // 230MB 差, 超出容差
        [InlineData(12288f, 20480f, false)]
        public void SameCapacity_容差内算同一个(float a, float b, bool expected)
        {
            Assert.Equal(expected, GpuVramSource.SameCapacity(a, b));
        }

        // ---------- GpuVramSource.IsVendorChannelForeign ----------

        [Fact]
        public void IsVendorChannelForeign_厂商报的容量命中另一块卡就是串卡()
        {
            // 3080 这张卡: 厂商报 12288 (其实是 Ti 的容量), 注册表真值 20480
            Assert.True(GpuVramSource.IsVendorChannelForeign(12288f, 20480f, RealAdapterTotals));
        }

        [Fact]
        public void IsVendorChannelForeign_本卡对得上就判为可信()
        {
            Assert.False(GpuVramSource.IsVendorChannelForeign(12288f, 12288f, RealAdapterTotals));
            Assert.False(GpuVramSource.IsVendorChannelForeign(20480f, 20480f, RealAdapterTotals));
        }

        [Fact]
        public void IsVendorChannelForeign_对不上但命中不了别的卡就不判定()
        {
            // 驱动可见容量 != 物理容量时 (实测 Arc A770: 16256 vs 16384) 会走到这里,
            // 此时厂商报的 16384 命中不了系统里任何另一块适配器 -> 不能判定成串卡
            Assert.False(GpuVramSource.IsVendorChannelForeign(16384f, 16256f, RealAdapterTotals));
        }

        [Fact]
        public void IsVendorChannelForeign_缺证据时判为可信()
        {
            Assert.False(GpuVramSource.IsVendorChannelForeign(null, 20480f, RealAdapterTotals));
            Assert.False(GpuVramSource.IsVendorChannelForeign(12288f, null, RealAdapterTotals));
            Assert.False(GpuVramSource.IsVendorChannelForeign(null, null, RealAdapterTotals));
            Assert.False(GpuVramSource.IsVendorChannelForeign(0f, 20480f, RealAdapterTotals));
            Assert.False(GpuVramSource.IsVendorChannelForeign(12288f, 20480f, null));
        }

        [Fact]
        public void IsVendorChannelForeign_真实容量低于下限不判定()
        {
            // 核显的 UMA 窗口 (几百 MB) 不能拿来当基准
            Assert.False(GpuVramSource.IsVendorChannelForeign(8192f, 512f, RealAdapterTotals));
        }

        // ---------- GpuVramSource.Select ----------

        [Fact]
        public void Select_厂商串卡时改用D3D通道和真实容量()
        {
            var (used, total) = GpuVramSource.Select(
                vendorUsedMB: 6495f, vendorTotalMB: 12288f,
                d3dUsedMB: 4.1f, d3dTotalMB: null,
                realTotalMB: 20480f,
                useD3D: true);

            Assert.Equal(4.1f, used.Value, 3);      // 用 D3D 的 used
            Assert.Equal(20480f, total.Value, 3);    // 用注册表真值
        }

        [Fact]
        public void Select_厂商串卡但D3D缺失时不混算()
        {
            // 绝不能拿"别的卡的 used"(6495) 去除"本卡的 total"(20480) -> 31.7% 这种又错又怪的值
            var (used, total) = GpuVramSource.Select(
                vendorUsedMB: 6495f, vendorTotalMB: 12288f,
                d3dUsedMB: null, d3dTotalMB: null,
                realTotalMB: 20480f,
                useD3D: true);

            Assert.False(used.HasValue);
            Assert.Equal(20480f, total.Value, 3);
        }

        [Fact]
        public void Select_厂商可信时维持原口径()
        {
            var (used, total) = GpuVramSource.Select(
                vendorUsedMB: 6495f, vendorTotalMB: 12288f,
                d3dUsedMB: 6305f, d3dTotalMB: 12288f,
                realTotalMB: 12288f,
                useD3D: false);

            Assert.Equal(6495f, used.Value, 3);      // 仍用厂商值 (保留 CUDA 可见性)
            Assert.Equal(12288f, total.Value, 3);
        }

        [Fact]
        public void Select_厂商缺值时用D3D兜底()
        {
            var (used, total) = GpuVramSource.Select(
                vendorUsedMB: null, vendorTotalMB: null,
                d3dUsedMB: 4.1f, d3dTotalMB: 12288f,
                realTotalMB: 12288f,
                useD3D: false);

            Assert.Equal(4.1f, used.Value, 3);
            Assert.Equal(12288f, total.Value, 3);
        }

        // ---------- HardwareRules.UsesDedicatedVramName ----------

        [Theory]
        [InlineData("NVIDIA GeForce RTX 3080", true)]
        [InlineData("NVIDIA GeForce RTX 3080 Ti", true)]
        [InlineData("AMD Radeon RX 7900 XTX", true)]
        [InlineData("Intel(R) Arc(TM) A770 Graphics", true)]
        [InlineData("Intel(R) UHD Graphics 770", false)]     // 核显
        [InlineData("AMD Radeon(TM) Graphics", false)]        // 核显
        [InlineData("", false)]
        [InlineData(null, false)]
        public void UsesDedicatedVramName_只有独显才参与串卡判定(string name, bool expected)
        {
            Assert.Equal(expected, HardwareRules.UsesDedicatedVramName(name));
        }

        // ---------- SensorMatcher.MatchGpu ----------

        [Theory]
        [InlineData("D3D Dedicated Memory Used", "GPU.VRAM.D3D.Used")]
        [InlineData("D3D Dedicated Memory Total", "GPU.VRAM.D3D.Total")]
        [InlineData("D3D Dedicated Memory Free", "GPU.VRAM.D3D.Free")]
        public void MatchGpu_D3D专用显存走独立Key(string sensorName, string expected)
        {
            Assert.Equal(expected,
                SensorMatcher.MatchGpu(sensorName, LibreHardwareMonitor.Hardware.SensorType.SmallData, false));
        }

        [Theory]
        [InlineData("GPU Memory Used", "GPU.VRAM.Used")]
        [InlineData("GPU Memory Total", "GPU.VRAM.Total")]
        public void MatchGpu_厂商显存Key不变(string sensorName, string expected)
        {
            Assert.Equal(expected,
                SensorMatcher.MatchGpu(sensorName, LibreHardwareMonitor.Hardware.SensorType.SmallData, false));
        }

        [Fact]
        public void MatchGpu_核显仍走共享显存()
        {
            // ★ 这条最关键: 核显的显存**只有** "D3D Shared Memory *" 一个来源,
            //   一旦被通用分支的 D3D 排除条件误伤, 核显的显存映射会整个消失。
            Assert.Equal("GPU.VRAM.Used",
                SensorMatcher.MatchGpu("D3D Shared Memory Used", LibreHardwareMonitor.Hardware.SensorType.SmallData, true));
            Assert.Equal("GPU.VRAM.Total",
                SensorMatcher.MatchGpu("D3D Shared Memory Total", LibreHardwareMonitor.Hardware.SensorType.SmallData, true));
        }

        [Fact]
        public void MatchGpu_非显存指标不变()
        {
            Assert.Equal("GPU.Load",
                SensorMatcher.MatchGpu("D3D 3D", LibreHardwareMonitor.Hardware.SensorType.Load, false));
            Assert.Equal("GPU.Temp",
                SensorMatcher.MatchGpu("GPU Core", LibreHardwareMonitor.Hardware.SensorType.Temperature, false));
        }

        [Fact]
        public void MatchGpu_显存占用率不再误用显存控制器利用率()
        {
            Assert.Equal("GPU.VRAM.Load",
                SensorMatcher.MatchGpu("GPU Memory", LibreHardwareMonitor.Hardware.SensorType.Load, false));
            Assert.Null(
                SensorMatcher.MatchGpu("GPU Memory Controller", LibreHardwareMonitor.Hardware.SensorType.Load, false));
        }
    }
}
