using LiteMonitor.Rendering.Configuration;

namespace LiteMonitor.Rendering.Presentation;

/// <summary>
/// 【R02 / T02 合同 §4.4 / U11】GPU 故障发生的<b>阶段</b>。
/// <para>★ 三条必须走<b>同一条</b> legacy 转换入口的路径：启动失败、首帧失败、运行故障恢复耗尽。
/// 阶段只用于诊断与"是否立刻回退"的判定
/// （计划 §4.4 :186 "最终失败才进入完整 legacy 切换" ⇒ 运行故障本身先走既有有限恢复）。</para>
/// </summary>
public enum GpuFailureStage
{
    /// <summary>启动失败：宿主 <c>Start()</c> 直接返回失败（尚未呈现过任何一帧）。</summary>
    Startup = 0,

    /// <summary>首帧失败：首次呈现调用期间抛错（合同 §10 U03 ⇒ 保守重建）。</summary>
    FirstFrame = 1,

    /// <summary>运行故障：已经成功呈现之后的渲染失败。</summary>
    Runtime = 2,

    /// <summary>
    /// 运行故障的自动恢复预算耗尽（既有码 <c>gpu_presentation_recovery_exhausted</c>）。
    /// </summary>
    RecoveryExhausted = 3,
}

/// <summary>
/// 【R02】GPU 故障报告（纯 CLR 快照；不引用 WinForms / DXGI ⇒ 可被确定性单测覆盖）。
/// <para>★ <see cref="FailureCode"/> 是**呈现根因**（例如 <c>gpu_presentation_render_failed</c>），
/// <b>不得</b>被"普通 fallback code"顶替（合同 §5.3 规则 2 / U11）。</para>
/// <para>★ <see cref="BindingState"/> 是**本代次**的 HWND 呈现历史
/// （计划 §4.1 :123-126：是否需要受控重建 HWND 的唯一依据）。</para>
/// </summary>
/// <param name="Stage">故障阶段。</param>
/// <param name="FailureCode">呈现根因码。</param>
/// <param name="FailureMessage">呈现根因消息（可空）。</param>
/// <param name="BindingState">本代次的 HWND 呈现历史。</param>
/// <param name="FirstFrameOutcome">首帧结果（合同 §2 维度 8）。</param>
public sealed record GpuFailureReport(
    GpuFailureStage Stage,
    string FailureCode,
    string? FailureMessage,
    PresentationBindingState BindingState,
    FirstFrameOutcome FirstFrameOutcome);

/// <summary>
/// 【R02 / 合同 §4 全局约束】禁止执行 legacy 转换的<b>执行上下文</b>。
/// <para>计划 §4.3 :169：禁止在 <c>OnPaint</c> / <c>WndProc</c> 的销毁回调 /
/// GPU 帧绘制栈内递归执行转换的任何一步。</para>
/// </summary>
[Flags]
public enum GpuFailureUnsafeContext
{
    /// <summary>无禁止上下文（UI 安全点）。</summary>
    None = 0,

    /// <summary>正在 <c>OnPaint</c> 内。</summary>
    Paint = 1 << 0,

    /// <summary>正在 <c>WndProc</c> 的句柄销毁回调内。</summary>
    HandleDestroy = 1 << 1,

    /// <summary>正在 GPU 帧绘制栈内（宿主的帧定时器回调）。</summary>
    GpuFrameStack = 1 << 2,
}

/// <summary>
/// 【R02 / 合同 §4.4 / U11】GPU 故障 → legacy 转换的<b>统一调度器</b>（纯 CLR）。
/// <para><b>它解决什么</b>：故障可能发生在 GPU 帧绘制栈内（计划 §4.3 :169 禁止在那里递归执行转换），
/// 也可能发生在启动路径里 ⇒ 故障报告必须<b>先入队</b>，再由 UI 安全点（UI Tick）统一执行转换。
/// 这让"启动失败 / 首帧失败 / 运行故障恢复耗尽"三条路径与"用户手动关闭 GPU"路径
/// <b>共用同一个</b> legacy 转换入口。</para>
/// <para><b>纯 CLR</b>（不引用 WinForms / DXGI）⇒ 全部规则可被确定性单测覆盖。</para>
/// </summary>
public sealed class GpuFailureFallbackScheduler
{
    /// <summary>待执行的回退请求（最多一条；先到先得 ⇒ 最早的那条根因不会被顶替）。</summary>
    private GpuFailureReport? _pending;

    /// <summary>被重复请求顶替掉的次数（诊断用）。</summary>
    private int _coalescedCount;

    /// <summary>当前生效的禁止上下文。</summary>
    private GpuFailureUnsafeContext _unsafeContext = GpuFailureUnsafeContext.None;

    /// <summary>是否正在执行一次转换（重入保护）。</summary>
    private bool _executing;

    /// <summary>
    /// 请求一次"GPU 故障 → legacy 转换"。
    /// <para>★ <b>先到先得</b>：已有一条待执行请求时，本请求被<b>合并</b>（不顶替既有根因）。
    /// 返回 <c>false</c> 表示本次请求被合并。</para>
    /// </summary>
    public bool Request(GpuFailureReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (_pending is not null)
        {
            _coalescedCount++;
            return false;
        }

        _pending = report;
        return true;
    }

    /// <summary>是否有待执行的回退请求。</summary>
    public bool HasPending => _pending is not null;

    /// <summary>待执行请求数（0 或 1）。</summary>
    public int PendingCount => _pending is null ? 0 : 1;

    /// <summary>被合并掉的重复请求次数（诊断用）。</summary>
    public int CoalescedCount => _coalescedCount;

    /// <summary>当前待执行的请求（只读；<c>null</c> = 无）。</summary>
    public GpuFailureReport? Pending => _pending;

    /// <summary>
    /// 取出并清除一条待执行请求。
    /// </summary>
    public bool TryDequeue(out GpuFailureReport report)
    {
        if (_pending is null)
        {
            report = null!;
            return false;
        }

        report = _pending;
        _pending = null;
        return true;
    }

    /// <summary>当前生效的禁止上下文。</summary>
    public GpuFailureUnsafeContext UnsafeContext => _unsafeContext;

    /// <summary>
    /// 【合同 §4 全局约束】当前是否<b>允许</b>执行 legacy 转换。
    /// <para>两个条件缺一不可：① 没有任何禁止上下文（不在 OnPaint / 销毁回调 / GPU 帧栈内）；
    /// ② 当前没有正在执行的转换（重入保护）。</para>
    /// </summary>
    public bool IsExecutionAllowed =>
        _unsafeContext == GpuFailureUnsafeContext.None && !_executing;

    /// <summary>
    /// 进入一个<b>禁止</b>执行转换的上下文；返回的租约在释放时退出该上下文。
    /// </summary>
    public IDisposable EnterUnsafeContext(GpuFailureUnsafeContext context)
    {
        if (context == GpuFailureUnsafeContext.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context), context, "禁止上下文不能是 None。");
        }

        _unsafeContext |= context;
        return new UnsafeContextLease(this, context);
    }

    /// <summary>标记"正在执行一次转换"（重入保护）。</summary>
    public void BeginExecution() => _executing = true;

    /// <summary>清除"正在执行一次转换"（重入保护）。</summary>
    public void EndExecution() => _executing = false;

    /// <summary>丢弃全部待执行请求（窗体关闭 / 用户显式重试时使用）。</summary>
    public void Clear()
    {
        _pending = null;
        _coalescedCount = 0;
    }

    private void ExitUnsafeContext(GpuFailureUnsafeContext context)
    {
        _unsafeContext &= ~context;
    }

    /// <summary>禁止上下文的租约（Dispose 时退出该上下文）。</summary>
    private sealed class UnsafeContextLease : IDisposable
    {
        private GpuFailureFallbackScheduler? _owner;
        private readonly GpuFailureUnsafeContext _context;

        internal UnsafeContextLease(GpuFailureFallbackScheduler owner, GpuFailureUnsafeContext context)
        {
            _owner = owner;
            _context = context;
        }

        public void Dispose()
        {
            GpuFailureFallbackScheduler? owner = _owner;
            if (owner is null)
            {
                return;
            }

            _owner = null;
            owner.ExitUnsafeContext(_context);
        }
    }
}
