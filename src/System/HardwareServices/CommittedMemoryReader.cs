using System;
using System.Runtime.InteropServices;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 提交量读取结果的状态。
    ///
    /// 为什么不用一个 bool 表达成功/失败：
    ///   "缺失"和"有效但为 0"是两件完全不同的事 —— 0 是合法读数（系统确实没提交任何内存），
    ///   缺失必须显示成"--"而不是 0。状态枚举既让调用方能区分两者，
    ///   又能让失败时把**原因**带出去（写诊断日志 / 排查），而不是所有失败都退化成同一个匿名 null。
    /// </summary>
    public enum CommittedMemoryStatus
    {
        /// <summary>读数有效（含"合法的 0"）。</summary>
        Ok = 0,

        /// <summary>原生 API 调用失败（返回 false、抛异常或入口不可用）。</summary>
        ApiFailed = 1,

        /// <summary>PageSize 为 0：页数没法换算成字节。</summary>
        InvalidPageSize = 2,

        /// <summary>提交上限为 0：没有分母就算不出比例。</summary>
        InvalidLimit = 3,

        /// <summary>页数 × 页大小超出 Int64 范围。</summary>
        Overflow = 4,

        /// <summary>结构体大小与 sizeof(PERFORMANCE_INFORMATION) 不符（布局被改坏或指针宽度不匹配）。</summary>
        StructureSizeMismatch = 5,

        /// <summary>旧采集代次的回调：既不读取也不写入，避免旧回调污染新代次。</summary>
        StaleGeneration = 6,
    }

    /// <summary>
    /// PERFORMANCE_INFORMATION 的原始读数（单位：**页**，不是字节）。
    ///
    /// 为什么保留原始页数而不是直接存字节：
    ///   换算必须由纯计算层统一做，并且要能在换算前就发现溢出（页数 × 页大小可能超出 Int64）。
    ///   如果 adapter 直接交出字节，溢出检查就只能发生在溢出之后了。
    /// </summary>
    public readonly struct CommittedMemoryRaw
    {
        /// <summary>页大小（字节/页）。</summary>
        public uint PageSize { get; }

        /// <summary>已提交页数（提交量）。</summary>
        public ulong CommitTotalPages { get; }

        /// <summary>提交上限页数（分页文件 + 物理内存的提交额度上限，会随系统设置变化）。</summary>
        public ulong CommitLimitPages { get; }

        /// <summary>
        /// 物理内存总量页数。
        /// ★ 这里只作为**诊断信息**保留：它**绝不**参与任何比例计算。
        ///   提交比例的分母必须是提交上限（CommitLimit），用物理内存总量当分母会算出 150% 这种
        ///   看起来像"爆表"、实际只是提交额度比物理内存大的比例。
        /// </summary>
        public ulong PhysicalTotalPages { get; }

        public CommittedMemoryRaw(
            uint pageSize,
            ulong commitTotalPages,
            ulong commitLimitPages,
            ulong physicalTotalPages = 0)
        {
            PageSize = pageSize;
            CommitTotalPages = commitTotalPages;
            CommitLimitPages = commitLimitPages;
            PhysicalTotalPages = physicalTotalPages;
        }
    }

    /// <summary>
    /// 提交量的原生数据源。
    ///
    /// 为什么把它抽成接口：
    ///   纯计算层必须能在**不碰真实系统 API** 的前提下被测试（注入固定页数、模拟 API 失败），
    ///   否则页数→字节的换算、溢出判定、0 与缺失的区分都只能在真机上碰运气。
    /// </summary>
    public interface ICommittedMemorySource
    {
        /// <summary>
        /// 读取一次系统提交信息。
        /// 返回 false 表示这次读取失败（原因见 <see cref="LastStatus"/> / <see cref="LastError"/>）。
        /// </summary>
        bool TryRead(out CommittedMemoryRaw raw);

        /// <summary>最近一次读取的状态（成功时为 <see cref="CommittedMemoryStatus.Ok"/>）。</summary>
        CommittedMemoryStatus LastStatus { get; }

        /// <summary>最近一次读取的失败原因（成功时为 null）。</summary>
        string? LastError { get; }
    }

    /// <summary>
    /// 提交量的 Windows 原生数据源：GetPerformanceInfo / K32GetPerformanceInfo。
    ///
    /// 为什么用这个 API 而不是性能计数器：
    ///   1) 它给的是**页**，配 PageSize 就能拿到精确的 64 位字节数，不需要经过 float；
    ///   2) 不需要管理员权限、不依赖任何硬件驱动，也不会因为性能计数器服务被禁用/重置而失效；
    ///   3) 它和"物理内存占用率"是两个独立口径，正好对应任务管理器"内存 → 已提交"。
    ///
    /// 注意：这里刻意**不引用** PerformanceCounterManager，
    ///   因此"优先使用系统计数器"开关关闭、或计数器尚未初始化时，本路径依然可用。
    /// </summary>
    public sealed class WindowsCommittedMemorySource : ICommittedMemorySource
    {
        /// <summary>
        /// sizeof(PERFORMANCE_INFORMATION) 的期望值。
        ///   64 位：cb(4) + 对齐(4) + 10×SIZE_T(80) + 3×DWORD(12) + 尾部对齐(4) = 104
        ///   32 位：cb(4) + 10×SIZE_T(40) + 3×DWORD(12) = 56
        /// </summary>
        private static readonly int ExpectedStructureSize = IntPtr.Size == 8 ? 104 : 56;

        private CommittedMemoryStatus _lastStatus = CommittedMemoryStatus.Ok;
        private string? _lastError;

        public CommittedMemoryStatus LastStatus => _lastStatus;
        public string? LastError => _lastError;

        /// <summary>
        /// sizeof(PERFORMANCE_INFORMATION) 的期望值（按指针宽度：64 位 104、32 位 56）。
        /// 暴露出来是为了让"结构布局是否正确"这件事可以被直接断言，而不是只靠运行期现象。
        /// </summary>
        public static int ExpectedPerformanceInformationSize => ExpectedStructureSize;

        /// <summary>实际 marshaled 出来的结构体大小（自检用）。</summary>
        public static int ActualStructureSize => Marshal.SizeOf<PERFORMANCE_INFORMATION>();

        /// <summary>
        /// PERFORMANCE_INFORMATION 原生结构。
        /// SIZE_T 必须按指针宽度声明（DWORD 是 32 位、SIZE_T 是指针宽度），
        /// 写死成 long/ulong 会在 32 位进程上把结构大小算错，API 会直接返回失败。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PERFORMANCE_INFORMATION
        {
            public uint cb;                    // DWORD
            public IntPtr CommitTotal;         // SIZE_T
            public IntPtr CommitLimit;         // SIZE_T
            public IntPtr CommitPeak;          // SIZE_T
            public IntPtr PhysicalTotal;       // SIZE_T
            public IntPtr PhysicalAvailable;   // SIZE_T
            public IntPtr SystemCache;         // SIZE_T
            public IntPtr KernelTotal;         // SIZE_T
            public IntPtr KernelPaged;         // SIZE_T
            public IntPtr KernelNonpaged;      // SIZE_T
            public IntPtr PageSize;            // SIZE_T
            public uint HandleCount;           // DWORD
            public uint ProcessCount;          // DWORD
            public uint ThreadCount;           // DWORD
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPerformanceInfo(
            [Out] out PERFORMANCE_INFORMATION lpPerformanceInformation,
            uint cb);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "K32GetPerformanceInfo")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool K32GetPerformanceInfo(
            [Out] out PERFORMANCE_INFORMATION lpPerformanceInformation,
            uint cb);

        public bool TryRead(out CommittedMemoryRaw raw)
        {
            raw = default;

            int size = Marshal.SizeOf<PERFORMANCE_INFORMATION>();
            if (size != ExpectedStructureSize)
            {
                // 结构布局被改坏（或指针宽度与预期不符）：宁可读不到，也不能按错布局解引用
                _lastStatus = CommittedMemoryStatus.StructureSizeMismatch;
                _lastError = $"PERFORMANCE_INFORMATION 大小不符: 期望 {ExpectedStructureSize}, 实际 {size}";
                return false;
            }

            var info = new PERFORMANCE_INFORMATION { cb = (uint)size };

            bool ok;
            try
            {
                ok = GetPerformanceInfo(out info, (uint)size);
            }
            catch (Exception ex)
            {
                _lastStatus = CommittedMemoryStatus.ApiFailed;
                _lastError = "GetPerformanceInfo 调用异常: " + ex.Message;
                return false;
            }

            if (!ok)
            {
                // psapi 入口不可用（精简系统 / API 集被裁剪）时回退到 kernel32 的 K32 版本
                try
                {
                    ok = K32GetPerformanceInfo(out info, (uint)size);
                }
                catch (Exception ex)
                {
                    _lastStatus = CommittedMemoryStatus.ApiFailed;
                    _lastError = "K32GetPerformanceInfo 调用异常: " + ex.Message;
                    return false;
                }
            }

            if (!ok)
            {
                _lastStatus = CommittedMemoryStatus.ApiFailed;
                _lastError = $"GetPerformanceInfo 返回 false (Win32={Marshal.GetLastWin32Error()})";
                return false;
            }

            if (info.cb != (uint)size)
            {
                // API 用 cb 校验结构大小：对不上说明我们对结构的理解与系统不一致
                _lastStatus = CommittedMemoryStatus.StructureSizeMismatch;
                _lastError = $"cb 不符: 期望 {size}, 实际 {info.cb}";
                return false;
            }

            long pageSize = info.PageSize.ToInt64();
            if (pageSize <= 0 || pageSize > uint.MaxValue)
            {
                _lastStatus = CommittedMemoryStatus.InvalidPageSize;
                _lastError = $"PageSize 非法: {pageSize}";
                return false;
            }

            _lastStatus = CommittedMemoryStatus.Ok;
            _lastError = null;
            raw = new CommittedMemoryRaw(
                pageSize: (uint)pageSize,
                commitTotalPages: ToUInt64(info.CommitTotal),
                commitLimitPages: ToUInt64(info.CommitLimit),
                physicalTotalPages: ToUInt64(info.PhysicalTotal));
            return true;
        }

        /// <summary>
        /// 把 SIZE_T（指针宽度）读成 ulong。
        /// 走 unchecked 是刻意的：这里只搬运位模式，不做算术，
        /// 超过 long.MaxValue 的页数也要能原样带出来（由计算层去做溢出判定）。
        /// </summary>
        private static ulong ToUInt64(IntPtr value) => unchecked((ulong)value.ToInt64());
    }

    /// <summary>
    /// 提交量快照：一次读取的不可变结果。
    ///
    /// 为什么是不可变对象：
    ///   它会被 UI / Web / 任务栏多个消费者同时读，还要跟着采集代次一起被缓存。
    ///   可变对象在"发布时是一组数、读到时变成另一组数"时会自相矛盾
    ///   （分子是新一代的、分母是旧一代的）。
    /// </summary>
    public sealed class CommittedMemorySnapshot
    {
        /// <summary>本次读取的状态。</summary>
        public CommittedMemoryStatus Status { get; }

        /// <summary>读数是否有效（合法的 0 也是有效）。</summary>
        public bool IsValid => Status == CommittedMemoryStatus.Ok;

        /// <summary>读数是否缺失（缺失时**不允许**被当成 0 使用）。</summary>
        public bool IsMissing => !IsValid;

        /// <summary>已提交字节数（64 位整数；只有 IsValid 时有意义）。</summary>
        public long CommittedBytes { get; }

        /// <summary>提交上限字节数（64 位整数；只有 IsValid 时有意义）。</summary>
        public long LimitBytes { get; }

        /// <summary>提交比例（%），double 计算；只有 IsValid 时有意义。</summary>
        public double LoadPercent { get; }

        /// <summary>本次快照所属的采集代次。</summary>
        public int Generation { get; }

        /// <summary>单调时间戳（TimeProvider.GetTimestamp，与墙上时间无关）。</summary>
        public long Timestamp { get; }

        /// <summary>
        /// 样本是否异常（提交量 &gt; 提交上限）。
        /// 异常样本仍然**保留原始字节值**用于诊断，只在展示时钳制比例。
        /// </summary>
        public bool IsAnomalous { get; }

        /// <summary>失败原因（有效时为 null）。</summary>
        public string? ErrorMessage { get; }

        /// <summary>
        /// 适配现有 float 取值接口的百分比。
        /// ★ 这是**唯一**一次降到 float 的地方：字节量全程保持 long，比例全程保持 double，
        ///   否则大数值会先在 float 上丢精度再传下去。
        /// </summary>
        public float LoadPercentAsFloat => (float)LoadPercent;

        /// <summary>
        /// 展示用比例：按项目现有进度条规则钳制到 [0, 100]。
        /// 钳制只发生在展示层，<see cref="CommittedBytes"/> / <see cref="LimitBytes"/> 保持真实值。
        /// </summary>
        public double DisplayLoadPercent => Math.Clamp(LoadPercent, 0d, 100d);

        /// <summary>展示用比例的 float 版本。</summary>
        public float DisplayLoadPercentAsFloat => (float)DisplayLoadPercent;

        internal CommittedMemorySnapshot(
            CommittedMemoryStatus status,
            long committedBytes,
            long limitBytes,
            double loadPercent,
            int generation,
            long timestamp,
            bool isAnomalous,
            string? errorMessage)
        {
            Status = status;
            CommittedBytes = committedBytes;
            LimitBytes = limitBytes;
            LoadPercent = loadPercent;
            Generation = generation;
            Timestamp = timestamp;
            IsAnomalous = isAnomalous;
            ErrorMessage = errorMessage;
        }

        /// <summary>构造一个"缺失"快照（不是有效 0）。</summary>
        public static CommittedMemorySnapshot Missing(
            CommittedMemoryStatus status,
            string? message,
            int generation,
            long timestamp)
        {
            return new CommittedMemorySnapshot(
                status, 0L, 0L, 0d, generation, timestamp, false, message);
        }
    }

    /// <summary>
    /// 提交量的纯计算层：原始页数 → 不可变快照。
    ///
    /// 为什么单独拆一层：
    ///   这里承载全部口径（分母是提交上限、0 与缺失的区分、溢出判定），
    ///   而它**不碰任何系统 API**，因此每一条口径都能用注入的固定页数直接断言。
    /// </summary>
    public static class CommittedMemoryCalculator
    {
        /// <summary>
        /// 按 Windows 口径换算并计算提交比例。
        ///
        /// 口径（与任务管理器"内存 → 已提交"一致）：
        ///   CommittedBytes  = CommitTotal  × PageSize
        ///   LimitBytes      = CommitLimit  × PageSize
        ///   LoadPercent     = 100 × CommittedBytes / LimitBytes
        ///
        /// ★ 分母永远是**提交上限**，不是物理内存总量。
        ///   提交上限通常大于物理内存（分页文件占一部分），
        ///   用物理内存当分母会算出 150% 这种"提交额度比内存还大"的假象。
        /// </summary>
        public static CommittedMemorySnapshot Compute(
            in CommittedMemoryRaw raw,
            int generation,
            long timestamp)
        {
            if (raw.PageSize == 0)
            {
                return CommittedMemorySnapshot.Missing(
                    CommittedMemoryStatus.InvalidPageSize,
                    "PageSize = 0，无法把页数换算成字节",
                    generation, timestamp);
            }

            if (raw.CommitLimitPages == 0)
            {
                // 没有分母就算不出比例；这是"缺失"，不是 0%
                return CommittedMemorySnapshot.Missing(
                    CommittedMemoryStatus.InvalidLimit,
                    "CommitLimit = 0，无法计算提交比例",
                    generation, timestamp);
            }

            long pageSize = raw.PageSize;
            long maxPages = long.MaxValue / pageSize;
            if (raw.CommitTotalPages > (ulong)maxPages || raw.CommitLimitPages > (ulong)maxPages)
            {
                return CommittedMemorySnapshot.Missing(
                    CommittedMemoryStatus.Overflow,
                    "页数 × 页大小超出 Int64 范围",
                    generation, timestamp);
            }

            long committedBytes;
            long limitBytes;
            try
            {
                committedBytes = checked((long)raw.CommitTotalPages * pageSize);
                limitBytes = checked((long)raw.CommitLimitPages * pageSize);
            }
            catch (OverflowException)
            {
                return CommittedMemorySnapshot.Missing(
                    CommittedMemoryStatus.Overflow,
                    "页数 × 页大小溢出",
                    generation, timestamp);
            }

            if (limitBytes <= 0)
            {
                return CommittedMemorySnapshot.Missing(
                    CommittedMemoryStatus.InvalidLimit,
                    "提交上限换算成字节后不是正数",
                    generation, timestamp);
            }

            // 比例用 double：分子分母都是 long，先转 double 再除，全程不经过 float
            double loadPercent = 100.0 * (double)committedBytes / (double)limitBytes;

            // 提交量大于上限属于异常样本：保留真实字节值，只在展示时钳制
            bool anomalous = committedBytes > limitBytes;

            return new CommittedMemorySnapshot(
                CommittedMemoryStatus.Ok,
                committedBytes,
                limitBytes,
                loadPercent,
                generation,
                timestamp,
                anomalous,
                null);
        }
    }

    /// <summary>
    /// 提交量读取器：把原生数据源、采集代次和快照缓存缝在一起。
    ///
    /// ★ 它**不是**一个轮询器：没有定时器、不自己开线程。
    ///   原生读取只在调用方明确要求"采一次"时发生，且同一采集代次内只会真正发生一次。
    ///   接线方式是复用现有的采集代次（HardwareSamplingCoordinator 发布的 generation）：
    ///   采集轮次里调一次 <see cref="Capture"/>，UI / Web / 任务栏再用
    ///   <see cref="TryGetPublished"/> 取同一代次的快照，绘制频率不会触发任何系统调用。
    ///
    /// ★ 它**不依赖** PerformanceCounterManager：
    ///   "优先使用系统计数器"开关关闭、或计数器尚未初始化时，本路径依然可用。
    /// </summary>
    public sealed class CommittedMemoryReader
    {
        private readonly ICommittedMemorySource _source;
        private readonly TimeProvider _time;
        private readonly object _gate = new object();

        /// <summary>最近一次读取的结果（含失败状态）；按采集代次缓存。</summary>
        private CommittedMemorySnapshot? _last;

        /// <param name="source">原生数据源；为 null 时使用 Windows 原生实现。</param>
        /// <param name="timeProvider">单调时钟（用于快照时间戳），默认 <see cref="TimeProvider.System"/>。</param>
        public CommittedMemoryReader(ICommittedMemorySource? source = null, TimeProvider? timeProvider = null)
        {
            _source = source ?? CreateDefaultSource();
            _time = timeProvider ?? TimeProvider.System;
        }

        private static ICommittedMemorySource CreateDefaultSource()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new WindowsCommittedMemorySource();
            }

            // 非 Windows 平台没有这个 API：直接给一个"永远失败"的数据源，
            // 让上层拿到"缺失"而不是抛异常。
            return new UnsupportedCommittedMemorySource();
        }

        /// <summary>
        /// 采集一次提交量快照。
        ///
        /// 同一采集代次内重复调用只会发生**一次**原生读取（后续调用直接复用本代次结果，
        /// 包括失败的"缺失"结果——否则同代次的多个消费者会把失败路径反复重试一遍）。
        /// 采集代次变化时重新读取，因此提交上限每轮都是新的（分页文件会动态变化，
        /// 不能把启动时的物理内存大小当上限缓存下来）。
        /// </summary>
        /// <param name="generation">当前采集代次（由协调器发布，保证单调递增）。</param>
        public CommittedMemorySnapshot Capture(int generation)
        {
            lock (_gate)
            {
                if (_last != null)
                {
                    if (_last.Generation == generation)
                    {
                        // 同代次：复用本代次已经读到的结果，不再触发原生调用
                        return _last;
                    }

                    if (generation < _last.Generation)
                    {
                        // 旧代次的回调：既不读也不写，避免旧结果污染新代次
                        return CommittedMemorySnapshot.Missing(
                            CommittedMemoryStatus.StaleGeneration,
                            $"旧采集代次 {generation} 晚于当前代次 {_last.Generation}，已忽略",
                            generation,
                            _time.GetTimestamp());
                    }
                }

                CommittedMemorySnapshot snapshot = ReadOnce(generation);
                _last = snapshot;
                return snapshot;
            }
        }

        /// <summary>
        /// 取当前采集代次**已经发布**的快照；没有就返回 null。
        ///
        /// ★ 这个方法**永远不会**触发原生读取：消费者（UI / Web / 任务栏）只能用它，
        ///   这样绘制频率再高也不会变成系统调用频率。
        /// </summary>
        public CommittedMemorySnapshot? TryGetPublished(int generation)
        {
            lock (_gate)
            {
                if (_last == null || _last.Generation != generation) return null;
                return _last.IsValid ? _last : null;
            }
        }

        /// <summary>最近一次读取的结果（含失败状态与失败原因），仅供诊断。</summary>
        public CommittedMemorySnapshot? LastSnapshot
        {
            get { lock (_gate) { return _last; } }
        }

        private CommittedMemorySnapshot ReadOnce(int generation)
        {
            long timestamp = _time.GetTimestamp();

            CommittedMemoryRaw raw;
            bool ok;
            try
            {
                ok = _source.TryRead(out raw);
            }
            catch (Exception ex)
            {
                return CommittedMemorySnapshot.Missing(
                    CommittedMemoryStatus.ApiFailed,
                    "提交量读取异常: " + ex.Message,
                    generation, timestamp);
            }

            if (!ok)
            {
                CommittedMemoryStatus status = _source.LastStatus;
                if (status == CommittedMemoryStatus.Ok)
                {
                    // 数据源没给出具体原因：按通用 API 失败处理
                    status = CommittedMemoryStatus.ApiFailed;
                }
                return CommittedMemorySnapshot.Missing(
                    status,
                    _source.LastError ?? "提交量读取失败",
                    generation, timestamp);
            }

            return CommittedMemoryCalculator.Compute(raw, generation, timestamp);
        }

        /// <summary>非 Windows 平台上的占位数据源：永远失败，绝不抛异常。</summary>
        private sealed class UnsupportedCommittedMemorySource : ICommittedMemorySource
        {
            public CommittedMemoryStatus LastStatus => CommittedMemoryStatus.ApiFailed;
            public string? LastError => "当前平台不支持 GetPerformanceInfo";

            public bool TryRead(out CommittedMemoryRaw raw)
            {
                raw = default;
                return false;
            }
        }
    }
}
