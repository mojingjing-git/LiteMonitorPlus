using LiteMonitor.Rendering.Power;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class PowerDisplaySchedulerTests
{
    [Fact]
    public void Normal_Visible_UsesConfiguredFps()
    {
        var scheduler = new PowerDisplayScheduler(60);

        scheduler.Apply(PowerMode.Off, onBattery: true, windowVisible: true);

        Assert.Equal(60, scheduler.CurrentFps);
        Assert.False(scheduler.IsPaused);
    }

    [Fact]
    public void Auto_OnBattery_ClampsToFifteenFps()
    {
        var scheduler = new PowerDisplayScheduler(60);

        scheduler.Apply(PowerMode.Auto, onBattery: true, windowVisible: true);

        Assert.Equal(PowerPolicy.AutoBatteryFps, scheduler.CurrentFps);
        Assert.Equal("auto-battery", scheduler.Current.Reason);
    }

    [Fact]
    public void Eco_And_Hidden_AreRepresentedWithoutZeroFpsSchedulerUpdate()
    {
        var scheduler = new PowerDisplayScheduler(60);

        scheduler.Apply(PowerMode.Eco, onBattery: false, windowVisible: true);
        Assert.Equal(PowerPolicy.EcoFps, scheduler.CurrentFps);

        scheduler.Apply(PowerMode.Eco, onBattery: false, windowVisible: false);
        Assert.True(scheduler.IsPaused);
        Assert.Equal(0, scheduler.CurrentFps);
        Assert.False(scheduler.TryTakeFrame(out _));
    }
}
