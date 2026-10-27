using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LiteMonitor.Diagnostics;

/// <summary>D04 诊断包输入；事件与快照由调用方从同一刷新批次取得。</summary>
public sealed record DiagnosticPackageInput(
    DiagnosticSnapshot Snapshot,
    IReadOnlyList<DiagnosticEvent> Events,
    string AppVersion,
    long DroppedCount = 0,
    long RejectedCount = 0,
    DateTimeOffset? GeneratedAtUtc = null);

/// <summary>D04 导出预览；与 ZIP 使用同一模型，避免“预览干净、导出泄漏”。</summary>
public sealed record DiagnosticPackagePreview(
    string FileName,
    long EstimatedBytes,
    int EventCount,
    int ModuleCount,
    IReadOnlyList<string> Warnings);

/// <summary>D04 导出结果；失败只返回稳定错误码，不把路径或异常原文带进 UI。</summary>
public sealed record DiagnosticPackageExportResult(bool Success, string? FilePath, long Bytes, string? ErrorCode);

/// <summary>
/// D04 诊断包：允许列表投影、同源预览、ZIP 清单/摘要、临时文件原子完成、10 MiB 截断。
/// <para>本服务不采集硬件、不读设置、不访问网络；快照和事件由调用方传入。</para>
/// </summary>
public sealed class DiagnosticPackageService
{
    public const long MaxPackageBytes = 10L * 1024 * 1024;
    private const string Schema = "litemonitor.diagnostics/1";
    private const string JsonEntryName = "diagnostic.json";
    private const string SummaryEntryName = "summary.txt";

    private static readonly HashSet<string> AllowedFactKeys = new(StringComparer.Ordinal)
    {
        "state", "batchId", "catalogVersion", "configRevision", "moduleCount",
        "operationId", "overallStatus", "progressSeq", "requestRevision",
        "sampleCount", "samplingDurationMs", "effectiveRevision", "failureCode",
        "good", "stale", "missing", "error", "maxSampleAgeMs",
        "sharedRequests", "skippedRequests", "collectionRounds", "failedCollections",
        "operation", "diagnosticReason",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DiagnosticPackagePreview CreatePreview(DiagnosticPackageInput input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));

        byte[] json = BuildJson(input);
        var warnings = new List<string>();
        if (input.DroppedCount > 0) warnings.Add($"events-dropped:{input.DroppedCount}");
        if (input.RejectedCount > 0) warnings.Add($"events-rejected:{input.RejectedCount}");
        if (json.LongLength > MaxPackageBytes) warnings.Add("package-too-large");

        return new DiagnosticPackagePreview(
            FileName: $"LiteMonitor-diagnostics-{(input.GeneratedAtUtc ?? DateTimeOffset.UtcNow):yyyyMMdd-HHmmss}.zip",
            EstimatedBytes: json.LongLength,
            EventCount: input.Events?.Count ?? 0,
            ModuleCount: input.Snapshot?.Modules?.Count ?? 0,
            Warnings: warnings);
    }

    public DiagnosticPackageExportResult Export(
        string filePath,
        DiagnosticPackageInput input,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("路径不能为空", nameof(filePath));
        if (input is null) throw new ArgumentNullException(nameof(input));
        cancellationToken.ThrowIfCancellationRequested();

        DiagnosticPackagePreview preview = CreatePreview(input);
        if (preview.EstimatedBytes > MaxPackageBytes)
            return new DiagnosticPackageExportResult(false, null, preview.EstimatedBytes, "package-too-large");

        string fullPath = Path.GetFullPath(filePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory)) return new DiagnosticPackageExportResult(false, null, 0, "invalid-path");

        string tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(directory);
            byte[] json = BuildJson(input);
            if (json.LongLength > MaxPackageBytes)
                return new DiagnosticPackageExportResult(false, null, json.LongLength, "package-too-large");

            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteEntry(zip, JsonEntryName, json);
                WriteEntry(zip, SummaryEntryName, Encoding.UTF8.GetBytes(BuildSummary(input, json.LongLength)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, fullPath, overwrite: true);
            long bytes = new FileInfo(fullPath).Length;
            return new DiagnosticPackageExportResult(true, fullPath, bytes, null);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(tempPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            SafeDelete(tempPath);
            return new DiagnosticPackageExportResult(false, null, 0, "write-failed");
        }
    }

    private static byte[] BuildJson(DiagnosticPackageInput input)
    {
        var modules = (input.Snapshot?.Modules ?? Array.Empty<DiagnosticModuleSnapshot>())
            .Select(SanitizeModule)
            .ToArray();
        var events = (input.Events ?? Array.Empty<DiagnosticEvent>())
            .Select(SanitizeEvent)
            .ToArray();

        var model = new PackageModel(
            Schema,
            input.AppVersion ?? string.Empty,
            input.GeneratedAtUtc ?? DateTimeOffset.UtcNow,
            new PackageSummary(events.Length, modules.Length, input.DroppedCount, input.RejectedCount),
            new DiagnosticSnapshot(
                input.Snapshot?.CapturedAtUtc ?? DateTimeOffset.UtcNow,
                modules),
            events);

        return JsonSerializer.SerializeToUtf8Bytes(model, JsonOptions);
    }

    private static DiagnosticModuleSnapshot SanitizeModule(DiagnosticModuleSnapshot module)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in module.Facts ?? new Dictionary<string, string>())
        {
            if (!AllowedFactKeys.Contains(pair.Key) && !pair.Key.StartsWith("module.", StringComparison.Ordinal))
                continue;
            if (!DiagnosticRedaction.IsSafeValue(pair.Value)) continue;
            facts[pair.Key] = pair.Value ?? string.Empty;
        }

        return module with
        {
            Summary = SafeText(module.Summary),
            Facts = facts,
        };
    }

    private static DiagnosticEvent SanitizeEvent(DiagnosticEvent evt)
    {
        return evt with
        {
            Module = SafeText(evt.Module),
            ReasonCode = SafeText(evt.ReasonCode),
            OperationId = evt.OperationId is null ? null : SafeText(evt.OperationId),
            Context = DiagnosticRedaction.Filter(evt.Context),
        };
    }

    private static string SafeText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return DiagnosticRedaction.IsSafeValue(value) ? value : "<redacted>";
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using Stream entryStream = entry.Open();
        entryStream.Write(bytes, 0, bytes.Length);
    }

    private static string BuildSummary(DiagnosticPackageInput input, long jsonBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("LiteMonitor diagnostics package");
        sb.AppendLine($"schema={Schema}");
        sb.AppendLine($"appVersion={SafeText(input.AppVersion)}");
        sb.AppendLine($"generatedAtUtc={(input.GeneratedAtUtc ?? DateTimeOffset.UtcNow):O}");
        sb.AppendLine($"modules={input.Snapshot?.Modules?.Count ?? 0}");
        sb.AppendLine($"events={input.Events?.Count ?? 0}");
        sb.AppendLine($"dropped={input.DroppedCount}");
        sb.AppendLine($"rejected={input.RejectedCount}");
        sb.AppendLine($"jsonBytes={jsonBytes}");
        sb.AppendLine("privacy=allowlist-projected; no credentials, paths or raw URLs");
        return sb.ToString();
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record PackageModel(
        string Schema,
        string AppVersion,
        DateTimeOffset GeneratedAtUtc,
        PackageSummary Summary,
        DiagnosticSnapshot Snapshot,
        IReadOnlyList<DiagnosticEvent> Events);

    private sealed record PackageSummary(
        int EventCount,
        int ModuleCount,
        long DroppedCount,
        long RejectedCount);
}
