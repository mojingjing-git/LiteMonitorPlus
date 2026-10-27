using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor;
using LiteMonitor.src.Core.Alerts;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>R15：恢复等待中的输入缺口不得被当成 Normal；R16：GPU 数量变化增量补规则。</summary>
    public sealed class R15R16AlertTests
    {
        private static AlertRule RecoveryRule() =>
            new("cpu.temp", "CPU 温度", "CPU.Temp", AlertDirection.Above,
                80, 75, TimeSpan.Zero, TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3),
                ReminderInterval: null, Enabled: true);

        private static AlertSample Sample(DateTimeOffset at, double? value, long batch, bool valid = true) =>
            new("CPU.Temp", "cpu", value, at, "s1", batch, valid);

        [Fact]
        public void GapWhileRecoveringBecomesInputUnavailable()
        {
            var evaluator = new AlertEvaluator();
            var rule = RecoveryRule();
            var t = DateTimeOffset.Parse("2026-10-27T00:00:00Z");

            Assert.Single(evaluator.Evaluate(rule, Sample(t, 85, 1)));
            Assert.Empty(evaluator.Evaluate(rule, Sample(t.AddSeconds(1), 75, 2)));
            Assert.Equal(AlertState.Recovering, evaluator.GetState(rule.RuleId, "cpu"));

            var events = evaluator.Evaluate(rule, Sample(t.AddSeconds(2), null, 3, valid: false));

            Assert.Single(events);
            Assert.Equal(AlertEventKind.InputUnavailable, events[0].Kind);
            Assert.Equal(AlertState.InputUnavailable, evaluator.GetState(rule.RuleId, "cpu"));
        }

        [Fact]
        public void SyncGpuRulesAddsMissingCardsAndPreservesExistingRule()
        {
            var settings = new Settings
            {
                AlertTempThreshold = 80,
                AlertTempEnabled = true,
                AlertRules = new List<AlertRule>
                {
                    new("temp.gpu.1", "GPU1 custom", "GPU.1.Temp", AlertDirection.Above,
                        99, 95, TimeSpan.Zero, TimeSpan.Zero,
                        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3),
                        ReminderInterval: null, Enabled: true),
                },
            };

            Assert.True(AlertRuleMigration.SyncGpuRules(settings, 3));

            Assert.Equal(3, settings.AlertRules.Count);
            Assert.Equal(3, settings.AlertRules.Select(r => r.RuleId).Distinct().Count());
            Assert.Contains(settings.AlertRules, r => r.RuleId == "temp.gpu.0");
            Assert.Contains(settings.AlertRules, r => r.RuleId == "temp.gpu.2");
            Assert.Equal(99, settings.AlertRules.Single(r => r.RuleId == "temp.gpu.1").TriggerThreshold);
            Assert.False(AlertRuleMigration.SyncGpuRules(settings, 3));
        }
    }
}
