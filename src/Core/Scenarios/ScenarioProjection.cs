using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace LiteMonitor.src.Core.Scenarios
{
    /// <summary>单个监控项的受限投影；只保存稳定 Key 与允许字段。</summary>
    public sealed class ScenarioMonitorItem
    {
        public string Key { get; set; } = "";
        public string UserLabel { get; set; } = "";
        public string TaskbarLabel { get; set; } = "";
        public string? UnitPanel { get; set; }
        public string? UnitTaskbar { get; set; }
        public bool VisibleInPanel { get; set; } = true;
        public bool VisibleInTaskbar { get; set; } = false;
        public int SortIndex { get; set; }
        public int TaskbarSortIndex { get; set; }
    }

    /// <summary>
    /// Settings 的受限投影。每个属性都在 ScenarioFieldCatalog 中有显式规则；
    /// 不在此类中的设置永远不能被场景捕获或应用。
    /// </summary>
    public sealed class ScenarioSettingsProjection
    {
        public string Skin { get; set; } = "DarkFlat_Classic";
        public bool TopMost { get; set; } = true;
        public bool HorizontalMode { get; set; }
        public int PanelWidth { get; set; } = 240;
        public double UIScale { get; set; } = 1.0;
        public double Opacity { get; set; } = 0.85;

        public Point Position { get; set; } = new Point(-1, -1);
        public string ScreenDevice { get; set; } = "";
        public string TaskbarMonitorDevice { get; set; } = "";

        public int RefreshMs { get; set; } = 1000;
        public double AnimationSpeed { get; set; } = 0.35;

        public bool ClickThrough { get; set; }
        public bool AutoHide { get; set; } = true;
        public bool ClampToScreen { get; set; } = true;
        public bool HideMainForm { get; set; }

        public bool ShowTaskbar { get; set; } = true;
        public bool HorizontalFollowsTaskbar { get; set; }
        public bool TaskbarAlignLeft { get; set; } = true;
        public int TaskbarPresetStyle { get; set; } = -1;
        public bool TaskbarCustomLayout { get; set; }
        public string TaskbarFontFamily { get; set; } = Settings.DEFAULT_TB_FONT;
        public float TaskbarFontSize { get; set; } = Settings.DEFAULT_TB_SIZE_BOLD;
        public bool TaskbarFontBold { get; set; } = true;
        public int TaskbarItemSpacing { get; set; } = Settings.DEFAULT_TB_GAP;
        public int TaskbarInnerSpacing { get; set; } = Settings.DEFAULT_TB_INNER_BOLD;
        public int TaskbarVerticalPadding { get; set; } = Settings.DEFAULT_TB_VOFF;
        public int HorizontalItemSpacing { get; set; } = 12;
        public int HorizontalInnerSpacing { get; set; } = 8;
        public bool HorizontalSingleLine { get; set; }
        public bool TaskbarClickThrough { get; set; }
        public bool TaskbarSingleLine { get; set; }
        public bool TaskbarHoverShowAll { get; set; } = true;
        public int TaskbarManualOffset { get; set; }
        public bool TaskbarCustomStyle { get; set; }
        public string TaskbarColorLabel { get; set; } = "#141414";
        public string TaskbarColorSafe { get; set; } = "#008040";
        public string TaskbarColorWarn { get; set; } = "#B57500";
        public string TaskbarColorCrit { get; set; } = "#C03030";
        public string TaskbarColorBg { get; set; } = "#D2D2D2";
        public int TaskbarDoubleClickAction { get; set; }

        public List<ScenarioMonitorItem> MonitorItems { get; set; } = new();
    }

    public static class ScenarioProjection
    {
        /// <summary>从当前已提交设置捕获允许字段；排除字段不会被读取。</summary>
        public static ScenarioSettingsProjection Capture(Settings source)
        {
            ArgumentNullException.ThrowIfNull(source);

            var projection = new ScenarioSettingsProjection
            {
                Skin = source.Skin,
                TopMost = source.TopMost,
                HorizontalMode = source.HorizontalMode,
                PanelWidth = source.PanelWidth,
                UIScale = source.UIScale,
                Opacity = source.Opacity,
                Position = source.Position,
                ScreenDevice = source.ScreenDevice,
                TaskbarMonitorDevice = source.TaskbarMonitorDevice,
                RefreshMs = source.RefreshMs,
                AnimationSpeed = source.AnimationSpeed,
                ClickThrough = source.ClickThrough,
                AutoHide = source.AutoHide,
                ClampToScreen = source.ClampToScreen,
                HideMainForm = source.HideMainForm,
                ShowTaskbar = source.ShowTaskbar,
                HorizontalFollowsTaskbar = source.HorizontalFollowsTaskbar,
                TaskbarAlignLeft = source.TaskbarAlignLeft,
                TaskbarPresetStyle = source.TaskbarPresetStyle,
                TaskbarCustomLayout = source.TaskbarCustomLayout,
                TaskbarFontFamily = source.TaskbarFontFamily,
                TaskbarFontSize = source.TaskbarFontSize,
                TaskbarFontBold = source.TaskbarFontBold,
                TaskbarItemSpacing = source.TaskbarItemSpacing,
                TaskbarInnerSpacing = source.TaskbarInnerSpacing,
                TaskbarVerticalPadding = source.TaskbarVerticalPadding,
                HorizontalItemSpacing = source.HorizontalItemSpacing,
                HorizontalInnerSpacing = source.HorizontalInnerSpacing,
                HorizontalSingleLine = source.HorizontalSingleLine,
                TaskbarClickThrough = source.TaskbarClickThrough,
                TaskbarSingleLine = source.TaskbarSingleLine,
                TaskbarHoverShowAll = source.TaskbarHoverShowAll,
                TaskbarManualOffset = source.TaskbarManualOffset,
                TaskbarCustomStyle = source.TaskbarCustomStyle,
                TaskbarColorLabel = source.TaskbarColorLabel,
                TaskbarColorSafe = source.TaskbarColorSafe,
                TaskbarColorWarn = source.TaskbarColorWarn,
                TaskbarColorCrit = source.TaskbarColorCrit,
                TaskbarColorBg = source.TaskbarColorBg,
                TaskbarDoubleClickAction = source.TaskbarDoubleClickAction
            };

            foreach (var item in (source.MonitorItems ?? new List<MonitorItemConfig>())
                         .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Key))
                         .OrderBy(item => item.SortIndex)
                         .ThenBy(item => item.Key, StringComparer.Ordinal))
            {
                projection.MonitorItems.Add(new ScenarioMonitorItem
                {
                    Key = item.Key,
                    UserLabel = item.UserLabel ?? "",
                    TaskbarLabel = item.TaskbarLabel ?? "",
                    UnitPanel = item.UnitPanel,
                    UnitTaskbar = item.UnitTaskbar,
                    VisibleInPanel = item.VisibleInPanel,
                    VisibleInTaskbar = item.VisibleInTaskbar,
                    SortIndex = item.SortIndex,
                    TaskbarSortIndex = item.TaskbarSortIndex
                });
            }

            Validate(projection);
            return projection;
        }

        /// <summary>
        /// 只应用允许字段。不会添加/删除监控项，不会触碰 Web、插件、凭据、启动项、
        /// 更新策略、探测策略、GPU 授权等排除字段。
        /// </summary>
        public static void ApplyTo(Settings target, ScenarioSettingsProjection projection)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(projection);
            Validate(projection);

            target.Skin = projection.Skin;
            target.TopMost = projection.TopMost;
            target.HorizontalMode = projection.HorizontalMode;
            target.PanelWidth = projection.PanelWidth;
            target.UIScale = projection.UIScale;
            target.Opacity = projection.Opacity;
            target.Position = projection.Position;
            target.ScreenDevice = projection.ScreenDevice;
            target.TaskbarMonitorDevice = projection.TaskbarMonitorDevice;
            target.RefreshMs = projection.RefreshMs;
            target.AnimationSpeed = projection.AnimationSpeed;
            target.ClickThrough = projection.ClickThrough;
            target.AutoHide = projection.AutoHide;
            target.ClampToScreen = projection.ClampToScreen;
            target.HideMainForm = projection.HideMainForm;
            target.ShowTaskbar = projection.ShowTaskbar;
            target.HorizontalFollowsTaskbar = projection.HorizontalFollowsTaskbar;
            target.TaskbarAlignLeft = projection.TaskbarAlignLeft;
            target.TaskbarPresetStyle = projection.TaskbarPresetStyle;
            target.TaskbarCustomLayout = projection.TaskbarCustomLayout;
            target.TaskbarFontFamily = projection.TaskbarFontFamily;
            target.TaskbarFontSize = projection.TaskbarFontSize;
            target.TaskbarFontBold = projection.TaskbarFontBold;
            target.TaskbarItemSpacing = projection.TaskbarItemSpacing;
            target.TaskbarInnerSpacing = projection.TaskbarInnerSpacing;
            target.TaskbarVerticalPadding = projection.TaskbarVerticalPadding;
            target.HorizontalItemSpacing = projection.HorizontalItemSpacing;
            target.HorizontalInnerSpacing = projection.HorizontalInnerSpacing;
            target.HorizontalSingleLine = projection.HorizontalSingleLine;
            target.TaskbarClickThrough = projection.TaskbarClickThrough;
            target.TaskbarSingleLine = projection.TaskbarSingleLine;
            target.TaskbarHoverShowAll = projection.TaskbarHoverShowAll;
            target.TaskbarManualOffset = projection.TaskbarManualOffset;
            target.TaskbarCustomStyle = projection.TaskbarCustomStyle;
            target.TaskbarColorLabel = projection.TaskbarColorLabel;
            target.TaskbarColorSafe = projection.TaskbarColorSafe;
            target.TaskbarColorWarn = projection.TaskbarColorWarn;
            target.TaskbarColorCrit = projection.TaskbarColorCrit;
            target.TaskbarColorBg = projection.TaskbarColorBg;
            target.TaskbarDoubleClickAction = projection.TaskbarDoubleClickAction;

            var byKey = (target.MonitorItems ?? new List<MonitorItemConfig>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Key))
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (var saved in projection.MonitorItems ?? new List<ScenarioMonitorItem>())
            {
                if (!byKey.TryGetValue(saved.Key, out var live)) continue;
                live.UserLabel = saved.UserLabel ?? "";
                live.TaskbarLabel = saved.TaskbarLabel ?? "";
                live.UnitPanel = saved.UnitPanel;
                live.UnitTaskbar = saved.UnitTaskbar;
                live.VisibleInPanel = saved.VisibleInPanel;
                live.VisibleInTaskbar = saved.VisibleInTaskbar;
                live.SortIndex = saved.SortIndex;
                live.TaskbarSortIndex = saved.TaskbarSortIndex;
            }
        }

        public static void Validate(ScenarioSettingsProjection projection)
        {
            ArgumentNullException.ThrowIfNull(projection);

            ValidateField("Skin", projection.Skin);
            ValidateField("TopMost", projection.TopMost);
            ValidateField("HorizontalMode", projection.HorizontalMode);
            ValidateField("PanelWidth", projection.PanelWidth);
            ValidateField("UIScale", projection.UIScale);
            ValidateField("Opacity", projection.Opacity);
            ValidateField("Position", projection.Position);
            ValidateField("ScreenDevice", projection.ScreenDevice ?? "");
            ValidateField("TaskbarMonitorDevice", projection.TaskbarMonitorDevice ?? "");
            ValidateField("RefreshMs", projection.RefreshMs);
            ValidateField("AnimationSpeed", projection.AnimationSpeed);
            ValidateField("ClickThrough", projection.ClickThrough);
            ValidateField("AutoHide", projection.AutoHide);
            ValidateField("ClampToScreen", projection.ClampToScreen);
            ValidateField("HideMainForm", projection.HideMainForm);
            ValidateField("ShowTaskbar", projection.ShowTaskbar);
            ValidateField("HorizontalFollowsTaskbar", projection.HorizontalFollowsTaskbar);
            ValidateField("TaskbarAlignLeft", projection.TaskbarAlignLeft);
            ValidateField("TaskbarPresetStyle", projection.TaskbarPresetStyle);
            ValidateField("TaskbarCustomLayout", projection.TaskbarCustomLayout);
            ValidateField("TaskbarFontFamily", projection.TaskbarFontFamily ?? "");
            ValidateField("TaskbarFontSize", (double)projection.TaskbarFontSize);
            ValidateField("TaskbarFontBold", projection.TaskbarFontBold);
            ValidateField("TaskbarItemSpacing", projection.TaskbarItemSpacing);
            ValidateField("TaskbarInnerSpacing", projection.TaskbarInnerSpacing);
            ValidateField("TaskbarVerticalPadding", projection.TaskbarVerticalPadding);
            ValidateField("HorizontalItemSpacing", projection.HorizontalItemSpacing);
            ValidateField("HorizontalInnerSpacing", projection.HorizontalInnerSpacing);
            ValidateField("HorizontalSingleLine", projection.HorizontalSingleLine);
            ValidateField("TaskbarClickThrough", projection.TaskbarClickThrough);
            ValidateField("TaskbarSingleLine", projection.TaskbarSingleLine);
            ValidateField("TaskbarHoverShowAll", projection.TaskbarHoverShowAll);
            ValidateField("TaskbarManualOffset", projection.TaskbarManualOffset);
            ValidateField("TaskbarCustomStyle", projection.TaskbarCustomStyle);
            ValidateField("TaskbarColorLabel", projection.TaskbarColorLabel ?? "");
            ValidateField("TaskbarColorSafe", projection.TaskbarColorSafe ?? "");
            ValidateField("TaskbarColorWarn", projection.TaskbarColorWarn ?? "");
            ValidateField("TaskbarColorCrit", projection.TaskbarColorCrit ?? "");
            ValidateField("TaskbarColorBg", projection.TaskbarColorBg ?? "");
            ValidateField("TaskbarDoubleClickAction", projection.TaskbarDoubleClickAction);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in projection.MonitorItems ?? new List<ScenarioMonitorItem>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Key))
                    throw new ArgumentException("监控项投影缺少稳定 Key。");
                if (!seen.Add(item.Key))
                    throw new ArgumentException($"监控项 Key 重复：{item.Key}");
                if (item.Key.Length > 128)
                    throw new ArgumentOutOfRangeException(nameof(item.Key), "监控项 Key 过长。");
            }
        }

        private static void ValidateField(string name, object? value)
        {
            var rule = ScenarioFieldCatalog.Find(name)
                       ?? throw new InvalidOperationException($"字段 {name} 不在场景允许列表中。");
            ScenarioFieldCatalog.ValidateValue(rule, value);
        }
    }
}
