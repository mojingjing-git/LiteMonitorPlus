using System;
using System.Threading;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 任务栏主题检查节奏（计划 §4.3 / §6.1）的行为测试。
    ///
    /// ★ 这里**不再自己复制一份 5 秒配置**：
    ///   周期来自生产代码的 `TaskbarForm.ThemeCheckInterval`，
    ///   触发器来自生产代码的 `TaskbarForm.CreateThemeCheckTrigger(...)`，
    ///   因此"把 TaskbarForm 的周期改成 10 秒/1 秒"会被本用例直接测出来。
    ///
    /// 主题检查本身在 WinForms 窗体里（TaskbarForm.Tick → TaskbarBizHelper.CheckTheme），
    /// 这里测的是它依赖的**单调截止时间触发器**：
    /// - 正常重复 Tick 不反复读取主题 / 反复应用分层样式；
    /// - 约每 5 秒检查一次；
    /// - 墙上时间回拨不影响检查节奏。
    ///
    /// 全部用假时钟驱动，不用 Sleep 验证定时逻辑。
    /// </summary>
    public class ThemeCheckScheduleTests
    {
        /// <summary>假时钟：单调时间戳与墙上时间独立。</summary>
        private sealed class FakeTimeProvider : TimeProvider
        {
            private long _ticks;
            private DateTimeOffset _wallClock;

            public FakeTimeProvider()
            {
                _wallClock = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
            }

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;

            public override long GetTimestamp() => Interlocked.Read(ref _ticks);

            public override DateTimeOffset GetUtcNow() => _wallClock;

            /// <summary>推进单调时间（同时推进墙上时间）。</summary>
            public void Advance(TimeSpan delta)
            {
                Interlocked.Add(ref _ticks, delta.Ticks);
                _wallClock += delta;
            }

            /// <summary>只回拨墙上时间（模拟系统时间被改回），单调时间戳不受影响。</summary>
            public void RewindWallClock(TimeSpan delta)
            {
                _wallClock -= delta;
            }
        }

        private static MonotonicPeriodicTrigger NewThemeTrigger(FakeTimeProvider clock)
        {
            // ★ 用生产代码的工厂方法与周期常量，不在测试里复制配置
            return TaskbarForm.CreateThemeCheckTrigger(clock);
        }

        [Fact]
        public void 主题检查周期由生产代码的唯一常量决定()
        {
            // 计划 §4.3：任务栏主题检查「约每 5 秒一次」。
            // 如果有人把 TaskbarForm 的周期改成别的值，这条用例会直接失败。
            Assert.Equal(TimeSpan.FromSeconds(5), TaskbarForm.ThemeCheckInterval);
        }

        [Fact]
        public void 主题检查约每5秒一次且重复Tick不重复读取()
        {
            var clock = new FakeTimeProvider();
            var trigger = NewThemeTrigger(clock);
            int themeChecks = 0;

            // 100ms 的高频 Tick 持续 4.9 秒 -> 一次都不检查
            for (int i = 0; i < 49; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                if (trigger.TryConsume()) themeChecks++;
            }
            Assert.Equal(0, themeChecks);

            // 到达 5 秒 -> 检查一次
            clock.Advance(TimeSpan.FromMilliseconds(100));
            if (trigger.TryConsume()) themeChecks++;
            Assert.Equal(1, themeChecks);

            // 接下来的 4.9 秒高频 Tick -> 不再检查
            for (int i = 0; i < 49; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                if (trigger.TryConsume()) themeChecks++;
            }
            Assert.Equal(1, themeChecks);

            // 到达 10 秒 -> 再检查一次
            clock.Advance(TimeSpan.FromMilliseconds(100));
            if (trigger.TryConsume()) themeChecks++;
            Assert.Equal(2, themeChecks);
        }

        [Fact]
        public void 主题检查在60秒内最多12次()
        {
            var clock = new FakeTimeProvider();
            var trigger = NewThemeTrigger(clock);
            int themeChecks = 0;

            // 100ms 刷新率跑满 60 秒
            for (int i = 0; i < 600; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                if (trigger.TryConsume()) themeChecks++;
            }

            // 5 秒一次 -> 60 秒内 12 次（边界允许一次计时误差）
            Assert.InRange(themeChecks, 11, 12);
        }

        [Fact]
        public void 主题检查不受墙上时间回拨影响()
        {
            var clock = new FakeTimeProvider();
            var trigger = NewThemeTrigger(clock);
            int themeChecks = 0;

            // 系统时间被改回 2 小时
            clock.RewindWallClock(TimeSpan.FromHours(2));

            for (int i = 0; i < 50; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                if (trigger.TryConsume()) themeChecks++;
            }

            // 单调时间到 5 秒 -> 正常检查一次
            Assert.Equal(1, themeChecks);
        }
    }
}
