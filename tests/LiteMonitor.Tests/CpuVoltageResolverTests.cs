using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// #166 CPU 电压候选选择层的回归测试。
    ///
    /// 本测试工程只有 xunit, 没有 Mock 框架, 所以全部用例都只测纯逻辑:
    /// CpuVoltageResolver 接收**可注入的候选列表**, 输出纯数据,
    /// 既不碰 LHM 静态状态, 也不会自己发起采集。
    ///
    /// 候选里的传感器名 / 标识全部取自 LHM 0.9.6 的真实实现
    /// (Amd17Cpu.cs / Amd10Cpu.cs / IntelCpu.cs / SuperIOHardware.cs),
    /// 不是凭空编的。
    /// </summary>
    public class CpuVoltageResolverTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.FromHours(8));

        // ---------- 候选构造辅助 ----------

        /// <summary>AMD Zen3 的每核心请求电压 (LHM: "Core #N VID")。</summary>
        private static CpuVoltageCandidate AmdCoreVid(int core, double value, string id = null)
        {
            return new CpuVoltageCandidate
            {
                SensorName = "Core #" + core + " VID",
                SensorIdentifier = id ?? ("/amdcpu/0/voltage/" + (core + 1)),
                HardwareIdentifier = "/amdcpu/0",
                HardwareName = "AMD Ryzen 7 5800X",
                HardwareLevel = CpuVoltageHardwareLevel.Cpu,
                Value = value,
                Timestamp = T0,
            };
        }

        /// <summary>AMD Zen3 的核心轨请求电压 (LHM: "Core (SVI2 TFN)")。</summary>
        private static CpuVoltageCandidate AmdCoreSvi2(double value)
        {
            return new CpuVoltageCandidate
            {
                SensorName = "Core (SVI2 TFN)",
                SensorIdentifier = "/amdcpu/0/voltage/0",
                HardwareIdentifier = "/amdcpu/0",
                HardwareName = "AMD Ryzen 7 5800X",
                HardwareLevel = CpuVoltageHardwareLevel.Cpu,
                Value = value,
                Timestamp = T0,
            };
        }

        /// <summary>AMD 的 SoC 轨 (LHM: "SoC (SVI2 TFN)") —— 不是 CPU 核心电压。</summary>
        private static CpuVoltageCandidate AmdSocSvi2(double value)
        {
            return new CpuVoltageCandidate
            {
                SensorName = "SoC (SVI2 TFN)",
                SensorIdentifier = "/amdcpu/0/voltage/1",
                HardwareIdentifier = "/amdcpu/0",
                HardwareName = "AMD Ryzen 7 5800X",
                HardwareLevel = CpuVoltageHardwareLevel.Cpu,
                Value = value,
                Timestamp = T0,
            };
        }

        /// <summary>Intel 的核心轨上报电压 (LHM: "CPU Core", 取自 IA32_PERF_STATUS)。</summary>
        private static CpuVoltageCandidate IntelCoreRail(double? value, string id = "/intelcpu/0/voltage/0", string hwId = "/intelcpu/0")
        {
            return new CpuVoltageCandidate
            {
                SensorName = "CPU Core",
                SensorIdentifier = id,
                HardwareIdentifier = hwId,
                HardwareName = "Intel Core i9",
                HardwareLevel = CpuVoltageHardwareLevel.Cpu,
                Value = value,
                Timestamp = T0,
            };
        }

        /// <summary>主板 SuperIO 的 Vcore (LHM SuperIOHardware: new Voltage("Vcore", 0))。</summary>
        private static CpuVoltageCandidate SuperIoVcore(double value)
        {
            return new CpuVoltageCandidate
            {
                SensorName = "Vcore",
                SensorIdentifier = "/superio/0/voltage/0",
                HardwareIdentifier = "/superio/0",
                HardwareName = "IT87XX",
                HardwareLevel = CpuVoltageHardwareLevel.SuperIO,
                Value = value,
                Timestamp = T0,
            };
        }

        private static CpuVoltageQuery Query(
            IEnumerable<CpuVoltageCandidate> candidates,
            int generation = 1,
            string preferred = null,
            TimeSpan? maxAge = null,
            bool allowReferenceOnly = true)
        {
            return new CpuVoltageQuery
            {
                Candidates = candidates.ToList(),
                Generation = generation,
                PreferredSensorIdentifier = preferred,
                Now = T0,
                MaxAge = maxAge,
                AllowReferenceOnly = allowReferenceOnly,
            };
        }

        // ---------- 场景: Vcore / VID / SoC 同时存在 ----------

        [Fact]
        public void Vcore与VID与SoC同时存在时_选中已确认的核心电压且SoC不混入()
        {
            // 实机候选: Intel "CPU Core" (核心轨上报) + AMD 风格的 VID + SoC 轨
            var candidates = new List<CpuVoltageCandidate>
            {
                IntelCoreRail(1.35, "/intelcpu/0/voltage/0"),
                AmdCoreVid(1, 1.55),
                AmdSocSvi2(1.20),
            };

            var resolver = new CpuVoltageResolver();
            var r = resolver.Resolve(Query(candidates));

            Assert.True(r.IsAvailable);
            Assert.Equal(CpuVoltageSourceKind.MeasuredCoreRail, r.SourceKind);
            Assert.Equal(1.35, r.Value.Value, 3);
            Assert.Equal("/intelcpu/0/voltage/0", r.SensorIdentifier);
            Assert.False(r.IsReferenceOnly);

            // SoC 轨绝对不能混进 CPU 核心电压
            Assert.NotEqual("/amdcpu/0/voltage/1", r.SensorIdentifier);
            Assert.Equal(CpuVoltageSourceKind.Excluded,
                CpuVoltageResolver.Classify(CpuVoltageHardwareLevel.Cpu, "SoC (SVI2 TFN)", out _));
        }

        [Fact]
        public void 只有VID时标记为参考值_不冒充实测电压()
        {
            // 实机 AMD 5800X: 8 个每核心 VID + 1 个 SVI2 TFN, 全是请求电压
            var candidates = Enumerable.Range(1, 8)
                .Select(i => AmdCoreVid(i, 1.55))
                .ToList();

            var resolver = new CpuVoltageResolver();
            var r = resolver.Resolve(Query(candidates));

            Assert.True(r.IsAvailable);
            Assert.Equal(CpuVoltageSourceKind.RequestedVid, r.SourceKind);
            Assert.True(r.IsReferenceOnly);
            Assert.Contains("VID", r.SourceLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("参考", r.SourceLabel, StringComparison.Ordinal);
            // 每核心多值必须显式聚合并注明, 不能任取第一核
            Assert.NotNull(r.AggregationNote);
            Assert.Contains("最高核心 VID", r.AggregationNote, StringComparison.Ordinal);
        }

        [Fact]
        public void 自动模式不允许参考值时_只有VID就返回缺失()
        {
            var candidates = new List<CpuVoltageCandidate> { AmdCoreVid(1, 1.55) };

            var resolver = new CpuVoltageResolver();
            var r = resolver.Resolve(Query(candidates, allowReferenceOnly: false));

            Assert.False(r.IsAvailable);
            Assert.Null(r.Value);
            Assert.NotNull(r.MissingReason);
        }

        // ---------- 场景: CPU 下无电压, SuperIO 提供 Vcore ----------

        [Fact]
        public void CPU下无电压而SuperIO提供Vcore时_可选入并标注需要主板慢扫描()
        {
            var candidates = new List<CpuVoltageCandidate>
            {
                SuperIoVcore(1.28),
                AmdSocSvi2(1.20),           // 同批里的干扰项
            };

            var resolver = new CpuVoltageResolver();
            var r = resolver.Resolve(Query(candidates));

            Assert.True(r.IsAvailable);
            Assert.Equal(CpuVoltageSourceKind.MotherboardVcore, r.SourceKind);
            Assert.Equal(1.28, r.Value.Value, 3);
            Assert.Equal("/superio/0/voltage/0", r.SensorIdentifier);
            Assert.Equal("/superio/0", r.HardwareIdentifier);

            // 主板来源只能随主板慢扫描刷新, 必须显式标注出来
            Assert.True(r.NeedsMotherboardSlowScan);
            Assert.True(r.IsMotherboardSource);
            Assert.Equal(3000, CpuVoltageResolver.MotherboardSlowScanIntervalMs);
        }

        // ---------- 场景: 同优先级候选乱序, 选择必须稳定 ----------

        [Fact]
        public void 同优先级候选乱序时_选择结果稳定与枚举顺序无关()
        {
            // 两颗 CPU 各自一个 "CPU Core", 同优先级同粒度。
            // 规则要求按**稳定标识**排序, 所以永远选 "/intelcpu/0/voltage/0",
            // 而不是"谁先枚举到谁赢"。
            var a = IntelCoreRail(1.30, "/intelcpu/0/voltage/0", "/intelcpu/0");
            var b = IntelCoreRail(1.40, "/intelcpu/1/voltage/0", "/intelcpu/1");

            var permutations = new[]
            {
                new List<CpuVoltageCandidate> { a, b },
                new List<CpuVoltageCandidate> { b, a },
            };

            foreach (var perm in permutations)
            {
                var resolver = new CpuVoltageResolver();
                var r = resolver.Resolve(Query(perm));
                Assert.True(r.IsAvailable);
                Assert.Equal("/intelcpu/0/voltage/0", r.SensorIdentifier);
                Assert.Equal(1.30, r.Value.Value, 3);
            }
        }

        [Fact]
        public void 每核心VID乱序时_聚合结果稳定且取最高核心VID()
        {
            var cores = new List<CpuVoltageCandidate>
            {
                AmdCoreVid(1, 1.45),
                AmdCoreVid(2, 1.55),
                AmdCoreVid(3, 1.50),
                AmdCoreVid(4, 1.40),
            };

            string pickedId = null;
            double pickedValue = 0;
            foreach (var perm in new[]
                     {
                         cores,
                         cores.AsEnumerable().Reverse().ToList(),
                         new List<CpuVoltageCandidate> { cores[1], cores[3], cores[0], cores[2] },
                     })
            {
                var resolver = new CpuVoltageResolver();
                var r = resolver.Resolve(Query(perm));
                Assert.True(r.IsAvailable);
                Assert.Equal(CpuVoltageSourceKind.RequestedVid, r.SourceKind);
                pickedId ??= r.SensorIdentifier;
                pickedValue = r.Value.Value;
                Assert.Equal(pickedId, r.SensorIdentifier);
                Assert.Equal(1.55, pickedValue, 3);   // 最高核心 VID, 不是第一核
                Assert.Contains("最高核心 VID", r.AggregationNote, StringComparison.Ordinal);
                Assert.Contains("4", r.AggregationNote, StringComparison.Ordinal);
            }
        }

        // ---------- 场景: 值有效性 ----------

        [Theory]
        [InlineData(null, false)]
        [InlineData(double.NaN, false)]
        [InlineData(double.PositiveInfinity, false)]
        [InlineData(double.NegativeInfinity, false)]
        [InlineData(-0.5, false)]
        [InlineData(0.0, true)]      // 0 可能是休眠 / 门控, 不能判失败
        [InlineData(1.35, true)]
        public void 有效性判定符合来源规则_零值不被误判为失败(double? value, bool expected)
        {
            Assert.Equal(expected, CpuVoltageResolver.IsValueValid(CpuVoltageSourceKind.MeasuredCoreRail, value));
        }

        [Fact]
        public void 明确排除的来源永远不产生有效值()
        {
            Assert.False(CpuVoltageResolver.IsValueValid(CpuVoltageSourceKind.Excluded, 1.35));
            Assert.False(CpuVoltageResolver.IsValueValid(CpuVoltageSourceKind.Unknown, 1.35));
        }

        [Theory]
        [InlineData(null)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(-1.0)]
        public void 无效读数一律返回缺失而不是当成零(double? bad)
        {
            var resolver = new CpuVoltageResolver();
            var r = resolver.Resolve(Query(new List<CpuVoltageCandidate> { IntelCoreRail(bad) }));

            Assert.False(r.IsAvailable);
            Assert.Null(r.Value);           // 缺失不是 0
            Assert.NotNull(r.MissingReason);
        }

        [Fact]
        public void 来源允许的零值被保留并标记为可能门控()
        {
            var resolver = new CpuVoltageResolver();
            var r = resolver.Resolve(Query(new List<CpuVoltageCandidate> { IntelCoreRail(0.0) }));

            Assert.True(r.IsAvailable);
            Assert.Equal(0.0, r.Value.Value, 3);
            Assert.True(r.IsZeroSuspect);
        }

        // ---------- 场景: 手动指定源不存在 / 热重载后标识改变 ----------

        [Fact]
        public void 手动指定源不存在时返回缺失且旧值旧来源不残留()
        {
            var resolver = new CpuVoltageResolver();

            // 先成功选中一个来源
            var first = resolver.Resolve(Query(
                new List<CpuVoltageCandidate> { AmdCoreVid(1, 1.55, "/amdcpu/0/voltage/2") },
                generation: 1,
                preferred: "/amdcpu/0/voltage/2"));
            Assert.True(first.IsAvailable);
            Assert.Equal(1.55, first.Value.Value, 3);
            Assert.Equal("/amdcpu/0/voltage/2", first.SensorIdentifier);

            // 热重载后标识变了, 手动指定的老来源已经不存在
            var second = resolver.Resolve(Query(
                new List<CpuVoltageCandidate> { AmdCoreVid(1, 1.55, "/amdcpu/0/voltage/9") },
                generation: 2,
                preferred: "/amdcpu/0/voltage/2"));

            Assert.False(second.IsAvailable);
            Assert.Null(second.Value);
            Assert.Null(second.SensorIdentifier);
            Assert.Null(second.HardwareIdentifier);
            Assert.Equal(CpuVoltageSourceKind.Unknown, second.SourceKind);
            Assert.Contains("不可用", second.MissingReason, StringComparison.Ordinal);
        }

        [Fact]
        public void 来源切换后不能把旧来源的值配到新来源的标签上()
        {
            var resolver = new CpuVoltageResolver();

            var first = resolver.Resolve(Query(
                new List<CpuVoltageCandidate> { AmdCoreVid(1, 1.55, "/amdcpu/0/voltage/2") },
                generation: 1,
                preferred: "/amdcpu/0/voltage/2"));
            Assert.Equal(1.55, first.Value.Value, 3);

            // 新一代: 同一个传感器名, 但标识变了, 而且换了自动模式
            var second = resolver.Resolve(Query(
                new List<CpuVoltageCandidate> { AmdCoreVid(1, 1.42, "/amdcpu/0/voltage/2") },
                generation: 2));

            Assert.True(second.IsAvailable);
            Assert.Equal(1.42, second.Value.Value, 3);   // 必须是新值, 不能沿用旧值
            Assert.Equal("/amdcpu/0/voltage/2", second.SensorIdentifier);
        }

        [Fact]
        public void 手动指定非CPU核心轨时拒绝映射()
        {
            var resolver = new CpuVoltageResolver();
            var r = resolver.Resolve(Query(
                new List<CpuVoltageCandidate> { AmdSocSvi2(1.20) },
                preferred: "/amdcpu/0/voltage/1"));

            Assert.False(r.IsAvailable);
            Assert.Null(r.Value);
            Assert.Contains("不是 CPU 核心电压", r.MissingReason, StringComparison.Ordinal);
        }

        [Fact]
        public void 过期候选返回缺失而不是显示旧电压()
        {
            var stale = new CpuVoltageCandidate
            {
                SensorName = "CPU Core",
                SensorIdentifier = "/intelcpu/0/voltage/0",
                HardwareIdentifier = "/intelcpu/0",
                HardwareLevel = CpuVoltageHardwareLevel.Cpu,
                Value = 1.30,
                Timestamp = T0.AddMinutes(-10),
            };

            var resolver = new CpuVoltageResolver();
            var r = resolver.Resolve(Query(new List<CpuVoltageCandidate> { stale }, maxAge: TimeSpan.FromSeconds(3)));

            Assert.False(r.IsAvailable);
            Assert.Null(r.Value);
            Assert.Contains("过期", r.MissingReason, StringComparison.Ordinal);
        }

        // ---------- 场景: 同代次复用, 不额外采集 ----------

        [Fact]
        public void 同代次多次读取复用上次结果()
        {
            var candidates = new List<CpuVoltageCandidate> { IntelCoreRail(1.35) };
            var resolver = new CpuVoltageResolver();

            var first = resolver.Resolve(Query(candidates, generation: 7));
            var second = resolver.Resolve(Query(candidates, generation: 7));

            Assert.False(first.WasReused);
            Assert.True(second.WasReused);
            Assert.Equal(first.Value, second.Value);
            Assert.Equal(first.SourceKind, second.SourceKind);
            Assert.Equal(first.SensorIdentifier, second.SensorIdentifier);
        }

        [Fact]
        public void 采集代次前进后重新选择不复用旧结果()
        {
            var resolver = new CpuVoltageResolver();

            var first = resolver.Resolve(Query(new List<CpuVoltageCandidate> { IntelCoreRail(1.35) }, generation: 7));
            var second = resolver.Resolve(Query(new List<CpuVoltageCandidate> { IntelCoreRail(1.22) }, generation: 8));

            Assert.False(second.WasReused);
            Assert.Equal(1.22, second.Value.Value, 3);
        }

        [Fact]
        public void 候选枚举顺序变化不影响同代次复用()
        {
            var a = IntelCoreRail(1.35, "/intelcpu/0/voltage/0");
            var resolver = new CpuVoltageResolver();

            resolver.Resolve(Query(new List<CpuVoltageCandidate> { a }, generation: 3));
            var second = resolver.Resolve(Query(new List<CpuVoltageCandidate> { a }, generation: 3));

            Assert.True(second.WasReused);
        }

        [Fact]
        public void Resolver公开接口不暴露LHM类型_因此无法旁路发起采集()
        {
            // 本项目的硬性契约: 普通硬件采集只走 HardwareSamplingCoordinator (最高 1 次/秒),
            // UI / Web 不得旁路采集, 取值按采集代次复用。
            // Resolver 既然不持有任何 LHM 对象, 就不可能自己去调 hw.Update()。
            var types = new[]
            {
                typeof(CpuVoltageResolver),
                typeof(CpuVoltageQuery),
                typeof(CpuVoltageCandidate),
                typeof(CpuVoltageResolution),
            };

            foreach (var t in types)
            {
                foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    foreach (var used in MemberTypes(m))
                    {
                        Assert.DoesNotContain("LibreHardwareMonitor", used.FullName ?? "", StringComparison.OrdinalIgnoreCase);
                    }
                }

                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    Assert.DoesNotContain("LibreHardwareMonitor", f.FieldType.FullName ?? "", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private static IEnumerable<Type> MemberTypes(MemberInfo member)
        {
            if (member is MethodInfo mi)
            {
                yield return mi.ReturnType;
                foreach (var p in mi.GetParameters()) yield return p.ParameterType;
            }
            else if (member is PropertyInfo pi)
            {
                yield return pi.PropertyType;
            }
            else if (member is FieldInfo fi)
            {
                yield return fi.FieldType;
            }
            else if (member is ConstructorInfo ci)
            {
                foreach (var p in ci.GetParameters()) yield return p.ParameterType;
            }
        }

        // ---------- 阳性对照: 正常核心电压来源被正确选中 ----------

        [Fact]
        public void 阳性对照_正常核心电压来源被正确选中()
        {
            var resolver = new CpuVoltageResolver();
            var r = resolver.Resolve(Query(new List<CpuVoltageCandidate> { IntelCoreRail(1.35) }));

            Assert.True(r.IsAvailable);
            Assert.True(r.IsValid);
            Assert.Equal(CpuVoltageSourceKind.MeasuredCoreRail, r.SourceKind);
            Assert.Equal(1.35, r.Value.Value, 3);
            Assert.False(r.IsReferenceOnly);
            Assert.Equal("/intelcpu/0/voltage/0", r.SensorIdentifier);
            Assert.Equal("/intelcpu/0", r.HardwareIdentifier);
            Assert.Equal(T0, r.Timestamp);
        }

        // ---------- 名称分类规则 (依据 LHM 0.9.6 真实实现) ----------

        [Theory]
        // LHM IntelCpu.cs: "CPU Core" = IA32_PERF_STATUS 上报的核心轨电压
        [InlineData(CpuVoltageHardwareLevel.Cpu, "CPU Core", CpuVoltageSourceKind.MeasuredCoreRail)]
        [InlineData(CpuVoltageHardwareLevel.Cpu, "CPU Core #1", CpuVoltageSourceKind.MeasuredCoreRail)]
        [InlineData(CpuVoltageHardwareLevel.Cpu, "P-Core #1", CpuVoltageSourceKind.MeasuredCoreRail)]
        [InlineData(CpuVoltageHardwareLevel.Cpu, "E-Core #1", CpuVoltageSourceKind.MeasuredCoreRail)]
        // LHM Amd17Cpu.cs: "Core #N VID" / "Core (SVI2 TFN)" 都是请求电压
        [InlineData(CpuVoltageHardwareLevel.Cpu, "Core #1 VID", CpuVoltageSourceKind.RequestedVid)]
        [InlineData(CpuVoltageHardwareLevel.Cpu, "Core (SVI2 TFN)", CpuVoltageSourceKind.RequestedVid)]
        [InlineData(CpuVoltageHardwareLevel.Cpu, "CPU Cores", CpuVoltageSourceKind.RequestedVid)]
        // 明确不是 CPU 核心电压的其它轨
        [InlineData(CpuVoltageHardwareLevel.Cpu, "SoC (SVI2 TFN)", CpuVoltageSourceKind.Excluded)]
        [InlineData(CpuVoltageHardwareLevel.Cpu, "GT/0", CpuVoltageSourceKind.Excluded)]
        [InlineData(CpuVoltageHardwareLevel.Cpu, "AUX", CpuVoltageSourceKind.Excluded)]
        [InlineData(CpuVoltageHardwareLevel.SuperIO, "+12V", CpuVoltageSourceKind.Excluded)]
        [InlineData(CpuVoltageHardwareLevel.SuperIO, "+5V", CpuVoltageSourceKind.Excluded)]
        [InlineData(CpuVoltageHardwareLevel.SuperIO, "+3.3V", CpuVoltageSourceKind.Excluded)]
        [InlineData(CpuVoltageHardwareLevel.SuperIO, "DIMM", CpuVoltageSourceKind.Excluded)]
        [InlineData(CpuVoltageHardwareLevel.SuperIO, "DRAM", CpuVoltageSourceKind.Excluded)]
        [InlineData(CpuVoltageHardwareLevel.SuperIO, "CPU System Agent", CpuVoltageSourceKind.Excluded)]
        [InlineData(CpuVoltageHardwareLevel.SuperIO, "CPU Termination", CpuVoltageSourceKind.Excluded)]
        [InlineData(CpuVoltageHardwareLevel.SuperIO, "CMOS Battery", CpuVoltageSourceKind.Excluded)]
        [InlineData(CpuVoltageHardwareLevel.SuperIO, "Chipset", CpuVoltageSourceKind.Excluded)]
        [InlineData(CpuVoltageHardwareLevel.SuperIO, "CPU VDDP", CpuVoltageSourceKind.Excluded)]
        [InlineData(CpuVoltageHardwareLevel.SuperIO, "CPU VDDIO Memory", CpuVoltageSourceKind.Excluded)]
        // 主板明确命名的 Vcore
        [InlineData(CpuVoltageHardwareLevel.SuperIO, "Vcore", CpuVoltageSourceKind.MotherboardVcore)]
        [InlineData(CpuVoltageHardwareLevel.Motherboard, "Vcore", CpuVoltageSourceKind.MotherboardVcore)]
        // 名称泛称, 来源含糊
        [InlineData(CpuVoltageHardwareLevel.Cpu, "CPU", CpuVoltageSourceKind.Ambiguous)]
        [InlineData(CpuVoltageHardwareLevel.Cpu, "CPU Voltage", CpuVoltageSourceKind.Ambiguous)]
        public void 名称分类规则符合LHM真实实现(CpuVoltageHardwareLevel level, string name, CpuVoltageSourceKind expected)
        {
            Assert.Equal(expected, CpuVoltageResolver.Classify(level, name, out _));
        }

        [Fact]
        public void 名称泛称CPU时自动模式不盲选但手动可选()
        {
            var generic = new CpuVoltageCandidate
            {
                SensorName = "CPU",
                SensorIdentifier = "/intelcpu/0/voltage/3",
                HardwareIdentifier = "/intelcpu/0",
                HardwareLevel = CpuVoltageHardwareLevel.Cpu,
                Value = 1.30,
                Timestamp = T0,
            };

            var resolver = new CpuVoltageResolver();
            var auto = resolver.Resolve(Query(new List<CpuVoltageCandidate> { generic }));
            Assert.False(auto.IsAvailable);
            Assert.Contains("盲选", auto.MissingReason, StringComparison.Ordinal);

            var manual = resolver.Resolve(Query(
                new List<CpuVoltageCandidate> { generic },
                generation: 2,
                preferred: "/intelcpu/0/voltage/3"));
            Assert.True(manual.IsAvailable);
            Assert.Equal(CpuVoltageSourceKind.Ambiguous, manual.SourceKind);
            Assert.True(manual.IsReferenceOnly);
            Assert.Contains("待确认", manual.SourceLabel, StringComparison.Ordinal);
        }
    }
}
