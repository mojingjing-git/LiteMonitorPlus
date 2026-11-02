using System;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.SystemServices.NetworkTraffic;

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
        private const string WebConsumerId = "web";
        private static readonly SamplingDemand WebDemand = new(
            Ui: false,
            Taskbar: false,
            Web: true,
            History: false,
            SensorWindow: false);

        private static volatile HardwareMonitor? _monitor;
        private static volatile IHardwareClient? _client;
        private static volatile bool _hostRequested;

        public static bool IsRegistered => _client != null;

        public static bool IsHostMode => _hostRequested;

        public static bool SupportsDriverCheck => IsRegistered && !_hostRequested && _monitor != null;

        public static bool SupportsDiskProbe => IsRegistered && !_hostRequested && _monitor != null;

        public static bool SupportsControllerProbe => IsRegistered && !_hostRequested && _monitor != null;

        public static bool SupportsTrafficExclusion => IsRegistered && !_hostRequested && _monitor != null;

        public static bool SupportsHardwareRefresh => IsRegistered && !_hostRequested && _monitor != null;

        /// <summary>C1 契约客户端（数据面：快照/目录/配置/需求/健康）。</summary>
        public static IHardwareClient? Client => _client;

        /// <summary>Stable identity used by Web demand synchronization to detect source replacement.</summary>
        public static object? RegisteredTarget => _client;

        public static void Register(
            IHardwareClient client,
            HardwareMonitor? localMonitor = null,
            bool hostRequested = false)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _monitor = localMonitor;
            _hostRequested = hostRequested;
        }

        /// <summary>Compatibility overload for process-local tests and older composition code.</summary>
        public static void Register(HardwareMonitor monitor, IHardwareClient? client = null)
        {
            ArgumentNullException.ThrowIfNull(monitor);
            Register(
                client ?? new HardwareClientAdapter(monitor),
                monitor,
                hostRequested: false);
        }

        public static void Unregister(IHardwareClient? expectedClient = null)
        {
            if (expectedClient is not null && !ReferenceEquals(_client, expectedClient)) return;

            _client = null;
            _monitor = null;
            _hostRequested = false;
        }

        public static string? NetworkIP => _client?.GetNetworkIP();

        public static float? GetSampleValue(string key)
        {
            IHardwareClient? client = _client;
            if (client is null) return null;

            try
            {
                var index = new HardwareSnapshotValueIndex(client.GetLatestSnapshot());
                return index.TryGetValue(key, out float value) ? value : null;
            }
            catch
            {
                return null;
            }
        }

        public static bool HasFpsData
        {
            get
            {
                IHardwareClient? client = _client;
                if (client is null) return false;

                try
                {
                    return new HardwareSnapshotValueIndex(client.GetLatestSnapshot()).TryGetValue("FPS", out _);
                }
                catch
                {
                    return false;
                }
            }
        }

        public static void CleanMemory(Action<int>? onProgress = null)
            => SystemOptimizer.CleanMemory(onProgress);

        public static Task SmartCheckDriverAsync()
            => SupportsDriverCheck
                ? _monitor!.SmartCheckDriver()
                : Task.CompletedTask;

        public static Task<bool> RequestSamplingAsync(CancellationToken cancellationToken = default)
        {
            IHardwareClient? client = _client;
            return client is null
                ? Task.FromResult(false)
                : client.RequestSamplingAsync(cancellationToken);
        }

        // ── 采样协调（Web 需求同步器等） ─────────────────────────────────────────

        /// <summary>采集层尚未注册时返回 false，同步器下一轮重试（保持既有语义）。</summary>
        public static bool TrySetWebDataDemand(bool hasDemand)
        {
            IHardwareClient? client = _client;
            if (client is null) return false;

            try
            {
                if (hasDemand)
                    client.SubmitDemand(WebConsumerId, WebDemand);
                else
                    client.ReleaseDemand(WebConsumerId);
                return true;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        public static void RefreshHardwareConfig()
        {
            if (SupportsHardwareRefresh)
            {
                _monitor!.RefreshHardwareConfig();
            }
        }

        internal static async Task<HardwareSelectionOptions> GetSelectionOptionsAsync(
            CancellationToken cancellationToken = default)
        {
            IHardwareClient? client = _client;
            if (client is null) return HardwareSelectionOptions.Empty();

            if (_hostRequested)
            {
                HardwareCatalog catalog = await client.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
                return HardwareSelectionOptions.FromCatalog(catalog, hostLimited: true);
            }

            HardwareMonitor? monitor = _monitor;
            if (monitor is null) return HardwareSelectionOptions.Empty();

            return await Task.Run(() => new HardwareSelectionOptions(
                Disks: HardwareMonitor.ListAllDiskOptions(),
                Networks: HardwareMonitor.ListAllNetworks(),
                Gpus: HardwareMonitor.ListAllGpuOptions(),
                Fans: HardwareMonitor.ListAllFans(),
                MoboTemperatures: HardwareMonitor.ListAllMoboTemps(),
                CpuVoltageCandidates: HardwareMonitor.ListCpuVoltageCandidates(),
                IsHostLimited: false), cancellationToken).ConfigureAwait(false);
        }

        public static int DetectedGpuCount
        {
            get
            {
                IHardwareClient? client = _client;
                if (client is not null)
                {
                    try
                    {
                        int count = new HardwareSnapshotValueIndex(client.GetLatestSnapshot()).GpuCount;
                        if (count > 0) return count;
                    }
                    catch
                    {
                        // Fall through to the process-local compatibility source, if present.
                    }
                }

                return _monitor is null ? 0 : HardwareMonitor.TryGetDetectedGpuCount();
            }
        }

        public static DiskProbePolicy? DiskProbePolicy => _monitor?.DiskProbePolicy;

        public static bool ApplyDiskProbeMode(DiskProbeMode mode)
            => _monitor?.ApplyDiskProbeMode(mode) ?? false;

        public static bool ApplyControllerProbeMode(ControllerProbeMode mode)
            => _monitor?.ApplyControllerProbeMode(mode) ?? false;

        public static NetworkTrafficExclusionStatus? TrafficExclusionStatus
            => _monitor?.GetTrafficExclusionStatus();
    }
}
