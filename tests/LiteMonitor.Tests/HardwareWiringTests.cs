using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.WebServer;   // WebFpsDemand（网页侧的 FPS 需求规则）
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// A2b 接线测试：<c>HardwareMonitor</c> 的采集调度 / 计数 / Web 需求语义。
    ///
    /// 覆盖简报要求的四组不变量：
    ///  1. **同一到期秒只消费一次**：主板/SuperIO 慢扫描约每 3 秒一次、磁盘后台扫描约每 10 秒一次；
    ///     活跃后台磁盘约每 3 秒一次、冷却中的后台磁盘约每 10 秒一次。
    ///  2. **维护触发计数与真实触发一致**：连续高频调用下"流量保存到期"计数不会在同一到期秒里暴增。
    ///  3. **网卡/磁盘计数不重复**：同一个设备 Key 在一次采集里只被计一次。
    ///  4. **forceAll 语义**：Web 开关开着但没有合法需求时不扩大采集范围；
    ///     有合法需求才按全量更新，且**不绕过显卡选择**。
    ///
    /// 全部用**假时钟**驱动，不用真 Sleep；
    /// 全部用**假硬件**（继承 LHM 公开的 Hardware 基类），**不启动 LHM / 驱动 /
    /// 真实 PresentMon / 外网请求**。
    /// </summary>
    public class HardwareWiringTests
    {
        // =========================================================
        // 测试替身
        // =========================================================

        /// <summary>假时钟：单调时间戳与墙上时间独立（回拨墙上时间不影响调度）。</summary>
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

            /// <summary>只回拨墙上时间（模拟系统时间被改回）。</summary>
            public void RewindWallClock(TimeSpan delta) => _wallClock -= delta;
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

        /// <summary>假的网卡硬件。</summary>
        private sealed class FakeNetworkHardware : Hardware
        {
            public FakeNetworkHardware(string name)
                : base(name, new Identifier(new[] { name }), new FakeSettings())
            {
            }

            public override HardwareType HardwareType => LibreHardwareMonitor.Hardware.HardwareType.Network;

            public override ISensor[] Sensors => Array.Empty<ISensor>();

            public int UpdateCount { get; private set; }

            public override void Update() => UpdateCount++;
        }

        /// <summary>假的显卡硬件。</summary>
        private sealed class FakeGpuHardware : Hardware
        {
            public FakeGpuHardware(string name)
                : base(name, new Identifier(new[] { name }), new FakeSettings())
            {
            }

            public override HardwareType HardwareType => LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia;

            public override ISensor[] Sensors => Array.Empty<ISensor>();

            public int UpdateCount { get; private set; }

            public override void Update() => UpdateCount++;
        }

        /// <summary>
        /// 假外部 FPS 服务启动器：只做计数，**不启动任何真实进程**
        /// （不触碰 PresentMon / ETW / 网络 / 驱动）。
        /// </summary>
        private sealed class FakeFpsLauncher : IFpsServiceLauncher
        {
            public bool EnsureAvailable() => true;

            public bool Start(Action<string> onLine, out string error)
            {
                error = null;
                return false;
            }

            public void Stop() { }

            public bool IsRunning => false;
        }

        /// <summary>假进程查询器：不触碰真实进程列表。</summary>
        private sealed class FakeFpsProcessQuery : IFpsProcessQuery
        {
            public FpsProcessQueryResult Query(int pid) => FpsProcessQueryResult.CreateExited();
        }

        /// <summary>模拟"界面 100 ms 一次请求"的采集节奏。</summary>
        private static void SimulateRequests(FakeTimeProvider clock, int requests, Action<int> onRequest)
        {
            for (int i = 0; i < requests; i++)
            {
                onRequest(i);
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }
        }

        // =========================================================
        // 结构断言工具（IL 扫描）
        // =========================================================

        /// <summary>
        /// IL 扫描小工具：只解析 <c>call</c> / <c>callvirt</c> / <c>ldstr</c> 三类指令。
        ///
        /// ★ 为什么用 IL 而不是源码文本断言：
        ///   - 文本断言会命中**注释**里的字符串（"断言一个字符串存在但没有行为意义"），
        ///     而且依赖源码文件在测试运行时存在（纯净副本 / 只发布二进制的环境里没有）；
        ///   - IL 断言直接锁定"这个方法体里**真的调用了**哪个方法"，
        ///     与注释无关、也不依赖源码文件，是**结构性**护栏而不是同义反复。
        /// </summary>
        private static class IlInspector
        {
            private static readonly OpCode[] OneByte = new OpCode[0x100];
            private static readonly OpCode[] TwoByte = new OpCode[0x100];

            static IlInspector()
            {
                foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.FieldType != typeof(OpCode)) continue;
                    var op = (OpCode)field.GetValue(null);
                    ushort value = unchecked((ushort)op.Value);
                    if (value < 0x100) OneByte[value] = op;
                    else if ((value & 0xFF00) == 0xFE00) TwoByte[value & 0xFF] = op;
                }
            }

            /// <summary>某个方法体的扫描结果。</summary>
            public sealed class ScanResult
            {
                /// <summary>方法体里被调用的方法（按出现顺序）。</summary>
                public List<MethodBase> Calls { get; } = new List<MethodBase>();

                /// <summary>方法体里用到的字符串字面量（ldstr）。</summary>
                public List<string> Strings { get; } = new List<string>();

                /// <summary>解析失败的调用指令数量（正常应为 0；不为 0 说明扫描器对该模块不可用）。</summary>
                public int UnresolvedCalls { get; set; }
            }

            /// <summary>扫描一个方法体。</summary>
            public static ScanResult Scan(MethodBase method)
            {
                var result = new ScanResult();
                if (method == null) return result;

                var body = method.GetMethodBody();
                if (body == null) return result;
                byte[] il = body.GetILAsByteArray();
                if (il == null) return result;

                Module module = method.Module;
                Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType
                    ? method.DeclaringType.GetGenericArguments()
                    : null;
                Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

                int i = 0;
                while (i < il.Length)
                {
                    OpCode op;
                    byte first = il[i];
                    if (first == 0xFE)
                    {
                        if (i + 1 >= il.Length) break;
                        op = TwoByte[il[i + 1]];
                        i += 2;
                    }
                    else
                    {
                        op = OneByte[first];
                        i += 1;
                    }

                    switch (op.OperandType)
                    {
                        case OperandType.InlineMethod:
                            {
                                int token = BitConverter.ToInt32(il, i);
                                i += 4;
                                MethodBase target = null;
                                try { target = module.ResolveMethod(token, typeArgs, methodArgs); }
                                catch (Exception) { /* 解析不了就记为未解析（下面的断言会暴露它） */ }
                                if (target != null) result.Calls.Add(target);
                                else result.UnresolvedCalls++;
                                break;
                            }
                        case OperandType.InlineString:
                            {
                                int token = BitConverter.ToInt32(il, i);
                                i += 4;
                                string text = null;
                                try { text = module.ResolveString(token); }
                                catch (Exception) { }
                                if (text != null) result.Strings.Add(text);
                                break;
                            }
                        case OperandType.InlineI:
                        case OperandType.ShortInlineR:
                        case OperandType.InlineBrTarget:
                        case OperandType.InlineField:
                        case OperandType.InlineSig:
                        case OperandType.InlineTok:
                        case OperandType.InlineType:
                            i += 4;
                            break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR:
                            i += 8;
                            break;
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.ShortInlineVar:
                            i += 1;
                            break;
                        case OperandType.InlineVar:
                            i += 2;
                            break;
                        case OperandType.InlineSwitch:
                            {
                                int count = BitConverter.ToInt32(il, i);
                                i += 4 + 4 * count;
                                break;
                            }
                        default:
                            break;   // InlineNone / InlinePhi（已废弃）
                    }
                }

                return result;
            }
        }

        /// <summary>枚举某个类型自身 + 其嵌套类型（编译器生成的闭包类也在内）的所有方法。</summary>
        private static IEnumerable<MethodBase> AllMethodsIncludingNested(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(flags)) yield return method;
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (var method in AllMethodsIncludingNested(nested)) yield return method;
            }
        }

        /// <summary>两个方法是否指向同一个方法（按声明类型 + 名称 + 参数签名判定）。</summary>
        private static bool SameMethod(MethodBase candidate, MethodInfo expected)
        {
            if (candidate == null || expected == null) return false;
            if (candidate.DeclaringType != expected.DeclaringType) return false;
            if (candidate.Name != expected.Name) return false;

            var actualInfo = candidate as MethodInfo;
            if (actualInfo == null) return false;

            var actualParams = actualInfo.GetParameters();
            var expectedParams = expected.GetParameters();
            if (actualParams.Length != expectedParams.Length) return false;
            for (int i = 0; i < actualParams.Length; i++)
            {
                if (actualParams[i].ParameterType != expectedParams[i].ParameterType) return false;
            }
            return true;
        }

        // =========================================================
        // 1. 同一到期秒只消费一次（单调截止时间）
        // =========================================================

        [Fact]
        public void 主板慢扫描在100ms请求下约每3秒一次()
        {
            var clock = new FakeTimeProvider();
            var schedule = new HardwareSamplingSchedule(clock);

            int slowScans = 0;
            // 12 秒 / 100ms = 120 次请求
            SimulateRequests(clock, 120, _ => { if (schedule.TryConsumeSlowScan()) slowScans++; });

            // 到期点：t = 0 / 3 / 6 / 9 -> 4 次
            Assert.Equal(4, slowScans);
            Assert.Equal(TimeSpan.FromSeconds(3), HardwareSamplingSchedule.SlowScanInterval);
        }

        [Fact]
        public void 磁盘后台扫描在100ms请求下约每10秒一次()
        {
            var clock = new FakeTimeProvider();
            var schedule = new HardwareSamplingSchedule(clock);

            int diskScans = 0;
            SimulateRequests(clock, 120, _ => { if (schedule.TryConsumeDiskBackgroundScan()) diskScans++; });

            // 到期点：t = 0 / 10 -> 2 次
            Assert.Equal(2, diskScans);
            Assert.Equal(TimeSpan.FromSeconds(10), HardwareSamplingSchedule.DiskBackgroundScanInterval);
        }

        [Fact]
        public void 同一到期时间只消费一次()
        {
            var clock = new FakeTimeProvider();
            var schedule = new HardwareSamplingSchedule(clock);

            Assert.True(schedule.TryConsumeSlowScan());

            // 同一个到期秒里被调用十次（对应旧实现 `_secondsCounter % 3 == 0` 的缺陷）：
            // 只能消费一次
            for (int i = 0; i < 10; i++)
            {
                Assert.False(schedule.TryConsumeSlowScan());
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            // 累计到 3 秒才允许下一次
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.True(schedule.TryConsumeSlowScan());
            Assert.False(schedule.TryConsumeSlowScan());
        }

        [Fact]
        public void 跳过若干周期后最多执行一次()
        {
            var clock = new FakeTimeProvider();
            var schedule = new HardwareSamplingSchedule(clock);

            Assert.True(schedule.TryConsumeSlowScan());

            // 直接跳过 10 个周期（30 秒）
            clock.Advance(TimeSpan.FromSeconds(30));

            Assert.True(schedule.TryConsumeSlowScan());   // 只执行一次，不补跑一串
            Assert.False(schedule.TryConsumeSlowScan());
        }

        [Fact]
        public void 墙上时间回拨不影响慢扫描调度()
        {
            var clock = new FakeTimeProvider();
            var schedule = new HardwareSamplingSchedule(clock);

            Assert.True(schedule.TryConsumeSlowScan());

            // 系统时间被改回 1 小时
            clock.RewindWallClock(TimeSpan.FromHours(1));

            Assert.False(schedule.TryConsumeSlowScan());

            clock.Advance(TimeSpan.FromSeconds(3));
            Assert.True(schedule.TryConsumeSlowScan());
        }

        [Fact]
        public void 两次慢扫描之间的间隔不小于3秒_即使采集节奏出现停顿()
        {
            var clock = new FakeTimeProvider();
            var schedule = new HardwareSamplingSchedule(clock);

            var consumedAt = new List<double>();
            void Request()
            {
                if (schedule.TryConsumeSlowScan())
                    consumedAt.Add((double)clock.GetTimestamp() / TimeSpan.TicksPerSecond);
            }

            // 启动阶段硬件初始化耗时 2.9 秒：第一次真实采集落在 t = 2.9
            clock.Advance(TimeSpan.FromMilliseconds(2900));
            Request();

            // 之后按 100ms 节奏继续请求
            for (int i = 0; i < 100; i++) { Request(); clock.Advance(TimeSpan.FromMilliseconds(100)); }

            Assert.True(consumedAt.Count >= 3, $"实际只消费了 {consumedAt.Count} 次");
            for (int i = 1; i < consumedAt.Count; i++)
            {
                double gap = consumedAt[i] - consumedAt[i - 1];
                Assert.True(gap >= 3.0, $"两次慢扫描间隔 {gap} 秒，小于 3 秒（消费点：{string.Join(", ", consumedAt)}）");
            }
        }

        [Fact]
        public void 活跃后台磁盘不快于每3秒一次()
        {
            var clock = new FakeTimeProvider();
            var manager = new DiskManager(clock);
            var schedule = new HardwareSamplingSchedule(clock);
            var disk = new FakeStorageHardware("E: WiringTestDisk");
            var cfg = new Settings();

            SimulateRequests(clock, 120, _ =>
            {
                bool slow = schedule.TryConsumeSlowScan();
                bool bg = schedule.TryConsumeDiskBackgroundScan();
                manager.ProcessUpdate(disk, cfg, slow, bg);
            });

            // 到期点：t = 0 / 3 / 6 / 9 -> 4 次
            Assert.Equal(4, disk.UpdateCount);
        }

        [Fact]
        public void 冷却中的后台磁盘不快于每10秒一次()
        {
            var clock = new FakeTimeProvider();
            var manager = new DiskManager(clock);
            var schedule = new HardwareSamplingSchedule(clock);
            var sensor = new FakeThroughputSensor("Read", 5000);   // 有读写活动
            var disk = new FakeStorageHardware("E: WiringTestDisk", sensor);
            var cfg = new Settings();

            manager.ProcessUpdate(disk, cfg, schedule.TryConsumeSlowScan(), schedule.TryConsumeDiskBackgroundScan());
            Assert.Equal(1, disk.UpdateCount);

            // 2 分钟没有活动 -> 进入冷却模式
            clock.Advance(TimeSpan.FromMinutes(2));
            sensor.Value = 0;
            manager.ProcessUpdate(disk, cfg, schedule.TryConsumeSlowScan(), schedule.TryConsumeDiskBackgroundScan());
            Assert.Equal(2, disk.UpdateCount);

            int before = disk.UpdateCount;
            SimulateRequests(clock, 120, _ =>
            {
                manager.ProcessUpdate(disk, cfg, schedule.TryConsumeSlowScan(), schedule.TryConsumeDiskBackgroundScan());
            });

            // 冷却中：12 秒内只应再更新 1 次（10 秒一次）
            Assert.Equal(before + 1, disk.UpdateCount);
        }

        // =========================================================
        // 2. 维护触发计数与真实触发一致
        // =========================================================

        [Fact]
        public void 维护触发计数与真实触发一致_同一到期秒不会暴增()
        {
            var clock = new FakeTimeProvider();
            int saves = 0;
            // 与生产同形：调用方自持调度器实例（生产用 SystemOptimizer.CreateScheduler()，
            // 这里注入假的动作，避免真的写流量历史文件）
            var scheduler = new MaintenanceScheduler(onTrafficSave: () => saves++, timeProvider: clock);
            var statistics = new HardwareSamplingStatistics();

            // 流量保存首次到期点（约 5 秒）
            clock.Advance(TimeSpan.FromSeconds(5));

            // 100ms 一次的连续调用：10 次都落在同一个到期秒里
            SimulateRequests(clock, 10, _ =>
            {
                var result = scheduler.RunDueTasks();
                HardwareMonitor.RecordMaintenanceTriggers(statistics, result);
            });

            Assert.Equal(1, saves);
            Assert.Equal(1, statistics.TrafficSaveTriggers);
        }

        [Fact]
        public void 自动GC请求在到期点只记一次()
        {
            var clock = new FakeTimeProvider();
            int gcRequests = 0;
            var scheduler = new MaintenanceScheduler(onGcRequest: () => gcRequests++, timeProvider: clock);
            var statistics = new HardwareSamplingStatistics();

            clock.Advance(TimeSpan.FromSeconds(30));   // 自动 GC 首次到期点

            SimulateRequests(clock, 10, _ =>
            {
                var result = scheduler.RunDueTasks();
                HardwareMonitor.RecordMaintenanceTriggers(statistics, result);
            });

            Assert.Equal(1, gcRequests);
            Assert.Equal(1, statistics.GcRequestTriggers);
        }

        [Fact]
        public void 维护任务跳过若干周期后最多执行一次()
        {
            var clock = new FakeTimeProvider();
            int saves = 0;
            var scheduler = new MaintenanceScheduler(onTrafficSave: () => saves++, timeProvider: clock);
            var statistics = new HardwareSamplingStatistics();

            // 直接跳过 9 个流量保存周期（600 秒）
            clock.Advance(TimeSpan.FromSeconds(600));

            var result = scheduler.RunDueTasks();
            HardwareMonitor.RecordMaintenanceTriggers(statistics, result);

            Assert.Equal(1, saves);
            Assert.Equal(1, statistics.TrafficSaveTriggers);
        }

        [Fact]
        public void 维护计数不按整数秒取模推算_未到期不计数()
        {
            var clock = new FakeTimeProvider();
            int saves = 0;
            var scheduler = new MaintenanceScheduler(onTrafficSave: () => saves++, timeProvider: clock);
            var statistics = new HardwareSamplingStatistics();

            // 未到任何到期点：跑满 4 秒（旧的 `%60==5` 口径在这里也不会命中，
            // 但本用例锁定的是"未到期就不计数"）
            SimulateRequests(clock, 40, _ =>
            {
                var result = scheduler.RunDueTasks();
                HardwareMonitor.RecordMaintenanceTriggers(statistics, result);
            });

            Assert.Equal(0, saves);
            Assert.Equal(0, statistics.TrafficSaveTriggers);
            Assert.Equal(0, statistics.GcRequestTriggers);
            Assert.Equal(0, statistics.WorkingSetTrimTriggers);
        }

        [Fact]
        public void 维护调度状态由HardwareMonitor自己的实例持有()
        {
            // 计划 §4.3：「周期状态属于正确的服务实例，不要用易互相污染的全局静态时间戳」
            var field = typeof(HardwareMonitor)
                .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType == typeof(MaintenanceScheduler));

            Assert.NotNull(field);
            Assert.True(field.IsInitOnly);   // readonly：实例级状态，不会被换掉
        }

        [Fact]
        public void SystemOptimizer能创建调用方自持的调度器实例()
        {
            // 只创建、不跑到期任务（跑到期会真的写流量历史文件）
            var scheduler = SystemOptimizer.CreateScheduler();
            Assert.NotNull(scheduler);
            Assert.True(scheduler.NextTrafficSaveDelay > TimeSpan.Zero);
        }

        // =========================================================
        // 3. 网卡 / 磁盘计数不重复
        // =========================================================

        [Fact]
        public void 网卡计数不重复_子管理器记一次采集层不再记()
        {
            var statistics = new HardwareSamplingStatistics();
            var manager = new NetworkManager();
            manager.SamplingStatistics = statistics;

            var counter = new HardwareDeviceUpdateCounter(statistics);
            var net = new FakeNetworkHardware("WiringTestFakeNic");
            var cfg = new Settings { LastAutoNetwork = net.Name };

            // NetworkManager：目标网卡真的执行了 Update -> 记 1 次
            manager.ProcessUpdate(net, cfg, 1.0, false);
            Assert.Equal(1, net.UpdateCount);

            // 采集层如果再记一次，同一个 Key 就会被记两遍 —— 必须被忽略
            counter.Record(net.HardwareType + ":" + net.Name, HardwareDeviceUpdateCounter.NetworkCategory);

            var updates = statistics.SnapshotDeviceUpdates();
            Assert.Equal(1, updates["Network:WiringTestFakeNic"]);
            Assert.Equal(1, statistics.SnapshotCategoryTotals()["Network"]);
        }

        [Fact]
        public void 磁盘计数不重复_子管理器记一次采集层不再记()
        {
            var statistics = new HardwareSamplingStatistics();
            var clock = new FakeTimeProvider();
            var manager = new DiskManager(clock);
            manager.SamplingStatistics = statistics;

            var counter = new HardwareDeviceUpdateCounter(statistics);
            var disk = new FakeStorageHardware("E: WiringTestDisk");
            var cfg = new Settings();

            manager.ProcessUpdate(disk, cfg, true, true);
            Assert.Equal(1, disk.UpdateCount);

            counter.Record(disk.HardwareType + ":" + disk.Name, HardwareDeviceUpdateCounter.StorageCategory);

            var updates = statistics.SnapshotDeviceUpdates();
            Assert.Equal(1, updates["Storage:E: WiringTestDisk"]);
            Assert.Equal(1, statistics.SnapshotCategoryTotals()["Storage"]);
        }

        [Fact]
        public void 采集层负责的分类照常计数()
        {
            var statistics = new HardwareSamplingStatistics();
            var counter = new HardwareDeviceUpdateCounter(statistics);

            counter.Record("GPU.0", "Gpu");
            counter.Record("Cpu:AMD Ryzen 7 5800X", "Cpu");
            counter.Record("Motherboard:ASUS", "Motherboard");

            var updates = statistics.SnapshotDeviceUpdates();
            Assert.Equal(1, updates["GPU.0"]);
            Assert.Equal(1, updates["Cpu:AMD Ryzen 7 5800X"]);
            Assert.Equal(1, updates["Motherboard:ASUS"]);
            Assert.Equal(3, statistics.SnapshotCategoryTotals().Values.Sum());
        }

        [Fact]
        public void 网卡与磁盘的计数归属判定()
        {
            Assert.False(HardwareDeviceUpdateCounter.IsCollectLayerOwned(HardwareDeviceUpdateCounter.NetworkCategory));
            Assert.False(HardwareDeviceUpdateCounter.IsCollectLayerOwned(HardwareDeviceUpdateCounter.StorageCategory));
            Assert.True(HardwareDeviceUpdateCounter.IsCollectLayerOwned("Cpu"));
            Assert.True(HardwareDeviceUpdateCounter.IsCollectLayerOwned("Gpu"));
            Assert.True(HardwareDeviceUpdateCounter.IsCollectLayerOwned("Memory"));
            Assert.True(HardwareDeviceUpdateCounter.IsCollectLayerOwned("Battery"));
            Assert.True(HardwareDeviceUpdateCounter.IsCollectLayerOwned("Motherboard"));
        }

        // =========================================================
        // 4. forceAll 语义（有效 Web 数据需求）
        // =========================================================

        [Fact]
        public void Web开关开着但没有合法需求时不扩大采集范围()
        {
            var cfg = new Settings { WebServerEnabled = true };
            cfg.MonitorItems.Add(new MonitorItemConfig { Key = "CPU.Load", VisibleInPanel = true });

            var req = HardwareUpdateRequirements.Compute(cfg, hasWebDataDemand: false, usePerformanceCounters: true);

            Assert.False(req.ForceAll, "没有合法 Web 需求时 ForceAll 必须为假（Web 开关不得顺带扩大采集范围）");
            // 本地需求仍然生效
            Assert.True(req.NeedCpu, "本地已启用的 CPU 指标必须仍在需求范围内");
            // 本地没开的项目不会被"Web 开关"顺带打开
            Assert.False(req.NeedGpu, "Web 开关不应打开 GPU 需求");
            Assert.False(req.NeedMem, "Web 开关不应打开内存需求");
            Assert.False(req.NeedNet, "Web 开关不应打开网卡需求");
            Assert.False(req.NeedDisk, "Web 开关不应打开磁盘需求");
            Assert.False(req.NeedBat, "Web 开关不应打开电池需求");
            Assert.False(req.NeedMobo, "Web 开关不应打开主板需求");
        }

        [Fact]
        public void 有合法Web需求时按全量普通指标需求更新()
        {
            var cfg = new Settings { WebServerEnabled = true };

            var req = HardwareUpdateRequirements.Compute(cfg, hasWebDataDemand: true, usePerformanceCounters: true);

            Assert.True(req.ForceAll, "有合法 Web 需求时必须按全量更新");
            Assert.True(req.NeedCpu, "全量需求应包含 CPU");
            Assert.True(req.NeedGpu, "全量需求应包含显卡");
            Assert.True(req.NeedMem, "全量需求应包含内存");
            Assert.True(req.NeedNet, "全量需求应包含网卡");
            Assert.True(req.NeedDisk, "全量需求应包含磁盘");
            Assert.True(req.NeedBat, "全量需求应包含电池");
            Assert.True(req.NeedMobo, "全量需求应包含主板");
        }

        [Fact]
        public void 断开网页后本地需求范围恢复()
        {
            var cfg = new Settings { WebServerEnabled = true };
            cfg.MonitorItems.Add(new MonitorItemConfig { Key = "CPU.Load", VisibleInPanel = true });
            cfg.MonitorItems.Add(new MonitorItemConfig { Key = "GPU.0.Load", VisibleInPanel = true });

            var withWeb = HardwareUpdateRequirements.Compute(cfg, hasWebDataDemand: true, usePerformanceCounters: true);
            Assert.True(withWeb.ForceAll);
            Assert.True(withWeb.NeedBat);        // 全量时电池也在范围内

            var afterDisconnect = HardwareUpdateRequirements.Compute(cfg, hasWebDataDemand: false, usePerformanceCounters: true);
            Assert.False(afterDisconnect.ForceAll);
            Assert.True(afterDisconnect.NeedCpu);    // 本地需求**不会**因断开网页被清空
            Assert.True(afterDisconnect.NeedGpu);
            Assert.False(afterDisconnect.NeedBat);   // 恢复为本地范围
        }

        [Fact]
        public void 全量Web需求不绕过显卡选择_只监控一张卡时不隐式更新两张物理卡()
        {
            var gpu0 = new FakeGpuHardware("NVIDIA RTX 3080");
            var gpu1 = new FakeGpuHardware("NVIDIA RTX 3080 Ti");

            // MonitoredGpuCount = 1 -> 监控集合里只有一张卡
            var monitored = new List<IHardware> { gpu0 };

            Assert.True(GpuUpdateSelection.ShouldUpdate(gpu0, gpu0, monitored));
            Assert.False(GpuUpdateSelection.ShouldUpdate(gpu1, gpu0, monitored));
        }

        [Fact]
        public void 配置两卡时两卡都必须更新()
        {
            var gpu0 = new FakeGpuHardware("NVIDIA RTX 3080");
            var gpu1 = new FakeGpuHardware("NVIDIA RTX 3080 Ti");

            // MonitoredGpuCount = 2 -> 监控集合里有两张卡
            var monitored = new List<IHardware> { gpu0, gpu1 };

            Assert.True(GpuUpdateSelection.ShouldUpdate(gpu0, gpu0, monitored));
            Assert.True(GpuUpdateSelection.ShouldUpdate(gpu1, gpu0, monitored));
        }

        [Fact]
        public void 首选卡始终会被更新()
        {
            var gpu0 = new FakeGpuHardware("NVIDIA RTX 3080");
            var gpu1 = new FakeGpuHardware("NVIDIA RTX 3080 Ti");

            var monitored = new List<IHardware> { gpu1 };   // 首选卡不在监控集合里

            Assert.True(GpuUpdateSelection.ShouldUpdate(gpu0, gpu0, monitored));
            Assert.True(GpuUpdateSelection.ShouldUpdate(gpu1, gpu0, monitored));
        }

        [Fact]
        public void 显卡映射还没建好时保持旧行为_更新所有显卡()
        {
            var gpu0 = new FakeGpuHardware("NVIDIA RTX 3080");
            var gpu1 = new FakeGpuHardware("NVIDIA RTX 3080 Ti");

            // 映射为空（热插拔后到下次 Rebuild 之间）：不能一张卡都不更新
            Assert.True(GpuUpdateSelection.ShouldUpdate(gpu0, null, new List<IHardware>()));
            Assert.True(GpuUpdateSelection.ShouldUpdate(gpu1, null, new List<IHardware>()));
        }

        // =========================================================
        // 5. 交给 B2 的公开接口契约
        // =========================================================

        [Fact]
        public void HardwareMonitor暴露HasFpsData供网页区分未启用()
        {
            var prop = typeof(HardwareMonitor).GetProperty("HasFpsData");
            Assert.NotNull(prop);
            Assert.Equal(typeof(bool), prop.PropertyType);
            Assert.True(prop.CanRead);
            Assert.Null(prop.SetMethod);   // 只读
        }

        [Fact]
        public void SetWebDataDemand是公开的bool入口()
        {
            var method = typeof(HardwareMonitor).GetMethod("SetWebDataDemand", new[] { typeof(bool) });
            Assert.NotNull(method);
            Assert.True(method.IsPublic);
            Assert.Equal(typeof(void), method.ReturnType);
        }

        // =========================================================
        // 6. FPS 需求接线（硬件侧 ↔ 网页侧共用同一套规则）
        // =========================================================

        /// <summary>造一份"本地 FPS 是否启用 + 网页版帧率开关"的设置。</summary>
        private static Settings MakeFpsSettings(bool localFpsEnabled, bool webFpsEnabled)
        {
            var cfg = new Settings { WebFpsEnabled = webFpsEnabled };
            if (localFpsEnabled)
                cfg.MonitorItems.Add(new MonitorItemConfig { Key = "FPS", VisibleInPanel = true });
            return cfg;
        }

        [Fact]
        public void 本地启用FPS时硬件侧需求为真()
        {
            var cfg = MakeFpsSettings(localFpsEnabled: true, webFpsEnabled: false);
            var probe = HardwareMonitor.CreateFpsDemandProbe(cfg, () => false);

            Assert.True(probe());
        }

        [Fact]
        public void 有合法Web需求且WebFpsEnabled为真时硬件侧需求为真()
        {
            var cfg = MakeFpsSettings(localFpsEnabled: false, webFpsEnabled: true);
            var probe = HardwareMonitor.CreateFpsDemandProbe(cfg, () => true);

            Assert.True(probe());
        }

        [Fact]
        public void 有合法Web需求但WebFpsEnabled为假时硬件侧需求为假()
        {
            // 本地也没启用 FPS
            var cfg = MakeFpsSettings(localFpsEnabled: false, webFpsEnabled: false);
            var probe = HardwareMonitor.CreateFpsDemandProbe(cfg, () => true);

            Assert.False(probe());
        }

        [Fact]
        public void 没有Web需求时硬件侧需求只由本地启用决定()
        {
            // 本地启用 -> 有需求（与 Web 无关）
            var localOn = HardwareMonitor.CreateFpsDemandProbe(
                MakeFpsSettings(localFpsEnabled: true, webFpsEnabled: true), () => false);
            Assert.True(localOn());

            // 本地没启用 -> 没需求（哪怕 WebFpsEnabled 开着，因为没有合法 Web 需求）
            var localOff = HardwareMonitor.CreateFpsDemandProbe(
                MakeFpsSettings(localFpsEnabled: false, webFpsEnabled: true), () => false);
            Assert.False(localOff());
        }

        [Fact]
        public void 硬件侧FPS需求探针与网页侧调用同一个规则方法()
        {
            // ★ 结构断言（**不是**"比较两次调用的结果"）：
            //   旧写法断言 `probe() == WebFpsDemand.IsFpsDemand(cfg, demand)`，
            //   而 probe 的实现体**就是同一个调用** —— 退化成 f(x)==f(x)，
            //   两侧一起变好一起变坏，无法发现"两侧漂移"。
            //
            //   这里断言的是**同一个 MethodInfo**：硬件侧探针与网页侧
            //   都必须调用 WebFpsDemand.IsFpsDemand 这**唯一**的规则实现。
            //   只要有人把探针改成手写副本（不再调用 WebFpsDemand），本用例立刻变红。
            //   规则的真值表由上面 4 个 FPS 用例覆盖。
            var rule = typeof(WebFpsDemand).GetMethod(
                "IsFpsDemand",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Settings), typeof(bool) },
                null);
            Assert.NotNull(rule);

            var cfg = MakeFpsSettings(localFpsEnabled: true, webFpsEnabled: true);
            var probe = HardwareMonitor.CreateFpsDemandProbe(cfg, () => true);
            Assert.NotNull(probe.Method);

            // ① 硬件侧：探针自身的实现体里必须调用 WebFpsDemand.IsFpsDemand
            var probeScan = IlInspector.Scan(probe.Method);
            Assert.Equal(0, probeScan.UnresolvedCalls);
            Assert.Contains(probeScan.Calls, c => SameMethod(c, rule));

            // ② 网页侧：快照输出用的也是同一个方法
            var webServerType = typeof(WebFpsDemand).Assembly.GetType("LiteMonitor.src.WebServer.LiteWebServer");
            Assert.NotNull(webServerType);

            bool webSideUsesRule = false;
            foreach (var method in AllMethodsIncludingNested(webServerType))
            {
                var scan = IlInspector.Scan(method);
                if (scan.Calls.Any(c => SameMethod(c, rule)))
                {
                    webSideUsesRule = true;
                    break;
                }
            }
            Assert.True(webSideUsesRule, "网页侧必须调用同一个 WebFpsDemand.IsFpsDemand，否则两侧会漂移");
        }

        [Fact]
        public void FPS需求探针注入后FpsCounter按同一规则判定需求()
        {
            // 走**真实接线路径**：AttachFpsDemandProbe -> FpsCounter.FpsDemandProvider
            // 用假启动器/假进程查询器，不触碰真实 PresentMon / ETW / 网络 / 驱动。
            var launcher = new FakeFpsLauncher();
            using var counter = new FpsCounter(null, launcher, new FakeFpsProcessQuery(), () => false);

            var cfg = MakeFpsSettings(localFpsEnabled: false, webFpsEnabled: true);
            bool demand = false;
            HardwareMonitor.AttachFpsDemandProbe(counter, cfg, () => demand);

            // 没有 Web 需求 -> 没需求
            Assert.False(counter.IsFpsDemandActive);

            // 有 Web 需求且 WebFpsEnabled -> 有需求
            demand = true;
            Assert.True(counter.IsFpsDemandActive);

            // 关掉网页版帧率开关 -> 又没有需求
            cfg.WebFpsEnabled = false;
            Assert.False(counter.IsFpsDemandActive);

            // 打开本地 FPS -> 需求与 Web 无关，仍然为真
            cfg.MonitorItems.Add(new MonitorItemConfig { Key = "FPS", VisibleInPanel = true });
            demand = false;
            Assert.True(counter.IsFpsDemandActive);
        }

        // =========================================================
        // 7. 失败采集计数（FailedCollections）护栏
        //
        //    ★ 复审 M5：把失败计数删掉 -> 计数恒为 0，而全量用例仍全绿。
        //      本组用例把"失败恰好被记一次"变成**可回归**的护栏。
        // =========================================================

        [Fact]
        public void 采集失败经接缝计数恰好加一()
        {
            var statistics = new HardwareSamplingStatistics();
            var logged = new List<KeyValuePair<string, Exception>>();
            var failure = new InvalidOperationException("接线测试：模拟采集异常");

            HardwareMonitor.RecordCollectionFailure(
                statistics,
                failure,
                "CollectOnce",
                (source, ex) => logged.Add(new KeyValuePair<string, Exception>(source, ex)));

            // ★ 头号不变量：一次失败 -> FailedCollections 恰好 +1（不得 +2）
            Assert.Equal(1, statistics.FailedCollections);

            // ★ 错误日志不能因为抽接缝而被吞掉
            Assert.Single(logged);
            Assert.Equal("CollectOnce", logged[0].Key);
            Assert.Same(failure, logged[0].Value);
        }

        [Fact]
        public void 采集层吞掉异常时协调器不重复计数()
        {
            // ★ 互斥性：用**真实生产类**驱动
            //   - 采集层接缝：HardwareMonitor.RecordCollectionFailure
            //   - 协调器：    HardwareSamplingCoordinator（真实生产类）
            // 采集层吞掉异常时由采集层记一次，协调器**不得**再记一次。
            var statistics = new HardwareSamplingStatistics();
            var logged = new List<string>();
            var failure = new InvalidOperationException("接线测试：模拟采集异常");

            using var coordinator = new HardwareSamplingCoordinator(
                periodProvider: () => 1000,
                sampler: (ct) =>
                {
                    try
                    {
                        throw failure;
                    }
                    catch (Exception ex)
                    {
                        // 采集层契约：吞掉异常 + 自己记一次失败（**绝不能**再抛）
                        HardwareMonitor.RecordCollectionFailure(
                            statistics, ex, "CollectOnce",
                            (source, e) => logged.Add(source));
                    }
                    return Task.CompletedTask;
                },
                statistics: statistics,
                errorLogger: (source, ex) => logged.Add(source));

            coordinator.SetReady(true);
            bool triggered = coordinator.RequestCollection();

            Assert.True(triggered, "已就绪的协调器应执行一次真实采集");
            // ★ 一次失败 -> 恰好 +1（采集层记了一次，协调器没有再记）
            Assert.Equal(1, statistics.FailedCollections);
            // 采集层确实吞掉了异常：协调器看到的是"成功"，只留下采集层写的那一条日志
            Assert.Single(logged);
            Assert.Equal("CollectOnce", logged[0]);
        }

        [Fact]
        public void 采集层若把异常上抛则同一次失败会被记两次()
        {
            // ★ 反向对照（敏感度自证）：证明上一条用例的断言**能分辨** +1 与 +2。
            //   采集层若违反契约把异常再抛出去，协调器会再记一次失败 -> 计数变成 2。
            //   （这正是复审变异 M1 描述的现象，用例把它固定下来。）
            var statistics = new HardwareSamplingStatistics();

            using var coordinator = new HardwareSamplingCoordinator(
                periodProvider: () => 1000,
                sampler: (ct) =>
                {
                    try
                    {
                        throw new InvalidOperationException("接线测试：模拟采集异常");
                    }
                    catch (Exception ex)
                    {
                        HardwareMonitor.RecordCollectionFailure(
                            statistics, ex, "CollectOnce", (source, e) => { });
                        throw;   // ★ 违反采集层契约：把异常再抛出去
                    }
                },
                statistics: statistics,
                errorLogger: (source, ex) => { });

            coordinator.SetReady(true);
            coordinator.RequestCollection();

            Assert.Equal(2, statistics.FailedCollections);
        }

        [Fact]
        public void 失败接缝默认仍然写真实错误日志文件()
        {
            // 默认出口写的是运行目录下的 LiteMonitor++_HardwareError.log。
            // ★ 测试结束后必须还原现场（复审会检查测试输出目录有没有残留副作用文件）。
            string logPath = Path.Combine(AppContext.BaseDirectory, "LiteMonitor++_HardwareError.log");
            bool existedBefore = File.Exists(logPath);
            string original = existedBefore ? File.ReadAllText(logPath) : null;

            try
            {
                var statistics = new HardwareSamplingStatistics();
                var failure = new InvalidOperationException("接线测试：默认错误日志出口");

                HardwareMonitor.RecordCollectionFailure(statistics, failure, "UpdateAll.GPU");

                Assert.Equal(1, statistics.FailedCollections);
                Assert.True(File.Exists(logPath), "默认出口应写出硬件错误日志文件");
                string content = File.ReadAllText(logPath);
                Assert.Contains("[UpdateAll.GPU]", content);
                Assert.Contains("接线测试：默认错误日志出口", content);
            }
            finally
            {
                // 还原现场：测试前不存在 -> 删掉；测试前存在 -> 写回原内容。
                if (existedBefore)
                {
                    if (original != null) File.WriteAllText(logPath, original);
                }
                else if (File.Exists(logPath))
                {
                    File.Delete(logPath);
                }
            }
        }

        [Fact]
        public void 两个失败出口都经过失败计数接缝()
        {
            // ★ 结构断言：CollectOnce 里的**两个**失败出口
            //   （显卡分支 catch / 外层 catch）都必须调用统一的失败计数接缝，
            //   且各自的 source 标签不能被改掉（复审会对比改动前后的日志内容）。
            var method = typeof(HardwareMonitor).GetMethod(
                "CollectOnce", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var scan = IlInspector.Scan(method);
            Assert.Equal(0, scan.UnresolvedCalls);

            int seamCalls = scan.Calls.Count(c =>
                c.DeclaringType == typeof(HardwareMonitor) && c.Name == "RecordCollectionFailure");
            Assert.Equal(2, seamCalls);

            // 两个失败出口的 source 标签
            Assert.Contains("CollectOnce", scan.Strings);
            Assert.Contains("UpdateAll.GPU", scan.Strings);

            // 反向：采集层不得再绕过接缝直接调用统计实例的内部计数方法
            Assert.DoesNotContain(scan.Calls, c => c.Name == "RecordFailedCollection");
        }

        // =========================================================
        // 8. 设备计数归属护栏（网卡 / 磁盘不重复计数）
        // =========================================================

        [Fact]
        public void 采集层不直连统计实例记录设备更新()
        {
            // ★ 结构断言（复审 M2 的护栏）：
            //   采集层（HardwareMonitor）**不得**直接调用统计实例的设备计数方法；
            //   设备计数只能经 HardwareDeviceUpdateCounter（守卫：Network/Storage 被忽略）。
            //   复审 M2 = 恢复旧调用点 + 绕过守卫 -> 两条都必须在下面变红。
            int directCalls = 0;
            foreach (var method in AllMethodsIncludingNested(typeof(HardwareMonitor)))
            {
                var scan = IlInspector.Scan(method);
                directCalls += scan.Calls.Count(c =>
                    c.DeclaringType == typeof(HardwareSamplingStatistics) && c.Name == "RecordDeviceUpdate");
            }
            Assert.Equal(0, directCalls);

            // ★ 正向对照（防止扫描器失效导致上面的断言变成空断言）：
            //   采集层的计数入口必须真的调用 HardwareDeviceUpdateCounter.Record
            var counterType = typeof(HardwareDeviceUpdateCounter);
            Assert.NotNull(counterType);

            var entry = typeof(HardwareMonitor).GetMethod(
                "RecordDeviceUpdate", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(entry);

            var entryScan = IlInspector.Scan(entry);
            Assert.Equal(0, entryScan.UnresolvedCalls);
            Assert.Contains(entryScan.Calls, c =>
                c.DeclaringType == counterType && c.Name == "Record");
        }

        [Fact]
        public void 采集层记录网卡与磁盘不计数_记录CPU与显卡才计数()
        {
            var statistics = new HardwareSamplingStatistics();
            var counter = new HardwareDeviceUpdateCounter(statistics);

            // 采集层入口（守卫路径）对网卡/磁盘必须**不计数**
            counter.Record("Network:接线测试网卡", HardwareDeviceUpdateCounter.NetworkCategory);
            counter.Record("Storage:接线测试磁盘", HardwareDeviceUpdateCounter.StorageCategory);

            // 采集层负责的分类照常计数
            counter.Record("Cpu:接线测试CPU", "Cpu");
            counter.Record("GPU.0", "Gpu");

            var updates = statistics.SnapshotDeviceUpdates();
            var totals = statistics.SnapshotCategoryTotals();

            Assert.False(updates.ContainsKey("Network:接线测试网卡"), "网卡由子管理器计数，采集层不得再记");
            Assert.False(updates.ContainsKey("Storage:接线测试磁盘"), "磁盘由子管理器计数，采集层不得再记");
            Assert.False(totals.ContainsKey("Network"), "网卡分类汇总不应由采集层产生");
            Assert.False(totals.ContainsKey("Storage"), "磁盘分类汇总不应由采集层产生");

            Assert.Equal(1, updates["Cpu:接线测试CPU"]);
            Assert.Equal(1, updates["GPU.0"]);
            Assert.Equal(2, totals.Values.Sum());
        }

        // =========================================================
        // 9. 调度器线程安全（同一到期时间最多消费一次）
        // =========================================================

        [Fact]
        public void 最小间隔触发器的比较与推进在同一临界区内()
        {
            // ★ 确定性护栏：公开类型 HardwareSamplingSchedule 的文档承诺
            //   "同一到期时间最多消费一次"，该承诺在并发下成立的**前提**是
            //   "比较 + 推进"被同一把锁保护住。
            //
            //   并发压力用例（同上）对"去掉锁"并不敏感（竞争窗口只有几纳秒，实测抓不到），
            //   所以这里用**结构断言**锁死：TryConsume 的方法体必须进入/退出 Monitor。
            //   锁被去掉、被换成别的对象、或锁范围被缩小，这条用例都会变红。
            var triggerType = typeof(HardwareSamplingSchedule).GetNestedType(
                "MinGapTrigger", BindingFlags.NonPublic);
            Assert.NotNull(triggerType);

            var tryConsume = triggerType.GetMethod(
                "TryConsume", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(tryConsume);

            var scan = IlInspector.Scan(tryConsume);
            Assert.Equal(0, scan.UnresolvedCalls);

            Assert.Contains(scan.Calls, c => c.DeclaringType == typeof(Monitor) && c.Name == "Enter");
            Assert.Contains(scan.Calls, c => c.DeclaringType == typeof(Monitor) && c.Name == "Exit");
        }

        [Fact]
        public void 同一到期时间并发消费只成功一次()
        {
            // 公开类型 HardwareSamplingSchedule 的文档承诺"同一到期时间最多消费一次"，
            // 这条承诺必须在**并发**下也成立：比较与推进必须在同一个临界区内完成。
            //
            // ★ 确定性由上一条用例（结构断言）保证；本条负责记录并发语义本身。
            int threads = 8;
            int rounds = 20;
            int consumed = 0;

            for (int round = 0; round < rounds; round++)
            {
                var clock = new FakeTimeProvider();
                var schedule = new HardwareSamplingSchedule(clock);

                int local = 0;
                var start = new ManualResetEventSlim(false);
                var workers = new Thread[threads];
                for (int i = 0; i < threads; i++)
                {
                    workers[i] = new Thread(() =>
                    {
                        start.Wait();
                        if (schedule.TryConsumeSlowScan()) Interlocked.Increment(ref local);
                    });
                    workers[i].Start();
                }

                start.Set();
                foreach (var worker in workers) worker.Join();
                consumed += local;
            }

            // 每轮只允许一个线程消费成功
            Assert.Equal(rounds, consumed);
        }
    }
}
