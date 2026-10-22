using System.Text;
using System.Text.Json;

namespace LiteMonitor.NetworkExclusionProbe.Support;

/// <summary>Collects structured evidence and mirrors everything to a log file.</summary>
public sealed class Evidence
{
    private readonly Dictionary<string, object?> _sections = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly StringBuilder _log = new();
    private readonly string? _logPath;

    public Evidence(string? logPath = null)
    {
        _logPath = logPath;
    }

    public void Log(string message)
    {
        lock (_gate)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message;
            _log.AppendLine(line);
            Console.WriteLine(line);
        }
    }

    public void Set(string key, object? value)
    {
        lock (_gate) { _sections[key] = value; }
    }

    public void Add(string key, object? value)
    {
        lock (_gate)
        {
            if (_sections.TryGetValue(key, out object? existing) && existing is List<object?> list)
            {
                list.Add(value);
            }
            else
            {
                _sections[key] = new List<object?> { value };
            }
        }
    }

    public string WriteJson(string path)
    {
        object? snapshot;
        lock (_gate)
        {
            snapshot = new Dictionary<string, object?>(_sections, StringComparer.Ordinal);
        }
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IncludeFields = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        string json = JsonSerializer.Serialize(snapshot, options);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    public void WriteLog()
    {
        if (_logPath == null) { return; }
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_logPath))!);
            File.WriteAllText(_logPath, _log.ToString(), new UTF8Encoding(false));
        }
    }
}
