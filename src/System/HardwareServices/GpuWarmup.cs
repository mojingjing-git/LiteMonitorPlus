using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 显卡传感器预热辅助。
    ///
    /// 背景: LibreHardwareMonitor 对 Intel 核显的负载传感器是懒激活的。
    ///   IntelIntegratedGpu 的构造函数创建了 _nodeUsage[] (SensorType.Load,
    ///   名称形如 "D3D 3D") 与 _sharedMemoryUsage 等传感器实例, 但只在 Update() 里
    ///   调用 ActivateSensor 才把它们注册进 Sensors 集合 —— 构造完成后 Sensors 里
    ///   只有 Temperature / Clock / Voltage / Power, 没有 Load。
    ///   而 NvidiaGpu / AmdGpu / IntelDiscreteGpu 独显的构造函数末尾会自调 Update(),
    ///   传感器开箱即就绪, 不受影响。
    ///
    /// 因此若在 SensorMap 建表之前不补一次 Update(), 核显的负载传感器
    /// 根本不会出现在 Sensors 里, 导致 "GPU.Load" / "GPU.0.Load" 键缺失,
    /// 表现为占用率恒为 0 或空白。
    ///
    /// 注意: AMD APU 不属于此列 —— AmdGpu 构造函数同样自调 Update(), 预热对它无效。
    /// </summary>
    public static class GpuWarmup
    {
        /// <summary>
        /// 该显卡是否需要补一次 Update()。
        ///
        /// 判定依据: 硬件是否已暴露任意 Load 类型传感器。
        ///   - 独显 (NVIDIA / AMD / Intel 独显): 构造函数已自调 Update(), 有 Load 传感器 -> false (不重复 Update)
        ///   - Intel 核显: 构造后有 Temp/Clock/Voltage/Power, 但**没有 Load** -> true (需要预热)
        ///
        /// 说明: 即使某块卡预热后依然没有 Load 传感器 (硬件本身不提供, 如虚拟显示适配器),
        /// 最多多付一次 Update(), 发生在启动/重载这类低频时机, 可接受。
        /// </summary>
        public static bool NeedsWarmUp(IEnumerable<SensorType> sensorTypes)
        {
            foreach (var t in sensorTypes)
            {
                if (t == SensorType.Load) return false;
            }
            return true;
        }

        /// <summary>
        /// 对所有"传感器尚未上线"的显卡补一次 Update()。
        /// 纯 GPU 操作, 不触碰主板/电池/磁盘, 避免恢复启动时的全硬件 Update 卡顿。
        /// </summary>
        public static void WarmUpIfNeeded(Computer computer, System.Action<IHardware> updater)
        {
            if (computer == null || updater == null) return;
            foreach (var hw in computer.Hardware)
            {
                if (hw == null) continue;
                if (!HardwareScanner.IsGpuHardware(hw)) continue;

                if (!NeedsWarmUp(GetSensorTypes(hw))) continue;

                System.Diagnostics.Debug.WriteLine(
                    $"[GpuWarmup] 显卡传感器未上线, 补一次 Update: {hw.Name} ({hw.HardwareType})");
                try { updater(hw); }
                catch { /* 预热失败不能阻断启动; 后续 UpdateAll 仍会再试 */ }
            }
        }

        /// <summary>
        /// 取出该硬件的传感器类型序列。
        /// 注意: LibreHardwareMonitor 的 IHardware.Sensors 实际类型是 ISensor[] (数组),
        ///      因此用 .Length 而不是 .Count。
        /// </summary>
        private static IEnumerable<SensorType> GetSensorTypes(IHardware hw)
        {
            var sensors = hw.Sensors;
            if (sensors == null) return new List<SensorType>();
            var list = new List<SensorType>(sensors.Length);
            for (int i = 0; i < sensors.Length; i++)
                list.Add(sensors[i].SensorType);
            return list;
        }
    }
}
