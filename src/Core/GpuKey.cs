using System;
using System.Globalization;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 多显卡监控的 Key 命名规则。
    ///
    /// 单显卡 (旧格式) : "GPU.Load"
    /// 多显卡 (新格式) : "GPU.0.Load"  —— 序号 = 被监控显卡在监控集合中的下标 (从 0 开始)
    ///
    /// 之所以这样设计, 是为了让既有逻辑零改动地继续工作:
    ///   - MetricUtils.GetType 走子串匹配, "GPU.1.Temp" 仍含 "TEMP" -> Temperature
    ///   - Settings.IsAnyEnabled("GPU") 走前缀匹配, 仍能命中
    ///   - MonitorItemConfig.UIGroup 取 Key.Split('.')[0], 仍归到 "GPU" 分组
    /// </summary>
    public static class GpuKey
    {
        /// <summary>显卡 Key 前缀</summary>
        public const string Prefix = "GPU.";

        /// <summary>设置项允许的显卡数量上限</summary>
        public const int MaxGpuCount = 8;

        /// <summary>
        /// 解析带序号的显卡 Key。
        /// "GPU.1.Temp" -> (index=1, metric="Temp") ; "GPU.Temp" -> false
        /// </summary>
        public static bool TryParse(string key, out int index, out string metric)
        {
            index = -1;
            metric = "";
            if (string.IsNullOrEmpty(key)) return false;
            if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

            string rest = key.Substring(Prefix.Length);
            int dot = rest.IndexOf('.');
            if (dot <= 0) return false;

            if (!int.TryParse(rest.Substring(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out index))
                return false;
            if (index < 0) return false;

            metric = rest.Substring(dot + 1);
            return metric.Length > 0;
        }

        /// <summary>构造带序号的 Key: (1, "Temp") -> "GPU.1.Temp"</summary>
        public static string Build(int index, string metric) => $"{Prefix}{index}.{metric}";

        /// <summary>基础 Key: "Temp" -> "GPU.Temp" (语言包 / 阈值回退用)</summary>
        public static string BaseKey(string metric) => Prefix + metric;

        /// <summary>
        /// 把带序号的 Key 归一化成基础 Key, 用于查语言包 / 阈值 / 峰值记录。
        /// "GPU.1.Temp" -> "GPU.Temp" ; "GPU.Temp" -> "GPU.Temp"
        /// </summary>
        public static string ToBaseKey(string key)
        {
            if (TryParse(key, out _, out string metric)) return BaseKey(metric);
            return key ?? "";
        }
    }
}