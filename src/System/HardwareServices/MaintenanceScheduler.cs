using System;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 单调周期触发器：用**单调时钟**（TimeProvider）判断周期任务是否到期。
    ///
    /// 解决的问题（计划 §4.3 / §6.1）：
    ///  1. 旧实现用「整数秒计数器取模」判断到期（secondsCounter % 60 == 5）。
    ///     同一个到期秒内被调用 N 次就会真正执行 N 次
    ///     （已实测：同一到期秒调用十次 → 十次文件重写）。
    ///  2. 旧实现依赖「墙上时间」，系统时间回拨会让到期判断失真。
    ///
    /// 语义：
    ///  - 同一到期时间**最多消费一次**（比较 + 推进在同一临界区内完成，多线程调用也只会有一次成功）；
    ///  - 跳过若干周期后**最多执行一次**，然后安排下一次（不补跑一串）；
    ///  - 状态由**实例**持有，不使用易互相污染的全局静态时间戳；
    ///  - 墙上时间回拨不影响判断（只使用单调时间戳）。
    /// </summary>
    public sealed class MonotonicPeriodicTrigger
    {
        private readonly TimeProvider _time;
        private readonly object _gate = new object();
        private TimeSpan _period;
        private TimeSpan _initialDelay;
        private long _nextDue;      // 下一次到期的单调时间戳
        private bool _armed;        // 是否已经排定过截止时间

        /// <param name="initialDelay">首次到期前的延迟（从创建时刻算起）。</param>
        /// <param name="period">后续周期。</param>
        /// <param name="timeProvider">可注入的单调时钟，默认 <see cref="TimeProvider.System"/>。</param>
        public MonotonicPeriodicTrigger(TimeSpan initialDelay, TimeSpan period, TimeProvider? timeProvider = null)
        {
            if (period <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(period), "周期必须为正数");

            _initialDelay = initialDelay < TimeSpan.Zero ? TimeSpan.Zero : initialDelay;
            _period = period;
            _time = timeProvider ?? TimeProvider.System;
            Arm();
        }

        /// <summary>当前周期。</summary>
        public TimeSpan Period => _period;

        /// <summary>首次到期前的延迟。</summary>
        public TimeSpan InitialDelay => _initialDelay;

        /// <summary>距离下一次到期还有多久（已经到期时返回 <see cref="TimeSpan.Zero"/>）。</summary>
        public TimeSpan Remaining
        {
            get
            {
                lock (_gate)
                {
                    if (!_armed) return TimeSpan.Zero;
                    long now = _time.GetTimestamp();
                    if (now >= _nextDue) return TimeSpan.Zero;
                    return TimeSpan.FromSeconds((double)(_nextDue - now) / Frequency);
                }
            }
        }

        /// <summary>
        /// 到期则消费一次并返回 true；**同一到期时间只会被消费一次**。
        /// 未到期返回 false（不执行任何动作）。
        /// 消费后会把截止时间推进到「现在之后」的下一个周期：跳过若干周期也只执行一次，不补跑。
        ///
        /// ★ 比较与推进在同一个临界区内完成：即使多个线程同时调用，
        ///   同一个到期时间也只会有一次返回 true。
        /// </summary>
        public bool TryConsume()
        {
            long now = _time.GetTimestamp();

            lock (_gate)
            {
                if (!_armed)
                {
                    // 理论上不会发生（构造函数已排定），兜底：从现在开始排首次到期
                    Arm(now);
                    return false;
                }

                if (now < _nextDue) return false;

                Advance(now);
                return true;
            }
        }

        /// <summary>重新排定（相当于重新开始一轮周期）。</summary>
        public void Reset()
        {
            lock (_gate) { Arm(); }
        }

        /// <summary>重新排定，并可同时更换周期与首次延迟。</summary>
        public void Reset(TimeSpan initialDelay, TimeSpan period)
        {
            if (period <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(period), "周期必须为正数");

            lock (_gate)
            {
                _initialDelay = initialDelay < TimeSpan.Zero ? TimeSpan.Zero : initialDelay;
                _period = period;
                Arm();
            }
        }

        // ============================================================
        // 内部实现
        // ============================================================

        private double Frequency
        {
            get
            {
                long frequency = _time.TimestampFrequency;
                return frequency > 0 ? frequency : TimeSpan.TicksPerSecond;
            }
        }

        private void Arm() => Arm(_time.GetTimestamp());

        private void Arm(long now)
        {
            _nextDue = SafeAdd(now, ToTimestampTicks(_initialDelay));
            _armed = true;
        }

        /// <summary>
        /// 消费掉当前到期时间，并把截止时间推进到未来。
        /// 关键：不是「now + period」，而是「原截止时间 + 若干个周期」——
        /// 这样到期点不会因为请求延迟而漂移；落后很多时直接跳到未来，不补跑一串。
        /// </summary>
        private void Advance(long now)
        {
            long periodTicks = ToTimestampTicks(_period);
            if (periodTicks <= 0) periodTicks = 1;

            long overdue = now - _nextDue;   // >= 0
            if (overdue > long.MaxValue - periodTicks * 2)
            {
                // 极端溢出保护：直接排到 now + period
                _nextDue = SafeAdd(now, periodTicks);
                return;
            }

            long periods = overdue / periodTicks + 1;   // 需要推进的周期数（至少 1）
            _nextDue = SafeAdd(_nextDue, periods * periodTicks);
        }

        private long ToTimestampTicks(TimeSpan span)
        {
            double ticks = span.TotalSeconds * Frequency;
            if (ticks <= 0) return 0;
            if (ticks >= long.MaxValue) return long.MaxValue;
            return (long)Math.Round(ticks);
        }

        private static long SafeAdd(long a, long b)
        {
            if (b <= 0) return a;
            if (a > long.MaxValue - b) return long.MaxValue;
            return a + b;
        }
    }

    /// <summary>
    /// 一次维护轮次里「实际到期并被消费」的任务。
    /// 用于让调用方（例如 HardwareMonitor）把统计计数器记在**真实执行**上，
    /// 而不是记在「计数器取模刚好命中」这种可能与实际执行脱节的地方。
    /// </summary>
    public readonly struct MaintenanceRunResult
    {
        public MaintenanceRunResult(bool trafficSave, bool gcRequest, bool workingSetTrim)
        {
            TrafficSave = trafficSave;
            GcRequest = gcRequest;
            WorkingSetTrim = workingSetTrim;
        }

        /// <summary>本次执行了流量保存。</summary>
        public bool TrafficSave { get; }
        /// <summary>本次请求了自动 GC。</summary>
        public bool GcRequest { get; }
        /// <summary>本次执行了自动工作集检查。</summary>
        public bool WorkingSetTrim { get; }
        /// <summary>本次是否有任何维护任务到期。</summary>
        public bool Any => TrafficSave || GcRequest || WorkingSetTrim;
    }

        /// <summary>
        /// 维护任务调度器（计划 §4.3 的周期约定）：
    ///
    /// | 工作 | 约定周期/首次触发 |
    /// |---|---|
    /// | 流量保存 | 首次约 5 秒，此后每 60 秒 |
    /// | 自动 GC 请求 | 首次约 30 秒，此后每 180 秒 |
    /// | 自动工作集检查 | 首次约 45 秒，此后每 300 秒 |
    ///
    /// 全部由**单调截止时间**驱动：
    ///  - 同一到期时间最多消费一次（同一秒内调用十次也只执行一次）；
    ///  - 跳过若干周期后最多执行一次，不补跑一串；
    ///  - 墙上时间回拨不影响调度。
    ///
    /// 三个动作都可以注入（便于用假时钟做单元测试，不需要真实写文件 / 真实 GC）。
    ///
    /// ★ 状态由**本实例**持有（不依赖任何全局静态时间戳）；
    ///   调用方应当持有自己的实例（见 <see cref="SystemOptimizer.CreateScheduler"/>），
    ///   以满足"周期状态属于正确的服务实例"。
    /// </summary>
    public sealed class MaintenanceScheduler
    {
        /// <summary>流量保存：首次约 5 秒。</summary>
        public static readonly TimeSpan TrafficSaveInitialDelay = TimeSpan.FromSeconds(5);
        /// <summary>流量保存：此后每 60 秒。</summary>
        public static readonly TimeSpan TrafficSaveInterval = TimeSpan.FromSeconds(60);
        /// <summary>自动 GC：首次约 30 秒。</summary>
        public static readonly TimeSpan GcRequestInitialDelay = TimeSpan.FromSeconds(30);
        /// <summary>自动 GC：此后每 180 秒。</summary>
        public static readonly TimeSpan GcRequestInterval = TimeSpan.FromSeconds(180);
        /// <summary>自动工作集检查：首次约 45 秒。</summary>
        public static readonly TimeSpan WorkingSetInitialDelay = TimeSpan.FromSeconds(45);
        /// <summary>自动工作集检查：此后每 300 秒。</summary>
        public static readonly TimeSpan WorkingSetInterval = TimeSpan.FromSeconds(300);

        private readonly MonotonicPeriodicTrigger _trafficSave;
        private readonly MonotonicPeriodicTrigger _gcRequest;
        private readonly MonotonicPeriodicTrigger _workingSet;

        private readonly Action? _onTrafficSave;
        private readonly Action? _onGcRequest;
        private readonly Action? _onWorkingSetTrim;

        /// <param name="onTrafficSave">流量保存动作（默认无操作）。</param>
        /// <param name="onGcRequest">自动 GC 动作（默认无操作）。</param>
        /// <param name="onWorkingSetTrim">自动工作集检查动作（默认无操作）。</param>
        /// <param name="timeProvider">可注入的单调时钟，默认 <see cref="TimeProvider.System"/>。</param>
        public MaintenanceScheduler(
            Action? onTrafficSave = null,
            Action? onGcRequest = null,
            Action? onWorkingSetTrim = null,
            TimeProvider? timeProvider = null)
        {
            var time = timeProvider ?? TimeProvider.System;
            _trafficSave = new MonotonicPeriodicTrigger(TrafficSaveInitialDelay, TrafficSaveInterval, time);
            _gcRequest = new MonotonicPeriodicTrigger(GcRequestInitialDelay, GcRequestInterval, time);
            _workingSet = new MonotonicPeriodicTrigger(WorkingSetInitialDelay, WorkingSetInterval, time);

            _onTrafficSave = onTrafficSave;
            _onGcRequest = onGcRequest;
            _onWorkingSetTrim = onWorkingSetTrim;
        }

        /// <summary>
        /// 距离下一次流量保存到期还有多久（诊断用；已经到期时为 <see cref="TimeSpan.Zero"/>）。
        /// </summary>
        public TimeSpan NextTrafficSaveDelay => _trafficSave.Remaining;

        /// <summary>
        /// 执行所有已到期的维护任务。
        /// 返回值表示**本次真正到期并被消费**的任务，可用于统计计数。
        /// </summary>
        public MaintenanceRunResult RunDueTasks()
        {
            bool trafficSave = false;
            bool gcRequest = false;
            bool workingSetTrim = false;

            if (_trafficSave.TryConsume())
            {
                trafficSave = true;
                Invoke(_onTrafficSave, "TrafficSave");
            }

            if (_gcRequest.TryConsume())
            {
                gcRequest = true;
                Invoke(_onGcRequest, "GcRequest");
            }

            if (_workingSet.TryConsume())
            {
                workingSetTrim = true;
                Invoke(_onWorkingSetTrim, "WorkingSetTrim");
            }

            return new MaintenanceRunResult(trafficSave, gcRequest, workingSetTrim);
        }

        /// <summary>
        /// 单个维护任务失败不能影响其它任务，也不能形成异常忙循环
        /// （下次仍按正常周期重试）。
        /// </summary>
        private static void Invoke(Action? action, string name)
        {
            if (action == null) return;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                HardwareErrorLog.Write("MaintenanceScheduler." + name, ex);
            }
        }
    }
}
