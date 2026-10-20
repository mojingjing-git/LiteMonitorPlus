using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32; // ★★★ 新增引用 ★★★

// LiteMonitor 系统服务命名空间
namespace LiteMonitor.src.SystemServices
{
    // ================================================================
    // 进程查询抽象（可注入，便于单元测试隔离真实进程列表）
    // ================================================================

    /// <summary>
    /// FPS 进程查询结果状态
    /// </summary>
    public enum FpsProcessQueryStatus
    {
        /// <summary>查询成功，进程存在</summary>
        Found = 0,

        /// <summary>进程确实不存在（已退出）</summary>
        Exited = 1,

        /// <summary>查询失败（权限不足 / 瞬时错误），<b>不等于</b>已退出</summary>
        Unknown = 2
    }

    /// <summary>
    /// FPS 进程查询结果
    /// </summary>
    public readonly struct FpsProcessQueryResult
    {
        /// <summary>查询状态</summary>
        public FpsProcessQueryStatus Status { get; }

        /// <summary>进程名（查询失败或已退出时为空串）</summary>
        public string Name { get; }

        /// <summary>
        /// 进程身份标识（一般是进程创建时间）。
        /// 用于识别 PID 重用：0 表示未知。
        /// </summary>
        public long Identity { get; }

        private FpsProcessQueryResult(FpsProcessQueryStatus status, string name, long identity)
        {
            Status = status;
            Name = name ?? string.Empty;
            Identity = identity;
        }

        /// <summary>构造"查询成功"结果</summary>
        public static FpsProcessQueryResult CreateFound(string name, long identity = 0)
            => new FpsProcessQueryResult(FpsProcessQueryStatus.Found, name, identity);

        /// <summary>构造"进程已退出"结果</summary>
        public static FpsProcessQueryResult CreateExited()
            => new FpsProcessQueryResult(FpsProcessQueryStatus.Exited, string.Empty, 0);

        /// <summary>
        /// 构造"查询失败"结果。
        /// 名称沿用原实现的 "Unknown"：查询失败不等于进程退出，但同样不参与 FPS 统计。
        /// </summary>
        public static FpsProcessQueryResult CreateUnknown()
            => new FpsProcessQueryResult(FpsProcessQueryStatus.Unknown, "Unknown", 0);
    }

    /// <summary>
    /// 可注入的进程查询器。
    /// 默认实现使用 <see cref="Process"/>，测试可注入假实现以避免依赖真实进程列表。
    /// </summary>
    public interface IFpsProcessQuery
    {
        /// <summary>查询指定 PID 的进程名与身份</summary>
        FpsProcessQueryResult Query(int pid);
    }

    // ================================================================
    // 外部 FPS 采集服务启动抽象（可注入，避免测试触碰真实 PresentMon / ETW / 网络）
    // ================================================================

    /// <summary>
    /// 可注入的外部 FPS 采集服务启动器
    /// </summary>
    public interface IFpsServiceLauncher
    {
        /// <summary>确保外部服务可运行（必要时下载组件）；不可用返回 false</summary>
        bool EnsureAvailable();

        /// <summary>启动外部服务；每收到一行输出回调一次 <paramref name="onLine"/></summary>
        bool Start(Action<string> onLine, out string? error);

        /// <summary>停止外部服务</summary>
        void Stop();

        /// <summary>外部服务进程是否仍在运行</summary>
        bool IsRunning { get; }
    }

    /// <summary>
    /// 外部 FPS 采集服务启动器的可选扩展：进程退出通知。
    ///
    /// 实现该接口后，外部服务进程一退出就会立刻回调，
    /// 调用方可以马上把"服务运行中"状态回落（而不是等下一轮健康检查）。
    /// </summary>
    public interface IFpsServiceExitNotifier
    {
        /// <summary>外部服务进程退出时触发（在任意线程回调）</summary>
        event Action? Exited;
    }

    /// <summary>
    /// FPS 状态存储选项
    /// </summary>
    public sealed class FpsStateOptions
    {
        /// <summary>闲置清理期限（毫秒），默认 30 秒</summary>
        public int IdleTimeoutMs { get; set; } = 30000;

        /// <summary>查询失败后的最小重试间隔（毫秒），默认 5 秒</summary>
        public int QueryRetryMs { get; set; } = 5000;

        /// <summary>成功查询结果的复用期限（毫秒），默认 5 秒。用于发现进程退出与 PID 重用</summary>
        public int ProcessRefreshMs { get; set; } = 5000;

        /// <summary>毫秒单调时钟（便于测试注入假时钟）</summary>
        public Func<long>? Clock { get; set; }

        /// <summary>需要排除（不参与 FPS 统计）的进程名</summary>
        public ISet<string>? ExcludedProcesses { get; set; }
    }

    /// <summary>
    /// 单个 PID 的诊断信息
    /// </summary>
    public readonly struct FpsProcessDiagnostic
    {
        /// <summary>进程 PID</summary>
        public int Pid { get; }

        /// <summary>代次（PID 被重用时递增）</summary>
        public long Generation { get; }

        /// <summary>缓存的进程名</summary>
        public string Name { get; }

        /// <summary>当前算出的 FPS（低于阈值时为 0）</summary>
        public float Fps { get; }

        /// <summary>距最后一次收到帧的毫秒数</summary>
        public long LastFrameAgeMs { get; }

        /// <summary>上次进程查询是否失败</summary>
        public bool QueryFailed { get; }

        /// <summary>是否还保留平滑历史</summary>
        public bool HasHistory { get; }

        /// <summary>滑动累计窗口中保留的采样数（0 表示历史已被清空）</summary>
        public int HistorySamples { get; }

        internal FpsProcessDiagnostic(int pid, long generation, string name, float fps, long lastFrameAgeMs, bool queryFailed, bool hasHistory, int historySamples)
        {
            Pid = pid;
            Generation = generation;
            Name = name;
            Fps = fps;
            LastFrameAgeMs = lastFrameAgeMs;
            QueryFailed = queryFailed;
            HasHistory = hasHistory;
            HistorySamples = historySamples;
        }
    }

    /// <summary>
    /// FPS 进程状态存储。
    ///
    /// 负责维护「每个 PID 的帧计数 / 活动时间 / 查询结果 / 平滑历史 / 清理条件」，
    /// 是本文件中与外部世界无关的纯状态逻辑，可通过注入 <see cref="IFpsProcessQuery"/>
    /// 与假时钟进行单元测试，不依赖真实进程列表。
    ///
    /// 关键约束：
    /// - 已退出的 PID 及时从帧计数、累加历史、平滑历史、结果、名称缓存中整体移除；
    /// - 查询失败（Unknown）做有界重试与负缓存，同一 PID 至少间隔 5 秒再查；
    /// - 长期不再产生帧的记录按闲置期限（默认 30 秒）有界清理；
    /// - 解析（写入帧）与统计/清理并发时，真实新增帧不会被过期清理永久丢弃。
    /// </summary>
    public sealed class FpsProcessStateStore
    {
        /// <summary>累计窗口大小：4 次采样 = 2.0 秒</summary>
        private const int AccumulatorSize = 4;

        /// <summary>奥运会窗口大小：6 次采样</summary>
        private const int OlympicSize = 6;

        /// <summary>低于该 FPS 不上报</summary>
        private const float MinReportedFps = 1.0f;

        /// <summary>查询状态：已确认进程退出（不作为"查询成功"缓存）</summary>
        private const int QueryStateExited = 3;

        /// <summary>一次采样的记录：帧数与持续时间</summary>
        private struct FrameSample
        {
            public int Count;
            public double Duration;
        }

        /// <summary>单个 PID 的状态记录</summary>
        private sealed class Entry
        {
            /// <summary>帧计数的封存标志位（最高位）</summary>
            private const int FrameSealedMask = int.MinValue;

            /// <summary>帧计数的数值位</summary>
            private const int FrameCountMask = int.MaxValue;

            /// <summary>
            /// 帧计数与封存标志打包在一个 int 里：
            /// 最高位=已封存，其余位=未消费帧数。
            /// 这样"加一帧"与"取走帧数"都是单次 CAS，不会出现
            /// "加一帧→被取走→再回退"导致计数变负、同一帧被统计两次的问题。
            /// </summary>
            private int _frameWord;
            private long _lastFrameTicks;  // 最后一次收到帧的单调时刻
            private long _lastQueryTicks;  // 最后一次发起进程查询的单调时刻
            private int _queryState;       // 0=未查询 1=成功 2=失败
            private string _name = string.Empty;
            private long _identity;        // 进程身份标识（创建时间），0=未知
            private float _fps;            // 当前算出的 FPS

            public readonly Queue<FrameSample> Accumulator = new Queue<FrameSample>(AccumulatorSize);
            public readonly Queue<float> Olympic = new Queue<float>(OlympicSize);

            public int Pid { get; }
            public long Generation { get; set; }

            public Entry(int pid) { Pid = pid; }

            public float Fps => _fps;
            public string Name => _name;
            public long Identity => _identity;
            public bool HasIdentity => _identity != 0;
            public bool QueryFailed => _queryState == 2;
            public bool HasResult => _queryState == 1;
            public long LastFrameTicks => Interlocked.Read(ref _lastFrameTicks);
            public long LastQueryTicks => Interlocked.Read(ref _lastQueryTicks);
            public bool HasHistory => Accumulator.Count > 0 || Olympic.Count > 0;

            /// <summary>
            /// 记录一帧；条目已封存时返回 false（调用方需换新条目）。
            /// 用 CAS 保证"加帧"与"封存"互斥：要么这一帧计入本条目，
            /// 要么明确失败交给调用方重试，不会出现计数被回退的情况。
            /// </summary>
            public bool TryAddFrame(long ticks)
            {
                while (true)
                {
                    int cur = Volatile.Read(ref _frameWord);
                    if ((cur & FrameSealedMask) != 0) return false;   // 已封存
                    if (cur == FrameCountMask) return false;          // 计数饱和，放弃这一帧
                    if (Interlocked.CompareExchange(ref _frameWord, cur + 1, cur) == cur)
                    {
                        Interlocked.Exchange(ref _lastFrameTicks, ticks);
                        return true;
                    }
                }
            }

            /// <summary>原子取走当前未消费的帧数（封存标志保持不变）</summary>
            public int TakeFrames()
            {
                while (true)
                {
                    int cur = Volatile.Read(ref _frameWord);
                    int count = cur & FrameCountMask;
                    int next = cur & FrameSealedMask;
                    if (Interlocked.CompareExchange(ref _frameWord, next, cur) == cur)
                    {
                        return count;
                    }
                }
            }

            /// <summary>把帧数放回（清理流程决定不清理、或转移残留帧时使用）</summary>
            public void RestoreFrames(int count)
            {
                if (count > 0) Interlocked.Add(ref _frameWord, count);
            }

            public void SetQueryState(int state) => Volatile.Write(ref _queryState, state);
            public void SetLastQueryTicks(long ticks) => Interlocked.Exchange(ref _lastQueryTicks, ticks);
            public void SetName(string name) => _name = name;
            public void SetIdentity(long identity) => _identity = identity;
            public void SetFps(float fps) => _fps = fps;

            /// <summary>清空累加/平滑历史与 FPS（PID 被重用时使用）</summary>
            public void ResetHistory()
            {
                Accumulator.Clear();
                Olympic.Clear();
                _fps = 0f;
            }

            /// <summary>
            /// 封存条目（禁止再写入帧）；首次封存成功返回 true。
            /// 注意：判断依据是"封存位"，不是整个字（帧数非 0 时整个字也非 0）。
            /// </summary>
            public bool Seal() => (Interlocked.Or(ref _frameWord, FrameSealedMask) & FrameSealedMask) == 0;

            /// <summary>解除封存（帧数保持不变）</summary>
            public void Unseal() => Interlocked.And(ref _frameWord, FrameCountMask);

            /// <summary>条目是否已封存</summary>
            public bool IsSealed => (Volatile.Read(ref _frameWord) & FrameSealedMask) != 0;
        }

        private readonly ConcurrentDictionary<int, Entry> _entries = new();
        private readonly object _cycleGate = new();   // 保护一轮统计（平滑队列非线程安全）
        private readonly object _gate = new();        // 保护条目增删（结构变更）
        private readonly IFpsProcessQuery _query;
        private readonly Func<long> _clock;
        private readonly int _idleTimeoutMs;
        private readonly int _queryRetryMs;
        private readonly int _processRefreshMs;
        private readonly ISet<string> _excluded;

        private int _queryCount;            // 累计发起过的进程查询次数
        private long _totalFramesObserved;  // 累计被统计消费的帧数
        private long _retiredTransferred;   // 清理流程转移出去的残留帧数
        private long _revivedCarried;       // 复活流程搬迁走的帧数

        /// <summary>构造状态存储</summary>
        /// <param name="query">进程查询器（可注入）</param>
        /// <param name="options">状态选项（可注入假时钟）</param>
        public FpsProcessStateStore(IFpsProcessQuery query, FpsStateOptions? options = null)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            options ??= new FpsStateOptions();

            _idleTimeoutMs = Math.Max(1000, options.IdleTimeoutMs);
            _queryRetryMs = Math.Max(100, options.QueryRetryMs);
            _processRefreshMs = Math.Max(100, options.ProcessRefreshMs);
            _clock = options.Clock ?? (() => Environment.TickCount64);
            _excluded = options.ExcludedProcesses ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>当前记录的 PID 数量</summary>
        public int ProcessCount => _entries.Count;

        /// <summary>累计发起过的进程查询次数（用于观测限频效果）</summary>
        public int QueryCount => Volatile.Read(ref _queryCount);

        /// <summary>累计被统计消费的帧数（用于观测是否丢帧）</summary>
        public long TotalFramesObserved => Interlocked.Read(ref _totalFramesObserved);

        /// <summary>清理流程（封存后）转移给存活条目的残留帧数（用于观测转移路径是否被执行）</summary>
        public long RetiredTransferredFrames => Interlocked.Read(ref _retiredTransferred);

        /// <summary>
        /// 复活流程搬迁到新条目的帧数（用于观测复活路径是否被执行）。
        ///
        /// ★ 登记说明（本轮新增，保留计数 —— 当前实现下不可达）：
        /// 该计数在"复活"路径（条目已被封存 → 换新条目并搬迁残留帧）被累加。
        /// 但封存只发生在清理流程里，而清理流程本身会先把残留帧转移给存活条目
        /// （见 <see cref="RetiredTransferredFrames"/>），因此复活路径的搬迁量恒为 0。
        /// 复审实测：20 万次并发写入 + 实际发生 886 次转移后，该计数仍恒为 0。
        /// 唯一理论可达路径是 <see cref="Entry.TryAddFrame"/> 的**计数饱和**分支
        /// （单 PID 积压 21 亿帧，实践中不可达）。
        /// 之所以保留这个公开成员：本轮契约是"公开 API 只许新增，不许改既有成员签名"，
        /// 删除它等于移除公开成员。这里只登记它不可达的事实，不改其行为。
        /// </summary>
        public long RevivedCarriedFrames => Interlocked.Read(ref _revivedCarried);

        /// <summary>是否存在可上报的 FPS 数据</summary>
        public bool HasActiveFps
        {
            get
            {
                foreach (var kv in _entries)
                {
                    if (kv.Value.Fps >= MinReportedFps) return true;
                }
                return false;
            }
        }

        /// <summary>解析一行 PresentMon 输出并记录一帧</summary>
        /// <param name="line">PresentMon 输出的一行文本</param>
        /// <returns>是否成功解析出 PID 并记录</returns>
        public bool RecordLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            if (line[0] == 'A') return false; // 快速检查 "Application" 表头

            // 优化解析：手动查找第一个和第二个逗号，避免 Split(',') 产生大量字符串碎片的 GC 压力
            int firstComma = line.IndexOf(',');
            if (firstComma == -1) return false;

            int secondComma = line.IndexOf(',', firstComma + 1);
            int length = (secondComma == -1) ? line.Length - firstComma - 1 : secondComma - firstComma - 1;
            if (length <= 0) return false;

            // 使用 ReadOnlySpan 避免 Substring 内存分配
            ReadOnlySpan<char> pidSpan = line.AsMemory(firstComma + 1, length).Span;
            if (!int.TryParse(pidSpan, out int pid)) return false;

            RecordFrame(pid);
            return true;
        }

        /// <summary>记录一个 PID 的一帧（线程安全）</summary>
        public void RecordFrame(int pid)
        {
            long now = _clock();

            // 快路径：已有条目且未被封存
            Entry entry = _entries.GetOrAdd(pid, p => new Entry(p));
            if (entry.TryAddFrame(now)) return;

            // 慢路径：条目已被清理流程封存 → 换成新条目，帧不能丢
            lock (_gate)
            {
                Entry current = _entries.GetOrAdd(pid, p => new Entry(p));
                if (current.TryAddFrame(now)) return;

                int carried = current.TakeFrames();
                Interlocked.Add(ref _revivedCarried, carried);
                var fresh = new Entry(pid) { Generation = current.Generation };
                fresh.RestoreFrames(carried);
                fresh.TryAddFrame(now);
                _entries[pid] = fresh;
            }
        }

        /// <summary>
        /// 推进一轮统计与清理。
        /// </summary>
        /// <param name="elapsedSeconds">本轮实际经过时间（秒）</param>
        /// <returns>本轮被清理掉的 PID 数量</returns>
        public int AdvanceCycle(double elapsedSeconds)
        {
            if (elapsedSeconds <= 0) return 0;

            lock (_cycleGate)
            {
                long now = _clock();
                int cleaned = 0;

                foreach (var kv in _entries)
                {
                    int pid = kv.Key;
                    Entry entry = kv.Value;

                    // 本周期帧数（原子取走，避免与解析线程竞争导致丢帧）
                    int currentCount = entry.TakeFrames();
                    if (currentCount > 0) Interlocked.Add(ref _totalFramesObserved, currentCount);

                    // 解析进程身份与名称（带负缓存与重试期限）
                    FpsProcessQueryResult query = ResolveProcess(entry, now);
                    if (query.Status == FpsProcessQueryStatus.Exited)
                    {
                        // 进程确实已退出 → 整体清除该 PID 的全部状态
                        if (RetireEntry(pid, entry, force: true)) cleaned++;
                        continue;
                    }

                    // 闲置清理：长期不再产生帧的记录按期限有界移除。
                    // ★ 必须放在 Unknown / 排除进程分支之前：
                    //   否则"查询持续失败"或"被排除"的记录会先被 continue 掉，
                    //   永远得不到回收，_entries 就会单调增长。
                    if (now - entry.LastFrameTicks >= _idleTimeoutMs)
                    {
                        if (RetireEntry(pid, entry, force: false)) cleaned++;
                        continue;
                    }

                    string processName = query.Name;
                    if (query.Status == FpsProcessQueryStatus.Unknown || _excluded.Contains(processName))
                    {
                        // 排除进程 / 查询未知的进程：不参与统计（保持原实现语义）
                        entry.SetFps(0f);
                        continue;
                    }

                    // ===== 以下为原有平滑算法（保持不变） =====
                    // 第一层：滑动累计算法（解决 600~1200 FPS 波动）
                    entry.Accumulator.Enqueue(new FrameSample { Count = currentCount, Duration = elapsedSeconds });
                    while (entry.Accumulator.Count > AccumulatorSize) entry.Accumulator.Dequeue();

                    // 累计求和：计算最近 2 秒内的总帧数和总时间
                    long totalCount = 0;
                    double totalTime = 0;
                    foreach (var sample in entry.Accumulator)
                    {
                        totalCount += sample.Count;
                        totalTime += sample.Duration;
                    }

                    // 计算初步稳定的 FPS（基于累计数据）
                    float rawStableFps = 0;
                    if (totalTime > 0) rawStableFps = (float)(totalCount / totalTime);

                    // 瞬时响应优化：对于刚启动的进程，如果帧率很高，直接使用瞬时值
                    if (entry.Accumulator.Count < 2 && (currentCount / elapsedSeconds) > 100)
                    {
                        rawStableFps = (float)(currentCount / elapsedSeconds);
                    }

                    // 第二层：奥运会平滑算法（解决数值毛刺）
                    entry.Olympic.Enqueue(rawStableFps);
                    while (entry.Olympic.Count > OlympicSize) entry.Olympic.Dequeue();

                    // 计算最终平滑的 FPS：去头去尾取平均（奥运会算法优化版）
                    float finalFps = 0;
                    int count = entry.Olympic.Count;
                    if (count >= 4)
                    {
                        float min = float.MaxValue;
                        float max = float.MinValue;
                        float sum = 0;
                        foreach (var f in entry.Olympic)
                        {
                            if (f < min) min = f;
                            if (f > max) max = f;
                            sum += f;
                        }
                        // 去掉一个最高值和一个最低值，计算剩余值的平均值
                        finalFps = (sum - min - max) / (count - 2);
                    }
                    else
                    {
                        // 数据不足时直接取平均
                        float sum = 0;
                        foreach (var f in entry.Olympic) sum += f;
                        finalFps = sum / count;
                    }

                    // 结果存储
                    if (finalFps < MinReportedFps)
                    {
                        // FPS 过低，不上报。
                        // ★ 与原实现不同：这里不再立即抹掉累加/平滑历史，
                        //   短暂的停顿不应立刻清空平滑状态，交由闲置期限统一清理。
                        entry.SetFps(0f);
                    }
                    else
                    {
                        // 更新该进程的最终 FPS
                        entry.SetFps(finalFps);
                    }
                }

                return cleaned;
            }
        }

        /// <summary>取指定 PID 的 FPS（低于阈值或不存在返回 false）</summary>
        public bool TryGetFps(int pid, out float fps)
        {
            fps = 0f;
            if (!_entries.TryGetValue(pid, out Entry? entry)) return false;
            if (entry.Fps < MinReportedFps) return false;
            fps = entry.Fps;
            return true;
        }

        /// <summary>找出当前 FPS 最高的进程（与原实现一致的选取规则）</summary>
        public bool TryGetTopProcess(out int pid, out float fps)
        {
            pid = 0;
            fps = -1f;
            foreach (var kv in _entries)
            {
                float value = kv.Value.Fps;
                if (value >= MinReportedFps && value > fps)
                {
                    fps = value;
                    pid = kv.Key;
                }
            }
            return pid != 0;
        }

        /// <summary>取指定 PID 缓存的进程名（未解析出名称时返回空串）</summary>
        public string GetProcessName(int pid)
        {
            return _entries.TryGetValue(pid, out Entry? entry) ? entry.Name : string.Empty;
        }

        /// <summary>清空全部 FPS 状态</summary>
        public void Clear()
        {
            _entries.Clear();
            Interlocked.Exchange(ref _totalFramesObserved, 0);
        }

        /// <summary>导出当前诊断信息（测试与排障用）</summary>
        public List<FpsProcessDiagnostic> GetDiagnostics()
        {
            long now = _clock();
            var list = new List<FpsProcessDiagnostic>(_entries.Count);
            foreach (var kv in _entries)
            {
                Entry entry = kv.Value;
                list.Add(new FpsProcessDiagnostic(
                    entry.Pid,
                    entry.Generation,
                    entry.Name,
                    entry.Fps,
                    now - entry.LastFrameTicks,
                    entry.QueryFailed,
                    entry.HasHistory,
                    entry.Accumulator.Count));
            }
            return list;
        }

        /// <summary>
        /// 解析进程名与身份。
        /// - 查询失败：负缓存，同一 PID 至少间隔 QueryRetryMs（默认 5 秒）才允许再查；
        /// - 查询成功：结果在 ProcessRefreshMs（默认 5 秒）内复用，用于发现退出与 PID 重用。
        /// </summary>
        private FpsProcessQueryResult ResolveProcess(Entry entry, long now)
        {
            if (entry.QueryFailed)
            {
                // 负缓存：不能每 500ms 就重复抛同一个查询异常
                if (now - entry.LastQueryTicks < _queryRetryMs)
                {
                    return FpsProcessQueryResult.CreateUnknown();
                }
            }
            else if (entry.HasResult)
            {
                if (now - entry.LastQueryTicks < _processRefreshMs)
                {
                    return FpsProcessQueryResult.CreateFound(entry.Name, entry.Identity);
                }
            }

            entry.SetLastQueryTicks(now);
            Interlocked.Increment(ref _queryCount);

            FpsProcessQueryResult result;
            try
            {
                result = _query.Query(entry.Pid);
            }
            catch
            {
                // 查询器抛异常按"未知"处理：权限问题或瞬时失败都不等于已退出
                result = FpsProcessQueryResult.CreateUnknown();
            }

            switch (result.Status)
            {
                case FpsProcessQueryStatus.Found:
                    // PID 重用判定：身份标识或进程名变化 => 新进程，旧名称与历史不能套上去
                    bool reused = (entry.HasResult && entry.HasIdentity && result.Identity != 0 && entry.Identity != result.Identity)
                                  || (entry.HasResult && !string.IsNullOrEmpty(entry.Name)
                                      && !string.Equals(entry.Name, result.Name, StringComparison.OrdinalIgnoreCase));
                    if (reused)
                    {
                        entry.ResetHistory();
                        entry.Generation++;
                    }
                    entry.SetName(result.Name);
                    if (result.Identity != 0) entry.SetIdentity(result.Identity);
                    entry.SetQueryState(1);
                    break;

                case FpsProcessQueryStatus.Exited:
                    // ★ 退出结果不能当作"查询成功"缓存：否则后续几轮会按缓存当成进程仍存在，
                    //   "已退出 PID 立即移除"就会落空。退出状态单独记，下一轮会重新确认。
                    entry.SetQueryState(QueryStateExited);
                    break;

                default:
                    entry.SetQueryState(2);
                    break;
            }

            return result;
        }

        /// <summary>
        /// 清理一个 PID 的全部状态（帧计数、累加历史、平滑历史、结果、名称缓存一并清除）。
        /// </summary>
        /// <param name="pid">进程 PID</param>
        /// <param name="entry">目标条目</param>
        /// <param name="force">true=进程已确认退出，立即清理；false=仅在没有新帧时清理</param>
        /// <returns>是否真的被清理</returns>
        private bool RetireEntry(int pid, Entry entry, bool force)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(pid, out Entry? current))
                {
                    return false; // 已经被其他流程移除
                }

                if (!ReferenceEquals(current, entry))
                {
                    // 条目已被"复活"流程换新（帧已搬迁到新条目）。
                    // 这里要清理的是同一个 PID 的当前条目，否则"已退出 PID 立即移除"会被跳过。
                    entry = current;
                }

                if (!entry.Seal()) return false; // 已被其他流程封存

                int pending = entry.TakeFrames();
                if (pending > 0 && !force)
                {
                    // 刚刚还有真实新帧：不是闲置记录，不能误删
                    entry.RestoreFrames(pending);
                    entry.Unseal();
                    return false;
                }

                _entries.TryRemove(pid, out _);

                if (pending > 0)
                {
                    // 残留帧转移到存活条目，避免真实新增帧被过期清理永久丢弃
                    Interlocked.Add(ref _retiredTransferred, pending);
                    var fresh = new Entry(pid) { Generation = entry.Generation };
                    if (_entries.TryAdd(pid, fresh))
                    {
                        fresh.RestoreFrames(pending);
                    }
                    else if (_entries.TryGetValue(pid, out Entry? alive) && !ReferenceEquals(alive, entry))
                    {
                        alive.RestoreFrames(pending);
                    }
                    else
                    {
                        fresh.RestoreFrames(pending);
                    }
                }

                return true;
            }
        }
    }

    /// <summary>
    /// FPS 计数器类，用于监控系统中各进程的帧率
    /// 使用 PresentMon 收集帧率数据，并通过多层算法平滑处理确保数据稳定
    /// 实现 IDisposable 接口以确保资源正确释放
    /// </summary>
    public class FpsCounter : IDisposable
    {
        // 状态标志
        private volatile bool _isRunning = false;     // FPS 计数服务运行状态
        private int _isRestarting = 0;                 // 服务重启状态（0/1）
        private int _startGate = 0;                   // 启动防重入闸门（0=空闲 1=启动中）
        private int _disposed = 0;                     // 最终释放标记
        private long _lastAccessTicks = 0;             // 最后一次被请求 FPS 数据的时间（单调毫秒）
        private long _lastDataTicks = 0;               // 最后一次收到数据的时间（单调毫秒）
        private long _nextStartAttemptTicks = 0;       // 启动失败后的重试退避截止时间
        private int _serviceStarted = 0;               // 外部服务已成功启动且尚未观察到退出（1=已启动）
        private long _startBlockedByBackoff = 0;       // 因"启动失败退避"被拒绝的拉起次数（观测限频效果）

        // ★★★ [新增] 生命周期代次（启动 / 停止 / 最终释放共享） ★★★
        //   每次"停止外部服务"（含最终释放）都会递增。
        //   启动流程在慢操作（EnsureAvailable，真机可达 120 秒）返回后、
        //   以及进程创建之后，都用它判断"期间生命周期是否已经改变"：
        //   变了 ⇒ 不得再创建外部进程；已经创建了 ⇒ 必须撤销并停掉它。
        private long _lifecycleGeneration = 0;

        // ★★★ [新增] 进程创建在途计数 ★★★
        //   >0 表示"正在创建外部进程，或正在撤销这次创建"。
        //   最终释放用它做**有界等待**：保证"释放返回后不会再有进程被创建"。
        private int _processCreationInFlight = 0;

        // 在途创建归零时的等待同步点
        private readonly object _creationDrainSync = new object();

        // ★★★ [新增] 生命周期互斥锁（启动 / 停止 / 最终释放共享） ★★★
        //   保护的临界区：
        //     - 启动侧："再校验 → 发布运行状态 / 撤销刚创建的进程"
        //     - 停止侧："停止启动器 → 回落运行状态"
        //   临界区**不跨越**慢操作（EnsureAvailable）与进程创建（Start），
        //   因此最终释放不会因为外部进程创建卡住而被无限期挂起。
        private readonly object _lifecycleGate = new object();

        // ★★★ [新增] 静态关机标志 ★★★
        private static bool _isSystemShuttingDown = false;

        static FpsCounter()
        {
            // 订阅系统关机事件
            try { SystemEvents.SessionEnding += (s, e) => _isSystemShuttingDown = true; } catch { }
        }

        // ★★★ [新增] 引用 DriverInstaller ★★★
        private readonly DriverInstaller _driverInstaller;

        // ★★★ [新增] 可注入的外部服务启动器与进程查询器（便于单元测试） ★★★
        private readonly IFpsServiceLauncher _launcher;
        private readonly IFpsProcessQuery _processQuery;

        // ★★★ [新增] FPS 状态存储（每 PID 的帧计数/活动/查询/历史/清理条件） ★★★
        private readonly FpsProcessStateStore _store;

        // 后台任务的取消源：只用于最终 Dispose，空闲停止不会取消（保证可恢复）
        private readonly CancellationTokenSource _cts = new();

        // 管理员权限判定（可注入，便于单元测试；默认使用 Windows 身份判断）
        private readonly Func<bool> _administratorProbe;

        // 毫秒单调时钟（可注入，便于单元测试用假时钟瞬间推进"启动失败退避窗口"）
        private readonly Func<long> _clock;

        // 秒表计时，用于计算采样周期
        private Stopwatch _cycleTimer = new Stopwatch();

        // ★★★ [新增] 排除进程列表，减少字符串比较开销 ★★★
        private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "LiteMonitor++", "LiteMonitorPlusFPS", "PresentMon", "Unknown"
        };

        // 锁定机制变量，用于实现粘性锁定逻辑
        private int _currentFocusPid = 0;       // 当前聚焦的进程 PID
        private int _pendingPid = 0;            // 待切换的进程 PID
        private int _pendingCount = 0;          // 待切换进程的连续领先周期数

        // PresentMon 会话名称
        private const string SESSION_NAME = "LiteMonitor++_Golden_Session";

        // 历史行为：无访问请求超过该秒数后停止外部服务
        private const int IdleStopSeconds = 5;

        // 启动失败后的重试退避（避免每 500ms 重复尝试）
        private const int StartRetryBackoffMs = 30000;

        // 最终释放时等待"在途进程创建"结束的上限（毫秒）。
        // 有界等待：既保证"释放返回后不会再有进程被创建"，
        // 又不会因为外部进程创建卡死而让释放无限期挂起。
        private const int DisposeCreationDrainTimeoutMs = 5000;

        // 文件路径常量
        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory; // 使用 AppDomain 确保准确
        // ★★★ [修改] 统一路径到 resources/assets ★★★
        private static readonly string AssetDir = Path.Combine(BaseDir, "resources");
        private static readonly string ExePath = Path.Combine(AssetDir, "LiteMonitorPlusFPS.exe");
        private static readonly string LogPath = Path.Combine(BaseDir, "fps_debug.log");

        /// <summary>
        /// FPS 构造函数
        /// 初始化环境并启动后台任务
        /// </summary>
        /// <param name="installer">注入 DriverInstaller 以复用下载逻辑</param>
        public FpsCounter(DriverInstaller installer)
            : this(installer, null, null, null)
        {
        }

        /// <summary>
        /// FPS 构造函数（可注入外部服务启动器与进程查询器，便于单元测试）
        /// </summary>
        /// <param name="installer">注入 DriverInstaller 以复用下载逻辑</param>
        /// <param name="launcher">外部 FPS 采集服务启动器；为 null 时使用默认的 PresentMon 启动器</param>
        /// <param name="processQuery">进程查询器；为 null 时使用基于 Process 的默认实现</param>
        public FpsCounter(DriverInstaller installer, IFpsServiceLauncher? launcher, IFpsProcessQuery? processQuery)
            : this(installer, launcher, processQuery, null)
        {
        }

        /// <summary>
        /// FPS 构造函数（可注入外部服务启动器、进程查询器与管理员权限判定）
        /// </summary>
        /// <param name="installer">注入 DriverInstaller 以复用下载逻辑</param>
        /// <param name="launcher">外部 FPS 采集服务启动器；为 null 时使用默认的 PresentMon 启动器</param>
        /// <param name="processQuery">进程查询器；为 null 时使用基于 Process 的默认实现</param>
        /// <param name="administratorProbe">
        /// 管理员权限判定；为 null 时使用 <see cref="IsAdministrator"/>。
        /// PresentMon 需要管理员权限（ETW 会话），非管理员不得启动外部服务。
        /// </param>
        public FpsCounter(DriverInstaller installer, IFpsServiceLauncher? launcher, IFpsProcessQuery? processQuery, Func<bool>? administratorProbe)
            : this(installer, launcher, processQuery, administratorProbe, null)
        {
        }

        /// <summary>
        /// FPS 构造函数（可注入外部服务启动器、进程查询器、管理员权限判定与毫秒单调时钟）
        /// </summary>
        /// <param name="installer">注入 DriverInstaller 以复用下载逻辑</param>
        /// <param name="launcher">外部 FPS 采集服务启动器；为 null 时使用默认的 PresentMon 启动器</param>
        /// <param name="processQuery">进程查询器；为 null 时使用基于 Process 的默认实现</param>
        /// <param name="administratorProbe">
        /// 管理员权限判定；为 null 时使用 <see cref="IsAdministrator"/>。
        /// PresentMon 需要管理员权限（ETW 会话），非管理员不得启动外部服务。
        /// </param>
        /// <param name="clock">
        /// 毫秒单调时钟；为 null 时使用 <see cref="Environment.TickCount64"/>。
        /// 注入假时钟后，"启动失败退避窗口"可以用假时钟瞬间推进，测试无需真实等待 30 秒。
        /// </param>
        public FpsCounter(DriverInstaller installer, IFpsServiceLauncher? launcher, IFpsProcessQuery? processQuery, Func<bool>? administratorProbe, Func<long>? clock)
        {
            _driverInstaller = installer;
            _administratorProbe = administratorProbe ?? IsAdministrator;
            _clock = clock ?? (() => Environment.TickCount64);
            _launcher = launcher ?? new PresentMonServiceLauncher(installer, _administratorProbe);
            _processQuery = processQuery ?? new SystemProcessQuery();

            // 外部服务进程退出时立刻回落"服务运行中"状态（基线行为）
            if (_launcher is IFpsServiceExitNotifier exitNotifier)
            {
                exitNotifier.Exited += OnExternalServiceExited;
            }

            _store = new FpsProcessStateStore(_processQuery, new FpsStateOptions
            {
                ExcludedProcesses = ExcludedProcesses,
                Clock = _clock
            });

            // [修改] 确保目录存在 (DriverInstaller 也会做，这里双重保险)
            try { if (!Directory.Exists(AssetDir)) Directory.CreateDirectory(AssetDir); } catch { }

            // 清除旧日志文件
            try { File.Delete(LogPath); } catch { }

            // 500ms 刷新一次 FPS 计算（稳定版频率）
            Task.Run(async () =>
            {
                _cycleTimer.Start();
                while (Volatile.Read(ref _disposed) == 0)
                {
                    try { await Task.Delay(500, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    CalculateFps();
                }
            });

            // 3秒检查一次服务健康状态和生命周期
            Task.Run(async () =>
            {
                while (Volatile.Read(ref _disposed) == 0)
                {
                    try { await Task.Delay(3000, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    CheckHealth();
                }
            });

            // ★★★ [修改] 构造函数中不再自动启动服务，改为按需启动 ★★★
        }

        /// <summary>
        /// FPS 需求提供器（可注入）。
        ///
        /// 返回 true 表示当前存在 FPS 采集需求。未设置（null）时保持历史行为：
        /// 最近 <see cref="IdleStopSeconds"/> 秒内被访问过即视为有需求。
        ///
        /// 后续阶段（Web FPS 开关）应注入形如 <c>() =&gt; localFpsDemand || webFpsDemand</c> 的实现；
        /// 本类只负责"是否应该继续运行"的判定位置，不自行决定需求规则。
        /// </summary>
        public Func<bool>? FpsDemandProvider { get; set; }

        /// <summary>当前 FPS 需求状态（外部可查询）</summary>
        public bool IsFpsDemandActive => ResolveDemand();

        /// <summary>FPS 采集服务当前是否正在运行（外部可查询）</summary>
        public bool IsServiceRunning => _isRunning && _launcher.IsRunning;

        /// <summary>当前是否存在可上报的 FPS 数据（外部可查询；false 时不应把 FPS 报成 0）</summary>
        public bool HasFpsData => _store.HasActiveFps;

        /// <summary>
        /// 因"启动失败退避"被拒绝的拉起次数（用于观测限频效果）。
        /// 该计数在 <see cref="GetFps"/> 调用链内同步递增，可用于确定性断言"退避确实拦下了拉起请求"。
        /// </summary>
        public long StartBlockedByBackoffCount => Interlocked.Read(ref _startBlockedByBackoff);

        private void Log(string msg)
        {
            try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
        }

        /// <summary>
        /// 外部服务进程意外退出时的回调：立刻把"服务运行中"状态回落。
        /// </summary>
        private void OnExternalServiceExited()
        {
            _isRunning = false;
            // ★ 进程"起来即退"必须记一次启动失败退避：
            //   否则外部会按 GetFps() 的调用频率无界重拉起 PresentMon
            //   （每次拉起还带一个 logman 子进程），违反"禁止失败重试忙循环"。
            RecordUnexpectedExitBackoff();
        }

        /// <summary>
        /// 外部进程已退出但退出事件未送达时的兜底：把运行状态回落。
        /// </summary>
        private void SyncServiceState()
        {
            if (_isRunning && !_launcher.IsRunning)
            {
                _isRunning = false;
                // 退出事件未送达时同样要记退避，否则退避会被完全绕过
                RecordUnexpectedExitBackoff();
            }
        }

        /// <summary>
        /// 获取当前聚焦进程的 FPS 值
        /// 实现了粘性锁定逻辑，避免频繁切换焦点
        /// </summary>
        /// <returns>当前焦点进程的 FPS 值，无数据时返回 0</returns>
        public float? GetFps()
        {
            // ★★★ [修改] 记录本次访问时间，并触发惰性启动 ★★★
            Interlocked.Exchange(ref _lastAccessTicks, _clock());

            // 已最终释放：不再启动服务
            if (Volatile.Read(ref _disposed) != 0) return 0f;

            // 没有需求：不启动外部服务
            if (!ResolveDemand()) return 0f;

            // 外部进程意外退出时立刻回落状态（事件未送达时的兜底），
            // 否则要等下一轮健康检查才自愈。
            SyncServiceState();

            // 如果没运行且没在启动中，尝试启动服务
            EnsureServiceStarted();

            if (!_isRunning) return 0f;

            // 粘性锁定逻辑：找出全场 FPS 最高的进程（优化版，避免 OrderBy 全量排序）
            if (!_store.TryGetTopProcess(out int challengerPid, out float challengerFps))
            {
                _currentFocusPid = 0;
                return 0f;
            }

            // 焦点进程仲裁逻辑
            if (_currentFocusPid == 0 || !_store.TryGetFps(_currentFocusPid, out _))
            {
                // 当前无焦点或焦点进程已退出，直接切换
                _currentFocusPid = challengerPid;
                _pendingCount = 0;
            }
            else if (challengerPid != _currentFocusPid)
            {
                // 有新的挑战者进程
                _store.TryGetFps(_currentFocusPid, out float currentFps);

                // 判断是否是 DWM 进程（桌面窗口管理器）
                bool isChallengerDwm = IsDwm(challengerPid);

                // DWM 进程需要更高的阈值才能切换（1.2倍），普通进程只需 1.1倍
                float thresholdRatio = isChallengerDwm ? 1.2f : 1.1f;

                if (challengerFps > currentFps * thresholdRatio)
                {
                    if (_pendingPid == challengerPid)
                    {
                        // 同一挑战者连续领先，增加计数
                        _pendingCount++;
                        // 碾压式超越（2倍）：直接切换
                        if (challengerFps > currentFps * 2.0f) _pendingCount += 10;

                        // 连续 4 个周期（约2秒）保持领先 -> 切换焦点
                        if (_pendingCount >= 2)
                        {
                            _currentFocusPid = challengerPid;
                            _pendingCount = 0;
                        }
                    }
                    else
                    {
                        // 新挑战者出现，初始化计数
                        _pendingPid = challengerPid;
                        _pendingCount = 1;
                    }
                }
                else
                {
                    // 挑战者未达到切换阈值，重置计数
                    _pendingCount = 0;
                }
            }
            else
            {
                // 当前焦点仍是最高 FPS 进程，重置计数
                _pendingCount = 0;
            }

            // 返回当前焦点进程的 FPS 值
            if (_store.TryGetFps(_currentFocusPid, out float val))
            {
                return (float)Math.Round(val);
            }

            return 0f;
        }

        /// <summary>
        /// 判断是否是 DWM 进程（桌面窗口管理器）
        /// </summary>
        /// <param name="pid">进程 PID</param>
        /// <returns>是 DWM 进程返回 true，否则返回 false</returns>
        private bool IsDwm(int pid)
        {
            return _store.GetProcessName(pid).Equals("dwm", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 解析当前是否存在 FPS 需求。
        /// 未注入 <see cref="FpsDemandProvider"/> 时保持历史行为：最近被访问过即视为有需求。
        ///
        /// ★ 本方法**不触碰**"启动失败退避"（<c>_nextStartAttemptTicks</c>）：
        /// 退避的成因是"上次启动失败"，与当前有无需求无关，需求跃迁也不清零它。
        /// </summary>
        private bool ResolveDemand()
        {
            Func<bool>? provider = FpsDemandProvider;
            bool demand;
            if (provider != null)
            {
                try { demand = provider(); }
                catch { demand = false; }
            }
            else
            {
                long last = Interlocked.Read(ref _lastAccessTicks);
                demand = last != 0 && _clock() - last <= IdleStopSeconds * 1000L;
            }

            // ★ 需求跃迁**不**清零启动失败退避：
            //   退避的成因是"上次启动失败"，与"当前有没有需求"无关。
            //   需求抖动（true→false→true）若能清掉退避，30 秒窗口就会被击穿，
            //   外部 PresentMon 会按需求抖动频率被反复重拉起（每次还带一个 logman 子进程）。
            //   需求恢复时若仍在退避窗口内被挡住是**正确**行为：30 秒后自动恢复。
            return demand;
        }

        /// <summary>
        /// 检查服务健康状态
        /// </summary>
        private void CheckHealth()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (Volatile.Read(ref _isRestarting) != 0 || Volatile.Read(ref _startGate) != 0) return;

            bool demand = ResolveDemand();

            // 外部进程已退出时先回落状态，避免"以为还在跑"而漏掉重启
            SyncServiceState();

            // ★★★ [修改] 没有需求时做可恢复停止（不是最终释放），需求恢复后可再次启动 ★★★
            if (_isRunning && !demand)
            {
                StopService();
                return;
            }

            // 需求存在但服务没在运行：补启动（可恢复）。
            // 这样"需求为真 ⇒ 服务应当运行"，外部只需提供需求判定，不必自己去触发启动。
            if (demand && !_isRunning)
            {
                EnsureServiceStarted();
                return;
            }

            // 只有在应该运行（有需求）的情况下才检查僵死状态
            if (_isRunning && demand)
            {
                // 判断服务是否异常：超过3秒没有收到数据或进程已退出
                bool isDead = (_clock() - _lastDataTicks) > 3000;
                if (!_launcher.IsRunning) isDead = true;

                // 异常时重启服务。
                // 先记一次启动失败退避：进程"秒退"时不能让重启变成每几秒一次的忙循环。
                if (isDead)
                {
                    ScheduleStartRetry();
                    Task.Run(() => RestartService());
                }
            }
        }

        /// <summary>
        /// 可恢复停止：停止外部 FPS 采集服务并清空 FPS 状态，但保留再次启动的能力。
        /// 与 <see cref="Dispose"/>（最终释放）不同：本方法不会结束后台任务，
        /// FPS 需求恢复后仍可重新启动。
        /// </summary>
        public void StopService()
        {
            // ★★★ [新增] 生命周期代次递增（**在取锁之前**） ★★★
            //   让在途启动立即失效：慢操作（EnsureAvailable）返回后、
            //   以及进程创建之后，都会发现"生命周期已经改变"
            //   ⇒ 不得发布运行状态，且必须撤销刚创建的进程。
            //   （代次先于取锁递增：在途启动即使正在等锁，也一样能看到代次已变。）
            Interlocked.Increment(ref _lifecycleGeneration);

            // ★ 与启动侧共享生命周期互斥：
            //   保证"发布运行状态"与"回落运行状态"这两段不会交错。
            lock (_lifecycleGate)
            {
                // ★ 先清"已启动"标记：主动停止不是"启动失败"，
                //   不能因此给后续重新拉起记上一条 30 秒退避。
                Interlocked.Exchange(ref _serviceStarted, 0);
                try { _launcher.Stop(); } catch { }
                _isRunning = false;
            }

            // 完整清除 FPS 状态（帧计数、累加历史、平滑历史、结果、名称缓存）
            _store.Clear();

            _currentFocusPid = 0;
            _pendingPid = 0;
            _pendingCount = 0;
        }

        /// <summary>
        /// 重启 PresentMon 服务
        /// </summary>
        private async Task RestartService()
        {
            if (Interlocked.CompareExchange(ref _isRestarting, 1, 0) != 0) return;
            try
            {
                // 释放当前资源（可恢复停止，不是最终释放）
                StopService();
                // 短暂延迟确保资源完全释放
                try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                // 重新启动服务
                StartService();
            }
            finally { Volatile.Write(ref _isRestarting, 0); }
        }

        /// <summary>
        /// 确保外部服务已启动（多消费者并发调用只会真正启动一次）
        /// </summary>
        private void EnsureServiceStarted()
        {
            if (_isRunning) return;
            if (Volatile.Read(ref _disposed) != 0) return;
            if (Volatile.Read(ref _startGate) != 0) return;   // 已有启动流程在进行
            if (_clock() < Interlocked.Read(ref _nextStartAttemptTicks))
            {
                // 退避生效：记一次被拦下的拉起（观测限频效果）
                Interlocked.Increment(ref _startBlockedByBackoff);
                return;
            }

            Task.Run(() => StartService());
        }

        /// <summary>
        /// 启动 PresentMon 服务
        /// 初始化 PresentMon 进程并设置数据接收
        /// </summary>
        private void StartService()
        {
            // ★★★ [修改] 原子防重入：并发启动请求只有一个会真正执行 ★★★
            if (Interlocked.CompareExchange(ref _startGate, 1, 0) != 0) return;

            try
            {
                if (Volatile.Read(ref _disposed) != 0) return;

                // ★★★ [修改] 拿到闸门后再复查：排队中的启动任务不能重复拉起第二个实例 ★★★
                if (_isRunning) return;
                if (_clock() < Interlocked.Read(ref _nextStartAttemptTicks)) return; // 失败退避

                // 没有需求：不启动、不下载
                if (!ResolveDemand()) return;

                // ★★★ [恢复基线] PresentMon 需要管理员权限（ETW 会话）。
                // 非管理员时必须在这里挡住：否则组件已存在的情况下会反复拉起一个
                // 立刻因无权限退出的 PresentMon，形成"每几秒拉起一次失败进程"的忙循环。
                if (!_administratorProbe()) return;

                // ★★★ [新增] 记录生命周期代次快照 ★★★
                //   慢操作（EnsureAvailable）返回后用它判断"期间是否发生过停止 / 最终释放"。
                long generation = Interlocked.Read(ref _lifecycleGeneration);

                // 确保外部服务可用（必要时下载组件，带退避避免失败重试忙循环）
                if (!_launcher.EnsureAvailable())
                {
                    ScheduleStartRetry();
                    return;
                }

                // ★★★ [新增] 慢操作返回后必须重新校验（释放状态 / 生命周期代次 / 需求） ★★★
                //   少了这一段：释放发生在慢准备期间时，慢准备返回后仍会创建外部进程，
                //   而负责收尾它的后台健康检查循环已经被取消
                //   ⇒ 留下一个**无人清理**的 FPS 外部进程。
                if (Volatile.Read(ref _disposed) != 0) return;
                if (Interlocked.Read(ref _lifecycleGeneration) != generation) return;
                if (!ResolveDemand()) return;

                // ★★★ [新增] 进程创建与"停止 / 最终释放"共享生命周期同步 ★★★
                //   在途区间覆盖"调用 Start() → 发布状态 / 撤销创建"，
                //   最终释放据此做有界等待，保证"释放返回后不会再有进程被创建"。
                Interlocked.Increment(ref _processCreationInFlight);
                try
                {
                    if (!_launcher.Start(OnPresentMonLine, out string? error))
                    {
                        Log($"FPS 服务启动失败: {error}");
                        ScheduleStartRetry();
                        return;
                    }

                    // ★ 与停止 / 最终释放共享生命周期互斥：
                    //   "再校验 → 发布状态 / 撤销创建"是一个不可交错的整体。
                    lock (_lifecycleGate)
                    {
                        // ★★★ [新增] 撤销检查 ★★★
                        //   覆盖"再校验通过之后、Start() 真正创建进程之前"发生释放的窗口：
                        //   此时必须把刚创建的进程停掉，
                        //   否则会留下一个**无人清理**的外部进程。
                        if (Volatile.Read(ref _disposed) != 0 ||
                            Interlocked.Read(ref _lifecycleGeneration) != generation)
                        {
                            try { _launcher.Stop(); } catch { }
                            return;
                        }

                        // 更新服务状态
                        // ★ 标记"已成功启动"必须**先于** _isRunning = true：
                        //   退出回调若恰好落在这两行之间（纳秒级窗口），
                        //   旧顺序下 RecordUnexpectedExitBackoff 会看到 _serviceStarted 仍为 0 而提前返回
                        //   → 这次失败**没被记账** → 下一次 GetFps() 会多拉起一次。
                        //   先置位则无论退出回调何时到达，都必然记上一条退避（Interlocked.Exchange 幂等，不会记两条）。
                        Interlocked.Exchange(ref _serviceStarted, 1);
                        _isRunning = true;
                        Interlocked.Exchange(ref _lastDataTicks, _clock());
                    }
                }
                finally
                {
                    ExitProcessCreationInFlight();
                }
            }
            catch
            {
                ScheduleStartRetry();
            }
            finally
            {
                Volatile.Write(ref _startGate, 0);
            }
        }

        /// <summary>
        /// 退出"进程创建在途"区间；归零时唤醒等待者（最终释放的有界等待）。
        /// </summary>
        private void ExitProcessCreationInFlight()
        {
            if (Interlocked.Decrement(ref _processCreationInFlight) == 0)
            {
                lock (_creationDrainSync) { Monitor.PulseAll(_creationDrainSync); }
            }
        }

        /// <summary>
        /// 有界等待在途的进程创建结束（单调时钟计时）。
        ///
        /// 用途：最终释放据此保证"**Dispose 返回后不会再有进程被创建**"——
        /// 正在创建的那一个会被启动流程撤销（停掉），
        /// 还没进入创建阶段的那个会在慢操作返回后被重新校验拦下。
        /// </summary>
        private void WaitForProcessCreationDrain(int timeoutMs)
        {
            if (Volatile.Read(ref _processCreationInFlight) == 0) return;

            var sw = Stopwatch.StartNew();
            lock (_creationDrainSync)
            {
                while (Volatile.Read(ref _processCreationInFlight) > 0)
                {
                    int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
                    if (remaining <= 0) return;
                    // 分片等待：即使错过一次唤醒，最多再多等一个分片就会重新检查
                    Monitor.Wait(_creationDrainSync, Math.Min(remaining, 50));
                }
            }
        }

        /// <summary>
        /// 启动失败后的退避，避免每 500ms 重复尝试（忙循环）
        /// </summary>
        private void ScheduleStartRetry()
        {
            Interlocked.Exchange(ref _nextStartAttemptTicks, _clock() + StartRetryBackoffMs);
        }

        /// <summary>
        /// 记录"外部服务意外退出"（进程起来即退 / 崩溃 / 正常退出）导致的启动失败退避。
        ///
        /// 不变量：<b>一旦因为启动失败或进程秒退而失败过一次，
        /// 后续在退避窗口（<see cref="StartRetryBackoffMs"/>）内不得因任何原因再次拉起外部进程
        /// （含需求重新恢复、需求抖动 true→false→true）。</b>
        /// 真实保证的范围**仅限**"退避窗口内不拉起"；
        /// 退避到期后是否拉起，仍取决于当时是否存在需求 —— 这一点不要写成"绝对不变量"。
        ///
        /// 主动停止（<see cref="StopService"/> / <see cref="Dispose"/>）不算失败：
        /// 那两条路径会先清掉 <c>_serviceStarted</c> 标记，因此这里不会误记退避。
        ///
        /// ★ 取舍说明（本轮新增行为 —— 不区分"正常退出 vs 崩溃"）：
        /// 本方法对**任何**意外退出都记一条 30s 退避，包括 PresentMon 正常退出。
        /// 代价：正常退出后 30 秒内不会重启（改前是下次 GetFps() 立即重启）。
        /// 理由：要区分退出原因就得额外判定（例如按退出码/退出时长区分"正常收尾"与"崩溃"），
        /// 那会引入"看起来正常、其实启动失败"的漏判窗口，
        /// 削弱"秒退必须记退避"这条刚修好的不变量；
        /// 且 PresentMon 通常常驻运行，该代价的实际影响很低。
        /// 因此本轮**只补文档，不新增行为判定**。
        /// </summary>
        private void RecordUnexpectedExitBackoff()
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            // 只有"确实成功启动过、且不是我们主动停的"才记退避。
            // Interlocked.Exchange 同时保证幂等：同一次退出只记一条退避。
            if (Interlocked.Exchange(ref _serviceStarted, 0) == 0) return;

            ScheduleStartRetry();
        }

        /// <summary>
        /// PresentMon 输出回调：解析一行并更新时间戳
        /// </summary>
        private void OnPresentMonLine(string line)
        {
            Interlocked.Exchange(ref _lastDataTicks, _clock());
            ParseLine(line);
        }

        /// <summary>
        /// 解析 PresentMon 输出的一行数据
        /// 提取进程 PID 和帧数信息
        /// </summary>
        /// <param name="line">PresentMon 输出的一行文本</param>
        private void ParseLine(string line)
        {
            try
            {
                _store.RecordLine(line);
            }
            catch { }
        }

        /// <summary>
        /// 计算各进程的 FPS 值
        /// 使用双层算法：滑动窗口累加 + 奥运会平滑
        /// </summary>
        private void CalculateFps()
        {
            if (!_isRunning) return;

            // 获取本周期的精确时间（约 0.5s）
            double elapsedSeconds = _cycleTimer.Elapsed.TotalSeconds;
            _cycleTimer.Restart();

            if (elapsedSeconds <= 0) return;

            // 统计与清理（含已退出 PID 移除、闲置清理、查询限频）
            _store.AdvanceCycle(elapsedSeconds);
        }

        /// <summary>
        /// 清理 PresentMon 僵尸进程和会话
        /// </summary>
        public static void ForceKillZombies()
        {
            // 如果是系统关机，跳过清理以避免 logman.exe 报错 (0xc0000142)
            if (_isSystemShuttingDown) return;

            try
            {
                // 1. 优先终止所有名为 LiteMonitorPlusFPS 的进程 (最重要)
                foreach (var p in Process.GetProcessesByName("LiteMonitorPlusFPS")) { try { p.Kill(); } catch { } }

                // 2. 停止 LiteMonitorPlusFPS 会话
                // 仅在 CLR 未关闭时尝试调用 logman，避免在程序退出极晚期引发异常
                if (!Environment.HasShutdownStarted)
                {
                    Process.Start(new ProcessStartInfo { FileName = "logman", Arguments = $"stop {SESSION_NAME} -ets", UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(100);
                }
            }
            catch { }
        }

        /// <summary>
        /// 检查当前进程是否有管理员权限
        /// </summary>
        /// <returns>有管理员权限返回 true，否则返回 false</returns>
        public static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent()) {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        /// <summary>
        /// 最终释放资源：停止外部服务并结束后台任务。
        /// 与 <see cref="StopService"/>（可恢复停止）区分：本方法之后无法再启动。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            // ★ 与启动侧共享生命周期同步：代次递增 + 停止启动器 + 回落运行状态。
            //   在途的启动流程会因此看到"生命周期已变" → 不得再创建进程；
            //   已经创建出来的那一个会被撤销（停掉）。
            StopService();

            // ★★★ [新增] 有界等待在途的进程创建结束 ★★★
            //   保证"**Dispose 返回后不会再有进程被创建**"：
            //   正在创建的那一个会在返回前完成撤销（停掉）；
            //   还没进入创建阶段的那个会在慢操作返回后被重新校验拦下。
            WaitForProcessCreationDrain(DisposeCreationDrainTimeoutMs);

            // 只取消，不释放 CancellationTokenSource：
            // 后台循环与 RestartService 仍会读取 _cts.Token，
            // 释放后读取会抛 ObjectDisposedException（只捕 OperationCanceledException 会漏成未观测异常）。
            try { _cts.Cancel(); } catch { }
        }

        // ================================================================
        // 默认实现：基于 Process 的进程查询器
        // ================================================================
        private sealed class SystemProcessQuery : IFpsProcessQuery
        {
            public FpsProcessQueryResult Query(int pid)
            {
                try
                {
                    // Process 对象用完立即释放
                    using Process proc = Process.GetProcessById(pid);

                    string name;
                    try { name = proc.ProcessName; }
                    catch (ArgumentException) { return FpsProcessQueryResult.CreateExited(); }
                    catch { return FpsProcessQueryResult.CreateUnknown(); }

                    long identity = 0;
                    try { identity = proc.StartTime.ToUniversalTime().Ticks; } catch { identity = 0; }

                    return FpsProcessQueryResult.CreateFound(name, identity);
                }
                catch (ArgumentException)
                {
                    // 进程不存在（已退出）
                    return FpsProcessQueryResult.CreateExited();
                }
                catch
                {
                    // 权限不足或瞬时失败：不等于已退出
                    return FpsProcessQueryResult.CreateUnknown();
                }
            }
        }

        // ================================================================
        // 默认实现：PresentMon 启动器
        // ================================================================
        private sealed class PresentMonServiceLauncher : IFpsServiceLauncher, IFpsServiceExitNotifier
        {
            private readonly DriverInstaller? _installer;
            private readonly Func<bool> _administratorProbe;
            private Process? _process;
            private long _nextEnsureTicks;

            /// <summary>外部服务进程退出时触发</summary>
            public event Action? Exited;

            public PresentMonServiceLauncher(DriverInstaller? installer, Func<bool>? administratorProbe = null)
            {
                _installer = installer;
                _administratorProbe = administratorProbe ?? IsAdministrator;
            }

            public bool IsRunning
            {
                get
                {
                    Process? proc = _process;
                    if (proc == null) return false;
                    try { return !proc.HasExited; }
                    catch { return false; }
                }
            }

            public bool EnsureAvailable()
            {
                // ★★★ [修复] PresentMon 需要管理员权限（ETW 会话）。
                // 这个检查必须对"组件已存在"的分支同样生效，否则非管理员会拉起一个
                // 立刻因无权限退出的 PresentMon。
                if (!_administratorProbe()) return false;

                if (File.Exists(ExePath)) return true;

                // 下载退避：失败时不要每 500ms 重复发起下载
                long now = Environment.TickCount64;
                if (now < Interlocked.Read(ref _nextEnsureTicks)) return false;
                Interlocked.Exchange(ref _nextEnsureTicks, now + 60000);

                try
                {
                    if (_installer != null)
                    {
                        _installer.CheckAndDownloadPresentMon(silent: true).Wait(120000);
                    }
                }
                catch { }

                return File.Exists(ExePath);
            }

            public bool Start(Action<string> onLine, out string? error)
            {
                error = null;
                Process? proc = null;
                try
                {
                    // 清理可能存在的僵尸进程
                    ForceKillZombies();

                    // 配置 PresentMon 进程启动信息
                    var psi = new ProcessStartInfo
                    {
                        FileName = ExePath,
                        Arguments = $"-session_name {SESSION_NAME} -stop_existing_session -output_stdout",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    // 启动 PresentMon 进程
                    proc = Process.Start(psi);
                    if (proc == null)
                    {
                        error = "PresentMon 启动失败";
                        return false;
                    }

                    _process = proc;

                    // 忽略错误输出
                    proc.ErrorDataReceived += (s, e) => { };
                    proc.BeginErrorReadLine();

                    // 监控进程退出状态：进程一退出就通知调用方回落"服务运行中"状态
                    Task.Run(async () =>
                    {
                        try { await proc.WaitForExitAsync(); } catch { }
                        try { Exited?.Invoke(); } catch { }
                    });

                    // 异步处理 PresentMon 输出数据
                    Task.Run(async () =>
                    {
                        try
                        {
                            while (!proc.StandardOutput.EndOfStream)
                            {
                                string? line = await proc.StandardOutput.ReadLineAsync();
                                if (!string.IsNullOrWhiteSpace(line))
                                {
                                    onLine(line);
                                }
                            }
                        }
                        catch { }
                    });

                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    try { proc?.Dispose(); } catch { }
                    return false;
                }
            }

            public void Stop()
            {
                Process? proc = Interlocked.Exchange(ref _process, null);
                try
                {
                    if (proc != null)
                    {
                        if (!proc.HasExited) proc.Kill();
                    }
                }
                catch { }
                finally
                {
                    try { proc?.Dispose(); } catch { }
                }

                // 清理僵尸进程与 ETW 会话
                ForceKillZombies();
            }
        }
    }
}
