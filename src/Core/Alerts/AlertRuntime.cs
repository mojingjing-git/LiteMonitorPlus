using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.Contracts;

namespace LiteMonitor.src.Core.Alerts
{
    /// <summary>
    /// T06：把一组规则接到一次采集批次上。只负责采样/状态机，不负责 UI 或通知。
    /// 同一批次只递增一次 BatchId，避免多规则各自计时造成抖动。
    /// </summary>
    public sealed class AlertRuntime
    {
        private AlertEvaluator _evaluator = new();
        private long _batchId;
        private IReadOnlyList<AlertRule>? _rulesSnapshot;
        private readonly Dictionary<string, string> _ruleSignatures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _devicesByMetric = new(StringComparer.Ordinal);

        // 快照时间戳与 UI 消费时刻不是同一时间轴：首个批次建立映射，
        // 后续批次只按源时间推进，排队/重绘延迟不能算作越限持续。
        private double? _sourceOriginMonoMs;
        private DateTimeOffset _sourceOriginWall;
        private int? _lastSnapshotEpoch;

        public string SessionId { get; } = Guid.NewGuid().ToString("N");

        public long CurrentBatchId => _batchId;

        /// <summary>
        /// R07：生产入口必须消费已发布的 HardwareSnapshot，不能再用 UI 缓存读数
        /// 伪造批次/采样时刻。同一 snapshot.BatchId 的重复 UI Tick 会被评估器丢弃。
        /// </summary>
        public IReadOnlyList<AlertTransition> Evaluate(
            IReadOnlyList<AlertRule> rules,
            HardwareSnapshot snapshot,
            DateTimeOffset now)
        {
            if (rules == null || rules.Count == 0 || snapshot == null)
                return Array.Empty<AlertTransition>();

            if (_lastSnapshotEpoch != snapshot.SessionEpoch)
            {
                _lastSnapshotEpoch = snapshot.SessionEpoch;
                _sourceOriginMonoMs = null;
            }

            Dictionary<string, MetricSample> samples = new(StringComparer.Ordinal);
            foreach (MetricSample sample in snapshot.Samples)
            {
                // 同一 Key 可能因设备/来源重复；保留第一条稳定身份样本。
                if (!samples.ContainsKey(sample.Key)) samples[sample.Key] = sample;
            }

            ApplyRuleSetChange(rules);
            long batchId = snapshot.BatchId;
            string sessionId = "snapshot:" + snapshot.SessionEpoch;
            var transitions = new List<AlertTransition>();

            foreach (AlertRule rule in rules)
            {
                if (rule == null || !rule.Enabled) continue;

                samples.TryGetValue(rule.MetricKey, out MetricSample? sample);
                double? value = sample?.Value;
                bool sampleGood = sample != null
                    && sample.Value.HasValue
                    && sample.Quality == SampleQuality.Good
                    && sample.SampleAgeMs <= rule.MaxSampleInterval.TotalMilliseconds;
                double sourceMonoMs = snapshot.TimestampMonoMs - Math.Max(0, sample?.SampleAgeMs ?? 0);
                DateTimeOffset observedAt = MapObservedAt(sourceMonoMs, now);

                if (!string.IsNullOrWhiteSpace(sample?.StableDeviceId))
                {
                    if (!_devicesByMetric.TryGetValue(rule.MetricKey, out HashSet<string>? devices))
                    {
                        devices = new HashSet<string>(StringComparer.Ordinal);
                        _devicesByMetric[rule.MetricKey] = devices;
                    }
                    devices.Add(sample!.StableDeviceId!);
                }

                // 采集失败批次中的 Missing 样本没有稳定身份。此时不能新建
                // "default" 状态保活原设备，必须把该指标已知的设备全部标记为
                // 不可用，否则原 Pending/Active 会把失败区间算进持续时间。
                bool identityMissing = string.IsNullOrWhiteSpace(sample?.StableDeviceId);
                IEnumerable<string> deviceIds;
                if (identityMissing && _devicesByMetric.TryGetValue(rule.MetricKey, out HashSet<string>? known))
                {
                    deviceIds = known.Count > 0 ? known.ToArray() : new[] { "default" };
                }
                else
                {
                    deviceIds = new[] { sample?.StableDeviceId ?? "default" };
                }

                foreach (string deviceId in deviceIds)
                {
                    bool valid = sampleGood && !identityMissing;
                    var alertSample = new AlertSample(
                        rule.MetricKey,
                        deviceId,
                        identityMissing ? null : value,
                        observedAt,
                        sessionId,
                        batchId,
                        IsValid: valid);

                    transitions.AddRange(_evaluator.Evaluate(rule, alertSample));
                }
            }

            return transitions;
        }

        private DateTimeOffset MapObservedAt(double sourceMonoMs, DateTimeOffset now)
        {
            if (sourceMonoMs <= 0) return now;

            if (!_sourceOriginMonoMs.HasValue)
            {
                _sourceOriginMonoMs = sourceMonoMs;
                _sourceOriginWall = now;
                return now;
            }

            // 源时间倒退（旧批次/时钟异常）时按源时间原样映射；评估器会
            // 丢弃倒序样本，不会倒扣持续时间。
            return _sourceOriginWall.AddMilliseconds(
                sourceMonoMs - _sourceOriginMonoMs.Value);
        }

        private void ApplyRuleSetChange(IReadOnlyList<AlertRule> rules)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (AlertRule rule in rules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.RuleId)) continue;
                present.Add(rule.RuleId);
                string signature = RuleSignature(rule);
                if (_ruleSignatures.TryGetValue(rule.RuleId, out string? previous)
                    && !string.Equals(previous, signature, StringComparison.Ordinal))
                {
                    // 同一 RuleId 的阈值/方向/持续/采样窗变化，旧 Pending 不能沿用。
                    _evaluator.ResetRule(rule.RuleId);
                }
                _ruleSignatures[rule.RuleId] = signature;
            }

            foreach (string removed in _ruleSignatures.Keys.Where(id => !present.Contains(id)).ToArray())
            {
                _evaluator.ResetRule(removed);
                _ruleSignatures.Remove(removed);
            }

            _rulesSnapshot = rules;
        }

        private static string RuleSignature(AlertRule rule)
        {
            static string F(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return string.Join('\u001f',
                rule.MetricKey,
                rule.Direction,
                F(rule.TriggerThreshold),
                F(rule.RecoveryThreshold),
                rule.TriggerDuration.Ticks,
                rule.RecoveryDuration.Ticks,
                rule.MaxSampleInterval.Ticks,
                rule.NotificationCooldown.Ticks,
                rule.ReminderInterval?.Ticks ?? -1,
                rule.Enabled,
                rule.Muted);
        }

        public IReadOnlyList<AlertTransition> Evaluate(
            IReadOnlyList<AlertRule> rules,
            Func<string, double?> readValue,
            DateTimeOffset now)
        {
            if (rules == null || rules.Count == 0 || readValue == null)
                return Array.Empty<AlertTransition>();

            if (!ReferenceEquals(_rulesSnapshot, rules))
            {
                _rulesSnapshot = rules;
                _evaluator = new AlertEvaluator();
                _batchId = 0;
            }

            long batchId = ++_batchId;
            var transitions = new List<AlertTransition>();
            foreach (AlertRule rule in rules)
            {
                if (rule == null || !rule.Enabled) continue;

                double? value = readValue(rule.MetricKey);
                bool valid = value.HasValue && double.IsFinite(value.Value);
                var sample = new AlertSample(
                    rule.MetricKey,
                    "default",
                    value,
                    now,
                    SessionId,
                    batchId,
                    IsValid: valid);

                transitions.AddRange(_evaluator.Evaluate(rule, sample));
            }

            return transitions;
        }

        public void Reset()
        {
            _evaluator = new AlertEvaluator();
            _batchId = 0;
            _ruleSignatures.Clear();
            _devicesByMetric.Clear();
            _sourceOriginMonoMs = null;
            _lastSnapshotEpoch = null;
        }

        public AlertState GetState(string ruleId, string deviceId = "default")
            => _evaluator.GetState(ruleId, deviceId);
    }
}
