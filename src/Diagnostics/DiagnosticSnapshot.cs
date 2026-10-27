using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteMonitor.Diagnostics;

/// <summary>模块事实的展示状态。没有事实时必须用 Unknown，不能伪装成正常。</summary>
public enum DiagnosticModuleState
{
    Ok,
    Warning,
    Error,
    Unknown,
    NotSupported,
}

/// <summary>
/// 单个模块的只读事实快照。
/// <para>Facts 只放可公开的结构化字段；不得放路径、令牌、原始请求/响应或设备序列号。</para>
/// </summary>
public sealed record DiagnosticModuleSnapshot(
    string Module,
    DiagnosticModuleState State,
    string? ReasonCode,
    string Summary,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyDictionary<string, string> Facts)
{
    public static DiagnosticModuleSnapshot Unknown(string module, string reasonCode, DateTimeOffset observedAtUtc)
        => new(module, DiagnosticModuleState.Unknown, reasonCode, "暂无可用事实", observedAtUtc,
            new Dictionary<string, string>(StringComparer.Ordinal));
}

/// <summary>一次诊断读取的模块集合。各模块保留自己的观测时刻，不声称跨模块原子。</summary>
public sealed record DiagnosticSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<DiagnosticModuleSnapshot> Modules)
{
    public DiagnosticModuleSnapshot? Find(string module)
        => Modules.FirstOrDefault(m => string.Equals(m.Module, module, StringComparison.Ordinal));
}

/// <summary>只读模块事实提供者。实现不得获取 LHM 对象锁、等待硬件命令或扫描磁盘/网络。</summary>
public interface IDiagnosticProvider
{
    string Module { get; }
    DiagnosticModuleSnapshot Read();
}

/// <summary>
/// 由既有状态读取函数构造的 Provider，用于 Web/插件/IPC 等尚未提供窄接口的模块。
/// 委托应只返回已缓存状态；不要在 Read 中触发采样或网络请求。
/// </summary>
public sealed class DelegateDiagnosticProvider : IDiagnosticProvider
{
    private readonly Func<DiagnosticModuleSnapshot> _read;

    public DelegateDiagnosticProvider(string module, Func<DiagnosticModuleSnapshot> read)
    {
        if (string.IsNullOrWhiteSpace(module)) throw new ArgumentException("module 不能为空", nameof(module));
        Module = module;
        _read = read ?? throw new ArgumentNullException(nameof(read));
    }

    public string Module { get; }
    public DiagnosticModuleSnapshot Read() => _read();
}

/// <summary>
/// 聚合多个 Provider。单模块失败只把该模块标为 Unknown，不影响其他模块。
/// </summary>
public sealed class DiagnosticSnapshotBuilder
{
    private readonly IReadOnlyList<IDiagnosticProvider> _providers;
    private readonly Func<DateTimeOffset> _clock;

    public DiagnosticSnapshotBuilder(IEnumerable<IDiagnosticProvider> providers, Func<DateTimeOffset>? clock = null)
    {
        _providers = (providers ?? throw new ArgumentNullException(nameof(providers))).ToList();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public DiagnosticSnapshot Build()
    {
        var capturedAt = _clock();
        var modules = new List<DiagnosticModuleSnapshot>(_providers.Count);
        foreach (var provider in _providers)
        {
            try
            {
                var snapshot = provider.Read();
                modules.Add(snapshot ?? DiagnosticModuleSnapshot.Unknown(provider.Module, "provider-null", capturedAt));
            }
            catch (Exception ex)
            {
                modules.Add(new DiagnosticModuleSnapshot(
                    provider.Module,
                    DiagnosticModuleState.Unknown,
                    "provider-failed",
                    "读取失败：" + ex.GetType().Name,
                    capturedAt,
                    new Dictionary<string, string>(StringComparer.Ordinal)));
            }
        }

        return new DiagnosticSnapshot(capturedAt, modules);
    }
}
