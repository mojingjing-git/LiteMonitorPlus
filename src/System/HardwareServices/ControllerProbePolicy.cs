using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 控制器探测模式（P-F08b —— #245 缓解层）。
    /// <para><b>★ 前置事实（IL 级取证，见 <c>tools/F08bDiskAllowlistSpike/SPIKE-CONCLUSION.md</c>）</b>：
    /// LHM 0.9.6 的 <c>Computer.AddGroups()</c> 里，<c>_controllerEnabled</c> 字段被读取后
    /// 紧接条件分支（<c>brfalse</c>）⇒ 开关为 false 时**根本不构造** 8 个控制器组
    /// （TBalancer / Heatmaster / AquaComputer / AeroCool / Nzxt / Razer / Arctic / MSI）。
    /// 这与磁盘的 <c>_storageEnabled</c> 是同一套"构造前隔离"机制 —— 本文件据此建模。</para>
    /// <para>★ 反面事实（同样来自上述取证）：控制器组一旦被构造，<c>NzxtGroup..ctor</c> 等
    /// 会**立即** <c>DeviceList.GetHidDevices</c> 并为每个匹配设备构造驱动对象（打开 HID 句柄）。
    /// ⇒ **设备级**排除在库侧没有任何"构造前"入口（与磁盘允许列表同理）。
    /// 因此本文件<b>不宣称</b>能对单个 USB 设备做构造前隔离；见 <see cref="ControllerDeviceExclusion"/> 的边界说明。</para>
    /// </summary>
    public enum ControllerProbeMode
    {
        /// <summary>
        /// 自动（默认）：沿用 fork 既有规则 —— 仅当监控项需要风扇/水泵时才开启控制器扫描。
        /// <para>★ 保持既有行为不变（不回退 fork 已有能力）。</para>
        /// </summary>
        Auto = 0,

        /// <summary>
        /// 关闭：**永不**构造任何控制器组。用户遇到 USB/HID 冲突（#245，如 DAPLink）时的明确开关。
        /// <para>代价：依赖外部控制器的风扇/水泵读数会缺失（显示 "--"，<b>不是 0</b>）。</para>
        /// </summary>
        Off = 1,

        /// <summary>
        /// 强制开启：无条件构造控制器组（用于排查"某某设备突然读不到"时的对照，
        /// 或用户在明知风险下需要读取 USB 控制器风机数据）。
        /// </summary>
        On = 2
    }

    /// <summary>控制器探测策略的状态机状态（供界面显示与测试断言）。</summary>
    public enum ControllerProbeState
    {
        /// <summary>尚未初始化（策略还没落到硬件树上）。</summary>
        Uninitialized = 0,
        /// <summary>稳定态：不构造控制器组。</summary>
        Disabled = 1,
        /// <summary>稳定态：允许构造控制器组。</summary>
        Enabled = 2,
        /// <summary>切换中（停采样 → 释放旧对象 → 重建 → 恢复）。</summary>
        Transitioning = 3,
        /// <summary>切换失败：保持既有读数可用，并对外报告失败原因。</summary>
        Degraded = 4
    }

    /// <summary>配置迁移状态。</summary>
    public enum ControllerProbeMigrationState
    {
        /// <summary>配置里已有明确选择，无需迁移。</summary>
        None = 0,
        /// <summary>
        /// 旧配置**缺少**控制器探测策略字段 ⇒ 显式迁移状态：按 <see cref="ControllerProbeMode.Auto"/>
        /// 运行（既有行为），并在设置页提示"可以彻底关闭控制器扫描"。
        /// </summary>
        Unconfigured = 1
    }

    /// <summary>
    /// 控制器探测策略：**唯一**的"能不能碰外部控制器（ USB / HID / SMBus 设备）"判定来源。
    /// <para>纯逻辑、无硬件依赖 ⇒ 状态机可以单测（不启动 LHM / 不枚举 HID）。</para>
    /// </summary>
    public sealed class ControllerProbePolicy
    {
        /// <summary>配置里的"未设置"内部值（配置侧仍用 <c>null</c> 表示键不存在）。</summary>
        public const int UnconfiguredModeValue = -1;

        /// <summary>当前生效的模式。</summary>
        public ControllerProbeMode Mode { get; private set; }

        /// <summary>当前状态机状态。</summary>
        public ControllerProbeState State { get; private set; }

        /// <summary>配置迁移状态。</summary>
        public ControllerProbeMigrationState MigrationState { get; private set; }

        /// <summary>最近一次切换失败的原因（null 表示没有失败）。</summary>
        public string? LastError { get; private set; }

        private ControllerProbePolicy(ControllerProbeMode mode, ControllerProbeMigrationState migration)
        {
            Mode = mode;
            MigrationState = migration;
            State = ControllerProbeState.Uninitialized;
        }

        /// <summary>
        /// 按配置构造策略。<c>null</c>（旧配置缺字段）⇒ <see cref="ControllerProbeMode.Auto"/> + 迁移状态。
        /// </summary>
        public static ControllerProbePolicy FromSettings(Settings? cfg)
        {
            int raw = cfg?.ControllerProbeMode ?? UnconfiguredModeValue;
            return FromRawValue(raw);
        }

        /// <summary>
        /// 按配置里的原始整数值构造策略（0/1/2 之外的非法值一律退化为 <see cref="ControllerProbeMode.Auto"/>）。
        /// <para>★ 与磁盘策略同一口径：**脏配置不得放开硬件访问**（这里是"不得变得更宽松"），
        /// 非法值退到 Auto 而不是 Off —— 因为 Auto 会按需求自行判断，仍是既有行为。</para>
        /// </summary>
        public static ControllerProbePolicy FromRawValue(int raw)
        {
            switch (raw)
            {
                case UnconfiguredModeValue:
                    return new ControllerProbePolicy(ControllerProbeMode.Auto, ControllerProbeMigrationState.Unconfigured);
                case (int)ControllerProbeMode.Off:
                    return new ControllerProbePolicy(ControllerProbeMode.Off, ControllerProbeMigrationState.None);
                case (int)ControllerProbeMode.On:
                    return new ControllerProbePolicy(ControllerProbeMode.On, ControllerProbeMigrationState.None);
                default:
                    // 0（自动）或任何非法值 ⇒ 自动。
                    return new ControllerProbePolicy(ControllerProbeMode.Auto, ControllerProbeMigrationState.None);
            }
        }

        /// <summary>
        /// 是否应当构造控制器组（= LHM 的 <c>Computer.IsControllerEnabled</c>）。
        /// </summary>
        /// <param name="fanMetricsNeeded">
        /// 当前是否有监控项需要外部控制器（风扇 / 水泵）。由调用方给出 —— 策略本身**不**去读监控项配置，
        /// 这样"需求判定"与"开不开关"两件事不会互相污染（也便于测试）。
        /// </param>
        public bool ShouldProbe(bool fanMetricsNeeded)
        {
            switch (Mode)
            {
                case ControllerProbeMode.Off: return false;
                case ControllerProbeMode.On: return true;
                default: return fanMetricsNeeded;   // Auto：既有规则
            }
        }

        /// <summary>
        /// 把"是否已经打开过"这件事记进状态机（供 UI 显示与实际状态保持一致）。
        /// <para>★ 幂等：同一目标值重复调用不会反复触发状态迁移。</para>
        /// </summary>
        public void MarkOpened(bool controllerEnabled)
        {
            ControllerProbeState target = controllerEnabled ? ControllerProbeState.Enabled : ControllerProbeState.Disabled;
            if (State == target) { LastError = null; return; }
            State = target;
            LastError = null;
        }

        /// <summary>
        /// 切换模式（状态机）。
        /// <para>★ 切换必须**有界且可失败**：失败时保持原模式并进入
        /// <see cref="ControllerProbeState.Degraded"/>，<b>绝不把失败伪装成成功</b>。</para>
        /// </summary>
        /// <param name="target">目标模式。</param>
        /// <param name="apply">真正落地的动作（停采样 → 释放旧对象 → 重建 → 恢复由调用方在采样协调器边界内完成）。抛异常 ⇒ 失败。</param>
        /// <param name="verify">落地后的校验（例如" Off 下硬件树里确实没有控制器设备"）。返回 false ⇒ 失败；null 时跳过。</param>
        /// <returns>切换是否成功。</returns>
        public bool ApplyMode(ControllerProbeMode target, Action<ControllerProbeMode> apply, Func<ControllerProbeMode, bool>? verify)
        {
            if (apply == null) throw new ArgumentNullException(nameof(apply));

            // 幂等：已经是目标模式且处于稳定态 ⇒ 不重建硬件树。
            if (target == Mode && (State == ControllerProbeState.Enabled || State == ControllerProbeState.Disabled))
            {
                MigrationState = ControllerProbeMigrationState.None;
                return true;
            }

            ControllerProbeMode previous = Mode;
            State = ControllerProbeState.Transitioning;
            LastError = null;

            try
            {
                apply(target);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Mode = previous;
                State = ControllerProbeState.Degraded;
                TryRevert(previous, apply);
                return false;
            }

            bool ok;
            try
            {
                ok = verify == null || verify(target);
            }
            catch (Exception ex)
            {
                ok = false;
                LastError = ex.Message;
            }

            if (!ok)
            {
                if (string.IsNullOrEmpty(LastError)) LastError = "controller probe mode switch rejected";
                Mode = previous;
                State = ControllerProbeState.Degraded;
                TryRevert(previous, apply);
                return false;
            }

            Mode = target;
            MigrationState = ControllerProbeMigrationState.None;
            LastError = null;
            return true;
        }

        /// <summary>回退到原模式（**最多一次**，保证"恢复有界"）。</summary>
        private void TryRevert(ControllerProbeMode previous, Action<ControllerProbeMode> apply)
        {
            try { apply(previous); }
            catch (Exception ex) { LastError = LastError ?? ex.Message; }
        }
    }

    /// <summary>
    /// "打开硬件树"接缝（控制器维度）：使"打开之前策略已生效"这件事可被观测、可被替身计数。
    /// <para>★ 与 <see cref="IHardwareTreeOpener"/>（磁盘）分开：两者是**不同的开关**，
    /// 合成一个接口会让"磁盘关着但控制器开着"这类组合无法表达。</para>
    /// </summary>
    public interface IControllerTreeOpener
    {
        /// <summary>是否构造控制器设备组（= LHM 的 <c>Computer.IsControllerEnabled</c>）。</summary>
        bool IsControllerEnabled { get; set; }

        /// <summary>打开硬件树（枚举并构造设备）。</summary>
        void Open();
    }

    /// <summary>
    /// 控制器探测闸门：**受库前提支撑的"构造前"隔离点**。
    /// <para>★ 它有 IL 级保证（<c>_controllerEnabled</c> ⇒ 条件分支 ⇒ 跳过 8 个控制器组的构造），
    /// 因此这道闸门**确实**能避免碰任何 HID/USB 设备 —— 这与磁盘允许列表（**做不到**构造前逐设备过滤）不同。</para>
    /// </summary>
    public sealed class ControllerProbeGate
    {
        /// <summary>当前策略。</summary>
        public ControllerProbePolicy Policy { get; }

        /// <summary>最近一次落下去的目标值所需的"风扇/水泵需求"（由调用方在 Open 前更新）。</summary>
        public bool FanMetricsNeeded { get; set; }

        public ControllerProbeGate(ControllerProbePolicy policy, bool fanMetricsNeeded = false)
        {
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
            FanMetricsNeeded = fanMetricsNeeded;
        }

        /// <summary>只落策略，不打开。</summary>
        public void ApplyTo(IControllerTreeOpener target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.IsControllerEnabled = Policy.ShouldProbe(FanMetricsNeeded);
        }

        /// <summary>落策略后打开（★ 打开之前策略一定已经生效）。</summary>
        public void Open(IControllerTreeOpener target)
        {
            ApplyTo(target);
            target.Open();
            Policy.MarkOpened(target.IsControllerEnabled);
        }
    }

    /// <summary>
    /// 控制器设备排除清单（#245 的**辅助**手段）。
    /// <para>★ <b>能力边界（必须如实说明）</b>：本清单作用在**已经构造出来的设备对象**上
    /// （决定"要不要 Update 它"），<b>不能</b>阻止设备被构造 —— 构造与 HID 句柄打开发生在
    /// <c>NzxtGroup..ctor</c> 等组的构造函数里（见文件头的前置事实），调用方拿到的清单已经是成品。
    /// ⇒ 它<b>不是</b> #245 的根治手段，只是"降低二次触碰频率"的缓解层。</para>
    /// <para>★ <b>标识可靠性</b>：LHM 的 <c>IHardware</c> **不暴露** USB VID/PID 或 PnP 硬件 ID，
    /// 只有 <c>Identifier</c> 与 <c>Name</c>。按 F07 的分级，这二者都**不能**作为跨重启的稳定身份。
    /// 因此本清单一律按"字符串包含"匹配，并且匹配结果只用于跳过 Update ——
    /// 一旦其中某个设备后续还要更新时，区分不了"同名不同设备"的情况，宁可漏排除也不误排除。</para>
    /// </summary>
    public sealed class ControllerDeviceExclusion
    {
        private string[] _patterns;

        /// <summary>
        /// 默认排除模式（大小写不敏感的字符串包含匹配）。
        /// <para>★ <c>daplink</c> / <c>cmsis-dap</c> / <c>0d28</c> 对应 Keil/Arm DAPLink 调试探头
        /// （行业通用的 CMSIS-DAP USB VID 0x0D28）。**默认列的目的是让用户一眼看到可以怎么改**，
        /// 真实 VID/PID 是否命中必须在目标机核对 ⇒ 列入待实机清单。</para>
        /// </summary>
        public static readonly string[] DefaultPatterns = { "daplink", "cmsis-dap", "0d28" };

        public ControllerDeviceExclusion(IEnumerable<string>? patterns = null)
        {
            _patterns = patterns == null ? DefaultPatterns.ToArray() : patterns.ToArray();
        }

        /// <summary>当前生效的匹配串（快照）。</summary>
        public IReadOnlyList<string> Patterns => _patterns;

        /// <summary>
        /// 替换整张清单（★ **实时生效**：下一次 <see cref="IsExcluded"/> 立即按新清单判定，
        /// 不需要重建硬件树、也不缓存历史结果）。
        /// </summary>
        public void SetPatterns(IEnumerable<string>? patterns)
        {
            _patterns = patterns == null ? Array.Empty<string>() : patterns.ToArray();
        }

        /// <summary>追加一条（实时生效）。</summary>
        public void Add(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return;
            var list = new List<string>(_patterns) { pattern.Trim() };
            _patterns = list.ToArray();
        }

        /// <summary>移除一条（实时生效，按大小写不敏感的全等比较）。</summary>
        public void Remove(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return;
            _patterns = _patterns.Where(p => !string.Equals(p, pattern.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        /// <summary>恢复默认清单（实时生效）。</summary>
        public void ResetToDefaults() => _patterns = DefaultPatterns.ToArray();

        /// <summary>
        /// 是否应当被排除（跳过 Update）。匹配串为空 ⇒ 一律不排除（空清单不该把一切都排除掉）。
        /// </summary>
        /// <param name="identifier">LHM 的 <c>IHardware.Identifier</c>（形如 "/nzxt/kraken/0"）。</param>
        /// <param name="name">LHM 的 <c>IHardware.Name</c>。</param>
        public bool IsExcluded(string? identifier, string? name)
        {
            if (_patterns.Length == 0) return false;
            if (string.IsNullOrEmpty(identifier) && string.IsNullOrEmpty(name)) return false;

            foreach (string pattern in _patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                if (identifier != null && identifier.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;
                if (name != null && name.Contains(pattern, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
