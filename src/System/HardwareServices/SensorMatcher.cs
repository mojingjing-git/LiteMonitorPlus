using System;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 传感器匹配器 (原 SensorMap.NormalizeKey)
    /// 职责：负责将花里胡哨的原始传感器名称，识别为标准 Key (如 "CPU.Temp")
    /// 通俗易懂：这是"翻译官"，把硬件术语翻译成软件能懂的通用词
    /// </summary>
    public static class SensorMatcher
    {
        // 复用 HardwareRules 的字符串匹配，避免重复造轮子
        private static bool Has(string source, string sub) => HardwareRules.Has(source, sub);

        /// <summary>
        /// 尝试匹配传感器名称到标准 Key
        /// </summary>
        public static string? Match(IHardware hw, ISensor s)
        {
            string name = s.Name;
            var type = hw.HardwareType;

            // --- CPU ---
            if (type == HardwareType.Cpu)
            {
                // 新代码：增加 "package" 支持，防止某些 CPU 把总负载叫 "CPU Package"
                if (s.SensorType == SensorType.Load)
                {
                    if (Has(name, "total") || Has(name, "package")) 
                        return "CPU.Load";
                }
                // [深度优化后的温度匹配逻辑]
                if (s.SensorType == SensorType.Temperature)
                {
                    // 1. 黄金标准：包含这些词的通常就是我们要的
                    if (Has(name, "package") ||  // Intel/AMD 标准
                        Has(name, "average") ||  // LHM 聚合数据
                        Has(name, "tctl") ||     // AMD 风扇控制温度 (最准)
                        Has(name, "tdie") ||     // AMD 核心硅片温度
                        Has(name, "ccd") ||       // AMD 核心板
                        Has(name, "cores"))     // 通用核心温度
                    {
                        return "CPU.Temp";
                    }

                    // 2. 银牌标准：通用名称兜底 (修复 AMD 7840HS 等移动端 CPU)
                    // 必须严格排除干扰项 (如 SOC, VRM, Pump 等)
                    if ((Has(name, "cpu") || Has(name, "core")) && 
                        !Has(name, "soc") &&     // 排除核显/片上系统
                        !Has(name, "vrm") &&     // 排除供电
                        !Has(name, "fan") &&     // 排除风扇(虽类型不同，但防名字干扰)
                        !Has(name, "pump") &&    // 排除水泵
                        !Has(name, "liquid") &&  // 排除水冷液
                        !Has(name, "coolant") && // 排除冷却液
                        !Has(name, "distance"))  // 排除 "Distance to TjMax"
                    {
                        return "CPU.Temp";
                    }
                }
                if (s.SensorType == SensorType.Power && (Has(name, "package") || Has(name, "cores"))) return "CPU.Power";

                // ★★ [#166] CPU 电压**不再**在这里映射成 "CPU.Voltage" ★★
                //   旧实现把 core / cpu / vcore / vid 一律映射到同一个键, 谁先枚举到谁赢
                //   —— 结果取决于 LHM 的传感器枚举顺序。
                //   实机证据 (AMD Ryzen 7 5800X / LHM 0.9.6): 实际选中的是 "Core #1 VID",
                //   那是处理器**请求**的电压, 不是核心供电电压, 口径是错的。
                //   现在电压的**选择**由 CpuVoltageResolver 负责 (按来源可信度 + 稳定标识排序),
                //   候选由 SensorMap 收集, 这里不再产出 "CPU.Voltage" 键 ——
                //   否则"先到先得"的旧路径会以兜底形式复活, 把错误口径重新引回来。
            }

            // --- GPU ---
            if (type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                string? gpuKey = MatchGpu(name, s.SensorType, HardwareRules.ShouldUseSharedMemory(hw));
                if (gpuKey != null) return gpuKey;
            }

            // --- Memory ---
            if (type == HardwareType.Memory) 
            {
                // [Critical Fix] 严格排除虚拟内存 (Virtual Memory)
                if (Has(hw.Name, "virtual")) return null;
                
                // 1. 负载
                // ★★★ 增强版匹配：支持名称仅为 "Memory" 的负载传感器 (常见于 LibreHardwareMonitor) ★★★
                if (s.SensorType == SensorType.Load)
                {
                     if (Has(name, "memory")) return "MEM.Load";
                     // 兼容只有 "Load" 字样的传感器
                     if (name.Equals("Load", StringComparison.OrdinalIgnoreCase)) return "MEM.Load";
                }
                
                // 2. ★ 增强版匹配：同时接受 Data 和 SmallData
                if (s.SensorType == SensorType.Data || s.SensorType == SensorType.SmallData)
                {
                    if (Has(name, "used")) return "MEM.Used";
                    // [Fix] 恢复对 Free 的支持，防止部分系统 Available 传感器缺失或命名为 Free
                    if (Has(name, "available") || Has(name, "free")) return "MEM.Available";
                }
            }

            // --- Battery ---
            if (type == HardwareType.Battery)
            {
                if (s.SensorType == SensorType.Level)
                {
                    // Fix: 过滤掉电池损耗/健康度数据 (通常也标记为 Level 类型)
                    if (Has(name, "Degradation") || Has(name, "Wear")) return null;
                    
                    // ★★★ 优先选择包含 "Charge" 的传感器 ★★★
                    if (Has(name, "Charge")) return "BAT.Percent";
                    
                    // 其他作为备选 (Weak)
                    return "BAT.Percent";
                }
                if (s.SensorType == SensorType.Power)
                {
                    // 无论当前是充电还是放电，只要是 Power 类型的电池传感器，都进行映射。
                    return "BAT.Power";
                }
                if (s.SensorType == SensorType.Voltage) return "BAT.Voltage";
                if (s.SensorType == SensorType.Current) return "BAT.Current";
            }

            return null;
        }

        /// <summary>
        /// [#166] CPU 电压候选的硬件层级翻译。
        /// LHM 的 SuperIO 是 Motherboard 的子硬件, 因此主板轨会走 SuperIO / Motherboard 两条分支。
        /// </summary>
        public static CpuVoltageHardwareLevel GetCpuVoltageLevel(HardwareType type)
        {
            switch (type)
            {
                case HardwareType.Cpu: return CpuVoltageHardwareLevel.Cpu;
                case HardwareType.SuperIO: return CpuVoltageHardwareLevel.SuperIO;
                case HardwareType.Motherboard: return CpuVoltageHardwareLevel.Motherboard;
                default: return CpuVoltageHardwareLevel.Other;
            }
        }

        /// <summary>
        /// 该传感器是否**可能**是 CPU 电压候选。
        ///
        /// ★ 这里只按**类型**过滤 (电压 + CPU/主板/SuperIO 层级);
        ///   名字语义 (VID / SVI / Vcore / SoC ...) 一律交给
        ///   <see cref="CpuVoltageResolver.Classify"/> 判定, 避免两处各写一套规则。
        /// </summary>
        public static bool IsCpuVoltageCandidate(HardwareType type, SensorType sensorType)
        {
            if (sensorType != SensorType.Voltage) return false;
            return GetCpuVoltageLevel(type) != CpuVoltageHardwareLevel.Other;
        }

        /// <summary>
        /// GPU 传感器匹配 (抽成独立方法, 便于单元测试, 不需要构造 IHardware/ISensor 桩)。
        ///
        /// ★★★ [显存修复] D3D 通道单独建 Key ★★★
        /// D3D 通道按适配器 (adapter) 统计, 每张卡各读各的, 不会串卡;
        /// 厂商通道 (NVAPI / ADL) 在 LHM 0.9.6 的 NVIDIA 实现上会被"全局化", 多卡时多张卡拿到同一份数据。
        /// 所以这里把 D3D 专用显存拆到独立 Key, 由取值层 (GpuVramSource) 按规则择一,
        /// 而不是像以前那样让厂商值把 D3D 值顶掉 (SensorMap 的冲突规则是"厂商优先")。
        /// </summary>
        public static string? MatchGpu(string name, SensorType sensorType, bool preferShared)
        {
            if (sensorType == SensorType.Load && (Has(name, "core") || Has(name, "d3d 3d"))) return "GPU.Load";
            if (sensorType == SensorType.Temperature && (Has(name, "core") || Has(name, "hot spot") || Has(name, "soc") || Has(name, "vr"))) return "GPU.Temp";

            // ★ [显存修复] D3D 专用显存 -> 独立 Key
            //   注意: 这里只拦 "D3D Dedicated Memory *"。核显的 "D3D Shared Memory *"
            //   仍然要走下面的通用分支 (它就是核显 GPU.VRAM.Used 的来源), 不能一起拦掉,
            //   否则核显的显存映射会整个消失。
            bool isD3DDedicated = Has(name, "d3d") && Has(name, "dedicated");
            if (sensorType == SensorType.SmallData && isD3DDedicated)
            {
                if (Has(name, "used")) return "GPU.VRAM.D3D.Used";
                if (Has(name, "total")) return "GPU.VRAM.D3D.Total";
                if (Has(name, "free")) return "GPU.VRAM.D3D.Free";
            }

            // VRAM Logic (简化且准确)
            // 1. 根据硬件规则判断是否应该优先找共享内存 (核显)
            if (sensorType == SensorType.SmallData
                && (Has(name, "memory") || Has(name, "dedicated") || Has(name, "shared")))
            {
                bool isShared = Has(name, "shared");
                bool isDedicated = Has(name, "dedicated");
                // Generic "Memory Used" (Vendor sensors often just say "Memory Used")
                bool isGenericMem = !isShared && !isDedicated && Has(name, "memory");

                if (preferShared)
                {
                    // Integrated: Only accept Shared Memory
                    // ★ 核显的 "D3D Shared Memory Used" 就是从这里进 GPU.VRAM.Used 的
                    if (isShared && Has(name, "used")) return "GPU.VRAM.Used";
                    if (isShared && Has(name, "total")) return "GPU.VRAM.Total";

                    // Fallback: 如果没有 Shared Memory (比如某些老驱动)，但有 Generic Memory，勉强用一下
                    // 但必须排除 Dedicated (核显通常没有专用显存，如果有也是假的或者极小)
                    if (isGenericMem && Has(name, "used")) return "GPU.VRAM.Used";
                }
                else
                {
                    // Discrete: Accept Dedicated or Generic Memory (Block Shared)
                    // ★ D3D 专用的已经在上面单独建 Key 了, 这里跳过, 避免被当成厂商口径
                    if (!isShared && !isD3DDedicated)
                    {
                        if (Has(name, "used")) return "GPU.VRAM.Used";
                        if (Has(name, "total")) return "GPU.VRAM.Total";
                    }
                }
            }

            // 显存占用率: 只认 "GPU Memory", 不要 "GPU Memory Controller" (那是显存控制器利用率, 不是占用率)
            if (sensorType == SensorType.Load && Has(name, "memory") && !Has(name, "controller"))
                return "GPU.VRAM.Load";

            return null;
        }
    }
}
