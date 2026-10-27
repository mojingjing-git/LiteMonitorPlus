using LiteMonitor;
using LiteMonitor.Core.Actions.Services;
using LiteMonitor.src.Core.Scenarios;
using Xunit;

namespace LiteMonitor.Tests;

public class ScenarioApplyCoordinatorTests
{
    private static Settings Baseline() => new()
    {
        Skin = "DarkFlat_Classic",
        PanelWidth = 300,
        RefreshMs = 1000
    };

    private static ScenarioProfile ProfileWithWidth(int width)
    {
        var source = Baseline();
        source.PanelWidth = width;
        return ScenarioProfile.Create("工作", source);
    }

    [Fact]
    public void Apply_SavesCandidateThenAppliesModules()
    {
        var current = Baseline();
        var profile = ProfileWithWidth(440);
        Settings? saved = null;
        Settings? applied = null;
        var coordinator = new ScenarioApplyCoordinator(
            save: candidate => { saved = candidate; return true; },
            applyModules: (before, candidate) =>
            {
                applied = candidate;
                return new[] { ModuleApplyResult.Ok("Appearance", "saved", "applied") };
            },
            themeExists: _ => true);

        var outcome = coordinator.Apply(profile, current, profile.DefinitionRevision);

        Assert.Equal(ApplyOperationStatus.Applied, outcome.Operation.OverallStatus);
        Assert.NotNull(saved);
        Assert.Equal(440, saved!.PanelWidth);
        Assert.NotNull(applied);
        Assert.Equal(440, applied!.PanelWidth);
        Assert.Equal("appearance", outcome.Operation.Modules[0].Module.ToLowerInvariant());
    }

    [Fact]
    public void Apply_SaveFailure_DoesNotInvokeModules()
    {
        var current = Baseline();
        var profile = ProfileWithWidth(440);
        var called = false;
        var coordinator = new ScenarioApplyCoordinator(
            save: _ => false,
            applyModules: (_, _) => { called = true; return Array.Empty<ModuleApplyResult>(); },
            themeExists: _ => true);

        var outcome = coordinator.Apply(profile, current, profile.DefinitionRevision);

        Assert.False(called);
        Assert.Equal(ApplyOperationStatus.Failed, outcome.Operation.OverallStatus);
        Assert.Equal("settings-write-failed", outcome.Operation.FailureCode);
        Assert.Equal(300, current.PanelWidth);
    }

    [Fact]
    public void Apply_PartialModules_AreReportedAsPartial()
    {
        var current = Baseline();
        var profile = ProfileWithWidth(440);
        var coordinator = new ScenarioApplyCoordinator(
            save: _ => true,
            applyModules: (_, _) => new[]
            {
                ModuleApplyResult.Ok("Appearance"),
                ModuleApplyResult.Fail("Taskbar", "saved", "live", "taskbar failed")
            },
            themeExists: _ => true);

        var outcome = coordinator.Apply(profile, current, profile.DefinitionRevision);

        Assert.Equal(ApplyOperationStatus.Partial, outcome.Operation.OverallStatus);
        Assert.Contains(outcome.Operation.Modules, module => !module.Applied && module.Module == "Taskbar");
    }

    [Fact]
    public void Apply_StaleRevision_IsRejectedBeforeSave()
    {
        var current = Baseline();
        var profile = ProfileWithWidth(440);
        var saved = false;
        var coordinator = new ScenarioApplyCoordinator(
            save: _ => { saved = true; return true; },
            applyModules: (_, _) => Array.Empty<ModuleApplyResult>(),
            themeExists: _ => true);

        var outcome = coordinator.Apply(profile, current, profile.DefinitionRevision + 1);

        Assert.False(saved);
        Assert.Equal("stale-revision", outcome.Operation.FailureCode);
    }

    [Fact]
    public void Apply_NoChanges_DoesNotSaveButReportsApplied()
    {
        var current = Baseline();
        var profile = ScenarioProfile.Create("工作", current);
        var saved = false;
        var coordinator = new ScenarioApplyCoordinator(
            save: _ => { saved = true; return true; },
            applyModules: (_, _) => Array.Empty<ModuleApplyResult>(),
            themeExists: _ => true);

        var outcome = coordinator.Apply(profile, current, profile.DefinitionRevision);

        Assert.False(saved);
        Assert.Equal(ApplyOperationStatus.Applied, outcome.Operation.OverallStatus);
        Assert.Equal("no-change", outcome.Operation.Modules[0].EffectiveState);
    }
}
