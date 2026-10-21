using System.Diagnostics;

namespace LiteMonitor.Rendering.Gpu.Effects;

public readonly record struct FrameTick(
    long FrameIndex,
    long TimestampTicks,
    TimeSpan Delta,
    TimeSpan AnimationTime,
    bool WasLate);

public sealed class FrameScheduler
{
    private readonly Func<long> _timestampProvider;
    private long _framePeriodTicks;
    private long _earlyToleranceTicks;
    private long _nextFrameTicks;
    private long _lastFrameTicks;
    private long _frameIndex;
    private bool _started;

    public FrameScheduler(int targetFramesPerSecond, Func<long>? timestampProvider = null)
    {
        if (targetFramesPerSecond is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFramesPerSecond));
        }

        TargetFramesPerSecond = targetFramesPerSecond;
        _framePeriodTicks = Stopwatch.Frequency / targetFramesPerSecond;
        _earlyToleranceTicks = Math.Max(1, _framePeriodTicks / 10);
        _timestampProvider = timestampProvider ?? Stopwatch.GetTimestamp;
    }

    public int TargetFramesPerSecond { get; private set; }

    public void UpdateTargetFramesPerSecond(int targetFramesPerSecond)
    {
        if (targetFramesPerSecond is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFramesPerSecond));
        }

        TargetFramesPerSecond = targetFramesPerSecond;
        _framePeriodTicks = Stopwatch.Frequency / targetFramesPerSecond;
        _earlyToleranceTicks = Math.Max(1, _framePeriodTicks / 10);
        Reset();
    }

    public bool TryTakeFrame(out FrameTick tick)
    {
        long now = _timestampProvider();
        if (!_started)
        {
            _started = true;
            _lastFrameTicks = now;
            _nextFrameTicks = now;
        }

        if (now + _earlyToleranceTicks < _nextFrameTicks)
        {
            tick = default;
            return false;
        }

        long lateBy = Math.Max(0, now - _nextFrameTicks);
        long elapsedTicks = Math.Max(0, now - _lastFrameTicks);
        _lastFrameTicks = now;
        // Rebase after each accepted frame. The UI message timer has a coarse,
        // platform-dependent cadence; accumulating against its theoretical
        // boundary makes every other frame miss by one timer interval.
        _nextFrameTicks = now + _framePeriodTicks;

        tick = new FrameTick(
            ++_frameIndex,
            now,
            TimeSpan.FromSeconds(elapsedTicks / (double)Stopwatch.Frequency),
            TimeSpan.FromSeconds(now / (double)Stopwatch.Frequency),
            WasLate: lateBy > _framePeriodTicks / 2);
        return true;
    }

    public void Reset()
    {
        _started = false;
        _nextFrameTicks = 0;
        _lastFrameTicks = 0;
        _frameIndex = 0;
    }
}

public sealed class LatestFrameMailbox<T>
{
    private readonly object _gate = new();
    private T? _latest;
    private bool _hasValue;
    private long _published;
    private long _consumed;
    private long _dropped;

    public int Capacity => 1;

    public long Published => Interlocked.Read(ref _published);

    public long Consumed => Interlocked.Read(ref _consumed);

    public long Dropped => Interlocked.Read(ref _dropped);

    public void Publish(T value)
    {
        lock (_gate)
        {
            if (_hasValue)
            {
                _dropped++;
            }

            _latest = value;
            _hasValue = true;
            _published++;
        }
    }

    public bool TryTake(out T? value)
    {
        lock (_gate)
        {
            if (!_hasValue)
            {
                value = default;
                return false;
            }

            value = _latest;
            _latest = default;
            _hasValue = false;
            _consumed++;
            return true;
        }
    }
}

public readonly record struct MaterialMotionSample(
    float Seconds,
    float TransitionProgress,
    float ScannerPhase,
    float StatusPulse);

public static class MaterialMotionTimeline
{
    public static MaterialMotionSample Evaluate(
        TimeSpan animationTime,
        TimeSpan stateAge,
        TimeSpan stateTransition,
        TimeSpan scannerPeriod)
    {
        double seconds = Math.Max(0, animationTime.TotalSeconds);
        double transition = stateTransition <= TimeSpan.Zero
            ? 1
            : Math.Clamp(stateAge.TotalSeconds / stateTransition.TotalSeconds, 0, 1);
        double scanner = scannerPeriod <= TimeSpan.Zero
            ? 0
            : (seconds % scannerPeriod.TotalSeconds) / scannerPeriod.TotalSeconds;
        float pulse = (float)(0.5 + 0.5 * Math.Sin(seconds * Math.PI * 2.0 / 1.2));
        return new MaterialMotionSample(
            (float)seconds,
            (float)transition,
            (float)scanner,
            pulse);
    }
}
