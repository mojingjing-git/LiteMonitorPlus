using System;
using System.Text;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 分组标题的统一解析入口。
    ///
    /// 优先级: 用户自定义别名 (Settings.GroupAliases) > 按卡分组的显卡简称 > 语言包 > 分组键本身。
    ///
    /// 为什么要统一: 面板 / 设置页 / 右键菜单 / 网页端 4 个界面各自算了一遍标题,
    /// 逻辑一散就会出现「面板显示简称、网页显示 GPU.0」这种前后端不一致。
    /// </summary>
    public static class GroupTitleResolver
    {
        /// <summary>
        /// 取分组的「默认标题」 (不含用户自定义别名)。
        /// </summary>
        public static string GetDefaultTitle(string groupKey)
        {
            if (string.IsNullOrEmpty(groupKey)) return "";

            if (GpuGroupPolicy.TryGetCardIndex(groupKey, out int cardIndex))
                return GpuGroupPolicy.GetDefaultTitle(cardIndex);

            // ★ 用 GetOriginal 而不是 T:
            //   T() 会先查用户覆盖 —— 而 GroupAliases 会被注入成 "Groups.{key}" 的
            //   LanguageManager override (SettingsHelper.SyncToLanguage / MonitorPage),
            //   用 T 会让「默认标题」返回用户别名, 名不副实, 还会让
            //   「输入值 == 默认标题」这个判定失效。
            //   别名优先级由 Resolve() 单独处理, 这里只负责语言包原值。
            string t = LanguageManager.GetOriginal(UIUtils.Intern("Groups." + groupKey));
            return string.IsNullOrEmpty(t) ? groupKey : UIUtils.Intern(t);
        }

        /// <summary>
        /// 取分组的最终显示标题 (用户别名优先)。
        /// </summary>
        public static string Resolve(Settings cfg, string groupKey)
        {
            if (string.IsNullOrEmpty(groupKey)) return "";

            if (cfg?.GroupAliases != null
                && cfg.GroupAliases.TryGetValue(groupKey, out string? alias)
                && !string.IsNullOrEmpty(alias))
            {
                return alias;
            }

            return GetDefaultTitle(groupKey);
        }

        /// <summary>
        /// 把分组键转成网页端可安全用作 DOM id / CSS 选择器的标识符。
        ///
        /// 网页前端会把 gid 拼进 querySelector('#rw-' + gid), 而 "GPU.0" 里的点号
        /// 会被当成 class 选择器 —— class 名不能以数字开头, querySelector 会抛
        /// DOMException, 整个网页端渲染中断 (白屏)。
        /// (已用 headless Chrome 实测: '#rw-GPU.0' 抛 SyntaxError,
        ///  整个 render() 中断, 连 HOST/DISK/NET/DATA 组都不渲染。)
        /// 这里统一把非 [A-Za-z0-9_-] 的字符替换成下划线: "GPU.0" -> "GPU_0"。
        /// 既有分组 ("CPU" / "HOST" / "DISK" ...) 全部保持原样, 零影响。
        /// </summary>
        public static string ToSafeGroupId(string groupKey)
        {
            if (string.IsNullOrEmpty(groupKey)) return "OTHER";

            var sb = new StringBuilder(groupKey.Length);
            foreach (char c in groupKey.ToUpperInvariant())
            {
                sb.Append((char.IsLetterOrDigit(c) || c == '_' || c == '-') ? c : '_');
            }
            return sb.ToString();
        }
    }
}
