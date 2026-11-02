using System.Collections.Generic;
using System.Buffers;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.src.Core;
using LiteMonitor.src.WebServer;
using LiteMonitor.SystemServices;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class ApplicationHardwareRuntimeTests
{
    [Fact]
    public void HostModeNeverInvokesTheLocalCollectorFactory()
    {
        var client = new RecordingHardwareClient(Snapshot());
        bool localFactoryCalled = false;

        using var runtime = ApplicationHardwareRuntime.CreateForTests(
            cfg: new Settings(),
            hostRequested: true,
            localCollectorFactory: () =>
            {
                localFactoryCalled = true;
                throw new InvalidOperationException("Host mode opened the local collector.");
            },
            hostClientFactory: () => client,
            networkIpProvider: () => "192.0.2.10");

        Assert.True(runtime.HostRequested);
        Assert.Null(runtime.LocalMonitor);
        Assert.Null(runtime.SamplingStatistics);
        Assert.False(runtime.SupportsDriverCheck);
        Assert.Same(client, runtime.Client);
        Assert.Equal("192.0.2.10", runtime.GetNetworkIP());
        Assert.False(localFactoryCalled);
    }

    [Fact]
    public void DefaultModeWrapsTheInjectedCollectorWithoutCallingTheHostFactory()
    {
        var collector = new E01FakeCollector();
        bool collectorFactoryCalled = false;

        using var runtime = ApplicationHardwareRuntime.CreateForTests(
            cfg: new Settings(),
            hostRequested: false,
            localCollectorFactory: () =>
            {
                collectorFactoryCalled = true;
                return collector;
            },
            hostClientFactory: () => throw new InvalidOperationException("Local mode used the Host client."));

        Assert.False(runtime.HostRequested);
        Assert.True(collectorFactoryCalled);
        Assert.Null(runtime.LocalMonitor);
        Assert.IsType<HardwareClientAdapter>(runtime.Client);
        runtime.Client.SubmitDemand(
            HardwareClientAdapter.WebConsumerId,
            new SamplingDemand(false, false, true, false, false));
        Assert.True(collector.WebDemand);
    }

    [Fact]
    public void GatewayUsesTheClientSnapshotAndKeyedWebDemandSlot()
    {
        var client = new RecordingHardwareClient(Snapshot(
            new MetricSample("CPU.Load", "cpu", 42.5f, "%", SampleQuality.Good, null, 0),
            new MetricSample("FPS", "game", 61f, "FPS", SampleQuality.Good, null, 0),
            new MetricSample("GPU.0.Load", "gpu", 73f, "%", SampleQuality.Good, null, 0)));
        HardwareGateway.Register(client, localMonitor: null, hostRequested: true);
        try
        {
            Assert.True(HardwareGateway.IsRegistered);
            Assert.True(HardwareGateway.IsHostMode);
            Assert.False(HardwareGateway.SupportsDriverCheck);
            Assert.False(HardwareGateway.SupportsDiskProbe);
            Assert.False(HardwareGateway.SupportsControllerProbe);
            Assert.False(HardwareGateway.SupportsTrafficExclusion);
            Assert.False(HardwareGateway.SupportsHardwareRefresh);
            Assert.Equal(42.5f, HardwareGateway.GetSampleValue("CPU.Load"));
            Assert.True(HardwareGateway.HasFpsData);
            Assert.Equal(1, HardwareGateway.DetectedGpuCount);

            Assert.True(HardwareGateway.TrySetWebDataDemand(true));
            Assert.True(client.Demands.TryGetValue("web", out SamplingDemand? demand));
            Assert.True(demand!.Web);

            Assert.True(HardwareGateway.TrySetWebDataDemand(false));
            Assert.False(client.Demands.ContainsKey("web"));
        }
        finally
        {
            HardwareGateway.Unregister(client);
        }

        Assert.False(HardwareGateway.IsRegistered);
    }

    [Fact]
    public void WebSnapshotReadsTheClientOnceForTheWholeResponse()
    {
        var cfg = new Settings
        {
            MonitorItems = new List<MonitorItemConfig>
            {
                new()
                {
                    Key = "CPU.Load",
                    VisibleInPanel = true,
                    SortIndex = 1
                }
            }
        };
        var client = new RecordingHardwareClient(
            Snapshot(new MetricSample("CPU.Load", "cpu", 42f, "%", SampleQuality.Good, null, 0)),
            () => Snapshot(new MetricSample(
                "CPU.Load",
                "cpu",
                42f,
                "%",
                SampleQuality.Good,
                null,
                0)));
        HardwareGateway.Register(client, localMonitor: null, hostRequested: true);
        using var server = new LiteWebServer(cfg);
        try
        {
            MethodInfo method = typeof(LiteWebServer).GetMethod(
                "WriteSnapshotJson",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("WriteSnapshotJson not found");
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                method.Invoke(server, new object[] { writer });
                writer.Flush();
            }

            Assert.Equal(1, client.SnapshotReads);
            using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
            JsonElement item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
            Assert.StartsWith("42", item.GetProperty("v").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            HardwareGateway.Unregister(client);
        }
    }

    [Fact]
    public void UiTickUsesOneSnapshotAndDoesNotReplayItAfterReadFailure()
    {
        string source = File.ReadAllText(FindRepoFile(
            Path.Combine("src", "UI", "UIController.cs")));
        int start = source.IndexOf("private async void Tick()", StringComparison.Ordinal);
        int end = start >= 0
            ? source.IndexOf("private void BuildMetrics()", start, StringComparison.Ordinal)
            : -1;

        Assert.True(start >= 0);
        Assert.True(end > start);
        string tick = source[start..end];

        Assert.Equal(1, CountOccurrences(tick, "_hwClient.GetLatestSnapshot()"));
        Assert.Contains("new HardwareSnapshotValueIndex(snapshot)", tick, StringComparison.Ordinal);
        Assert.Contains("if (snapshotReadSucceeded)", tick, StringComparison.Ordinal);
        Assert.Contains("HardwareHistoryLogger.RecordSnapshot(_cfg, _snapshotIndex)", tick, StringComparison.Ordinal);
        Assert.Contains("CheckRuleAlerts(snapshot, _snapshotIndex)", tick, StringComparison.Ordinal);
    }

    [Fact]
    public void HostSessionAndCatalogRefreshesCannotRetainStaleGpuMetadata()
    {
        string source = File.ReadAllText(FindRepoFile(
            Path.Combine("src", "UI", "UIController.cs")));
        int sessionStart = source.IndexOf("private void ApplySnapshotSession", StringComparison.Ordinal);
        int queueStart = sessionStart >= 0
            ? source.IndexOf("private void QueueGpuMetadataRefresh", sessionStart, StringComparison.Ordinal)
            : -1;
        int catalogEnd = queueStart >= 0
            ? source.IndexOf("private void RefreshPerCardGroupTitles", queueStart, StringComparison.Ordinal)
            : -1;

        Assert.True(sessionStart >= 0);
        Assert.True(queueStart > sessionStart);
        Assert.True(catalogEnd > queueStart);

        string sessionCode = source[sessionStart..queueStart];
        string catalogCode = source[queueStart..catalogEnd];
        Assert.Contains("GpuRegistry.Clear()", sessionCode, StringComparison.Ordinal);
        Assert.Contains("_currentSessionEpoch != expectedSessionEpoch", catalogCode, StringComparison.Ordinal);
        Assert.Contains("GpuRegistry.ReplaceAll(index > 0 ? gpuInfo : null)", catalogCode, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string FindRepoFile(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(directory, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException("Repository file not found.", relativePath);
    }

    private static HardwareSnapshot Snapshot(params MetricSample[] samples) =>
        new(
            BatchId: 11,
            CatalogVersion: 3,
            ConfigRevision: 2,
            Samples: samples,
            SamplingDuration: TimeSpan.FromMilliseconds(2),
            TimestampMonoMs: 5,
            SessionEpoch: 4);

    private sealed class RecordingHardwareClient : IHardwareClient
    {
        private readonly HardwareSnapshot _snapshot;
        private readonly Func<HardwareSnapshot>? _snapshotFactory;

        public RecordingHardwareClient(
            HardwareSnapshot snapshot,
            Func<HardwareSnapshot>? snapshotFactory = null)
        {
            _snapshot = snapshot;
            _snapshotFactory = snapshotFactory;
        }

        public Dictionary<string, SamplingDemand> Demands { get; } = new(StringComparer.Ordinal);

        public int SnapshotReads { get; private set; }

        public HardwareSnapshot GetLatestSnapshot()
        {
            SnapshotReads++;
            return _snapshotFactory?.Invoke() ?? _snapshot;
        }

        public Task<HardwareCatalog> GetCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardwareCatalog(3, Array.Empty<HardwareCatalogEntry>()));

        public HardwareHealth GetHealth() => new(HardwareState.Ready, null, 1, null);

        public string? GetNetworkIP() => "192.0.2.20";

        public void SubmitDemand(string consumerId, SamplingDemand demand) => Demands[consumerId] = demand;

        public bool ReleaseDemand(string consumerId) => Demands.Remove(consumerId);

        public Task<bool> RequestSamplingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public SamplingApplyResult ApplyConfig(SamplingConfig config) =>
            new(true, config.Revision, null);

        public Task<bool> RequestRescanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public void SubmitDemand(SamplingDemand demand)
        {
        }

        public void Dispose()
        {
        }
    }
}
