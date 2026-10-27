using System;
using System.Collections.Generic;

namespace LiteMonitor.Diagnostics;

/// <summary>诊断事件严重程度。数值只用于展示排序，不写入稳定协议。</summary>
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
    Critical,
}

/// <summary>
/// C2 诊断事件合同：只承载可解释事实，不记录每帧/每个传感器值/正常轮询。
/// <para>EventId 由 <see cref="DiagnosticEventStore"/> 单调分配；调用方传入 0 即可。</para>
/// </summary>
public sealed record DiagnosticEvent(
    DateTimeOffset UtcTime,
    string Module,
    DiagnosticSeverity Severity,
    string ReasonCode,
    string? OperationId,
    string SessionId,
    IReadOnlyDictionary<string, string>? Context = null)
{
    public long EventId { get; init; }
    public int RepeatCount { get; init; } = 1;
}
