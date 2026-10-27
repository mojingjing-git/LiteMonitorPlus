using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.Contracts;
using LiteMonitor.src.Core.Alerts;
using Xunit;

namespace LiteMonitor.Tests
{
    public sealed class AlertRuntimeSnapshotTests
    {
        private static AlertRule Rule(int triggerSeconds = 5, double trigger = 80)
            => new AlertRule(
                "cpu", "cpu", "CPU.Temp", AlertDirection.Above, trigger, trigger - 5,
                TimeSpan.FromSeconds(triggerSeconds), TimeSpan.Zero,
                TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3));

        private static HardwareSnapshot Snapshot(
            long batch, double timestampMonoMs, params MetricSample[] samples)
            => new HardwareSnapshot(batch, 1, 1, samples, TimeSpan.Zero, timestampMonoMs, 1);

        private static MetricSample Sample(
            string key = "CPU.Temp", double? value = 90, string? deviceId = "cpu-A",
            SampleQuality quality = SampleQuality.Good, double ageMs = 0)
            => new MetricSample(key, deviceId, (float?)value, "C", quality,
                quality == SampleQuality.Good ? null : "collection-failed", ageMs);

        [Fact]
        public void MissingIdentityMarksKnownDeviceUnavailable_InsteadOfCreatingDefault()
        {
            var runtime = new AlertRuntime();
            var rule = Rule();
            DateTimeOffset wall = DateTimeOffset.UtcNow;

            var first = runtime.Evaluate(
                new[] { rule },
                Snapshot(1, 1000, Sample()),
                wall);
            Assert.Empty(first); // Pending, not yet active

            var missing = runtime.Evaluate(
                new[] { rule },
                Snapshot(2, 2000, Sample(value: null, deviceId: null, quality: SampleQuality.Missing)),
                wall.AddSeconds(1));
            Assert.Contains(missing, t =>
                t.DeviceId == "cpu-A" && t.Kind == AlertEventKind.InputUnavailable);

            var recovered = runtime.Evaluate(
                new[] { rule },
                Snapshot(3, 7000, Sample()),
                wall.AddSeconds(6));

            // 失败区间不能补算为持续 5 秒：恢复后应重新进入 Pending，而不是立即 Active。
            Assert.DoesNotContain(recovered, t => t.Kind == AlertEventKind.Triggered);
            Assert.Equal(AlertState.Pending, runtime.GetState("cpu", "cpu-A"));
        }

        [Fact]
        public void QueueDelayAndRepeatedSourceTimeDoNotExtendTriggerDuration()
        {
            var runtime = new AlertRuntime();
            var rule = Rule();
            DateTimeOffset wall = DateTimeOffset.UtcNow;

            Assert.Empty(runtime.Evaluate(
                new[] { rule },
                Snapshot(1, 1000, Sample(ageMs: 0)),
                wall));

            // 第二批次在 UI 队列里晚了 6 秒，但源时刻仍是 1000ms；不能因此触发。
            var queued = runtime.Evaluate(
                new[] { rule },
                Snapshot(2, 2000, Sample(ageMs: 1000)),
                wall.AddSeconds(6));
            Assert.DoesNotContain(queued, t => t.Kind == AlertEventKind.Triggered);
            Assert.Equal(AlertState.Pending, runtime.GetState("cpu", "cpu-A"));
        }

        [Fact]
        public void RuleDefinitionChangeResetsOnlyChangedRulePending()
        {
            var runtime = new AlertRuntime();
            var original = Rule();
            DateTimeOffset wall = DateTimeOffset.UtcNow;

            Assert.Empty(runtime.Evaluate(
                new[] { original },
                Snapshot(1, 1000, Sample()),
                wall));

            var changed = original with { TriggerThreshold = 100, RecoveryThreshold = 95 };
            var afterChange = runtime.Evaluate(
                new[] { changed },
                Snapshot(2, 7000, Sample(value: 110)),
                wall.AddSeconds(6));

            // 新阈值只在这一份样本上满足，不能继承旧阈值下的 6 秒 Pending。
            Assert.DoesNotContain(afterChange, t => t.Kind == AlertEventKind.Triggered);
            Assert.Equal(AlertState.Pending, runtime.GetState("cpu", "cpu-A"));
        }
    }
}
