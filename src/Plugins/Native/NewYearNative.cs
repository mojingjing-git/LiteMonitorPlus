using System;
using System.Globalization;
using System.Text.Json;

namespace LiteMonitor.src.Plugins.Native
{
    /// <summary>
    /// [F13] 元旦倒计时**本地兜底**处理器（<c>native://newyear</c>）。
    ///
    /// <para><b>用途</b>：节假日接口不可用 / 返回空数据时，按**本机日期**给出"距离元旦还有多少天"。
    /// 这是 issue #611 的原始诉求（"能不能出个距离元旦还有多少天接一下"）。</para>
    ///
    /// <para><b>不冒充法定节假日数据</b>：本处理器只算**元旦这一天**的日期差，
    /// <b>不推测调休安排</b>、也不产出"下一个法定节假日"的名字。返回的 <c>name</c> 恒为"元旦"，
    /// 界面据此可与主源给出的节假日名区分开；<c>source</c> 字段显式标注数据来源为本地推算。</para>
    ///
    /// <para><b>纯本地</b>：不触网 ⇒ 不占并发槽位、不产生响应体、不受允许目标白名单约束
    /// （但它仍走 <c>native://</c> 的统一入口与沙箱闸门，见 <c>PluginExecutor.ExecuteNativeAsync</c>）。</para>
    ///
    /// <para><b>时钟可注入</b>（<see cref="TimeProvider"/>）⇒ 跨年 / 闰年 / 时区都能被确定性测试覆盖；
    /// 生产默认用系统时钟的**本地日期**（与 <c>DateTime.Today</c> 同口径）。</para>
    /// </summary>
    public static class NewYearNative
    {
        /// <summary>按可注入时钟的**本地日期**计算元旦倒计时（生产入口）。</summary>
        /// <param name="timeProvider">时钟来源；为 null 时回落到 <see cref="TimeProvider.System"/>。</param>
        public static string GetCountdownJson(TimeProvider timeProvider)
        {
            var provider = timeProvider ?? TimeProvider.System;
            return GetCountdownJson(provider.GetLocalNow().Date);
        }

        /// <summary>按给定的本地日期计算元旦倒计时（确定性测试接缝）。</summary>
        /// <param name="today">本地"今天"（只取日期部分，时区 / 时刻被丢弃）。</param>
        public static string GetCountdownJson(DateTime today)
        {
            today = today.Date;

            // 目标：today 之后的**下一个**元旦（1 月 1 日）。
            var newYear = new DateTime(today.Year, 1, 1);
            if (newYear < today) newYear = newYear.AddYears(1);

            return JsonSerializer.Serialize(new
            {
                name = "元旦",
                date = newYear.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                rest = (newYear - today).Days,
                // 显式标注数据来源：主源不可用时这里给的是**本地日期推算**，不含调休安排。
                source = "local"
            });
        }
    }
}
