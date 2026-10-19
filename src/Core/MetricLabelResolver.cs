using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Core
{
    public static class MetricLabelResolver
    {
        /// <summary>
        /// 统一解析监控项的主标签 (Name)
        /// 优先级：UserLabel > InfoService(Runtime) > DynamicLabel(Config)
        /// </summary>
        public static string ResolveLabel(MonitorItemConfig config)
        {
            if (config == null) return "";

            // 1. 用户自定义 (最高优先级)
            if (!string.IsNullOrEmpty(config.UserLabel)) 
                return config.UserLabel;

            // 2. [多显卡] 显卡项的标签
            //    ★ [按卡拆组] 组标题已经标明「这是哪张卡」, 项标签再带一遍显卡简称
            //      就是重复 (面板会变成 "3080 Ti" / "3080 Ti 温度"), 所以拆组模式下
            //      只取基础标签 ("温度")。
            //    ★ 语言包没有该条目时 (例如自建的 "GPU.0.Custom"), T() 会回退成原始 Key
            //      ("GPU.Custom"), 这时**必须**继续往下走动态标签分支 ——
            //      否则插件 / InfoService 注入的标签会被原始 Key 顶掉。
            if (GpuKey.TryParse(config.Key, out int gpuIndex, out string metric))
            {
                string baseKey = GpuKey.BaseKey(metric);                   // "GPU.Load"
                string baseLabel = LanguageManager.T(UIUtils.Intern("Items." + baseKey));
                bool translated = !string.Equals(baseLabel, baseKey, StringComparison.Ordinal);

                if (GpuGroupPolicy.SplitPerCard)
                {
                    if (translated) return StripGpuPrefix(baseLabel);
                    // 未翻译 -> 继续往下走动态标签分支
                }
                else
                {
                    string shortName = GpuRegistry.GetShortName(gpuIndex);
                    if (!string.IsNullOrEmpty(shortName))
                    {
                        return $"{shortName} {StripGpuPrefix(baseLabel)}".Trim();
                    }
                    // 简称还没拿到 -> 继续往下走动态标签分支 (与旧行为一致)
                }
            }

            // 3. 运行时动态注入 (高优先级)
            // 插件运行过程中会实时向 InfoService 注入 PROP.Label.Key
            // [Optimization] Use cached key if available
            string dynKey = !string.IsNullOrEmpty(config.CachedPropLabelKey) 
                ? config.CachedPropLabelKey 
                : "PROP.Label." + config.Key;

            string dynVal = InfoService.Instance.GetValue(dynKey);
            
            // 业务约束：如果动态值为 ERROR 或空，则尝试降级 (避免闪烁或显示错误)
            // 但 InfoService 通常只存最新有效值(除非插件显式写入Error)
            if (!string.IsNullOrEmpty(dynVal)) 
                return dynVal;

            // 3. 配置中的动态标签 (中优先级)
            // 这是由 SyncService 定期同步并持久化保存的快照
            if (!string.IsNullOrEmpty(config.DynamicLabel)) 
                return config.DynamicLabel;

            // 4. 默认/失败
            return ""; 
        }

        /// <summary>
        /// 统一解析监控项的简略标签 (Taskbar)
        /// 优先级：TaskbarLabel > InfoService(Runtime) > DynamicTaskbarLabel(Config)
        /// </summary>
        public static string ResolveShortLabel(MonitorItemConfig config)
        {
            if (config == null) return "";

            // 1. 用户自定义
            if (!string.IsNullOrEmpty(config.TaskbarLabel)) 
                return config.TaskbarLabel;

            // 2. [多显卡] 任务栏用显卡简称区分 (数值单位已能区分指标: % / °C / W / GHz)
            if (GpuKey.TryParse(config.Key, out int gpuIndex, out _))
            {
                string shortName = GpuRegistry.GetShortName(gpuIndex);
                if (!string.IsNullOrEmpty(shortName)) return shortName;
            }

            // 3. 运行时动态注入
            string dynKey = !string.IsNullOrEmpty(config.CachedPropShortLabelKey)
                ? config.CachedPropShortLabelKey
                : "PROP.ShortLabel." + config.Key;
            
            string dynVal = InfoService.Instance.GetValue(dynKey);
            if (!string.IsNullOrEmpty(dynVal)) 
                return dynVal;

            // 4. 配置中的动态标签
            if (!string.IsNullOrEmpty(config.DynamicTaskbarLabel)) 
                return config.DynamicTaskbarLabel;

            return "";
        }

        /// <summary>
        /// 去掉基础标签里的 "GPU" 前缀, 避免拼成 "3080 Ti GPU温度"。
        /// "GPU温度" -> "温度" ; "GPU Load" -> "Load" ; "显存占用" -> "显存占用"
        /// </summary>
        private static string StripGpuPrefix(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "";
            string t = label.Trim();
            if (t.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
            {
                t = t.Substring(3).Trim();
            }
            return t.Length > 0 ? t : label.Trim();
        }
    }
}