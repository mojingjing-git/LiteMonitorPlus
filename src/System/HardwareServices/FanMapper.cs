using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 专用服务：负责智能识别和映射风扇/水泵/机箱风扇
    /// </summary>
    public class FanMapper
    {
        // 将原 SensorMap 中的 ScanAndMapFans 及相关辅助方法移到这里
        
        public void ScanAndMapFans(IEnumerable<IHardware> hardware, Settings cfg, Dictionary<string, ISensor> targetMap)
        {
            ISensor? cpuFan = null;
            ISensor? cpuPump = null; 
            ISensor? caseFan = null;
            
            // 1. 搜集全系统所有“活着”的风扇 (采用黑名单机制)
            var activeFans = new List<(IHardware Hw, ISensor S, float Rpm)>();
            
            void CollectFans(IHardware hw)
            {
                // ★★★ 黑名单：坚决排除以下类型的风扇 ★★★
                bool isExcluded = hw.HardwareType == HardwareType.GpuNvidia ||
                                  hw.HardwareType == HardwareType.GpuAmd ||
                                  hw.HardwareType == HardwareType.GpuIntel ||
                                  hw.HardwareType == HardwareType.Cpu ||
                                  hw.HardwareType == HardwareType.Storage ||
                                  hw.HardwareType == HardwareType.Memory ||
                                  hw.HardwareType == HardwareType.Network;

                if (!isExcluded)
                {
                    hw.Update();
                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType == SensorType.Fan && s.Value.HasValue)
                        {
                            // [底噪过滤] 门槛 200 RPM
                            if (s.Value.Value > 200)
                            {
                                activeFans.Add((hw, s, s.Value.Value));
                            }
                        }
                    }
                }
                foreach (var sub in hw.SubHardware) CollectFans(sub);
            }
            foreach (var hw in hardware) CollectFans(hw);

            // 辅助函数：判断是否已被占用
            bool IsTaken(ISensor s) => s == cpuFan || s == cpuPump || s == caseFan;
            
            // -----------------------------------------------------------
            // 步骤 2: 尊重用户手动配置 (最高优先级)
            // -----------------------------------------------------------
            if (!string.IsNullOrEmpty(cfg.PreferredCpuFan)) cpuFan = FindFanByConfig(activeFans, cfg.PreferredCpuFan);
            if (!string.IsNullOrEmpty(cfg.PreferredCaseFan)) caseFan = FindFanByConfig(activeFans, cfg.PreferredCaseFan);
            if (!string.IsNullOrEmpty(cfg.PreferredCpuPump)) cpuPump = FindFanByConfig(activeFans, cfg.PreferredCpuPump);

            // -----------------------------------------------------------
            // 步骤 3: 确立 CPU 风扇
            // -----------------------------------------------------------
            if (cpuFan == null && activeFans.Count > 0)
            {
                var coolerFan = activeFans.FirstOrDefault(x => !IsTaken(x.S) && 
                    IsCoolerHardware(x.Hw) && !SensorMap.Has(x.S.Name, "Pump"));
                
                if (coolerFan.S != null) cpuFan = coolerFan.S;
                else
                {
                    var namedCpu = activeFans.FirstOrDefault(x => !IsTaken(x.S) && SensorMap.Has(x.S.Name, "CPU"));
                    if (namedCpu.S != null) cpuFan = namedCpu.S;
                    else
                    {
                        var first = activeFans.FirstOrDefault(x => !IsTaken(x.S));
                        if (first.S != null) cpuFan = first.S;
                    }
                }
            }

            // -----------------------------------------------------------
            // 步骤 4: 确立 水泵 (Pump)
            // -----------------------------------------------------------
            if (cpuPump == null && activeFans.Count > 0)
            {
                var coolerPump = activeFans.FirstOrDefault(x => !IsTaken(x.S) && 
                    IsCoolerHardware(x.Hw) && 
                    (SensorMap.Has(x.S.Name, "Pump") || SensorMap.Has(x.S.Name, "Speed"))); 
                
                if (coolerPump.S == null)
                    coolerPump = activeFans.FirstOrDefault(x => !IsTaken(x.S) && IsCoolerHardware(x.Hw));

                if (coolerPump.S != null) cpuPump = coolerPump.S;
                else
                {
                    var namedPump = activeFans.FirstOrDefault(x => !IsTaken(x.S) && 
                        (SensorMap.Has(x.S.Name, "Pump") || SensorMap.Has(x.S.Name, "Water") || SensorMap.Has(x.S.Name, "AIO")));
                    if (namedPump.S != null) cpuPump = namedPump.S;
                }
            }

            // 智能猜想水泵 (转速 > 3000)
            if (cpuPump == null)
            {
                var highSpeedCandidate = activeFans.Where(x => !IsTaken(x.S) && x.Rpm > 3000)
                                                   .OrderByDescending(x => x.Rpm)
                                                   .FirstOrDefault();
                if (highSpeedCandidate.S != null) cpuPump = highSpeedCandidate.S;
            }

            // -----------------------------------------------------------
            // 步骤 5: 确立 机箱风扇
            // -----------------------------------------------------------
            if (caseFan == null && activeFans.Count > 0)
            {
                var leftovers = activeFans.Where(x => !IsTaken(x.S)).ToList();

                var best = leftovers.FirstOrDefault(x => SensorMap.Has(x.S.Name, "Rear"));
                if (best.S == null) best = leftovers.FirstOrDefault(x => SensorMap.Has(x.S.Name, "Chassis"));
                if (best.S == null) best = leftovers.FirstOrDefault(x => SensorMap.Has(x.S.Name, "Sys"));
                if (best.S == null) best = leftovers.FirstOrDefault(x => SensorMap.Has(x.S.Name, "Case"));

                if (best.S != null) caseFan = best.S;
                else if (leftovers.Count > 0)
                {
                    var sorted = leftovers.OrderBy(x => x.Rpm).ToList();
                    caseFan = sorted.First().S; 
                    if (cpuPump == null && sorted.Count > 1) cpuPump = sorted.Last().S;
                }
            }

            // 3. 写入 Map
            if (cpuFan != null) targetMap["CPU.Fan"] = cpuFan;
            if (caseFan != null) targetMap["CASE.Fan"] = caseFan;
            if (cpuPump != null) targetMap["CPU.Pump"] = cpuPump;
        }

        private ISensor? FindFanByConfig(List<(IHardware Hw, ISensor S, float Rpm)> fans, string configStr)
        {
            foreach (var item in fans)
            {
                string uid = $"[{item.Hw.Name}] {item.S.Name}";
                if (uid == configStr || item.S.Name == configStr) return item.S;
            }
            return null;
        }

        private bool IsCoolerHardware(IHardware h) 
        {
            if (h.HardwareType == HardwareType.Cooler) return true;
            string n = h.Name;
            return SensorMap.Has(n, "Kraken") || SensorMap.Has(n, "Corsair") || SensorMap.Has(n, "Liquid") || SensorMap.Has(n, "AIO") || SensorMap.Has(n, "Cooler");
        }
    }
}