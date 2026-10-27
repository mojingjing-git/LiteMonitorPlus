using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteMonitor.src.Core.Scenarios
{
    /// <summary>场景定义可以触达的模块。新增字段默认不进入场景。</summary>
    public enum ScenarioModule
    {
        Appearance,
        MonitorItems,
        Taskbar,
        Placement,
        Refresh,
        Visibility
    }

    public enum ScenarioFieldType
    {
        String,
        Bool,
        Int,
        Double,
        Point,
        MonitorItems
    }

    /// <summary>一个允许字段的显式契约：类型、边界和归属模块。</summary>
    public sealed record ScenarioFieldRule(
        string Name,
        ScenarioFieldType Type,
        ScenarioModule Module,
        double? Min = null,
        double? Max = null,
        bool NullableString = false);

    /// <summary>
    /// 场景字段允许列表。它是正向白名单，不是“排除黑名单”：
    /// 任何未在这里声明的 Settings 字段都不会被捕获或应用。
    /// </summary>
    public static class ScenarioFieldCatalog
    {
        public const int CurrentSchemaVersion = 1;
        public const int MaxProfiles = 32;
        public const int MaxFileBytes = 2 * 1024 * 1024;

        private static readonly ScenarioFieldRule[] _rules =
        {
            // 外观
            new("Skin", ScenarioFieldType.String, ScenarioModule.Appearance),
            new("TopMost", ScenarioFieldType.Bool, ScenarioModule.Appearance),
            new("HorizontalMode", ScenarioFieldType.Bool, ScenarioModule.Appearance),
            new("PanelWidth", ScenarioFieldType.Int, ScenarioModule.Appearance, 120, 2000),
            new("UIScale", ScenarioFieldType.Double, ScenarioModule.Appearance, 0.5, 3.0),
            new("Opacity", ScenarioFieldType.Double, ScenarioModule.Appearance, 0.1, 1.0),

            // 位置/屏幕锚点
            new("Position", ScenarioFieldType.Point, ScenarioModule.Placement),
            new("ScreenDevice", ScenarioFieldType.String, ScenarioModule.Placement),
            new("TaskbarMonitorDevice", ScenarioFieldType.String, ScenarioModule.Placement),

            // 刷新与动画
            new("RefreshMs", ScenarioFieldType.Int, ScenarioModule.Refresh, 250, 60000),
            new("AnimationSpeed", ScenarioFieldType.Double, ScenarioModule.Refresh, 0, 1.0),

            // 可见性与可恢复入口（不包含 HideTrayIcon：必须保留托盘入口）
            new("ClickThrough", ScenarioFieldType.Bool, ScenarioModule.Visibility),
            new("AutoHide", ScenarioFieldType.Bool, ScenarioModule.Visibility),
            new("ClampToScreen", ScenarioFieldType.Bool, ScenarioModule.Visibility),
            new("HideMainForm", ScenarioFieldType.Bool, ScenarioModule.Visibility),

            // 任务栏
            new("ShowTaskbar", ScenarioFieldType.Bool, ScenarioModule.Taskbar),
            new("HorizontalFollowsTaskbar", ScenarioFieldType.Bool, ScenarioModule.Taskbar),
            new("TaskbarAlignLeft", ScenarioFieldType.Bool, ScenarioModule.Taskbar),
            new("TaskbarPresetStyle", ScenarioFieldType.Int, ScenarioModule.Taskbar, -1, 1),
            new("TaskbarCustomLayout", ScenarioFieldType.Bool, ScenarioModule.Taskbar),
            new("TaskbarFontFamily", ScenarioFieldType.String, ScenarioModule.Taskbar),
            new("TaskbarFontSize", ScenarioFieldType.Double, ScenarioModule.Taskbar, 6, 48),
            new("TaskbarFontBold", ScenarioFieldType.Bool, ScenarioModule.Taskbar),
            new("TaskbarItemSpacing", ScenarioFieldType.Int, ScenarioModule.Taskbar, 0, 64),
            new("TaskbarInnerSpacing", ScenarioFieldType.Int, ScenarioModule.Taskbar, 0, 64),
            new("TaskbarVerticalPadding", ScenarioFieldType.Int, ScenarioModule.Taskbar, -16, 64),
            new("HorizontalItemSpacing", ScenarioFieldType.Int, ScenarioModule.Taskbar, 0, 64),
            new("HorizontalInnerSpacing", ScenarioFieldType.Int, ScenarioModule.Taskbar, 0, 64),
            new("HorizontalSingleLine", ScenarioFieldType.Bool, ScenarioModule.Taskbar),
            new("TaskbarClickThrough", ScenarioFieldType.Bool, ScenarioModule.Taskbar),
            new("TaskbarSingleLine", ScenarioFieldType.Bool, ScenarioModule.Taskbar),
            new("TaskbarHoverShowAll", ScenarioFieldType.Bool, ScenarioModule.Taskbar),
            new("TaskbarManualOffset", ScenarioFieldType.Int, ScenarioModule.Taskbar, -4096, 4096),
            new("TaskbarCustomStyle", ScenarioFieldType.Bool, ScenarioModule.Taskbar),
            new("TaskbarColorLabel", ScenarioFieldType.String, ScenarioModule.Taskbar),
            new("TaskbarColorSafe", ScenarioFieldType.String, ScenarioModule.Taskbar),
            new("TaskbarColorWarn", ScenarioFieldType.String, ScenarioModule.Taskbar),
            new("TaskbarColorCrit", ScenarioFieldType.String, ScenarioModule.Taskbar),
            new("TaskbarColorBg", ScenarioFieldType.String, ScenarioModule.Taskbar),
            new("TaskbarDoubleClickAction", ScenarioFieldType.Int, ScenarioModule.Taskbar, 0, 2),

            // 监控项投影
            new("MonitorItems", ScenarioFieldType.MonitorItems, ScenarioModule.MonitorItems)
        };

        private static readonly Dictionary<string, ScenarioFieldRule> _byName =
            _rules.ToDictionary(rule => rule.Name, StringComparer.Ordinal);

        public static IReadOnlyList<ScenarioFieldRule> Rules => _rules;
        public static IReadOnlyCollection<string> AllowedNames => _byName.Keys;

        public static ScenarioFieldRule? Find(string name)
            => name != null && _byName.TryGetValue(name, out var rule) ? rule : null;

        public static bool IsAllowed(string name) => Find(name) != null;

        /// <summary>首版明确不进入场景的字段类别；测试用，不参与捕获。</summary>
        public static IReadOnlyCollection<string> ExcludedExamples { get; } = new[]
        {
            "WebServerEnabled", "WebServerPort", "WebServerPassword", "WebServerAllowedHosts",
            "PluginInstances", "AutoStart", "AutoCheckUpdate", "DiskProbeMode",
            "ControllerProbeMode", "UseWinPerCounters", "IgnoreSmbTraffic",
            "GpuThemeEffectsEnabled", "GpuLiveBackgroundEnabled", "GpuWdaCapturePolicyAccepted",
            "HideTrayIcon", "Language", "SettingThemeMode", "AlertRules", "AlertMuteAll"
        };

        internal static void ValidateValue(ScenarioFieldRule rule, object? value)
        {
            if (value == null)
                throw new ArgumentNullException(rule.Name, $"场景字段 {rule.Name} 不能为 null。");

            switch (rule.Type)
            {
                case ScenarioFieldType.String:
                    if (value is not string s)
                        throw new ArgumentException($"场景字段 {rule.Name} 必须是字符串。", rule.Name);
                    if (s.Length > 4096)
                        throw new ArgumentOutOfRangeException(rule.Name, "字符串长度超过 4096。");
                    break;
                case ScenarioFieldType.Bool:
                    if (value is not bool) throw new ArgumentException($"场景字段 {rule.Name} 必须是布尔值。", rule.Name);
                    break;
                case ScenarioFieldType.Int:
                    if (value is not int i) throw new ArgumentException($"场景字段 {rule.Name} 必须是整数。", rule.Name);
                    CheckRange(rule, i);
                    break;
                case ScenarioFieldType.Double:
                    if (value is not double d || double.IsNaN(d) || double.IsInfinity(d))
                        throw new ArgumentException($"场景字段 {rule.Name} 必须是有限数值。", rule.Name);
                    CheckRange(rule, d);
                    break;
                case ScenarioFieldType.Point:
                    if (value is not System.Drawing.Point) throw new ArgumentException($"场景字段 {rule.Name} 必须是坐标。", rule.Name);
                    break;
            }
        }

        private static void CheckRange(ScenarioFieldRule rule, double value)
        {
            if (rule.Min.HasValue && value < rule.Min.Value)
                throw new ArgumentOutOfRangeException(rule.Name, $"小于下限 {rule.Min.Value}。");
            if (rule.Max.HasValue && value > rule.Max.Value)
                throw new ArgumentOutOfRangeException(rule.Name, $"大于上限 {rule.Max.Value}。");
        }
    }
}
