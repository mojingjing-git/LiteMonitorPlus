using System;
using System.IO;
using System.Text.Json;

namespace LiteMonitor.Diagnostics;

/// <summary>
/// D02：上次运行是否正常退出的最小磁盘标记。
/// 只写一个固定小文件，不保存事件内容；失败一律静默，绝不阻断启动/退出。
/// </summary>
public static class DiagnosticRunMarker
{
    public sealed record PreviousRun(
        bool HadMarker,
        bool CleanShutdown,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? EndedAtUtc);

    private sealed record MarkerState(
        bool CleanShutdown,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? EndedAtUtc);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiteMonitor++", "diagnostics", "last-run.json");

    public static PreviousRun ReadAndBegin(string? path = null)
    {
        path ??= DefaultPath;
        PreviousRun previous = ReadPrevious(path);
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            Write(path, new MarkerState(false, DateTimeOffset.UtcNow, null));
        }
        catch
        {
            // 标记失败不影响诊断，也不影响启动。
        }
        return previous;
    }

    public static void MarkCleanShutdown(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            MarkerState? state = ReadState(path);
            DateTimeOffset started = state?.StartedAtUtc ?? DateTimeOffset.UtcNow;
            Write(path, new MarkerState(true, started, DateTimeOffset.UtcNow));
        }
        catch
        {
            // 退出路径同样静默。
        }
    }

    private static PreviousRun ReadPrevious(string path)
    {
        try
        {
            MarkerState? state = ReadState(path);
            return state is null
                ? new PreviousRun(false, false, null, null)
                : new PreviousRun(true, state.CleanShutdown, state.StartedAtUtc, state.EndedAtUtc);
        }
        catch
        {
            return new PreviousRun(false, false, null, null);
        }
    }

    private static MarkerState? ReadState(string path)
    {
        if (!File.Exists(path)) return null;
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MarkerState>(json);
    }

    private static void Write(string path, MarkerState state)
    {
        string json = JsonSerializer.Serialize(state);
        string temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}
