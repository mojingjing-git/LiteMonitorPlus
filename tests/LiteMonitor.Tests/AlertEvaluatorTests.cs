using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor;
using LiteMonitor.src.Core.Alerts;
using Xunit;

namespace LiteMonitor.Tests
{
    public sealed class AlertEvaluatorTests
    {
        private static AlertRule Rule(
            double trigger = 80,
            double recovery = 75,
            int triggerSeconds = 5,
            int recoverySeconds = 10,
            int cooldownSeconds = 180,
            bool muted = false,
            TimeSpan? reminder = null)
        {
            return new AlertRule(
                "temp.cpu", "CPU 温度", "CPU.Temp", AlertDirection.Above,
                trigger, recovery,
                TimeSpan.FromSeconds(triggerSeconds),
                TimeSpan.FromSeconds(recoverySeconds),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(cooldownSeconds),
                reminder, Enabled: true, Muted: muted);
        }

        private static AlertSample Sample(
            DateTimeOffset at, double? value = 85, string device = "cpu",
            string session = "s1", long batch = 1, bool valid = true)
        {
            return new AlertSample("CPU.Temp", device, value, at, session, batch, valid);
        }

        [Fact]
        public void TriggerNeedsContinuousDuration()
        {
            var evaluator = new AlertEvaluator();
            var rule = Rule();
            var t = DateTimeOffset.Parse("2026-10-27T00:00:00Z");

            Assert.Empty(evaluator.Evaluate(rule, Sample(t, 85, batch: 1)));
            Assert.Empty(evaluator.Evaluate(rule, Sample(t.AddSeconds(4), 85, batch: 2)));
            var events = evaluator.Evaluate(rule, Sample(t.AddSeconds(5), 85, batch: 3));

            Assert.Single(events);
            Assert.Equal(AlertEventKind.Triggered, events[0].Kind);
            Assert.Equal(AlertState.Active, events[0].State);
            Assert.NotNull(events[0].IncidentId);
        }

        [Fact]
        public void SampleGapDoesNotCountAsContinuousTime()
        {
            var evaluator = new AlertEvaluator();
            var rule = Rule();
            var t = DateTimeOffset.Parse("2026-10-27T00:00:00Z");

            evaluator.Evaluate(rule, Sample(t, 85, batch: 1));
            evaluator.Evaluate(rule, Sample(t.AddSeconds(4), 85, batch: 2));
            evaluator.Evaluate(rule, Sample(t.AddSeconds(40), 85, batch: 3));
            var events = evaluator.Evaluate(rule, Sample(t.AddSeconds(45), 85, batch: 4));

            Assert.Single(events);
            Assert.Equal(AlertEventKind.Triggered, events[0].Kind);
        }

        [Fact]
        public void RecoveryUsesRecoveryThresholdAndDuration()
        {
            var evaluator = new AlertEvaluator();
            var rule = Rule();
            var t = DateTimeOffset.Parse("2026-10-27T00:00:00Z");

            evaluator.Evaluate(rule, Sample(t, 85, batch: 1));
            evaluator.Evaluate(rule, Sample(t.AddSeconds(5), 85, batch: 2));
            Assert.Equal(AlertState.Active, evaluator.GetState(rule.RuleId, "cpu"));

            Assert.Empty(evaluator.Evaluate(rule, Sample(t.AddSeconds(6), 75, batch: 3)));
            Assert.Equal(AlertState.Recovering, evaluator.GetState(rule.RuleId, "cpu"));
            var recovered = evaluator.Evaluate(rule, Sample(t.AddSeconds(16), 74, batch: 4));

            Assert.Single(recovered);
            Assert.Equal(AlertEventKind.Recovered, recovered[0].Kind);
            Assert.Equal(AlertState.Normal, recovered[0].State);
            Assert.Null(evaluator.GetIncidentId(rule.RuleId, "cpu"));
        }

        [Fact]
        public void ValueBetweenThresholdsStaysActive()
        {
            var evaluator = new AlertEvaluator();
            var rule = Rule(recoverySeconds: 5);
            var t = DateTimeOffset.Parse("2026-10-27T00:00:00Z");

            evaluator.Evaluate(rule, Sample(t, 85, batch: 1));
            evaluator.Evaluate(rule, Sample(t.AddSeconds(5), 85, batch: 2));
            Assert.Empty(evaluator.Evaluate(rule, Sample(t.AddSeconds(6), 78, batch: 3)));
            Assert.Equal(AlertState.Active, evaluator.GetState(rule.RuleId, "cpu"));
            Assert.Empty(evaluator.Evaluate(rule, Sample(t.AddSeconds(7), 74, batch: 4)));
            Assert.Equal(AlertState.Recovering, evaluator.GetState(rule.RuleId, "cpu"));
            Assert.Empty(evaluator.Evaluate(rule, Sample(t.AddSeconds(8), 79, batch: 5)));
            Assert.Equal(AlertState.Active, evaluator.GetState(rule.RuleId, "cpu"));
        }

        [Fact]
        public void CooldownSuppressesNotificationButKeepsEvent()
        {
            var evaluator = new AlertEvaluator();
            var rule = Rule(triggerSeconds: 0, recoverySeconds: 0, cooldownSeconds: 180);
            var t = DateTimeOffset.Parse("2026-10-27T00:00:00Z");

            var first = evaluator.Evaluate(rule, Sample(t, 85, batch: 1));
            Assert.Equal(AlertNotificationDecision.Notify, first[0].Notification);
            var recovered = evaluator.Evaluate(rule, Sample(t.AddSeconds(1), 74, batch: 2));
            Assert.Equal(AlertEventKind.Recovered, recovered[0].Kind);
            var second = evaluator.Evaluate(rule, Sample(t.AddSeconds(2), 85, batch: 3));

            Assert.Equal(AlertEventKind.Triggered, second[0].Kind);
            Assert.Equal(AlertNotificationDecision.SuppressedByCooldown, second[0].Notification);
        }

        [Fact]
        public void MutedRuleStillEvaluatesState()
        {
            var evaluator = new AlertEvaluator();
            var rule = Rule(triggerSeconds: 0, muted: true);
            var t = DateTimeOffset.Parse("2026-10-27T00:00:00Z");

            var events = evaluator.Evaluate(rule, Sample(t, 85, batch: 1));

            Assert.Single(events);
            Assert.Equal(AlertEventKind.Triggered, events[0].Kind);
            Assert.Equal(AlertNotificationDecision.SuppressedByMute, events[0].Notification);
            Assert.False(events[0].ShouldNotify);
        }

        [Fact]
        public void DuplicateAndOutOfOrderBatchesAreIgnored()
        {
            var evaluator = new AlertEvaluator();
            var rule = Rule(triggerSeconds: 300);
            var t = DateTimeOffset.Parse("2026-10-27T00:00:00Z");

            evaluator.Evaluate(rule, Sample(t, 85, batch: 10));
            Assert.Empty(evaluator.Evaluate(rule, Sample(t, 85, batch: 10)));
            Assert.Empty(evaluator.Evaluate(rule, Sample(t.AddSeconds(-1), 85, batch: 9)));
            Assert.Empty(evaluator.Evaluate(rule, Sample(t.AddSeconds(1), 85, batch: 11)));
            Assert.Equal(AlertState.Pending, evaluator.GetState(rule.RuleId, "cpu"));
        }

        [Fact]
        public void MultipleDevicesHaveIndependentIncidents()
        {
            var evaluator = new AlertEvaluator();
            var rule = Rule(triggerSeconds: 0);
            var t = DateTimeOffset.Parse("2026-10-27T00:00:00Z");

            var a = evaluator.Evaluate(rule, Sample(t, 85, device: "gpu0", batch: 1));
            var b = evaluator.Evaluate(rule, Sample(t, 40, device: "gpu1", batch: 1));

            Assert.Single(a);
            Assert.Empty(b);
            Assert.NotEqual(evaluator.GetIncidentId(rule.RuleId, "gpu0"),
                evaluator.GetIncidentId(rule.RuleId, "gpu1"));
        }

        [Fact]
        public void InputUnavailableKeepsActiveIncidentUntilValidRecovery()
        {
            var evaluator = new AlertEvaluator();
            var rule = Rule(triggerSeconds: 0);
            var t = DateTimeOffset.Parse("2026-10-27T00:00:00Z");

            evaluator.Evaluate(rule, Sample(t, 85, batch: 1));
            string? incident = evaluator.GetIncidentId(rule.RuleId, "cpu");
            var unavailable = evaluator.Evaluate(rule, Sample(t.AddSeconds(1), null, batch: 2, valid: false));

            Assert.Single(unavailable);
            Assert.Equal(AlertEventKind.InputUnavailable, unavailable[0].Kind);
            Assert.Equal(incident, unavailable[0].IncidentId);
            Assert.Equal(AlertState.InputUnavailable, evaluator.GetState(rule.RuleId, "cpu"));

            var restored = evaluator.Evaluate(rule, Sample(t.AddSeconds(2), 85, batch: 3));
            Assert.Single(restored);
            Assert.Equal(AlertEventKind.InputRestored, restored[0].Kind);
            Assert.Equal(incident, restored[0].IncidentId);
            Assert.Equal(AlertState.Active, evaluator.GetState(rule.RuleId, "cpu"));
        }

        [Fact]
        public void MigrationIsIdempotentAndPreservesDisabledState()
        {
            var settings = new Settings
            {
                AlertTempEnabled = false,
                AlertTempThreshold = 80
            };

            Assert.True(AlertRuleMigration.Apply(settings, monitoredGpuCount: 1));
            Assert.Equal(AlertRuleMigration.CurrentVersion, settings.AlertRulesVersion);
            Assert.Equal(4, settings.AlertRules.Count);
            Assert.All(settings.AlertRules, r => Assert.False(r.Enabled));
            Assert.Equal(60, settings.AlertRules.Single(r => r.RuleId == "temp.disk").TriggerThreshold);
            Assert.False(AlertRuleMigration.Apply(settings, monitoredGpuCount: 1));
            Assert.Equal(4, settings.AlertRules.Count);
        }
    }
}
