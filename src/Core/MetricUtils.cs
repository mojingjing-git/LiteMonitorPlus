using System;
using System.Linq;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Core
{
    public enum MetricType
    {
        Percent,    // Load, BatPercent
        Temperature,// Temp
        Memory,     // Mem, Vram
        DataSize,   // Data Total
        DataSpeed,  // Net, Disk
        Frequency,  // Clock
        Power,      // Power
        Voltage,    // Voltage
        Current,    // Current
        RPM,        // Fan, Pump
        FPS,        // FPS
        Unknown
    }

    /// <summary>
    /// LiteMonitor++ 核心指标处理工具
    /// 包含：类型解析、数据格式化、阈值评估、状态管理
    /// </summary>
    public static class MetricUtils
    {
        // =========================================================
        // 1. 全局状态 (自动缓存，3秒刷新)
        // =========================================================
        private static long _lastPowerCheck = 0;// 上次刷新电源功耗时间
        private static bool _lastAc = false;// 是否接通电源
        private static bool _lastCharging = false;// 是否正在充电 (排除充满/保护)

        /// <summary>
        /// 获取当前电源状态 (Unified)
        /// AcOnline: 是否接通电源
        /// IsCharging: 是否正在充电 (排除充满/保护)
        /// </summary>
        public static (bool AcOnline, bool IsCharging) GetPowerStatus()
        {
            long now = Environment.TickCount64;
            if (now - _lastPowerCheck > 3000) // 3000ms 自动节流
            {
                _lastPowerCheck = now;
                try
                {
                    var s = System.Windows.Forms.SystemInformation.PowerStatus;
                    _lastAc = s.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                    _lastCharging = (s.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.Charging) == System.Windows.Forms.BatteryChargeStatus.Charging;
                }
                catch { }
            }
            return (_lastAc, _lastCharging);
        }
        
        // [Optimization] Cache data size units
        private static readonly string[] _dataSizes = { "KB", "MB", "GB", "TB", "PB" };

        public const int STATE_SAFE = 0;
        public const int STATE_WARN = 1;
        public const int STATE_CRIT = 2;

        // =========================================================
        // 2. 类型解析 (原 MetricHelper)
        // =========================================================
        public static MetricType GetType(string key)
        {
            if (string.IsNullOrEmpty(key)) return MetricType.Unknown;
            
            if (key.Equals("FPS", StringComparison.OrdinalIgnoreCase)) return MetricType.FPS;
            if (key.StartsWith("BAT", StringComparison.OrdinalIgnoreCase))
            {
                if (key.IndexOf("Percent", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Percent;
                if (key.IndexOf("Voltage", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Voltage;
                if (key.IndexOf("Current", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Current;
                if (key.IndexOf("Power", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Power;
                return MetricType.Percent;
            }
            if (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("VRAM", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Memory;

            if (key.IndexOf("LOAD", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Percent;
            if (key.IndexOf("TEMP", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Temperature;
            if (key.IndexOf("CLOCK", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Frequency;
            if (key.IndexOf("POWER", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Power;
            if (key.IndexOf("VOLTAGE", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Voltage;
            if (key.IndexOf("FAN", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("PUMP", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.RPM;
            if (key.StartsWith("NET", StringComparison.OrdinalIgnoreCase) || 
                key.StartsWith("DISK", StringComparison.OrdinalIgnoreCase)) return MetricType.DataSpeed;
            if (key.IndexOf("DATA", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.DataSize;
            
            return MetricType.Unknown;
        }

        // =========================================================
        // 3. 格式化逻辑 (原 Formatter)
        // =========================================================

        /// <summary>
        /// 获取纯数值字符串 (已处理缩放、舍入、紧凑模式)
        /// </summary>
        public static string GetValueStr(string key, float? value, bool compact = false, string? userFormat = null)
        {
            float v = value ?? 0.0f;
            var type = GetType(key);

            // ★★ [#166] CPU 电压：数值与来源必须来自**同一份已发布解析结果** ★★
            //   - 缺失（来源不可用 / 没有候选 / 读数无效）→ 显示 "--"，**绝不显示 0**；
            //   - 有效 → 用已发布分辨率里的值格式化，保证面板/任务栏/网页三处数字完全一致。
            if (IsCpuVoltageKey(key))
            {
                CpuVoltageResolution? resolution = CpuVoltageSourcePublisher.Current;
                if (resolution == null || !resolution.IsAvailable || !resolution.IsValid || !resolution.Value.HasValue)
                    return MissingValueText;

                return FormatValueAdaptive(resolution.Value.Value, -1);
            }

            // ★★ [Fix #137] 提交内存 ("MEM.Commit.Load") 走**独立**分支 ★★
            //   它必须在下面的 Memory 分支**之前**返回：
            //   既有 Memory 分支会把百分比乘以 Settings.DetectedRamTotalGB（物理内存总量），
            //   而提交内存的分母必须是**当前提交上限**（分页文件 + 物理内存的提交额度），
            //   用物理内存当分母会算出 150% 这种"提交额度比物理内存还大"的假象。
            if (IsCommittedMemoryKey(key))
            {
                return GetCommittedMemoryValueStr(v, userFormat);
            }

            // 1. 内存特殊处理 (容量 vs 百分比)
            if (type == MetricType.Memory)
            {
                 var cfg = Settings.Load();
                 // ★ 该监控项自己的单位格式如果明确要求百分比，就**不能**走容量换算：
                 //   否则数值被换算成 GB 数、单位却按用户格式显示 %，两者对不上
                 //   （实测 50% × 32GB 会显示成 "16.0%"）。全局 MemoryDisplayMode 只是默认口径，单项格式优先。
                 if (cfg.MemoryDisplayMode == 1 && !IsPercentUnitOverride(userFormat)) // 容量模式
                 {
                     double totalGB = (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) 
                        ? Settings.DetectedRamTotalGB 
                        : GetGpuVramTotalGB(key);

                     if (totalGB > 0)
                     {
                         // 转换为字节 -> 格式化 -> 取数值部分
                         // [Refactor] 使用自适应格式化 (-1)，确保 100GB 时不显示 .0
                         return FormatDataSizeParts((v / 100.0) * totalGB * 1073741824.0, -1).val;
                     }
                 }
            }

            // 2. 数据量/速度 (自动单位缩放)
            if (type == MetricType.DataSpeed || type == MetricType.DataSize)
            {
                //return FormatDataSizeParts(v, compact ? -1 : 1).val;
                return FormatDataSizeParts(v,-1 ).val;
            }

            // 4. 标准模式 (Panel)
            if (type == MetricType.FPS || type == MetricType.RPM) return $"{v:0}";
            if (type == MetricType.Frequency) return $"{v/1000f:F1}"; // 频率保持 F1，通常 GHz 需要一位小数
            
            // 内存百分比、负载、温度、功耗等

            return FormatValueAdaptive(v, -1);
        }

        /// <summary>
        /// 内部通用的数值格式化逻辑 (自适应小数点)
        /// </summary>
        private static string FormatValueAdaptive(double val, int decimals)
        {
            if (decimals >= 0)
            {
                // 固定小数位模式
                string format = "0." + new string('0', decimals);
                if (decimals == 0) format = "0";
                
                // [Fix] 即使是固定模式，如果数值 >= 99.95，也遵循用户习惯显示为整数
                // 除非 decimals > 1 (通常是电压/电流等需要高精度的场合)
                if (val >= 99.95 && decimals <= 1) return val.ToString("0");
                
                return val.ToString(format);
            }

            // 自适应模式 (decimals < 0)
            if (val < 9.995) return val.ToString("0.00");   // 0.00 - 9.99
            if (val < 99.95) return val.ToString("0.0");   // 10.0 - 99.9
            return val.ToString("0");                      // 100+
        }

        public enum UnitContext
        {
            Panel,          // 主界面 (完整, 带空格)
            Taskbar,        // 任务栏 (紧凑, 无空格)
            SettingsPanel,  // 设置界面 - 主界面预览 (带占位符)
            SettingsTaskbar // 设置界面 - 任务栏预览 (带占位符)
        }

        /// <summary>
        /// 获取单位字符串 (处理上下文、动态单位)
        /// </summary>
        public static string GetUnitStr(string key, float? value, UnitContext context)
        {
            var type = GetType(key);

            // ★★ [#166] CPU 电压：单位恒为 "V"，但**必须带上来源标记** ★★
            //   处理器只上报"请求电压"(VID) 时，单位后面挂 " (CPU VID)"：
            //   这是**独立于用户标签**的通道 —— 用户把监控项改名成"我的电压"也抹不掉这条提示。
            //   缺失时**不带单位**（否则会显示成 "-- V" 这种自相矛盾的读数）。
            if (IsCpuVoltageKey(key))
            {
                CpuVoltageResolution? resolution = CpuVoltageSourcePublisher.Current;
                if (resolution == null || !resolution.IsAvailable) return "";
                return "V" + GetCpuVoltageSourceMarker(resolution);
            }

            // ★★ [Fix #137] 提交内存走独立分支（与 GetValueStr 的分支一一对应）★★
            //   单位是**常量**（容量模式固定 GB），因此数值与单位**跨代次也不会错配**。
            if (IsCommittedMemoryKey(key))
            {
                return GetCommittedMemoryUnitStr(context);
            }
            
            // 1. 内存 (GB vs %)
            if (type == MetricType.Memory) 
            {
                if (Settings.Load().MemoryDisplayMode != 1) return "%";
                if (context == UnitContext.SettingsPanel || context == UnitContext.SettingsTaskbar) return "{u}";

                double totalGB = (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) ? Settings.DetectedRamTotalGB : GetGpuVramTotalGB(key);
                
                return (totalGB > 0) 
                    ? FormatDataSizeParts(((value ?? 0f) / 100.0) * totalGB * 1073741824.0, -1).unit 
                    : "%"; // [Fix] 容量未知(totalGB<=0)时回退百分比，与 GetValueStr 的数值回退口径一致；
                           // 原先此处返回 "GB"，而数值已回退成百分比，界面会显示成「45.0 GB」这种既非容量也非百分比的读数。
            }
            

            // 2. 数据 (动态单位)
            if (type == MetricType.DataSpeed || type == MetricType.DataSize)
            {
                // Settings 模式下，返回 {u} 占位符，提示用户这是动态单位
                // 仅 SettingsPanel 和 SettingsTaskbar 返回占位符
                if (context == UnitContext.SettingsPanel || context == UnitContext.SettingsTaskbar)
                {
                    if (type == MetricType.DataSpeed)
                        return context == UnitContext.SettingsTaskbar ? "{u}" : "{u}/s";
                    return "{u}";
                }

                // 需要根据数值重新计算单位 (FormatDataSizeParts 开销很小)
                // UnitContext.Taskbar 对应紧凑模式 -> 整数 (0)
                // UnitContext.Panel 对应主界面 -> 自适应 (-1)
                int decimals = (context == UnitContext.Taskbar) ? 0 : -1;
                string u = FormatDataSizeParts(value ?? 0, decimals).unit;
                
                if (type == MetricType.DataSpeed)
                    return context == UnitContext.Taskbar ? u : u + "/s";
                
                return u;
            }
            // ★★★ [2] 新增：如果是插件项，去 InfoService 查默认单位 ★★★
            if (key.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase))
            {
                 // 尝试获取刚才 SyncService 注入的单位
                 string u = InfoService.Instance.GetValue(key + ".Unit");
                 if (!string.IsNullOrEmpty(u)) return u;
            }

            // 3. 电池充电后缀
            // [Fix] 仅在真正充电时显示闪电图标 (AcOnline + IsCharging)
            // 充满电插着电源时 (AcOnline=true, IsCharging=false) 不应显示闪电
            var pStatus = GetPowerStatus();
            string suffix = (
                key.StartsWith("BAT", StringComparison.OrdinalIgnoreCase) 
                && pStatus.AcOnline 
                && pStatus.IsCharging // <--- 新增条件
                && context != UnitContext.SettingsPanel 
                && context != UnitContext.SettingsTaskbar) ? "+" : "";

            // 4. 标准单位表
            return type switch
            {
                MetricType.Percent     => "%" + suffix,
                MetricType.Temperature => "°C",
                MetricType.Frequency   => "GHz",
                MetricType.Power       => "W" + suffix,
                MetricType.Voltage     => "V" + suffix,
                MetricType.Current     => "A" + suffix,
                // Taskbar 和 SettingsTaskbar 使用简写
                MetricType.FPS         => (context == UnitContext.Taskbar || context == UnitContext.SettingsTaskbar) ? "F" : " FPS",
                MetricType.RPM         => (context == UnitContext.Taskbar || context == UnitContext.SettingsTaskbar) ? "R" : " RPM",
                _ => ""
            };
        }

        public static string GetSampleValueStr(string key)
        {
            var type = GetType(key);
            //if (type == MetricType.Memory && Settings.Load().MemoryDisplayMode == 1) return "99.9";
            
            return type switch
            {
                MetricType.Frequency => "9.9",// GHz
                // MetricType.Voltage => "1.25",// V
                MetricType.RPM => "9999", // RPM
                MetricType.DataSpeed => "9.9.",// MB
                MetricType.DataSize => "9.9.",// MB
                _ => "99.9"
            };
        }

        /// <summary>
        /// 该监控项的单位格式是否**显式要求百分比**。
        ///
        /// 判据是「含 % 且不含 {u}」：
        ///   - 含 <c>{u}</c> 说明用户要的是「在自动单位后面拼点东西」，仍按全局显示模式走；
        ///   - 不含 <c>{u}</c> 又写了 % —— 那就是「我就要按百分比看」，容量换算必须让位。
        /// 空格式表示隐藏单位（不是要百分比），返回 false。
        /// </summary>
        private static bool IsPercentUnitOverride(string? userFormat)
        {
            if (string.IsNullOrWhiteSpace(userFormat)) return false;

            return !userFormat.Contains("{u}", StringComparison.OrdinalIgnoreCase) &&
                   userFormat.Contains("%", StringComparison.Ordinal);
        }

        // =========================================================
        // ★ [Fix #137] 提交内存 ("MEM.Commit.Load") 的独立格式分支
        // =========================================================

        /// <summary>提交内存监控项的 Key（#137）。</summary>
        public const string CommittedMemoryKey = "MEM.Commit.Load";

        /// <summary>提交内存容量模式使用的**固定**单位。</summary>
        /// <remarks>
        /// ★ 为什么固定成 GB，而不是像既有内存项那样按数值自动换算（KB/MB/GB/TB）：
        ///   容量模式要同时显示"分子（提交量）+ 分母（当前提交上限）"两个量，
        ///   而 formatter 的签名（GetValueStr / GetUnitStr）只带一个 float，
        ///   数值与单位是**两次独立调用**。
        ///   若单位跟着快照里的分母自动换算，两次调用之间恰好发布了新一代次时，
        ///   就会出现「数值按 GB 算、单位却变成 TB」这种倍率不一致的读数。
        ///   单位固定成 GB 之后：
        ///     - 分子与分母必然同倍率（都是 GB），不会出现 "76 GB / 98304 MB"；
        ///     - 单位与数值**跨代次也不会错配**（单位是常量）；
        ///     - 提交量与提交上限本来就必然是 GB 量级，与任务管理器的口径一致。
        /// </remarks>
        public const string CommittedMemoryCapacityUnit = "GB";

        /// <summary>缺失读数的占位文本（★ 缺失不是 0）。</summary>
        private const string MissingValueText = "--";

        /// <summary>是否是"内存已提交"监控项。</summary>
        public static bool IsCommittedMemoryKey(string? key)
            => string.Equals(key, CommittedMemoryKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 取当前**已发布**的提交内存状态。
        /// ★ 只读发布点，**绝不触发任何系统调用**（绘制频率再高也不会变成系统调用频率）。
        /// </summary>
        private static CommittedMemoryDisplay? GetCommittedDisplay()
            => CommittedMemoryPublisher.Current;

        /// <summary>
        /// 提交内存的数值文本。
        /// </summary>
        /// <param name="value">调用方拿到的百分比（百分比口径下使用）。</param>
        /// <param name="userFormat">该监控项自己的单位格式（用于识别 "%" 覆盖）。</param>
        private static string GetCommittedMemoryValueStr(float value, string? userFormat)
        {
            CommittedMemoryDisplay? display = GetCommittedDisplay();

            // ★ 缺失不是 0：快照未就绪 / API 失败 / 上限为 0 / 溢出 一律显示 "--"，
            //   既不显示 0，也绝不回退成物理内存率。
            if (display == null || display.IsMissing) return MissingValueText;

            var cfg = Settings.Load();

            // 百分比口径（或用户显式要求 %）：直接用百分比，与其它百分比指标同规则。
            if (cfg.MemoryDisplayMode != 1 || IsPercentUnitOverride(userFormat))
            {
                return FormatValueAdaptive(value, -1);
            }

            // ★ 容量口径：分子 / 分母取自**同一个不可变状态对象**，
            //   因此必然同代次、同倍率（不会出现 "76 GB / 98304 MB" 这种倍率不一致）。
            //   分母是**当前提交上限**，不是物理内存总量。
            double committedGb = display.CommittedBytes / 1073741824.0;
            double limitGb = display.LimitBytes / 1073741824.0;
            string num = FormatValueAdaptive(committedGb, -1);
            string den = FormatValueAdaptive(limitGb, -1);
            return $"{num} / {den}";
        }

        /// <summary>
        /// 提交内存的单位文本。
        /// </summary>
        private static string GetCommittedMemoryUnitStr(UnitContext context)
        {
            var cfg = Settings.Load();
            bool settingsContext = context == UnitContext.SettingsPanel || context == UnitContext.SettingsTaskbar;

            if (!settingsContext)
            {
                // 缺失读数不带单位（否则会显示成 "--GB" / "--%" 这种自相矛盾的读数）
                CommittedMemoryDisplay? display = GetCommittedDisplay();
                if (display == null || display.IsMissing) return "";
            }

            if (cfg.MemoryDisplayMode != 1) return "%";

            // 设置界面的单位输入框展示的是"默认单位格式"，与实时读数无关。
            if (settingsContext) return "{u}";

            return CommittedMemoryCapacityUnit;
        }

        public static string GetDisplayUnit(string key, string calculatedUnit, string userFormat)
        {
            string result;
            if (string.IsNullOrEmpty(userFormat))
            {
                result = (userFormat != null) ? "" : calculatedUnit;   // Empty = Hide; null = 用默认单位
            }
            else
            {
                result = userFormat.Contains("{u}") ? userFormat.Replace("{u}", calculatedUnit) : userFormat;
            }

            // ★ [#166] 来源标记必须拼在**用户单位格式之后**：
            //   用户自定义单位（甚至把单位整个隐藏）不能把"这是 VID 参考值"这条信息抹掉。
            //   默认单位里已经带标记时不重复追加。
            if (IsCpuVoltageKey(key))
            {
                string marker = GetCpuVoltageSourceMarker();
                if (marker.Length > 0 && !result.Contains(marker, StringComparison.Ordinal)) result += marker;
            }

            return result;
        }

        // =========================================================
        // ★ [#166] CPU 电压：来源说明（独立于用户标签的通道）
        // =========================================================

        /// <summary>CPU 电压监控项的 Key。</summary>
        public const string CpuVoltageKey = "CPU.Voltage";

        /// <summary>是否是 CPU 电压监控项。</summary>
        public static bool IsCpuVoltageKey(string? key)
            => string.Equals(key, CpuVoltageKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 取来源类型对应的**短标记**（挂在单位后面）。
        /// ★ 只有"请求电压(VID)"需要标记：其它来源本来就是实测值，不需要额外提醒。
        /// ★ 空字符串 = 不加标记。
        /// </summary>
        public static string GetCpuVoltageSourceMarker(CpuVoltageResolution? resolution = null)
        {
            CpuVoltageResolution? r = resolution ?? CpuVoltageSourcePublisher.Current;
            if (r == null || !r.IsAvailable) return "";
            if (r.SourceKind != CpuVoltageSourceKind.RequestedVid) return "";
            return LanguageManager.T("CpuVoltage.MarkerReferenceOnly");
        }

        /// <summary>
        /// 取来源类型的**完整说明文案**（设置页 / 提示用）。
        /// ★ 走独立通道：用户自定义监控项名称、隐藏单位都覆盖不掉它。
        /// </summary>
        public static string GetCpuVoltageSourceKindLabel(CpuVoltageSourceKind kind)
        {
            switch (kind)
            {
                case CpuVoltageSourceKind.MeasuredCoreRail: return LanguageManager.T("CpuVoltage.Measured");
                case CpuVoltageSourceKind.MotherboardVcore: return LanguageManager.T("CpuVoltage.MotherboardVcore");
                case CpuVoltageSourceKind.RequestedVid: return LanguageManager.T("CpuVoltage.ReferenceOnly");
                case CpuVoltageSourceKind.Ambiguous: return LanguageManager.T("CpuVoltage.Ambiguous");
                case CpuVoltageSourceKind.Excluded: return LanguageManager.T("CpuVoltage.Excluded");
                default: return LanguageManager.T("CpuVoltage.Unavailable");
            }
        }

        /// <summary>
        /// 当前 CPU 电压的来源说明（完整文案）。
        /// ★ 没有数据时返回"来源不可用" —— 绝不返回空串, 免得界面上什么都不显示。
        /// </summary>
        public static string GetCpuVoltageSourceHint()
        {
            CpuVoltageResolution? r = CpuVoltageSourcePublisher.Current;
            if (r == null || !r.IsAvailable) return LanguageManager.T("CpuVoltage.Unavailable");
            return GetCpuVoltageSourceKindLabel(r.SourceKind);
        }
        // [Refactor] 统一数据大小格式化逻辑
        
        /// <summary>
        /// 将字节数转换为带单位的友好显示，返回数值字符串与单位字符串。
        /// </summary>
        public static (string val, string unit) FormatDataSizeParts(double bytes, int decimals = -1)
        {
            // [Optimization] Use cached array
            // string[] sizes = { "KB", "MB", "GB", "TB", "PB" };

            // 将字节转换为 KB 作为基准
            double len = bytes / 1024.0;

            // 逐级放大单位，直到数值小于 1000 或到达最大单位
            // [Optimize] 使用 1000 而不是 1024 作为阈值，避免出现 "1023 KB" 这种较长的字符串，提前转换为 "0.99 MB"
            int order = 0;
            while (len >= 1000 && order < _dataSizes.Length - 1)
            {
                order++;
                len /= 1024.0;
            }

            // 返回格式化后的数值与对应单位
            return (FormatValueAdaptive(len, decimals), _dataSizes[order]);
        }

        public static string FormatDataSize(double bytes, string suffix = "", int decimals = -1)
        {
            var (val, unit) = FormatDataSizeParts(bytes, decimals);
            return $"{val}{unit}{suffix}";
        }



        // =========================================================
        // 4. 评估逻辑 (原 MetricEvaluator)
        // =========================================================
        public static int GetState(string key, double value)
        {
            if (double.IsNaN(value)) return STATE_SAFE;
            if (key.StartsWith("BAT", StringComparison.OrdinalIgnoreCase) && value < 0) return STATE_SAFE;

            var type = GetType(key);
            double checkValue = value;

            // 对于动态范围的指标 (CPU频率, 功耗, 转速, FPS)，
            // 状态判断应该基于它们相对于"历史最大值"的百分比，而不是绝对数值。
            // 例如：如果 CPU 频率跑到了 历史最大值的 90%，就认为是高负载(红色)。
            // 否则 4.0GHz 可能永远触发不了阈值。
            if (type is MetricType.Frequency or MetricType.Power or MetricType.RPM or MetricType.FPS or MetricType.Voltage or MetricType.Current)
            {
                 // [Refactor] Use GetAdaptivePercentage directly since we are already inside the adaptive type check block
                 checkValue = GetAdaptivePercentage(key, value) * 100.0;

                 if (checkValue >= 90) return STATE_CRIT;
                 if (checkValue >= 80) return STATE_WARN;
                 return STATE_SAFE;
            }
            else if (type is MetricType.DataSpeed or MetricType.DataSize)
            {
                checkValue = value / 1048576.0; // To MB
            }

            var (warn, crit) = GetThresholds(key);

            // 电池低电量反向判断
            if (key.Equals("BAT.Percent", StringComparison.OrdinalIgnoreCase))
            {
                if (checkValue <= crit) return STATE_CRIT;
                if (checkValue <= warn) return STATE_WARN;
                return STATE_SAFE;
            }

            if (checkValue >= crit) return STATE_CRIT;
            if (checkValue >= warn) return STATE_WARN;

            return STATE_SAFE;
        }

        public static (double warn, double crit) GetThresholds(string key)
        {
            var cfg = Settings.Load();
            var th = cfg.Thresholds;
            var type = GetType(key);

            return type switch
            {
                MetricType.Temperature => (th.Temp.Warn, th.Temp.Crit),
                MetricType.DataSpeed => key.StartsWith("NET") 
                    ? (key.IndexOf("UP", StringComparison.OrdinalIgnoreCase) >= 0 ? (th.NetUpMB.Warn, th.NetUpMB.Crit) : (th.NetDownMB.Warn, th.NetDownMB.Crit))
                    : (th.DiskIOMB.Warn, th.DiskIOMB.Crit),
                MetricType.DataSize => (key.IndexOf("UP", StringComparison.OrdinalIgnoreCase) >= 0 ? (th.DataUpMB.Warn, th.DataUpMB.Crit) : (th.DataDownMB.Warn, th.DataDownMB.Crit)),
                MetricType.Percent => key.Equals("BAT.Percent", StringComparison.OrdinalIgnoreCase) ? (60, 20) : (th.Load.Warn, th.Load.Crit),
                _ => (th.Load.Warn, th.Load.Crit)
            };
        }

        /// <summary>
        /// 计算进度条显示值 (0.0 - 1.0)
        /// 包含自适应最大值逻辑和最小视觉宽度修正 (5%)
        /// </summary>
        public static double GetProgressValue(string key, double value)
        {
            // 1. 获取基础百分比 (自适应或归一化)
            // [Refactor] Inlined GetUnifiedPercent logic
            var type = GetType(key);
            double rawPct;
            
            if (type is MetricType.Frequency or MetricType.Power or MetricType.RPM or MetricType.FPS or MetricType.Voltage or MetricType.Current)
                rawPct = GetAdaptivePercentage(key, value);
            else
                rawPct = value / 100.0;

            // 2. 负值处理：电池**充电**时放电功率/电流为负（放电为正）。
            //    产品口径（已确认）：取绝对值，按实际幅度绘制进度条 ——
            //    充电 20W 就画 20% 长的条，而不是一律满条。
            //    满条只能看出「正在充电」，看不出充得多快，信息量更低。
            //    颜色不由这里决定：GetState 对 BAT 负值返回 STATE_SAFE，所以不会误判为告警。
            if (rawPct < 0) rawPct = Math.Abs(rawPct);

            // 3. 视觉修正：限制在 [0.05, 1.0] 之间
            // 确保即使数值很小也能看到一条细线
            return Math.Max(0.05, Math.Min(1.0, rawPct));
        }

        /// <summary>
        /// 取指定 Key 对应显卡的显存总容量 (GB)。
        /// "GPU.1.VRAM" -> 查 "GPU.1" ; "GPU.VRAM" -> 查 "GPU" (旧格式回退)
        /// </summary>
        private static double GetGpuVramTotalGB(string key)
        {
            string prefix = key ?? "";
            int vramPos = prefix.LastIndexOf("VRAM", StringComparison.OrdinalIgnoreCase);
            if (vramPos > 0) prefix = prefix.Substring(0, vramPos);
            prefix = prefix.TrimEnd('.');

            if (Settings.DetectedGpuVramTotalGBByKey.TryGetValue(prefix, out float total) && total > 0)
                return total;

            return Settings.DetectedGpuVramTotalGB; // 旧单卡缓存回退
        }

        public static double GetAdaptivePercentage(string key, double val)
        {
            var cfg = Settings.Load();
            float max = 1.0f;

            // ★★★ [多显卡] 带序号的 Key ("GPU.1.Power") 归一化成基础 Key ("GPU.Power") ★★★
            string baseKey = GpuKey.ToBaseKey(key);

            if (baseKey == "CPU.Clock") max = cfg.RecordedMaxCpuClock;
            else if (baseKey == "CPU.Power") max = cfg.RecordedMaxCpuPower;
            else if (baseKey == "GPU.Clock") max = cfg.RecordedMaxGpuClock;
            else if (baseKey == "GPU.Power") max = cfg.RecordedMaxGpuPower;
            else if (baseKey == "CPU.Fan") max = cfg.RecordedMaxCpuFan;
            else if (baseKey == "CPU.Pump") max = cfg.RecordedMaxCpuPump;
            else if (baseKey == "CASE.Fan") max = cfg.RecordedMaxChassisFan;
            else if (baseKey == "GPU.Fan") max = cfg.RecordedMaxGpuFan;
            else if (baseKey == "FPS") max = cfg.RecordedMaxFps;
            else if (baseKey == "CPU.Voltage") max = 1.6f;
            else if (baseKey == "BAT.Power") max = 100f;
            else if (baseKey == "BAT.Voltage") max = 20f;
            else if (baseKey == "BAT.Current") max = 5f;

            // ★★★ [多显卡] 该卡有独立峰值时优先使用, 让每张卡的进度条各自自适应 ★★★
            if (key != baseKey
                && cfg.RecordedMaxGpuDynamic != null
                && cfg.RecordedMaxGpuDynamic.TryGetValue(key, out float dynMax)
                && dynMax > 1f)
            {
                max = dynMax;
            }

            if (max < 1) max = 1;
            double pct = val / max;
            return pct > 1.0 ? 1.0 : pct;
        }
        
        // =========================================================
        // 5. 基础转换
        // =========================================================
        public static int ParseInt(string s) => int.TryParse(new string(s?.Where(c => char.IsDigit(c) || c == '-').ToArray()), out int v) ? v : 0;
        public static double ParseDouble(string s) => double.TryParse(new string(s?.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray()), out double v) ? v : 0;
        public static string ToStr(double v, string format = "F1") => v.ToString(format);
    }
}
