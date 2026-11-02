using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI;
using LiteMonitor.src.UI.Controls;
using Xunit;

namespace LiteMonitor.Tests;

public class SettingsEditingDesktopTests
{
    private static void Sta(Action action)
    {
        Exception error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Desktop test timed out");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void LauncherReusesWindowAndNavigationRemainsClean() => Sta(() =>
    {
        var cfg = new Settings();
        using var first = SettingsWindowLauncher.Open(cfg, null!, null!);
        var second = SettingsWindowLauncher.Open(cfg, null!, null!, "Monitor");
        Assert.Same(first, second);
        first.SwitchPage("MainPanel");
        first.SwitchPage("Monitor");
        var state = (bool)typeof(SettingsForm).GetMethod("HasPendingEdits", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(first, null)!;
        Assert.False(state);
    });

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 1)]
    [InlineData("{u}/S", 2)]
    public void UnitModeRoundTripsLegacyValues(string value, int selected) => Sta(() =>
    {
        var cfg = new MonitorItemConfig { Key = "NET.Down", UnitPanel = value };
        using var row = new MonitorItemRow(cfg, false);
        var mode = row.Controls.OfType<ComboBox>().Single();
        Assert.Equal(selected, mode.SelectedIndex);
        row.SyncToConfig();
        Assert.Equal(value, cfg.UnitPanel);
        mode.SelectedIndex = 1;
        row.SyncToConfig();
        Assert.Equal("", cfg.UnitPanel);
        mode.SelectedIndex = 0;
        row.SyncToConfig();
        Assert.Null(cfg.UnitPanel);
    });

    [Fact]
    public void UnitControlsFitBetweenNameAndVisibilityColumns() => Sta(() =>
    {
        using var row = new MonitorItemRow(new MonitorItemConfig { Key = "CPU.Load" }, false);
        var mode = row.Controls.OfType<ComboBox>().Single();
        var unit = (LiteUnderlineInput)typeof(MonitorItemRow).GetField("_inputUnit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(row)!;
        Assert.True(mode.Right <= unit.Left);
        Assert.True(unit.Right <= MonitorLayout.X_COL3);
    });

    [Fact]
    public void RevertingMonitorNameRestoresStoredValueWithoutRewritingText() => Sta(() =>
    {
        var cfg = new MonitorItemConfig { Key = "CPU.Load", UserLabel = "Original" };
        using var row = new MonitorItemRow(cfg, false);
        var input = (LiteUnderlineInput)typeof(MonitorItemRow).GetField("_inputName", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(row)!;
        input.Inner.Text = "Original X ";
        row.SyncToConfig();
        Assert.Equal("Original X ", input.Inner.Text);
        input.Inner.Text = "Original";
        row.SyncToConfig();
        Assert.Equal("Original", cfg.UserLabel);
    });
}
