using System.Threading;

namespace LiteMonitor.Rendering.Configuration;

/// <summary>
/// 【T02 合同 §7.2 / 裁决 D1】句柄代次的**唯一真相源**。
/// <para>T00 §4 已确认：全仓 <c>handleGeneration|HandleGeneration</c> 仅命中
/// <c>LiveGlassController</c> 与 <c>src/System/Backdrop/**</c> 的一条链，
/// **GPU 侧无并行计数器** ⇒ "合并为同一真相源"是改动量最小、且不新增口径的路径。</para>
/// <para><b>唯一递增点</b> = <see cref="Advance"/>；<b>唯一驱动者</b> = 协调器（owner B）
/// 在 <c>HandleCreated</c> 时调用。</para>
/// <para><b>跨代次丢弃判据（GPU-004，合同 §7.4）</b>：代次必须**先于** <c>IsWindow</c> 判定 ——
/// <c>IsWindow</c> 只回答"这个数值当前是否是一个窗口"，无法区分"旧 HWND 已销毁"与
/// "句柄数值被复用"。</para>
/// </summary>
public sealed class WindowHandleGeneration
{
    private long _current = 1;

    /// <summary>当前句柄代次。初始为 1。</summary>
    public long Current => Interlocked.Read(ref _current);

    /// <summary>推进一代并返回新的代次。**唯一递增点**。</summary>
    public long Advance() => Interlocked.Increment(ref _current);

    /// <summary>给定代次是否就是当前代次。</summary>
    public bool IsCurrent(long generation) => generation == Current;

    /// <summary>
    /// 给定代次是否早于当前代次（已被取代）。为 true 时**禁止**对该代次的 HWND
    /// 执行任何 Win32 调用（合同 §7.4）。
    /// </summary>
    public bool IsOlder(long generation) => generation < Current;
}
