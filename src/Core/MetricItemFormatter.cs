using System;

namespace LiteMonitor.Core
{
    /// <summary>格式化上下文（纯编排器自有枚举；适配层映射到 MetricUtils.UnitContext）。</summary>
    public enum MetricFormatContext
    {
        Panel,
        Taskbar,
    }

    /// <summary>
    /// 数值/单位解析委托（P-B4-1）：由适配层（MetricItem）传入——通常是 MetricUtils 方法组。
    /// 纯编排器只经委托取字符串，自身不可见任何全局服务（InfoService/Settings/LanguageManager）。
    /// </summary>
    public sealed record MetricFormatResolvers(
        Func<string, float?, bool, string?, string> GetValueStr,
        Func<string, float?, MetricFormatContext, string> GetUnitStr,
        Func<string, string, string, string> GetDisplayUnit,
        Func<string, double, int> GetState,
        Func<string, double, double> GetProgressValue);

    /// <summary>格式化缓存全量快照（纯数据；UI 拥有并持有，P-B4 §4.3 的 ViewState 数据面）。</summary>
    public sealed record MetricFormatterState(
        float CachedDisplayValue = -99999f,
        (bool Ac, bool Charging) CachedPowerState = default,
        string CachedNormalText = "",
        string CachedHorizontalText = "",
        string CachedValueText = "",
        string CachedUnitText = "",
        bool HasCustomUnit = false,
        int CachedColorState = 0,
        double CachedPercent = 0.0);

    /// <summary>一次格式化的产出：新缓存状态 + 当前上下文文本。</summary>
    public sealed record MetricFormatterResult(MetricFormatterState State, string Text);

    /// <summary>
    /// B4-1 纯规则编排器（Plan B §4.3）：决定"何时刷新缓存、文本如何组装、颜色状态取谁"。
    /// 输入全部显式（含插件色解析结果与电源态）——不查询任何全局服务；阈值与分支口径
    /// 逐字对应既有 MetricItem.GetFormattedText 行为（181+22 条 fixture 为行为准绳）。
    /// </summary>
    public static class MetricItemFormatter
    {
        /// <summary>缓存失效阈值：显示精度通常为 0.00，阈值必须小于 0.01（原 0.05 的修复值）。</summary>
        public const float CacheRefreshDelta = 0.005f;

        public static MetricFormatterResult Format(
            string key,
            float displayValue,
            string? textValue,
            bool isHorizontal,
            string? userFormatPanel,
            string? userFormatTaskbar,
            int? pluginColorOverride,   // 插件项：解析后的状态值（无覆盖=0）；非插件项：null
            (bool Ac, bool Charging) currentPower,
            MetricFormatterState prev,
            MetricFormatResolvers r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            key ??= "";

            string userFormat = isHorizontal ? userFormatTaskbar : userFormatPanel;
            bool hasCustomUnit = !string.IsNullOrEmpty(userFormat);

            // ── TextValue 路径（插件/仪表盘项）：不做缓存数值刷新，只补单位 ─────────────
            if (textValue != null)
            {
                var ctx = isHorizontal ? MetricFormatContext.Taskbar : MetricFormatContext.Panel;
                string defUnit = r.GetUnitStr(key, null, ctx);
                string finalUnit = r.GetDisplayUnit(key, defUnit, userFormat);

                string text = !string.IsNullOrEmpty(finalUnit) && !textValue.EndsWith(finalUnit, StringComparison.Ordinal)
                    ? textValue + finalUnit
                    : textValue;

                // 插件项颜色由 override 决定（无覆盖=0）；非插件项颜色保持原值
                // （与既有行为一致：该路径不触碰非插件颜色）。其余缓存原样保留。
                int color = pluginColorOverride ?? prev.CachedColorState;
                var kept = prev with { HasCustomUnit = hasCustomUnit, CachedColorState = color };
                return new MetricFormatterResult(kept, text);
            }

            // ── 数值路径：电池电源态变化强制刷新；否则按阈值 ───────────────────────────
            bool isBat = key.StartsWith("BAT", StringComparison.OrdinalIgnoreCase);
            bool powerChanged = isBat && prev.CachedPowerState != currentPower;
            bool refresh = Math.Abs(displayValue - prev.CachedDisplayValue) > CacheRefreshDelta || powerChanged;

            if (!refresh)
            {
                // R4：插件色独立于文本缓存——数值未变时仍要应用本次 override（旧实现在缓存判定前更新）。
                var kept = prev with
                {
                    HasCustomUnit = hasCustomUnit,
                    CachedColorState = pluginColorOverride ?? prev.CachedColorState,
                };
                return new MetricFormatterResult(kept, isHorizontal ? prev.CachedHorizontalText : prev.CachedNormalText);
            }

            var next = new MetricFormatterState(
                CachedDisplayValue: displayValue,
                CachedPowerState: isBat ? currentPower : prev.CachedPowerState,
                CachedColorState: prev.CachedColorState,
                CachedPercent: prev.CachedPercent);

            // === Panel 缓存 ===
            // ★ 把该监控项自己的单位格式一起传进去：内存容量模式下若用户把单位写成 %，
            //   数值必须保持百分比，不能换算成 GB（IsPercentUnitOverride 语义在解析器内）。
            string valNormal = r.GetValueStr(key, displayValue, false, userFormatPanel);
            string unitNormal = r.GetUnitStr(key, displayValue, MetricFormatContext.Panel);
            string finalUnitPanel = r.GetDisplayUnit(key, unitNormal, userFormatPanel);
            string normalText = valNormal + finalUnitPanel;

            // === Taskbar 缓存 ===
            // 自动模式：数值压缩（compact=true）+ Taskbar 上下文单位；
            // 自定义模式：不压缩（compact=false），单位仍按 Taskbar 上下文计算。
            bool hasCustomTaskbar = !string.IsNullOrEmpty(userFormatTaskbar);
            bool compact = !hasCustomTaskbar;
            string valTaskbar = r.GetValueStr(key, displayValue, compact, userFormatTaskbar);
            string unitTaskbar = r.GetUnitStr(key, displayValue, MetricFormatContext.Taskbar);
            string finalUnitTaskbar = r.GetDisplayUnit(key, unitTaskbar, userFormatTaskbar);
            string horizontalText = valTaskbar + finalUnitTaskbar;

            string cachedValueText, cachedUnitText;
            bool resultHasCustomUnit;
            if (isHorizontal)
            {
                cachedValueText = valTaskbar;
                cachedUnitText = finalUnitTaskbar;
                resultHasCustomUnit = hasCustomTaskbar;
            }
            else
            {
                cachedValueText = valNormal;
                cachedUnitText = finalUnitPanel;
                resultHasCustomUnit = !string.IsNullOrEmpty(userFormatPanel);
            }

            // 非插件项：阈值决定颜色（插件项保持 override，即使 override=0）
            int colorState = pluginColorOverride ?? r.GetState(key, displayValue);
            double percent = r.GetProgressValue(key, displayValue);

            var final = new MetricFormatterState(
                CachedDisplayValue: displayValue,
                CachedPowerState: next.CachedPowerState,
                CachedNormalText: normalText,
                CachedHorizontalText: horizontalText,
                CachedValueText: cachedValueText,
                CachedUnitText: cachedUnitText,
                HasCustomUnit: resultHasCustomUnit,
                CachedColorState: colorState,
                CachedPercent: percent);

            return new MetricFormatterResult(final, isHorizontal ? horizontalText : normalText);
        }
    }
}
