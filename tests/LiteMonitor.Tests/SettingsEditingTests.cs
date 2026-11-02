using LiteMonitor.src.Core;
using LiteMonitor.src.UI;
using Xunit;

namespace LiteMonitor.Tests;

public class SettingsEditingTests
{
    [Fact]
    public void NewSessionAndRevertedEditAreClean()
    {
        var draft = new Settings();
        var state = new SettingsEditState(draft);
        Assert.False(state.HasChanges(draft));
        int original = draft.PanelWidth;
        draft.PanelWidth++;
        Assert.True(state.HasChanges(draft));
        draft.PanelWidth = original;
        Assert.False(state.HasChanges(draft));
    }

    [Fact]
    public void CandidatePreservesExternalChangesAndTelemetry()
    {
        var live = new Settings();
        var draft = live.DeepClone();
        var state = new SettingsEditState(draft);
        draft.PanelWidth = 320;
        live.Opacity = 0.5;
        live.RecordedMaxCpuPower = 200;
        var candidate = state.CreateCandidate(live, draft);
        Assert.Equal(320, candidate.PanelWidth);
        Assert.Equal(0.5, candidate.Opacity);
        Assert.Equal(200, candidate.RecordedMaxCpuPower);
        Assert.NotEqual(320, live.PanelWidth);
        live.ClickThrough = true;
        draft.HideTrayIcon = true;
        draft.ShowTaskbar = false;
        var combined = state.CreateCandidate(live, draft);
        Assert.True(SettingsEditState.EnsureInteractiveEntry(combined));
        Assert.False(combined.HideTrayIcon);
    }

    [Fact]
    public void ExplicitUserMaximumIsAnEdit()
    {
        var draft = new Settings();
        var state = new SettingsEditState(draft);
        draft.RecordedMaxCpuPower++;
        Assert.True(state.HasChanges(draft));
    }

    [Fact]
    public void MonitorMergePreservesNewItemsAndOtherFields()
    {
        var live = new Settings { MonitorItems = new() { new() { Key = "CPU.Load", UserLabel = "before", UnitPanel = null } } };
        var draft = live.DeepClone();
        var state = new SettingsEditState(draft);
        draft.MonitorItems[0].UserLabel = "after";
        live.MonitorItems[0].UnitPanel = "%";
        live.MonitorItems.Add(new() { Key = "DASH.new", UserLabel = "external" });
        var candidate = state.CreateCandidate(live, draft);
        Assert.Equal("after", candidate.MonitorItems[0].UserLabel);
        Assert.Equal("%", candidate.MonitorItems[0].UnitPanel);
        Assert.Contains(candidate.MonitorItems, x => x.Key == "DASH.new");
    }

    [Fact]
    public void AcceptingImmediateSkinPreservesOtherPendingEdits()
    {
        var draft = new Settings();
        var state = new SettingsEditState(draft);
        draft.PanelWidth++;
        draft.Skin = "external-theme";
        state.AcceptField(draft, "Skin");
        Assert.True(state.HasChanges(draft));
        state.Accept(draft);
        Assert.False(state.HasChanges(draft));
    }

    [Fact]
    public void ReorderHandlesSparseIndicesAndPreservesExternalItemSlot()
    {
        var live = new Settings { MonitorItems = new() {
            new() { Key = "A", SortIndex = 100 }, new() { Key = "B", SortIndex = 200 }, new() { Key = "C", SortIndex = 300 } } };
        var draft = live.DeepClone();
        var state = new SettingsEditState(draft);
        draft.MonitorItems[1].SortIndex = 300;
        draft.MonitorItems[2].SortIndex = 200;
        live.MonitorItems.Add(new() { Key = "external", SortIndex = 150 });
        var candidate = state.CreateCandidate(live, draft);
        Assert.Equal(new[] { "A", "external", "C", "B" }, candidate.MonitorItems.OrderBy(x => x.SortIndex).Select(x => x.Key));
    }

    [Fact]
    public void PasswordAndPluginSecretsHaveStableInMemoryComparison()
    {
        var draft = new Settings { WebServerPassword = "test password", PluginInstances = new() { new() { Id = "test", InputValues = new() { ["api_key"] = "secret" } } } };
        var state = new SettingsEditState(draft);
        Assert.False(state.HasChanges(draft));
        Assert.False(state.HasChanges(draft));
        draft.PluginInstances[0].InputValues["api_key"] = "changed";
        Assert.True(state.HasChanges(draft));
    }
}
