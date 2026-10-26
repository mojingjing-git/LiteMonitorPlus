using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;

namespace LiteMonitor.SystemServices.Pipe;

/// <summary>
/// 客户端侧显式操作记录（E02 §6.5/§6.6）：容量与截止期限**有界**。
/// <para>语义：超时只表示**本调用方**停止等待——记录仍在，新请求据此取得同一
/// OperationId（busy 语义），**不重复发起底层非幂等操作**；一个加入者取消不取消
/// 共享操作（取消只作用于各自的等待）。</para>
/// <para>记录到期即被剪除（不可查），调用方此后只能靠"发起新操作"表达意图。</para>
/// </summary>
internal sealed class PipeOperationRegistry
{
    /// <summary>操作记录容量上限（超过即剪除最旧的已终结记录；全在途则拒绝新建）。</summary>
    public const int DefaultCapacity = 32;

    /// <summary>操作记录截止期限（超过后不再可查）。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(2);

    /// <summary>单条操作记录：状态机 + 共享完成信号（多加入者共用一个完成源）。</summary>
    internal sealed class Record
    {
        /// <summary>本地标识（Host 侧 OperationId 在受理后才可知）。</summary>
        public string LocalId { get; init; } = "";

        /// <summary>Host 侧操作 ID（受理后回填；超时调用方凭此查询真实结局）。</summary>
        public string OperationId { get; set; } = "";

        public HardwareOperationState State { get; set; } = HardwareOperationState.Queued;

        public string? FailureReason { get; set; }

        /// <summary>截止期限（本地单调毫秒）；到期后记录被剪除。</summary>
        public long DeadlineTick { get; set; }

        /// <summary>共享完成源：一个加入者取消不影响其他加入者（各自等待自己的取消）。</summary>
        public TaskCompletionSource<HardwareOperationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsTerminal => State is HardwareOperationState.Completed or HardwareOperationState.Failed
            or HardwareOperationState.Cancelled or HardwareOperationState.Rejected;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Record> _records = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly TimeSpan _ttl;

    public PipeOperationRegistry(int capacity = DefaultCapacity, TimeSpan? ttl = null)
    {
        _capacity = Math.Max(1, capacity);
        _ttl = ttl ?? DefaultTtl;
    }

    public int Count { get { lock (_gate) { PruneLocked(); return _records.Count; } } }

    /// <summary>新建操作记录；容量超限且无可剪除项时返回 null（调用方转可解释 Rejected）。</summary>
    public Record? TryCreate(string localId)
    {
        lock (_gate)
        {
            PruneLocked();
            if (_records.Count >= _capacity)
            {
                Record? oldest = null;
                foreach (Record r in _records.Values)
                {
                    if (r.IsTerminal && (oldest is null || r.DeadlineTick < oldest.DeadlineTick)) oldest = r;
                }
                if (oldest is null) return null; // 全在途：不新建（调用方给出可解释拒绝）
                _records.Remove(oldest.LocalId);
            }
            var record = new Record
            {
                LocalId = localId,
                DeadlineTick = Environment.TickCount64 + (long)_ttl.TotalMilliseconds,
            };
            _records[localId] = record;
            return record;
        }
    }

    public Record? Find(string localId)
    {
        lock (_gate)
        {
            PruneLocked();
            return _records.TryGetValue(localId, out Record? r) ? r : null;
        }
    }

    /// <summary>完成操作（共享完成源兑现；所有加入者同时收到同一终态）。</summary>
    public void Complete(Record record, HardwareOperationResult result)
    {
        record.State = result.State;
        record.FailureReason = result.FailureReason;
        if (!string.IsNullOrEmpty(result.OperationId)) record.OperationId = result.OperationId;
        record.Completion.TrySetResult(result);
    }

    /// <summary>剪除过期记录（截止期限到）——执行记录有界，不随运行时间增长。</summary>
    public void Prune()
    {
        lock (_gate) { PruneLocked(); }
    }

    private void PruneLocked()
    {
        long now = Environment.TickCount64;
        List<string>? expired = null;
        foreach (KeyValuePair<string, Record> kvp in _records)
        {
            if (kvp.Value.DeadlineTick <= now)
            {
                (expired ??= new List<string>()).Add(kvp.Key);
            }
        }
        if (expired is null) return;
        foreach (string id in expired) _records.Remove(id);
    }
}
