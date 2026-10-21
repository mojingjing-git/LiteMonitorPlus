using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// CPU 电压候选的来源类型。
    ///
    /// 为什么要有这个分类:
    ///   "CPU 电压" 这个指标在 LHM 里可能落到完全不同的物理量上 ——
    ///   有的是 VRM 测出来的核心轨电压, 有的是处理器上报的当前工作电压,
    ///   还有的只是处理器**请求**的电压码 (VID)。三者数值接近、量纲相同,
    ///   混在一起显示就会把"请求值"说成"实测值", 这是 #166 最容易踩的坑。
    /// </summary>
    public enum CpuVoltageSourceKind
    {
        /// <summary>未分类 (不应该出现在最终结果里)。</summary>
        Unknown = 0,

        /// <summary>
        /// 经 LHM 实现确认的核心供电遥测, 且来自 CPU 硬件节点。
        ///
        /// 注意口径: 它**不等于** VRM 的 ADC 实测值。
        /// LHM 0.9.6 的 Intel 实现 (IntelCpu.cs) 取自 IA32_PERF_STATUS,
        ///   是处理器自己上报的当前核心工作电压;
        /// AMD 实现目前没有同级别的核心轨遥测, 只有 VID 码 (见 RequestedVid)。
        /// </summary>
        MeasuredCoreRail = 1,

        /// <summary>
        /// 主板 / SuperIO 明确命名为 Vcore 的核心供电电压。
        /// 这是 SuperIO ADC 真的测出来的核心轨电压, CPU 归属由命名本身确认;
        /// 但它只能随主板慢扫描 (3 秒) 一起刷新, 刷新率远低于 CPU。
        /// </summary>
        MotherboardVcore = 2,

        /// <summary>
        /// 处理器**请求**的电压 (VID / Vcore 电压码), 不是测得的供电电压。
        ///
        /// LHM 0.9.6 的两个来源:
        ///   - Amd17Cpu.cs: "Core #N VID" = 1.550 - 0.00625 * CurCpuVid (MSR 0xC0010071);
        ///   - Amd17Cpu.cs: "Core (SVI2 TFN)" = 1.550 - 0.00625 * VDDCOR (SVI0 遥测平面);
        ///   - Amd10Cpu.cs: "CPU Cores" = SVI1/SVI2 电压码换算。
        /// 三者都是**请求电压**, 只能当参考值显示, 绝不能与测得电压平均或混称。
        /// </summary>
        RequestedVid = 3,

        /// <summary>
        /// 来源含糊 (例如只叫 "CPU"), 语义无法确认。
        /// 自动模式**不盲选**; 手动列表里可以显示, 但要标注"来源待确认"。
        /// </summary>
        Ambiguous = 4,

        /// <summary>
        /// 明确不是 CPU 核心电压的其它电压轨
        /// (SoC / GT / SA / AUX / DIMM / 输入供电 / +5V / +12V / 电池 / CMOS ...)。
        /// 自动与手动都不应映射成 CPU 核心电压。
        /// </summary>
        Excluded = 5,
    }

    /// <summary>候选所在的硬件层级 (对应 LHM 的 HardwareType, 由接线层翻译)。</summary>
    public enum CpuVoltageHardwareLevel
    {
        Unknown = 0,
        /// <summary>CPU 硬件节点 (LHM: HardwareType.Cpu)。</summary>
        Cpu = 1,
        /// <summary>SuperIO 芯片 (LHM: HardwareType.SuperIO), 主板上的监控控制器。</summary>
        SuperIO = 2,
        /// <summary>主板硬件节点 (LHM: HardwareType.Motherboard)。</summary>
        Motherboard = 3,
        /// <summary>其它 (GPU / 电池 / 内存 ...), 与 CPU 核心电压无关。</summary>
        Other = 4,
    }

    /// <summary>
    /// 候选的粒度: 单条轨的读数, 还是每核心一个读数。
    /// 决定聚合策略 —— 每核心多值必须显式聚合并注明, 不能任取第一核。
    /// </summary>
    public enum CpuVoltageGranularity
    {
        /// <summary>整条核心轨一个读数。</summary>
        Rail = 0,
        /// <summary>每核心一个读数 (Intel "CPU Core #N" / AMD "Core #N VID")。</summary>
        PerCore = 1,
    }

    /// <summary>
    /// 一个 CPU 电压候选 (纯数据, 不持有 LHM 对象)。
    ///
    /// 为什么要做成"可注入的纯输入":
    ///   - 采集必须由共享协调器 <see cref="HardwareSamplingCoordinator"/> 限频执行,
    ///     UI / Web 不得旁路采集, 所以这里**不允许**持有 IHardware / ISensor,
    ///     更不允许在解析过程中调用 hw.Update();
    ///   - 只有这样, 选择规则才能在没有任何硬件的机器上用假数据做单元测试。
    /// </summary>
    public sealed class CpuVoltageCandidate
    {
        /// <summary>传感器名 (LHM 的 ISensor.Name), 例如 "Core (SVI2 TFN)" / "Vcore"。</summary>
        public string SensorName { get; init; } = "";

        /// <summary>
        /// 传感器稳定标识 (LHM 的 ISensor.Identifier, 例如 "/amdcpu/0/voltage/2")。
        /// 手动选择与热重载后定位都靠它 —— 它不随枚举顺序变化。
        /// </summary>
        public string SensorIdentifier { get; init; } = "";

        /// <summary>所属硬件的稳定标识 (LHM 的 IHardware.Identifier)。</summary>
        public string HardwareIdentifier { get; init; } = "";

        /// <summary>所属硬件名 (仅用于显示与日志)。</summary>
        public string HardwareName { get; init; } = "";

        /// <summary>硬件层级。</summary>
        public CpuVoltageHardwareLevel HardwareLevel { get; init; } = CpuVoltageHardwareLevel.Unknown;

        /// <summary>当前读数 (V)。null 表示本次采集没有拿到值。</summary>
        public double? Value { get; init; }

        /// <summary>读数的采集时刻。缺时间戳就无法判断新鲜度。</summary>
        public DateTimeOffset? Timestamp { get; init; }

        /// <summary>粒度。接线层不填时由分类结果推断。</summary>
        public CpuVoltageGranularity? Granularity { get; init; }
    }

    /// <summary>
    /// CPU 电压的解析结果。
    ///
    /// 约定: 缺失就是缺失 (Value == null), **绝不用 0 顶替**, 也绝不在来源消失后
    /// 继续沿用上一代的电压 —— 那样面板上会一直显示一个已经失效的旧电压。
    /// </summary>
    public sealed class CpuVoltageResolution
    {
        /// <summary>本次结果对应的采集代次。</summary>
        public int Generation { get; init; }

        /// <summary>是否有可用数值。false 时 <see cref="Value"/> 必为 null。</summary>
        public bool IsAvailable { get; init; }

        /// <summary>数值本身是否通过了来源相关的有效性规则。</summary>
        public bool IsValid { get; init; }

        /// <summary>
        /// 是否只是参考值 (VID 请求电压 / 来源待确认)。
        /// true 时 UI 必须明确标注, 不得称作"实测核心电压"。
        /// </summary>
        public bool IsReferenceOnly { get; init; }

        /// <summary>电压值 (V)。</summary>
        public double? Value { get; init; }

        /// <summary>来源类型。</summary>
        public CpuVoltageSourceKind SourceKind { get; init; }

        /// <summary>来源的简短说明 (中文)。语言包接线时可用 <see cref="SourceKind"/> 映射。</summary>
        public string SourceLabel { get; init; } = "";

        /// <summary>聚合说明 (例如 "最高核心 VID (8 个核心候选)")。</summary>
        public string? AggregationNote { get; init; }

        /// <summary>来源硬件的稳定标识。</summary>
        public string? HardwareIdentifier { get; init; }

        /// <summary>来源传感器的稳定标识。</summary>
        public string? SensorIdentifier { get; init; }

        /// <summary>来源传感器名。</summary>
        public string? SensorName { get; init; }

        /// <summary>读数的采集时刻。</summary>
        public DateTimeOffset? Timestamp { get; init; }

        /// <summary>
        /// 读数为 0 —— 可能是休眠 / 门控, 只能提示, **不能**据此判定失败。
        /// </summary>
        public bool IsZeroSuspect { get; init; }

        /// <summary>
        /// 选中的来源来自主板 / SuperIO, 需要接入既有主板慢扫描 (3 秒) 需求,
        /// 否则这个电压会长期停在旧值上。
        /// </summary>
        public bool NeedsMotherboardSlowScan { get; init; }

        /// <summary>缺失原因 (仅在 <see cref="IsAvailable"/> == false 时有值)。</summary>
        public string? MissingReason { get; init; }

        /// <summary>本次结果是否来自同代次复用 (没有重新做选择计算)。</summary>
        public bool WasReused { get; init; }

        /// <summary>来源是否来自主板 / SuperIO (慢扫描来源)。</summary>
        public bool IsMotherboardSource =>
            SourceKind == CpuVoltageSourceKind.MotherboardVcore || NeedsMotherboardSlowScan;
    }

    /// <summary>
    /// CPU 电压候选的选择层 (纯逻辑, 不碰 LHM 静态状态)。
    ///
    /// 职责边界:
    ///   - 只回答"在给定的一组候选里, CPU 电压该取哪一个, 它到底是什么口径";
    ///   - 不采集、不刷新、不写缓存, 也不提供定时器 —— 采集一律走
    ///     <see cref="HardwareSamplingCoordinator"/>, 接线层按代次把候选喂进来。
    ///
    /// 为什么要单独抽一层:
    ///   原来的 SensorMatcher 把 "core / cpu / vcore / vid" 一律映射到同一个
    ///   CPU.Voltage 键, 于是三种物理量被混为一谈, 而且同优先级的候选是"先到先得",
    ///   结果取决于 LHM 的传感器枚举顺序。#166 要求:
    ///   (1) 区分实测核心供电遥测 / VID 请求电压 / 主板 Vcore;
    ///   (2) 同优先级用稳定标识排序, 与枚举顺序无关;
    ///   (3) 来源失效 / 过期 / 值无效时返回缺失, 不沿用旧值。
    /// </summary>
    public sealed class CpuVoltageResolver
    {
        /// <summary>
        /// 主板慢扫描节奏 (毫秒)。与 HardwareSamplingSchedule.SlowScanInterval (3 秒) 保持一致,
        /// 主板来源的电压只能随这一节奏刷新, 因此 UI 必须披露它的刷新速度。
        /// </summary>
        public const int MotherboardSlowScanIntervalMs = 3000;

        // ---- 名称规则 (只作为候选入口, 最终语义以 LHM 0.9.6 实现为准) ----

        /// <summary>名称里带独立单词 "VID" —— 厂商自己标注的请求电压。</summary>
        private static readonly Regex _vidWord =
            new Regex(@"\bVID\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>每核心命名: "Core #1" / "CPU Core #1" / "P-Core #1" / "E-Core #1"。</summary>
        private static readonly Regex _perCoreName =
            new Regex(@"\b(core|p-core|e-core|cpu core)\s*#\s*\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>名称里带独立单词 "VCORE" —— 主板明确命名的核心轨。</summary>
        private static readonly Regex _vcoreWord =
            new Regex(@"\bVCORE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>独立单词形式的排除词 (避免 "sa" 误伤 "vccsa" 之外的词, 也避免子串误判)。</summary>
        private static readonly string[] _excludedTokens = { "gt", "sa", "vccsa", "ram", "vram", "gpu" };

        /// <summary>
        /// 明确不是 CPU 核心电压的电压轨关键词 (子串匹配, 全部小写比较)。
        /// 依据 LHM 0.9.6 SuperIOHardware / Nct677X / IT87XX 的真实命名整理。
        /// </summary>
        private static readonly string[] _excludedPhrases =
        {
            "soc", "aux", "dimm", "dram", "vbat", "battery", "cmos", "chipset", "pch",
            "termination", "vddio", "vref", "vtt", "vsmisc", "standby", "northbridge",
            "avcc", "avsb", "3vcc", "vcc3v", "vsb3v", "vsb", "vdd", "vddp",
            "12v", "5v", "3.3v", "1.8v", "mos", "system agent", "cpu i/o", "vin1", "vin2",
            "vin3", "vin4", "vin5", "vin6", "vin7", "1,8v",
        };

        private readonly object _gate = new();
        private int _cachedGeneration = int.MinValue;
        private string? _cachedPreferredIdentifier;
        private string _cachedFingerprint = "";
        private CpuVoltageResolution? _cached;

        /// <summary>
        /// 解析一次查询。
        ///
        /// 同代次重复读取会直接复用上次结果 (WasReused == true), 不重新做选择计算;
        /// 一旦采集代次前进、来源标识改变或指定来源消失, 缓存立即作废 ——
        /// 旧值与旧来源绝不残留。
        /// </summary>
        public CpuVoltageResolution Resolve(CpuVoltageQuery query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));

            string fingerprint = BuildFingerprint(query);
            lock (_gate)
            {
                if (_cached != null
                    && _cachedGeneration == query.Generation
                    && string.Equals(_cachedPreferredIdentifier, query.PreferredSensorIdentifier, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_cachedFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return CopyWith(_cached, wasReused: true);
                }
            }

            CpuVoltageResolution result = Compute(query, fingerprint);

            lock (_gate)
            {
                _cached = result;
                _cachedGeneration = query.Generation;
                _cachedPreferredIdentifier = query.PreferredSensorIdentifier;
                _cachedFingerprint = fingerprint;
            }

            return result;
        }

        /// <summary>
        /// 清空缓存。来源切换 / 热重载 / 硬件树重建后必须调用,
        /// 否则上一代的电压值和来源标签会跟新一代的数据拼在一起。
        /// </summary>
        public void Reset()
        {
            lock (_gate)
            {
                _cached = null;
                _cachedGeneration = int.MinValue;
                _cachedPreferredIdentifier = null;
                _cachedFingerprint = "";
            }
        }

        /// <summary>
        /// 判断一个候选属于哪种来源 (纯函数, 供设置页给候选列表打标签用)。
        /// </summary>
        public static CpuVoltageSourceKind Classify(
            CpuVoltageHardwareLevel level,
            string? sensorName,
            out CpuVoltageGranularity granularity)
        {
            granularity = CpuVoltageGranularity.Rail;
            string name = (sensorName ?? "").Trim();
            if (name.Length == 0) return CpuVoltageSourceKind.Unknown;

            // 1) 明确排除的其它电压轨优先 —— 名字里出现这些词的一律不是 CPU 核心电压。
            if (IsExcludedRail(name)) return CpuVoltageSourceKind.Excluded;

            // 2) CPU 节点上、语义已确认的核心供电遥测。
            if (level == CpuVoltageHardwareLevel.Cpu)
            {
                // LHM IntelCpu.cs: 整条核心轨的传感器就叫 "CPU Core";
                // 每核心的叫 "CPU Core #1" / "P-Core #1" / "E-Core #1"。
                if (string.Equals(name, "CPU Core", StringComparison.OrdinalIgnoreCase))
                {
                    granularity = CpuVoltageGranularity.Rail;
                    return CpuVoltageSourceKind.MeasuredCoreRail;
                }
                if (_perCoreName.IsMatch(name) && !_vidWord.IsMatch(name))
                {
                    granularity = CpuVoltageGranularity.PerCore;
                    return CpuVoltageSourceKind.MeasuredCoreRail;
                }
            }

            // 3) 处理器请求电压 (VID)。名字里带 VID 的先判, 再判 AMD 的 SVI/TFN 与 "CPU Cores"。
            if (_vidWord.IsMatch(name))
            {
                granularity = _perCoreName.IsMatch(name)
                    ? CpuVoltageGranularity.PerCore
                    : CpuVoltageGranularity.Rail;
                return CpuVoltageSourceKind.RequestedVid;
            }
            if (level == CpuVoltageHardwareLevel.Cpu
                && (name.IndexOf("SVI", StringComparison.OrdinalIgnoreCase) >= 0
                    || string.Equals(name, "CPU Cores", StringComparison.OrdinalIgnoreCase)))
            {
                // "Core (SVI2 TFN)" / "CPU Cores": LHM 用 SVI 电压码换算出来的请求电压,
                // 不是测得的供电电压 —— 与 "Core #N VID" 同口径。
                granularity = CpuVoltageGranularity.Rail;
                return CpuVoltageSourceKind.RequestedVid;
            }

            // 4) 主板明确命名的 Vcore (SuperIO ADC 实测, CPU 归属由命名确认)。
            if ((level == CpuVoltageHardwareLevel.SuperIO || level == CpuVoltageHardwareLevel.Motherboard)
                && _vcoreWord.IsMatch(name))
            {
                granularity = CpuVoltageGranularity.Rail;
                return CpuVoltageSourceKind.MotherboardVcore;
            }

            // 5) 其余一律"来源待确认", 自动模式不盲选。
            return CpuVoltageSourceKind.Ambiguous;
        }

        /// <summary>
        /// 值是否有效: 有限、非负, 并且符合来源定义。
        ///
        /// 为什么 0 不算失败:
        ///   休眠 / 门控期间, 真实的核心轨读数就是 0 V。只因为等于 0 就判失败,
        ///   会让"CPU 睡着了"变成"CPU 没有电压", 反而丢掉真实信息。
        ///   同时这里**不**套用任何跨平台通用的电压区间 —— 同一颗 CPU 在不同
        ///   平台 / 不同状态下差别很大, 套区间只会误杀。
        /// </summary>
        public static bool IsValueValid(CpuVoltageSourceKind kind, double? value)
        {
            if (kind == CpuVoltageSourceKind.Excluded) return false;
            if (kind == CpuVoltageSourceKind.Unknown) return false;
            if (!value.HasValue) return false;
            double v = value.Value;
            if (double.IsNaN(v) || double.IsInfinity(v)) return false;
            if (v < 0) return false;
            return true;
        }

        private CpuVoltageResolution Compute(CpuVoltageQuery query, string fingerprint)
        {
            var classified = new List<(CpuVoltageCandidate Candidate, CpuVoltageSourceKind Kind, CpuVoltageGranularity Granularity)>();
            foreach (var c in CandidatesOf(query))
            {
                if (c == null) continue;
                CpuVoltageSourceKind kind = Classify(c.HardwareLevel, c.SensorName, out CpuVoltageGranularity granularity);
                if (c.Granularity.HasValue) granularity = c.Granularity.Value;
                classified.Add((c, kind, granularity));
            }

            // ---- 手动指定来源 ----
            if (!string.IsNullOrWhiteSpace(query.PreferredSensorIdentifier))
            {
                string wanted = query.PreferredSensorIdentifier.Trim();
                var picked = classified
                    .Where(t => string.Equals(t.Candidate.SensorIdentifier, wanted, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.Candidate.SensorIdentifier, StringComparer.Ordinal)
                    .Select(t => t.Candidate)
                    .FirstOrDefault();

                if (picked == null)
                {
                    // 来源消失 (换了机器 / 热重载后标识变了): 返回缺失, 不保留旧值旧来源。
                    return Missing(query.Generation, "指定来源不可用 (传感器标识已不存在)");
                }

                var pickedEntry = classified.First(t => ReferenceEquals(t.Candidate, picked));
                if (pickedEntry.Kind == CpuVoltageSourceKind.Excluded)
                {
                    return Missing(query.Generation, "指定来源不是 CPU 核心电压 (如 SoC / GT / DIMM / 输入供电)");
                }
                if (!IsValueValid(pickedEntry.Kind, picked.Value))
                {
                    return Missing(query.Generation, "指定来源的读数无效");
                }
                return Build(query.Generation, picked, pickedEntry.Kind, pickedEntry.Granularity, null, 1);
            }

            // ---- 自动选择 ----
            var usable = new List<(CpuVoltageCandidate Candidate, CpuVoltageSourceKind Kind, CpuVoltageGranularity Granularity)>();
            bool sawInvalid = false;
            bool sawStale = false;
            bool sawAmbiguous = false;

            DateTimeOffset now = query.Now ?? DateTimeOffset.UtcNow;
            foreach (var t in classified)
            {
                if (t.Kind == CpuVoltageSourceKind.Excluded) continue;
                if (t.Kind == CpuVoltageSourceKind.Ambiguous) { sawAmbiguous = true; continue; }
                if (t.Kind == CpuVoltageSourceKind.RequestedVid && !query.AllowReferenceOnly) continue;

                if (!IsValueValid(t.Kind, t.Candidate.Value)) { sawInvalid = true; continue; }

                if (query.MaxAge.HasValue)
                {
                    if (!t.Candidate.Timestamp.HasValue) { sawStale = true; continue; }
                    if (now - t.Candidate.Timestamp.Value > query.MaxAge.Value) { sawStale = true; continue; }
                }

                usable.Add(t);
            }

            if (usable.Count == 0)
            {
                if (sawStale) return Missing(query.Generation, "候选已过期或缺少时间戳, 无法确认新鲜度");
                if (sawInvalid) return Missing(query.Generation, "候选读数无效 (非有限值或为负)");
                if (sawAmbiguous) return Missing(query.Generation, "只有来源待确认的候选, 自动模式不做盲选");
                return Missing(query.Generation, "没有可用的 CPU 电压候选");
            }

            // 排序: 来源可信度 -> CPU 归属 -> 粒度 -> 稳定标识。
            // ★ 最后一级用稳定标识 (Ordinal) 而不是枚举顺序, 保证同优先级候选的选择稳定。
            var ordered = usable
                .OrderBy(t => (int)t.Kind)
                .ThenBy(t => t.Candidate.HardwareLevel == CpuVoltageHardwareLevel.Cpu ? 0 : 1)
                .ThenBy(t => (int)t.Granularity)
                .ThenBy(t => t.Candidate.SensorIdentifier ?? "", StringComparer.Ordinal)
                .ToList();

            var best = ordered[0];
            var group = ordered
                .Where(t => t.Kind == best.Kind
                            && t.Granularity == best.Granularity
                            && (t.Candidate.HardwareLevel == CpuVoltageHardwareLevel.Cpu) == (best.Candidate.HardwareLevel == CpuVoltageHardwareLevel.Cpu))
                .ToList();

            // 每核心多值: 显式聚合策略 = 取最高值, 并在标签注明 —— 不能任取第一核。
            if (group.Count > 1 && best.Granularity == CpuVoltageGranularity.PerCore)
            {
                var max = group
                    .OrderByDescending(t => t.Candidate.Value ?? double.MinValue)
                    .ThenBy(t => t.Candidate.SensorIdentifier ?? "", StringComparer.Ordinal)
                    .First();
                string note = (best.Kind == CpuVoltageSourceKind.RequestedVid ? "最高核心 VID" : "最高核心电压")
                              + " (" + group.Count.ToString(CultureInfo.InvariantCulture) + " 个核心候选)";
                return Build(query.Generation, max.Candidate, best.Kind, best.Granularity, note, group.Count);
            }

            if (group.Count > 1)
            {
                // 同一条轨出现多个来源 (例如双路 CPU): 按稳定标识择一, 不靠枚举顺序。
                var first = group[0];
                string note = "同源多候选 (" + group.Count.ToString(CultureInfo.InvariantCulture) + " 个), 按稳定标识择一";
                return Build(query.Generation, first.Candidate, first.Kind, first.Granularity, note, group.Count);
            }

            return Build(query.Generation, best.Candidate, best.Kind, best.Granularity, null, 1);
        }

        private static CpuVoltageResolution Build(
            int generation,
            CpuVoltageCandidate candidate,
            CpuVoltageSourceKind kind,
            CpuVoltageGranularity granularity,
            string? aggregationNote,
            int candidateCount)
        {
            double value = candidate.Value ?? 0d;
            bool referenceOnly = kind == CpuVoltageSourceKind.RequestedVid || kind == CpuVoltageSourceKind.Ambiguous;
            bool mobo = candidate.HardwareLevel == CpuVoltageHardwareLevel.SuperIO
                        || candidate.HardwareLevel == CpuVoltageHardwareLevel.Motherboard;

            string label = kind switch
            {
                CpuVoltageSourceKind.MeasuredCoreRail => "CPU 核心电压 (处理器上报)",
                CpuVoltageSourceKind.MotherboardVcore => "CPU 核心电压 (主板 Vcore 实测)",
                CpuVoltageSourceKind.RequestedVid => "CPU VID (参考值, 非实测供电电压)",
                CpuVoltageSourceKind.Ambiguous => "CPU 电压 (来源待确认)",
                _ => "CPU 电压",
            };

            return new CpuVoltageResolution
            {
                Generation = generation,
                IsAvailable = true,
                IsValid = true,
                IsReferenceOnly = referenceOnly,
                Value = value,
                SourceKind = kind,
                SourceLabel = label,
                AggregationNote = aggregationNote,
                HardwareIdentifier = candidate.HardwareIdentifier,
                SensorIdentifier = candidate.SensorIdentifier,
                SensorName = candidate.SensorName,
                Timestamp = candidate.Timestamp,
                // 0 可能是休眠 / 门控, 只提示不判失败。
                IsZeroSuspect = value == 0d,
                NeedsMotherboardSlowScan = mobo,
            };
        }

        private static CpuVoltageResolution Missing(int generation, string reason)
        {
            return new CpuVoltageResolution
            {
                Generation = generation,
                IsAvailable = false,
                IsValid = false,
                Value = null,
                SourceKind = CpuVoltageSourceKind.Unknown,
                MissingReason = reason,
            };
        }

        private static CpuVoltageResolution CopyWith(CpuVoltageResolution src, bool wasReused)
        {
            return new CpuVoltageResolution
            {
                Generation = src.Generation,
                IsAvailable = src.IsAvailable,
                IsValid = src.IsValid,
                IsReferenceOnly = src.IsReferenceOnly,
                Value = src.Value,
                SourceKind = src.SourceKind,
                SourceLabel = src.SourceLabel,
                AggregationNote = src.AggregationNote,
                HardwareIdentifier = src.HardwareIdentifier,
                SensorIdentifier = src.SensorIdentifier,
                SensorName = src.SensorName,
                Timestamp = src.Timestamp,
                IsZeroSuspect = src.IsZeroSuspect,
                NeedsMotherboardSlowScan = src.NeedsMotherboardSlowScan,
                MissingReason = src.MissingReason,
                WasReused = wasReused,
            };
        }

        /// <summary>
        /// 候选集合指纹。刻意按传感器标识排序后再拼, 这样同一批候选
        /// 无论枚举顺序如何, 指纹都一样 —— 顺序变化不应该让同代次缓存失效。
        /// </summary>
        private static string BuildFingerprint(CpuVoltageQuery query)
        {
            var sb = new StringBuilder();
            sb.Append(query.Generation).Append('|');
            sb.Append(query.PreferredSensorIdentifier ?? "").Append('|');
            sb.Append(query.AllowReferenceOnly ? '1' : '0').Append('|');
            sb.Append(query.MaxAge?.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|');

            var parts = CandidatesOf(query)
                .Where(c => c != null)
                .Select(c => string.Join("~",
                    c.SensorIdentifier ?? "",
                    c.HardwareIdentifier ?? "",
                    c.HardwareLevel.ToString(),
                    c.Value?.ToString("R", CultureInfo.InvariantCulture) ?? "",
                    c.Timestamp?.ToString("O", CultureInfo.InvariantCulture) ?? ""))
                .OrderBy(s => s, StringComparer.Ordinal);
            foreach (var p in parts) sb.Append(p).Append(';');

            return sb.ToString();
        }

        /// <summary>取候选列表, 容忍调用方传入 null。</summary>
        private static IReadOnlyList<CpuVoltageCandidate> CandidatesOf(CpuVoltageQuery query)
        {
            return query.Candidates ?? Array.Empty<CpuVoltageCandidate>();
        }

        /// <summary>名称里是否出现明确"不是 CPU 核心电压"的关键词。</summary>
        private static bool IsExcludedRail(string name)
        {
            string lower = name.ToLowerInvariant();

            foreach (var phrase in _excludedPhrases)
            {
                if (lower.Contains(phrase, StringComparison.Ordinal)) return true;
            }

            foreach (var token in _excludedTokens)
            {
                if (HasWholeToken(lower, token)) return true;
            }

            return false;
        }

        /// <summary>
        /// 整词匹配 (按空白与常见分隔符切词)。
        /// 用它而不是子串, 是为了不把 "vccsa" 里的 "sa" 或 "vtarget" 里的 "gt" 误判成排除词。
        /// </summary>
        private static bool HasWholeToken(string lowerName, string token)
        {
            int idx = 0;
            while (idx < lowerName.Length)
            {
                int found = lowerName.IndexOf(token, idx, StringComparison.Ordinal);
                if (found < 0) return false;

                bool leftOk = found == 0 || !IsTokenChar(lowerName[found - 1]);
                int end = found + token.Length;
                bool rightOk = end >= lowerName.Length || !IsTokenChar(lowerName[end]);
                if (leftOk && rightOk) return true;

                idx = found + 1;
            }

            return false;
        }

        private static bool IsTokenChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '#';
        }
    }

    /// <summary>
    /// 一次 CPU 电压解析请求。
    ///
    /// 采集代次 (<see cref="Generation"/>) 与 HardwareSamplingCoordinator 的代次对齐:
    /// 同一代次内重复读取直接复用, 代次一换就重新选,
    /// 来源切换 / 热重载后旧值旧来源立即作废。
    /// </summary>
    public sealed class CpuVoltageQuery
    {
        /// <summary>候选列表 (由接线层在采集代次内提供)。</summary>
        public IReadOnlyList<CpuVoltageCandidate> Candidates { get; init; } = Array.Empty<CpuVoltageCandidate>();

        /// <summary>
        /// 手动指定的传感器标识 (为空 = 自动)。
        /// 手动指定但该标识不存在时必须返回缺失, 不能悄悄改用另一种含义的电压。
        /// </summary>
        public string? PreferredSensorIdentifier { get; init; }

        /// <summary>采集代次。</summary>
        public int Generation { get; init; }

        /// <summary>判定"过期"的基准时刻 (默认当前时间)。</summary>
        public DateTimeOffset? Now { get; init; }

        /// <summary>
        /// 允许的最大年龄。设置后, 超过这个时间的候选一律视为过期 (缺失),
        /// 且没有时间戳的候选也会被丢弃 —— 没法证明新鲜就不要显示。
        /// 为 null 时不做时效判断。
        /// </summary>
        public TimeSpan? MaxAge { get; init; }

        /// <summary>
        /// 自动模式下是否允许退回到 VID 参考值。
        /// 默认允许 —— 只有 VID 时以"参考值"明确标注显示, 而不是什么都不显示。
        /// </summary>
        public bool AllowReferenceOnly { get; init; } = true;
    }
}
