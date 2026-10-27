using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.src.Core.Alerts;
using Xunit;

namespace LiteMonitor.Tests
{
    public sealed class AlertRuntimeTests
    {
        private static AlertRule Rule(
            string id,
            string key = "CPU.Temp",
            bool enabled = true,
            int triggerSeconds = 0,
            double threshold = 80)
        {
            return new AlertRule(
                id, id, key, AlertDirection.Above, threshold, 75,
                TimeSpan.FromSeconds(triggerSeconds), TimeSpan.FromSeconds(0),
                TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3),
                ReminderInterval: null, Enabled: enabled);
        }

        [Fact]
        public void OneBatchEvaluatesAllRulesWithSameBatchId()
        {
            var runtime = new AlertRuntime();
            var rules = new[] { Rule("cpu"), Rule("gpu", "GPU.Temp") };
            var values = new Dictionary<string, double?> { ["CPU.Temp"] = 85, ["GPU.Temp"] = 85 };

            var transitions = runtime.Evaluate(rules, key => values[key], DateTimeOffset.UtcNow);

            Assert.Equal(2, transitions.Count);
            Assert.Equal(2, transitions.Select(t => t.RuleId).Distinct().Count());
            Assert.Equal(1, runtime.CurrentBatchId);
        }

        [Fact]
        public void DisabledRuleIsNotSampled()
        {
            var runtime = new AlertRuntime();
            bool read = false;

            var transitions = runtime.Evaluate(
                new[] { Rule("cpu", enabled: false) },
                _ => { read = true; return 99; },
                DateTimeOffset.UtcNow);

            Assert.Empty(transitions);
            Assert.False(read);
        }

        [Fact]
        public void MissingValueFeedsInputUnavailable()
        {
            var runtime = new AlertRuntime();
            var transitions = runtime.Evaluate(
                new[] { Rule("cpu", triggerSeconds: 0) },
                _ => null,
                DateTimeOffset.UtcNow);

            Assert.Single(transitions);
            Assert.Equal(AlertEventKind.InputUnavailable, transitions[0].Kind);
        }

        [Fact]
        public void ReplacingRuleListResetsEvaluator()
        {
            var runtime = new AlertRuntime();
            var first = new[] { Rule("cpu") };
            var t = DateTimeOffset.UtcNow;
            Assert.Single(runtime.Evaluate(first, _ => 85, t));

            var second = new[] { Rule("cpu") };
            var transitions = runtime.Evaluate(second, _ => 85, t.AddSeconds(1));

            Assert.Single(transitions);
            Assert.Equal(AlertEventKind.Triggered, transitions[0].Kind);
        }

        [Fact]
        public void ChangingRuleContentResetsPendingTimer()
        {
            var runtime = new AlertRuntime();
            DateTimeOffset t = DateTimeOffset.UtcNow;
            var pending = Rule("cpu", triggerSeconds: 30);

            Assert.Empty(runtime.Evaluate(new[] { pending }, _ => 85, t));
            Assert.Equal(AlertState.Pending, runtime.GetState("cpu"));

            var changed = Rule("cpu", triggerSeconds: 30, threshold: 90);
            var transitions = runtime.Evaluate(new[] { changed }, _ => 85, t.AddSeconds(10));

            Assert.Equal(AlertState.Normal, runtime.GetState("cpu"));
            Assert.DoesNotContain(transitions, x => x.Kind == AlertEventKind.Triggered);
        }

        [Fact]
        public void ResetClearsActiveState()
        {
            var runtime = new AlertRuntime();
            var rule = Rule("cpu");
            DateTimeOffset t = DateTimeOffset.UtcNow;

            Assert.Single(runtime.Evaluate(new[] { rule }, _ => 85, t));
            Assert.Equal(AlertState.Active, runtime.GetState("cpu"));
            runtime.Reset();
            Assert.Equal(AlertState.Normal, runtime.GetState("cpu"));
        }
    }
}
