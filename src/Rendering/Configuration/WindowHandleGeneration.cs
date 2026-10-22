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
    /// <summary>
    /// 进程内**唯一**实例。
    /// <para>【主审裁决 D-G】句柄代次必须**全进程唯一**：若 owner B（协调器）与 owner C（宿主）
    /// 各自 <c>new</c> 一个，两侧会读到**不同的代次** ⇒ 合同 §7.4 的跨代次丢弃判据
    /// （"代次先于 <c>IsWindow</c>"）失效，GPU 清理链与 live 捕获链会互相误判。</para>
    /// <para>因此唯一实例由**类型自身**持有，并落在 owner A 的本文件 ——
    /// <c>src/Rendering/Configuration/</c> 位于 UI 层与 Rendering 层**之下**，
    /// 两侧都只读消费它 ⇒ **不产生层次倒置**，也**不可能**出现第二份 holder。</para>
    /// <para>owner B 与 owner C **禁止**另行 <c>new WindowHandleGeneration()</c>；
    /// 测试如需隔离实例仍可自行 <c>new</c>（生产路径不得）。</para>
    /// </summary>
    public static WindowHandleGeneration Instance { get; } = new WindowHandleGeneration();

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
