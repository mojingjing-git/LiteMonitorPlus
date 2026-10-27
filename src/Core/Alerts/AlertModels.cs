using System;

namespace LiteMonitor.src.Core.Alerts
{
    public enum AlertDirection
    {
        Above,
        Below
    }

    public enum AlertState
    {
        Normal,
        Pending,
        Active,
        Recovering,
        InputUnavailable
    }

    public enum AlertEventKind
    {
        Triggered,
        Recovered,
        InputUnavailable,
        InputRestored,
        Reminder
    }

    public enum AlertNotificationDecision
    {
        Notify,
        SuppressedByCooldown,
        SuppressedByMute,
        NotApplicable
    }

    /// <summary>T05：可持久化的告警规则；不持有窗体、采集器或通知 API。</summary>
    public sealed record AlertRule(
        string RuleId,
        string Name,
        string MetricKey,
        AlertDirection Direction,
        double TriggerThreshold,
        double RecoveryThreshold,
        TimeSpan TriggerDuration,
        TimeSpan RecoveryDuration,
        TimeSpan MaxSampleInterval,
        TimeSpan NotificationCooldown,
        TimeSpan? ReminderInterval = null,
        bool Enabled = true,
        bool Muted = false)
    {
        public bool IsValid(out string? error)
        {
            if (string.IsNullOrWhiteSpace(RuleId)) { error = "rule_id_empty"; return false; }
            if (string.IsNullOrWhiteSpace(MetricKey)) { error = "metric_key_empty"; return false; }
            if (!double.IsFinite(TriggerThreshold) || !double.IsFinite(RecoveryThreshold))
            {
                error = "threshold_not_finite"; return false;
            }
            if (Direction == AlertDirection.Above && RecoveryThreshold >= TriggerThreshold)
            {
                error = "recovery_must_be_below_trigger"; return false;
            }
            if (Direction == AlertDirection.Below && RecoveryThreshold <= TriggerThreshold)
            {
                error = "recovery_must_be_above_trigger"; return false;
            }
            if (TriggerDuration < TimeSpan.Zero || RecoveryDuration < TimeSpan.Zero)
            {
                error = "duration_negative"; return false;
            }
            if (MaxSampleInterval <= TimeSpan.Zero)
            {
                error = "max_sample_interval_must_be_positive"; return false;
            }
            if (NotificationCooldown < TimeSpan.Zero)
            {
                error = "cooldown_negative"; return false;
            }
            if (ReminderInterval.HasValue && ReminderInterval.Value <= TimeSpan.Zero)
            {
                error = "reminder_interval_must_be_positive"; return false;
            }
            error = null; return true;
        }
    }

    /// <summary>标准化样本；Value=null 或 IsValid=false 表示输入不可用。</summary>
    public sealed record AlertSample(
        string MetricKey,
        string DeviceId,
        double? Value,
        DateTimeOffset ObservedAt,
        string SessionId,
        long BatchId,
        bool IsValid = true)
    {
        public bool HasValue => IsValid && Value.HasValue && double.IsFinite(Value.Value);
    }

    public sealed record AlertTransition(
        string RuleId,
        string DeviceId,
        AlertState State,
        AlertEventKind Kind,
        DateTimeOffset ObservedAt,
        string? IncidentId,
        AlertNotificationDecision Notification,
        string ReasonCode)
    {
        public bool ShouldNotify => Notification == AlertNotificationDecision.Notify;
    }
}
