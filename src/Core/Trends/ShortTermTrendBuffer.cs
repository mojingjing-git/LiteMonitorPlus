using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.Contracts;

namespace LiteMonitor.src.Core.Trends
{
    /// <summary>T02：秒级短时趋势桶。每指标最多 3600 个，32 指标合计 115,200。</summary>
    public sealed record TrendBucket(
        DateTimeOffset TimeUtc,
        float? Value,
        SampleQuality Quality,
        double SampleAgeMs,
        string? StableDeviceId,
        float? Max = null,
        float? Min = null,
        int Count = 1);

    public sealed record TrendSeriesKey(string MetricKey, string? StableDeviceId);

    public sealed record TrendBufferStats(
        int SeriesCount,
        int PointCount,
        int MaxSeries,
        int CapacityPerSeries,
        long AcceptedSnapshots,
        long DuplicateBatches,
        long OutOfOrderBatches,
        long RejectedSamples,
        long SessionResets,
        long DroppedPoints);

    public sealed class ShortTermTrendBuffer
    {
        public const int DefaultCapacityPerSeries = 3600;
        public const int DefaultMaxSeries = 32;

        private readonly object _gate = new();
        private readonly Dictionary<TrendSeriesKey, Queue<TrendBucket>> _series =
            new Dictionary<TrendSeriesKey, Queue<TrendBucket>>();
        private readonly int _capacityPerSeries;
        private readonly int _maxSeries;

        private long _lastBatchId = -1;
        private int _lastSessionEpoch;
        private bool _hasBatch;
        private long _acceptedSnapshots;
        private long _duplicateBatches;
        private long _outOfOrderBatches;
        private long _rejectedSamples;
        private long _sessionResets;
        private long _droppedPoints;

        public ShortTermTrendBuffer(
            int capacityPerSeries = DefaultCapacityPerSeries,
            int maxSeries = DefaultMaxSeries)
        {
            if (capacityPerSeries <= 0) throw new ArgumentOutOfRangeException(nameof(capacityPerSeries));
            if (maxSeries <= 0) throw new ArgumentOutOfRangeException(nameof(maxSeries));
            _capacityPerSeries = capacityPerSeries;
            _maxSeries = maxSeries;
        }

        public IngestResult Ingest(HardwareSnapshot snapshot, DateTimeOffset observedAtUtc)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            lock (_gate)
            {
                if (_hasBatch && snapshot.SessionEpoch != _lastSessionEpoch)
                {
                    // Host 重启/采集器重建：旧会话的秒级明细不混入新会话。
                    _series.Clear();
                    _hasBatch = false;
                    _sessionResets++;
                }

                if (_hasBatch)
                {
                    if (snapshot.BatchId == _lastBatchId)
                    {
                        _duplicateBatches++;
                        return IngestResult.Duplicate;
                    }
                    if (snapshot.BatchId < _lastBatchId)
                    {
                        _outOfOrderBatches++;
                        return IngestResult.OutOfOrder;
                    }
                }

                _lastBatchId = snapshot.BatchId;
                _lastSessionEpoch = snapshot.SessionEpoch;
                _hasBatch = true;
                _acceptedSnapshots++;

                int accepted = 0;
                int rejected = 0;
                foreach (MetricSample sample in snapshot.Samples)
                {
                    if (string.IsNullOrWhiteSpace(sample.Key) || sample.Value is null)
                    {
                        rejected++;
                        continue;
                    }

                    var key = new TrendSeriesKey(sample.Key, sample.StableDeviceId);
                    if (!_series.TryGetValue(key, out Queue<TrendBucket>? queue))
                    {
                        if (_series.Count >= _maxSeries)
                        {
                            rejected++;
                            continue;
                        }
                        queue = new Queue<TrendBucket>(_capacityPerSeries);
                        _series.Add(key, queue);
                    }

                    queue.Enqueue(new TrendBucket(
                        observedAtUtc,
                        sample.Value,
                        sample.Quality,
                        sample.SampleAgeMs,
                        sample.StableDeviceId));

                    if (queue.Count > _capacityPerSeries)
                    {
                        queue.Dequeue();
                        _droppedPoints++;
                    }
                    accepted++;
                }

                _rejectedSamples += rejected;
                return new IngestResult(true, accepted, rejected, false);
            }
        }

        public IReadOnlyList<TrendBucket> GetSeries(string metricKey, string? stableDeviceId = null)
        {
            lock (_gate)
            {
                return _series.TryGetValue(new TrendSeriesKey(metricKey, stableDeviceId), out Queue<TrendBucket>? queue)
                    ? queue.ToArray()
                    : Array.Empty<TrendBucket>();
            }
        }

        public TrendBufferStats GetStats()
        {
            lock (_gate)
            {
                return new TrendBufferStats(
                    _series.Count,
                    _series.Values.Sum(q => q.Count),
                    _maxSeries,
                    _capacityPerSeries,
                    _acceptedSnapshots,
                    _duplicateBatches,
                    _outOfOrderBatches,
                    _rejectedSamples,
                    _sessionResets,
                    _droppedPoints);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _series.Clear();
                _hasBatch = false;
                _lastBatchId = -1;
                _lastSessionEpoch = 0;
            }
        }
    }

    public readonly record struct IngestResult(
        bool Accepted,
        int AcceptedSamples,
        int RejectedSamples,
        bool SessionReset)
    {
        public static IngestResult Duplicate => new(false, 0, 0, false);
        public static IngestResult OutOfOrder => new(false, 0, 0, false);
    }
}
