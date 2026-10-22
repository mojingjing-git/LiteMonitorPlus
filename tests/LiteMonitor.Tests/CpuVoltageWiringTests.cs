using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// #166「CPU 电压」的**接线**测试：候选准备 → 来源选择 → 发布 → 取值 → 失效。
    ///
    /// 覆盖验收硬门槛里的接线类场景：
    ///   1) Vcore / VID / SoC 同时存在 → 选中已确认的核心电压, SoC 不混入；
    ///   2) 只有 VID → 标记为参考值, 不冒充实测电压；
    ///   3) CPU 下无电压、SuperIO 提供 Vcore → 可选入并接入**既有**主板慢扫描需求；
    ///   4) 同优先级候选乱序 → 选择结果稳定（与枚举顺序无关）；
    ///   5) 手动源不存在 / 热重载后标识改变 → 缺失, 旧值与旧来源都不残留；
    ///   6) 100 ms UI + 多 Web 读取 → **不增加**普通硬件取值次数, 同代次复用；
    ///   7) 设置迁移 / 旧用户标签与可见性 → 不重复新增指标, 不覆盖用户选择；
    ///   8) 旧配置缺 PreferredCpuVoltageSensor 字段 → 视为 Auto, 不报错。
    ///
    /// ★ 全部用**假硬件**（继承 LHM 公开的 Hardware 基类），
    ///   **不打开 LHM、不碰驱动、不读真实硬件树**。
    /// </summary>
    public class CpuVoltageWiringTests : IDisposable
    {
        private static readonly DateTimeOffset Now =
            new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(8));

        public CpuVoltageWiringTests() => CpuVoltageSourcePublisher.Clear();

        public void Dispose() => CpuVoltageSourcePublisher.Clear();

        // =========================================================
        // 测试替身
        // =========================================================

        /// <summary>空的 ISettings 实现（LHM 的 Hardware 基类构造函数需要它）。</summary>
        private sealed class FakeSettings : ISettings
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

            public bool Contains(string name) => _values.ContainsKey(name);

            public void SetValue(string name, string value) => _values[name] = value;

            public string GetValue(string name, string value) => _values.TryGetValue(name, out var v) ? v : value;

            public void Remove(string name) => _values.Remove(name);
        }

        /// <summary>假的电压传感器：名字 / 标识 / 读数都可控。</summary>
        private sealed class FakeVoltageSensor : ISensor
        {
            public FakeVoltageSensor(string name, string identifier, float? value = null)
            {
                Name = name;
                Identifier = new Identifier(identifier.Split('/').Where(s => s.Length > 0).ToArray());
                Value = value;
            }

            public string Name { get; set; }
            public float? Value { get; set; }
            public SensorType SensorType => SensorType.Voltage;
            public IControl Control => null;
            public IHardware Hardware => null;
            public Identifier Identifier { get; }
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

        /// <summary>假的负载传感器（非电压类型，不该被登记成候选）。</summary>
        private sealed class FakeLoadSensor : ISensor
        {
            public FakeLoadSensor(string name) { Name = name; }

            public string Name { get; set; }
            public float? Value => 12f;
            public SensorType SensorType => SensorType.Load;
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

        /// <summary>假硬件：记录真实执行过的 Update 次数（用来证明"候选准备不触发采集"）。</summary>
        private sealed class FakeHardware : Hardware
        {
            private readonly ISensor[] _sensors;

            public FakeHardware(string name, string identifier, HardwareType type, params ISensor[] sensors)
                : base(name, new Identifier(identifier.Split('/').Where(s => s.Length > 0).ToArray()), new FakeSettings())
            {
                _sensors = sensors;
                HardwareType = type;
            }

            public override HardwareType HardwareType { get; }

            public override ISensor[] Sensors => _sensors;

            public int UpdateCount { get; private set; }

            public override void Update() => UpdateCount++;
        }

        private static FakeHardware CpuHardware(params ISensor[] sensors)
            => new FakeHardware("AMD Ryzen 7 5800X", "/amdcpu/0", HardwareType.Cpu, sensors);

        private static FakeHardware SuperIoHardware(params ISensor[] sensors)
            => new FakeHardware("IT87XX", "/superio/0", HardwareType.SuperIO, sensors);

        private static FakeHardware MotherboardHardware(params ISensor[] sensors)
            => new FakeHardware("X570 UNIFY", "/motherboard/0", HardwareType.Motherboard, sensors);

        private static FakeHardware GpuHardware(params ISensor[] sensors)
            => new FakeHardware("RTX 3080 Ti", "/gpu-nvidia/0", HardwareType.GpuNvidia, sensors);

        // ---------- 候选构造辅助（与实机命名一致） ----------

        private static CpuVoltageCandidate AmdCoreSvi2(double value, string id = "/amdcpu/0/voltage/0")
            => new CpuVoltageCandidate
            {
                SensorName = "Core (SVI2 TFN)",
                SensorIdentifier = id,
                HardwareIdentifier = "/amdcpu/0",
                HardwareName = "AMD Ryzen 7 5800X",
                HardwareLevel = CpuVoltageHardwareLevel.Cpu,
                Value = value,
                Timestamp = Now,
            };

        private static CpuVoltageCandidate AmdCoreVid(int core, double value, string id = null)
            => new CpuVoltageCandidate
            {
                SensorName = "Core #" + core + " VID",
                SensorIdentifier = id ?? ("/amdcpu/0/voltage/" + (core + 1)),
                HardwareIdentifier = "/amdcpu/0",
                HardwareName = "AMD Ryzen 7 5800X",
                HardwareLevel = CpuVoltageHardwareLevel.Cpu,
                Value = value,
                Timestamp = Now,
            };

        private static CpuVoltageCandidate AmdSocSvi2(double value)
            => new CpuVoltageCandidate
            {
                SensorName = "SoC (SVI2 TFN)",
                SensorIdentifier = "/amdcpu/0/voltage/1",
                HardwareIdentifier = "/amdcpu/0",
                HardwareName = "AMD Ryzen 7 5800X",
                HardwareLevel = CpuVoltageHardwareLevel.Cpu,
                Value = value,
                Timestamp = Now,
            };

        private static CpuVoltageCandidate IntelCoreRail(double value)
            => new CpuVoltageCandidate
            {
                SensorName = "CPU Core",
                SensorIdentifier = "/intelcpu/0/voltage/0",
                HardwareIdentifier = "/intelcpu/0",
                HardwareName = "Intel Core i9",
                HardwareLevel = CpuVoltageHardwareLevel.Cpu,
                Value = value,
                Timestamp = Now,
            };

        private static CpuVoltageCandidate SuperIoVcore(double value)
            => new CpuVoltageCandidate
            {
                SensorName = "Vcore",
                SensorIdentifier = "/superio/0/voltage/0",
                HardwareIdentifier = "/superio/0",
                HardwareName = "IT87XX",
                HardwareLevel = CpuVoltageHardwareLevel.SuperIO,
                Value = value,
                Timestamp = Now,
            };

        private static HardwareValueProvider NewProvider(
            Settings cfg, HardwareSamplingStatistics? statistics = null, Dictionary<string, float>? lastValid = null)
        {
            return new HardwareValueProvider(
                new Computer(),
                cfg,
                new SensorMap(),
                new NetworkManager(),
                new DiskManager(),
                null,                                   // 不需要 FPS
                new PerformanceCounterManager(),        // 未初始化
                new object(),
                lastValid ?? new Dictionary<string, float>(),
                statistics);
        }

        // =========================================================
        // 1) 候选准备：只登记 CPU / 主板 / SuperIO 的电压传感器
        // =========================================================

        [Fact]
        public void 候选准备只登记CPU与主板的电压传感器()
        {
            var map = new SensorMap();
            var refs = new List<SensorMap.CpuVoltageSensorRef>();

            FakeHardware cpu = CpuHardware(
                new FakeVoltageSensor("Core (SVI2 TFN)", "/amdcpu/0/voltage/0", 1.55f),
                new FakeVoltageSensor("SoC (SVI2 TFN)", "/amdcpu/0/voltage/1", 1.20f),
                new FakeLoadSensor("CPU Total"));

            FakeHardware superIo = SuperIoHardware(
                new FakeVoltageSensor("Vcore", "/superio/0/voltage/0", 1.28f),
                new FakeVoltageSensor("+12V", "/superio/0/voltage/1", 12.0f));

            FakeHardware gpu = GpuHardware(
                new FakeVoltageSensor("GPU Core", "/gpu-nvidia/0/voltage/0", 0.95f));

            SensorMap.CollectCpuVoltageCandidates(cpu, refs);
            SensorMap.CollectCpuVoltageCandidates(superIo, refs);
            SensorMap.CollectCpuVoltageCandidates(gpu, refs);

            // CPU 的 2 个电压 + SuperIO 的 2 个电压；负载传感器与显卡电压都不算候选
            Assert.Equal(4, refs.Count);
            Assert.Contains(refs, r => r.SensorIdentifier == "/amdcpu/0/voltage/0");
            Assert.Contains(refs, r => r.SensorIdentifier == "/superio/0/voltage/0");
            Assert.DoesNotContain(refs, r => r.HardwareLevel == CpuVoltageHardwareLevel.Other);

            // 层级翻译正确
            Assert.Equal(CpuVoltageHardwareLevel.Cpu, refs[0].HardwareLevel);
            Assert.Equal(CpuVoltageHardwareLevel.SuperIO, refs[2].HardwareLevel);
        }

        [Fact]
        public void 候选准备不触发任何硬件采集()
        {
            var map = new SensorMap();

            FakeHardware cpu = CpuHardware(new FakeVoltageSensor("Core (SVI2 TFN)", "/amdcpu/0/voltage/0", 1.55f));
            FakeHardware superIo = SuperIoHardware(new FakeVoltageSensor("Vcore", "/superio/0/voltage/0", 1.28f));

            map.AddCpuVoltageCandidates(cpu);
            map.AddCpuVoltageCandidates(superIo);
            map.RefreshCpuVoltageCandidates(Now);

            Assert.Equal(0, cpu.UpdateCount);
            Assert.Equal(0, superIo.UpdateCount);
            Assert.Equal(2, map.CpuVoltageCandidates.Count);
        }

        [Fact]
        public void 刷新候选后读数与时间戳来自当前采集()
        {
            var map = new SensorMap();

            FakeHardware cpu = CpuHardware(new FakeVoltageSensor("Core (SVI2 TFN)", "/amdcpu/0/voltage/0", 1.55f));
            map.AddCpuVoltageCandidates(cpu);
            map.RefreshCpuVoltageCandidates(Now);

            CpuVoltageCandidate candidate = map.CpuVoltageCandidates.Single();
            Assert.Equal(1.55f, candidate.Value!.Value, 3);
            Assert.Equal(Now, candidate.Timestamp!.Value);
        }

        [Fact]
        public void SensorMatcher不再把CPU电压映射成CPU_Voltage键()
        {
            // ★ 根因修复：旧实现把 core/cpu/vcore/vid 一律映射到同一个键，
            //   由 SensorMap 的"先到先得"决定用哪一个（实测选中的是 "Core #1 VID"）。
            FakeHardware cpu = CpuHardware(new FakeVoltageSensor("Core #1 VID", "/amdcpu/0/voltage/2", 1.55f));

            Assert.Null(SensorMatcher.Match(cpu, cpu.Sensors[0]));
        }

        // =========================================================
        // 2) 来源选择：Vcore / VID / SoC 同时存在
        // =========================================================

        [Fact]
        public void Vcore与VID与SoC同时存在时选中已确认的核心电压且SoC不混入()
        {
            var candidates = new List<CpuVoltageCandidate>
            {
                SuperIoVcore(1.28),
                AmdCoreVid(1, 1.55),
                AmdSocSvi2(1.20),
            };

            var resolver = new CpuVoltageResolver();
            CpuVoltageResolution r = HardwareMonitor.ResolveCpuVoltage(resolver, candidates, null, 1, Now);

            Assert.True(r.IsAvailable);
            Assert.Equal(CpuVoltageSourceKind.MotherboardVcore, r.SourceKind);
            Assert.Equal("/superio/0/voltage/0", r.SensorIdentifier);
            Assert.Equal(1.28, r.Value!.Value, 3);

            // SoC 轨既不能被选中, 也不能被算进"同源多候选"的聚合
            Assert.NotEqual("/amdcpu/0/voltage/1", r.SensorIdentifier);
            Assert.Equal(CpuVoltageSourceKind.Excluded,
                CpuVoltageResolver.Classify(CpuVoltageHardwareLevel.Cpu, "SoC (SVI2 TFN)", out _));
        }

        [Fact]
        public void 处理器上报的核心电压优先于VID与主板Vcore()
        {
            var candidates = new List<CpuVoltageCandidate>
            {
                IntelCoreRail(1.35),
                SuperIoVcore(1.28),
                AmdCoreVid(1, 1.55),
            };

            var resolver = new CpuVoltageResolver();
            CpuVoltageResolution r = HardwareMonitor.ResolveCpuVoltage(resolver, candidates, null, 1, Now);

            Assert.Equal(CpuVoltageSourceKind.MeasuredCoreRail, r.SourceKind);
            Assert.Equal("/intelcpu/0/voltage/0", r.SensorIdentifier);
            Assert.False(r.IsReferenceOnly);
        }

        [Fact]
        public void 只有VID时标记为参考值不冒充实测电压()
        {
            var candidates = Enumerable.Range(1, 8).Select(i => AmdCoreVid(i, 1.55)).ToList();

            var resolver = new CpuVoltageResolver();
            CpuVoltageResolution r = HardwareMonitor.ResolveCpuVoltage(resolver, candidates, null, 1, Now);

            Assert.True(r.IsAvailable);
            Assert.Equal(CpuVoltageSourceKind.RequestedVid, r.SourceKind);
            Assert.True(r.IsReferenceOnly);
            Assert.NotNull(r.AggregationNote);
            Assert.Contains("最高核心 VID", r.AggregationNote!, StringComparison.Ordinal);
        }

        // =========================================================
        // 3) 主板来源：接入既有主板慢扫描需求
        // =========================================================

        [Fact]
        public void CPU下无电压而SuperIO提供Vcore时接入既有主板慢扫描需求()
        {
            var candidates = new List<CpuVoltageCandidate> { SuperIoVcore(1.28) };

            Assert.True(HardwareMonitor.CpuVoltageNeedsMotherboardScan(candidates, null));

            var cfg = new Settings { UseWinPerCounters = false };
            cfg.InitDefaultItems();

            var req = HardwareUpdateRequirements.Compute(cfg, hasWebDataDemand: false, usePerformanceCounters: false,
                cpuVoltageNeedsMotherboard: true);

            Assert.True(req.NeedMobo, "选中主板来源时主板必须被扫描，否则电压会长期停在旧值上");

            // ★ 慢扫描节奏沿用既有的 3 秒（不新增定时器、不加快主板刷新）
            Assert.Equal(3000, CpuVoltageResolver.MotherboardSlowScanIntervalMs);
            Assert.Equal(TimeSpan.FromSeconds(3), HardwareSamplingSchedule.SlowScanInterval);
        }

        [Fact]
        public void 手动指定CPU来源时不需要主板慢扫描()
        {
            var candidates = new List<CpuVoltageCandidate> { AmdCoreSvi2(1.55), SuperIoVcore(1.28) };

            Assert.False(HardwareMonitor.CpuVoltageNeedsMotherboardScan(candidates, "/amdcpu/0/voltage/0"));
            Assert.True(HardwareMonitor.CpuVoltageNeedsMotherboardScan(candidates, "/superio/0/voltage/0"));
            // 手动来源已经消失 → 不需要为它扫描主板（界面显示"来源不可用"）
            Assert.False(HardwareMonitor.CpuVoltageNeedsMotherboardScan(candidates, "/amdcpu/0/voltage/99"));
        }

        [Fact]
        public void 只有CPU候选时不会误开主板慢扫描()
        {
            var candidates = new List<CpuVoltageCandidate> { AmdCoreSvi2(1.55), AmdSocSvi2(1.20) };
            Assert.False(HardwareMonitor.CpuVoltageNeedsMotherboardScan(candidates, null));
        }

        // =========================================================
        // 4) 同优先级候选乱序 → 结果稳定
        // =========================================================

        [Fact]
        public void 同优先级候选乱序时选择结果稳定与枚举顺序无关()
        {
            var a = IntelCoreRail(1.30);
            var b = new CpuVoltageCandidate
            {
                SensorName = "CPU Core",
                SensorIdentifier = "/intelcpu/1/voltage/0",
                HardwareIdentifier = "/intelcpu/1",
                HardwareName = "Intel Core i9",
                HardwareLevel = CpuVoltageHardwareLevel.Cpu,
                Value = 1.40,
                Timestamp = Now,
            };

            foreach (var order in new[]
                     {
                         new List<CpuVoltageCandidate> { a, b },
                         new List<CpuVoltageCandidate> { b, a },
                     })
            {
                var resolver = new CpuVoltageResolver();
                CpuVoltageResolution r = HardwareMonitor.ResolveCpuVoltage(resolver, order, null, 1, Now);

                Assert.True(r.IsAvailable);
                Assert.Equal("/intelcpu/0/voltage/0", r.SensorIdentifier);
                Assert.Equal(1.30, r.Value!.Value, 3);
            }
        }

        [Fact]
        public void 同一代次重复解析会复用结果不重复计算()
        {
            var candidates = new List<CpuVoltageCandidate> { IntelCoreRail(1.35) };
            var resolver = new CpuVoltageResolver();

            CpuVoltageResolution first = HardwareMonitor.ResolveCpuVoltage(resolver, candidates, null, 7, Now);
            CpuVoltageResolution second = HardwareMonitor.ResolveCpuVoltage(resolver, candidates, null, 7, Now);

            Assert.False(first.WasReused);
            Assert.True(second.WasReused);
            Assert.Equal(first.SensorIdentifier, second.SensorIdentifier);
        }

        // =========================================================
        // 5) 来源切换 / 手动源消失 → 缺失，旧值旧来源都不残留
        // =========================================================

        [Fact]
        public void 手动指定源不存在时返回缺失且旧值旧来源不残留()
        {
            var resolver = new CpuVoltageResolver();

            CpuVoltageResolution first = HardwareMonitor.ResolveCpuVoltage(
                resolver, new List<CpuVoltageCandidate> { AmdCoreVid(1, 1.55, "/amdcpu/0/voltage/2") },
                "/amdcpu/0/voltage/2", 1, Now);
            Assert.True(first.IsAvailable);
            CpuVoltageSourcePublisher.Publish(first);

            // 热重载后标识变了：老的手动来源已经不存在
            CpuVoltageResolution second = HardwareMonitor.ResolveCpuVoltage(
                resolver, new List<CpuVoltageCandidate> { AmdCoreVid(1, 1.55, "/amdcpu/0/voltage/9") },
                "/amdcpu/0/voltage/2", 2, Now);

            Assert.False(second.IsAvailable);
            Assert.Null(second.Value);
            Assert.Null(second.SensorIdentifier);
            Assert.Null(second.HardwareIdentifier);
            Assert.Contains("不可用", second.MissingReason!, StringComparison.Ordinal);

            CpuVoltageSourcePublisher.Publish(second);
            Assert.Null(CpuVoltageSourcePublisher.TryGetValue());
        }

        [Fact]
        public void 用户换来源后旧值与旧来源立即作废()
        {
            var cfg = new Settings { UseWinPerCounters = false };
            var stats = new HardwareSamplingStatistics();
            using var provider = NewProvider(cfg, stats);

            provider.OnCollectionStarted(1);

            // 旧来源：Core #1 VID
            CpuVoltageResolution first = HardwareMonitor.ResolveCpuVoltage(
                new CpuVoltageResolver(),
                new List<CpuVoltageCandidate> { AmdCoreVid(1, 1.55, "/amdcpu/0/voltage/2") },
                "/amdcpu/0/voltage/2", 1, Now);
            CpuVoltageSourcePublisher.Publish(first);
            Assert.Equal(1.55f, provider.GetValue("CPU.Voltage")!.Value, 3);

            // 用户把来源换成另一个传感器
            cfg.PreferredCpuVoltageSensor = "/amdcpu/0/voltage/3";
            provider.OnCollectionStarted(2);

            // ★ 旧值与旧来源都不能残留（否则会出现"新来源标签 + 旧数值"）
            Assert.Null(CpuVoltageSourcePublisher.Current);
            Assert.Null(provider.GetValue("CPU.Voltage"));
        }

        [Fact]
        public void 取值层在来源缺失时返回null而不是零()
        {
            var cfg = new Settings { UseWinPerCounters = false };
            using var provider = NewProvider(cfg);
            provider.OnCollectionStarted(1);

            CpuVoltageSourcePublisher.Clear();

            Assert.Null(provider.GetValue("CPU.Voltage"));
        }

        [Fact]
        public void 手动指定非CPU核心轨时拒绝映射()
        {
            var resolver = new CpuVoltageResolver();
            CpuVoltageResolution r = HardwareMonitor.ResolveCpuVoltage(
                resolver, new List<CpuVoltageCandidate> { AmdSocSvi2(1.20) }, "/amdcpu/0/voltage/1", 1, Now);

            Assert.False(r.IsAvailable);
            Assert.Null(r.Value);
            Assert.Contains("不是 CPU 核心电压", r.MissingReason!, StringComparison.Ordinal);
        }

        [Fact]
        public void 非法读数一律缺失而不是零()
        {
            var resolver = new CpuVoltageResolver();

            foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.5 })
            {
                var candidate = IntelCoreRail(1.35);
                var broken = new CpuVoltageCandidate
                {
                    SensorName = candidate.SensorName,
                    SensorIdentifier = candidate.SensorIdentifier,
                    HardwareIdentifier = candidate.HardwareIdentifier,
                    HardwareName = candidate.HardwareName,
                    HardwareLevel = candidate.HardwareLevel,
                    Value = bad,
                    Timestamp = Now,
                };

                CpuVoltageResolution r = HardwareMonitor.ResolveCpuVoltage(
                    resolver, new List<CpuVoltageCandidate> { broken }, null, 1, Now);

                Assert.False(r.IsAvailable);
                Assert.Null(r.Value);
            }
        }

        [Fact]
        public void 来源允许的零值被保留并标记为可能门控()
        {
            var resolver = new CpuVoltageResolver();
            CpuVoltageResolution r = HardwareMonitor.ResolveCpuVoltage(
                resolver, new List<CpuVoltageCandidate> { IntelCoreRail(0.0) }, null, 1, Now);

            Assert.True(r.IsAvailable);
            Assert.Equal(0.0, r.Value!.Value, 3);
            Assert.True(r.IsZeroSuspect);
        }

        // =========================================================
        // 6) 100 ms UI + 多 Web 读取 → 不增加取值次数，同代次复用
        // =========================================================

        [Fact]
        public void 同一代次内界面与网页多次读取只算一次值()
        {
            var cfg = new Settings { UseWinPerCounters = false };
            var stats = new HardwareSamplingStatistics();
            using var provider = NewProvider(cfg, stats);

            provider.OnCollectionStarted(1);
            CpuVoltageSourcePublisher.Publish(HardwareMonitor.ResolveCpuVoltage(
                new CpuVoltageResolver(), new List<CpuVoltageCandidate> { IntelCoreRail(1.234) }, null, 1, Now));

            long before = stats.ValueComputations;

            // 模拟 100 ms 界面刷新 + 多个网页读取
            for (int i = 0; i < 10; i++) provider.GetValue("CPU.Voltage");

            Assert.Equal(before + 1, stats.ValueComputations);
        }

        [Fact]
        public void 界面与网页读到的是同一份已发布结果()
        {
            CpuVoltageResolution resolution = HardwareMonitor.ResolveCpuVoltage(
                new CpuVoltageResolver(), new List<CpuVoltageCandidate> { IntelCoreRail(1.234) }, null, 1, Now);
            CpuVoltageSourcePublisher.Publish(resolution);

            // 面板 / 任务栏 / 网页读的都是同一个发布态对象（口径不可能漂移）
            Assert.Same(CpuVoltageSourcePublisher.Current, CpuVoltageSourcePublisher.Current);
            Assert.Equal(1.234, CpuVoltageSourcePublisher.TryGetValue()!.Value, 3);
        }

        // =========================================================
        // 7) 设置迁移 / 旧用户选择
        // =========================================================

        [Fact]
        public void 旧配置没有PreferredCpuVoltageSensor字段时视为自动()
        {
            const string json = "{\"PreferredCpuFan\":\"\",\"MonitorItems\":[{\"Key\":\"CPU.Voltage\",\"SortIndex\":205,\"VisibleInPanel\":false,\"UserLabel\":\"我的电压\"}]}";

            Settings? cfg = JsonSerializer.Deserialize<Settings>(json);

            Assert.NotNull(cfg);
            Assert.Equal("", cfg!.PreferredCpuVoltageSensor);   // 空 = 自动
        }

        [Fact]
        public void 设置迁移不重复新增CPU电压指标且保留用户选择()
        {
            // 旧配置：只有 CPU.Voltage 一项，且用户改过名字 / 打开过任务栏
            var cfg = new Settings
            {
                MonitorItems = new List<MonitorItemConfig>
                {
                    new MonitorItemConfig
                    {
                        Key = "CPU.Voltage",
                        SortIndex = 205,
                        VisibleInPanel = false,
                        VisibleInTaskbar = true,
                        UserLabel = "我的电压",
                    },
                },
            };

            cfg.CheckAndAppendMissingItems();

            List<MonitorItemConfig> items = cfg.MonitorItems.Where(x => x.Key == "CPU.Voltage").ToList();
            Assert.Single(items);                       // 不重复新增指标
            Assert.Equal("我的电压", items[0].UserLabel);  // 不覆盖用户标签
            Assert.True(items[0].VisibleInTaskbar);       // 不覆盖用户可见性
            Assert.False(items[0].VisibleInPanel);
            Assert.Equal("", cfg.PreferredCpuVoltageSensor);  // 旧配置没有该字段 = 自动
        }

        [Fact]
        public void 标准项齐全时迁移不改动任何用户选择()
        {
            var cfg = new Settings();
            cfg.InitDefaultItems();

            MonitorItemConfig voltage = cfg.MonitorItems.Single(x => x.Key == "CPU.Voltage");
            voltage.UserLabel = "我的电压";
            voltage.VisibleInTaskbar = true;
            voltage.VisibleInPanel = true;
            voltage.SortIndex = 205;

            cfg.CheckAndAppendMissingItems();

            MonitorItemConfig after = cfg.MonitorItems.Single(x => x.Key == "CPU.Voltage");
            Assert.Equal("我的电压", after.UserLabel);
            Assert.True(after.VisibleInTaskbar);
            Assert.True(after.VisibleInPanel);
            Assert.Equal(205, after.SortIndex);
            Assert.Single(cfg.MonitorItems.Where(x => x.Key == "CPU.Voltage"));
        }

        [Fact]
        public void CPU电压默认仍然是隐藏的()
        {
            var cfg = new Settings();
            cfg.InitDefaultItems();

            MonitorItemConfig item = cfg.MonitorItems.Single(x => x.Key == "CPU.Voltage");
            Assert.False(item.VisibleInPanel);
            Assert.False(item.VisibleInTaskbar);
            Assert.Equal(205, item.SortIndex);
        }

        [Fact]
        public void 只开启CPU电压时仍然需要LHM的CPU采集()
        {
            var cfg = new Settings();
            cfg.InitDefaultItems();
            foreach (MonitorItemConfig item in cfg.MonitorItems)
            {
                bool only = item.Key == "CPU.Voltage";
                item.VisibleInPanel = only;
                item.VisibleInTaskbar = only;
            }

            var req = HardwareUpdateRequirements.Compute(cfg, hasWebDataDemand: false, usePerformanceCounters: false);

            Assert.True(req.NeedCpu, "只开启 CPU 电压时也必须触发所需的 CPU 更新");
        }

        [Fact]
        public void 性能计数器开关不影响电压来源()
        {
            var cfg = new Settings();
            cfg.InitDefaultItems();
            foreach (MonitorItemConfig item in cfg.MonitorItems)
            {
                bool only = item.Key == "CPU.Voltage";
                item.VisibleInPanel = only;
                item.VisibleInTaskbar = only;
            }

            var withCounters = HardwareUpdateRequirements.Compute(cfg, false, true);
            var withoutCounters = HardwareUpdateRequirements.Compute(cfg, false, false);

            Assert.Equal(withCounters.NeedCpu, withoutCounters.NeedCpu);
            // ★ 电压永远不走性能计数器：取值层对 CPU.Voltage 只认已发布结果
            using var provider = NewProvider(cfg);
            provider.OnCollectionStarted(1);
            CpuVoltageSourcePublisher.Clear();
            Assert.Null(provider.GetValue("CPU.Voltage"));
        }
    }
}
