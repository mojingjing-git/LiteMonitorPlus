using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 硬件扫描器：负责硬件的发现、列举和命名逻辑 (主要服务于 UI 设置)
    /// </summary>
    public static class HardwareScanner
    {
        private static List<string>? _cachedFanList = null;
        private static List<string>? _cachedNetworkList = null;
        private static List<string>? _cachedDiskList = null;
        private static List<GpuOption>? _cachedGpuOptions = null;
        private static List<DeviceOption>? _cachedDiskOptions = null;
        private static List<string>? _cachedMoboTempList = null;

        /// <summary>
        /// 身份解析器（缓存解析结果，避免每次枚举都反射一遍）。
        /// ★ 无状态，可安全共享。
        /// </summary>
        private static readonly DeviceIdentityResolver IdentityResolver = new DeviceIdentityResolver();

        public sealed class GpuOption
        {
            public string Label { get; set; } = "";
            public string Value { get; set; } = "";
            public string Name { get; set; } = "";

            // ★ [F07] 身份相关字段：Value 现在优先是**稳定设备 ID**（旧版是 LHM Identifier）。
            public string StableId { get; set; } = "";
            public string RuntimeId { get; set; } = "";
            public IdentityStability Stability { get; set; } = IdentityStability.Unknown;
        }

        /// <summary>
        /// 清除所有扫描缓存
        /// </summary>
        public static void ClearCache()
        {
            _cachedFanList = null;
            _cachedNetworkList = null;
            _cachedDiskList = null;
            _cachedGpuOptions = null;
            _cachedDiskOptions = null;
            _cachedMoboTempList = null;
        }

        /// <summary>
        /// 只读缓存里已检测到的显卡数量, 不碰硬件、不加锁。
        /// 供 UI 线程在拿不到硬件锁时快速拿到"上次检测到的卡数"。
        /// </summary>
        public static int CachedGpuCount => _cachedGpuOptions?.Count ?? 0;

        /// <summary>
        /// 智能命名：将传感器与其所属硬件结合命名，并处理 SuperIO 芯片名的替换
        /// </summary>
        public static string GenerateSmartName(ISensor sensor, IHardware hardware, IComputer computer)
        {
            return GenerateSmartNameCore(sensor, hardware, computer);
        }

        /// <summary>
        /// 智能命名（computer 为 null 时 SuperIO 用自己的名字 —— 可注入的硬件集合版本）。
        /// </summary>
        private static string GenerateSmartNameCore(ISensor sensor, IHardware hardware, IComputer? computer)
        {
            string hwName = hardware.Name;
            // 如果是 SuperIO，尝试替换为主板名
            if (hardware.HardwareType == HardwareType.SuperIO && computer != null)
            {
                var mobo = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
                if (mobo != null) hwName = mobo.Name;
            }
            return $"{sensor.Name} [{hwName}]";
        }

        /// <summary>
        /// 列出所有网卡名称
        /// </summary>
        public static List<string> ListAllNetworks(IComputer computer)
        {
            if (_cachedNetworkList != null && _cachedNetworkList.Count > 0)
                return _cachedNetworkList.ToList();

            var list = computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Network)
                .Select(h => h.Name).Distinct().ToList();

            if (list.Count > 0) _cachedNetworkList = list;
            return list.ToList();
        }

        /// <summary>
        /// 列出所有硬盘名称
        /// <para>★ [F07] 不再按显示名去重：同型号的两块盘会**分别**出现在列表里（#538 根因）。
        /// 标签沿用既有范式加 "#序号" 后缀。</para>
        /// </summary>
        public static List<string> ListAllDisks(IComputer computer)
        {
            return ListAllDiskOptions(computer).Select(x => x.Label).ToList();
        }

        /// <summary>
        /// 列出所有硬盘的 **Label + Value** 选择项。
        /// <para>★ [F07] Value 是设备的**稳定 ID**（无法证明时退回运行时 ID），
        /// 不再是显示名 ⇒ 同型号多盘可以分别选择。</para>
        /// <para>★ 枚举**只读属性**，不调用 Update() ⇒ 不会顺带做 SMART 探测（#466/#601 止血口径）。</para>
        /// </summary>
        public static List<DeviceOption> ListAllDiskOptions(IComputer computer)
        {
            return ListAllDiskOptions(computer.Hardware);
        }

        /// <summary>
        /// 列出所有硬盘的 **Label + Value** 选择项（可注入的硬件集合版本 —— 测试接缝）。
        /// </summary>
        public static List<DeviceOption> ListAllDiskOptions(IEnumerable<IHardware> hardware)
        {
            if (_cachedDiskOptions != null && _cachedDiskOptions.Count > 0)
                return CloneDeviceOptions(_cachedDiskOptions);

            var disks = hardware
                .Where(h => h.HardwareType == HardwareType.Storage)
                .ToList();

            var catalog = DeviceCatalog.Build(disks, DeviceKind.Storage, IdentityResolver);
            var options = catalog.ToOptions(DeviceKind.Storage).ToList();

            if (options.Count > 0) _cachedDiskOptions = CloneDeviceOptions(options);
            return CloneDeviceOptions(options);
        }

        /// <summary>
        /// 列出所有显卡名称，供旧调用点使用。
        /// </summary>
        public static List<string> ListAllGpus(IComputer computer)
        {
            return ListAllGpuOptions(computer).Select(x => x.Label).ToList();
        }

        /// <summary>
        /// 列出所有显卡选项。
        /// <para>★ [F07] Value 优先用**稳定设备 ID**（显卡的 DeviceId = PnP 实例路径）；
        /// 无法证明时退回运行时标识并标记为不稳定（同名标签照旧加 "#序号" 后缀）。</para>
        /// </summary>
        public static List<GpuOption> ListAllGpuOptions(IComputer computer)
        {
            return ListAllGpuOptions(computer.Hardware);
        }

        /// <summary>
        /// 列出所有显卡选项（可注入的硬件集合版本 —— 测试接缝）。
        /// </summary>
        public static List<GpuOption> ListAllGpuOptions(IEnumerable<IHardware> hardware)
        {
            if (_cachedGpuOptions != null && _cachedGpuOptions.Count > 0)
                return CloneGpuOptions(_cachedGpuOptions);

            var gpus = hardware.Where(IsGpuHardware).ToList();
            var catalog = DeviceCatalog.Build(gpus, DeviceKind.Gpu, IdentityResolver);
            var options = catalog.ToOptions(DeviceKind.Gpu);

            var list = options.Select(o => new GpuOption
            {
                Label = o.Label,
                Value = o.Value,
                Name = o.Name,
                StableId = o.StableId,
                RuntimeId = o.RuntimeId,
                Stability = o.Stability,
            }).ToList();

            if (list.Count > 0) _cachedGpuOptions = CloneGpuOptions(list);
            return CloneGpuOptions(list);
        }

        /// <summary>
        /// 按配置里保存的取值定位显卡。
        /// <para>★ [F07] 口径：稳定 ID → 运行时标识 → 显示名（**仅唯一命中**）。
        /// 同名多卡时返回 null（交给自动模式 / 让用户明确选择），绝不随机挑第一块。</para>
        /// </summary>
        public static IHardware? FindGpuByConfig(IComputer computer, string configValue)
        {
            return FindGpuByConfig(computer.Hardware, configValue);
        }

        /// <summary>
        /// 按配置里保存的取值定位显卡（可注入的硬件集合版本 —— 测试接缝）。
        /// </summary>
        public static IHardware? FindGpuByConfig(IEnumerable<IHardware> hardware, string configValue)
        {
            if (string.IsNullOrWhiteSpace(configValue)) return null;

            var gpus = hardware.Where(IsGpuHardware).ToList();
            var pairs = gpus
                .Select(hw => (Hardware: hw, Identity: IdentityResolver.Resolve(hw, DeviceKind.Gpu)))
                .ToList();

            var byStable = pairs.Where(p =>
                !string.IsNullOrEmpty(p.Identity.StableId) &&
                string.Equals(p.Identity.StableId, configValue, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byStable.Count == 1) return byStable[0].Hardware;

            var byRuntime = pairs.Where(p =>
                string.Equals(p.Identity.RuntimeId, configValue, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byRuntime.Count == 1) return byRuntime[0].Hardware;

            var byName = pairs.Where(p =>
                string.Equals(p.Identity.DisplayName, configValue, StringComparison.OrdinalIgnoreCase)).ToList();
            return byName.Count == 1 ? byName[0].Hardware : null;
        }

        public static bool IsGpuHardware(IHardware hw)
        {
            return hw.HardwareType == HardwareType.GpuNvidia ||
                   hw.HardwareType == HardwareType.GpuAmd ||
                   hw.HardwareType == HardwareType.GpuIntel;
        }

        /// <summary>
        /// 取硬件的 LHM 原始标识（**仅作运行时标识**，不是身份）。
        /// <para>★ LHM 0.9.6 实测：显卡是 "/gpu-nvidia/{枚举序号}"、存储是 "/nvme"（媒体类型）——
        /// 两者都**不能**当跨重启身份用。</para>
        /// </summary>
        private static string GetHardwareValue(IHardware hardware)
        {
            string identifier = hardware.Identifier?.ToString() ?? "";
            return string.IsNullOrWhiteSpace(identifier) ? (hardware.Name ?? "") : identifier;
        }

        private static List<GpuOption> CloneGpuOptions(List<GpuOption> source)
        {
            return source
                .Select(x => new GpuOption
                {
                    Label = x.Label,
                    Value = x.Value,
                    Name = x.Name,
                    StableId = x.StableId,
                    RuntimeId = x.RuntimeId,
                    Stability = x.Stability,
                })
                .ToList();
        }

        private static List<DeviceOption> CloneDeviceOptions(List<DeviceOption> source)
        {
            return source
                .Select(x => new DeviceOption
                {
                    Label = x.Label,
                    Value = x.Value,
                    Name = x.Name,
                    Kind = x.Kind,
                    IsPresent = x.IsPresent,
                    Stability = x.Stability,
                    RuntimeId = x.RuntimeId,
                    StableId = x.StableId,
                })
                .ToList();
        }

        /// <summary>
        /// 列出所有风扇传感器的 **Label + Value** 选择项。
        /// <para>★ [F07] 风扇身份 = **父设备身份 + 传感器名**（母计划 F11 的多风扇槽位基础）：
        /// 同一个传感器名出现在不同父设备上时不会互相覆盖。</para>
        /// <para>★ 本方法**只读属性**，不调用 Update()（不刷新主板控制器）。</para>
        /// </summary>
        public static List<DeviceOption> ListAllFanOptions(IComputer computer, object syncLock)
        {
            return ListAllFanOptions(computer.Hardware, syncLock);
        }

        /// <summary>
        /// 列出所有风扇传感器的 **Label + Value** 选择项（可注入的硬件集合版本 —— 测试接缝）。
        /// </summary>
        public static List<DeviceOption> ListAllFanOptions(IEnumerable<IHardware> hardware, object syncLock)
        {
            var options = new List<DeviceOption>();
            lock (syncLock)
            {
                void Scan(IHardware hw)
                {
                    bool isExcluded = hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd ||
                                      hw.HardwareType == HardwareType.GpuIntel || hw.HardwareType == HardwareType.Cpu ||
                                      hw.HardwareType == HardwareType.Storage || hw.HardwareType == HardwareType.Memory ||
                                      hw.HardwareType == HardwareType.Network;

                    if (!isExcluded)
                    {
                        // 父设备身份：优先稳定 ID，退回运行时标识。
                        DeviceIdentity parent = IdentityResolver.Resolve(hw, DeviceKind.Motherboard);
                        string parentId = parent.HasStableId ? parent.StableId : parent.RuntimeId;

                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType != SensorType.Fan) continue;
                            options.Add(new DeviceOption
                            {
                                Label = GenerateSmartNameCore(s, hw, null),
                                Value = $"fan:{parentId}/{s.Name}",
                                Name = GenerateSmartNameCore(s, hw, null),
                                Kind = DeviceKind.Fan,
                                IsPresent = true,
                                Stability = parent.Stability,
                                StableId = parent.StableId,
                                RuntimeId = parent.RuntimeId,
                            });
                        }
                    }
                    foreach (var sub in hw.SubHardware) Scan(sub);
                }
                foreach (var hw in hardware) Scan(hw);
            }

            return options;
        }

        /// <summary>
        /// 列出所有风扇传感器 (排除 CPU/GPU 等核心自带风扇，主要针对主板/机箱风扇)
        /// </summary>
        public static List<string> ListAllFans(IComputer computer, object syncLock)
        {
            if (_cachedFanList != null && _cachedFanList.Count > 0)
                return _cachedFanList.ToList();

            var list = new List<string>();
            lock (syncLock)
            {
                void Scan(IHardware hw)
                {
                    bool isExcluded = hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd ||
                                      hw.HardwareType == HardwareType.GpuIntel || hw.HardwareType == HardwareType.Cpu ||
                                      hw.HardwareType == HardwareType.Storage || hw.HardwareType == HardwareType.Memory ||
                                      hw.HardwareType == HardwareType.Network;

                    if (!isExcluded)
                    {
                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType == SensorType.Fan) 
                                list.Add(GenerateSmartName(s, hw, computer));
                        }
                    }
                    foreach (var sub in hw.SubHardware) Scan(sub);
                }
                foreach (var hw in computer.Hardware) Scan(hw);
            }

            list.Sort();
            var final = list.Distinct().ToList();
            if (final.Count > 0) _cachedFanList = final;
            return final.ToList();
        }

        /// <summary>
        /// 列出所有适合作为主板/系统温度的传感器
        /// </summary>
        public static List<string> ListAllMoboTemps(IComputer computer, object syncLock)
        {
            if (_cachedMoboTempList != null && _cachedMoboTempList.Count > 0)
                return _cachedMoboTempList.ToList();

            var list = new List<string>();
            lock (syncLock)
            {
                void Scan(IHardware hw)
                {
                    bool isExcluded = hw.HardwareType == HardwareType.Cpu || hw.HardwareType == HardwareType.GpuNvidia ||
                                      hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel ||
                                      hw.HardwareType == HardwareType.Storage || hw.HardwareType == HardwareType.Memory ||
                                      hw.HardwareType == HardwareType.Network;

                    if (!isExcluded)
                    {
                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType == SensorType.Temperature) 
                                list.Add(GenerateSmartName(s, hw, computer));
                        }
                    }
                    foreach (var sub in hw.SubHardware) Scan(sub);
                }
                foreach (var hw in computer.Hardware) Scan(hw);
            }

            list.Sort();
            var final = list.Distinct().ToList();
            if (final.Count > 0) _cachedMoboTempList = final;
            return final.ToList();
        }
    }
}
