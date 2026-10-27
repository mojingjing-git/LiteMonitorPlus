using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteMonitor.Diagnostics;

/// <summary>
/// C2 有界事件收集器骨架：1024 条滚动、上下文约 2 MiB、单条约 2 KiB，淘汰最旧并计数。
/// <para>默认只驻留进程内；D02 才接入 UI 与“上次运行”标记，本类不写盘、不采集硬件。</para>
/// </summary>
public sealed class DiagnosticEventStore
{
    private readonly object _gate = new();
    private readonly List<DiagnosticEvent> _events = new();
    private readonly int _capacity;
    private readonly int _maxTotalContextBytes;
    private readonly int _maxSingleContextBytes;
    private readonly TimeSpan _mergeWindow;

    private long _nextEventId;
    private int _contextBytes;
    private long _droppedCount;
    private long _rejectedCount;

    public DiagnosticEventStore(
        int capacity = 1024,
        int maxTotalContextBytes = 2 * 1024 * 1024,
        int maxSingleContextBytes = 2 * 1024,
        TimeSpan? mergeWindow = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxTotalContextBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxTotalContextBytes));
        if (maxSingleContextBytes <= 0 || maxSingleContextBytes > maxTotalContextBytes)
            throw new ArgumentOutOfRangeException(nameof(maxSingleContextBytes));

        _capacity = capacity;
        _maxTotalContextBytes = maxTotalContextBytes;
        _maxSingleContextBytes = maxSingleContextBytes;
        _mergeWindow = mergeWindow ?? TimeSpan.FromSeconds(5);
    }

    public int Count { get { lock (_gate) { return _events.Count; } } }
    public long DroppedCount { get { lock (_gate) { return _droppedCount; } } }
    public long RejectedCount { get { lock (_gate) { return _rejectedCount; } } }

    /// <summary>加入一条事件；超单条上限/无法容纳时拒绝并计数，不抛出。</summary>
    public bool Add(DiagnosticEvent evt)
    {
        if (evt is null) throw new ArgumentNullException(nameof(evt));

        lock (_gate)
        {
            var context = DiagnosticRedaction.Filter(evt.Context);
            int bytes = DiagnosticRedaction.EstimateBytes(context);
            if (bytes > _maxSingleContextBytes)
            {
                _rejectedCount++;
                return false;
            }

            int mergeIndex = FindMergeIndex(evt);
            if (mergeIndex >= 0)
            {
                var existing = _events[mergeIndex];
                _contextBytes -= DiagnosticRedaction.EstimateBytes(existing.Context);
                var merged = evt with
                {
                    EventId = existing.EventId,
                    Context = context,
                    RepeatCount = existing.RepeatCount + 1,
                };
                _events[mergeIndex] = merged;
                _contextBytes += bytes;
                return true;
            }

            while (_events.Count >= _capacity
                || (_contextBytes + bytes > _maxTotalContextBytes && _events.Count > 0))
            {
                DropOldest();
            }

            if (_contextBytes + bytes > _maxTotalContextBytes)
            {
                _rejectedCount++;
                return false;
            }

            var stored = evt with { EventId = ++_nextEventId, Context = context };
            _events.Add(stored);
            _contextBytes += bytes;
            return true;
        }
    }

    public IReadOnlyList<DiagnosticEvent> Snapshot()
    {
        lock (_gate) { return _events.ToArray(); }
    }

    private int FindMergeIndex(DiagnosticEvent evt)
    {
        if (_mergeWindow <= TimeSpan.Zero) return -1;
        for (int i = _events.Count - 1; i >= 0; i--)
        {
            var last = _events[i];
            if (!string.Equals(last.Module, evt.Module, StringComparison.Ordinal)) continue;
            if (!string.Equals(last.ReasonCode, evt.ReasonCode, StringComparison.Ordinal)) continue;
            if (last.Severity != evt.Severity) continue;
            if (!string.Equals(last.OperationId, evt.OperationId, StringComparison.Ordinal)) continue;
            if (!string.Equals(last.SessionId, evt.SessionId, StringComparison.Ordinal)) continue;
            if (!SameContextIdentity(last.Context, evt.Context)) continue;
            if (evt.UtcTime < last.UtcTime) continue;
            if (evt.UtcTime - last.UtcTime <= _mergeWindow) return i;
        }
        return -1;
    }

    private static readonly string[] IdentityContextKeys =
    {
        "ruleId", "rule_id",
        "deviceId", "device_id",
        "incidentId", "incident_id",
        "eventKind", "event_kind",
        "batchId", "batch_id",
    };

    private static bool SameContextIdentity(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        foreach (string key in IdentityContextKeys)
        {
            string? l = left != null && left.TryGetValue(key, out string? lv) ? lv : null;
            string? r = right != null && right.TryGetValue(key, out string? rv) ? rv : null;
            if (!string.Equals(l, r, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private void DropOldest()
    {
        var oldest = _events[0];
        _events.RemoveAt(0);
        _contextBytes -= DiagnosticRedaction.EstimateBytes(oldest.Context);
        _droppedCount++;
    }
}
