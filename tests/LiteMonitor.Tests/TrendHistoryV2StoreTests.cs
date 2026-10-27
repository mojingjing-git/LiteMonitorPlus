using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LiteMonitor.Contracts;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Trends;
using Xunit;

namespace LiteMonitor.Tests
{
    public class TrendHistoryV2StoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lm-trend-v2-" + Guid.NewGuid().ToString("N"));

        public TrendHistoryV2StoreTests()
        {
            Directory.CreateDirectory(_dir);
        }

        [Fact]
        public void AppendAndReadSince_AcrossDailyFiles()
        {
            var clock = new MutableClock(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
            var store = new TrendHistoryV2Store(_dir, clock: () => clock.Now);
            DateTimeOffset t1 = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
            DateTimeOffset t2 = new(2026, 10, 26, 10, 1, 0, TimeSpan.Zero);
            DateTimeOffset t3 = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

            int written = store.Append(new[]
            {
                new TrendHistoryPointRecord(t1, "CPU.Load", null, 12, SampleQuality.Good, 100),
                new TrendHistoryPointRecord(t2, "CPU.Load", null, 13, SampleQuality.Good, 100),
                new TrendHistoryPointRecord(t3, "GPU.Load", "gpu-1", 44, SampleQuality.Stale, 900),
            });

            Assert.Equal(3, written);
            Assert.Equal(3, store.Manifest.TotalPoints);
            Assert.Equal(t3, store.Manifest.LastImportedTimestampUtc);
            Assert.Equal(2, Directory.GetFiles(_dir, "trend-*.jsonl").Length);

            TrendHistoryReadResult read = store.ReadSince(t1.AddMinutes(-1));
            Assert.Equal(3, read.Points.Count);
            Assert.Equal(0, read.CorruptLines);
            Assert.Equal("GPU.Load", read.Points[2].MetricKey);
            Assert.Equal(SampleQuality.Stale, read.Points[2].Quality);
        }

        [Fact]
        public void ImportV1_IsIncrementalAndIdempotent()
        {
            string legacyPath = Path.Combine(_dir, "HardwareHistory.json");
            var legacy = new HardwareHistoryData
            {
                Points =
                {
                    new HardwareHistoryPoint
                    {
                        Time = new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Local),
                        Values = { ["CPU.Temp"] = new HardwareHistoryValue { Avg = 55, Max = 60, Min = 50, Count = 6 } },
                    },
                    new HardwareHistoryPoint
                    {
                        Time = new DateTime(2026, 10, 26, 8, 1, 0, DateTimeKind.Local),
                        Values = { ["CPU.Temp"] = new HardwareHistoryValue { Avg = 56, Max = 61, Min = 51, Count = 6 } },
                    },
                },
            };
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacy));

            var store = new TrendHistoryV2Store(_dir, clock: () => new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal(2, store.ImportV1(legacyPath));
            Assert.Equal(0, store.ImportV1(legacyPath));

            legacy.Points.Add(new HardwareHistoryPoint
            {
                Time = new DateTime(2026, 10, 26, 8, 2, 0, DateTimeKind.Local),
                Values = { ["CPU.Temp"] = new HardwareHistoryValue { Avg = 57, Max = 62, Min = 52, Count = 6 } },
            });
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacy));
            Assert.Equal(1, store.ImportV1(legacyPath));

            TrendHistoryReadResult read = store.ReadSince(DateTimeOffset.MinValue);
            Assert.Equal(3, read.Points.Count);
            Assert.All(read.Points, p => Assert.Equal("CPU.Temp", p.MetricKey));
        }

        [Fact]
        public void ReadSince_SkipsCorruptLineAndCountsIt()
        {
            var store = new TrendHistoryV2Store(_dir, clock: () => new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
            store.Append(new[]
            {
                new TrendHistoryPointRecord(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero), "CPU.Load", null, 1, SampleQuality.Good, 0),
            });
            string file = Directory.GetFiles(_dir, "trend-*.jsonl").Single();
            File.AppendAllText(file, "{ this is not json\n");

            TrendHistoryReadResult read = store.ReadSince(DateTimeOffset.MinValue);
            Assert.Single(read.Points);
            Assert.Equal(1, read.CorruptLines);
        }

        [Fact]
        public void EnforceBytes_DropsOldestSegmentsFirst()
        {
            var clock = new MutableClock(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
            // 单条约 123 B；4 天约 496 B。上限必须低于 4 条、高于 3 条，才能触发“删最旧分段”。
            var store = new TrendHistoryV2Store(_dir, maxBytes: 400, clock: () => clock.Now);
            for (int day = 24; day <= 27; day++)
            {
                clock.Now = new DateTimeOffset(2026, 10, day, 9, 0, 0, TimeSpan.Zero);
                store.Append(new[]
                {
                    new TrendHistoryPointRecord(clock.Now, "CPU.Load", null, day, SampleQuality.Good, 0),
                });
            }

            string[] files = Directory.GetFiles(_dir, "trend-*.jsonl").OrderBy(p => p).ToArray();
            Assert.True(files.Length < 4);
            Assert.EndsWith("trend-2026-10-27.jsonl", files.Last());
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private sealed class MutableClock
        {
            public MutableClock(DateTimeOffset now) => Now = now;
            public DateTimeOffset Now { get; set; }
        }
    }
}
