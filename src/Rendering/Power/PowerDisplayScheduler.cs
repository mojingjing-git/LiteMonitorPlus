using LiteMonitor.Rendering.Gpu.Effects;

namespace LiteMonitor.Rendering.Power;

/// <summary>
/// P01/P02 的显示调度适配层：把纯策略结果落到 FrameScheduler。
/// 隐藏时只记录暂停状态，不把 0 FPS 传给 FrameScheduler。
/// </summary>
public sealed class PowerDisplayScheduler
{
    private readonly FrameScheduler _scheduler;
    private readonly int _normalFps;
    private DisplayCadence _current;

    public PowerDisplayScheduler(int normalFps)
    {
        if (normalFps is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(normalFps));
        }

        _normalFps = normalFps;
        _scheduler = new FrameScheduler(normalFps);
        _current = new DisplayCadence(normalFps, false, "normal");
    }

    public DisplayCadence Current => _current;

    public int CurrentFps => _current.Fps;

    public bool IsPaused => _current.Paused;

    public void Apply(PowerMode mode, bool onBattery, bool windowVisible)
    {
        DisplayCadence cadence = PowerPolicy.ResolveDisplay(
            mode,
            onBattery,
            windowVisible,
            _normalFps);

        if (!cadence.Paused && cadence.Fps != _scheduler.TargetFramesPerSecond)
        {
            _scheduler.UpdateTargetFramesPerSecond(cadence.Fps);
        }

        _current = cadence;
    }

    public bool TryTakeFrame(out FrameTick tick)
    {
        if (_current.Paused)
        {
            tick = default;
            return false;
        }

        return _scheduler.TryTakeFrame(out tick);
    }

    public void Reset() => _scheduler.Reset();
}
