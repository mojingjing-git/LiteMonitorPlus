using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 硬件采集的低开销统计计数器。
    ///
    /// 设计约束（来自计划 §4.1 / §6.1 与 Task 0 探针需求）：
    ///  - 全部是内存里的自增计数，默认运行**不逐帧写日志**、不做任何 IO；
    ///  - 采集轮数、每设备 Update 次数、慢扫描/保存/GC/裁剪触发次数、采集代次都能读到；
    ///  - 读取接口返回快照副本，调用方不需要加锁也不会被打断。
    /// </summary>
    public sealed class HardwareSamplingStatistics
    {
        private long _collectionRounds;
        private long _skippedRequests;      // 未到期 -> 快速返回
        private long _rejectedRequests;      // 未就绪 / 已释放 -> 拒绝
        private long _sharedRequests;        // 复用在途采集
        private long _failedCollections;     // 采集异常
        private long _forcedCollections;     // 例外采集（初始化 / 重载 / 设备切换触发）
        private long _valueComputations;     // 真正执行了一次取值计算
        private long _slowScanTriggers;
        private long _diskBackgroundScanTriggers;
        private long _trafficSaveTriggers;
        private long _gcRequestTriggers;
        private long _workingSetTrimTriggers;
        private long _generation;
        private double _lastDurationMs;
        private double _totalDurationMs;

        // 设备级计数：Key 形如 "GPU.0" / "Cpu:AMD Ryzen 7 5800X"
        private readonly object _deviceGate = new object();
        private readonly Dictionary<string, long> _deviceUpdates = new Dictionary<string, long>(StringComparer.Ordinal);
        // 分类汇总：Key 形如 "Cpu" / "Gpu" / "Memory" / "Network" / "Storage" / "Motherboard" / "Battery"
        private readonly Dictionary<string, long> _categoryTotals = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>实际发生的真实采集轮数（= 采集代次数量）。</summary>
        public long CollectionRounds => Interlocked.Read(ref _collectionRounds);
        /// <summary>未到期被快速返回的请求次数。</summary>
        public long SkippedRequests => Interlocked.Read(ref _skippedRequests);
        /// <summary>因未就绪或已释放而被拒绝的请求次数。</summary>
        public long RejectedRequests => Interlocked.Read(ref _rejectedRequests);
        /// <summary>并发请求复用在途采集（共享同一个任务）的次数。</summary>
        public long SharedRequests => Interlocked.Read(ref _sharedRequests);
        /// <summary>采集过程中抛出异常的次数。</summary>
        public long FailedCollections => Interlocked.Read(ref _failedCollections);
        /// <summary>
        /// 例外采集次数：不是"正常到期"触发的采集。
        /// 初始化完成、重载完成、设备选择变更这三类会立即触发一次采集，
        /// 它们单独计数，稳态统计（60 秒 ≤ 60 轮）时必须把它们排除在外。
        /// </summary>
        public long ForcedCollections => Interlocked.Read(ref _forcedCollections);
        /// <summary>真正执行过一次取值计算的次数（同代次重复读不会增加）。</summary>
        public long ValueComputations => Interlocked.Read(ref _valueComputations);
        /// <summary>慢扫描（主板/SuperIO 等）触发次数。</summary>
        public long SlowScanTriggers => Interlocked.Read(ref _slowScanTriggers);
        /// <summary>后台磁盘扫描触发次数。</summary>
        public long DiskBackgroundScanTriggers => Interlocked.Read(ref _diskBackgroundScanTriggers);
        /// <summary>流量保存触发次数。</summary>
        public long TrafficSaveTriggers => Interlocked.Read(ref _trafficSaveTriggers);
        /// <summary>自动 GC 请求触发次数。</summary>
        public long GcRequestTriggers => Interlocked.Read(ref _gcRequestTriggers);
        /// <summary>自动工作集裁剪触发次数。</summary>
        public long WorkingSetTrimTriggers => Interlocked.Read(ref _workingSetTrimTriggers);
        /// <summary>当前采集代次编号（每完成一次真实采集 +1）。</summary>
        public long Generation => Interlocked.Read(ref _generation);
        /// <summary>最近一次采集的耗时（毫秒）。</summary>
        public double LastCollectionDurationMs => Volatile.Read(ref _lastDurationMs);
        /// <summary>累计采集耗时（毫秒）。</summary>
        public double TotalCollectionDurationMs => Volatile.Read(ref _totalDurationMs);

        internal void RecordCollectionStarted(int generation)
        {
            Interlocked.Increment(ref _collectionRounds);
            Interlocked.Exchange(ref _generation, generation);
        }

        internal void RecordCollectionFinished(int generation, bool success, double durationMs)
        {
            if (!success) Interlocked.Increment(ref _failedCollections);
            Volatile.Write(ref _lastDurationMs, durationMs);
            lock (_deviceGate)
            {
                _totalDurationMs += durationMs;
            }
        }

        /// <summary>
        /// 记录一次**被取消**的采集。
        ///
        /// 取消既不是成功也不是失败，因此语义上与 <see cref="RecordCollectionFinished"/> 分开：
        ///  - **不**计入 <see cref="FailedCollections"/>：取消是调用方（关闭 / 主动取消）造成的，
        ///    不是采集动作自己失败，把它算成失败会让"采集异常"这个交付指标凭空多出噪声；
        ///  - 但这一轮采集动作**确实已经开始过**（<see cref="RecordCollectionStarted"/> 已经把
        ///    <see cref="CollectionRounds"/> +1），真实消耗掉的时间必须计入，
        ///    否则"累计采集耗时"会漏掉一次真实消耗。
        /// </summary>
        internal void RecordCollectionCanceled(int generation, double durationMs)
        {
            Volatile.Write(ref _lastDurationMs, durationMs);
            lock (_deviceGate)
            {
                _totalDurationMs += durationMs;
            }
        }

        internal void RecordSkippedRequest() => Interlocked.Increment(ref _skippedRequests);
        internal void RecordRejectedRequest() => Interlocked.Increment(ref _rejectedRequests);
        internal void RecordSharedRequest() => Interlocked.Increment(ref _sharedRequests);
        internal void RecordValueComputation() => Interlocked.Increment(ref _valueComputations);

        /// <summary>
        /// 记录一次采集异常。
        /// 协调器自己捕获的异常会自动记到这里；
        /// 采集动作内部自行吞掉的异常（例如 HardwareMonitor.CollectOnce 的 catch）
        /// 需要由那一侧显式调用本方法，否则该计数会漏记。
        /// </summary>
        internal void RecordFailedCollection() => Interlocked.Increment(ref _failedCollections);

        /// <summary>记录一次"例外采集"（初始化 / 重载 / 设备选择变更触发的立即采集）。</summary>
        internal void RecordForcedCollection() => Interlocked.Increment(ref _forcedCollections);

        internal void RecordSlowScanTrigger() => Interlocked.Increment(ref _slowScanTriggers);
        internal void RecordDiskBackgroundScanTrigger() => Interlocked.Increment(ref _diskBackgroundScanTriggers);
        internal void RecordTrafficSaveTrigger() => Interlocked.Increment(ref _trafficSaveTriggers);
        internal void RecordGcRequestTrigger() => Interlocked.Increment(ref _gcRequestTriggers);
        internal void RecordWorkingSetTrimTrigger() => Interlocked.Increment(ref _workingSetTrimTriggers);

        /// <summary>记录一次设备更新（CPU / 每张 GPU / 内存 / 电池 / 网卡 / 磁盘 / 主板）。</summary>
        internal void RecordDeviceUpdate(string deviceKey, string category)
        {
            lock (_deviceGate)
            {
                _deviceUpdates.TryGetValue(deviceKey, out long deviceCount);
                _deviceUpdates[deviceKey] = deviceCount + 1;

                _categoryTotals.TryGetValue(category, out long categoryCount);
                _categoryTotals[category] = categoryCount + 1;
            }
        }

        /// <summary>每设备 / 每卡的 Update 次数快照。</summary>
        public IReadOnlyDictionary<string, long> SnapshotDeviceUpdates()
        {
            lock (_deviceGate)
            {
                return new Dictionary<string, long>(_deviceUpdates, StringComparer.Ordinal);
            }
        }

        /// <summary>按分类汇总的 Update 次数快照（"Cpu" / "Gpu" / "Memory" / ...）。</summary>
        public IReadOnlyDictionary<string, long> SnapshotCategoryTotals()
        {
            lock (_deviceGate)
            {
                return new Dictionary<string, long>(_categoryTotals, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// 硬件采集协调器：唯一���真实采集入口」。
    ///
    /// 职责（计划 §4.1 / §4.3）：
    ///  1. 用**单调时钟**（TimeProvider）判断"是否到期"，墙上时间回拨不影响调度；
    ///  2. 同一时刻最多一个真实采集任务，并发消费者共享进行中的任务，不排队；
    ///  3. 未到期快速返回：不创建任务、不调用 hw.Update()、不清值缓存、不推进采集时间；
    ///  4. 正常任务按采集起始时间安排下一次；超长任务结束后重新安排未来期限，不补跑错过的多轮；
    ///  5. 采样异常按正常周期重试，并保留故障日志，不形成异常忙循环；
    ///  6. 关闭后不再接受新请求。
    ///
    /// 状态全部由实例持有（不���全局静态时间戳），便于用假时钟做单元测试。
    /// </summary>
    public sealed class HardwareSamplingCoordinator : IDisposable
    {
        /// <summary>普通硬件采集的最小周期（毫秒）。UI 刷新更快时也按这个下限采集。</summary>
        public const int MinimumPeriodMs = 1000;

        private readonly object _gate = new object();
        private readonly TimeProvider _time;
        private readonly Func<int> _periodProvider;
        private readonly Func<CancellationToken, Task> _sampler;
        private readonly Action<int>? _onCollectionStarted;
        private readonly Action<int, bool>? _onCollectionCompleted;
        private readonly Action<int>? _onCollectionSettled;
        private readonly Action<string, Exception>? _errorLogger;
        private readonly HardwareSamplingStatistics _statistics;

        private Task<bool>? _running;          // 进行中的采集任务（并发消费者共享它）
        private long _nextDue;                  // 下一次允许采集的截止时间（单调时间戳）
        private bool _hasDeadline;              // 是否已经排定过截止时间
        private bool _forceNextCollection;      // 下一次采集是"例外采集"（初始化/重载/设备切换）
        private bool _currentCollectionForced;  // 当前这轮采集是不是例外采集
        private long _lastCollectionStart;      // 上一次采集的"生效起点"（单调时间戳）
        private int _lastPeriodMs;              // 排定截止时间时使用的周期
        private bool _ready;                    // 是否允许采集（初始化/重载完成）
        private bool _disposed;
        private int _generation;

        public HardwareSamplingStatistics Statistics => _statistics;

        /// <summary>当前采集代次编号。</summary>
        public int Generation => Volatile.Read(ref _generation);

        public bool IsReady { get { lock (_gate) { return _ready; } } }
        public bool IsDisposed { get { lock (_gate) { return _disposed; } } }

        /// <param name="periodProvider">返回当前有效采集周期（毫秒）。每次判定时读取，天然支持周期调整。</param>
        /// <param name="sampler">真正的采集动作。抛异常会被捕获并按正常周期重试。</param>
        /// <param name="timeProvider">可注入的单调时钟，默认 <see cref="TimeProvider.System"/>。</param>
        /// <param name="onCollectionStarted">采集开始回调（参数为新的采集代次）。</param>
        /// <param name="onCollectionCompleted">采集结束回调（参数为代次与是否成功）。</param>
        /// <param name="statistics">共享的统计计数器实例。</param>
        /// <param name="onCollectionSettled">
        /// 采集落定回调（参数为采集代次），在采样动作**之后**调用。
        /// 取值缓存可以用它把"采集中途被读到并写进缓存的旧值"作废掉；
        /// 为空则不调用（保持既有行为）。
        /// </param>
        /// <param name="errorLogger">采集异常的错误出口，默认写硬件错误日志；测试可注入替身，避免污染测试输出目录。</param>
        public HardwareSamplingCoordinator(
            Func<int> periodProvider,
            Func<CancellationToken, Task> sampler,
            TimeProvider? timeProvider = null,
            Action<int>? onCollectionStarted = null,
            Action<int, bool>? onCollectionCompleted = null,
            HardwareSamplingStatistics? statistics = null,
            Action<int>? onCollectionSettled = null,
            Action<string, Exception>? errorLogger = null)
        {
            _periodProvider = periodProvider ?? throw new ArgumentNullException(nameof(periodProvider));
            _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
            _time = timeProvider ?? TimeProvider.System;
            _onCollectionStarted = onCollectionStarted;
            _onCollectionCompleted = onCollectionCompleted;
            _onCollectionSettled = onCollectionSettled;
            _errorLogger = errorLogger ?? ((source, ex) => HardwareErrorLog.Write(source, ex));
            _statistics = statistics ?? new HardwareSamplingStatistics();
        }

        /// <summary>
        /// 标记是否可以采集。
        /// 置 true 表示"现在允许采集"，下一次请求会立即执行一次真实采集
        /// （用于初始化完成、重载完成这类需要立刻重新采样的场景）。
        ///
        /// ★ 这一轮采集会被记为**例外采集**（<see cref="HardwareSamplingStatistics.ForcedCollections"/>），
        ///   稳态统计时必须把它与"正常到期采集"分开。
        /// </summary>
        public void SetReady(bool ready)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _ready = ready;
                if (ready)
                {
                    // 重新就绪 -> 允许立即采样一次（记为例外采集）
                    _hasDeadline = false;
                    _forceNextCollection = true;
                }
            }
        }

        /// <summary>
        /// 标记"需要重新采样"：下一次请求会立即执行一次真实采集。
        /// 只用于初始化 / 重载 / 设备选择变更等明确场景；
        /// 普通 UI / Web 请求不会调用它，因此无法借此绕过周期门限。
        ///
        /// ★ 这一轮采集会被记为**例外采集**（<see cref="HardwareSamplingStatistics.ForcedCollections"/>）。
        /// </summary>
        public void Invalidate()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _hasDeadline = false;
                _forceNextCollection = true;
            }
        }

        /// <summary>
        /// 请求一次采集（异步）。返回 true 表示本次请求触发（或共享到）了一次真实采集。
        /// 未到期 / 未就绪 / 已释放时返回 false，且**不会**创建任何采集任务。
        /// </summary>
        public Task<bool> RequestCollectionAsync(CancellationToken cancellationToken = default)
        {
            Task<bool>? existing = TryBeginCollection(out TaskCompletionSource<bool> starter, out long start);
            if (existing != null) return existing;

            _ = Task.Run(() => ExecuteCollectionAsync(starter, start, cancellationToken));
            return starter.Task;
        }

        /// <summary>
        /// 请求一次采集（同步）。与 <see cref="RequestCollectionAsync"/> 共用同一套门限与并发保护。
        /// 采集在调用线程上就地执行，语义与旧的 UpdateAll() 一致。
        /// </summary>
        public bool RequestCollection()
        {
            Task<bool>? existing = TryBeginCollection(out TaskCompletionSource<bool> starter, out long start);
            if (existing != null) return existing.GetAwaiter().GetResult();

            ExecuteCollectionAsync(starter, start, CancellationToken.None).GetAwaiter().GetResult();
            return starter.Task.GetAwaiter().GetResult();
        }

        /// <summary>等待在途采集结束（带超时）。关闭时用来确保不会再碰已关闭的硬件。</summary>
        public bool WaitForIdle(TimeSpan timeout)
        {
            Task<bool>? running;
            lock (_gate) { running = _running; }
            if (running == null) return true;
            try { return running.Wait(timeout); }
            catch { return false; }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _ready = false;
            }
        }

        // =========================================================
        // 内部实现
        // =========================================================

        /// <summary>
        /// 判定并"占位"一次采集。
        /// 返回 null 表示"本次请求应当启动新采集"（starter/start 已填好）；
        /// 返回非 null 表示已有结论：已经在途的任务，或未到期/未就绪的 false。
        /// </summary>
        private Task<bool>? TryBeginCollection(out TaskCompletionSource<bool> starter, out long start)
        {
            starter = null!;
            start = 0;

            lock (_gate)
            {
                if (_disposed)
                {
                    _statistics.RecordRejectedRequest();
                    return Task.FromResult(false);
                }

                if (!_ready)
                {
                    // 初始化 / 重载尚未完成：不能误判为"可采集"
                    _statistics.RecordRejectedRequest();
                    return Task.FromResult(false);
                }

                if (_running != null)
                {
                    // 已有在途采集：共享它，不排队再采一次
                    _statistics.RecordSharedRequest();
                    return _running;
                }

                long now = _time.GetTimestamp();
                int periodMs = CurrentPeriodMs();

                if (_hasDeadline && !_forceNextCollection)
                {
                    if (periodMs != _lastPeriodMs)
                    {
                        // 周期变了：以最近一次采集的起点重新锚定，用户改刷新率立即生效
                        _nextDue = _lastCollectionStart + PeriodTicks(periodMs);
                        _lastPeriodMs = periodMs;
                    }

                    if (now < _nextDue)
                    {
                        // 未到期：快速返回，不创建任务、不清缓存、不推进采集时间
                        _statistics.RecordSkippedRequest();
                        return Task.FromResult(false);
                    }
                }

                start = now;
                starter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _running = starter.Task;

                if (_forceNextCollection)
                {
                    // 例外采集（初始化 / 重载 / 设备选择变更）：单独计数，
                    // 稳态统计（60 秒 ≤ 60 轮）时必须把它排除在外。
                    _forceNextCollection = false;
                    _currentCollectionForced = true;
                    _statistics.RecordForcedCollection();
                }
                else
                {
                    _currentCollectionForced = false;
                }
                return null;
            }
        }

        private async Task ExecuteCollectionAsync(TaskCompletionSource<bool> starter, long start, CancellationToken cancellationToken)
        {
            bool success = false;
            int generation = 0;
            // ★ 关闭期早退标记：这一轮采集"按要求没有开始"，
            //   既不是成功也不是失败，绝不能被计入 FailedCollections。
            bool skipped = false;
            // ★ 取消标记：采集动作**确实开始过**，但被取消打断。
            //   取消既不是成功也不是失败：不能计入 FailedCollections，
            //   但这一轮真实消耗的耗时必须计入。
            bool canceled = false;

            try
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        // 关闭过程中不再开始新的真实采集
                        skipped = true;
                        return;
                    }
                    generation = ++_generation;
                }

                _statistics.RecordCollectionStarted(generation);
                _onCollectionStarted?.Invoke(generation);

                await _sampler(cancellationToken).ConfigureAwait(false);
                success = true;
            }
            catch (OperationCanceledException)
            {
                // 取消不算失败，按正常周期重试
                // ★ 显式标记"被取消"：这一轮既不是成功也不是失败，
                //   finally 里据此不把 FailedCollections +1（但耗时照记）。
                canceled = true;
            }
            catch (Exception ex)
            {
                // 采样异常：保留日志与旧有效数据，按正常周期重试，不形成忙循环
                _errorLogger?.Invoke("HardwareSamplingCoordinator", ex);
            }
            finally
            {
                long end = _time.GetTimestamp();
                int periodMs = CurrentPeriodMs();
                long periodTicks = PeriodTicks(periodMs);

                lock (_gate)
                {
                    long effectiveStart = start;
                    if (end > start + periodTicks)
                    {
                        // 超长任务：不补跑错过的多轮，改为以结束时间重新安排一个未来期限
                        effectiveStart = end;
                    }

                    // 固定网格：截止时间每次推进恰好一个周期，不会因为请求延迟而累积漂移。
                    // 例外采集不在网格上（它是立即触发的），因此从本次起点重新锚定。
                    long next = (_hasDeadline && !_currentCollectionForced)
                        ? _nextDue + periodTicks
                        : effectiveStart + periodTicks;
                    if (next <= end)
                    {
                        // 已经落后（超长任务 / 长时间没有请求）：跳过错过的周期，只安排下一次
                        next = end + periodTicks;
                    }

                    _nextDue = next;
                    _hasDeadline = true;
                    _lastPeriodMs = periodMs;
                    _lastCollectionStart = effectiveStart;
                    _running = null;
                }

                if (!skipped)
                {
                    // 只有真正跑过的采集才记结果。
                    // ★ 关闭期被跳过的采集**不是失败**：不记 FailedCollections，
                    //   也不写耗时（根本没有采集动作发生）。
                    double durationMs = _time.GetElapsedTime(start, end).TotalMilliseconds;
                    if (canceled)
                    {
                        // ★ 被取消：既不是成功也不是失败。
                        //   采集动作确实跑过 -> 耗时计入；
                        //   绝不能计入 FailedCollections（取消是调用方/关闭过程的副产品）。
                        _statistics.RecordCollectionCanceled(generation, durationMs);
                    }
                    else
                    {
                        _statistics.RecordCollectionFinished(generation, success, durationMs);
                    }
                }

                // ★ 必须先完成 Task，再调用外部回调。
                //   starter.Task 是 RequestCollectionAsync 返回给调用方的唯一完成信号，
                //   UI 在 UIController.Tick 里 await 它；如果回调抛异常就会跳过下面的
                //   TrySetResult，那个 await 永远不返回 -> Tick 的 finally 走不到 ->
                //   _busy 永久为 true -> 界面彻底停止刷新。
                starter.TrySetResult(success);

                // 采集落定回调：把"采集中途被读到并写进缓存的旧值"作废掉（可选）
                try { _onCollectionSettled?.Invoke(generation); }
                catch (Exception ex) { _errorLogger?.Invoke("HardwareSamplingCoordinator.Settled", ex); }

                // 结束回调：异常不能影响已经完成的 Task
                try { _onCollectionCompleted?.Invoke(generation, success); }
                catch (Exception ex) { _errorLogger?.Invoke("HardwareSamplingCoordinator.Completed", ex); }
            }
        }

        private int CurrentPeriodMs()
        {
            int periodMs = _periodProvider();
            if (periodMs <= 0) periodMs = MinimumPeriodMs;
            return periodMs;
        }

        private long PeriodTicks(int periodMs)
        {
            long frequency = _time.TimestampFrequency;
            if (frequency <= 0) frequency = TimeSpan.TicksPerSecond;
            return (long)Math.Round(frequency * (double)periodMs / 1000.0);
        }
    }
}
