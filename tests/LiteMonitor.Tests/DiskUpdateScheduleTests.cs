using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 磁盘更新节奏与冷却规则（计划 §4.3 / §6.1）的行为测试。
    ///
    /// - 用**假时钟**驱动，不用几秒/几分钟的 Sleep 验证定时逻辑；
    /// - 用假的 IHardware 桩（继承 LHM 公开的 Hardware 基类）记录**真实 Update 次数**，
    ///   不启动 LHM / 驱动 / 真实硬件。
    /// </summary>
    public class DiskUpdateScheduleTests
    {
        /// <summary>假时钟：单调时间戳与墙上时间独立。</summary>
        private sealed class FakeTimeProvider : TimeProvider
        {
            private long _ticks;
            private DateTimeOffset _wallClock;

            public FakeTimeProvider()
            {
                _wallClock = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
            }

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;

            public override long GetTimestamp() => Interlocked.Read(ref _ticks);

            public override DateTimeOffset GetUtcNow() => _wallClock;

            /// <summary>推进单调时间（同时推进墙上时间）。</summary>
            public void Advance(TimeSpan delta)
            {
                Interlocked.Add(ref _ticks, delta.Ticks);
                _wallClock += delta;
            }
        }

        /// <summary>空的 ISettings 实现（LHM 的 Hardware 基类构造函数需要它）。</summary>
        private sealed class FakeSettings : ISettings
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

            public bool Contains(string name) => _values.ContainsKey(name);

            public void SetValue(string name, string value) => _values[name] = value;

            public string GetValue(string name, string value) => _values.TryGetValue(name, out var v) ? v : value;

            public void Remove(string name) => _values.Remove(name);
        }

        /// <summary>假的 Throughput 传感器（用来制造"有读写活动"的场景）。</summary>
        private sealed class FakeThroughputSensor : ISensor
        {
            public FakeThroughputSensor(string name, float value)
            {
                Name = name;
                Value = value;
            }

            public string Name { get; set; }
            public float? Value { get; set; }
            public SensorType SensorType => SensorType.Throughput;
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

            // IElement
            public void Accept(IVisitor visitor) { }
            public void Traverse(IVisitor visitor) { }
        }

        /// <summary>假的磁盘硬件：只记录真实 Update 次数，不接触驱动。</summary>
        private sealed class FakeStorageHardware : Hardware
        {
            private readonly List<ISensor> _sensors;

            public FakeStorageHardware(string name, params ISensor[] sensors)
                : base(name, new Identifier(new[] { name }), new FakeSettings())
            {
                _sensors = sensors.ToList();
            }

            public override HardwareType HardwareType => LibreHardwareMonitor.Hardware.HardwareType.Storage;

            public override ISensor[] Sensors => _sensors.ToArray();

            /// <summary>真实执行过的 Update 次数。</summary>
            public int UpdateCount { get; private set; }

            public override void Update() => UpdateCount++;
        }

        // =========================================================
        // 活跃后台磁盘：不快于每 3 秒一次
        // =========================================================

        [Fact]
        public void 活跃后台磁盘不快于每3秒一次()
        {
            var clock = new FakeTimeProvider();
            var manager = new DiskManager(clock);
            var disk = new FakeStorageHardware("E: TestDisk");
            var cfg = new Settings();

            // 第一次：允许第一次更新
            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(1, disk.UpdateCount);

            // 即使调用方一直说"慢扫描 / 后台扫描"，1 秒后也不应该再更新
            clock.Advance(TimeSpan.FromSeconds(1));
            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(1, disk.UpdateCount);

            // 累计到 3 秒才允许更新
            clock.Advance(TimeSpan.FromSeconds(2));
            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(2, disk.UpdateCount);

            clock.Advance(TimeSpan.FromSeconds(2));
            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(2, disk.UpdateCount);

            clock.Advance(TimeSpan.FromSeconds(1));
            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(3, disk.UpdateCount);
        }

        // =========================================================
        // 冷却中的后台磁盘：不快于每 10 秒一次
        // =========================================================

        [Fact]
        public void 冷却中的后台磁盘不快于每10秒一次()
        {
            var clock = new FakeTimeProvider();
            var manager = new DiskManager(clock);
            var sensor = new FakeThroughputSensor("Read", 5000);   // 有读写活动
            var disk = new FakeStorageHardware("E: TestDisk", sensor);
            var cfg = new Settings();

            // 第一次更新：有流量 -> 记录活跃时间
            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(1, disk.UpdateCount);

            // 2 分钟没有活动 -> 进入冷却模式（空闲 > 1 分钟）
            clock.Advance(TimeSpan.FromMinutes(2));
            sensor.Value = 0;   // 磁盘不再有读写
            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(2, disk.UpdateCount);

            // 冷却中：1 秒后不再更新
            clock.Advance(TimeSpan.FromSeconds(1));
            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(2, disk.UpdateCount);

            // 冷却中：累计到 10 秒才允许更新
            clock.Advance(TimeSpan.FromSeconds(9));
            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(3, disk.UpdateCount);
        }

        // =========================================================
        // 深睡 / 目标盘 / 手动指定
        // =========================================================

        [Fact]
        public void 深睡磁盘不再更新()
        {
            var clock = new FakeTimeProvider();
            var manager = new DiskManager(clock);
            var sensor = new FakeThroughputSensor("Read", 5000);
            var disk = new FakeStorageHardware("E: TestDisk", sensor);
            var cfg = new Settings();

            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(1, disk.UpdateCount);

            // 空闲超过 5 分钟 -> 深睡，保持原有休眠规则
            clock.Advance(TimeSpan.FromMinutes(6));
            sensor.Value = 0;
            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(1, disk.UpdateCount);

            clock.Advance(TimeSpan.FromMinutes(1));
            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(1, disk.UpdateCount);
        }

        [Fact]
        public void 目标磁盘跟随采集周期更新()
        {
            var clock = new FakeTimeProvider();
            var manager = new DiskManager(clock);
            var disk = new FakeStorageHardware("E: TestDisk");
            var cfg = new Settings { LastAutoDisk = disk.Name };

            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(1, disk.UpdateCount);

            clock.Advance(TimeSpan.FromSeconds(1));
            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(2, disk.UpdateCount);
        }

        [Fact]
        public void 手动指定磁盘时只更新被指定的设备()
        {
            var clock = new FakeTimeProvider();
            var manager = new DiskManager(clock);
            var preferred = new FakeStorageHardware("E: TestDisk");
            var other = new FakeStorageHardware("F: OtherDisk");
            var cfg = new Settings { PreferredDisk = preferred.Name };

            manager.ProcessUpdate(other, cfg, true, true);
            Assert.Equal(0, other.UpdateCount);

            manager.ProcessUpdate(preferred, cfg, true, true);
            Assert.Equal(1, preferred.UpdateCount);
        }

        // =========================================================
        // 真实计数
        // =========================================================

        [Fact]
        public void 真实计数只在实际执行Update时增加()
        {
            var clock = new FakeTimeProvider();
            var manager = new DiskManager(clock);
            var statistics = new HardwareSamplingStatistics();
            manager.SamplingStatistics = statistics;

            var disk = new FakeStorageHardware("E: TestDisk");
            var cfg = new Settings();

            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(1, disk.UpdateCount);

            var updates = statistics.SnapshotDeviceUpdates();
            Assert.Equal(1, updates["Storage:E: TestDisk"]);
            Assert.Equal(1, statistics.SnapshotCategoryTotals()["Storage"]);

            // 没有真正更新 -> 计数不增加
            clock.Advance(TimeSpan.FromSeconds(1));
            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(1, disk.UpdateCount);
            Assert.Equal(1, statistics.SnapshotDeviceUpdates()["Storage:E: TestDisk"]);
        }
    }
}
