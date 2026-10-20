using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace LiteMonitor.src.SystemServices.Backdrop
{
    // ================================================================
    // 捕获后端 / 图像处理 的最小抽象（供集成者与 A15 捕获后端对接）
    // ================================================================

    /// <summary>
    /// 实时背景**捕获后端**：把"一次捕获请求"变成"一张底图"。
    ///
    /// ★ 所有权合同（与 <see cref="IBackdropFrameSource"/> 的"借用"语义**不同**，务必分清）：
    /// 1. <see cref="TryCapture"/> 成功时返回的位图，其所有权**转移给调用方**
    ///    （即 <see cref="BackdropCaptureService"/>）—— 调用方负责最终释放。
    ///    这是本接口与"借用型" <c>IBackdropFrameSource.TryGetFrame</c> 的根本区别：
    ///    捕获后端**创建**位图，因此它把所有权交出去。
    /// 2. 失败时必须返回 <c>null</c>（或一个由调用方负责释放的中间位图）。
    /// 3. 本接口**不启动**任何线程/计时器；抓屏的时机由后台服务决定。
    /// 4. <see cref="IsPreview"/> 语义由真实捕获后端自行保证（真实捕获必须报"非预览"）。
    /// </summary>
    public interface IBackdropCaptureBackend
    {
        /// <summary>后端是否已就绪、可以捕获。</summary>
        bool IsReady { get; }

        /// <summary>未就绪原因（人类可读）。<see cref="IsReady"/> 为 false 时必须给出可读原因。</summary>
        string UnavailableReason { get; }

        /// <summary>
        /// 捕获一帧。
        /// <para>
        /// ★ 所有权：成功时 <paramref name="frame"/> 的所有权转移给调用方；
        ///   失败时 <paramref name="frame"/> 必须为 null（若有中间位图则一并交出，由调用方释放）。
        /// </para>
        /// <para>
        /// ★ <paramref name="timing"/> 用于回报"瞬时排除窗口"的时长
        ///   （<c>SetWindowDisplayAffinity</c> 生效期间无法捕获）—— 这是 M2 合同 §6.2
        ///   要求**并列记录**的两段之一。
        /// </para>
        /// </summary>
        /// <param name="request">不可变捕获请求（含物理矩形、代次、模糊半径、HWND）。</param>
        /// <param name="frame">捕获到的位图（所有权转移给调用方）。</param>
        /// <param name="timing">后端自报的分段耗时（经过时间口径）。</param>
        /// <returns>捕获到有效帧时为 true。</returns>
        bool TryCapture(BackdropServiceRequest request, out Image? frame, out BackdropServiceTiming timing);

        /// <summary>
        /// 恢复窗口的显示亲和性（把 <c>WDA_EXCLUDEFROMCAPTURE</c> 排除属性还原）。
        /// <para>
        /// ★ 合同 §5.2.3：**取消 / 失败后必须调用**，否则窗口会在录屏里永久消失。
        ///   实现必须**幂等**（可被重复调用）。
        /// </para>
        /// </summary>
        void RestoreDisplayAffinity();
    }

    /// <summary>
    /// 实时背景**图像处理**（降采样 + 物理模糊半径滤波）。
    ///
    /// ★ 所有权合同：
    /// 1. <paramref name="source"/> 是**借用**输入：所有权仍属调用方（服务）。
    /// 2. 输出的 <paramref name="processed"/> 是**调用方的新所有权**
    ///    （除非与输入是**同一实例**，此时所有权不变）。
    /// 3. 失败时必须返回 <c>false</c> 且输出为 null，并且**不得释放**输入。
    /// </summary>
    public interface IBackdropFrameProcessor
    {
        /// <summary>
        /// 处理一帧（降采样 + 按 <c>request.BlurPx</c> 做物理模糊）。
        /// </summary>
        /// <param name="request">不可变捕获请求（模糊半径等参数取自它）。</param>
        /// <param name="source">输入帧（借用，不得释放）。</param>
        /// <param name="processed">输出帧（所有权转移给调用方）。</param>
        /// <returns>处理成功时为 true。</returns>
        bool TryProcess(BackdropServiceRequest request, Image source, out Image? processed);
    }

    // ================================================================
    // 服务状态与测量
    // ================================================================

    /// <summary>捕获服务的运行状态（服务视角）。</summary>
    public enum BackdropServiceState
    {
        /// <summary>未启动 / 已停止。</summary>
        Stopped = 0,

        /// <summary>已启动，正在进行第一次捕获。</summary>
        Starting = 1,

        /// <summary>正常运行中（至少成功发布过一帧）。</summary>
        Running = 2,

        /// <summary>已暂停（窗口隐藏 / 最小化 ⇒ 不捕获）。</summary>
        Suspended = 3,

        /// <summary>降级中（后端不可用、几何无效等，尚未成功产出过帧）。</summary>
        Degraded = 4,

        /// <summary>故障中（捕获或滤波失败，正在按退避重试）。</summary>
        Faulted = 5,
    }

    /// <summary>
    /// 捕获服务的**可诊断状态**（只读快照）。
    ///
    /// ★ 合同 §7「降级不静默」：每次降级都带 <see cref="DegradeReason"/>（原因码）与
    ///   <see cref="DegradeReasonText"/>（可读原因）；<see cref="LastFailureReason"/>
    ///   保留**最后一次失败**的原因，即使后来恢复也不清空。
    /// </summary>
    public readonly struct BackdropCaptureStatus
    {
        internal BackdropCaptureStatus(
            BackdropServiceState state,
            string? stateText,
            bool isRunning,
            bool isReady,
            bool liveFrameAvailable,
            BackdropRenderMode effectiveMode,
            BackdropDegradeReason degradeReason,
            string? degradeReasonText,
            string? lastFailureReason,
            DateTime lastFailureUtc,
            DateTime lastFrameUtc,
            int consecutiveFailures,
            int targetFps,
            bool backendReady,
            string? backendUnavailableReason)
        {
            State = state;
            StateText = stateText;
            IsRunning = isRunning;
            IsReady = isReady;
            LiveFrameAvailable = liveFrameAvailable;
            EffectiveMode = effectiveMode;
            DegradeReason = degradeReason;
            DegradeReasonText = degradeReasonText;
            LastFailureReason = lastFailureReason;
            LastFailureUtc = lastFailureUtc;
            LastFrameUtc = lastFrameUtc;
            ConsecutiveFailures = consecutiveFailures;
            TargetFps = targetFps;
            BackendReady = backendReady;
            BackendUnavailableReason = backendUnavailableReason;
        }

        /// <summary>服务当前状态。</summary>
        public BackdropServiceState State { get; }

        /// <summary>
        /// 当前状态的说明文字（如"捕获服务已停止。"）。
        /// <para>★ 与 <see cref="DegradeReason"/> / <see cref="DegradeReasonText"/> **分列**：
        /// 降级原因码与降级原因文本永远成对出现（要么都指向一次降级，要么都为空），
        /// 状态说明只描述"现在处于什么状态"，不会冒充降级原因（合同 §7 可诊断性要求）。</para>
        /// </summary>
        public string? StateText { get; }

        /// <summary>后台任务是否在运行。</summary>
        public bool IsRunning { get; }

        /// <summary>是否有**可借出**的实时帧（与 <c>IBackdropFrameSource.IsReady</c> 同义）。</summary>
        public bool IsReady { get; }

        /// <summary>当前是否持有一帧实时底图。</summary>
        public bool LiveFrameAvailable { get; }

        /// <summary>
        /// 服务视角的**有效模式**。
        /// <para>
        /// ★ <see cref="BackdropRenderMode.Glass"/> = 当前有可用实时帧；
        ///   其余情况表示"实时材质未生效" —— 此时背景层实际回退到 gradient 还是 solid，
        ///   由 <c>BackdropModeResolver</c> 的降级链决定，**本服务不预测**。
        /// </para>
        /// </summary>
        public BackdropRenderMode EffectiveMode { get; }

        /// <summary>当前降级原因码（未降级时为 <see cref="BackdropDegradeReason.None"/>）。</summary>
        public BackdropDegradeReason DegradeReason { get; }

        /// <summary>当前降级原因文本（人类可读；未降级时为 null）。</summary>
        public string? DegradeReasonText { get; }

        /// <summary>最后一次失败的原因文本（恢复后仍保留；从未失败时为 null）。</summary>
        public string? LastFailureReason { get; }

        /// <summary>最后一次失败的 UTC 时刻。</summary>
        public DateTime LastFailureUtc { get; }

        /// <summary>最后一次成功发布帧的 UTC 时刻。</summary>
        public DateTime LastFrameUtc { get; }

        /// <summary>连续失败次数（成功一次即清零）。</summary>
        public int ConsecutiveFailures { get; }

        /// <summary>当前目标帧率。</summary>
        public int TargetFps { get; }

        /// <summary>捕获后端是否就绪。</summary>
        public bool BackendReady { get; }

        /// <summary>捕获后端自报的就绪原因（后端不可用时为可读文本）。</summary>
        public string? BackendUnavailableReason { get; }

        /// <summary>生成一行可写日志的诊断文本（会分配字符串，仅在显式调用时产生）。</summary>
        public string ToLogString()
        {
            return $"backdrop-capture state={State} running={(IsRunning ? "yes" : "no")}"
                 + $" ready={(IsReady ? "yes" : "no")} mode={EffectiveMode}"
                 + $" degraded={(DegradeReason != BackdropDegradeReason.None ? "yes" : "no")}"
                 + $" code={DegradeReason} fps={TargetFps} fails={ConsecutiveFailures}"
                 + $" backendReady={(BackendReady ? "yes" : "no")}"
                 + $" reason={DegradeReasonText ?? "-"} lastFailure={LastFailureReason ?? "-"}";
        }
    }

    /// <summary>
    /// 捕获服务的**测量快照**（只读）。
    ///
    /// ★ 口径分离（合同 §6.1 / §6.2）：
    /// 1. <c>*Ms</c> 里的分段耗时（captureMs / filterMs / publishMs / waitUiMs /
    ///    excludeWindowMs）与 <see cref="ElapsedMsP95"/> 都是**经过时间**口径
    ///    （<see cref="System.Diagnostics.Stopwatch"/> 墙钟）。
    /// 2. <see cref="CpuMsTotal"/> / <see cref="CpuMsPerFrameAvg"/> 是**处理器时间**口径
    ///    （<c>Process.TotalProcessorTime</c> 差值）。
    /// 3. **两者不得混用**：CPU 预算（≤40 ms/秒）只认 CPU 口径；
    ///    延迟判据（p95 ≤30 ms）只认经过时间口径。
    /// </summary>
    public readonly struct BackdropServiceMetrics
    {
        internal BackdropServiceMetrics(
            long framesCaptured,
            long framesPublished,
            long framesDroppedStale,
            long captureFailures,
            double lastCaptureMs,
            double lastFilterMs,
            double lastPublishMs,
            double lastWaitUiMs,
            double lastExcludeWindowMs,
            double avgCaptureMs,
            double avgFilterMs,
            double avgPublishMs,
            double avgWaitUiMs,
            double avgExcludeWindowMs,
            double maxWaitUiMs,
            double maxExcludeWindowMs,
            double cpuMsTotal,
            double cpuMsPerFrameAvg,
            double elapsedMsLast,
            double elapsedMsP95,
            double elapsedMsMax,
            double elapsedSinceStartMs)
        {
            FramesCaptured = framesCaptured;
            FramesPublished = framesPublished;
            FramesDroppedStale = framesDroppedStale;
            CaptureFailures = captureFailures;
            LastCaptureMs = lastCaptureMs;
            LastFilterMs = lastFilterMs;
            LastPublishMs = lastPublishMs;
            LastWaitUiMs = lastWaitUiMs;
            LastExcludeWindowMs = lastExcludeWindowMs;
            AvgCaptureMs = avgCaptureMs;
            AvgFilterMs = avgFilterMs;
            AvgPublishMs = avgPublishMs;
            AvgWaitUiMs = avgWaitUiMs;
            AvgExcludeWindowMs = avgExcludeWindowMs;
            MaxWaitUiMs = maxWaitUiMs;
            MaxExcludeWindowMs = maxExcludeWindowMs;
            CpuMsTotal = cpuMsTotal;
            CpuMsPerFrameAvg = cpuMsPerFrameAvg;
            ElapsedMsLast = elapsedMsLast;
            ElapsedMsP95 = elapsedMsP95;
            ElapsedMsMax = elapsedMsMax;
            ElapsedSinceStartMs = elapsedSinceStartMs;
        }

        /// <summary>成功捕获的帧数（捕获成功且滤波成功）。</summary>
        public long FramesCaptured { get; }

        /// <summary>成功发布（交给 UI 候选池）的帧数。</summary>
        public long FramesPublished { get; }

        /// <summary>因代次不匹配被**后台自己回收**的帧数（合同 §4.2.4）。</summary>
        public long FramesDroppedStale { get; }

        /// <summary>捕获/滤波失败次数。</summary>
        public long CaptureFailures { get; }

        /// <summary>最近一次捕获耗时（毫秒，经过时间口径）。</summary>
        public double LastCaptureMs { get; }

        /// <summary>最近一次滤波耗时（毫秒，经过时间口径）。</summary>
        public double LastFilterMs { get; }

        /// <summary>最近一次发布耗时（毫秒，经过时间口径，含代次校验）。</summary>
        public double LastPublishMs { get; }

        /// <summary>最近一次**等待 UI 接管**的时长（毫秒，经过时间口径）。</summary>
        public double LastWaitUiMs { get; }

        /// <summary>最近一次**瞬时排除窗口**的时长（毫秒，经过时间口径）。</summary>
        public double LastExcludeWindowMs { get; }

        /// <summary>捕获耗时均值（毫秒，经过时间口径）。</summary>
        public double AvgCaptureMs { get; }

        /// <summary>滤波耗时均值（毫秒，经过时间口径）。</summary>
        public double AvgFilterMs { get; }

        /// <summary>发布耗时均值（毫秒，经过时间口径）。</summary>
        public double AvgPublishMs { get; }

        /// <summary>等待 UI 接管时长均值（毫秒，经过时间口径）。</summary>
        public double AvgWaitUiMs { get; }

        /// <summary>瞬时排除窗口时长均值（毫秒，经过时间口径）。</summary>
        public double AvgExcludeWindowMs { get; }

        /// <summary>等待 UI 接管的最大时长（毫秒，经过时间口径）。</summary>
        public double MaxWaitUiMs { get; }

        /// <summary>瞬时排除窗口的最大时长（毫秒，经过时间口径）。</summary>
        public double MaxExcludeWindowMs { get; }

        /// <summary>
        /// 累计**处理器时间**（毫秒，CPU 口径）：捕获 + 滤波 + 发布三段的 CPU 时间合计。
        /// <para>
        /// ★ Windows 进程 CPU 时钟粒度约 15.6 ms，单轮差值不可直接采信：
        ///   一轮差值落在粒度以内时这里会记 0 —— 这表示**不可判定**，**不表示零开销**。
        ///   请在足够长的时间窗口内用 <c>CpuMsTotal / (ElapsedSinceStartMs / 1000)</c>
        ///   折算 ms/秒，再与合同 §6.3 的 40 ms/秒预算比对；
        ///   需要 old/new 配对比较时，请按合同 §6.1 的批量交替方法单独测量。
        /// </para>
        /// </summary>
        public double CpuMsTotal { get; }

        /// <summary>每帧平均处理器时间（毫秒，CPU 口径）。</summary>
        public double CpuMsPerFrameAvg { get; }

        /// <summary>最近一轮端到端**经过时间**（毫秒，含等待 UI 接管）。</summary>
        public double ElapsedMsLast { get; }

        /// <summary>端到端**经过时间**的 p95（毫秒；样本窗口 = 最近 128 轮）。</summary>
        public double ElapsedMsP95 { get; }

        /// <summary>端到端经过时间的最大值（毫秒，样本窗口内）。</summary>
        public double ElapsedMsMax { get; }

        /// <summary>服务启动至今的经过时间（毫秒）。</summary>
        public double ElapsedSinceStartMs { get; }

        /// <summary>生成一行可写日志的诊断文本（会分配字符串，仅在显式调用时产生）。</summary>
        public string ToLogString()
        {
            return $"backdrop-metrics captured={FramesCaptured} published={FramesPublished}"
                 + $" stale={FramesDroppedStale} failures={CaptureFailures}"
                 + $" captureMs={LastCaptureMs:0.##}/{AvgCaptureMs:0.##}"
                 + $" filterMs={LastFilterMs:0.##}/{AvgFilterMs:0.##}"
                 + $" publishMs={LastPublishMs:0.##}/{AvgPublishMs:0.##}"
                 + $" waitUiMs={LastWaitUiMs:0.##}/{AvgWaitUiMs:0.##}(max {MaxWaitUiMs:0.##})"
                 + $" excludeWindowMs={LastExcludeWindowMs:0.##}/{AvgExcludeWindowMs:0.##}(max {MaxExcludeWindowMs:0.##})"
                 + $" cpuMsTotal={CpuMsTotal:0.##} elapsedP95={ElapsedMsP95:0.##}";
        }
    }

    // ================================================================
    // 帧租约（UI 接管后的所有权凭证）
    // ================================================================

    /// <summary>
    /// 一帧的**租约**：UI 接管实时帧之后拿到的所有权凭证。
    ///
    /// ★ 所有权合同（合同 §4.1 / §4.2）：
    /// 1. 租约由 <see cref="BackdropCaptureService.TryAcquireFrame"/> 创建 ——
    ///    那是一次**一次性所有权转移**：从那一刻起后台对该帧**无任何权利**，
    ///    永远不会去释放它。
    /// 2. **只有租约持有者（UI）能释放位图**：调用 <see cref="Dispose"/>。
    ///    UI 应在**本次绘制结束、旧帧不再被任何 Paint 使用之后**再释放旧租约
    ///    （合同 §4.2.2）。
    /// 3. <see cref="Invalidate"/> 只作废"可借用"状态（不再被服务提供），
    ///    **不释放**位图 —— 释放权仍然在 UI 手里。
    /// </summary>
    public sealed class BackdropFrameLease : IDisposable
    {
        private Image? _frame;
        private readonly BackdropCaptureService? _owner;
        private int _invalidated;
        private int _disposed;

        internal BackdropFrameLease(BackdropCaptureService owner, Image frame, BackdropServiceRequest request)
        {
            _owner = owner;
            _frame = frame;
            Request = request;
        }

        /// <summary>该帧对应的捕获请求（含代次，接管时的那一份）。</summary>
        public BackdropServiceRequest Request { get; }

        /// <summary>帧位图（所有权属于本租约）。已释放时抛 <see cref="ObjectDisposedException"/>。</summary>
        public Image Frame =>
            Volatile.Read(ref _frame) ?? throw new ObjectDisposedException(nameof(BackdropFrameLease));

        /// <summary>
        /// 帧位图（可空版本：已释放时返回 null）。
        /// <para>★ 服务内部用它做"是否还有可借出的帧"判断，避免在已释放的租约上抛异常。</para>
        /// </summary>
        internal Image? FrameOrNull => Volatile.Read(ref _frame);

        /// <summary>是否已作废（代次变化 / 暂停 / 停止）。</summary>
        public bool IsInvalidated => Volatile.Read(ref _invalidated) != 0;

        /// <summary>是否已释放。</summary>
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>当前是否仍然可用（未释放、未作废、位图仍在）。</summary>
        public bool IsValid =>
            Volatile.Read(ref _disposed) == 0
            && Volatile.Read(ref _invalidated) == 0
            && Volatile.Read(ref _frame) is not null;

        /// <summary>
        /// 作废本帧（服务不再把它借给绘制路径）。
        /// <para>★ 只作废、**不释放** —— 位图的所有权仍在 UI 手里，必须由 UI 释放。</para>
        /// </summary>
        public void Invalidate() => Interlocked.Exchange(ref _invalidated, 1);

        /// <summary>
        /// 释放本帧的位图（UI 侧唯一的释放入口）。可重复调用。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            Image? image = Interlocked.Exchange(ref _frame, null);
            if (image is not null) image.Dispose();

            _owner?.NotifyLeaseDisposed(this);
        }

        /// <summary>
        /// 兜底终结器：UI 若忘了 <see cref="Dispose"/>，最终仍会释放位图（非确定性）。
        /// <para>★ 终结器线程**不回调服务**（避免在终结器里取服务锁）。</para>
        /// </summary>
        ~BackdropFrameLease()
        {
            Image? image = Interlocked.Exchange(ref _frame, null);
            image?.Dispose();
        }
    }

    // ================================================================
    // 后台捕获服务
    //
    // ★ 命名说明（本包统一）：服务侧类型一律用 "BackdropService*" 前缀 ——
    //   同命名空间下已有 ScreenCaptureFrameSource.cs 定义的
    //   BackdropCaptureRequest / BackdropCaptureState / BackdropCaptureMetrics，
    //   重名会直接编译失败，故本包整体改名避让（**不改动对方的文件**）。
    // ================================================================

    /// <summary>
    /// 实时背景**捕获服务**（M2 / A15）：把"不可变捕获请求"变成"可被 UI 接管的帧"。
    ///
    /// ★ 合同要点（M2 合同 §4 / §5 / §6 / §7）：
    /// 1. **单后台任务**：只有一个后台线程按目标帧率节流地捕获（默认 2 fps、上限 6 fps），
    ///    不是"每帧一个任务"。
    /// 2. **帧交接**：后台只**发布候选（pending）**，绝不释放 UI 正在绘制的位图；
    ///    pending **最多一份**；UI 通过 <see cref="TryAcquireFrame"/> 接管，
    ///    接管是**一次性所有权转移**，转移后后台对该帧无任何权利。
    /// 3. **代次校验**：代次不匹配的帧由**后台自己回收**，绝不丢给 UI。
    /// 4. **失败路径不泄漏**：捕获失败 / 滤波失败 / 取消 / 句柄失效 / 超时，
    ///    都释放"已创建但未发布"的中间位图。
    /// 5. **取消有上限**：停止可等待但有超时；取消/失败后**恢复显示亲和性**；
    ///    失败按退避重试，**不在绘制路径里重试**。
    /// 6. **绘制路径绝不抓屏**：<see cref="TryGetFrame"/> 只读当前帧，不启动捕获。
    /// 7. **两种口径不混用**：分段耗时是经过时间，CPU 预算是处理器时间。
    /// </summary>
    public sealed class BackdropCaptureService : IBackdropFrameSource, IDisposable
    {
        // ============================================================
        // 常量
        // ============================================================

        /// <summary>等待 UI 接管时的单次等待片（毫秒）—— 保证取消能被及时看到。</summary>
        private const int TakeoverWaitSliceMs = 50;

        /// <summary>经过时间 p95 的样本窗口长度（轮）。</summary>
        private const int ElapsedSampleCapacity = 128;

        /// <summary>失败退避的起始时长（毫秒）。</summary>
        private const int RetryBackoffBaseMs = 500;

        /// <summary>失败退避的最长时长（毫秒）。</summary>
        private const int RetryBackoffMaxMs = 5000;

        /// <summary>默认等待 UI 接管的上限（毫秒）。实际取"与帧间隔的较小者"。</summary>
        private const double DefaultTakeoverTimeoutMs = 1000d;

        /// <summary>单帧像素上限（合同 §6.3：初始捕获面积预算 2,000,000 物理像素）。</summary>
        public const long DefaultMaxFramePixels = 2_000_000L;

        /// <summary>单帧字节上限（合同 §6.3：帧缓冲预算 32MiB）。</summary>
        public const long DefaultMaxFrameBytes = 32L * 1024 * 1024;

        // ============================================================
        // 依赖与固定资源
        // ============================================================

        private readonly IBackdropCaptureBackend _backend;
        private readonly IBackdropFrameProcessor? _processor;
        private readonly Process _process;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        /// <summary>帧交接锁：保护 pending / activeLease / 请求切换。</summary>
        private readonly object _gate = new();

        /// <summary>状态与指标锁（与帧交接锁分离，避免交接等待期间阻塞状态读取）。</summary>
        private readonly object _metricsGate = new();

        private readonly double _takeoverTimeoutMs;

        /// <summary>单帧像素上限（缓冲预算闸门，合同 §6.3）。</summary>
        private readonly long _maxFramePixels;

        /// <summary>单帧字节上限（缓冲预算闸门，合同 §6.3）。</summary>
        private readonly long _maxFrameBytes;

        // ============================================================
        // 帧交接状态（受 _gate 保护）
        // ============================================================

        /// <summary>当前请求快照（不可变对象，用 Volatile 读写）。</summary>
        private BackdropServiceRequest? _request;

        /// <summary>候选帧（**后台拥有**；同一时刻最多一份）。</summary>
        private Image? _pending;

        /// <summary>候选帧对应的请求（用于代次校验）。</summary>
        private BackdropServiceRequest? _pendingRequest;

        /// <summary>已接管的当前帧（**UI 拥有**；服务对它只有"不再提供"的权力）。</summary>
        private BackdropFrameLease? _activeLease;

        private bool _disposed;

        // ============================================================
        // 线程与运行状态
        // ============================================================

        private CancellationTokenSource? _cts;
        private Thread? _worker;
        private int _suspended;

        /// <summary>已请求停止（Interlocked 标记）：防止"卡住的后端"在停止后仍发布帧。</summary>
        private int _stopRequested;

        /// <summary>失败退避的到期时刻（毫秒，_clock 口径）—— 只有后台线程读写。</summary>
        private double _retryNotBeforeMs;

        // ============================================================
        // 状态与指标（受 _metricsGate 保护）
        // ============================================================

        private BackdropServiceState _state = BackdropServiceState.Stopped;
        private string? _stateText;
        private BackdropDegradeReason _degradeReason = BackdropDegradeReason.None;
        private string? _degradeReasonText;
        private string? _lastFailureReason;
        private DateTime _lastFailureUtc = DateTime.MinValue;
        private DateTime _lastFrameUtc = DateTime.MinValue;
        private int _consecutiveFailures;

        private long _framesCaptured;
        private long _framesPublished;

        /// <summary>因代次不匹配被后台自己回收的帧数（Interlocked 原子计数，不受 _metricsGate 保护）。</summary>
        private long _framesDroppedStale;

        private long _captureFailures;
        private double _sumCaptureMs;
        private double _sumFilterMs;
        private double _sumPublishMs;
        private double _sumWaitUiMs;
        private double _sumExcludeWindowMs;
        private double _lastCaptureMs;
        private double _lastFilterMs;
        private double _lastPublishMs;
        private double _lastWaitUiMs;
        private double _lastExcludeWindowMs;
        private double _maxWaitUiMs;
        private double _maxExcludeWindowMs;
        private double _cpuMsTotal;
        private double _elapsedLastMs;
        private double _elapsedMaxMs;
        private readonly double[] _elapsedRing = new double[ElapsedSampleCapacity];
        private int _elapsedRingCount;
        private int _elapsedRingIndex;

        // ============================================================
        // 构造
        // ============================================================

        /// <summary>
        /// 创建捕获服务。
        /// </summary>
        /// <param name="backend">捕获后端（**注入**，服务不 new 任何具体实现）。</param>
        /// <param name="processor">图像处理（可空）。为 null 时直接发布捕获到的原始帧（不滤波）。</param>
        /// <param name="takeoverTimeout">等待 UI 接管的单轮上限（毫秒，默认 1000，实际与帧间隔取小）。</param>
        /// <param name="maxFramePixels">单帧像素上限（合同 §6.3：初始捕获面积预算 2,000,000 物理像素）。</param>
        /// <param name="maxFrameBytes">单帧字节上限（合同 §6.3：帧缓冲预算 32MiB）。</param>
        public BackdropCaptureService(
            IBackdropCaptureBackend backend,
            IBackdropFrameProcessor? processor = null,
            double takeoverTimeoutMs = DefaultTakeoverTimeoutMs,
            long maxFramePixels = DefaultMaxFramePixels,
            long maxFrameBytes = DefaultMaxFrameBytes)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _processor = processor;
            _takeoverTimeoutMs = takeoverTimeoutMs > 0d ? takeoverTimeoutMs : DefaultTakeoverTimeoutMs;
            _maxFramePixels = maxFramePixels > 0 ? maxFramePixels : DefaultMaxFramePixels;
            _maxFrameBytes = maxFrameBytes > 0 ? maxFrameBytes : DefaultMaxFrameBytes;
            _process = Process.GetCurrentProcess();
        }

        // ============================================================
        // 生命周期
        // ============================================================

        /// <summary>后台任务是否在运行。</summary>
        public bool IsRunning
        {
            get
            {
                Thread? worker = _worker;
                return worker is not null && worker.IsAlive;
            }
        }

        /// <summary>当前请求快照（可空 = 尚未启动）。</summary>
        public BackdropServiceRequest? CurrentRequest => Volatile.Read(ref _request);

        /// <summary>候选帧数量（合同 §9.5 的探针断言用：恒为 0 或 1）。</summary>
        public int PendingFrameCount
        {
            get
            {
                lock (_gate) return _pending is null ? 0 : 1;
            }
        }

        /// <summary>是否持有已接管的当前帧。</summary>
        public bool HasActiveFrame
        {
            get
            {
                lock (_gate) return _activeLease is not null;
            }
        }

        /// <summary>
        /// 启动捕获服务（**单后台任务**）。
        /// <para>
        /// ★ 只能在"主题请求 glass 且玻璃开关已开启"时调用（合同 §5.1）；
        ///   非 glass 场景根本不启动本服务。
        /// </para>
        /// </summary>
        /// <param name="request">初始捕获请求（不可变快照）。</param>
        public void Start(BackdropServiceRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));

            CancellationTokenSource cts;
            Thread worker;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_worker is not null)
                {
                    throw new InvalidOperationException("捕获服务已在运行；请先调用 StopAsync()。");
                }

                Volatile.Write(ref _request, request);
                cts = new CancellationTokenSource();
                _cts = cts;
                Interlocked.Exchange(ref _stopRequested, 0);
                worker = new Thread(() => WorkerLoop(cts.Token))
                {
                    IsBackground = true,
                    Name = "LiteMonitor.BackdropCapture",
                };
                _worker = worker;
            }

            SetState(BackdropServiceState.Starting, "正在启动实时捕获……");
            worker.Start();
        }

        /// <summary>
        /// 更新当前请求（换肤 / 尺寸变化 / 句柄重建时调用）。
        /// <para>
        /// ★ 代次变化时：已接管的当前帧**作废**（不再提供给绘制路径），
        ///   未接管的候选帧由后台**自己回收**（合同 §4.2.4）。
        ///   已接管的位图**不释放** —— 它的所有权在 UI 手里（合同 §4.2.3）。
        /// </para>
        /// </summary>
        public void UpdateRequest(BackdropServiceRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                BackdropServiceRequest? previous = Volatile.Read(ref _request);
                Volatile.Write(ref _request, request);

                if (previous is null || previous.MatchesGeneration(request)) return;

                _activeLease?.Invalidate();

                if (_pending is not null)
                {
                    Image? stale = _pending;
                    _pending = null;
                    _pendingRequest = null;
                    stale.Dispose();
                }
            }
        }

        /// <summary>
        /// 暂停 / 恢复捕获（窗口隐藏 / 最小化 ⇒ 暂停）。
        /// <para>
        /// ★ 暂停期间**不捕获**；恢复时**合并为一帧**（立即捕获一次，不补历史帧、不追帧）。
        /// </para>
        /// </summary>
        public void SetSuspended(bool suspended)
        {
            Interlocked.Exchange(ref _suspended, suspended ? 1 : 0);
            if (!suspended) return;

            lock (_gate)
            {
                _activeLease?.Invalidate();

                if (_pending is not null)
                {
                    Image? stale = _pending;
                    _pending = null;
                    _pendingRequest = null;
                    stale.Dispose();
                }
            }

            SetState(BackdropServiceState.Suspended, "窗口不可见，已暂停捕获。");
        }

        /// <summary>
        /// 停止服务：取消后台任务（有上限等待）、恢复显示亲和性、同步释放后台仍拥有的位图。
        /// <para>
        /// ★ 合同 §5.2.2：取消**必须可等待但有上限**，不阻塞 UI 无限等。
        /// ★ 合同 §5.2.3：取消后**必须恢复显示亲和性**。
        /// ★ 合同 §4.2.3：已被 UI 接管的帧**不由本服务释放**，须由 UI 释放自己的租约。
        /// </para>
        /// </summary>
        /// <param name="timeout">等待后台任务退出的上限。</param>
        /// <returns>后台任务是否在上限内退出。</returns>
        public Task<bool> StopAsync(TimeSpan timeout) => Task.Run(() => StopCore(timeout));

        /// <summary>
        /// 停止服务（同步版本，供退出流程使用）。
        /// </summary>
        public bool Stop(TimeSpan timeout) => StopCore(timeout);

        /// <summary>释放本服务（停止后台任务并释放自持资源）。</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
            }

            StopCore(TimeSpan.FromSeconds(2));

            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _activeLease?.Invalidate();
            }

            GC.SuppressFinalize(this);
        }

        private bool StopCore(TimeSpan timeout)
        {
            CancellationTokenSource? cts;
            Thread? worker;
            lock (_gate)
            {
                cts = _cts;
                worker = _worker;
                _cts = null;
                _worker = null;
            }

            try { cts?.Cancel(); } catch (ObjectDisposedException) { /* 已释放 */ }

            // ★ 兜底：即使后端卡住、超时未退出，也不允许它把"迟到的帧"发布出去
            Interlocked.Exchange(ref _stopRequested, 1);

            bool stopped = true;
            if (worker is not null && worker.IsAlive)
            {
                try
                {
                    stopped = worker.Join(timeout);
                }
                catch (ThreadStateException)
                {
                    stopped = false;
                }
            }

            try { cts?.Dispose(); } catch (ObjectDisposedException) { /* 已释放 */ }

            // ★ 取消后必须恢复显示亲和性（否则窗口在录屏里永久消失）
            RestoreAffinitySafely();

            // ★ 同步释放后台仍拥有的候选帧（合同 §4.2.6）
            lock (_gate)
            {
                if (_pending is not null)
                {
                    Image? stale = _pending;
                    _pending = null;
                    _pendingRequest = null;
                    stale.Dispose();
                }
            }

            SetState(BackdropServiceState.Stopped, "捕获服务已停止。");
            return stopped;
        }

        // ============================================================
        // 帧交接（UI 线程）
        // ============================================================

        /// <summary>
        /// 接管候选帧（**一次性所有权转移**）。
        /// <para>
        /// ★ 必须由 **UI 线程**调用（与绘制路径同线程 ⇒ 不会出现"后台释放了正在画的帧"）。
        /// ★ 接管成功后，调用方**必须**在旧帧不再被任何 Paint 使用之后释放旧租约，
        ///   并最终释放本租约（<see cref="BackdropFrameLease.Dispose"/>）。
        /// ★ 没有可接管的帧时返回 false（不抛异常、不阻塞）。
        /// </para>
        /// </summary>
        /// <param name="lease">接管到的租约（未接管到时为 null）。</param>
        public bool TryAcquireFrame(out BackdropFrameLease? lease)
        {
            lease = null;

            lock (_gate)
            {
                if (_disposed) return false;

                Image? frame = _pending;
                if (frame is null) return false;

                BackdropServiceRequest? pendingRequest = _pendingRequest;
                _pending = null;
                _pendingRequest = null;

                BackdropServiceRequest? current = Volatile.Read(ref _request);
                if (pendingRequest is null || current is null || !pendingRequest.MatchesGeneration(current))
                {
                    // ★ 代次不匹配：后台自己回收，绝不丢给 UI（合同 §4.2.4）
                    frame.Dispose();
                    Interlocked.Increment(ref _framesDroppedStale);
                    Monitor.PulseAll(_gate);
                    return false;
                }

                // ★ 接管：从此刻起后台对该帧**无任何权利**（合同 §4.2.3）
                lease = new BackdropFrameLease(this, frame, pendingRequest);
                _activeLease = lease;

                Monitor.PulseAll(_gate);
                return true;
            }
        }

        /// <summary>
        /// 释放一个租约（= <see cref="BackdropFrameLease.Dispose"/> 的空安全包装，便于统一接线）。
        /// </summary>
        public void ReleaseFrame(BackdropFrameLease? lease) => lease?.Dispose();

        // ============================================================
        // IBackdropFrameSource（绘制路径的只读借用接口）
        // ============================================================

        /// <summary>
        /// 是否有一帧可以借出（与 <see cref="Status"/> 的 IsReady 同义）。
        /// </summary>
        public bool IsReady
        {
            get
            {
                lock (_gate)
                {
                    return _activeLease is not null && _activeLease.FrameOrNull is not null;
                }
            }
        }

        /// <summary>
        /// 不可用原因（人类可读；可用时为空串）。
        /// <para>★ 合同 §7：降级必须**不静默** —— 这里永远给出可读原因。</para>
        /// </summary>
        public string UnavailableReason
        {
            get
            {
                BackdropCaptureStatus status = Status;
                if (status.IsReady) return string.Empty;
                if (!string.IsNullOrEmpty(status.DegradeReasonText)) return status.DegradeReasonText!;
                if (!string.IsNullOrEmpty(status.BackendUnavailableReason)) return status.BackendUnavailableReason!;
                return "尚未接管到实时帧（后台捕获还没产出可用帧）。";
            }
        }

        /// <summary>
        /// 本服务提供的永远是**实时捕获帧**，不是预览固定图（合同 §2.2.1）。
        /// </summary>
        public bool IsPreview => false;

        /// <summary>
        /// 取当前帧（**借用**：调用方与渲染器都**不得释放**它）。
        /// <para>
        /// ★ 合同 §5.2.5 硬红线：本方法**只读当前帧，绝不启动抓屏**。
        ///   模糊在捕获/滤波阶段已完成，<paramref name="blurPx"/> 仅作接口兼容，不在此处处理。
        /// ★ 必须在 UI 线程调用（与租约释放同线程 ⇒ 不会出现 use-after-dispose）。
        /// </para>
        /// </summary>
        /// <param name="blurPx">要求的模糊半径（物理像素）—— 本实现忽略（已在捕获阶段应用）。</param>
        /// <param name="frame">当前帧（借用）。没有可用帧时为 null。</param>
        public bool TryGetFrame(double blurPx, out Image? frame)
        {
            frame = null;

            lock (_gate)
            {
                BackdropFrameLease? lease = _activeLease;
                if (lease is null) return false;

                Image? image = lease.FrameOrNull;
                if (image is null) return false;

                frame = image;
                return true;
            }
        }

        // ============================================================
        // 状态与指标
        // ============================================================

        /// <summary>当前可诊断状态（只读快照）。</summary>
        public BackdropCaptureStatus Status
        {
            get
            {
                // ★ 先取"是否有可借出帧"，**不与 _metricsGate 嵌套**（避免两把锁的顺序依赖）
                bool liveFrame;
                lock (_gate)
                {
                    liveFrame = _activeLease is not null && _activeLease.FrameOrNull is not null;
                }

                lock (_metricsGate)
                {
                    bool backendReady = SafeBackendIsReady();

                    return new BackdropCaptureStatus(
                        _state,
                        _stateText,
                        IsRunning,
                        liveFrame,
                        liveFrame,
                        liveFrame ? BackdropRenderMode.Glass : BackdropRenderMode.Solid,
                        _degradeReason,
                        _degradeReasonText,
                        _lastFailureReason,
                        _lastFailureUtc,
                        _lastFrameUtc,
                        _consecutiveFailures,
                        Volatile.Read(ref _request)?.TargetFps ?? BackdropServiceRequest.DefaultTargetFps,
                        backendReady,
                        backendReady ? null : SafeBackendUnavailableReason());
                }
            }
        }

        /// <summary>当前测量快照（只读）。</summary>
        public BackdropServiceMetrics Metrics
        {
            get
            {
                lock (_metricsGate)
                {
                    long samples = _framesCaptured;
                    double avgCapture = samples > 0 ? _sumCaptureMs / samples : 0d;
                    double avgFilter = samples > 0 ? _sumFilterMs / samples : 0d;
                    double avgPublish = samples > 0 ? _sumPublishMs / samples : 0d;
                    double avgWait = samples > 0 ? _sumWaitUiMs / samples : 0d;
                    double avgExclude = samples > 0 ? _sumExcludeWindowMs / samples : 0d;

                    return new BackdropServiceMetrics(
                        _framesCaptured,
                        _framesPublished,
                        Interlocked.Read(ref _framesDroppedStale),
                        _captureFailures,
                        _lastCaptureMs,
                        _lastFilterMs,
                        _lastPublishMs,
                        _lastWaitUiMs,
                        _lastExcludeWindowMs,
                        avgCapture,
                        avgFilter,
                        avgPublish,
                        avgWait,
                        avgExclude,
                        _maxWaitUiMs,
                        _maxExcludeWindowMs,
                        _cpuMsTotal,
                        samples > 0 ? _cpuMsTotal / samples : 0d,
                        _elapsedLastMs,
                        ComputeElapsedP95(),
                        _elapsedMaxMs,
                        _clock.Elapsed.TotalMilliseconds);
                }
            }
        }

        private double ComputeElapsedP95()
        {
            int count = _elapsedRingCount;
            if (count <= 0) return 0d;

            double[] copy = new double[count];
            Array.Copy(_elapsedRing, copy, count);
            Array.Sort(copy);

            int index = (int)Math.Ceiling(0.95 * count) - 1;
            if (index < 0) index = 0;
            if (index >= count) index = count - 1;
            return copy[index];
        }

        // ============================================================
        // 后台任务
        // ============================================================

        private void WorkerLoop(CancellationToken ct)
        {
            double nextDueMs = 0d;

            while (!ct.IsCancellationRequested)
            {
                BackdropServiceRequest? request = Volatile.Read(ref _request);
                if (request is null)
                {
                    WaitOrCancel(ct, 100);
                    continue;
                }

                if (Interlocked.CompareExchange(ref _suspended, 0, 0) != 0)
                {
                    // ★ 隐藏 / 最小化：不捕获；恢复后合并为一帧（nextDue 归零 ⇒ 立即捕获一次）
                    nextDueMs = 0d;
                    WaitOrCancel(ct, 100);
                    continue;
                }

                double nowMs = _clock.Elapsed.TotalMilliseconds;
                if (nowMs < nextDueMs)
                {
                    WaitOrCancel(ct, (int)Math.Min(Math.Max(nextDueMs - nowMs, 1d), 100d));
                    continue;
                }

                if (nowMs < _retryNotBeforeMs)
                {
                    // ★ 失败退避：不在每次绘制里无限重试（合同 §5.2.4）
                    WaitOrCancel(ct, (int)Math.Min(Math.Max(_retryNotBeforeMs - nowMs, 1d), 100d));
                    continue;
                }

                RunOneCapture(request, ct);

                // ★ 不追帧：下一次到期时间从"本轮结束"起算 ⇒ 不会突发补帧
                nextDueMs = _clock.Elapsed.TotalMilliseconds + request.FrameIntervalMs;
            }

            SetState(BackdropServiceState.Stopped, "捕获服务已停止。");
        }

        private void RunOneCapture(BackdropServiceRequest request, CancellationToken ct)
        {
            if (!request.HasValidBounds)
            {
                RecordFailure(BackdropDegradeReason.InvalidBounds, "客户区物理矩形无效，已跳过捕获。", false);
                return;
            }

            if (!SafeBackendIsReady())
            {
                RecordFailure(
                    BackdropDegradeReason.BackendNotReady,
                    SafeBackendUnavailableReason() ?? "捕获后端未就绪。",
                    false);
                RestoreAffinitySafely();
                return;
            }

            // ★ CPU 口径起点（处理器时间）；等待 UI 接管段不计入（它几乎不消耗 CPU）
            TimeSpan cpuStart = _process.TotalProcessorTime;

            // --------------------------------------------------------
            // 捕获
            // --------------------------------------------------------
            double t0 = _clock.Elapsed.TotalMilliseconds;
            Image? raw = null;
            BackdropServiceTiming timing = BackdropServiceTiming.Empty;
            bool captured;
            try
            {
                captured = _backend.TryCapture(request, out raw, out timing);
            }
            catch (Exception ex)
            {
                captured = false;
                raw = null;
                RecordFailure(BackdropDegradeReason.BackendFrameInvalid, "捕获异常：" + ex.Message, true);
                return;
            }

            double captureMs = _clock.Elapsed.TotalMilliseconds - t0;

            if (!captured || raw is null)
            {
                // ★ 失败路径：释放"已创建但未发布"的中间位图（合同 §4.2.6）
                raw?.Dispose();
                RecordFailure(
                    BackdropDegradeReason.BackendFrameInvalid,
                    "捕获失败：后端没有给出可用帧。",
                    true);
                return;
            }

            // --------------------------------------------------------
            // 滤波（降采样 + 物理模糊半径）
            // --------------------------------------------------------
            double t1 = _clock.Elapsed.TotalMilliseconds;
            Image? processed = raw;
            if (_processor is not null)
            {
                bool filtered;
                try
                {
                    filtered = _processor.TryProcess(request, raw, out processed);
                }
                catch (Exception ex)
                {
                    filtered = false;
                    processed = null;
                    RecordFailure(BackdropDegradeReason.BackendFrameInvalid, "滤波异常：" + ex.Message, true);
                    raw.Dispose();
                    return;
                }

                if (!filtered || processed is null)
                {
                    processed = null;
                    // ★ 滤波失败：释放输入位图（合同 §4.2.6）
                    raw.Dispose();
                    RecordFailure(
                        BackdropDegradeReason.BackendFrameInvalid,
                        "滤波失败：没有得到可用的处理结果。",
                        true);
                    return;
                }

                if (!ReferenceEquals(processed, raw))
                {
                    // 输出是新位图 ⇒ 输入由本服务释放
                    raw.Dispose();
                    raw = null;
                }
            }

            double filterMs = _clock.Elapsed.TotalMilliseconds - t1;

            // --------------------------------------------------------
            // 缓冲预算闸门（合同 §6.3：初始捕获面积预算 2,000,000 物理像素、帧缓冲预算 32MiB）
            // ★ 放在**发布之前**：超预算的帧一律不进入 pending，也不交给 UI。
            //   这一道闸门在服务层，与是否配置了 processor 无关 ——
            //   否则 processor == null 时会照单全收超限帧（A15 复核发现的缺口）。
            // ★ 尺寸必须在释放**之前**读取：位图释放后读 Width/Height 会抛异常。
            // --------------------------------------------------------
            int frameWidth = processed!.Width;
            int frameHeight = processed.Height;
            long framePixels = (long)frameWidth * frameHeight;
            long frameBytes = framePixels * 4;   // 32bpp 估计
            // ★ 两条限制里**像素是约束性的**：2,000,000 px × 4B = 7.6MiB，永远低于 32MiB 上限。
            //   字节闸门是兜底（防止将来放宽像素预算时踩到缓冲上限）；按 4B 估算在 48bpp HDR 下偏小，
            //   但最坏情况 2,000,000 px × 6B = 11.4MiB 仍远在 32MiB 之内，不会误拒。
            if (framePixels > _maxFramePixels || frameBytes > _maxFrameBytes)
            {
                processed.Dispose();
                RecordFailure(
                    BackdropDegradeReason.BackendFrameInvalid,
                    $"捕获帧 {frameWidth}×{frameHeight}（{framePixels} 像素 / 约 {frameBytes / 1024 / 1024}MiB）" +
                    $"超出缓冲预算（{_maxFramePixels} 像素 / {_maxFrameBytes / 1024 / 1024}MiB），本帧已丢弃。",
                    true);
                return;
            }

            // --------------------------------------------------------
            // 发布（代次校验 + pending 替换）
            // --------------------------------------------------------
            double t2 = _clock.Elapsed.TotalMilliseconds;
            bool published = PublishFrame(processed!, request);
            double publishMs = _clock.Elapsed.TotalMilliseconds - t2;

            // --------------------------------------------------------
            // 等待 UI 接管（测量 waitUiMs）
            // --------------------------------------------------------
            double waitMs = WaitForTakeover(ct, request);

            // ★ CPU 口径终点：只覆盖"捕获 + 滤波 + 发布"三段
            double cpuMs = (_process.TotalProcessorTime - cpuStart).TotalMilliseconds;

            RecordSuccess(
                captureMs,
                filterMs,
                publishMs,
                waitMs,
                timing.ExcludeWindowMs,
                cpuMs,
                captureMs + filterMs + publishMs + waitMs,
                published);
        }

        /// <summary>
        /// 发布候选帧。
        /// <para>
        /// ★ 合同 §4.2.5：pending **最多一份** —— 新帧产生时，若旧 pending 尚未被接管，
        ///   后台**先释放旧的**再发布新的。
        /// ★ 合同 §4.2.4：代次不匹配的帧由**发布方自己回收**，不交给 UI。
        /// </para>
        /// </summary>
        private bool PublishFrame(Image frame, BackdropServiceRequest request)
        {
            lock (_gate)
            {
                if (_disposed || Volatile.Read(ref _stopRequested) != 0)
                {
                    // 已停止：不再发布（迟到的帧由本服务释放）
                    frame.Dispose();
                    return false;
                }

                BackdropServiceRequest? current = Volatile.Read(ref _request);
                if (current is null || !request.MatchesGeneration(current))
                {
                    frame.Dispose();
                    Interlocked.Increment(ref _framesDroppedStale);
                    return false;
                }

                if (_pending is not null)
                {
                    Image? stale = _pending;
                    _pending = null;
                    _pendingRequest = null;
                    stale.Dispose();
                }

                _pending = frame;
                _pendingRequest = request;

                Monitor.PulseAll(_gate);
                return true;
            }
        }

        /// <summary>
        /// 等待 UI 接管候选帧（有上限）。
        /// <para>
        /// ★ 这是后台唯一的"阻塞"来源（合同 §6.2 的 <c>waitUiMs</c>）：
        ///   后台**主动等** UI 来取，避免生产无人消费的帧（浪费 CPU），
        ///   同时天然把 pending 压到 ≤1 份。
        /// ★ 等待上限 = min(帧间隔, 接管超时) ⇒ 不会拖慢下一次捕获。
        /// </para>
        /// </summary>
        private double WaitForTakeover(CancellationToken ct, BackdropServiceRequest request)
        {
            double budgetMs = Math.Min(request.FrameIntervalMs, _takeoverTimeoutMs);

            lock (_gate)
            {
                if (_pending is null) return 0d;

                double start = _clock.Elapsed.TotalMilliseconds;
                while (_pending is not null && !ct.IsCancellationRequested)
                {
                    double remain = budgetMs - (_clock.Elapsed.TotalMilliseconds - start);
                    if (remain <= 0d) break;
                    Monitor.Wait(_gate, (int)Math.Min(remain, TakeoverWaitSliceMs));
                }

                return _clock.Elapsed.TotalMilliseconds - start;
            }
        }

        // ============================================================
        // 内部记账
        // ============================================================

        private void RecordSuccess(
            double captureMs,
            double filterMs,
            double publishMs,
            double waitMs,
            double excludeWindowMs,
            double cpuMs,
            double elapsedMs,
            bool published)
        {
            lock (_metricsGate)
            {
                _framesCaptured++;
                if (published) _framesPublished++;

                _lastCaptureMs = captureMs;
                _lastFilterMs = filterMs;
                _lastPublishMs = publishMs;
                _lastWaitUiMs = waitMs;
                _lastExcludeWindowMs = excludeWindowMs;

                _sumCaptureMs += captureMs;
                _sumFilterMs += filterMs;
                _sumPublishMs += publishMs;
                _sumWaitUiMs += waitMs;
                _sumExcludeWindowMs += excludeWindowMs;

                if (waitMs > _maxWaitUiMs) _maxWaitUiMs = waitMs;
                if (excludeWindowMs > _maxExcludeWindowMs) _maxExcludeWindowMs = excludeWindowMs;

                _cpuMsTotal += cpuMs;

                _elapsedLastMs = elapsedMs;
                if (elapsedMs > _elapsedMaxMs) _elapsedMaxMs = elapsedMs;
                _elapsedRing[_elapsedRingIndex] = elapsedMs;
                _elapsedRingIndex = (_elapsedRingIndex + 1) % _elapsedRing.Length;
                if (_elapsedRingCount < _elapsedRing.Length) _elapsedRingCount++;

                _consecutiveFailures = 0;
                _retryNotBeforeMs = 0d;
                _lastFrameUtc = DateTime.UtcNow;
                _state = BackdropServiceState.Running;
                _degradeReason = BackdropDegradeReason.None;
                _degradeReasonText = null;
            }
        }

        private void RecordFailure(BackdropDegradeReason reason, string text, bool backendFailure)
        {
            int failures;
            lock (_metricsGate)
            {
                _consecutiveFailures++;
                failures = _consecutiveFailures;
                _degradeReason = reason;
                _degradeReasonText = text;
                _lastFailureReason = text;
                _lastFailureUtc = DateTime.UtcNow;
                // ★ 状态说明同步更新：RecordFailure 直接改 _state（绕过 SetState），
                //   若不更新 _stateText，Faulted/Degraded 状态下 UI 会显示上一条状态文字
                //   （如"正在启动实时捕获……"），与真实原因不符。失败说明本身即状态说明。
                _stateText = text;
                _state = backendFailure ? BackdropServiceState.Faulted : BackdropServiceState.Degraded;
                if (backendFailure) _captureFailures++;
            }

            // ★ 失败退避（合同 §5.2.4）：不在绘制路径里重试，只在后台按退避重试
            double delay = RetryBackoffBaseMs * Math.Pow(2d, Math.Min(failures - 1, 8));
            if (delay > RetryBackoffMaxMs) delay = RetryBackoffMaxMs;
            _retryNotBeforeMs = _clock.Elapsed.TotalMilliseconds + delay;
        }

        private void SetState(BackdropServiceState state, string? text)
        {
            lock (_metricsGate)
            {
                _state = state;
                // ★ 无条件覆盖：text 参数就是"当前状态说明"。
                //   传 null 表示本状态没有说明文字，不应残留上一条状态的文案。
                _stateText = text;

                // ★ 原因码与原因文本必须**成对**（合同 §7：降级必须可诊断，不能自相矛盾）。
                //   状态不是"降级/故障"时，把降级原因码与降级原因文本**一起清空**：
                //   否则会出现「原因码 = BackendFrameInvalid、原因文本 = 捕获服务已停止。」的错配，
                //   让 A17 的支持矩阵按错误原因归档。历史失败原因仍保留在 LastFailureReason。
                if (state != BackdropServiceState.Degraded && state != BackdropServiceState.Faulted)
                {
                    _degradeReason = BackdropDegradeReason.None;
                    _degradeReasonText = null;
                }
            }
        }

        private bool SafeBackendIsReady()
        {
            try { return _backend.IsReady; }
            catch { return false; }
        }

        private string? SafeBackendUnavailableReason()
        {
            try { return _backend.UnavailableReason; }
            catch { return "捕获后端状态查询失败。"; }
        }

        private void RestoreAffinitySafely()
        {
            try { _backend.RestoreDisplayAffinity(); }
            catch { /* 恢复失败不阻断停止流程 */ }
        }

        private static bool WaitOrCancel(CancellationToken ct, int milliseconds)
        {
            if (milliseconds <= 0) return !ct.IsCancellationRequested;
            try { return ct.WaitHandle.WaitOne(milliseconds); }
            catch (ObjectDisposedException) { return false; }
        }

        /// <summary>租约被 UI 释放时的回调（服务只是不再提供这一帧，不碰位图）。</summary>
        internal void NotifyLeaseDisposed(BackdropFrameLease lease)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeLease, lease)) _activeLease = null;
            }
        }
    }
}
