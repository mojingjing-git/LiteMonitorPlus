using System;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件文案的语言选择器。
    /// 插件 JSON 里同时提供中文字段与 _en 字段：界面为中文时用中文，
    /// 其余语言优先用英文；英文缺失时回退中文（绝不显示空白）。
    /// </summary>
    public static class PluginI18n
    {
        /// <summary>当前是否应优先使用英文文案</summary>
        public static bool PreferEnglish => PreferEnglishFor(LanguageManager.CurrentLang);

        /// <summary>
        /// 按指定语言代码判断是否优先使用英文文案。
        /// 抽成纯函数是为了让单元测试不必改动全局语言状态
        /// （LanguageManager.CurrentLang 是进程级单例，并行测试下会互相干扰）。
        /// </summary>
        public static bool PreferEnglishFor(string? langCode)
            => !(langCode ?? "zh").StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 按当前界面语言在中文/英文之间选词；英文缺失时回退中文。
        /// ★ 永不返回 null：插件 JSON 里可能写了 "label": null，
        ///   直接返回 null 会让下游的模板替换或控件赋值炸掉。
        /// </summary>
        public static string Pick(string? zh, string? en)
            => PickFor(LanguageManager.CurrentLang, zh, en);

        /// <summary>
        /// 按指定语言代码在中文/英文之间选词；英文缺失时回退中文。永不返回 null。
        /// </summary>
        public static string PickFor(string? langCode, string? zh, string? en)
            => PreferEnglishFor(langCode) && !string.IsNullOrEmpty(en) ? en! : (zh ?? "");
    }
}
