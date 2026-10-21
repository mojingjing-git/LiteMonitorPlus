using System;
using System.Threading;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 已发布的提交内存状态（不可变）。
    ///
    /// 为什么要有"发布态"这一层，而不是直接把 <see cref="CommittedMemorySnapshot"/> 丢给消费者：
    ///   容量模式要同时显示"分子（提交量）+ 分母（当前提交上限）"两个量，
    ///   而 formatter 的签名（GetValueStr / GetUnitStr）只带一个 float，数值与单位是**两次独立调用**。
    ///   如果两次调用各自去取快照，就可能拿到**不同代次**的两个快照，
    ///   于是出现「分子是新一代、分母是旧一代」这种自相矛盾的读数。
    ///   把两个量**打包进同一个不可变对象**并原子发布，
    ///   formatter 只要读一次这个对象，分子与分母就必然同代次、同倍率。
    /// </summary>
    public sealed class CommittedMemoryDisplay
    {
        /// <summary>本次状态所属的采集代次。</summary>
        public int Generation { get; }

        /// <summary>读取状态（Ok 表示有效，含"合法的 0"）。</summary>
        public CommittedMemoryStatus Status { get; }

        /// <summary>读数是否缺失（缺失时**不允许**被当成 0 使用）。</summary>
        public bool IsMissing => Status != CommittedMemoryStatus.Ok;

        /// <summary>提交比例（%），double 计算；只有有效时才有意义。</summary>
        public double LoadPercent { get; }

        /// <summary>已提交字节数（64 位整数；只有有效时才有意义）。</summary>
        public long CommittedBytes { get; }

        /// <summary>提交上限字节数（64 位整数；只有有效时才有意义）。</summary>
        public long LimitBytes { get; }

        /// <summary>适配现有 float 取值接口的百分比。</summary>
        public float LoadPercentAsFloat => (float)LoadPercent;

        /// <summary>展示用比例（钳制到 0–100，与既有进度条规则一致）。</summary>
        public double DisplayLoadPercent => Math.Clamp(LoadPercent, 0d, 100d);

        internal CommittedMemoryDisplay(int generation, CommittedMemorySnapshot snapshot)
        {
            Generation = generation;
            Status = snapshot.Status;
            LoadPercent = snapshot.LoadPercent;
            CommittedBytes = snapshot.CommittedBytes;
            LimitBytes = snapshot.LimitBytes;
        }
    }

    /// <summary>
    /// 提交内存的**单一发布点**（Single Publication Point）。
    ///
    /// ★ 契约（务必遵守）：
    ///   1) 只有采集层（HardwareMonitor.CollectOnce）会**写**这里；
    ///   2) formatter / Web / 任务栏**只读**这里，而且读到的必须是**已发布**的状态；
    ///   3) 任何消费者都**不允许**自己去调用 GetPerformanceInfo —— 否则绘制频率（100ms）
    ///      就会变成系统调用频率，并且面板 / 任务栏 / Web 三处可能各读各的、出现口径不一致。
    ///
    /// ★ 为什么是静态的：面板、任务栏、Web 三条消费链彼此不知道对方的存在，
    ///   唯一能让它们口径一致的办法就是"同一个发布点"，而不是"各自去读同一个 API"。
    /// </summary>
    public static class CommittedMemoryPublisher
    {
        private static CommittedMemoryDisplay? _current;

        /// <summary>
        /// 当前已发布的提交内存状态；从未发布过（还没采到第一轮）时为 null。
        /// ★ 只读：调用方**不会**因此触发任何系统调用。
        /// </summary>
        public static CommittedMemoryDisplay? Current => Volatile.Read(ref _current);

        /// <summary>
        /// 发布一次采集结果（采集层专用）。
        /// </summary>
        /// <param name="generation">本次采集的代次。</param>
        /// <param name="snapshot">采集层拿到的快照（可能是"缺失"快照）。</param>
        /// <returns>发布出去的状态对象（便于调用方直接断言）。</returns>
        public static CommittedMemoryDisplay Publish(int generation, CommittedMemorySnapshot snapshot)
        {
            var display = new CommittedMemoryDisplay(generation, snapshot);
            // 原子替换：读者要么看到旧状态，要么看到新状态，不会看到"半截"状态。
            Volatile.Write(ref _current, display);
            return display;
        }

        /// <summary>清空已发布状态（硬件层释放 / 重载时调用，避免旧代次的结果复活）。</summary>
        public static void Clear() => Volatile.Write(ref _current, null);

        /// <summary>
        /// 取当前提交比例（%）。
        /// ★ 缺失时返回 **null**（而不是 0）："没有数据"和"真的是 0"必须能区分。
        /// </summary>
        public static float? TryGetLoadPercent()
        {
            CommittedMemoryDisplay? display = Current;
            if (display == null || display.IsMissing) return null;
            return display.LoadPercentAsFloat;
        }
    }
}
