using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.Core;
using LiteMonitor.SystemServices.Pipe;

namespace LiteMonitor.SystemServices;

/// <summary>
/// Owns the one hardware data source selected for the current application process.
/// Host mode deliberately owns no <see cref="HardwareMonitor"/> instance.
/// </summary>
internal sealed class ApplicationHardwareRuntime : IDisposable
{
    private readonly Func<string?> _networkIpProvider;
    private readonly Func<Task>? _driverCheck;
    private bool _disposed;

    private ApplicationHardwareRuntime(
        IHardwareClient client,
        HardwareMonitor? localMonitor,
        bool hostRequested,
        Func<string?> networkIpProvider,
        Func<Task>? driverCheck)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        LocalMonitor = localMonitor;
        HostRequested = hostRequested;
        _networkIpProvider = networkIpProvider ?? throw new ArgumentNullException(nameof(networkIpProvider));
        _driverCheck = driverCheck;
    }

    public IHardwareClient Client { get; }

    public HardwareMonitor? LocalMonitor { get; }

    public bool HostRequested { get; }

    public HardwareSamplingStatistics? SamplingStatistics => LocalMonitor?.SamplingStatistics;

    public bool SupportsDriverCheck => _driverCheck is not null;

    public string? GetNetworkIP() => _networkIpProvider();

    public Task SmartCheckDriverAsync() => _driverCheck?.Invoke() ?? Task.CompletedTask;

    public static ApplicationHardwareRuntime Create(Settings cfg, bool hostRequested)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        if (hostRequested)
        {
            var client = new PipeHardwareClient(cfg.RefreshMs, fallbackNetworkIp: ResolveNetworkIp);
            return new ApplicationHardwareRuntime(
                client,
                localMonitor: null,
                hostRequested: true,
                networkIpProvider: ResolveNetworkIp,
                driverCheck: null);
        }

        var monitor = new HardwareMonitor(cfg);
        var adapter = new HardwareClientAdapter(monitor, cfg.MonitorItems.Select(item => item.Key).ToList());
        return new ApplicationHardwareRuntime(
            adapter,
            monitor,
            hostRequested: false,
            networkIpProvider: monitor.GetNetworkIP,
            driverCheck: monitor.SmartCheckDriver);
    }

    /// <summary>Test seam: mode is explicit and neither branch requires a real LHM computer.</summary>
    internal static ApplicationHardwareRuntime CreateForTests(
        Settings cfg,
        bool hostRequested,
        Func<IHardwareCollector> localCollectorFactory,
        Func<IHardwareClient> hostClientFactory,
        Func<string?>? networkIpProvider = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(localCollectorFactory);
        ArgumentNullException.ThrowIfNull(hostClientFactory);

        if (hostRequested)
        {
            return new ApplicationHardwareRuntime(
                hostClientFactory(),
                localMonitor: null,
                hostRequested: true,
                networkIpProvider: networkIpProvider ?? (() => null),
                driverCheck: null);
        }

        IHardwareCollector collector = localCollectorFactory();
        var client = new HardwareClientAdapter(collector, cfg.MonitorItems.Select(item => item.Key).ToList());
        return new ApplicationHardwareRuntime(
            client,
            localMonitor: null,
            hostRequested: false,
            networkIpProvider: networkIpProvider ?? collector.GetNetworkIP,
            driverCheck: null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Client.Dispose();
        LocalMonitor?.Dispose();
    }

    private static string? ResolveNetworkIp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter =>
                    adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Where(address =>
                    address.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !System.Net.IPAddress.IsLoopback(address.Address))
                .Select(address => address.Address.ToString())
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
