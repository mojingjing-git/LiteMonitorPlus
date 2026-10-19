using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 核心服务：负责将物理硬件传感器映射为标准 Key (如 CPU.Temp)
    /// </summary>
    public class SensorMap
    {
        // 核心映射字典
        private readonly Dictionary<string, ISensor> _map = new();
        
        // ★★★ [新增] 引入子服务：风扇匹配器 (解耦) ★★★
        private readonly FanMapper _fanMapper = new FanMapper();

        // 高性能缓存
        // CPU 核心传感器对 (用于加权平均计算)
        public class CpuCoreSensors
        {
            public ISensor? Clock;
            public ISensor? Load;
        }
        public List<CpuCoreSensors> CpuCoreCache { get; private set; } = new();
        
        // 显卡硬件缓存 (用于快速定位)
        public IHardware? CachedGpu { get; private set; }

        // ★★★ [多显卡] 被监控的显卡列表, 下标即 Key 中的序号 ★★★
        public List<IHardware> CachedGpus { get; private set; } = new List<IHardware>();

        /// <summary>取指定序号的被监控显卡, 越界返回 null</summary>
        public IHardware? GetGpuByIndex(int index)
        {
            lock (_lock)
            {
                if (index < 0 || index >= CachedGpus.Count) return null;
                return CachedGpus[index];
            }
        }
        // ★★★ [新增] 缓存总线传感器 (用于 Zen 5 频率修正) ★★★
        public ISensor? CpuBusSpeedSensor { get; private set; }

        private DateTime _lastMapBuild = DateTime.MinValue;
        
        // ★★★ [优化 1] 移除了全系统传感器指纹相关字段 ★★★
        private readonly object _lock = new object();
        
        // ★★★ [新增] 配置引用 ★★★
        private Settings _cfg;

        // [Fix] 回归 v1.2.9 的简单逻辑
        // 只有在初始化时才需要 build，之后除非手动触发或 10 分钟兜底，否则不需要任何检测。
        // EnsureFresh 的返回值改为 void，因为不需要外部感知是否重建。
        public void EnsureFresh(Computer computer, Settings cfg) 
        {
            if ((DateTime.Now - _lastMapBuild).TotalMinutes > 10)
            {
                Rebuild(computer, cfg);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                CpuCoreCache.Clear();
                CachedGpu = null;
                CachedGpus = new List<IHardware>();
                CpuBusSpeedSensor = null;
                GpuRegistry.Clear();
                // _lastSensorFingerprint = 0; // 已移除
            }
        }



        public bool TryGetSensor(string key, out ISensor? sensor)
        {
            lock (_lock)
            {
                return _map.TryGetValue(key, out sensor);
            }
        }

        /// <summary>
        /// [新增] 获取内部映射的副本，用于 HardwareValueProvider 的静态化预热
        /// </summary>
        public Dictionary<string, ISensor> GetInternalMap()
        {
            lock (_lock)
            {
                return new Dictionary<string, ISensor>(_map);
            }
        }

        /// <summary>
        /// 选出被监控的显卡集合。
        /// 规则:
        ///   - MonitoredGpuCount == 1 : 沿用旧逻辑, 优先用 PreferredGpu 指定的卡, 否则取优先级最高的一张
        ///   - MonitoredGpuCount >= 2 : 按优先级取前 N 张
        /// 顺序与 Rebuild 中的遍历顺序一致, 保证 Key 中的序号稳定。
        /// </summary>
        private static List<IHardware> SelectMonitoredGpus(Computer computer, Settings cfg)
        {
            var all = computer.Hardware
                .Where(HardwareScanner.IsGpuHardware)
                .OrderBy(h => HardwareRules.GetHwPriority(h))
                .ToList();

            if (all.Count == 0) return new List<IHardware>();

            int want = cfg.MonitoredGpuCount;
            if (want < 1) want = 1;

            if (want == 1)
            {
                var selected = HardwareScanner.FindGpuByConfig(computer, cfg.PreferredGpu ?? "");
                if (selected != null) return new List<IHardware> { selected };
                return new List<IHardware> { all[0] };
            }

            return all.Take(Math.Min(want, all.Count)).ToList();
        }

        /// <summary>
        /// 在被监控显卡列表中按引用查找序号, 找不到返回 -1。
        /// 这里刻意不用 Dictionary + ReferenceEqualityComparer —— 该比较器只实现了
        /// IEqualityComparer&lt;object&gt;, 不能直接用于 Dictionary&lt;IHardware, int&gt;。
        /// </summary>
        private static int FindGpuIndex(List<IHardware> monitored, IHardware hw)
        {
            for (int i = 0; i < monitored.Count; i++)
            {
                if (ReferenceEquals(monitored[i], hw)) return i;
            }
            return -1;
        }

        // =======================================================================
        // [核心] 构建传感器映射与缓存 (最复杂的构建逻辑)
        // =======================================================================

        public void Rebuild(Computer computer, Settings cfg) // ★ 签名修改
        {
            _cfg = cfg;
            // 1. 准备临时容器 (线程安全)
            var newMap = new Dictionary<string, ISensor>(StringComparer.OrdinalIgnoreCase); // 使用忽略大小写的比较器，减少字符串重复
            var newCpuCache = new List<CpuCoreSensors>();
            IHardware? newGpu = null;
            ISensor? newBusSensor = null; // 临时变量
            
            // ★★★ [新增] 临时列表：用于智能匹配 ★★★
            // 注意：这里不再在递归中直接处理风扇匹配，而是收集起来统一交给 ScanAndMapFans 处理
            // 但为了保持原代码结构，我们依然用candidates收集主板相关数据
            var candidatesMoboTemps = new List<ISensor>(capacity: 10); // 预设容量，减少扩容开销

            // ★★★ [多显卡] 选出被监控的显卡集合 (序号即 Key 中的序号) ★★★
            var monitoredGpus = SelectMonitoredGpus(computer, cfg);

            // ★★★ [多显卡] 显卡简称的本地缓冲区, 在原子交换点整体写入 GpuRegistry ★★★
            var gpuInfoBuffer = new GpuInfo[GpuKey.MaxGpuCount];

            // 局部递归函数
            void RegisterTo(IHardware hw, int gpuIndex)
            {
                //hw.Update();

                bool isGpuHardware = HardwareScanner.IsGpuHardware(hw);

                // ★★★ [多显卡] 只有在监控集合里的显卡才继续处理 ★★★
                if (isGpuHardware && gpuIndex < 0) return;

                // --- 填充 CPU 缓存 (用于加权平均) ---
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    // ★★★ [新增] 查找并缓存 Bus Speed 传感器 ★★★
                    // 优先查找名为 "Bus Speed" 的时钟传感器
                    if (newBusSensor == null)
                    {
                        newBusSensor = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.Contains("Bus Speed"));
                    }

                    // 查找所有核心频率 (排除总线频率)
                    var clocks = hw.Sensors.Where(s => s.SensorType == SensorType.Clock && Has(s.Name, "core") && !Has(s.Name, "bus"));
                    
                    foreach (var clock in clocks)
                    {
                        // ★★★ 修复：AMD 负载叫 "CPU Core #1"，频率叫 "Core #1"，不相等。
                        // 改用 EndsWith 匹配，既支持 Intel (名字一样) 也支持 AMD (带前缀)
                        var load = hw.Sensors.FirstOrDefault(s => 
                            s.SensorType == SensorType.Load && 
                            s.Name.EndsWith(clock.Name, StringComparison.OrdinalIgnoreCase)); 
                            
                        newCpuCache.Add(new CpuCoreSensors { Clock = clock, Load = load });
                    }
                }

                // --- 填充 GPU 缓存 (优化版：完全依赖优先级排序) ---
                if (isGpuHardware)
                {
                    if (newGpu == null)
                    {
                        newGpu = hw;
                    }

                    // ★★★ [多显卡] 登记显卡简称, 供 UI 打标签 ★★★
                    // 先写进本地数组, 最后在原子交换点整体替换 GpuRegistry ——
                    // 避免读侧 (UI 线程) 看到"半更新"的注册表。
                    if (gpuIndex >= 0 && gpuIndex < gpuInfoBuffer.Length)
                    {
                        gpuInfoBuffer[gpuIndex] = new GpuInfo
                        {
                            ShortName = HardwareRules.GetShortGpuName(hw.Name),
                            FullName = hw.Name
                        };
                    }


                    // ★★★ [多显卡] GPU 风扇映射 (按序号写各自的 Key) ★★★
                    // 修复: 旧代码这里是无条件赋值 newMap["GPU.Fan"] = fan,
                    //       多卡时后一张卡会覆盖前一张, 导致风扇读数串卡。
                    //       现在每张卡写进自己的 "GPU.{序号}.Fan", 互不干扰。
                    var fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
                    if (fan == null) fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control);
                    if (fan != null)
                    {
                        string fanKey = GpuKey.Build(gpuIndex, "Fan");
                        if (!newMap.ContainsKey(fanKey)) newMap[fanKey] = fan;

                        // 第 0 张卡额外写入旧格式 Key, 保证旧配置继续可用
                        if (gpuIndex == 0 && !newMap.ContainsKey("GPU.Fan")) newMap["GPU.Fan"] = fan;
                    }
                }
                
                // ★★★ [新增] 收集主板/SuperIO 传感器 ★★★
                if (hw.HardwareType == HardwareType.Motherboard || hw.HardwareType == HardwareType.SuperIO)
                {
                    foreach (var s in hw.Sensors)
                    {
                        // 风扇数据由后续 ScanAndMapFans 全局扫描，这里只收集温度备用
                        if (s.SensorType == SensorType.Temperature) candidatesMoboTemps.Add(s);
                    }
                }

                // --- 普通传感器映射 ---
                foreach (var s in hw.Sensors)
                {
                    string? key = SensorMatcher.Match(hw, s); // 调用 SensorMatcher 进行识别
                    if (!string.IsNullOrEmpty(key))
                    {
                        // ★★★ [多显卡] 显卡指标改写成带序号的形式 ★★★
                        bool isGpuKey = isGpuHardware
                                        && key.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase);
                        string finalKey = key;
                        if (isGpuKey)
                        {
                            string metric = key.Substring(GpuKey.Prefix.Length);
                            finalKey = GpuKey.Build(gpuIndex, metric);
                        }

                        if (!newMap.ContainsKey(finalKey))
                        {
                            newMap[finalKey] = s;
                        }
                        else
                        {
                            // [Fix] 冲突解决策略：优先选择 Vendor (非 D3D) 传感器
                            // 必须严格保护高优先级显卡的数据，防止被低优先级显卡覆盖！
                            var existing = newMap[finalKey];
                            
                            // 1. 如果现有数据来自"更强"的显卡 (Priority数值更小)，绝对禁止覆盖！
                            if (HardwareRules.GetHwPriority(existing.Hardware) < HardwareRules.GetHwPriority(hw))
                            {
                                continue;
                            }

                            // 2. 如果优先级相同，则应用 D3D vs Vendor 优选逻辑。
                            bool existingIsD3D = existing.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
                            bool newIsD3D = s.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
                            
                            // 如果现有的是 D3D 而新来的不是 D3D -> 替换为新来的 (Upgrade)
                            if (existingIsD3D && !newIsD3D)
                            {
                                newMap[finalKey] = s;
                            }
                            // 其他情况 (Vendor vs Vendor, Vendor vs D3D) -> 保持现有 (First Wins / Vendor Wins)
                        }

                        // ★★★ [多显卡] 第 0 张卡额外写入旧格式 Key, 兼容旧配置 ★★★
                        if (isGpuKey && gpuIndex == 0 && !newMap.ContainsKey(key))
                        {
                            newMap[key] = s;
                        }
                    }
                }

                foreach (var sub in hw.SubHardware) RegisterTo(sub, gpuIndex);
            }

            // 按优先级排序并注册
            var ordered = computer.Hardware.OrderBy(h => HardwareRules.GetHwPriority(h));
            foreach (var hw in ordered)
            {
                int gpuIndex = FindGpuIndex(monitoredGpus, hw);
                RegisterTo(hw, gpuIndex);
            }
            
            // ============================================
            // ★★★ [新增] 智能匹配逻辑 ★★★
            // ============================================
            
            // A. 主板温度 (策略：System > Motherboard > Chipset > MaxValue)
            if (!newMap.ContainsKey("MOBO.Temp") && candidatesMoboTemps.Count > 0)
            {
                // 1. 优先匹配标准名称 (System, Motherboard)
                var best = candidatesMoboTemps.FirstOrDefault(x => Has(x.Name, "System"));
                if (best == null) best = candidatesMoboTemps.FirstOrDefault(x => Has(x.Name, "Motherboard"));
                
                // 2. 其次匹配芯片组 (Chipset)
                if (best == null) best = candidatesMoboTemps.FirstOrDefault(x => Has(x.Name, "Chipset") || Has(x.Name, "PCH"));
                
                // 3. 兜底策略：找有效值中最大的 (假设热点即关键点，且排除 0 和 200+ 异常值)
                if (best == null)
                {
                    // ★★★ 优化策略：优先寻找"合理范围"(15-68度)内的最大值，这通常是 System/Motherboard 温度 ★★★
                    // 只有当找不到合理值时，才去寻找更宽范围(0-95度)的最大值(可能是 VRM 或 Chipset)
                    // 修复：用户反馈 Asus Z790 主板 Temperature #5 经常跳到 100+，导致误报。
                    
                    ISensor? bestSafe = null;
                    float maxSafe = -999f;
                    
                    ISensor? bestFallback = null;
                    float maxFallback = -999f;

                    foreach (var t in candidatesMoboTemps)
                    {
                        if (!t.Value.HasValue) continue;
                        float v = t.Value.Value;
                        
                        // 1. 收集宽范围 (0 - 95)，严格过滤掉 >95 的异常值/虚假值 (原逻辑是 110，太宽了)
                        if (v > 0 && v < 95 && v > maxFallback)
                        {
                            maxFallback = v;
                            bestFallback = t;
                        }
                        
                        // 2. 收集合理范围 (15 - 68)
                        // 68度通常是主板/系统温度的合理上限，超过这个值可能是 Chipset 或 VRM
                        if (v >= 15 && v <= 68 && v > maxSafe)
                        {
                            maxSafe = v;
                            bestSafe = t;
                        }
                    }
                    
                    // 优先使用合理范围的传感器
                    best = bestSafe ?? bestFallback;
                }
                
                if (best != null) newMap["MOBO.Temp"] = best;
            }

            // B. 风扇匹配只在确实显示风扇/水泵时运行，避免启动阶段无意义地刷新主板控制器。
            if (cfg.IsAnyEnabled("CPU.Fan") ||
                cfg.IsAnyEnabled("CPU.Pump") ||
                cfg.IsAnyEnabled("CASE.Fan"))
            {
                _fanMapper.ScanAndMapFans(computer, cfg, newMap);
            }

            // 2. 原子交换数据 (加锁)
            lock (_lock)
            {
                _map.Clear();
                foreach (var kv in newMap) _map[kv.Key] = kv.Value;
                
                CpuCoreCache = newCpuCache;
                CachedGpu = newGpu;
                CachedGpus = new List<IHardware>(monitoredGpus); // ★★★ [多显卡] ★★★
                CpuBusSpeedSensor = newBusSensor; // ★ 更新 Bus Sensor 缓存

                // ★★★ [多显卡] 显卡简称注册表整体替换 (原子) ★★★
                GpuRegistry.ReplaceAll(gpuInfoBuffer);
                
                _lastMapBuild = DateTime.Now;
                // ★★★ [优化 3] 指纹记录已移除 ★★★
            }
        }

        // 复用 HardwareRules 的字符串匹配，避免重复造轮子
        public static bool Has(string source, string sub) => HardwareRules.Has(source, sub);
    }
}
