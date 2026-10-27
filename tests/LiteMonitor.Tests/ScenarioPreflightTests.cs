using LiteMonitor;
using LiteMonitor.src.Core.Scenarios;
using Xunit;

namespace LiteMonitor.Tests;

public class ScenarioPreflightTests
{
    private static Settings Baseline() => new()
    {
        Skin = "DarkFlat_Classic",
        PanelWidth = 300,
        RefreshMs = 1000,
        WebServerPort = 51888
    };

    [Fact]
    public void ValidProfile_WithThemeAndChanges_CanApply()
    {
        var current = Baseline();
        var target = Baseline();
        target.PanelWidth = 420;
        target.Skin = "DarkFlat_Classic";
        var profile = ScenarioProfile.Create("工作", target);

        var result = ScenarioPreflight.Inspect(profile, current, _ => true);

        Assert.True(result.CanApply);
        Assert.DoesNotContain(result.Issues, issue => issue.Blocking);
        Assert.Contains(result.Changes, change => change.Field == "PanelWidth"
            && Equals(change.CurrentValue, 300)
            && Equals(change.TargetValue, 420));
    }

    [Fact]
    public void MissingTheme_IsBlocking()
    {
        var profile = ScenarioProfile.Create("工作", Baseline());
        var result = ScenarioPreflight.Inspect(profile, Baseline(), _ => false);

        Assert.False(result.CanApply);
        Assert.Contains(result.Issues, issue => issue.Code == "theme-missing" && issue.Blocking);
    }

    [Fact]
    public void UnsupportedSchema_IsBlocking()
    {
        var profile = ScenarioProfile.Create("工作", Baseline());
        profile.SchemaVersion = ScenarioFieldCatalog.CurrentSchemaVersion + 1;

        var result = ScenarioPreflight.Inspect(profile, Baseline(), _ => true);

        Assert.False(result.CanApply);
        Assert.Contains(result.Issues, issue => issue.Code == "schema-unsupported" && issue.Blocking);
    }

    [Fact]
    public void ExcludedFields_DoNotAppearInDiff()
    {
        var current = Baseline();
        var target = Baseline();
        target.WebServerPort = 59999;
        target.AutoStart = true;
        var profile = ScenarioProfile.Create("工作", target);

        var result = ScenarioPreflight.Inspect(profile, current, _ => true);

        Assert.DoesNotContain(result.Changes, change =>
            change.Field is "WebServerPort" or "AutoStart");
    }

    [Fact]
    public void MissingMonitorItem_IsWarningNotBlocking()
    {
        var current = Baseline();
        var profile = ScenarioProfile.Create("工作", current);
        profile.Settings.MonitorItems.Add(new ScenarioMonitorItem { Key = "not-installed" });

        var result = ScenarioPreflight.Inspect(profile, current, _ => true);

        Assert.True(result.CanApply);
        Assert.Contains(result.Issues, issue =>
            issue.Code == "monitor-item-unavailable" && !issue.Blocking);
    }
}
