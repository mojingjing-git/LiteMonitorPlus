using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.src.SystemServices;
using LiteMonitor.SystemServices;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// E01 测试替身：可编程的采集层能力面（<see cref="IHardwareCollector"/>）。
    /// <para>★ 目的：命令面 / 需求面 / 快照面 / 目录面都能在**不打开真实硬件树**的环境里被断言
    /// （<c>HardwareMonitor</c> 的构造函数会 Open LHM 硬件树，单测不可实例化）。</para>
    /// </summary>
    internal sealed class E01FakeCollector : IHardwareCollector
    {
        public SnapshotPublisher Publisher { get; } = new SnapshotPublisher();
        public CatalogPublisher Catalog { get; } = new CatalogPublisher();

        public bool IsReady { get; set; } = true;
        public int SamplingGeneration { get; set; }
        public bool WebDemand { get; private set; }
        public int RecordedRevision { get; private set; } = -1;
        public int RefreshCount { get; private set; }
        public List<int> AppliedControllerModes { get; } = new List<int>();
        public List<int> AppliedDiskModes { get; } = new List<int>();
        public int RescanCount { get; private set; }
        public bool RescanResult { get; set; } = true;

        /// <summary>测试用：让重扫"永不自行完成"（用于验证等待超时 / 取消只结束等待）。</summary>
        public TaskCompletionSource<bool>? RescanGate { get; set; }

        public Exception? ApplyFailure { get; set; }

        public HardwareSnapshot? PublishedSnapshot => Publisher.HasPublished ? Publisher.Current : null;

        public HardwareCatalog? PublishedCatalog =>
            Catalog.Current.Entries.Count == 0 ? null : Catalog.Current;

        public string? CurrentMissingReason => Publisher.CurrentMissingReason;

        public float? Get(string key) => null;

        public string? GetNetworkIP() => null;

        public void SetWebDataDemand(bool hasDemand) => WebDemand = hasDemand;

        public void RecordSamplingConfigRevision(int revision)
        {
            RecordedRevision = revision;
            Publisher.RecordConfigRevision(revision);
        }

        public bool ApplyControllerProbeMode(ControllerProbeMode mode)
        {
            AppliedControllerModes.Add((int)mode);
            if (ApplyFailure != null) throw ApplyFailure;
            return true;
        }

        public bool ApplyDiskProbeMode(DiskProbeMode mode)
        {
            AppliedDiskModes.Add((int)mode);
            if (ApplyFailure != null) throw ApplyFailure;
            return true;
        }

        public void RefreshHardwareConfig()
        {
            RefreshCount++;
            if (ApplyFailure != null) throw ApplyFailure;
        }

        public Task<bool> RequestSamplingAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> RequestFullRescanAsync(CancellationToken cancellationToken)
        {
            RescanCount++;
            if (RescanGate != null) return RescanGate.Task;
            return Task.FromResult(RescanResult);
        }
    }
}
