using System;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.src.SystemServices;                  // HardwareMonitor/策略类型（现状命名空间）
using LiteMonitor.src.SystemServices.NetworkTraffic;  // NetworkTrafficExclusionStatus

namespace LiteMonitor.SystemServices
{
    /// <summary>
    /// 应用层硬件网关（P-B3 阶段二/三）：组合根（UIController 构造）注册一次，
    /// 消费方不再读 <c>HardwareMonitor.Instance</c> 静态（已降 internal，B5 拆程序集前的先行收口）。
    /// 数据面优先走 <see cref="Client"/>（C1 契约）；交互动作与系统服务属性按 Plan B §4.1 留应用层，
    /// 以本网关为唯一取用点，B3+ 各模块服务接线后逐项收编。未注册时全部安全返回空/无操作。
    /// </summary>
    public static class HardwareGateway
    {
        private static volatile HardwareMonitor? _monitor;
        private static volatile IHardwareClient? _client;

        public static bool IsRegistered => _monitor != null;

        /// <summary>C1 契约客户端（数据面：快照/目录/配置/需求/健康）。</summary>
        public static IHardwareClient? Client => _client;

        /// <summary>已注册采集器的身份对象（供 Web 需求同步器的目标变更检测，不暴露成员）。</summary>
        public static object? RegisteredTarget => _monitor;

        public static void Register(HardwareMonitor monitor, IHardwareClient? client = null)
        {
            _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            _client = client;
        }

        public static void Unregister()
        {
            _client = null;
            _monitor = null;
        }

        // ── 数据面便捷 ──────────────────────────────────────────────────────────
        public static string? NetworkIP => _monitor?.GetNetworkIP();

        public static float? GetSampleValue(string key) => _monitor?.Get(key);

        public static bool HasFpsData => _monitor?.HasFpsData ?? false;

        // ── 交互动作（应用层保留） ────────────────────────────────────────────────
        public static void CleanMemory(Action<int>? onProgress = null) => _monitor?.CleanMemory(onProgress);

        public static Task SmartCheckDriverAsync()
            => _monitor?.SmartCheckDriver() ?? Task.CompletedTask;

        // ── 采样协调（Web 需求同步器等） ─────────────────────────────────────────
        public static Task<bool> RequestSamplingAsync(CancellationToken cancellationToken = default)
        {
            var monitor = _monitor;
            if (monitor == null) return Task.FromResult(false);
            return monitor.UpdateIfDueAsync(cancellationToken);
        }

        /// <summary>采集层尚未注册时返回 false，同步器下一轮重试（保持既有语义）。</summary>
        public static bool TrySetWebDataDemand(bool hasDemand)
        {
            var monitor = _monitor;
            if (monitor == null) return false;
            monitor.SetWebDataDemand(hasDemand);
            return true;
        }

        // ── 硬件重配与策略（AppActions / 设置页） ────────────────────────────────
        public static void RefreshHardwareConfig() => _monitor?.RefreshHardwareConfig();

        public static int DetectedGpuCount => HardwareMonitor.TryGetDetectedGpuCount();

        public static DiskProbePolicy? DiskProbePolicy => _monitor?.DiskProbePolicy;

        public static bool ApplyDiskProbeMode(DiskProbeMode mode)
            => _monitor?.ApplyDiskProbeMode(mode) ?? false;

        public static bool ApplyControllerProbeMode(ControllerProbeMode mode)
            => _monitor?.ApplyControllerProbeMode(mode) ?? false;

        public static NetworkTrafficExclusionStatus? TrafficExclusionStatus
            => _monitor?.GetTrafficExclusionStatus();
    }
}
