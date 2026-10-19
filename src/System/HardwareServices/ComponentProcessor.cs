using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 组件处理器：负责 CPU 和 GPU 的复杂数值加工（聚合、修正、过滤）
    /// </summary>
    public class ComponentProcessor
    {
        private readonly IComputer _computer;
        private readonly Settings _cfg;
        private readonly SensorMap _sensorMap;

        private List<ISensor>? _cpuLoadSensorsCache = null;
        private List<ISensor>? _cpuTempSensorsCache = null;

        public ComponentProcessor(IComputer computer, Settings cfg, SensorMap sensorMap)
        {
            _computer = computer;
            _cfg = cfg;
            _sensorMap = sensorMap;
        }

        public void ClearCache()
        {
            _cpuLoadSensorsCache = null;
            _cpuTempSensorsCache = null;
        }

        /// <summary>
        /// 获取 CPU 负载 (多核平均)
        /// </summary>
        public float? GetCpuLoad()
        {
            if (_cpuLoadSensorsCache == null)
            {
                var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (cpu != null)
                {
                    _cpuLoadSensorsCache = new List<ISensor>();
                    foreach (var s in cpu.Sensors)
                    {
                        if (s.SensorType != SensorType.Load) continue;
                        if (SensorMap.Has(s.Name, "Core") && SensorMap.Has(s.Name, "#") && 
                            !SensorMap.Has(s.Name, "Total") && !SensorMap.Has(s.Name, "SOC") && 
                            !SensorMap.Has(s.Name, "Max") && !SensorMap.Has(s.Name, "Average"))
                        {
                            _cpuLoadSensorsCache.Add(s);
                        }
                    }
                }
            }

            if (_cpuLoadSensorsCache != null && _cpuLoadSensorsCache.Count > 0)
            {
                double totalLoad = 0;
                int coreCount = 0;
                foreach (var s in _cpuLoadSensorsCache)
                {
                    if (s.Value.HasValue) { totalLoad += s.Value.Value; coreCount++; }
                }
                if (coreCount > 0) return (float)(totalLoad / coreCount);
            }
            return null;
        }

        /// <summary>
        /// 获取 CPU 温度 (核心最大值)
        /// </summary>
        public float? GetCpuTemp()
        {
            if (_cpuTempSensorsCache == null)
            {
                var cpuT = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (cpuT != null)
                {
                    _cpuTempSensorsCache = new List<ISensor>();
                    foreach (var s in cpuT.Sensors)
                    {
                        if (s.SensorType == SensorType.Temperature && 
                            !SensorMap.Has(s.Name, "Distance") && !SensorMap.Has(s.Name, "Average") && !SensorMap.Has(s.Name, "Max"))
                        {
                            _cpuTempSensorsCache.Add(s);
                        }
                    }
                }
            }

            if (_cpuTempSensorsCache != null && _cpuTempSensorsCache.Count > 0)
            {
                float maxTemp = -1000f;
                bool found = false;
                foreach (var s in _cpuTempSensorsCache)
                {
                    if (s.Value.HasValue && s.Value.Value > 0)
                    {
                        if (s.Value.Value > maxTemp) { maxTemp = s.Value.Value; found = true; }
                    }
                }
                if (found) return maxTemp;
            }
            return null;
        }

        /// <summary>
        /// 获取复合数值 (频率、功耗等带修正或过滤逻辑的数值)
        /// </summary>
        public float? GetCompositeValue(string key, Dictionary<string, ISensor> sensorCache)
        {
            if (key == "CPU.Clock")
            {
                if (_sensorMap.CpuCoreCache.Count == 0) return null;
                double sum = 0; int count = 0; float maxRaw = 0;
                
                // Zen 5 频率修正逻辑
                float correctionFactor = 1.0f;
                if (_sensorMap.CpuBusSpeedSensor != null && _sensorMap.CpuBusSpeedSensor.Value.HasValue)
                {
                    float bus = _sensorMap.CpuBusSpeedSensor.Value.Value;
                    if (bus > 1.0f && bus < 20.0f) 
                    { 
                        float factor = 100.0f / bus; 
                        if (factor > 2.0f && factor < 10.0f) correctionFactor = factor; 
                    }
                }

                foreach (var core in _sensorMap.CpuCoreCache)
                {
                    if (core.Clock == null || !core.Clock.Value.HasValue) continue;
                    float clk = core.Clock.Value.Value * correctionFactor;
                    if (clk > maxRaw) maxRaw = clk;
                    if (clk > 400f) { sum += clk; count++; }
                }
                if (maxRaw > 0) _cfg.UpdateMaxRecord(key, maxRaw);
                if (count > 0) return (float)(sum / count);
                return maxRaw;
            }

            if (key == "CPU.Power")
            {
                if (sensorCache.TryGetValue("CPU.Power", out var s) && s.Value.HasValue) 
                { 
                    // 熔断保护：过滤异常高功耗
                    if (s.Value.Value > 600.0f) return null;
                    _cfg.UpdateMaxRecord(key, s.Value.Value); 
                    return s.Value.Value; 
                }
                return null;
            }

            if (key.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
            {
                // ★★★ [多显卡] 解析出显卡序号, 取对应那张卡的传感器 ★★★
                int gpuIndex = -1;
                string metric = key.Substring(GpuKey.Prefix.Length);
                if (GpuKey.TryParse(key, out int parsedIndex, out string parsedMetric))
                {
                    gpuIndex = parsedIndex;
                    metric = parsedMetric;
                }

                var gpu = gpuIndex >= 0 ? _sensorMap.GetGpuByIndex(gpuIndex) : _sensorMap.CachedGpu;
                if (gpu == null) return null;

                if (metric.Equals("Clock", StringComparison.OrdinalIgnoreCase))
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Clock && (SensorMap.Has(x.Name, "graphics") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "shader")));
                    if (s != null && s.Value.HasValue) 
                    { 
                        float val = s.Value.Value; 
                        if (val > 6000.0f) return null; 
                        _cfg.UpdateMaxRecord(key, val); 
                        return val; 
                    }
                }
                else if (metric.Equals("Power", StringComparison.OrdinalIgnoreCase))
                {
                    var s = gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Power && (SensorMap.Has(x.Name, "package") || SensorMap.Has(x.Name, "ppt") || SensorMap.Has(x.Name, "board") || SensorMap.Has(x.Name, "core") || SensorMap.Has(x.Name, "gpu")));
                    if (s != null && s.Value.HasValue) 
                    { 
                        float val = s.Value.Value; 
                        if (val > 1200.0f) return null; 
                        _cfg.UpdateMaxRecord(key, val); 
                        return val; 
                    }
                }
            }
            return null;
        }
    }
}
