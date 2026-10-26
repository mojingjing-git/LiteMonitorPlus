using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;

namespace LiteMonitor.HardwareHost;

/// <summary>
/// E02 命令面线格式（Host 侧）。与 <c>src/System/Pipe/PipeCommandProtocol.cs</c>
/// （客户端侧同名契约）**必须保持一致**：两处各自持有同构的 JSON 选项与 DTO，
/// 以便在不触碰 Contracts（集成人独占）的前提下演进协议次版本。
/// <para>约定：参数 JSON 用 camelCase（对齐 IpcMessages 注释的
/// <c>{ "consumerId": string, "demand": SamplingDemand }</c>）；
/// 读取一律大小写不敏感（容忍 PascalCase 写法）；结果 DTO 的字段名以 IpcMessages
/// 注释为准（HardwareOperationResult 的 Accepted/OperationId/State/FailureReason）。</para>
/// </summary>
internal static class HostCommandJson
{
    /// <summary>
    /// 读写共用选项：大小写不敏感（camelCase/PascalCase 都能解析）+ 写出用 camelCase
    /// （与 IpcMessages 注释的 {"triggered": bool} 形状一致）。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>按消费者登记需求的命令参数（IpcMessages: submit-demand-v2）。</summary>
internal sealed record SubmitDemandArgs(string ConsumerId, SamplingDemand Demand);

/// <summary>释放需求的命令参数（IpcMessages: release-demand）。</summary>
internal sealed record ReleaseDemandArgs(string ConsumerId);

/// <summary>查询操作状态的命令参数（IpcMessages: operation-status）。</summary>
internal sealed record OperationStatusArgs(string OperationId);

/// <summary>到期采样的结果（IpcMessages: sampling-due → { "triggered": bool }）。</summary>
internal sealed record SamplingDueResult(bool Triggered);

/// <summary>需求登记结果（submit-demand-v2 → { "registered": bool }）。</summary>
internal sealed record DemandRegisterResult(bool Registered);

/// <summary>需求释放结果（release-demand → { "released": bool }）。</summary>
internal sealed record DemandReleaseResult(bool Released);

/// <summary>
/// Host 侧有界操作记录（E02 §6.5/§6.6）：显式操作（重扫）带独立操作 ID 与状态机
/// （Queued/Running/Completed/Failed/Cancelled/Rejected）。
/// <para>容量与截止期限都有界：超限时先剪除过期记录，仍超则拒绝受理并给出可解释原因
/// （operation-capacity-exceeded），不无限增长、不静默丢弃。</para>
/// <para>超时调用方凭 OperationId 后续查询真实结局；重复请求加入在途操作（不重复重扫）。</para>
/// </summary>
internal sealed class HostOperationRegistry
{
    /// <summary>操作记录容量上限（超过即拒绝受理）。</summary>
    public const int DefaultCapacity = 32;

    /// <summary>操作记录截止期限（超过后不再可查，返回 unknown-operation）。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(2);

    private sealed class Entry
    {
        public string Id = "";
        public HardwareOperationState State = HardwareOperationState.Queued;
        public string? FailureReason;
        public long DeadlineTick;
        public long CreatedTick;
        public TaskCompletionSource<HardwareOperationResult> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly TimeSpan _ttl;

    public HostOperationRegistry(int capacity = DefaultCapacity, TimeSpan? ttl = null)
    {
        _capacity = Math.Max(1, capacity);
        _ttl = ttl ?? DefaultTtl;
    }

    public int Count { get { lock (_gate) { PruneLocked(); return _entries.Count; } } }

    /// <summary>创建操作记录；容量超限返回 false（调用方转 Rejected 并给出可解释原因）。</summary>
    public bool TryCreate(string id, out Task<HardwareOperationResult> completion)
    {
        lock (_gate)
        {
            PruneLocked();
            if (_entries.Count >= _capacity)
            {
                // 仍超限：剪除最旧的已终结记录；全部在途则拒绝（可解释）。
                Entry? oldest = null;
                foreach (Entry e in _entries.Values)
                {
                    if (IsTerminal(e.State) && (oldest is null || e.CreatedTick < oldest.CreatedTick)) oldest = e;
                }
                if (oldest is null)
                {
                    completion = Task.FromResult(HardwareOperationResult.Reject("operation-capacity-exceeded"));
                    return false;
                }
                _entries.Remove(oldest.Id);
            }
            var entry = new Entry
            {
                Id = id,
                State = HardwareOperationState.Queued,
                DeadlineTick = Environment.TickCount64 + (long)_ttl.TotalMilliseconds,
                CreatedTick = Environment.TickCount64,
            };
            _entries[id] = entry;
            completion = entry.Completion.Task;
            return true;
        }
    }

    /// <summary>查询操作当前状态（未知/已过期 → null，调用方转 unknown-operation）。</summary>
    public HardwareOperationResult? TryGet(string id)
    {
        lock (_gate)
        {
            PruneLocked();
            if (!_entries.TryGetValue(id, out Entry? entry)) return null;
            return Snapshot(entry);
        }
    }

    /// <summary>推进操作状态（终态不再被覆盖；完成时同时兑现 Completion）。</summary>
    public bool TrySetState(string id, HardwareOperationState state, string? failureReason)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out entry)) return false;
            if (IsTerminal(entry.State)) return false;
            entry.State = state;
            entry.FailureReason = failureReason;
        }
        if (IsTerminal(state))
        {
            HardwareOperationResult result = Snapshot(entry);
            entry.Completion.TrySetResult(result);
        }
        return true;
    }

    private static HardwareOperationResult Snapshot(Entry entry) => new(
        Accepted: entry.State is not (HardwareOperationState.Rejected or HardwareOperationState.Failed),
        OperationId: entry.Id,
        State: entry.State,
        FailureReason: entry.FailureReason);

    private static bool IsTerminal(HardwareOperationState state) =>
        state is HardwareOperationState.Completed or HardwareOperationState.Failed
            or HardwareOperationState.Cancelled or HardwareOperationState.Rejected;

    /// <summary>剪除过期记录（截止期限到）——执行记录有界，不随运行时间增长。</summary>
    private void PruneLocked()
    {
        long now = Environment.TickCount64;
        List<string>? expired = null;
        foreach (KeyValuePair<string, Entry> kvp in _entries)
        {
            if (kvp.Value.DeadlineTick <= now)
            {
                (expired ??= new List<string>()).Add(kvp.Key);
            }
        }
        if (expired is null) return;
        foreach (string id in expired) _entries.Remove(id);
    }
}
