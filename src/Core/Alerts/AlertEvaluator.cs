using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteMonitor.src.Core.Alerts
{
    /// <summary>
    /// T05：纯规则引擎。只依赖样本和规则；通知由调用方按 AlertTransition.Notification 决定。
    /// 样本时间用于累计持续时间，重复批次/旧样本/会话切换不会补算。
    /// </summary>
    public sealed class AlertEvaluator
    {
        private readonly Dictionary<string, DeviceState> _states = new(StringComparer.Ordinal);

        public IReadOnlyList<AlertTransition> Evaluate(AlertRule rule, AlertSample sample)
        {
            if (rule == null || sample == null || !rule.Enabled) return Array.Empty<AlertTransition>();
            if (!rule.IsValid(out _)) return Array.Empty<AlertTransition>();
            if (!string.Equals(rule.MetricKey, sample.MetricKey, StringComparison.Ordinal))
                return Array.Empty<AlertTransition>();

            string key = rule.RuleId + "\u001f" + sample.DeviceId;
            if (!_states.TryGetValue(key, out DeviceState? state))
            {
                state = new DeviceState();
                _states[key] = state;
            }

            // 同一会话的重复批次、倒序批次直接丢弃；不增加持续时长。
            if (state.SessionId == sample.SessionId)
            {
                if (sample.BatchId <= state.LastBatchId) return Array.Empty<AlertTransition>();
                if (state.LastSampleAt.HasValue && sample.ObservedAt < state.LastSampleAt.Value)
                    return Array.Empty<AlertTransition>();
            }
            else if (state.SessionId != null)
            {
                // 换设备/换 Host 会话：旧事件不能跨会话确认恢复，但也不能继续累计。
                List<AlertTransition> reset = new();
                if (state.State == AlertState.Active && state.IncidentId != null)
                {
                    reset.Add(Transition(rule, sample, state, AlertState.InputUnavailable,
                        AlertEventKind.InputUnavailable, AlertNotificationDecision.NotApplicable, "session_changed"));
                }
                state.State = AlertState.Normal;
                state.PendingSince = null;
                state.RecoveringSince = null;
                state.LastSampleAt = null;
                state.IncidentId = null;
                if (reset.Count > 0)
                {
                    state.SessionId = sample.SessionId;
                    state.LastBatchId = sample.BatchId;
                    state.LastSampleAt = sample.ObservedAt;
                    state.State = AlertState.InputUnavailable;
                    return reset;
                }
            }

            bool gap = state.LastSampleAt.HasValue &&
                sample.ObservedAt - state.LastSampleAt.Value > rule.MaxSampleInterval;

            if (gap)
            {
                // 缺口不能补成持续越限；任何未关闭 Incident（Active/Recovering）
                // 都必须进入 InputUnavailable，等待有效输入后重走恢复等待。
                if ((state.State == AlertState.Active || state.State == AlertState.Recovering)
                    && state.IncidentId != null)
                {
                    state.State = AlertState.InputUnavailable;
                    state.PendingSince = null;
                    state.RecoveringSince = null;
                    state.SessionId = sample.SessionId;
                    state.LastBatchId = sample.BatchId;
                    state.LastSampleAt = sample.ObservedAt;
                    return new[]
                    {
                        Transition(rule, sample, state, AlertState.InputUnavailable,
                            AlertEventKind.InputUnavailable, AlertNotificationDecision.NotApplicable, "sample_gap")
                    };
                }
                state.PendingSince = null;
                state.RecoveringSince = null;
                state.State = AlertState.Normal;
            }

            if (!sample.HasValue)
            {
                state.SessionId = sample.SessionId;
                state.LastBatchId = sample.BatchId;
                state.LastSampleAt = sample.ObservedAt;
                state.PendingSince = null;
                state.RecoveringSince = null;

                if (state.State == AlertState.InputUnavailable)
                    return Array.Empty<AlertTransition>();

                state.State = AlertState.InputUnavailable;
                return new[]
                {
                    Transition(rule, sample, state, AlertState.InputUnavailable,
                        AlertEventKind.InputUnavailable, AlertNotificationDecision.NotApplicable, "input_unavailable")
                };
            }

            List<AlertTransition> events = new();
            AlertState previous = state.State;

            if (state.State == AlertState.InputUnavailable)
            {
                if (state.IncidentId != null)
                {
                    state.State = AlertState.Active;
                    events.Add(Transition(rule, sample, state, AlertState.Active,
                        AlertEventKind.InputRestored, AlertNotificationDecision.NotApplicable, "input_restored"));
                }
                else
                {
                    state.State = AlertState.Normal;
                    events.Add(Transition(rule, sample, state, AlertState.Normal,
                        AlertEventKind.InputRestored, AlertNotificationDecision.NotApplicable, "input_restored"));
                }
            }

            bool trigger = IsTrigger(rule, sample.Value!.Value);
            bool recovery = IsRecovery(rule, sample.Value!.Value);

            switch (state.State)
            {
                case AlertState.Normal:
                    if (trigger)
                    {
                        if (rule.TriggerDuration <= TimeSpan.Zero)
                        {
                            state.State = AlertState.Active;
                            state.IncidentId = $"{rule.RuleId}:{sample.DeviceId}:{sample.ObservedAt:O}";
                            AlertNotificationDecision immediate = DecideNotification(rule, state, sample.ObservedAt);
                            events.Add(Transition(rule, sample, state, AlertState.Active,
                                AlertEventKind.Triggered, immediate, "trigger_duration_met"));
                        }
                        else
                        {
                            state.State = AlertState.Pending;
                            state.PendingSince = sample.ObservedAt;
                        }
                    }
                    break;

                case AlertState.Pending:
                    if (!trigger)
                    {
                        state.State = AlertState.Normal;
                        state.PendingSince = null;
                    }
                    else
                    {
                        state.PendingSince ??= sample.ObservedAt;
                        if (sample.ObservedAt - state.PendingSince.Value >= rule.TriggerDuration)
                        {
                            state.State = AlertState.Active;
                            state.IncidentId = $"{rule.RuleId}:{sample.DeviceId}:{sample.ObservedAt:O}";
                            state.PendingSince = null;
                            state.RecoveringSince = null;
                            AlertNotificationDecision decision = DecideNotification(rule, state, sample.ObservedAt);
                            events.Add(Transition(rule, sample, state, AlertState.Active,
                                AlertEventKind.Triggered, decision, "trigger_duration_met"));
                        }
                    }
                    break;

                case AlertState.Active:
                    if (recovery)
                    {
                        state.RecoveringSince ??= sample.ObservedAt;
                        if (sample.ObservedAt - state.RecoveringSince.Value >= rule.RecoveryDuration)
                        {
                            state.State = AlertState.Normal;
                            AlertNotificationDecision decision = DecideNotification(rule, state, sample.ObservedAt);
                            events.Add(Transition(rule, sample, state, AlertState.Normal,
                                AlertEventKind.Recovered, decision, "recovery_duration_met"));
                            state.IncidentId = null;
                            state.RecoveringSince = null;
                        }
                        else
                        {
                            state.State = AlertState.Recovering;
                        }
                    }
                    else
                    {
                        state.RecoveringSince = null;
                        if (rule.ReminderInterval.HasValue &&
                            state.LastNotificationAt.HasValue &&
                            sample.ObservedAt - state.LastNotificationAt.Value >= rule.ReminderInterval.Value)
                        {
                            AlertNotificationDecision decision = DecideNotification(rule, state, sample.ObservedAt);
                            events.Add(Transition(rule, sample, state, AlertState.Active,
                                AlertEventKind.Reminder, decision, "reminder_interval_elapsed"));
                        }
                    }
                    break;

                case AlertState.Recovering:
                    if (trigger)
                    {
                        // 回差区间内再次越上限：回到同一活动事件，不重复触发。
                        state.State = AlertState.Active;
                        state.RecoveringSince = null;
                    }
                    else if (recovery)
                    {
                        state.RecoveringSince ??= sample.ObservedAt;
                        if (sample.ObservedAt - state.RecoveringSince.Value >= rule.RecoveryDuration)
                        {
                            state.State = AlertState.Normal;
                            AlertNotificationDecision decision = DecideNotification(rule, state, sample.ObservedAt);
                            events.Add(Transition(rule, sample, state, AlertState.Normal,
                                AlertEventKind.Recovered, decision, "recovery_duration_met"));
                            state.IncidentId = null;
                            state.RecoveringSince = null;
                        }
                    }
                    else
                    {
                        // 进入触发/恢复阈值之间：仍视为活动，不确认恢复。
                        state.State = AlertState.Active;
                        state.RecoveringSince = null;
                    }
                    break;
            }

            state.SessionId = sample.SessionId;
            state.LastBatchId = sample.BatchId;
            state.LastSampleAt = sample.ObservedAt;
            _ = previous;
            return events;
        }

        /// <summary>规则定义变化时只清该规则的状态；等价规则集合保持原计时。</summary>
        public void ResetRule(string ruleId)
        {
            if (string.IsNullOrWhiteSpace(ruleId)) return;
            string prefix = ruleId + "\u001f";
            foreach (string key in _states.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                _states.Remove(key);
            }
        }

        public AlertState GetState(string ruleId, string deviceId)
        {
            return _states.TryGetValue(ruleId + "\u001f" + deviceId, out DeviceState? state)
                ? state.State
                : AlertState.Normal;
        }

        public string? GetIncidentId(string ruleId, string deviceId)
        {
            return _states.TryGetValue(ruleId + "\u001f" + deviceId, out DeviceState? state)
                ? state.IncidentId
                : null;
        }

        private static bool IsTrigger(AlertRule rule, double value) =>
            rule.Direction == AlertDirection.Above
                ? value >= rule.TriggerThreshold
                : value <= rule.TriggerThreshold;

        private static bool IsRecovery(AlertRule rule, double value) =>
            rule.Direction == AlertDirection.Above
                ? value <= rule.RecoveryThreshold
                : value >= rule.RecoveryThreshold;

        private static AlertNotificationDecision DecideNotification(
            AlertRule rule, DeviceState state, DateTimeOffset at)
        {
            if (rule.Muted) return AlertNotificationDecision.SuppressedByMute;
            if (state.LastNotificationAt.HasValue &&
                at - state.LastNotificationAt.Value < rule.NotificationCooldown)
                return AlertNotificationDecision.SuppressedByCooldown;

            state.LastNotificationAt = at;
            return AlertNotificationDecision.Notify;
        }

        private static AlertTransition Transition(
            AlertRule rule, AlertSample sample, DeviceState state,
            AlertState to, AlertEventKind kind,
            AlertNotificationDecision notification, string reason)
        {
            return new AlertTransition(rule.RuleId, sample.DeviceId, to, kind,
                sample.ObservedAt, state.IncidentId, notification, reason);
        }

        private sealed class DeviceState
        {
            public AlertState State = AlertState.Normal;
            public string? SessionId;
            public long LastBatchId = long.MinValue;
            public DateTimeOffset? LastSampleAt;
            public DateTimeOffset? PendingSince;
            public DateTimeOffset? RecoveringSince;
            public DateTimeOffset? LastNotificationAt;
            public string? IncidentId;
        }
    }
}
