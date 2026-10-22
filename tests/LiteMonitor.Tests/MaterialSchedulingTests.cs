using System.Diagnostics;
using LiteMonitor.Rendering.Gpu.Effects;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class MaterialSchedulingTests
{
    [Fact]
    public void SchedulerCapsRequestsAtTargetCadence()
    {
        long now = 0;
        FrameScheduler scheduler = new(30, () => now);

        Assert.True(scheduler.TryTakeFrame(out FrameTick first));
        Assert.False(scheduler.TryTakeFrame(out _));
        now = 34_000_000;
        Assert.True(scheduler.TryTakeFrame(out FrameTick second));

        Assert.Equal(1, first.FrameIndex);
        Assert.Equal(2, second.FrameIndex);
    }

    [Fact]
    public void SchedulerAcceptsSmallEarlyFramesWithoutAlternatingLateFrames()
    {
        long now = 0;
        FrameScheduler scheduler = new(30, () => now);

        Assert.True(scheduler.TryTakeFrame(out _));
        now += TicksFor(29);
        Assert.False(scheduler.TryTakeFrame(out _));

        now += TicksFor(2);
        Assert.True(scheduler.TryTakeFrame(out FrameTick early));
        Assert.False(early.WasLate);

        now += TicksFor(31);
        Assert.True(scheduler.TryTakeFrame(out FrameTick next));
        Assert.False(next.WasLate);
    }

    [Fact]
    public void SchedulerReportsLateFramesAndDoesNotAccumulateBacklog()
    {
        long now = 0;
        FrameScheduler scheduler = new(30, () => now);
        scheduler.TryTakeFrame(out _);
        now = 100_000_000;

        Assert.True(scheduler.TryTakeFrame(out FrameTick tick));
        Assert.True(tick.WasLate);
        Assert.False(scheduler.TryTakeFrame(out _));
    }

    [Fact]
    public void SchedulerAppliesUpdatedTargetAndStartsFreshCadence()
    {
        long now = 0;
        FrameScheduler scheduler = new(30, () => now);

        Assert.True(scheduler.TryTakeFrame(out _));
        Assert.False(scheduler.TryTakeFrame(out _));

        scheduler.UpdateTargetFramesPerSecond(60);
        Assert.Equal(60, scheduler.TargetFramesPerSecond);
        Assert.True(scheduler.TryTakeFrame(out FrameTick resetFrame));
        Assert.Equal(1, resetFrame.FrameIndex);

        now += TicksFor(14);
        Assert.False(scheduler.TryTakeFrame(out _));
        now += TicksFor(3);
        Assert.True(scheduler.TryTakeFrame(out FrameTick updatedFrame));
        Assert.Equal(2, updatedFrame.FrameIndex);
    }

    [Fact]
    public void LatestMailboxDropsSupersededFramesAndConsumesOne()
    {
        LatestFrameMailbox<int> mailbox = new();
        mailbox.Publish(1);
        mailbox.Publish(2);

        Assert.True(mailbox.TryTake(out int value));
        Assert.Equal(2, value);
        Assert.False(mailbox.TryTake(out _));
        Assert.Equal(1, mailbox.Capacity);
        Assert.Equal(2, mailbox.Published);
        Assert.Equal(1, mailbox.Dropped);
        Assert.Equal(1, mailbox.Consumed);
    }

    [Fact]
    public void FixedTimeMotionIsDeterministicWithoutWallClock()
    {
        MaterialMotionSample first = MaterialMotionTimeline.Evaluate(
            TimeSpan.FromSeconds(1.5),
            TimeSpan.FromMilliseconds(90),
            TimeSpan.FromMilliseconds(180),
            TimeSpan.FromMilliseconds(2800));
        MaterialMotionSample second = MaterialMotionTimeline.Evaluate(
            TimeSpan.FromSeconds(1.5),
            TimeSpan.FromMilliseconds(90),
            TimeSpan.FromMilliseconds(180),
            TimeSpan.FromMilliseconds(2800));

        Assert.Equal(first, second);
        Assert.Equal(0.5f, first.TransitionProgress, precision: 6);
        Assert.InRange(first.ScannerPhase, 0, 1);
        Assert.InRange(first.StatusPulse, 0, 1);
    }

    private static long TicksFor(int milliseconds) =>
        Stopwatch.Frequency * milliseconds / 1000;
}
