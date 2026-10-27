using System.Drawing;
using LiteMonitor;
using LiteMonitor.src.Core.Scenarios;
using Xunit;

namespace LiteMonitor.Tests;

public class ScenarioProjectionTests
{
    [Fact]
    public void CaptureAndApply_OnlyTouchesAllowedFields()
    {
        var source = new Settings
        {
            Skin = "ScenarioSkin",
            HorizontalMode = true,
            PanelWidth = 320,
            UIScale = 1.25,
            Opacity = 0.7,
            TopMost = false,
            Position = new Point(12, 34),
            RefreshMs = 2500,
            WebServerEnabled = true,
            WebServerPort = 5050,
            AutoStart = true,
            HideTrayIcon = true,
            Language = "ja",
            GpuThemeEffectsEnabled = true
        };
        source.MonitorItems.Add(new MonitorItemConfig
        {
            Key = "CPU.Load",
            UserLabel = "处理器",
            UnitPanel = null,
            UnitTaskbar = "",
            VisibleInPanel = true,
            VisibleInTaskbar = true,
            SortIndex = 3
        });

        var projection = ScenarioProjection.Capture(source);
        Assert.Equal("ScenarioSkin", projection.Skin);
        Assert.True(projection.HorizontalMode);
        Assert.Equal(320, projection.PanelWidth);
        Assert.Equal(2500, projection.RefreshMs);
        Assert.Null(projection.MonitorItems[0].UnitPanel);
        Assert.Equal("", projection.MonitorItems[0].UnitTaskbar);

        var target = new Settings
        {
            WebServerEnabled = false,
            WebServerPort = 5000,
            AutoStart = false,
            HideTrayIcon = false,
            Language = "en",
            GpuThemeEffectsEnabled = false
        };
        target.MonitorItems.Add(new MonitorItemConfig { Key = "CPU.Load", UnitPanel = "old", UnitTaskbar = "old" });

        ScenarioProjection.ApplyTo(target, projection);

        Assert.Equal("ScenarioSkin", target.Skin);
        Assert.True(target.HorizontalMode);
        Assert.Equal(320, target.PanelWidth);
        Assert.Null(target.MonitorItems[0].UnitPanel);
        Assert.Equal("", target.MonitorItems[0].UnitTaskbar);
        Assert.False(target.WebServerEnabled);
        Assert.Equal(5000, target.WebServerPort);
        Assert.False(target.AutoStart);
        Assert.False(target.HideTrayIcon);
        Assert.Equal("en", target.Language);
        Assert.False(target.GpuThemeEffectsEnabled);
    }

    [Fact]
    public void Catalog_RejectsExcludedAndUnknownFields()
    {
        Assert.False(ScenarioFieldCatalog.IsAllowed("WebServerPassword"));
        Assert.False(ScenarioFieldCatalog.IsAllowed("PluginInstances"));
        Assert.False(ScenarioFieldCatalog.IsAllowed("HideTrayIcon"));
        Assert.False(ScenarioFieldCatalog.IsAllowed("NotARealSetting"));
        Assert.True(ScenarioFieldCatalog.IsAllowed("RefreshMs"));
        Assert.True(ScenarioFieldCatalog.IsAllowed("MonitorItems"));
    }

    [Fact]
    public void Apply_DoesNotAddUnknownMonitorItems()
    {
        var source = new Settings();
        source.MonitorItems.Add(new MonitorItemConfig { Key = "GPU.0.Load", VisibleInPanel = true });
        var projection = ScenarioProjection.Capture(source);

        var target = new Settings();
        target.MonitorItems.Add(new MonitorItemConfig { Key = "CPU.Load", VisibleInPanel = true });
        ScenarioProjection.ApplyTo(target, projection);

        Assert.Single(target.MonitorItems);
        Assert.Equal("CPU.Load", target.MonitorItems[0].Key);
    }

    [Fact]
    public void Validate_RejectsOutOfRangeAndNull()
    {
        var projection = new ScenarioSettingsProjection { RefreshMs = 10 };
        Assert.Throws<ArgumentOutOfRangeException>(() => ScenarioProjection.Validate(projection));

        var nullSkin = new ScenarioSettingsProjection { Skin = null! };
        Assert.Throws<ArgumentNullException>(() => ScenarioProjection.Validate(nullSkin));
    }
}
