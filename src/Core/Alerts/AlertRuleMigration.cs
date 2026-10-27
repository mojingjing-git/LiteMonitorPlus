using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.Core.Alerts
{
    /// <summary>
    /// T05：把旧温度告警设置迁移成可解释规则。迁移幂等；已关闭的旧告警只生成禁用规则。
    /// </summary>
    public static class AlertRuleMigration
    {
        public const int CurrentVersion = 1;

        public static bool Apply(Settings settings, int monitoredGpuCount)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (settings.AlertRulesVersion >= CurrentVersion) return false;

            settings.AlertRules = Build(settings, monitoredGpuCount).ToList();
            settings.AlertRulesVersion = CurrentVersion;
            return true;
        }

        /// <summary>
        /// R16：监控 GPU 数量变化时增量补齐内置逐卡规则；不重建、不覆盖用户
        /// 已编辑的规则或事件状态。仅添加缺失的 temp.gpu.N。
        /// </summary>
        public static bool SyncGpuRules(Settings settings, int monitoredGpuCount)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.AlertRules ??= new List<AlertRule>();

            var ids = new HashSet<string>(settings.AlertRules.Select(r => r.RuleId), StringComparer.Ordinal);
            bool changed = false;
            int count = Math.Max(0, monitoredGpuCount);

            for (int i = 0; i < count; i++)
            {
                string id = $"temp.gpu.{i}";
                if (ids.Contains(id)) continue;

                settings.AlertRules.Add(TempRule(
                    id, $"GPU{i} 温度", GpuKey.Build(i, "Temp"),
                    settings.AlertTempThreshold, settings.AlertTempEnabled));
                changed = true;
            }

            return changed;
        }

        public static IReadOnlyList<AlertRule> Build(Settings settings, int monitoredGpuCount)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            int global = settings.AlertTempThreshold;
            int disk = Math.Min(global - 20, 60);
            bool enabled = settings.AlertTempEnabled;
            var rules = new List<AlertRule>
            {
                TempRule("temp.cpu", "CPU 温度", "CPU.Temp", global, enabled),
                TempRule("temp.mobo", "主板温度", "MOBO.Temp", global, enabled),
                TempRule("temp.disk", "磁盘温度", "DISK.Temp", disk, enabled)
            };

            int gpuCount = Math.Max(0, monitoredGpuCount);
            for (int i = 0; i < gpuCount; i++)
            {
                rules.Add(TempRule($"temp.gpu.{i}", $"GPU{i} 温度", GpuKey.Build(i, "Temp"), global, enabled));
            }

            return rules;
        }

        private static AlertRule TempRule(string id, string name, string metricKey, int trigger, bool enabled)
        {
            int recovery = Math.Max(0, trigger - 5);
            return new AlertRule(
                RuleId: id,
                Name: name,
                MetricKey: metricKey,
                Direction: AlertDirection.Above,
                TriggerThreshold: trigger,
                RecoveryThreshold: recovery,
                TriggerDuration: TimeSpan.FromSeconds(5),
                RecoveryDuration: TimeSpan.FromSeconds(10),
                MaxSampleInterval: TimeSpan.FromSeconds(30),
                NotificationCooldown: TimeSpan.FromMinutes(3),
                ReminderInterval: null,
                Enabled: enabled);
        }
    }
}
