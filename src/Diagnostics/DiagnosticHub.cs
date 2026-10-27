using System;
using System.Collections.Generic;

namespace LiteMonitor.Diagnostics;

/// <summary>
/// 应用级诊断事件出口：只保留进程内有界事件，不逐条写盘。
/// <para>诊断窗口关闭只释放订阅，不清空这里；趋势/场景/低功耗后续也接同一出口。</para>
/// </summary>
public sealed class DiagnosticHub
{
    private static readonly Lazy<DiagnosticHub> _current = new(() => new DiagnosticHub(), true);

    public static DiagnosticHub Current => _current.Value;

    public DiagnosticEventStore Store { get; }
    public string SessionId { get; }

    /// <summary>
    /// 诊断窗口读取快照的入口；组合根可替换为真实 Provider 集合。
    /// 默认返回空快照，避免窗口在未接线时抛异常。
    /// </summary>
    public Func<DiagnosticSnapshot> SnapshotProvider { get; set; } =
        () => new DiagnosticSnapshot(DateTimeOffset.UtcNow, Array.Empty<DiagnosticModuleSnapshot>());

    public DiagnosticHub(DiagnosticEventStore? store = null, string? sessionId = null)
    {
        Store = store ?? new DiagnosticEventStore();
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
    }

    public bool Record(
        string module,
        DiagnosticSeverity severity,
        string reasonCode,
        string? operationId = null,
        IReadOnlyDictionary<string, string>? context = null,
        DateTimeOffset? utcTime = null)
    {
        if (string.IsNullOrWhiteSpace(module)) throw new ArgumentException("module 不能为空", nameof(module));
        if (string.IsNullOrWhiteSpace(reasonCode)) throw new ArgumentException("reasonCode 不能为空", nameof(reasonCode));

        return Store.Add(new DiagnosticEvent(
            utcTime ?? DateTimeOffset.UtcNow,
            module,
            severity,
            reasonCode,
            operationId,
            SessionId,
            context));
    }
}
