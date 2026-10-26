using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件监控项同步服务
    /// 负责将插件配置同步到全局 MonitorItems 设置中
    /// </summary>
    public class PluginMonitorSyncService
    {
        // ★ 单例是"懒加载"语义：未调用 Instance 前确实为 null。
        private static PluginMonitorSyncService? _instance;
        public static PluginMonitorSyncService Instance => _instance ??= new PluginMonitorSyncService();

        private PluginMonitorSyncService() { }

        /// <summary>
        /// 同步指定插件实例的监控项到全局设置
        /// </summary>
        /// <param name="inst">插件实例配置</param>
        /// <param name="tmpl">插件模板</param>
        public bool SyncMonitorItem(PluginInstanceConfig inst, PluginTemplate tmpl, bool saveIfChanged = true)
        {
            if (inst == null || tmpl == null) return false;

            // 优化 1: 正则预编译 (Static Readonly)
            // 移至类级别定义
            
            var settings = Settings.Load();
            bool changed = false;

            var targets = inst.Targets != null && inst.Targets.Count > 0 ? inst.Targets : new List<Dictionary<string, string>> { new Dictionary<string, string>() };
            var validKeys = PluginKeyOwnership.Outputs(inst, tmpl)
                .Select(binding => PluginConstants.DASH_PREFIX + binding.RawKey).ToHashSet(StringComparer.Ordinal);

            // 优化 2: 高风险并发崩溃修复 - 加锁
            lock (settings.MonitorItems)
            {
                var ownedKeys = PluginKeyOwnership.MonitorKeys(settings, inst.Id);
                for (int i = 0; i < targets.Count; i++)
                {
                    // ... (保持原有循环逻辑，但被锁保护)
                    var targetInputs = targets[i];
                    var mergedInputs = new Dictionary<string, string>(inst.InputValues);
                    
                    // Merge Target Inputs
                    foreach (var kv in targetInputs) mergedInputs[kv.Key] = kv.Value;

                    // Apply Defaults
                    if (tmpl.Inputs != null)
                    {
                        foreach (var input in tmpl.Inputs)
                        {
                            if (input.DefaultValue != null && (!mergedInputs.ContainsKey(input.Key) || string.IsNullOrEmpty(mergedInputs[input.Key])))
                            {
                                mergedInputs[input.Key] = input.DefaultValue;
                            }
                        }
                    }

                    string keySuffix = (inst.Targets != null && inst.Targets.Count > 0) ? $".{i}" : "";

                    if (tmpl.Outputs != null)
                    {
                        foreach (var output in tmpl.Outputs)
                        {
                            string itemKey = PluginConstants.DASH_PREFIX + inst.Id + keySuffix + "." + output.Key;
                            validKeys.Add(itemKey);

                            // 注入 Loading 状态 (仅当当前无值时)
                            if (string.IsNullOrEmpty(InfoService.Instance.GetValue(itemKey)))
                            {
                                InfoService.Instance.InjectValue(itemKey, PluginConstants.STATUS_LOADING);
                            }
                            // ★★★ [新增] 顺便注入默认单位，这样 MetricUtils 就能查到了 ★★★
                            InfoService.Instance.InjectValue(itemKey + ".Unit", output.Unit);

                            var item = settings.MonitorItems.FirstOrDefault(x => x.Key == itemKey);

                            string labelPattern = PluginI18n.Pick(output.Label, output.LabelEn);
                            if (string.IsNullOrEmpty(labelPattern)) labelPattern = (PluginI18n.Pick(tmpl.Meta.Name, tmpl.Meta.NameEn)) + " " + output.Key;

                            string finalName = PluginProcessor.ResolveTemplate(labelPattern, mergedInputs);
                            string finalShort = PluginProcessor.ResolveTemplate(PluginI18n.Pick(output.ShortLabel, output.ShortLabelEn), mergedInputs);

                            if (item == null)
                            {
                                // Create New Item
                                string safeLabel = !string.IsNullOrEmpty(finalName) ? finalName : (PluginI18n.Pick(tmpl.Meta.Name, tmpl.Meta.NameEn) + " " + output.Key);
                                string safeShort = !string.IsNullOrEmpty(finalShort) ? finalShort : output.Key;

                                // New output keys append without renumbering another
                                // instance's existing monitor items during hot reload.
                                int nextSort = (settings.MonitorItems.Any() ? settings.MonitorItems.Max(x => x.SortIndex) : 0) + 1;
                                int nextTbSort = (settings.MonitorItems.Any() ? settings.MonitorItems.Max(x => x.TaskbarSortIndex) : 0) + 1;

                                item = new MonitorItemConfig
                                {
                                    Key = itemKey,
                                    UserLabel = "", // Auto mode
                                    DynamicLabel = safeLabel,
                                    TaskbarLabel = "", // Auto mode
                                    DynamicTaskbarLabel = safeShort,
                                    UnitPanel = output.Unit,
                                    UnitTaskbar = output.Unit, // [Fix] Sync Unit to Taskbar as well
                                    VisibleInPanel = true,
                                    SortIndex = nextSort,
                                    TaskbarSortIndex = nextTbSort,
                                };
                                settings.MonitorItems.Add(item);
                                changed = true;
                            }
                            else
                            {
                                // Update Existing Item
                                string safeLabel = !string.IsNullOrEmpty(finalName) ? finalName : (PluginI18n.Pick(tmpl.Meta.Name, tmpl.Meta.NameEn) + " " + output.Key);
                                string safeShort = !string.IsNullOrEmpty(finalShort) ? finalShort : output.Key;

                                if (item.DynamicLabel != safeLabel) { item.DynamicLabel = safeLabel; changed = true; }
                                if (item.DynamicTaskbarLabel != safeShort) { item.DynamicTaskbarLabel = safeShort; changed = true; }
                                // ★★★ [建议] 注释掉下面这两行，防止重启后强制覆盖用户的设置 ★★★
                                // if (item.UnitPanel != output.Unit) { item.UnitPanel = output.Unit; changed = true; }
                                // if (item.UnitTaskbar != output.Unit) { item.UnitTaskbar = output.Unit; changed = true; } // [Fix] Sync Unit to Taskbar as well
                            }
                        }
                    }
                }

                // Cleanup Orphaned Items for this Instance
                var toRemove = settings.MonitorItems
                    .Where(x => ownedKeys.Contains(x.Key) && !validKeys.Contains(x.Key))
                    .ToList();

                foreach (var item in toRemove)
                {
                    settings.MonitorItems.Remove(item);
                    changed = true;
                }
                PluginKeyOwnership.RememberMonitorKeys(settings, inst.Id, validKeys);
            } // End Lock

            if (changed && saveIfChanged) settings.Save();
            return changed;
        }

        /// <summary>
        /// 清理指定插件实例的所有监控项
        /// </summary>
        public void RemoveMonitorItems(string instanceId, bool saveIfChanged = true)
        {
            var settings = Settings.Load();
            bool changed;
            lock (settings.MonitorItems)
            {
                var keys = PluginKeyOwnership.MonitorKeys(settings, instanceId);
                var itemsToRemove = settings.MonitorItems.Where(item => keys.Contains(item.Key)).ToList();
                foreach (var item in itemsToRemove) settings.MonitorItems.Remove(item);
                PluginKeyOwnership.RememberMonitorKeys(settings, instanceId, new HashSet<string>(StringComparer.Ordinal));
                changed = itemsToRemove.Count > 0;
            }
            if (changed && saveIfChanged) settings.Save();
        }

        /// <summary>
        /// 尝试根据 Key 反向推断 Label (UI 辅助方法)
        /// </summary>
        public string TryGetSmartLabel(string itemKey, List<PluginTemplate> templates, string targetField = "label")
        {
            try
            {
                if (string.IsNullOrEmpty(itemKey)) return "";
                var settings = Settings.Load();
                string owner;
                lock (settings.MonitorItems) owner = PluginKeyOwnership.MonitorOwner(settings, itemKey);
                var instance = settings.PluginInstances.FirstOrDefault(i => i.Id == owner);
                var template = templates.FirstOrDefault(t => t.Id == instance?.TemplateId);
                if (instance == null || template == null) return "";

                foreach (var binding in PluginKeyOwnership.Outputs(instance, template))
                {
                    if (PluginConstants.DASH_PREFIX + binding.RawKey != itemKey) continue;
                    var inputs = new Dictionary<string, string>(instance.InputValues);
                    if (binding.TargetIndex >= 0)
                        foreach (var pair in instance.Targets[binding.TargetIndex]) inputs[pair.Key] = pair.Value;
                    foreach (var input in template.Inputs ?? new List<PluginInput>())
                        if (!inputs.ContainsKey(input.Key)) inputs[input.Key] = input.DefaultValue;

                    var output = binding.Output;
                    string pattern = targetField == "short_label"
                        ? PluginI18n.Pick(output.ShortLabel, output.ShortLabelEn)
                        : PluginI18n.Pick(output.Label, output.LabelEn);
                    if (string.IsNullOrEmpty(pattern)) pattern = PluginI18n.Pick(template.Meta.Name, template.Meta.NameEn);
                    string resolved = PluginProcessor.ResolveTemplate(pattern, inputs);
                    return string.IsNullOrEmpty(resolved) || resolved.Contains("{{")
                        ? PluginI18n.Pick(template.Meta.Name, template.Meta.NameEn) + " " + output.Key : resolved;
                }
                return PluginI18n.Pick(template.Meta.Name, template.Meta.NameEn);
            }
            catch { return ""; }
        }

    }
}
