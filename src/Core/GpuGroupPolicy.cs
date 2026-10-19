using System;
using System.Globalization;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 多显卡「按卡拆组」策略。
    ///
    /// 背景: 之前所有显卡的监控项都归到同一个 "GPU" 分组, 面板上会并排出现多组
    ///       重复的显卡型号。本策略把每张卡拆成独立分组 ("GPU.0" / "GPU.1"),
    ///       每组自带一个可自定义的小标题, 该卡的 6 个指标全部包裹在这个标题下。
    ///
    /// 分组键推导规则 (集中在这里, 避免各调用点各写一套):
    ///   "GPU.0.Load" -> "GPU.0"   (按卡拆组)
    ///   "GPU.Temp"   -> "GPU"     (旧版单卡格式, 没有序号, 不拆)
    ///   "CPU.Load"   -> "CPU"
    /// </summary>
    public static class GpuGroupPolicy
    {
        /// <summary>
        /// 是否启用「按卡拆组」。
        ///
        /// 打开时:
        ///   1. 面板 / 设置页 / 右键菜单 / 网页端都会按显卡拆成独立分组;
        ///   2. 组内项标签不再重复显卡简称 —— 分组标题已经承担了「这是哪张卡」的表达职责,
        ///      两边都带就是重复 (面板会变成 "3080 Ti" / "3080 Ti 温度")。
        ///
        /// 这两件事必须同生共死, 所以由同一个开关控制。
        ///
        /// ⚠️ 这里用 `static readonly` 而不是 `const`:
        ///   `const` 会被编译器常量折叠, 于是 `if (SplitPerCard) return ...;` 之后的
        ///   旧逻辑会被判定为「无法访问的代码」, 触发 CS0162 警告。
        ///   (实测: 用 const 时构建警告从 232 变成 234, 改 static readonly 后回到 232。)
        ///   `static readonly` 编译器折叠不了, 两条分支都保留, 零新增警告。
        /// </summary>
        public static readonly bool SplitPerCard = true;

        /// <summary>按卡分组的分组键前缀, 例如 "GPU.0"</summary>
        public const string PerCardGroupPrefix = GpuKey.Prefix;

        /// <summary>
        /// 取监控项所属的「按卡分组」键。
        /// "GPU.0.Load" -> "GPU.0" ; 其余 (含旧版 "GPU.Load") 返回 false。
        /// </summary>
        public static bool TryGetPerCardGroupKey(string itemKey, out string groupKey)
        {
            groupKey = "";
            if (!GpuKey.TryParse(itemKey, out int index, out _)) return false;
            groupKey = PerCardGroupPrefix + index.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// 判断一个分组键是不是「按卡分组」, 并取回显卡序号。
        /// "GPU.0" -> true, 0 ; "GPU" -> false ; "GPU.0.Load" -> false ; "CPU" -> false
        /// </summary>
        public static bool TryGetCardIndex(string groupKey, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(groupKey)) return false;
            if (!groupKey.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase)) return false;

            string rest = groupKey.Substring(GpuKey.Prefix.Length);
            // 按卡分组键里只允许再出现一个数字段: "GPU.<序号>"
            if (rest.Length == 0 || rest.IndexOf('.') >= 0) return false;
            if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out index)) return false;
            return index >= 0;
        }

        /// <summary>
        /// 按卡分组的默认标题 (方案 A = 显卡简称)。
        /// 简称取不到 (硬件还没初始化完) 时退回 "GPU.{序号}", 保证标题永不空。
        /// </summary>
        public static string GetDefaultTitle(int cardIndex)
        {
            string shortName = GpuRegistry.GetShortName(cardIndex);
            if (!string.IsNullOrEmpty(shortName)) return shortName;
            return PerCardGroupPrefix + cardIndex.ToString(CultureInfo.InvariantCulture);
        }
    }
}
