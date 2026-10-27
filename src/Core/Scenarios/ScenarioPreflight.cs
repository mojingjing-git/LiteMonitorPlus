using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;

namespace LiteMonitor.src.Core.Scenarios
{
    /// <summary>某个允许字段从当前设置到场景目标的差异。</summary>
    public sealed record ScenarioFieldChange(
        string Field,
        ScenarioModule Module,
        object? CurrentValue,
        object? TargetValue);

    /// <summary>预检问题。Blocking=false 表示可继续但要提示（例如监控项缺失）。</summary>
    public sealed record ScenarioPreflightIssue(
        string Field,
        string Code,
        string Message,
        bool Blocking);

    public sealed record ScenarioPreflightResult(
        bool CanApply,
        long TargetRevision,
        IReadOnlyList<ScenarioPreflightIssue> Issues,
        IReadOnlyList<ScenarioFieldChange> Changes)
    {
        public IReadOnlyList<ScenarioPreflightIssue> BlockingIssues
            => Issues.Where(issue => issue.Blocking).ToArray();
    }

    /// <summary>
    /// 应用前的只读预检：版本/结构/主题/监控项接缝，以及允许字段差异。
    /// 不持久化、不应用设置、不修改当前窗口。
    /// </summary>
    public static class ScenarioPreflight
    {
        public static ScenarioPreflightResult Inspect(
            ScenarioProfile? profile,
            Settings current,
            Func<string, bool>? themeExists = null)
        {
            ArgumentNullException.ThrowIfNull(current);

            var issues = new List<ScenarioPreflightIssue>();
            var changes = new List<ScenarioFieldChange>();

            if (profile == null)
            {
                issues.Add(new ScenarioPreflightIssue("", "profile-missing", "场景不存在。", true));
                return new ScenarioPreflightResult(false, 0, issues, changes);
            }

            if (!profile.IsSupportedVersion)
            {
                issues.Add(new ScenarioPreflightIssue(
                    "", "schema-unsupported",
                    $"场景版本 {profile.SchemaVersion} 不受支持。", true));
            }

            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                issues.Add(new ScenarioPreflightIssue("", "id-empty", "场景缺少稳定 Id。", true));
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                issues.Add(new ScenarioPreflightIssue("", "name-empty", "场景名称为空。", true));
            }

            try
            {
                ScenarioProjection.Validate(profile.Settings);
            }
            catch (Exception ex)
            {
                issues.Add(new ScenarioPreflightIssue(
                    "", "projection-invalid", ex.Message, true));
                return new ScenarioPreflightResult(false, profile.DefinitionRevision, issues, changes);
            }

            // R06：场景应用与设置保存共用同一条“至少保留一个可交互入口”约束。
            // 仅排除 HideTrayIcon 不够：当前托盘可能已隐藏，场景若再把两个窗口
            // 都设为隐藏/穿透，用户会失去全部常规操作入口。
            var candidate = current.DeepClone();
            ScenarioProjection.ApplyTo(candidate, profile.Settings);
            bool noInteractiveEntry =
                (candidate.HideMainForm || candidate.ClickThrough) &&
                (!candidate.ShowTaskbar || candidate.TaskbarClickThrough) &&
                candidate.HideTrayIcon;
            if (noInteractiveEntry)
            {
                issues.Add(new ScenarioPreflightIssue(
                    "ClickThrough", "interactive-entry",
                    "应用后主窗口和任务栏均不可交互，且托盘图标隐藏；请至少保留一个可操作入口。",
                    true));
            }

            var skin = profile.Settings.Skin ?? "";
            if (string.IsNullOrWhiteSpace(skin))
            {
                issues.Add(new ScenarioPreflightIssue("Skin", "skin-empty", "场景未指定主题。", true));
            }
            else if (themeExists != null && !themeExists(skin))
            {
                issues.Add(new ScenarioPreflightIssue(
                    "Skin", "theme-missing", $"主题不存在：{skin}", true));
            }

            var currentKeys = (current.MonitorItems ?? new List<MonitorItemConfig>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Key))
                .Select(item => item.Key)
                .ToHashSet(StringComparer.Ordinal);
            var targetKeys = (profile.Settings.MonitorItems ?? new List<ScenarioMonitorItem>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Key))
                .Select(item => item.Key)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var key in targetKeys.Where(key => !currentKeys.Contains(key)).OrderBy(key => key, StringComparer.Ordinal))
            {
                issues.Add(new ScenarioPreflightIssue(
                    "MonitorItems." + key, "monitor-item-unavailable",
                    $"当前配置没有监控项 {key}；应用时会跳过，不会自动添加。", false));
            }

            foreach (var key in currentKeys.Where(key => !targetKeys.Contains(key)).OrderBy(key => key, StringComparer.Ordinal))
            {
                issues.Add(new ScenarioPreflightIssue(
                    "MonitorItems." + key, "monitor-item-undefined",
                    $"场景未定义监控项 {key}；应用时会保留当前值。", false));
            }

            try
            {
                changes.AddRange(ScenarioDiff.Build(current, profile.Settings));
            }
            catch (Exception ex)
            {
                issues.Add(new ScenarioPreflightIssue(
                    "", "diff-failed", "计算差异失败：" + ex.Message, true));
            }

            return new ScenarioPreflightResult(
                issues.All(issue => !issue.Blocking),
                profile.DefinitionRevision,
                issues,
                changes);
        }
    }

    /// <summary>允许字段差异；不读取/比较任何排除字段。</summary>
    public static class ScenarioDiff
    {
        private static readonly Dictionary<string, PropertyInfo> _properties =
            typeof(ScenarioSettingsProjection)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0)
                .ToDictionary(property => property.Name, StringComparer.Ordinal);

        public static IReadOnlyList<ScenarioFieldChange> Build(
            Settings current,
            ScenarioSettingsProjection target)
        {
            ArgumentNullException.ThrowIfNull(current);
            ArgumentNullException.ThrowIfNull(target);

            var currentProjection = ScenarioProjection.Capture(current);
            var changes = new List<ScenarioFieldChange>();

            foreach (var rule in ScenarioFieldCatalog.Rules)
            {
                if (rule.Type == ScenarioFieldType.MonitorItems)
                {
                    if (!MonitorItemsEqual(currentProjection.MonitorItems, target.MonitorItems))
                    {
                        changes.Add(new ScenarioFieldChange(
                            "MonitorItems", rule.Module,
                            currentProjection.MonitorItems.Count,
                            target.MonitorItems.Count));
                    }
                    continue;
                }

                if (!_properties.TryGetValue(rule.Name, out var property))
                    continue;

                var currentValue = property.GetValue(currentProjection);
                var targetValue = property.GetValue(target);
                if (!Equals(currentValue, targetValue))
                {
                    changes.Add(new ScenarioFieldChange(
                        rule.Name, rule.Module, currentValue, targetValue));
                }
            }

            return changes;
        }

        private static bool MonitorItemsEqual(
            IReadOnlyList<ScenarioMonitorItem> left,
            IReadOnlyList<ScenarioMonitorItem>? right)
        {
            var a = (left ?? Array.Empty<ScenarioMonitorItem>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Key))
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToArray();
            var b = (right ?? new List<ScenarioMonitorItem>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Key))
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToArray();

            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i].Key != b[i].Key
                    || a[i].UserLabel != b[i].UserLabel
                    || a[i].TaskbarLabel != b[i].TaskbarLabel
                    || a[i].UnitPanel != b[i].UnitPanel
                    || a[i].UnitTaskbar != b[i].UnitTaskbar
                    || a[i].VisibleInPanel != b[i].VisibleInPanel
                    || a[i].VisibleInTaskbar != b[i].VisibleInTaskbar
                    || a[i].SortIndex != b[i].SortIndex
                    || a[i].TaskbarSortIndex != b[i].TaskbarSortIndex)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
