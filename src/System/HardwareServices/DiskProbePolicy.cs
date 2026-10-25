using System;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 磁盘探测模式（P-F08a 止血层）。
    /// <para><b>背景</b>：取证报告 <c>docs/verification/2026-10-25-TASK-3-crash-hardware.md</c> 确认
    /// fork 的 <c>HardwareMonitor</c> 无条件 <c>IsStorageEnabled = true</c>，于是 LHM 0.9.6 在
    /// <c>Computer.Open()</c> 里**创建存储设备组并打开设备句柄**，直接导致：</para>
    /// <list type="bullet">
    /// <item>#466：可移动磁盘被占用，无法安全移除；</item>
    /// <item>#601：与 HWiNFO 同时运行时争抢设备（截图点名 LiteMonitor 持有共享同步对象）；</item>
    /// <item>#245：USB/HID 设备枚举被连带干扰（DAPLink 等设备识别受影响）。</item>
    /// </list>
    /// <para><b>本包只做止血</b>：<see cref="CountersOnly"/> 让 LHM **完全不构造存储设备**，
    /// 速度与活动率改由 Windows 计数器提供，温度/SMART 明确缺失（**不显示 0**）。
    /// 精细允许列表（只对指定磁盘开 SMART）属于 P-F08b，本包不提前实现。</para>
    /// </summary>
    public enum DiskProbeMode
    {
        /// <summary>
        /// 仅计数器（默认）：**不构造任何存储设备**，不打开设备句柄、不做 SMART 探测。
        /// 磁盘速度/活动率来自 Windows 性能计数器；磁盘温度/SMART 缺失（显示为 "--"，不是 0）。
        /// </summary>
        CountersOnly = 0,

        /// <summary>
        /// 兼容模式：恢复旧行为 —— 允许 LHM 打开存储设备（SMART / 温度）。
        /// 用户显式选择后才会进入；代价是可能再次占用可移动磁盘、并与其它硬件工具冲突。
        /// </summary>
        SmartAllowed = 1
    }

    /// <summary>磁盘探测策略的状态机状态（供界面显示与测试断言）。</summary>
    public enum DiskProbeState
    {
        /// <summary>尚未初始化（策略还没落到硬件树上）。</summary>
        Uninitialized = 0,
        /// <summary>稳定态：仅计数器。</summary>
        CountersOnly = 1,
        /// <summary>稳定态：兼容模式（允许 SMART）。</summary>
        SmartAllowed = 2,
        /// <summary>切换中（停采样 → 释放旧对象 → 重建 → 恢复）。</summary>
        Transitioning = 3,
        /// <summary>切换失败：保持计数器数据可用，并对外报告失败原因。</summary>
        Degraded = 4
    }

    /// <summary>配置迁移状态。</summary>
    public enum DiskProbeMigrationState
    {
        /// <summary>配置里已有明确选择，无需迁移。</summary>
        None = 0,
        /// <summary>
        /// 旧配置**缺少**磁盘探测策略字段 ⇒ 显式迁移状态：
        /// 首次打开硬件前一律按 <see cref="DiskProbeMode.CountersOnly"/> 运行，
        /// 并在设置页提示"兼容模式与 SMART 的影响"，等待用户明确选择。
        /// ★ 用户原有的磁盘偏好与监控项**一律保留**，绝不因为迁移被清空或改绑到别的设备。
        /// </summary>
        Unconfigured = 1
    }

    /// <summary>
    /// 磁盘探测策略：**唯一**的"能不能碰存储设备"判定来源。
    /// <para>纯逻辑、无硬件依赖 ⇒ 状态机可以单测（不启动 LHM / 驱动）。</para>
    /// </summary>
    public sealed class DiskProbePolicy
    {
        /// <summary>
        /// 配置里的"未设置"哨兵值。
        /// ★ 用 -1 而不是 0：旧配置缺字段时必须与"用户明确选了仅计数器"**区分开**，
        /// 否则迁移状态会被静默吞掉（用户永远不知道自己处在兼容模式之外）。
        /// </summary>
        public const int UnconfiguredModeValue = -1;

        /// <summary>当前生效的模式。</summary>
        public DiskProbeMode Mode { get; private set; }

        /// <summary>当前状态机状态。</summary>
        public DiskProbeState State { get; private set; }

        /// <summary>配置迁移状态。</summary>
        public DiskProbeMigrationState MigrationState { get; private set; }

        /// <summary>最近一次切换失败的原因（null 表示没有失败）。</summary>
        public string? LastError { get; private set; }

        /// <summary>
        /// 是否允许 LHM 构造存储设备（= <c>Computer.IsStorageEnabled</c>）。
        /// ★ 仅计数器模式下**必须为 false**：这是"不占用可移动磁盘、不与 HWiNFO 争抢"的唯一开关。
        /// </summary>
        public bool StorageProbeEnabled => Mode == DiskProbeMode.SmartAllowed;

        /// <summary>是否允许 SMART 探测（温度等）。</summary>
        public bool AllowsSmart => Mode == DiskProbeMode.SmartAllowed;

        /// <summary>是否允许显示磁盘温度。</summary>
        public bool AllowsDiskTemperature => Mode == DiskProbeMode.SmartAllowed;

        /// <summary>是否允许使用 Windows 计数器（两种模式都允许 —— 它不碰设备句柄）。</summary>
        public bool AllowsDiskCounters => true;

        private DiskProbePolicy(DiskProbeMode mode, DiskProbeMigrationState migration)
        {
            Mode = mode;
            MigrationState = migration;
            State = mode == DiskProbeMode.SmartAllowed
                ? DiskProbeState.SmartAllowed
                : DiskProbeState.CountersOnly;
        }

        /// <summary>
        /// 按配置构造策略。
        /// <para>★ 旧配置缺字段（<see cref="UnconfiguredModeValue"/>）⇒ 一律按
        /// <see cref="DiskProbeMode.CountersOnly"/> 生效（首次打开硬件前不碰存储设备），
        /// 同时进入 <see cref="DiskProbeMigrationState.Unconfigured"/> 迁移状态，等用户明确选择。</para>
        /// </summary>
        public static DiskProbePolicy FromSettings(Settings? cfg)
        {
            int raw = cfg?.DiskProbeMode ?? UnconfiguredModeValue;
            return FromRawValue(raw);
        }

        /// <summary>按配置里的原始整数值构造策略（-1 / 0 / 1 之外的非法值一律退化为仅计数器）。</summary>
        public static DiskProbePolicy FromRawValue(int raw)
        {
            if (raw == UnconfiguredModeValue)
                return new DiskProbePolicy(DiskProbeMode.CountersOnly, DiskProbeMigrationState.Unconfigured);

            if (raw == (int)DiskProbeMode.SmartAllowed)
                return new DiskProbePolicy(DiskProbeMode.SmartAllowed, DiskProbeMigrationState.None);

            // 0（仅计数器）或任何非法值 ⇒ 仅计数器 + 无迁移提示。
            return new DiskProbePolicy(DiskProbeMode.CountersOnly, DiskProbeMigrationState.None);
        }

        /// <summary>
        /// 切换模式（状态机）。
        /// <para>★ 切换必须**有界且可失败**：失败时保持原模式与计数器数据，
        /// 并进入 <see cref="DiskProbeState.Degraded"/>，绝不把失败伪装成成功。</para>
        /// </summary>
        /// <param name="target">目标模式。</param>
        /// <param name="apply">
        /// 真正落地的动作（停采样 → 释放旧对象 → 重建 → 恢复由调用方在采样协调器边界内完成）。
        /// 抛异常 ⇒ 切换失败。
        /// </param>
        /// <param name="verify">
        /// 落地后的校验（例如"仅计数器模式下硬件树里确实没有存储设备"）。
        /// 返回 false ⇒ 切换失败。为 null 时跳过校验。
        /// </param>
        /// <returns>切换是否成功。</returns>
        public bool ApplyMode(DiskProbeMode target, Action<DiskProbeMode> apply, Func<DiskProbeMode, bool>? verify)
        {
            if (apply == null) throw new ArgumentNullException(nameof(apply));

            // 幂等：已经是目标模式且处于稳定态 ⇒ 不重建硬件树（避免无谓的停采样/重载）。
            if (target == Mode && (State == DiskProbeState.CountersOnly || State == DiskProbeState.SmartAllowed))
            {
                MigrationState = DiskProbeMigrationState.None;
                return true;
            }

            DiskProbeMode previous = Mode;
            State = DiskProbeState.Transitioning;
            LastError = null;

            try
            {
                apply(target);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Mode = previous;
                State = DiskProbeState.Degraded;
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
                if (string.IsNullOrEmpty(LastError)) LastError = "disk probe mode switch rejected";
                Mode = previous;
                State = DiskProbeState.Degraded;
                TryRevert(previous, apply);
                return false;
            }

            Mode = target;
            State = target == DiskProbeMode.SmartAllowed ? DiskProbeState.SmartAllowed : DiskProbeState.CountersOnly;
            MigrationState = DiskProbeMigrationState.None;
            LastError = null;
            return true;
        }

        /// <summary>
        /// 回退到原模式（**最多一次**，保证"恢复有界"）。
        /// 回退本身失败只记录，不再递归 —— 计数器数据不依赖存储设备，始终可用。
        /// </summary>
        private void TryRevert(DiskProbeMode previous, Action<DiskProbeMode> apply)
        {
            try { apply(previous); }
            catch (Exception ex) { LastError = LastError ?? ex.Message; }
        }
    }

    /// <summary>
    /// "打开硬件树"接缝：让**打开之前**的策略落点可被观测、可被替身计数。
    /// <para>★ 存在的理由：LHM 的 <c>Computer.Open()</c> 不是虚方法，无法在测试里替换；
    /// 用这一层把"策略已落、然后才打开"这件事变成可断言的产品行为。</para>
    /// </summary>
    public interface IHardwareTreeOpener
    {
        /// <summary>是否构造存储设备（= LHM 的 <c>Computer.IsStorageEnabled</c>）。</summary>
        bool IsStorageEnabled { get; set; }

        /// <summary>打开硬件树（枚举并构造设备）。</summary>
        void Open();
    }

    /// <summary>LHM <see cref="Computer"/> 的接缝适配器。</summary>
    public sealed class LhmHardwareTree : IHardwareTreeOpener
    {
        private readonly Computer _computer;

        public LhmHardwareTree(Computer computer)
        {
            _computer = computer ?? throw new ArgumentNullException(nameof(computer));
        }

        public bool IsStorageEnabled
        {
            get => _computer.IsStorageEnabled;
            set => _computer.IsStorageEnabled = value;
        }

        public void Open() => _computer.Open();
    }

    /// <summary>
    /// 磁盘探测闸门：**打开硬件树的唯一入口**。
    /// <para>★ 规则单一来源：先按策略落 <c>IsStorageEnabled</c>，再打开硬件树。
    /// 于是"第一次 <c>Computer.Open()</c> 之前存储设备已被禁用"这条不变量由代码结构保证，
    /// 而不是靠"记得在某处先设一次"。</para>
    /// </summary>
    public sealed class DiskProbeGate
    {
        /// <summary>当前策略。</summary>
        public DiskProbePolicy Policy { get; }

        public DiskProbeGate(DiskProbePolicy policy)
        {
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        /// <summary>只落策略，不打开（用于已构造好的 <see cref="Computer"/>）。</summary>
        public void ApplyTo(IHardwareTreeOpener target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.IsStorageEnabled = Policy.StorageProbeEnabled;
        }

        /// <summary>落策略后打开硬件树（★ 打开之前策略一定已经生效）。</summary>
        public void Open(IHardwareTreeOpener target)
        {
            ApplyTo(target);
            target.Open();
        }
    }

    /// <summary>
    /// 磁盘计数器来源（Windows 性能计数器）—— 只读系统计数器，**不打开任何设备句柄**。
    /// </summary>
    public interface IDiskCounterSource
    {
        /// <summary>磁盘读取速度（字节/秒）；不可用时返回 null（**不是 0**）。</summary>
        float? GetDiskRead();

        /// <summary>磁盘写入速度（字节/秒）；不可用时返回 null（**不是 0**）。</summary>
        float? GetDiskWrite();

        /// <summary>磁盘活动时间（百分比，0~100）；不可用时返回 null（**不是 0**）。</summary>
        float? GetDiskActive();
    }

    /// <summary>
    /// <see cref="PerformanceCounterManager"/> 的 <see cref="IDiskCounterSource"/> 适配器。
    /// <para>★ 放在本文件而不是改 PerformanceCounterManager：那个文件不在本包的改动清单里。</para>
    /// </summary>
    public sealed class PerfCounterDiskSource : IDiskCounterSource
    {
        private readonly PerformanceCounterManager _perf;

        public PerfCounterDiskSource(PerformanceCounterManager perf)
        {
            _perf = perf ?? throw new ArgumentNullException(nameof(perf));
        }

        public float? GetDiskRead() => _perf.GetDiskRead();

        public float? GetDiskWrite() => _perf.GetDiskWrite();

        public float? GetDiskActive()
        {
            float? active = _perf.GetDiskActive();
            if (!active.HasValue) return null;
            return Math.Clamp(active.Value, 0f, 100f);
        }
    }
}
