using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.Contracts;
using LiteMonitor.src.Core.Alerts;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>R07：告警生产入口必须消费已发布快照，不能由 UI 缓存伪造批次/采样时刻。</summary>
    public sealed class R07AlertSnapshotTests
    {
        private static AlertRule Rule() =>
            new("cpu.temp", "CPU 温度", "CPU.Temp", AlertDirection.Above,
                80, 75, TimeSpan.Zero, TimeSpan.Zero,
                TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3),
                ReminderInterval: null, Enabled: true);

        private static HardwareSnapshot Snapshot(
            long batchId,
            float? value,
            SampleQuality quality = SampleQuality.Good,
            double ageMs = 0,
            string? deviceId = "cpu-stable",
            int sessionEpoch = 7)
        {
            return new HardwareSnapshot(
                batchId, 1, 1,
                new List<MetricSample>
                {
                    new("CPU.Temp", deviceId, value, "°C", quality, null, ageMs),
                },
                TimeSpan.FromMilliseconds(10),
                0,
                sessionEpoch);
        }

        [Fact]
        public void GoodSnapshotTriggersUsingStableDeviceId()
        {
            var runtime = new AlertRuntime();
            var transitions = runtime.Evaluate(
                new[] { Rule() }, Snapshot(11, 85), DateTimeOffset.UtcNow);

            Assert.Single(transitions);
            Assert.Equal("cpu-stable", transitions[0].DeviceId);
            Assert.Equal(AlertEventKind.Triggered, transitions[0].Kind);
        }

        [Fact]
        public void StaleSampleIsInputUnavailable()
        {
            var runtime = new AlertRuntime();
            var transitions = runtime.Evaluate(
                new[] { Rule() },
                Snapshot(12, 85, SampleQuality.Stale, ageMs: 5000),
                DateTimeOffset.UtcNow);

            Assert.Single(transitions);
            Assert.Equal(AlertEventKind.InputUnavailable, transitions[0].Kind);
        }

        [Fact]
        public void TooOldSampleIsInputUnavailable()
        {
            var runtime = new AlertRuntime();
            var transitions = runtime.Evaluate(
                new[] { Rule() },
                Snapshot(13, 85, ageMs: 30_001),
                DateTimeOffset.UtcNow);

            Assert.Single(transitions);
            Assert.Equal(AlertEventKind.InputUnavailable, transitions[0].Kind);
        }

        [Fact]
        public void DuplicateUiTickForSameBatchIsDeduplicated()
        {
            var runtime = new AlertRuntime();
            var now = DateTimeOffset.UtcNow;
            var rules = new[] { Rule() };

            var first = runtime.Evaluate(rules, Snapshot(14, 85), now);
            var second = runtime.Evaluate(rules, Snapshot(14, 85), now.AddSeconds(1));

            Assert.Single(first);
            Assert.Equal(AlertEventKind.Triggered, first[0].Kind);
            Assert.Empty(second);
        }
    }
}
