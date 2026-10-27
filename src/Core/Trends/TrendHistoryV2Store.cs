using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using LiteMonitor.Contracts;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.Core.Trends
{
    /// <summary>T03：长期趋势点。按天分段 JSONL，manifest 记录最后导入时刻。</summary>
    public sealed record TrendHistoryPointRecord(
        DateTimeOffset TimeUtc,
        string MetricKey,
        string? StableDeviceId,
        float Value,
        SampleQuality Quality,
        double SampleAgeMs,
        float? Max = null,
        float? Min = null,
        int Count = 1);

    public sealed record TrendHistoryManifest(
        int Version,
        DateTimeOffset? LastImportedTimestampUtc,
        long TotalPoints,
        DateTimeOffset UpdatedAtUtc);

    public sealed record TrendHistoryReadResult(
        IReadOnlyList<TrendHistoryPointRecord> Points,
        int CorruptLines,
        int FilesRead);

    public sealed class TrendHistoryV2Store
    {
        public const int CurrentVersion = 2;
        public const long DefaultMaxBytes = 64L * 1024 * 1024;
        public const int DefaultRetentionDays = 7;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private readonly object _gate = new();
        private readonly string _directory;
        private readonly string _manifestPath;
        private readonly long _maxBytes;
        private readonly int _retentionDays;
        private readonly Func<DateTimeOffset> _clock;
        private TrendHistoryManifest _manifest;

        public TrendHistoryV2Store(
            string? directory = null,
            long maxBytes = DefaultMaxBytes,
            int retentionDays = DefaultRetentionDays,
            Func<DateTimeOffset>? clock = null)
        {
            _directory = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiteMonitor++", "trends-v2");
            _manifestPath = Path.Combine(_directory, "manifest.json");
            _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
            _retentionDays = retentionDays > 0 ? retentionDays : DefaultRetentionDays;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            Directory.CreateDirectory(_directory);
            _manifest = LoadManifest();
        }

        public TrendHistoryManifest Manifest
        {
            get { lock (_gate) return _manifest; }
        }

        public int Append(IEnumerable<TrendHistoryPointRecord> points)
        {
            ArgumentNullException.ThrowIfNull(points);
            List<TrendHistoryPointRecord> list = points
                .Where(p => !string.IsNullOrWhiteSpace(p.MetricKey))
                .ToList();
            if (list.Count == 0) return 0;

            lock (_gate)
            {
                foreach (IGrouping<string, TrendHistoryPointRecord> day in list.GroupBy(
                    p => p.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                {
                    string path = Path.Combine(_directory, $"trend-{day.Key}.jsonl");
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(stream);
                    foreach (TrendHistoryPointRecord point in day.OrderBy(p => p.TimeUtc))
                    {
                        writer.WriteLine(JsonSerializer.Serialize(point, JsonOptions));
                    }
                }

                DateTimeOffset? latest = list.Max(p => p.TimeUtc);
                if (_manifest.LastImportedTimestampUtc is null || latest > _manifest.LastImportedTimestampUtc)
                {
                    _manifest = _manifest with
                    {
                        LastImportedTimestampUtc = latest,
                        TotalPoints = _manifest.TotalPoints + list.Count,
                        UpdatedAtUtc = _clock(),
                    };
                }
                else
                {
                    _manifest = _manifest with
                    {
                        TotalPoints = _manifest.TotalPoints + list.Count,
                        UpdatedAtUtc = _clock(),
                    };
                }
                WriteManifest();
                EnforceRetention();
                return list.Count;
            }
        }

        public int AppendBuckets(string metricKey, string? stableDeviceId, IEnumerable<TrendBucket> buckets)
        {
            return Append(buckets.Select(b => new TrendHistoryPointRecord(
                b.TimeUtc, metricKey, stableDeviceId, b.Value ?? 0, b.Quality, b.SampleAgeMs,
                b.Max ?? b.Value ?? 0, b.Min ?? b.Value ?? 0, b.Count > 0 ? b.Count : 1)));
        }

        public TrendHistoryReadResult ReadSince(DateTimeOffset sinceUtc)
        {
            lock (_gate)
            {
                var points = new List<TrendHistoryPointRecord>();
                int corrupt = 0;
                int files = 0;
                DateTime sinceLocalDate = sinceUtc.ToLocalTime().Date;
                foreach (string path in Directory.EnumerateFiles(_directory, "trend-*.jsonl").OrderBy(p => p))
                {
                    if (!TryParseDay(path, out DateTime day) || day < sinceLocalDate) continue;
                    files++;
                    foreach (string line in File.ReadLines(path))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            TrendHistoryPointRecord? point = JsonSerializer.Deserialize<TrendHistoryPointRecord>(line, JsonOptions);
                            if (point is null || point.TimeUtc < sinceUtc) continue;
                            points.Add(point);
                        }
                        catch (JsonException)
                        {
                            corrupt++;
                        }
                    }
                }
                points.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
                return new TrendHistoryReadResult(points, corrupt, files);
            }
        }

        /// <summary>只读导入 v1 分钟聚合；按 LastImportedTimestamp 增量，重复调用不重复追加。</summary>
        public int ImportV1(string legacyJsonPath)
        {
            if (string.IsNullOrWhiteSpace(legacyJsonPath) || !File.Exists(legacyJsonPath)) return 0;

            HardwareHistoryData? legacy;
            try
            {
                legacy = JsonSerializer.Deserialize<HardwareHistoryData>(File.ReadAllText(legacyJsonPath));
            }
            catch (JsonException)
            {
                return 0;
            }
            if (legacy?.Points is null) return 0;

            DateTimeOffset? cutoff = Manifest.LastImportedTimestampUtc;
            var imported = new List<TrendHistoryPointRecord>();
            foreach (HardwareHistoryPoint point in legacy.Points)
            {
                DateTime localTime = DateTime.SpecifyKind(point.Time, DateTimeKind.Local);
                var time = new DateTimeOffset(localTime);
                if (cutoff is not null && time <= cutoff) continue;
                foreach (KeyValuePair<string, HardwareHistoryValue> value in point.Values)
                {
                    if (string.IsNullOrWhiteSpace(value.Key)) continue;
                    imported.Add(new TrendHistoryPointRecord(
                        time, value.Key, null, value.Value.Avg, SampleQuality.Good, 0,
                        value.Value.Max, value.Value.Min, value.Value.Count));
                }
            }

            int count = Append(imported);
            return count;
        }

        public void TrimToNow()
        {
            lock (_gate)
            {
                DateTime cutoff = _clock().ToLocalTime().Date.AddDays(-_retentionDays + 1);
                foreach (string path in Directory.EnumerateFiles(_directory, "trend-*.jsonl").ToList())
                {
                    if (TryParseDay(path, out DateTime day) && day < cutoff)
                        File.Delete(path);
                }
                EnforceBytes();
            }
        }

        private void EnforceRetention()
        {
            DateTime cutoff = _clock().ToLocalTime().Date.AddDays(-_retentionDays + 1);
            foreach (string path in Directory.EnumerateFiles(_directory, "trend-*.jsonl").ToList())
            {
                if (TryParseDay(path, out DateTime day) && day < cutoff)
                    File.Delete(path);
            }
            EnforceBytes();
        }

        private void EnforceBytes()
        {
            var files = Directory.EnumerateFiles(_directory, "trend-*.jsonl")
                .Select(p => new FileInfo(p)).OrderBy(f => f.Name).ToList();
            long total = files.Sum(f => f.Length);
            foreach (FileInfo file in files)
            {
                if (total <= _maxBytes) break;
                long length = file.Length;
                file.Delete();
                total -= length;
            }
        }

        private TrendHistoryManifest LoadManifest()
        {
            try
            {
                if (File.Exists(_manifestPath))
                {
                    TrendHistoryManifest? manifest = JsonSerializer.Deserialize<TrendHistoryManifest>(
                        File.ReadAllText(_manifestPath), JsonOptions);
                    if (manifest is { Version: CurrentVersion }) return manifest;
                }
            }
            catch (JsonException)
            {
                // 损坏的 manifest 不阻断启动；由现有分段重建最小 manifest。
            }
            return new TrendHistoryManifest(CurrentVersion, null, 0, _clock());
        }

        private void WriteManifest()
        {
            string temp = _manifestPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_manifest, JsonOptions));
            File.Move(temp, _manifestPath, overwrite: true);
        }

        private static bool TryParseDay(string path, out DateTime day)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return DateTime.TryParseExact(name.Length > 6 ? name[6..] : string.Empty, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
        }
    }
}
