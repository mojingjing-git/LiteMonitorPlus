using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace LiteMonitor.src.Plugins
{
    internal static class PluginKeyOwnership
    {
        internal static IEnumerable<(string RawKey, PluginOutput Output, int TargetIndex)> Outputs(
            PluginInstanceConfig instance, PluginTemplate template)
        {
            // ★ instance.Targets 是**可变自动属性**：Roslyn 不跟踪它的 null-state，
            //   「判空」与「解引用」一旦跨语句，流分析就失效（CS8602）。
            //   ⇒ 取局部变量，并把判空与解引用放进**同一个守卫块**；后续一律用局部变量
            //     （局部变量的 null-state 可被收窄，字段的不行）。
            var targets = instance.Targets;
            bool hasTargets = false;
            int count = 1;
            if (targets != null && targets.Count > 0)
            {
                hasTargets = true;
                count = targets.Count;
            }
            for (int i = 0; i < count; i++)
            foreach (var output in template.Outputs ?? new List<PluginOutput>())
                yield return (instance.Id + (hasTargets ? $".{i}" : "") + "." + output.Key,
                    output, hasTargets ? i : -1);
        }

        // Enumerate the complete keys written by Executor, initial values and schema
        // synchronization. Keep this snapshot after Stop, until those keys are cleared.
        internal static HashSet<string> PublicDataKeys(PluginInstanceConfig instance, PluginTemplate template)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in Outputs(instance, template))
            {
                string raw = binding.RawKey;
                string monitor = PluginConstants.DASH_PREFIX + raw;
                keys.UnionWith(new[] { raw, raw + ".Color", raw + ".Unit", monitor, monitor + ".Unit",
                    "PROP.Label." + monitor, "PROP.ShortLabel." + monitor });
            }
            if (template.Execution?.Type != "api_json" && template.Execution?.Type != "chain")
            {
                // ★ 同 Outputs()：Targets 是**可变自动属性**，Roslyn 不跟踪它的 null-state，
                //   判空与解引用必须放进同一个守卫块、后续一律用局部变量（消 CS8602）。
                var targets = instance.Targets;
                bool hasTargets = false;
                int count = 1;
                if (targets != null && targets.Count > 0)
                {
                    hasTargets = true;
                    count = targets.Count;
                }
                for (int i = 0; i < count; i++) keys.Add(instance.Id + (hasTargets ? $".{i}" : ""));
            }
            return keys;
        }

        private sealed class MonitorOwnership
        {
            internal readonly Dictionary<string, string> Owners = new(StringComparer.Ordinal);
        }

        // Scope the ledger to the Settings object, including isolated test settings.
        // Every access below is under that object's MonitorItems lock. Manager takes
        // its publication gate first; this helper never calls back into Manager.
        private static readonly ConditionalWeakTable<Settings, MonitorOwnership> MonitorLedgers = new();

        // ★ additionalId 允许为 null：MonitorOwner 查账本时不需要附加候选实例 id。
        private static MonitorOwnership Ledger(Settings settings, string? additionalId)
        {
            var ledger = MonitorLedgers.GetValue(settings, _ => new MonitorOwnership());
            foreach (var item in settings.MonitorItems)
            {
                if (ledger.Owners.ContainsKey(item.Key)) continue;
                // ★ 没有归属实例是正常结果（key 不属于任何插件实例）⇒ owner 必须可空。
                string? owner = ExistingOwner(settings, item.Key, additionalId);
                if (owner != null) ledger.Owners[item.Key] = owner;
            }
            return ledger;
        }

        // ★ 返回 null 表示「该 key 不属于任何插件实例」，是正常语义（不是异常路径）。
        private static string? ExistingOwner(Settings settings, string monitorKey, string? additionalId)
        {
            if (monitorKey == null || !monitorKey.StartsWith(PluginConstants.DASH_PREFIX, StringComparison.Ordinal)) return null;
            string raw = monitorKey.Substring(PluginConstants.DASH_PREFIX.Length);
            // ★ additionalId 为 null 时不追加候选：原代码 Append(null) 的结果会被下面的
            //   !string.IsNullOrEmpty(id) 过滤掉 ⇒ 两者等价，故显式判空而非传 null。
            IEnumerable<string> ids = settings.PluginInstances.Select(i => i.Id);
            if (!string.IsNullOrEmpty(additionalId)) ids = ids.Append(additionalId);
            // Import persisted keys only once, resolving the whole instance id before
            // binding each complete key. A removed parent's id remains a candidate;
            // a still configured A.B wins over A, regardless of configuration order.
            return ids
                .Where(id => !string.IsNullOrEmpty(id) && (raw == id || raw.StartsWith(id + ".", StringComparison.Ordinal)))
                .OrderByDescending(id => id.Length).FirstOrDefault();
        }

        internal static HashSet<string> MonitorKeys(Settings settings, string instanceId)
            => Ledger(settings, instanceId).Owners.Where(p => p.Value == instanceId)
                .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        // ★ 该 key 尚未归属任何实例时返回 null（正常语义，非异常）⇒ 返回类型可空。
        internal static string? MonitorOwner(Settings settings, string key)
        {
            var ledger = Ledger(settings, null);
            return ledger.Owners.TryGetValue(key, out var owner) ? owner : ExistingOwner(settings, key, null);
        }

        internal static void RememberMonitorKeys(Settings settings, string instanceId, HashSet<string> keys)
        {
            var ledger = Ledger(settings, instanceId);
            foreach (string old in ledger.Owners.Where(p => p.Value == instanceId).Select(p => p.Key).ToList())
                ledger.Owners.Remove(old);
            foreach (string key in keys) ledger.Owners[key] = instanceId;
        }
    }
}
