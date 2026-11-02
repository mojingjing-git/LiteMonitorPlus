using System;
using LiteMonitor;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 【行为 Fixture】数值格式与单位的"输入 → 输出"固化用例（派工单 P-B0b，Plan B §6.1 第一行）。
    ///
    /// ★ 全部期望值都是 2026-10-26 对 master `67afb8a` **实跑采集**后逐字固化的（抄题不出题），
    ///   包括那些"看起来怪"的输出（如 "1000KB"、"-0.00"、99.95f→"99.9"）。
    ///   B1/B2 迁移采集消费方、B4 拆分格式规则时，本文件**必须零修改通过**——
    ///   任何期望值的变化都等于行为变化，需在验收记录里单独说明并经裁决，
    ///   不允许"顺手修正"期望值。
    ///   ★ 疑似 bug 与浮点边界现象由本测试中的固定输入矩阵覆盖，
    ///   期望值由同一矩阵冻结；
    ///
    /// 全局状态约定（构造时应用、Dispose 时还原）：
    ///   - 阈值 Load 60/85、Temp 50/70、NetUp 1/2、NetDown 2/8、DiskIO 2/8、
    ///     DataUp 512/1024、DataDown 2048/5096；
    ///   - RecordedMax：CpuClock 4200 / CpuPower 200 / GpuPower 100 / SysPower 800 /
    ///     GpuClock 1800 / Fps 144 / CpuFan 4000 / GpuFan 3500 / CpuPump 5000 / ChassisFan 3000；
    ///   - MemoryDisplayMode=0、DetectedRamTotalGB=0、显存缓存清空；
    ///   - CpuVoltageSourcePublisher / CommittedMemoryPublisher 清空；语言 zh。
    ///
    /// 会改进程级状态（Settings 单例 + 发布点 + 语言），故入 "cfgfile" 串行集合。
    /// </summary>
    [Collection("cfgfile")]
    public class MetricFormatBehaviorFixtureTests : IDisposable
    {
        private readonly Settings _cfg;

        public MetricFormatBehaviorFixtureTests()
        {
            LanguageManager.Load("zh");
            _cfg = Settings.Load();

            _oldThresholds = _cfg.Thresholds;
            _oldMode = _cfg.MemoryDisplayMode;
            _oldRam = Settings.DetectedRamTotalGB;
            _oldVram = Settings.DetectedGpuVramTotalGB;
            _oldMax = (_cfg.RecordedMaxCpuClock, _cfg.RecordedMaxCpuPower, _cfg.RecordedMaxGpuPower,
                       _cfg.RecordedMaxSysPower, _cfg.RecordedMaxGpuClock, _cfg.RecordedMaxFps,
                       _cfg.RecordedMaxCpuFan, _cfg.RecordedMaxGpuFan, _cfg.RecordedMaxCpuPump,
                       _cfg.RecordedMaxChassisFan);

            _cfg.Thresholds = new ThresholdsSet
            {
                Load = new ValueRange { Warn = 60, Crit = 85 },
                Temp = new ValueRange { Warn = 50, Crit = 70 },
                DiskIOMB = new ValueRange { Warn = 2, Crit = 8 },
                NetUpMB = new ValueRange { Warn = 1, Crit = 2 },
                NetDownMB = new ValueRange { Warn = 2, Crit = 8 },
                DataUpMB = new ValueRange { Warn = 512, Crit = 1024 },
                DataDownMB = new ValueRange { Warn = 2048, Crit = 5096 },
            };
            _cfg.RecordedMaxCpuClock = 4200f;
            _cfg.RecordedMaxCpuPower = 200f;
            _cfg.RecordedMaxGpuPower = 100f;
            _cfg.RecordedMaxSysPower = 800f;
            _cfg.RecordedMaxGpuClock = 1800f;
            _cfg.RecordedMaxFps = 144f;
            _cfg.RecordedMaxCpuFan = 4000f;
            _cfg.RecordedMaxGpuFan = 3500f;
            _cfg.RecordedMaxCpuPump = 5000f;
            _cfg.RecordedMaxChassisFan = 3000f;
            _cfg.MemoryDisplayMode = 0;
            Settings.DetectedRamTotalGB = 0f;
            Settings.DetectedGpuVramTotalGB = 0f;
            Settings.DetectedGpuVramTotalGBByKey.Clear();

            CpuVoltageSourcePublisher.Clear();
            CommittedMemoryPublisher.Clear();
        }

        private readonly ThresholdsSet _oldThresholds;
        private readonly int _oldMode;
        private readonly float _oldRam;
        private readonly float _oldVram;
        private readonly (float, float, float, float, float, float, float, float, float, float) _oldMax;

        public void Dispose()
        {
            _cfg.Thresholds = _oldThresholds;
            (_cfg.RecordedMaxCpuClock, _cfg.RecordedMaxCpuPower, _cfg.RecordedMaxGpuPower,
             _cfg.RecordedMaxSysPower, _cfg.RecordedMaxGpuClock, _cfg.RecordedMaxFps,
             _cfg.RecordedMaxCpuFan, _cfg.RecordedMaxGpuFan, _cfg.RecordedMaxCpuPump,
             _cfg.RecordedMaxChassisFan) = _oldMax;
            _cfg.MemoryDisplayMode = _oldMode;
            Settings.DetectedRamTotalGB = _oldRam;
            Settings.DetectedGpuVramTotalGB = _oldVram;
            Settings.DetectedGpuVramTotalGBByKey.Clear();
            CpuVoltageSourcePublisher.Clear();
            CommittedMemoryPublisher.Clear();
            LanguageManager.ClearOverrides();
            LanguageManager.Load("zh");
        }

        /// <summary>按 MetricItem 的真实调用链拼最终显示文本（值 + 单位）。</summary>
        private static string Compose(string key, float? value, MetricUtils.UnitContext ctx, string? userFormat)
        {
            string val = MetricUtils.GetValueStr(key, value, false, userFormat);
            string unit = MetricUtils.GetUnitStr(key, value, ctx);
            return val + MetricUtils.GetDisplayUnit(key, unit, userFormat);
        }

        // =========================================================
        // 1. GetValueStr：正常/紧凑格式 × 数值域（0、null、负、极小、极大、边界）
        // =========================================================

        [Theory]
        [InlineData("CPU.Load", 45.678, false, null, "45.7")]
        [InlineData("CPU.Load", 45.678, true, null, "45.7")]     // ★ compact 与非 compact 输出一致（compact 形参当前不参与格式化）
        [InlineData("CPU.Load", 0.0, false, null, "0.00")]       // 真实 0
        [InlineData("CPU.Load", null, false, null, "0.00")]      // 缺失(null)按 0 显示（CPU.Voltage/MEM.Commit 之外的一般键）
        [InlineData("CPU.Load", 100.0, false, null, "100")]
        [InlineData("CPU.Load", 99.95, false, null, "99.9")]     // ★ float(99.95) < double(99.95)：切换点落不到
        [InlineData("CPU.Load", 99.96, false, null, "100")]
        [InlineData("CPU.Load", 9.994, false, null, "9.99")]
        [InlineData("CPU.Load", 9.995, false, null, "9.99")]     // ★ 同上，9.995f 边界落不到
        [InlineData("CPU.Temp", -5.5, false, null, "-5.50")]     // 负温度
        [InlineData("CPU.Temp", -0.001, false, null, "-0.00")]   // ★ 负零显示
        [InlineData("CPU.Temp", 0.004, false, null, "0.00")]
        [InlineData("CPU.Temp", 1E+12, false, null, "999999995904")] // ★ float(1e12) 全数字展开
        [InlineData("CPU.Clock", 3600000.0, false, null, "3600.0")]  // mHz → GHz，F1
        [InlineData("CPU.Clock", 125000.0, false, null, "125.0")]
        [InlineData("CPU.Clock", null, false, null, "0.0")]
        [InlineData("CPU.Clock", 0.0, false, null, "0.0")]
        [InlineData("GPU.Fan", 1850.0, false, null, "1850")]     // RPM 整数
        [InlineData("GPU.Fan", 1850.0, true, null, "1850")]
        [InlineData("FPS", 144.0, false, null, "144")]
        [InlineData("FPS", 59.9, false, null, "60")]             // 四舍五入进位
        [InlineData("BAT.Power", -20.5, false, null, "-20.50")]  // 放电为负
        [InlineData("BAT.Voltage", 12.876, false, null, "12.9")]
        [InlineData("BAT.Current", 3.251, false, null, "3.25")]
        [InlineData("SYS.Power", 456.78, false, null, "457")]    // ≥100 取整
        [InlineData("NET.Down", 15728640.0, false, null, "15.0")]     // 15 MiB
        [InlineData("NET.Down", 15728640.0, true, null, "15.0")]      // ★ compact 同输出
        [InlineData("NET.Down", 0.0, false, null, "0.00")]
        [InlineData("NET.Down", null, false, null, "0.00")]
        [InlineData("NET.Down", 1E+12, false, null, "931")]      // 极大 → 931GB（<1000 不进 TB）
        [InlineData("DATA.DayDown", 1048576.0, false, null, "1.00")]
        [InlineData("DISK.Read", 1023.0, false, null, "1.00")]   // ★ 0.999KB 舍入显示 "1.00"
        [InlineData("MOBO.Temp", -12.34, false, null, "-12.34")]
        public void FIX_GetValueStr_正常与紧凑_数值域(string key, double? value, bool compact, string? userFormat, string expected)
        {
            Assert.Equal(expected, MetricUtils.GetValueStr(key, (float?)value, compact, userFormat));
        }

        /// <summary>compact 形参在整个类型表上不改变输出（现状固化；修复需另行裁决，见 B0b 报告疑似清单 #1）。</summary>
        [Theory]
        [InlineData("CPU.Load", 45.678)]
        [InlineData("CPU.Temp", -5.5)]
        [InlineData("GPU.Fan", 1850.0)]
        [InlineData("NET.Down", 15728640.0)]
        [InlineData("CPU.Clock", 125000.0)]
        public void FIX_GetValueStr_compact形参不改变输出(string key, double value)
        {
            string normal = MetricUtils.GetValueStr(key, (float)value, false, null);
            string compact = MetricUtils.GetValueStr(key, (float)value, true, null);
            Assert.Equal(normal, compact);
        }

        // =========================================================
        // 2. GetValueStr：内存口径（百分比/容量/显存/多卡显存/百分比覆盖）
        // =========================================================

        [Theory]
        [InlineData(0, 32f, 0f, null, 0f, "MEM.Load", 50.0, null, "50.0")]
        [InlineData(1, 32f, 0f, null, 0f, "MEM.Load", 50.0, null, "16.0")]
        [InlineData(1, 32f, 0f, null, 0f, "MEM.Load", 50.0, "%", "50.0")]     // 单项 % 覆盖：不换算
        [InlineData(1, 32f, 0f, null, 0f, "MEM.Load", 50.0, "{u}", "16.0")]  // 含 {u}：全局口径优先
        [InlineData(1, 0f, 0f, null, 0f, "MEM.Load", 50.0, null, "50.0")]    // 容量未知回退百分比
        [InlineData(1, 32f, 8f, null, 0f, "GPU.VRAM", 50.0, null, "4.00")]
        [InlineData(1, 32f, 0f, "GPU.1", 16f, "GPU.1.VRAM", 50.0, null, "8.00")] // 多卡按 Key 各自查容量
        [InlineData(1, 32f, 0f, null, 0f, "MEM.Load", 0.0, null, "0.00")]    // 0 是有效数据
        [InlineData(1, 32f, 0f, null, 0f, "MEM.Load", null, null, "0.00")]
        [InlineData(1, 32f, 0f, null, 0f, "MEM.Load", 100.0, null, "32.0")]
        public void FIX_GetValueStr_内存口径(int mode, float ram, float vram, string? vramKey, float vramVal,
                                             string key, double? value, string? userFormat, string expected)
        {
            _cfg.MemoryDisplayMode = mode;
            Settings.DetectedRamTotalGB = ram;
            Settings.DetectedGpuVramTotalGB = vram;
            Settings.DetectedGpuVramTotalGBByKey.Clear();
            if (vramKey != null) Settings.DetectedGpuVramTotalGBByKey[vramKey] = vramVal;

            Assert.Equal(expected, MetricUtils.GetValueStr(key, (float?)value, false, userFormat));
        }

        // =========================================================
        // 3. GetUnitStr：单位表 × 四种上下文
        // =========================================================

        [Theory]
        [InlineData("CPU.Load", 45.6, (int)MetricUtils.UnitContext.Panel, "%")]
        [InlineData("CPU.Load", 45.6, (int)MetricUtils.UnitContext.Taskbar, "%")]
        [InlineData("CPU.Temp", 45.0, (int)MetricUtils.UnitContext.Panel, "\u00B0C")]
        [InlineData("CPU.Clock", 3600.0, (int)MetricUtils.UnitContext.Panel, "GHz")]
        [InlineData("GPU.Fan", 1850.0, (int)MetricUtils.UnitContext.Panel, " RPM")]
        [InlineData("GPU.Fan", 1850.0, (int)MetricUtils.UnitContext.Taskbar, "R")]
        [InlineData("FPS", 144.0, (int)MetricUtils.UnitContext.Panel, " FPS")]
        [InlineData("FPS", 144.0, (int)MetricUtils.UnitContext.Taskbar, "F")]
        [InlineData("BAT.Percent", 50.0, (int)MetricUtils.UnitContext.SettingsPanel, "%")]
        [InlineData("BAT.Power", 20.0, (int)MetricUtils.UnitContext.SettingsPanel, "W")]
        [InlineData("BAT.Voltage", 12.8, (int)MetricUtils.UnitContext.SettingsPanel, "V")]
        [InlineData("NET.Down", 15728640.0, (int)MetricUtils.UnitContext.Panel, "MB/s")]
        [InlineData("NET.Down", 15728640.0, (int)MetricUtils.UnitContext.Taskbar, "MB")]
        [InlineData("NET.Down", 15728640.0, (int)MetricUtils.UnitContext.SettingsPanel, "{u}/s")]
        [InlineData("NET.Down", 15728640.0, (int)MetricUtils.UnitContext.SettingsTaskbar, "{u}")]
        [InlineData("DATA.DayDown", 1048576.0, (int)MetricUtils.UnitContext.Panel, "MB")]     // 动态单位随值换算
        [InlineData("DATA.DayDown", 1073741824.0, (int)MetricUtils.UnitContext.Panel, "GB")]
        [InlineData("DATA.DayDown", 1048576.0, (int)MetricUtils.UnitContext.SettingsPanel, "{u}")]
        [InlineData("", 5.0, (int)MetricUtils.UnitContext.Panel, "")]                          // 空 Key → Unknown 无单位
        [InlineData("UNKNOWN.XYZ", 5.0, (int)MetricUtils.UnitContext.Panel, "")]               // 未登记类型无单位
        [InlineData("SYS.Power", 500.0, (int)MetricUtils.UnitContext.Panel, "W")]
        public void FIX_GetUnitStr_单位表与上下文(string key, double? value, int context, string expected)
        {
            Assert.Equal(expected, MetricUtils.GetUnitStr(key, (float?)value, (MetricUtils.UnitContext)context));
        }

        /// <summary>
        /// BAT 键在 Panel/Taskbar 上下文的单位带"充电中"后缀，依赖**宿主机实时电源状态**
        /// （GetPowerStatus 直读 SystemInformation，无注入点）——无法跨机器逐字固化，
        /// 仅固化"单位本体 + 至多一个 + 后缀"这一约束。采集机（台式未充电）实测为 "%" / "W"。
        /// </summary>
        [Theory]
        [InlineData("BAT.Percent", 50.0, "%")]
        [InlineData("BAT.Power", 20.0, "W")]
        public void FIX_GetUnitStr_电池Panel单位_受宿主电源状态影响(string key, double value, string baseUnit)
        {
            string unit = MetricUtils.GetUnitStr(key, (float)value, MetricUtils.UnitContext.Panel);
            Assert.True(unit == baseUnit || unit == baseUnit + "+",
                $"期望 {baseUnit} 或 {baseUnit}+，实际 {unit}（后缀取决于宿主机是否正在充电）");
        }

        [Theory]
        [InlineData(1, 32f, "MEM.Load", 50.0, (int)MetricUtils.UnitContext.Panel, "GB")]
        [InlineData(1, 32f, "MEM.Load", 50.0, (int)MetricUtils.UnitContext.SettingsPanel, "{u}")]
        [InlineData(1, 0f, "MEM.Load", 50.0, (int)MetricUtils.UnitContext.Panel, "%")] // 容量未知回退 %
        [InlineData(0, 32f, "MEM.Load", 50.0, (int)MetricUtils.UnitContext.Panel, "%")]
        public void FIX_GetUnitStr_内存口径(int mode, float ram, string key, double? value, int context, string expected)
        {
            _cfg.MemoryDisplayMode = mode;
            Settings.DetectedRamTotalGB = ram;
            Assert.Equal(expected, MetricUtils.GetUnitStr(key, (float?)value, (MetricUtils.UnitContext)context));
        }

        // =========================================================
        // 4. FormatDataSizeParts / FormatDataSize：千位进制边界 1023/1024/1023999/1048576 + 数值域
        // =========================================================

        [Theory]
        [InlineData(1023.0, -1, "1.00", "KB")]            // ★ 0.999KB 舍入显示 1.00
        [InlineData(1024.0, -1, "1.00", "KB")]
        [InlineData(1023999.0, -1, "1000", "KB")]         // ★ 999.999KB 舍入成 "1000KB"（见报告疑似 #4）
        [InlineData(1048576.0, -1, "1.00", "MB")]
        [InlineData(0.0, -1, "0.00", "KB")]
        [InlineData(1.0, -1, "0.00", "KB")]
        [InlineData(1E+12, -1, "931", "GB")]
        [InlineData(1073741824.0, -1, "1.00", "GB")]
        [InlineData(1099511627776.0, -1, "1.00", "TB")]
        [InlineData(1024.0, 0, "1", "KB")]
        [InlineData(1023999.0, 0, "1000", "KB")]
        [InlineData(15728640.0, 1, "15.0", "MB")]
        [InlineData(-1024.0, -1, "-1.00", "KB")]          // 负值（上传/下载冲销等场景）
        public void FIX_FormatDataSizeParts_千位进制与数值域(double bytes, int decimals, string expectedVal, string expectedUnit)
        {
            var (val, unit) = MetricUtils.FormatDataSizeParts(bytes, decimals);
            Assert.Equal(expectedVal, val);
            Assert.Equal(expectedUnit, unit);
        }

        [Theory]
        [InlineData(1536.0, "/s", -1, "1.50KB/s")]
        [InlineData(1048576.0, "", -1, "1.00MB")]
        [InlineData(0.0, "", -1, "0.00KB")]
        [InlineData(1023.0, "B", 0, "1KBB")]
        public void FIX_FormatDataSize_带后缀(double bytes, string suffix, int decimals, string expected)
        {
            Assert.Equal(expected, MetricUtils.FormatDataSize(bytes, suffix, decimals));
        }

        // =========================================================
        // 5. GetState：阈值边界（warn/crit 两侧 + 自适应类 80%/90% + 电池反向）
        // =========================================================

        [Theory]
        [InlineData("CPU.Load", 59.99, 0)]
        [InlineData("CPU.Load", 60.0, 1)]
        [InlineData("CPU.Load", 84.99, 1)]
        [InlineData("CPU.Load", 85.0, 2)]
        [InlineData("CPU.Load", 100.0, 2)]
        [InlineData("CPU.Load", double.NaN, 0)]
        [InlineData("CPU.Temp", 49.99, 0)]
        [InlineData("CPU.Temp", 50.0, 1)]
        [InlineData("CPU.Temp", 69.99, 1)]
        [InlineData("CPU.Temp", 70.0, 2)]
        [InlineData("BAT.Percent", 19.99, 2)]   // 电池反向：低电量告警（阈值硬编码 60/20）
        [InlineData("BAT.Percent", 20.0, 2)]
        [InlineData("BAT.Percent", 21.0, 1)]
        [InlineData("BAT.Percent", 60.0, 1)]
        [InlineData("BAT.Percent", 60.01, 0)]
        [InlineData("BAT.Percent", 100.0, 0)]
        [InlineData("BAT.Percent", -5.0, 0)]    // 电池负值（异常读数）不告警
        [InlineData("CPU.Clock", 3359.99, 0)]   // 频率按历史峰值(4200)归一：80% = 3360
        [InlineData("CPU.Clock", 3360.0, 1)]
        [InlineData("CPU.Clock", 3779.99, 1)]
        [InlineData("CPU.Clock", 3780.0, 2)]    // 90% = 3780
        [InlineData("CPU.Power", 159.99, 0)]    // 峰值 200：80% = 160
        [InlineData("CPU.Power", 160.0, 1)]
        [InlineData("CPU.Power", 180.0, 2)]
        [InlineData("GPU.Power", 79.99, 0)]     // 峰值 100
        [InlineData("GPU.Power", 80.0, 1)]
        [InlineData("GPU.Power", 90.0, 2)]
        [InlineData("GPU.Fan", 2799.99, 0)]     // 峰值 3500：80% = 2800
        [InlineData("GPU.Fan", 2800.0, 1)]
        [InlineData("GPU.Fan", 3150.0, 2)]
        [InlineData("FPS", 115.19, 0)]          // 峰值 144：80% ≈ 115.2
        [InlineData("FPS", 115.2, 1)]
        [InlineData("FPS", 129.6, 1)]           // ★ 129.6/144 = 0.89999…：90% 临界因浮点落不到 CRIT
        [InlineData("CPU.Voltage", 1.279, 0)]   // 固定峰值 1.6V
        [InlineData("CPU.Voltage", 1.28, 0)]    // ★ 1.28/1.6f = 0.79999995：80% 临界落不到 WARN
        [InlineData("CPU.Voltage", 1.44, 1)]
        [InlineData("BAT.Current", 3.99, 0)]    // 固定峰值 5A
        [InlineData("BAT.Current", 4.0, 1)]
        [InlineData("BAT.Current", 4.5, 2)]
        [InlineData("NET.Down", 2097151.0, 0)]  // MB 换算：2MB = 2097152（阈值 NetDown 2/8）
        [InlineData("NET.Down", 2097152.0, 1)]
        [InlineData("NET.Down", 8388608.0, 2)]
        [InlineData("NET.Up", 1048575.0, 0)]    // 阈值 NetUp 1/2
        [InlineData("NET.Up", 1048576.0, 1)]
        [InlineData("DISK.Read", 2097152.0, 1)] // 阈值 DiskIO 2/8
        [InlineData("DATA.DayDown", 2147483648.0, 1)]  // 阈值 DataDown 2048/5096（MB）
        [InlineData("DATA.DayDown", 5343543296.0, 2)]
        [InlineData("DATA.DayDown", 2145386496.0, 0)]
        public void FIX_GetState_阈值边界(string key, double value, int expected)
        {
            Assert.Equal(expected, MetricUtils.GetState(key, value));
        }

        /// <summary>
        /// 【非产品大小写观察行】GetAdaptivePercentage 的 RecordedMax 匹配是**区分大小写**的
        /// （GetType 则是 OrdinalIgnoreCase，同函数族口径不一致）：
        /// 大小写失配 → 匹配不到峰值 → max=1 → 100% → 恒 CRIT。
        /// 产品默认 Key 均为 PascalCase（CPU.Clock/GPU.Power）不受影响，
        /// 但手写配置/外部注入的变体 Key 会直接恒红。固化现状，修复另行裁决（报告疑似 #3）。
        /// </summary>
        [Theory]
        [InlineData("CPU.CLOCK", 3359.99, 2)]
        [InlineData("GPU.POWER", 79.99, 2)]
        public void FIX_GetState_非产品大小写_峰值匹配失配恒严重(string key, double value, int expected)
        {
            Assert.Equal(expected, MetricUtils.GetState(key, value));
        }

        // =========================================================
        // 6. GetDisplayUnit：用户单位/自定义格式（null=默认、空=隐藏、{u}=占位替换）
        // =========================================================

        [Theory]
        [InlineData("CPU.Load", "%", null, "%")]
        [InlineData("CPU.Load", "%", "", "")]                 // 空格式 = 隐藏单位
        [InlineData("CPU.Load", "%", "%", "%")]
        [InlineData("CPU.Load", "%", "{u}", "%")]
        [InlineData("CPU.Load", "%", " {u} used", " % used")]
        [InlineData("NET.Down", "MB/s", "{u}/s", "MB/s/s")]   // ★ 计算单位已含 /s 时出现双 /s（报告疑似 #2）
        [InlineData("MEM.Load", "GB", "GiB {u}", "GiB GB")]
        [InlineData("CPU.Load", "%", "pct", "pct")]
        [InlineData("CPU.Temp", "\u00B0C", null, "\u00B0C")]
        [InlineData("CPU.Temp", "\u00B0C", "{u}??", "\u00B0C??")]
        public void FIX_GetDisplayUnit_用户单位格式(string key, string calculatedUnit, string? userFormat, string expected)
        {
            Assert.Equal(expected, MetricUtils.GetDisplayUnit(key, calculatedUnit, userFormat));
        }

        // =========================================================
        // 7. CPU.Voltage：数值与来源同发布点（#166）
        // =========================================================

        private static void PublishVid(double? v, int gen = 1, CpuVoltageSourceKind kind = CpuVoltageSourceKind.RequestedVid)
            => CpuVoltageSourcePublisher.Publish(new CpuVoltageResolution
            {
                Generation = gen, IsAvailable = true, IsValid = true,
                IsReferenceOnly = kind == CpuVoltageSourceKind.RequestedVid,
                Value = v, SourceKind = kind
            });

        [Fact]
        public void FIX_CpuVoltage_VID发布_数值取发布点忽略传入值_单位带来源标记()
        {
            PublishVid(1.225);

            // 传入 99 也被忽略：数值必须来自已发布解析结果
            Assert.Equal("1.23", MetricUtils.GetValueStr("CPU.Voltage", 99f, false, null));
            Assert.Equal("V (CPU VID)", MetricUtils.GetUnitStr("CPU.Voltage", null, MetricUtils.UnitContext.Panel));
            Assert.Equal(" (CPU VID)", MetricUtils.GetDisplayUnit("CPU.Voltage", "V", ""));      // 隐藏单位也抹不掉标记
            Assert.Equal("V (CPU VID)", MetricUtils.GetDisplayUnit("CPU.Voltage", "V", null));
            Assert.Equal("Volts V (CPU VID)", MetricUtils.GetDisplayUnit("CPU.Voltage", "V", "Volts {u}"));
        }

        [Fact]
        public void FIX_CpuVoltage_实测来源_不带标记()
        {
            PublishVid(1.182, gen: 2, kind: CpuVoltageSourceKind.MeasuredCoreRail);

            Assert.Equal("1.18", MetricUtils.GetValueStr("CPU.Voltage", 0f, false, null));
            Assert.Equal("V", MetricUtils.GetUnitStr("CPU.Voltage", null, MetricUtils.UnitContext.Panel));
        }

        [Fact]
        public void FIX_CpuVoltage_VID但无值_数值缺失单位仍带标记()
        {
            // Value=null：读数缺失显示 "--"，但来源通道仍在 → 单位保留标记
            PublishVid(null, gen: 3);

            Assert.Equal("--", MetricUtils.GetValueStr("CPU.Voltage", 1.2f, false, null));
            Assert.Equal("V (CPU VID)", MetricUtils.GetUnitStr("CPU.Voltage", null, MetricUtils.UnitContext.Panel));
        }

        [Fact]
        public void FIX_CpuVoltage_来源不可用_缺失且无单位无标记()
        {
            CpuVoltageSourcePublisher.Publish(new CpuVoltageResolution
            {
                Generation = 4, IsAvailable = false, IsValid = false,
                IsReferenceOnly = false, Value = 1.1, SourceKind = CpuVoltageSourceKind.Excluded
            });

            Assert.Equal("--", MetricUtils.GetValueStr("CPU.Voltage", 1.1f, false, null));
            Assert.Equal("", MetricUtils.GetUnitStr("CPU.Voltage", null, MetricUtils.UnitContext.Panel));
        }

        [Fact]
        public void FIX_CpuVoltage_从未发布_缺失且无单位()
        {
            CpuVoltageSourcePublisher.Clear();

            Assert.Equal("--", MetricUtils.GetValueStr("CPU.Voltage", 1.1f, false, null));
            Assert.Equal("", MetricUtils.GetUnitStr("CPU.Voltage", null, MetricUtils.UnitContext.Panel));
            Assert.Equal("V", MetricUtils.GetDisplayUnit("CPU.Voltage", "V", null)); // 标记只随发布点走
        }

        // =========================================================
        // 8. MEM.Commit.Load：提交内存独立分支（#137）
        // =========================================================

        private static void PublishCommit(long committedBytes, long limitBytes, int gen)
        {
            var raw = new CommittedMemoryRaw(4096, (ulong)(committedBytes / 4096), (ulong)(limitBytes / 4096));
            CommittedMemoryPublisher.Publish(gen, CommittedMemoryCalculator.Compute(raw, gen, 1000));
        }

        [Fact]
        public void FIX_CommittedMemory_容量模式_分子分母同快照固定GB()
        {
            PublishCommit(48L * 1024 * 1024 * 1024, 64L * 1024 * 1024 * 1024, 1);
            _cfg.MemoryDisplayMode = 1;
            Settings.DetectedRamTotalGB = 32;

            Assert.Equal("48.0 / 64.0", MetricUtils.GetValueStr("MEM.Commit.Load", 75f, false, null));
            Assert.Equal("GB", MetricUtils.GetUnitStr("MEM.Commit.Load", 75f, MetricUtils.UnitContext.Panel));
        }

        [Fact]
        public void FIX_CommittedMemory_百分比模式_用传入百分比()
        {
            PublishCommit(48L * 1024 * 1024 * 1024, 64L * 1024 * 1024 * 1024, 1);
            _cfg.MemoryDisplayMode = 0;

            Assert.Equal("75.0", MetricUtils.GetValueStr("MEM.Commit.Load", 75f, false, null));
            Assert.Equal("%", MetricUtils.GetUnitStr("MEM.Commit.Load", 75f, MetricUtils.UnitContext.Panel));
        }

        [Fact]
        public void FIX_CommittedMemory_未发布_缺失且无单位()
        {
            _cfg.MemoryDisplayMode = 1;

            Assert.Equal("--", MetricUtils.GetValueStr("MEM.Commit.Load", 75f, false, null));
            Assert.Equal("", MetricUtils.GetUnitStr("MEM.Commit.Load", 75f, MetricUtils.UnitContext.Panel));
        }

        // =========================================================
        // 9. 多语言：来源标记走 LanguageManager 通道
        // =========================================================

        [Fact]
        public void FIX_多语言_九包标记一致_覆盖通道生效()
        {
            PublishVid(1.225);

            // 9 个语言包的 MarkerReferenceOnly 文案一致（" (CPU VID)"）——切换语言不改输出
            LanguageManager.Load("en");
            Assert.Equal("V (CPU VID)", MetricUtils.GetUnitStr("CPU.Voltage", null, MetricUtils.UnitContext.Panel));
            LanguageManager.Load("ja");
            Assert.Equal("V (CPU VID)", MetricUtils.GetUnitStr("CPU.Voltage", null, MetricUtils.UnitContext.Panel));
            LanguageManager.Load("zh");
            Assert.Equal("V (CPU VID)", MetricUtils.GetUnitStr("CPU.Voltage", null, MetricUtils.UnitContext.Panel));

            // 用户自定义覆盖经 LanguageManager.T 透传到标记（证明 i18n 通道接线）
            LanguageManager.SetOverride("CpuVoltage.MarkerReferenceOnly", " [VID\u6807\u8bb0]");
            Assert.Equal("V [VID\u6807\u8bb0]", MetricUtils.GetUnitStr("CPU.Voltage", null, MetricUtils.UnitContext.Panel));
            Assert.Equal(" [VID\u6807\u8bb0]", MetricUtils.GetDisplayUnit("CPU.Voltage", "V", ""));

            LanguageManager.ClearOverrides();
            Assert.Equal("V (CPU VID)", MetricUtils.GetUnitStr("CPU.Voltage", null, MetricUtils.UnitContext.Panel));

            // 词典缺 key 回退后缀
            Assert.Equal("NoSuchKey", LanguageManager.T("CpuVoltage.NoSuchKey"));
        }

        // =========================================================
        // 10. MetricItem 组装路径：GetFormattedText / CachedValueText / CachedUnitText
        // =========================================================

        private static string ItemText(string key, double displayValue, string? unitPanel, string? unitTaskbar,
                                       bool horizontal, string? textValue = null)
        {
            var item = new MetricItem
            {
                Key = key,
                BoundConfig = new MonitorItemConfig { Key = key, UnitPanel = unitPanel, UnitTaskbar = unitTaskbar },
                DisplayValue = (float)displayValue,
                TextValue = textValue
            };
            return item.GetFormattedText(horizontal);
        }

        [Theory]
        [InlineData("CPU.Load", 45.678, false, "45.7%")]
        [InlineData("CPU.Load", 45.678, true, "45.7%")]
        [InlineData("CPU.Temp", -5.5, false, "-5.50\u00B0C")]
        [InlineData("CPU.Clock", 3600000.0, false, "3600.0GHz")]
        [InlineData("GPU.Fan", 1850.0, false, "1850 RPM")]
        [InlineData("GPU.Fan", 1850.0, true, "1850R")]
        [InlineData("NET.Down", 15728640.0, false, "15.0MB/s")]
        [InlineData("NET.Down", 15728640.0, true, "15.0MB")]
        [InlineData("FPS", 144.0, false, "144 FPS")]
        [InlineData("FPS", 144.0, true, "144F")]
        [InlineData("SYS.Power", 456.78, false, "457W")]
        public void FIX_MetricItem_组装_标准类型与上下文(string key, double displayValue, bool horizontal, string expected)
        {
            Assert.Equal(expected, ItemText(key, displayValue, null, null, horizontal));
        }

        [Theory]
        [InlineData("CPU.Load", 45.678, "{u}!", false, "45.7%!")]
        [InlineData("CPU.Load", 45.678, "", false, "45.7")]
        public void FIX_MetricItem_组装_用户单位格式(string key, double displayValue, string unitPanel, bool horizontal, string expected)
        {
            Assert.Equal(expected, ItemText(key, displayValue, unitPanel, null, horizontal));
        }

        [Fact]
        public void FIX_MetricItem_组装_内存容量模式()
        {
            _cfg.MemoryDisplayMode = 1;
            Settings.DetectedRamTotalGB = 32;

            Assert.Equal("16.0GB", ItemText("MEM.Load", 50, null, null, false));
            // 任务栏单项 % 覆盖：数值回百分比、单位显示 %
            Assert.Equal("50.0%", ItemText("MEM.Load", 50, null, "%", true));
        }

        [Theory]
        [InlineData("CPU.Load", "hello", "hello%")]  // 插件文本缺单位时自动补默认单位
        [InlineData("CPU.Load", "99%", "99%")]       // 已含单位不重复追加
        [InlineData("", "abc", "abc")]               // Unknown 类型无单位
        [InlineData("DASH.Time", "12:34", "12:34")]  // DASH 项 InfoService 无单位
        public void FIX_MetricItem_组装_TextValue路径(string key, string textValue, string expected)
        {
            Assert.Equal(expected, ItemText(key, 0, null, null, false, textValue));
        }

        [Fact]
        public void FIX_MetricItem_缓存_微小变化不刷新_越阈值刷新()
        {
            var item = new MetricItem
            {
                Key = "CPU.Load",
                BoundConfig = new MonitorItemConfig { Key = "CPU.Load" },
                DisplayValue = 9.994f
            };
            Assert.Equal("9.99%", item.GetFormattedText(false));

            item.DisplayValue = 9.996f;   // Δ=0.002 < 0.005 缓存阈值：文本不更新
            Assert.Equal("9.99%", item.GetFormattedText(false));

            item.DisplayValue = 10.004f;  // Δ=0.01 > 0.005：重算并跨过 9.995 显示边界
            Assert.Equal("10.0%", item.GetFormattedText(false));
        }

        [Fact]
        public void FIX_MetricItem_CachedValueText与CachedUnitText分离暴露()
        {
            var item = new MetricItem
            {
                Key = "GPU.Fan",
                BoundConfig = new MonitorItemConfig { Key = "GPU.Fan" },
                DisplayValue = 1850f
            };
            item.GetFormattedText(false);

            Assert.Equal("1850", item.CachedValueText);
            Assert.Equal(" RPM", item.CachedUnitText);
        }
    }
}
