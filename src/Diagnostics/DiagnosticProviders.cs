using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiteMonitor.Contracts;
using LiteMonitor.Core.Actions.Services;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.Diagnostics;

/// <summary>
/// 采集模块事实：健康状态 + 最新批次质量。读取只走已发布快照，不触发采样。
/// </summary>
public sealed class HardwareDiagnosticProvider : IDiagnosticProvider
{
    private readonly Func<HardwareHealth> _health;
    private readonly Func<HardwareSnapshot> _snapshot;
    private readonly HardwareSamplingStatistics? _statistics;
    private readonly Func<DateTimeOffset> _clock;

    public HardwareDiagnosticProvider(
        Func<HardwareHealth> health,
        Func<HardwareSnapshot> snapshot,
        HardwareSamplingStatistics? statistics = null,
        Func<DateTimeOffset>? clock = null)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _statistics = statistics;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Module => "sampling";

    public DiagnosticModuleSnapshot Read()
    {
        var now = _clock();
        HardwareHealth health = _health();
        HardwareSnapshot snapshot = _snapshot();

        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["state"] = health.State.ToString(),
            ["progressSeq"] = health.ProgressSeq.ToString(CultureInfo.InvariantCulture),
            ["batchId"] = snapshot.BatchId.ToString(CultureInfo.InvariantCulture),
            ["catalogVersion"] = snapshot.CatalogVersion.ToString(CultureInfo.InvariantCulture),
            ["configRevision"] = snapshot.ConfigRevision.ToString(CultureInfo.InvariantCulture),
            ["sampleCount"] = snapshot.Samples.Count.ToString(CultureInfo.InvariantCulture),
            ["samplingDurationMs"] = snapshot.SamplingDuration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(health.CurrentOperation)) facts["operation"] = health.CurrentOperation;
        if (!string.IsNullOrWhiteSpace(health.DiagnosticReason)) facts["diagnosticReason"] = health.DiagnosticReason;

        int good = snapshot.Samples.Count(s => s.Quality == SampleQuality.Good);
        int stale = snapshot.Samples.Count(s => s.Quality == SampleQuality.Stale);
        int missing = snapshot.Samples.Count(s => s.Quality == SampleQuality.Missing);
        int error = snapshot.Samples.Count(s => s.Quality == SampleQuality.Error);
        facts["good"] = good.ToString(CultureInfo.InvariantCulture);
        facts["stale"] = stale.ToString(CultureInfo.InvariantCulture);
        facts["missing"] = missing.ToString(CultureInfo.InvariantCulture);
        facts["error"] = error.ToString(CultureInfo.InvariantCulture);
        if (snapshot.Samples.Count > 0)
        {
            facts["maxSampleAgeMs"] = snapshot.Samples.Max(s => s.SampleAgeMs)
                .ToString("F0", CultureInfo.InvariantCulture);
        }
        if (_statistics is not null)
        {
            facts["collectionRounds"] = _statistics.CollectionRounds.ToString(CultureInfo.InvariantCulture);
            facts["failedCollections"] = _statistics.FailedCollections.ToString(CultureInfo.InvariantCulture);
            facts["skippedRequests"] = _statistics.SkippedRequests.ToString(CultureInfo.InvariantCulture);
            facts["sharedRequests"] = _statistics.SharedRequests.ToString(CultureInfo.InvariantCulture);
        }

        DiagnosticModuleState state;
        string? reason;
        switch (health.State)
        {
            case HardwareState.Ready when snapshot.Samples.Count > 0 && error == 0:
                state = DiagnosticModuleState.Ok;
                reason = null;
                break;
            case HardwareState.Ready:
                state = DiagnosticModuleState.Warning;
                reason = snapshot.Samples.Count == 0 ? "snapshot-empty" : "sample-errors";
                break;
            case HardwareState.Degraded:
                state = DiagnosticModuleState.Warning;
                reason = "hardware-degraded";
                break;
            case HardwareState.Unresponsive:
                state = DiagnosticModuleState.Error;
                reason = "hardware-unresponsive";
                break;
            case HardwareState.Stopped:
            case HardwareState.Stopping:
                state = DiagnosticModuleState.Error;
                reason = "hardware-stopped";
                break;
            default:
                state = DiagnosticModuleState.Warning;
                reason = "hardware-starting";
                break;
        }

        string summary = $"采集 {health.State}；批次 {snapshot.BatchId}；样本 {snapshot.Samples.Count}（Good {good} / Missing {missing}）";
        return new DiagnosticModuleSnapshot(Module, state, reason, summary, now, facts);
    }
}

/// <summary>
/// 配置/主题应用结果事实：只读取 C1 最近一次操作，不猜测成功。
/// </summary>
public sealed class ApplyResultDiagnosticProvider : IDiagnosticProvider
{
    private readonly Func<ApplyOperationResult?> _lastApply;
    private readonly Func<DateTimeOffset> _clock;

    public ApplyResultDiagnosticProvider(Func<ApplyOperationResult?> lastApply, Func<DateTimeOffset>? clock = null)
    {
        _lastApply = lastApply ?? throw new ArgumentNullException(nameof(lastApply));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Module => "apply";

    public DiagnosticModuleSnapshot Read()
    {
        var now = _clock();
        ApplyOperationResult? result = _lastApply();
        if (result is null)
        {
            return new DiagnosticModuleSnapshot(
                Module,
                DiagnosticModuleState.Unknown,
                "no-apply-operation",
                "尚未记录应用操作",
                now,
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["operationId"] = result.OperationId,
            ["overallStatus"] = result.OverallStatus.ToString(),
            ["requestRevision"] = result.RequestRevision.ToString(CultureInfo.InvariantCulture),
            ["moduleCount"] = result.Modules.Count.ToString(CultureInfo.InvariantCulture),
        };
        if (result.EffectiveRevision.HasValue)
            facts["effectiveRevision"] = result.EffectiveRevision.Value.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(result.FailureCode)) facts["failureCode"] = result.FailureCode;
        foreach (ModuleApplyResult module in result.Modules)
        {
            facts["module." + module.Module] = module.Applied
                ? "applied" + (string.IsNullOrWhiteSpace(module.EffectiveState) ? string.Empty : ":" + module.EffectiveState)
                : "failed:" + (module.FailureReason ?? "unknown");
        }

        DiagnosticModuleState state = result.OverallStatus switch
        {
            ApplyOperationStatus.Applied => DiagnosticModuleState.Ok,
            ApplyOperationStatus.Partial => DiagnosticModuleState.Warning,
            ApplyOperationStatus.Pending => DiagnosticModuleState.Warning,
            ApplyOperationStatus.Cancelled => DiagnosticModuleState.Warning,
            _ => DiagnosticModuleState.Error,
        };
        string? reason = result.OverallStatus switch
        {
            ApplyOperationStatus.Applied => null,
            ApplyOperationStatus.Partial => "apply-partial",
            ApplyOperationStatus.Pending => "apply-pending",
            ApplyOperationStatus.Cancelled => "apply-cancelled",
            ApplyOperationStatus.Failed => "apply-failed",
            _ => "apply-unknown",
        };
        string summary = $"最近应用 {result.OverallStatus}；{result.Modules.Count} 个模块；修订 {result.RequestRevision}";
        return new DiagnosticModuleSnapshot(Module, state, reason, summary, now, facts);
    }
}
