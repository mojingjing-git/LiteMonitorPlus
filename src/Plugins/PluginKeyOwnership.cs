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
            bool hasTargets = instance.Targets != null && instance.Targets.Count > 0;
            int count = hasTargets ? instance.Targets.Count : 1;
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
                bool hasTargets = instance.Targets != null && instance.Targets.Count > 0;
                int count = hasTargets ? instance.Targets.Count : 1;
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

        private static MonitorOwnership Ledger(Settings settings, string additionalId)
        {
            var ledger = MonitorLedgers.GetValue(settings, _ => new MonitorOwnership());
            foreach (var item in settings.MonitorItems)
            {
                if (ledger.Owners.ContainsKey(item.Key)) continue;
                string owner = ExistingOwner(settings, item.Key, additionalId);
                if (owner != null) ledger.Owners[item.Key] = owner;
            }
            return ledger;
        }

        private static string ExistingOwner(Settings settings, string monitorKey, string additionalId)
        {
            if (monitorKey == null || !monitorKey.StartsWith(PluginConstants.DASH_PREFIX, StringComparison.Ordinal)) return null;
            string raw = monitorKey.Substring(PluginConstants.DASH_PREFIX.Length);
            // Import persisted keys only once, resolving the whole instance id before
            // binding each complete key. A removed parent's id remains a candidate;
            // a still configured A.B wins over A, regardless of configuration order.
            return settings.PluginInstances.Select(i => i.Id).Append(additionalId)
                .Where(id => !string.IsNullOrEmpty(id) && (raw == id || raw.StartsWith(id + ".", StringComparison.Ordinal)))
                .OrderByDescending(id => id.Length).FirstOrDefault();
        }

        internal static HashSet<string> MonitorKeys(Settings settings, string instanceId)
            => Ledger(settings, instanceId).Owners.Where(p => p.Value == instanceId)
                .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        internal static string MonitorOwner(Settings settings, string key)
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
