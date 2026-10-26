using System;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.src.SystemServices;
using LiteMonitor.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// E01 命令面（复审必改 1：消除孤儿接口成员）：进程内适配器的命令面语义断言。
    /// <para>覆盖：ApplyConfigAsync 委托既有逻辑、成功后回写 revision、失败给出可解释原因；
    /// RequestFullRescanAsync 有界并返回 HardwareOperationResult；
    /// ReleaseDemand 的字典移除语义。</para>
    /// <para>★ 语义与管道侧（P-E02）一一对应，便于做一致性对照。</para>
    /// </summary>
    public class E01AdapterCommandSurfaceTests
    {
        /// <summary>构造一个"不改变策略"的配置（ModeRaw=-1 ⇒ 不触发策略切换）。</summary>
        private static SamplingConfig Config(int revision) => new SamplingConfig(
            Revision: revision,
            RefreshMs: 1000,
            ControllerProbe: new ControllerProbePolicyData(-1, Array.Empty<string>()),
            DiskProbe: new DiskProbePolicyData(-1),
            Devices: Array.Empty<DeviceSelection>());

        [Fact]
        public async Task C1_ApplyConfigAsync_委托既有逻辑_成功后回写revision()
        {
            var collector = new E01FakeCollector();
            using var adapter = new HardwareClientAdapter(collector);

            SamplingApplyResult result = await adapter.ApplyConfigAsync(Config(11));

            Assert.True(result.Applied);
            Assert.Equal(11, result.Revision);
            Assert.Null(result.FailureReason);
            Assert.Equal(1, collector.RefreshCount);
            Assert.Equal(11, collector.RecordedRevision);          // ★ 生效 revision 回写
            Assert.Equal(11, collector.Publisher.ConfigRevision);  // ★ 进入已发布快照
        }

        [Fact]
        public async Task C2_ApplyConfigAsync_失败_返回可解释原因且不伪报成功()
        {
            var collector = new E01FakeCollector { ApplyFailure = new InvalidOperationException("控制器被占用") };
            using var adapter = new HardwareClientAdapter(collector);

            SamplingApplyResult result = await adapter.ApplyConfigAsync(Config(12));

            Assert.False(result.Applied);
            Assert.Null(result.Revision);
            Assert.Contains("控制器被占用", result.FailureReason);
            Assert.Equal(-1, collector.RecordedRevision);   // 失败不写 revision
        }

        [Fact]
        public async Task C3_RequestFullRescanAsync_有界_成功返回Completed与操作ID()
        {
            var collector = new E01FakeCollector { RescanResult = true };
            using var adapter = new HardwareClientAdapter(collector);

            HardwareOperationResult result = await adapter.RequestFullRescanAsync();

            Assert.True(result.Accepted);
            Assert.Equal(HardwareOperationState.Completed, result.State);
            Assert.False(string.IsNullOrEmpty(result.OperationId));
            Assert.Null(result.FailureReason);
            Assert.Equal(1, collector.RescanCount);
        }

        [Fact]
        public async Task C4_RequestFullRescanAsync_未执行_返回Failed且带原因()
        {
            var collector = new E01FakeCollector { RescanResult = false };
            using var adapter = new HardwareClientAdapter(collector);

            HardwareOperationResult result = await adapter.RequestFullRescanAsync();

            Assert.True(result.Accepted);
            Assert.Equal(HardwareOperationState.Failed, result.State);
            Assert.NotNull(result.FailureReason);
        }

        [Fact]
        public async Task C5_RequestFullRescanAsync_调用方取消_只结束等待不谎报底层已停()
        {
            var collector = new E01FakeCollector
            {
                RescanGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            using var adapter = new HardwareClientAdapter(collector);
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);

            HardwareOperationResult result = await adapter.RequestFullRescanAsync(cts.Token);

            Assert.True(result.Accepted);
            Assert.Equal(HardwareOperationState.Cancelled, result.State);
            Assert.NotNull(result.FailureReason);
            // ★ 底层重扫**没有**被取消（仍在进行中）：调用方取消只结束等待。
            Assert.False(collector.RescanGate.Task.IsCompleted);
        }

        [Fact]
        public async Task C5b_RequestFullRescanAsync_等待超时_返回Running且说明底层可能仍在执行()
        {
            var collector = new E01FakeCollector
            {
                RescanGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            using var adapter = new HardwareClientAdapter(collector) { FullRescanTimeout = TimeSpan.FromMilliseconds(80) };

            HardwareOperationResult result = await adapter.RequestFullRescanAsync();

            Assert.True(result.Accepted);
            Assert.Equal(HardwareOperationState.Running, result.State);
            Assert.NotNull(result.FailureReason);
            Assert.False(collector.RescanGate.Task.IsCompleted);   // ★ 底层没被取消
        }

        [Fact]
        public void C6_ReleaseDemand_字典移除_释放未登记返回false()
        {
            var collector = new E01FakeCollector();
            using var adapter = new HardwareClientAdapter(collector);

            adapter.SubmitDemand("sensor-window",
                new SamplingDemand(Ui: false, Taskbar: false, Web: false, History: false, SensorWindow: true));

            Assert.True(adapter.ReleaseDemand("sensor-window"));
            Assert.False(adapter.ReleaseDemand("sensor-window"));
            Assert.Equal(0, adapter.DemandConsumerCount);
        }

        [Fact]
        public async Task C7_适配器实现契约的新成员_不再抛NotSupportedException()
        {
            var collector = new E01FakeCollector();
            using IHardwareClient client = new HardwareClientAdapter(collector);

            // 契约 v2 的新成员在进程内实现上必须可用（消除孤儿）。
            SamplingApplyResult applied = await client.ApplyConfigAsync(Config(3));
            Assert.True(applied.Applied);

            HardwareOperationResult rescan = await client.RequestFullRescanAsync();
            Assert.True(rescan.Accepted);

            client.SubmitDemand("x", new SamplingDemand(false, false, false, false, false));
            Assert.True(client.ReleaseDemand("x"));
        }
    }
}
