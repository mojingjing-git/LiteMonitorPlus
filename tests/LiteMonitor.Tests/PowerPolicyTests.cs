using System;
using LiteMonitor.Rendering.Power;
using Xunit;

namespace LiteMonitor.Tests
{
    public sealed class PowerPolicyTests
    {
        [Fact]
        public void Off_Visible_UsesNormalCadence()
        {
            var cadence = PowerPolicy.ResolveDisplay(PowerMode.Off, onBattery: true, windowVisible: true, normalFps: 30);
            Assert.Equal(30, cadence.Fps);
            Assert.False(cadence.Paused);
        }

        [Fact]
        public void Auto_OnBattery_Uses15Fps()
        {
            var cadence = PowerPolicy.ResolveDisplay(PowerMode.Auto, onBattery: true, windowVisible: true);
            Assert.Equal(15, cadence.Fps);
            Assert.Equal("auto-battery", cadence.Reason);
        }

        [Fact]
        public void Auto_OnAc_UsesNormalCadence()
        {
            var cadence = PowerPolicy.ResolveDisplay(PowerMode.Auto, onBattery: false, windowVisible: true, normalFps: 60);
            Assert.Equal(60, cadence.Fps);
            Assert.Equal("normal", cadence.Reason);
        }

        [Fact]
        public void Eco_Uses10Fps_AndHiddenPauses()
        {
            Assert.Equal(10, PowerPolicy.ResolveDisplay(PowerMode.Eco, onBattery: false, windowVisible: true).Fps);
            var hidden = PowerPolicy.ResolveDisplay(PowerMode.Eco, onBattery: false, windowVisible: false);
            Assert.True(hidden.Paused);
            Assert.Equal(0, hidden.Fps);
        }

        [Fact]
        public void AnimationAndSampleIntervals_AreClamped()
        {
            Assert.Equal(10, PowerPolicy.ResolveAnimationFps(PowerMode.Eco, onBattery: false, normalFps: 60));
            Assert.Equal(15, PowerPolicy.ResolveAnimationFps(PowerMode.Auto, onBattery: true, normalFps: 60));
            Assert.Equal(5, PowerPolicy.ResolveDisplay(PowerMode.Eco, onBattery: false, windowVisible: true, normalFps: 5).Fps);
            Assert.Equal(5, PowerPolicy.ResolveDisplay(PowerMode.Auto, onBattery: true, windowVisible: true, normalFps: 5).Fps);
            Assert.Equal(5, PowerPolicy.ResolveAnimationFps(PowerMode.Eco, onBattery: false, normalFps: 5));
            Assert.Equal(TimeSpan.FromSeconds(2), PowerPolicy.ResolveSampleInterval(PowerMode.Eco, false, TimeSpan.FromMilliseconds(500)));
            Assert.Equal(TimeSpan.FromMilliseconds(1500), PowerPolicy.ResolveSampleInterval(PowerMode.Auto, true, TimeSpan.FromMilliseconds(500)));
            Assert.Equal(TimeSpan.FromSeconds(5), PowerPolicy.ResolveSampleInterval(PowerMode.Eco, false, TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void InvalidArguments_AreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PowerPolicy.ResolveDisplay(PowerMode.Off, false, true, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => PowerPolicy.ResolveSampleInterval(PowerMode.Off, false, TimeSpan.Zero));
        }
    }
}
