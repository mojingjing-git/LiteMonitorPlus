using System;

namespace LiteMonitor.Rendering.Power
{
    public enum PowerMode
    {
        Off = 0,
        Auto = 1,
        Eco = 2,
    }

    /// <summary>显示调度的纯策略结果；Fps=0 表示暂停呈现。</summary>
    public readonly record struct DisplayCadence(int Fps, bool Paused, string Reason);

    /// <summary>
    /// P01/P02 低功耗纯策略：不读取系统状态、不持有窗口/计时器。
    /// 功率来源、可见性和配置由组合根注入，便于真实设备测试与回放。
    /// </summary>
    public static class PowerPolicy
    {
        public const int AutoBatteryFps = 15;
        public const int EcoFps = 10;

        public static DisplayCadence ResolveDisplay(
            PowerMode mode,
            bool onBattery,
            bool windowVisible,
            int normalFps = 30)
        {
            if (normalFps is < 1 or > 240)
                throw new ArgumentOutOfRangeException(nameof(normalFps));

            if (!windowVisible)
                return new DisplayCadence(0, true, "hidden");

            return mode switch
            {
                PowerMode.Eco => new DisplayCadence(Math.Min(normalFps, EcoFps), false, "eco"),
                PowerMode.Auto when onBattery => new DisplayCadence(Math.Min(normalFps, AutoBatteryFps), false, "auto-battery"),
                _ => new DisplayCadence(normalFps, false, "normal"),
            };
        }

        public static int ResolveAnimationFps(
            PowerMode mode,
            bool onBattery,
            int normalFps = 60)
        {
            if (normalFps is < 1 or > 240)
                throw new ArgumentOutOfRangeException(nameof(normalFps));

            return mode switch
            {
                PowerMode.Eco => Math.Min(normalFps, EcoFps),
                PowerMode.Auto when onBattery => Math.Min(normalFps, AutoBatteryFps),
                _ => normalFps,
            };
        }

        public static TimeSpan ResolveSampleInterval(
            PowerMode mode,
            bool onBattery,
            TimeSpan normalInterval)
        {
            if (normalInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(normalInterval));

            return mode switch
            {
                PowerMode.Eco when normalInterval < TimeSpan.FromSeconds(2) => TimeSpan.FromSeconds(2),
                PowerMode.Auto when onBattery && normalInterval < TimeSpan.FromSeconds(1.5) => TimeSpan.FromSeconds(1.5),
                _ => normalInterval,
            };
        }
    }
}
