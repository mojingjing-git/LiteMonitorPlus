using System.Collections.Generic;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件系统常量定义
    /// </summary>
    public static class PluginConstants
    {
        /// <summary>
        /// 插件监控项的前缀 Key
        /// </summary>
        public const string DASH_PREFIX = "DASH.";

        /// <summary>
        /// 默认刷新间隔 (秒)
        /// </summary>
        public const int DEFAULT_INTERVAL = 1;

        /// <summary>
        /// 错误状态显示文本
        /// </summary>
        public const string STATUS_ERROR = "[ERROR]";

        /// <summary>
        /// 加载中状态显示文本
        /// </summary>
        public const string STATUS_LOADING = "...";

        /// <summary>
        /// 未知状态显示文本 (解析失败/路径错误)
        /// </summary>
        public const string STATUS_UNKNOWN = "[UNKNOWN]";

        /// <summary>
        /// 空值状态显示文本 (值为空)
        /// </summary>
        public const string STATUS_EMPTY = "[EMPTY]";

        /// <summary>
        /// 插件配置文件扩展名
        /// </summary>
        public const string CONFIG_EXT = "*.json";

        /// <summary>
        /// 失败重试间隔 (毫秒)
        /// </summary>
        public const int RETRY_INTERVAL_MS = 5000;

        /// <summary>
        /// 内置插件 id 保护名单：市场插件的 id 撞上其中任意一项都会被拒绝安装。
        ///
        /// 维护提示：新增内置插件时，必须同步更新
        ///   - 本文件（客户端）
        ///   - 插件仓库 scripts/gen_index.py 的 BUILTIN_IDS
        ///
        /// 重要提示：这份名单只是【第二道防线】。真正兜底的是 PluginMarket.IsTargetAvailable
        /// 的 L2 规则（目标文件已存在且不在市场记录内 → 拒绝安装），它不依赖任何名单，
        /// 因此即使这里漏改了某个新内置插件，也不会被市场插件覆盖。
        /// </summary>
        public static readonly HashSet<string> BuiltinPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Bilibili", "Crypto", "Exchange", "GitHub", "Hitokoto", "Holiday",
            "ProxyLatency", "PublicIP", "Stock", "UniversalAPI", "Weather", "WeiboHot"
        };
    }
}
