using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.WebServer;
using Xunit;

namespace LiteMonitor.Tests
{
    [Collection("cfgfile")]
    public class ReviewMetricPresentationRegressionTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void F06_LogicalUsageSurvivesColdHotAndRefreshedPhysicalCache(bool lowerCaseDrive)
        {
            WithDiskManager((manager, clock) =>
            {
                // Computer is never opened. Its real Hardware getter enumerates only this fake group.
                var computer = new Computer();
                var firstRead = new FakeSensor("Read Rate", SensorType.Throughput, 4096f);
                var firstWrite = new FakeSensor("Write Rate", SensorType.Throughput, 8192f);
                var secondRead = new FakeSensor("Read Rate", SensorType.Throughput, 0f);
                var first = new FakeDisk("ReviewFirstDisk", firstRead, firstWrite,
                    new FakeSensor("Temperature", SensorType.Temperature, 42f));
                var second = new FakeDisk("ReviewSecondDisk", secondRead,
                    new FakeSensor("Write Rate", SensorType.Throughput, 0f),
                    new FakeSensor("Temperature", SensorType.Temperature, 52f));
                var groups = (IList)PrivateField(typeof(Computer), "_groups", false).GetValue(computer);
                object group = CreateFakeHardwareGroup(first, second);
                var cfg = new Settings();
                var lastValid = new Dictionary<string, float>();
                var sync = new object();
                DriveInfo drive = DriveInfo.GetDrives().FirstOrDefault(d =>
                    d.IsReady && d.TotalSize > 0 && d.Name.Length >= 2 && char.IsAsciiLetter(d.Name[0]));
                Assert.NotNull(drive);
                string letter = drive.Name.Substring(0, 1);
                if (lowerCaseDrive) letter = letter.ToLowerInvariant();
                string usedKey = "DISK." + letter + ".Used";

                float? Read(string key) => manager.GetBestValue(key, computer, cfg, lastValid, sync);
                void AssertLogicalUsage()
                {
                    // Bracket the real read to allow actual disk activity between free-space samples.
                    float before = DriveUsedPercent(drive);
                    float? actual = Read(usedKey);
                    float after = DriveUsedPercent(drive);
                    Assert.True(actual.HasValue, usedKey + " was hidden by the physical disk cache");
                    Assert.InRange(actual.Value, 0f, 100f);
                    Assert.InRange(actual.Value, Math.Min(before, after) - 0.1f, Math.Max(before, after) + 0.1f);
                }

                try
                {
                    groups.Add(group);
                    Assert.Equal(new IHardware[] { first, second }, computer.Hardware.ToArray());
                    AssertLogicalUsage();
                    Assert.True(string.IsNullOrEmpty(cfg.LastAutoDisk));

                    Assert.Equal(4096f, Read("DISK.Read"));
                    Assert.Equal(first.Name, cfg.LastAutoDisk);
                    AssertLogicalUsage();
                    Assert.Equal(8192f, Read("DISK.Write"));
                    Assert.Equal(42f, Read("DISK.Temp"));
                    AssertLogicalUsage();

                    // Zero traffic permits re-selection only when the real ten-second cache expires.
                    firstRead.Value = 0f;
                    firstWrite.Value = 0f;
                    secondRead.Value = 16384f;
                    clock.Advance(TimeSpan.FromMilliseconds(9999));
                    Assert.Equal(0f, Read("DISK.Read"));
                    Assert.Equal(first.Name, cfg.LastAutoDisk);
                    AssertLogicalUsage();

                    clock.Advance(TimeSpan.FromMilliseconds(1));
                    Assert.Equal(16384f, Read("DISK.Read"));
                    Assert.Equal(second.Name, cfg.LastAutoDisk);
                    Assert.Equal(52f, Read("DISK.Temp"));
                    AssertLogicalUsage();
                    Assert.Equal(0, first.UpdateCount);
                    Assert.Equal(0, second.UpdateCount);
                    Assert.DoesNotContain(usedKey, lastValid.Keys);
                }
                finally
                {
                    groups.Remove(group);
                }
            });
        }

        [Theory]
        [InlineData("DISK.Read")]
        [InlineData("DISK.Write")]
        [InlineData("DISK.Temp")]
        [InlineData("DISK.C.Temp")]
        [InlineData("DISK.C.used")]
        [InlineData("DISK.C.Used.extra")]
        [InlineData("DISK.1.Used")]
        [InlineData("DISK.CC.Used")]
        [InlineData("OTHER.C.Used")]
        public void F06_OnlyCompleteLogicalUsedKeysAccessDriveInfo(string key)
        {
            WithDiskManager((manager, _) =>
            {
                var lastValid = new Dictionary<string, float> { [key] = 17f };
                Assert.Equal(17f, manager.GetBestValue(key, new Computer(), new Settings(), lastValid, new object()));
                Assert.Empty(DriveInfoCache());
            });
        }

        [Fact]
        public void F06_NotReadyDriveKeepsExistingLastValueFallback()
        {
            WithDiskManager((manager, _) =>
            {
                var drive = Enumerable.Range('A', 26).Select(c => new DriveInfo((char)c + ":\\"))
                    .FirstOrDefault(d => !d.IsReady);
                Assert.NotNull(drive);
                string key = "DISK." + drive.Name[0] + ".Used";
                var lastValid = new Dictionary<string, float> { [key] = 9f };
                Assert.Equal(9f, manager.GetBestValue(key, new Computer(), new Settings(), lastValid, new object()));
                Assert.Single(DriveInfoCache());
            });
        }

        [Theory]
        [InlineData("us.NDX", "NDX", "usNDX", "usNDX")]
        [InlineData("usNDX", "NDX", "usNDX", "usNDX")]
        [InlineData("us.AAPL", "AAPL", "usAAPL", "usAAPL")]
        [InlineData("usAAPL.OQ", "AAPL", "usAAPL", "usAAPL")]
        [InlineData("sh600519.SH", "600519", "sh600519", "sh600519")]
        [InlineData("sz000001.SZ", "000001", "sz000001", "sz000001")]
        [InlineData("hk00700.HK", "00700", "hk00700", "hk00700")]
        [InlineData("sh000001", "index", "sh000001", "sh000001")]
        [InlineData("sz399001", "index", "sz399001", "sz399001")]
        [InlineData("hk00700", "Tencent", "hk00700", "hk00700")]
        [InlineData("", "usNDX", "", "usNDX")]
        [InlineData("?", "sh000001", "", "sh000001")]
        [InlineData(null, "hk00700", null, "hk00700")]
        public void F07_BundledStockTransformsAndFetchFallbackRemainCompatible(
            string searchCode, string query, string expectedCode, string expectedFetchCode)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "resources", "plugins", "Stock.json");
            Assert.True(File.Exists(path), "The shipped Stock template must be copied to the test output.");
            var template = JsonSerializer.Deserialize<PluginTemplate>(File.ReadAllText(path));
            Assert.NotNull(template);
            var search = Assert.Single(template.Execution.Steps.Where(s => s.Id == "search_stock"));
            var fetch = Assert.Single(template.Execution.Steps.Where(s => s.Id == "fetch_stock"));
            var context = new Dictionary<string, string> { ["query"] = query };
            if (searchCode != null) context["code"] = searchCode;

            PluginProcessor.ApplyTransforms(search.Process, context);

            if (expectedCode == null) Assert.False(context.ContainsKey("code"));
            else Assert.Equal(expectedCode, context["code"]);
            Assert.Equal("http://qt.gtimg.cn/q=" + expectedFetchCode,
                PluginProcessor.ResolveTemplate(fetch.Url, context));
        }

        [Theory]
        [InlineData("%", 32f, "50.0", "%")]
        [InlineData(null, 32f, "16.0", "GB")]
        [InlineData("", 32f, "16.0", "")]
        [InlineData("{u}", 32f, "16.0", "GB")]
        [InlineData("{u}%", 32f, "16.0", "GB%")]
        [InlineData(null, 0f, "50.0", "%")]
        [InlineData("{u}", 0f, "50.0", "%")]
        [InlineData("", 0f, "50.0", "")]
        public void F08_SnapshotMemoryUsesItemFormatAndRawProgressAndStatus(
            string unitPanel, float totalGb, string value, string unit)
        {
            WithPresentationSettings(cfg =>
            {
                Settings.DetectedRamTotalGB = totalGb;
                var row = Assert.Single(SnapshotRows(new MonitorItemConfig { Key = "MEM.Load", UnitPanel = unitPanel },
                    key => { Assert.Equal("MEM.Load", key); return 50f; }));
                AssertRow(row, "MEM.Load", value, unit, 50d, MetricUtils.STATE_WARN);
                Assert.True(row.GetProperty("primary").GetBoolean());
            });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("%")]
        public void F08_PercentGlobalModeKeepsPercentPresentation(string unitPanel)
        {
            WithPresentationSettings(cfg =>
            {
                cfg.MemoryDisplayMode = 0;
                var row = Assert.Single(SnapshotRows(new MonitorItemConfig { Key = "MEM.Load", UnitPanel = unitPanel }, _ => 50f));
                AssertRow(row, "MEM.Load", "50.0", "%", 50d, MetricUtils.STATE_WARN);
            });
        }

        [Theory]
        [InlineData(null, "48.0 / 64.0", "GB")]
        [InlineData("%", "75.0", "%")]
        [InlineData("", "48.0 / 64.0", "")]
        [InlineData("{u}", "48.0 / 64.0", "GB")]
        public void F08_SnapshotCommittedMemoryUsesPublishedLimitAndItemFormat(string unitPanel, string value, string unit)
        {
            WithPresentationSettings(cfg =>
            {
                const ulong pagesPerGiB = 1024UL * 1024 * 1024 / 4096;
                var raw = new CommittedMemoryRaw(4096, 48 * pagesPerGiB, 64 * pagesPerGiB);
                CommittedMemoryPublisher.Publish(7, CommittedMemoryCalculator.Compute(raw, 7, 1000));
                var row = Assert.Single(SnapshotRows(
                    new MonitorItemConfig { Key = MetricUtils.CommittedMemoryKey, UnitPanel = unitPanel },
                    _ => CommittedMemoryPublisher.TryGetLoadPercent()));
                AssertRow(row, MetricUtils.CommittedMemoryKey, value, unit, 75d, MetricUtils.STATE_WARN);
            });
        }

        [Theory]
        [InlineData("GPU.0.VRAM", null, "4.00", "GB")]
        [InlineData("GPU.1.VRAM", null, "12.0", "GB")]
        [InlineData("GPU.1.VRAM", "%", "50.0", "%")]
        [InlineData("GPU.1.VRAM", "", "12.0", "")]
        [InlineData("GPU.1.VRAM", "{u}", "12.0", "GB")]
        [InlineData("GPU.2.VRAM", null, "50.0", "%")]
        public void F08_SnapshotVramUsesEachCardsCapacity(string key, string unitPanel, string value, string unit)
        {
            WithPresentationSettings(cfg =>
            {
                Settings.DetectedGpuVramTotalGBByKey["GPU.0"] = 8f;
                Settings.DetectedGpuVramTotalGBByKey["GPU.1"] = 24f;
                var row = Assert.Single(SnapshotRows(new MonitorItemConfig { Key = key, UnitPanel = unitPanel }, _ => 50f));
                AssertRow(row, key, value, unit, 50d, MetricUtils.STATE_WARN);
                Assert.False(row.GetProperty("primary").GetBoolean());
            });
        }

        [Theory]
        [InlineData(false, true, null, " FPS")]
        [InlineData(true, false, null, " FPS")]
        [InlineData(false, false, "", "")]
        [InlineData(false, true, "frames", "frames")]
        public void F08_SnapshotUnavailableFpsNeverReadsValue(bool demand, bool hasData, string unitPanel, string unit)
        {
            WithPresentationSettings(cfg =>
            {
                var row = Assert.Single(SnapshotRows(new MonitorItemConfig { Key = "FPS", UnitPanel = unitPanel },
                    _ => throw new InvalidOperationException("FPS must not be read"), demand, hasData));
                AssertRow(row, "FPS", "--", unit, 0d, MetricUtils.STATE_SAFE);
            });
        }

        [Fact]
        public void F08_SnapshotTrustedZeroFpsIsDataAndMissingHardwareIsOmitted()
        {
            WithPresentationSettings(cfg =>
            {
                int reads = 0;
                var row = Assert.Single(SnapshotRows(new MonitorItemConfig { Key = "FPS" },
                    _ => { reads++; return 0f; }, true, true));
                Assert.Equal(1, reads);
                AssertRow(row, "FPS", "0", " FPS", 5d, MetricUtils.STATE_SAFE);
                Assert.Empty(SnapshotRows(new MonitorItemConfig { Key = "BAT.Percent" }, _ => null));
                Assert.Empty(SnapshotRows(new MonitorItemConfig { Key = MetricUtils.CommittedMemoryKey },
                    _ => CommittedMemoryPublisher.TryGetLoadPercent()));
            });
        }

        [Theory]
        [InlineData(null, "+1.2% 123.4", "+1.2% 123.4")]
        [InlineData("%", "+1.2% 123.4", "+1.2% 123.4")]
        [InlineData("", "+1.2% 123.4", "+1.2% 123.4")]
        [InlineData("{u}", "", "--")]
        public void F08_SnapshotDashPreservesPluginTextUnitAndColor(string unitPanel, string text, string expectedValue)
        {
            var item = new MonitorItemConfig { Key = "DASH.Review.Stock.main", UnitPanel = unitPanel };
            var info = new Dictionary<string, string>
            {
                ["Review.Stock.main"] = text,
                ["Review.Stock.main.Unit"] = "USD",
                ["Review.Stock.main.Color"] = "2"
            };
            var row = Assert.Single(SnapshotRows(item,
                _ => throw new InvalidOperationException("DASH must not read hardware"),
                readInfo: key => info[key]));
            AssertRow(row, item.Key, expectedValue, "USD", 0d, 2);
        }

        [Fact]
        public void F08_BothPublicResolveValueOverloadsKeepDefaultFormattingAndFpsGuards()
        {
            WithPresentationSettings(cfg =>
            {
                Assert.Equal(WebSnapshotValueState.HasData,
                    WebSnapshotValues.ResolveValue("MEM.Load", true, _ => 50f, out string oldValue, out float? oldRaw));
                Assert.Equal("16.0", oldValue);
                Assert.Equal(50f, oldRaw);
                Assert.Equal(WebSnapshotValueState.HasData,
                    WebSnapshotValues.ResolveValue("MEM.Load", true, true, _ => 50f, out string value, out float? raw));
                Assert.Equal(oldValue, value);
                Assert.Equal(oldRaw, raw);

                float? MustNotRead(string key) => throw new InvalidOperationException("FPS must not be read");
                Assert.Equal(WebSnapshotValueState.NotEnabled,
                    WebSnapshotValues.ResolveValue("FPS", false, MustNotRead, out value, out raw));
                Assert.Equal("--", value);
                Assert.Null(raw);
                Assert.Equal(WebSnapshotValueState.NotEnabled,
                    WebSnapshotValues.ResolveValue("FPS", true, false, MustNotRead, out value, out raw));
                Assert.Equal("--", value);
                Assert.Null(raw);
            });
        }

        [Fact]
        public void F08_LiveSnapshotCallsTheBehaviorTestedRowWriter()
        {
            var snapshot = typeof(LiteWebServer).GetMethod("WriteSnapshotJson", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(snapshot);
            var scan = IlInspector.Scan(snapshot);
            Assert.Equal(0, scan.UnresolvedCalls);
            Assert.Contains(scan.Calls, call => call.DeclaringType == typeof(LiteWebServer) &&
                call.Name == nameof(LiteWebServer.WriteSnapshotItemJson));
        }

        private static JsonElement[] SnapshotRows(MonitorItemConfig item, Func<string, float?> readValue,
            bool fpsDemand = false, bool hasFpsData = true, Func<string, string> readInfo = null)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                LiteWebServer.WriteSnapshotItemJson(writer, item, "Review metric", "review", "Review group", 3,
                    fpsDemand, hasFpsData, readValue,
                    readInfo ?? (_ => throw new InvalidOperationException("Hardware must not read DASH values")));
                writer.WriteEndArray();
            }
            using var document = JsonDocument.Parse(stream.ToArray());
            return document.RootElement.EnumerateArray().Select(row => row.Clone()).ToArray();
        }

        private static void AssertRow(JsonElement row, string key, string value, string unit, double pct, int status)
        {
            Assert.Equal(key, row.GetProperty("k").GetString());
            Assert.Equal(value, row.GetProperty("v").GetString());
            Assert.Equal(unit, row.GetProperty("u").GetString());
            Assert.Equal(pct, row.GetProperty("pct").GetDouble(), 6);
            Assert.Equal(status, row.GetProperty("sts").GetInt32());
            Assert.Equal("Review metric", row.GetProperty("n").GetString());
            Assert.Equal("review", row.GetProperty("gid").GetString());
            Assert.Equal("Review group", row.GetProperty("gn").GetString());
            Assert.Equal(3, row.GetProperty("gidx").GetInt32());
        }

        private static void WithPresentationSettings(Action<Settings> body)
        {
            var instance = PrivateField(typeof(Settings), "_instance", true);
            object previousInstance = instance.GetValue(null);
            var publisher = PrivateField(typeof(CommittedMemoryPublisher), "_current", true);
            object previousPublication = publisher.GetValue(null);
            float previousRam = Settings.DetectedRamTotalGB;
            float previousVram = Settings.DetectedGpuVramTotalGB;
            var previousCards = Settings.DetectedGpuVramTotalGBByKey.ToArray();
            var previousCulture = CultureInfo.CurrentCulture;
            // No Load(forceReload), Save, directory overrides, or save-guard changes: no configuration I/O.
            try
            {
                var cfg = new Settings { MemoryDisplayMode = 1 };
                cfg.Thresholds.Load.Warn = 45;
                cfg.Thresholds.Load.Crit = 90;
                instance.SetValue(null, cfg);
                Settings.DetectedRamTotalGB = 32f;
                Settings.DetectedGpuVramTotalGB = 0f;
                Settings.DetectedGpuVramTotalGBByKey.Clear();
                CommittedMemoryPublisher.Clear();
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                body(cfg);
            }
            finally
            {
                instance.SetValue(null, previousInstance);
                publisher.SetValue(null, previousPublication);
                Settings.DetectedRamTotalGB = previousRam;
                Settings.DetectedGpuVramTotalGB = previousVram;
                Settings.DetectedGpuVramTotalGBByKey.Clear();
                foreach (var card in previousCards) Settings.DetectedGpuVramTotalGBByKey[card.Key] = card.Value;
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        private static float DriveUsedPercent(DriveInfo drive)
            => 100f - (float)((double)drive.TotalFreeSpace / drive.TotalSize * 100.0);

        private static Dictionary<string, DriveInfo> DriveInfoCache()
            => (Dictionary<string, DriveInfo>)PrivateField(typeof(DiskManager), "_driveInfoCache", true).GetValue(null);

        private static void WithDiskManager(Action<DiskManager, ManualClock> body)
        {
            var cache = DriveInfoCache();
            var previous = cache.ToArray();
            var clock = new ManualClock();
            var manager = new DiskManager(clock);
            try
            {
                manager.ClearCache();
                body(manager, clock);
            }
            finally
            {
                manager.ClearCache();
                foreach (var entry in previous) cache[entry.Key] = entry.Value;
            }
        }

        private static object CreateFakeHardwareGroup(params Hardware[] disks)
        {
            // IGroup is internal in LHM. Reuse only its list-backed getter; skip all constructors.
            Type groupType = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Memory.MemoryGroup", true);
            object group = RuntimeHelpers.GetUninitializedObject(groupType);
            PrivateField(groupType, "_hardware", false).SetValue(group, disks.ToList());
            return group;
        }

        private static FieldInfo PrivateField(Type type, string name, bool isStatic)
        {
            var field = type.GetField(name, BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance));
            Assert.NotNull(field);
            return field;
        }

        private sealed class ManualClock : TimeProvider
        {
            private long _ticks;
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => _ticks;
            public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
        }

        private sealed class FakeHardwareSettings : ISettings
        {
            public bool Contains(string name) => false;
            public void SetValue(string name, string value) { }
            public string GetValue(string name, string value) => value;
            public void Remove(string name) { }
        }

        private sealed class FakeDisk : Hardware
        {
            private readonly ISensor[] _sensors;
            public FakeDisk(string name, params ISensor[] sensors)
                : base(name, new Identifier(new[] { name }), new FakeHardwareSettings()) => _sensors = sensors;
            public override HardwareType HardwareType => LibreHardwareMonitor.Hardware.HardwareType.Storage;
            public override ISensor[] Sensors => _sensors;
            public int UpdateCount { get; private set; }
            public override void Update() => UpdateCount++;
        }

        private sealed class FakeSensor : ISensor
        {
            public FakeSensor(string name, SensorType type, float value)
            {
                Name = name;
                SensorType = type;
                Value = value;
            }
            public string Name { get; set; }
            public float? Value { get; set; }
            public SensorType SensorType { get; }
            public IControl Control => null;
            public IHardware Hardware => null;
            public Identifier Identifier => default;
            public int Index => 0;
            public bool IsDefaultHidden => false;
            public float? Max => null;
            public float? Min => null;
            public IReadOnlyList<IParameter> Parameters => Array.Empty<IParameter>();
            public IEnumerable<SensorValue> Values => Array.Empty<SensorValue>();
            public TimeSpan ValuesTimeWindow { get; set; }
            public void ResetMin() { }
            public void ResetMax() { }
            public void ClearValues() { }
            public void Accept(IVisitor visitor) { }
            public void Traverse(IVisitor visitor) { }
        }
    }
}
