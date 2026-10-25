using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P-F08a：磁盘探测 CountersOnly 止血模式的护栏。
    ///
    /// <para><b>要守住的四条不变量</b>：</para>
    /// <list type="number">
    /// <item>① <b>禁止设备构造次数为 0</b>：仅计数器模式下，第一次打开硬件树**之前**就必须把
    ///       <c>IsStorageEnabled</c> 落成 false ⇒ LHM 根本不构造存储设备（不打开设备句柄、
    ///       不做 SMART 探测）⇒ 可移动磁盘不被占用、不与 HWiNFO 争抢设备。</item>
    /// <item>② <b>策略状态机</b>：模式切换要么成功、要么明确降级；失败**不得**被伪装成成功，
    ///       也**不得**把模式改掉。</item>
    /// <item>③ <b>缺失不是 0</b>：仅计数器模式下磁盘温度缺失（返回 null），
    ///       既不显示 0，也不回退到上一代的旧温度。</item>
    /// <item>④ <b>迁移保留用户数据</b>：旧配置缺字段 ⇒ 显式迁移状态 + 首次按仅计数器运行，
    ///       但用户原有的磁盘偏好与监控项**必须原样保留**。</item>
    /// </list>
    ///
    /// <para>全部用假设备工厂 / 假时钟 / 假计数器，<b>不启动 LHM、不打开真实存储设备</b>。</para>
    /// </summary>
    public class DiskProbePolicyTests
    {
        // =========================================================
        // 假设备工厂
        // =========================================================

        /// <summary>
        /// 假设备工厂：模拟 LHM 0.9.6 的 <c>Computer.Open() → AddGroups()</c> 语义 ——
        /// 只有 <c>IsStorageEnabled</c> 为 true 时才构造存储设备。
        /// <para>★ 这条语义有 IL 级证据（见本类的"库前提守卫"用例）：LHM 读 <c>_storageEnabled</c>
        /// 字段并用条件分支跳过 <c>StorageGroup</c> 的构造。</para>
        /// </summary>
        private sealed class FakeDeviceFactory : IHardwareTreeOpener
        {
            public bool IsStorageEnabled { get; set; } = true;

            /// <summary>被构造出来的"存储设备"个数（仅计数器模式下必须是 0）。</summary>
            public int StorageDeviceConstructionCount { get; private set; }

            /// <summary>被打开的次数。</summary>
            public int OpenCount { get; private set; }

            public void Open()
            {
                OpenCount++;
                if (IsStorageEnabled) StorageDeviceConstructionCount += 3;   // 假装机器上有 3 个盘
            }
        }

        /// <summary>假的磁盘计数器来源（Windows 性能计数器替身）。</summary>
        private sealed class FakeCounterSource : IDiskCounterSource
        {
            public float? Read = 1024f;
            public float? Write = 2048f;
            public float? Active = 42f;

            public float? GetDiskRead() => Read;
            public float? GetDiskWrite() => Write;
            public float? GetDiskActive() => Active;
        }

        /// <summary>空的 ISettings 实现（LHM 的 Hardware 基类构造函数需要它）。</summary>
        private sealed class FakeLhmSettings : ISettings
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

            public bool Contains(string name) => _values.ContainsKey(name);
            public void SetValue(string name, string value) => _values[name] = value;
            public string GetValue(string name, string value) => _values.TryGetValue(name, out var v) ? v : value;
            public void Remove(string name) => _values.Remove(name);
        }

        /// <summary>假的存储设备：只记录真实 Update 次数，不接触驱动。</summary>
        private sealed class FakeStorageHardware : Hardware
        {
            public FakeStorageHardware(string name) : base(name, new Identifier(new[] { name }), new FakeLhmSettings())
            {
            }

            public override HardwareType HardwareType => LibreHardwareMonitor.Hardware.HardwareType.Storage;

            public override ISensor[] Sensors => Array.Empty<ISensor>();

            /// <summary>真实执行过的 Update 次数。</summary>
            public int UpdateCount { get; private set; }

            public override void Update() => UpdateCount++;
        }

        // =========================================================
        // ① 假设备工厂：禁止设备构造次数为 0
        // =========================================================

        [Fact]
        public void 仅计数器模式_打开硬件树_禁止设备构造次数为0()
        {
            var policy = DiskProbePolicy.FromRawValue((int)DiskProbeMode.CountersOnly);
            var gate = new DiskProbeGate(policy);

            // ★ 故意先把开关设成 true：只有"策略真的在打开之前落下去"才会变成 false。
            var factory = new FakeDeviceFactory { IsStorageEnabled = true };

            gate.Open(factory);

            Assert.Equal(0, factory.StorageDeviceConstructionCount);
            Assert.False(factory.IsStorageEnabled);
            Assert.Equal(1, factory.OpenCount);
        }

        [Fact]
        public void 兼容模式_打开硬件树_允许构造存储设备()
        {
            // ★ 反向用例：证明上面那条断言**可证伪**（把模式换成兼容模式，构造次数必须 > 0）。
            var policy = DiskProbePolicy.FromRawValue((int)DiskProbeMode.SmartAllowed);
            var gate = new DiskProbeGate(policy);
            var factory = new FakeDeviceFactory();

            gate.Open(factory);

            Assert.True(factory.StorageDeviceConstructionCount > 0,
                "兼容模式必须仍然构造存储设备，否则本护栏的 0 只是假象");
            Assert.True(factory.IsStorageEnabled);
        }

        [Fact]
        public void 旧配置缺字段_首次打开前禁止设备构造次数为0()
        {
            // ★ 迁移语义：旧配置没有该字段 ⇒ 第一次打开硬件前仍必须是仅计数器。
            var cfg = new Settings { DiskProbeMode = DiskProbePolicy.UnconfiguredModeValue };
            var policy = DiskProbePolicy.FromSettings(cfg);

            Assert.Equal(DiskProbeMode.CountersOnly, policy.Mode);
            Assert.Equal(DiskProbeMigrationState.Unconfigured, policy.MigrationState);
            Assert.False(policy.StorageProbeEnabled);

            var factory = new FakeDeviceFactory { IsStorageEnabled = true };
            new DiskProbeGate(policy).Open(factory);

            Assert.Equal(0, factory.StorageDeviceConstructionCount);
        }

        // =========================================================
        // ② 策略状态机
        // =========================================================

        [Fact]
        public void 未配置_按仅计数器生效且进入迁移状态()
        {
            var policy = DiskProbePolicy.FromSettings(new Settings { DiskProbeMode = DiskProbePolicy.UnconfiguredModeValue });

            Assert.Equal(DiskProbeMode.CountersOnly, policy.Mode);
            Assert.Equal(DiskProbeMigrationState.Unconfigured, policy.MigrationState);
            Assert.Equal(DiskProbeState.CountersOnly, policy.State);
            Assert.False(policy.StorageProbeEnabled);
            Assert.False(policy.AllowsSmart);
            Assert.False(policy.AllowsDiskTemperature);
            Assert.True(policy.AllowsDiskCounters);
        }

        [Fact]
        public void 显式选择仅计数器_无迁移提示()
        {
            var policy = DiskProbePolicy.FromSettings(new Settings { DiskProbeMode = (int)DiskProbeMode.CountersOnly });

            Assert.Equal(DiskProbeMode.CountersOnly, policy.Mode);
            Assert.Equal(DiskProbeMigrationState.None, policy.MigrationState);
            Assert.False(policy.StorageProbeEnabled);
        }

        [Fact]
        public void 显式选择兼容模式_允许存储探测()
        {
            var policy = DiskProbePolicy.FromSettings(new Settings { DiskProbeMode = (int)DiskProbeMode.SmartAllowed });

            Assert.Equal(DiskProbeMode.SmartAllowed, policy.Mode);
            Assert.Equal(DiskProbeMigrationState.None, policy.MigrationState);
            Assert.True(policy.StorageProbeEnabled);
            Assert.True(policy.AllowsSmart);
            Assert.True(policy.AllowsDiskTemperature);
        }

        [Fact]
        public void 非法配置值_退化为仅计数器()
        {
            var policy = DiskProbePolicy.FromRawValue(99);

            Assert.Equal(DiskProbeMode.CountersOnly, policy.Mode);
            Assert.False(policy.StorageProbeEnabled);
        }

        [Fact]
        public void 切换到同一模式_不重建硬件树()
        {
            var policy = DiskProbePolicy.FromRawValue((int)DiskProbeMode.CountersOnly);
            int applyCount = 0;

            bool ok = policy.ApplyMode(DiskProbeMode.CountersOnly,
                _ => applyCount++,
                _ => true);

            Assert.True(ok);
            Assert.Equal(0, applyCount);
        }

        [Fact]
        public void 切换成功_进入新模式状态并清除迁移状态()
        {
            var policy = DiskProbePolicy.FromRawValue((int)DiskProbeMode.CountersOnly);
            DiskProbeMode? applied = null;

            bool ok = policy.ApplyMode(DiskProbeMode.SmartAllowed,
                m => applied = m,
                _ => true);

            Assert.True(ok);
            Assert.Equal(DiskProbeMode.SmartAllowed, applied);
            Assert.Equal(DiskProbeMode.SmartAllowed, policy.Mode);
            Assert.Equal(DiskProbeState.SmartAllowed, policy.State);
            Assert.Equal(DiskProbeMigrationState.None, policy.MigrationState);
            Assert.Null(policy.LastError);
        }

        [Fact]
        public void 切换时抛异常_保持原模式并进入降级状态()
        {
            var policy = DiskProbePolicy.FromRawValue((int)DiskProbeMode.CountersOnly);

            bool ok = policy.ApplyMode(DiskProbeMode.SmartAllowed,
                _ => throw new InvalidOperationException("boom"),
                _ => true);

            Assert.False(ok);
            Assert.Equal(DiskProbeMode.CountersOnly, policy.Mode);       // ★ 失败不得改模式
            Assert.Equal(DiskProbeState.Degraded, policy.State);
            Assert.False(string.IsNullOrEmpty(policy.LastError));
        }

        [Fact]
        public void 切换后校验不通过_保持原模式并进入降级状态()
        {
            var policy = DiskProbePolicy.FromRawValue((int)DiskProbeMode.CountersOnly);

            bool ok = policy.ApplyMode(DiskProbeMode.SmartAllowed,
                _ => { },
                _ => false);

            Assert.False(ok);
            Assert.Equal(DiskProbeMode.CountersOnly, policy.Mode);
            Assert.Equal(DiskProbeState.Degraded, policy.State);
            Assert.False(string.IsNullOrEmpty(policy.LastError));
        }

        [Fact]
        public void 切换失败后的回退有界_最多再落地一次()
        {
            var policy = DiskProbePolicy.FromRawValue((int)DiskProbeMode.CountersOnly);
            int applyCount = 0;

            policy.ApplyMode(DiskProbeMode.SmartAllowed,
                _ =>
                {
                    applyCount++;
                    throw new InvalidOperationException("boom");
                },
                _ => true);

            // 1 次失败 + 1 次回退，**不递归**（恢复必须有界）。
            Assert.Equal(2, applyCount);
            Assert.Equal(DiskProbeState.Degraded, policy.State);
        }

        // =========================================================
        // ③ 缺失不是 0：仅计数器模式下的磁盘取值语义
        // =========================================================

        [Fact]
        public void 仅计数器模式_不更新存储设备()
        {
            var policy = DiskProbePolicy.FromRawValue((int)DiskProbeMode.CountersOnly);
            var manager = new DiskManager(policy: policy);
            var disk = new FakeStorageHardware("E: TestDisk");

            manager.ProcessUpdate(disk, new Settings(), true, true);

            Assert.Equal(0, disk.UpdateCount);   // ★ 一次 Update 都不该发生
        }

        [Fact]
        public void 兼容模式_仍然更新存储设备()
        {
            // ★ 反向用例：证明上一条断言可证伪。
            var policy = DiskProbePolicy.FromRawValue((int)DiskProbeMode.SmartAllowed);
            var manager = new DiskManager(policy: policy);
            var disk = new FakeStorageHardware("E: TestDisk");

            manager.ProcessUpdate(disk, new Settings(), true, true);

            Assert.Equal(1, disk.UpdateCount);
        }

        [Fact]
        public void 仅计数器模式_磁盘温度缺失而不是0且不回退旧值()
        {
            var policy = DiskProbePolicy.FromRawValue((int)DiskProbeMode.CountersOnly);
            var manager = new DiskManager(policy: policy);

            var lastValid = new Dictionary<string, float> { ["DISK.Temp"] = 42f };
            var syncLock = new object();

            float? value = manager.GetBestValue("DISK.Temp", new Computer(), new Settings(), lastValid, syncLock);

            Assert.Null(value);                                  // ★ 缺失，不是 0
            Assert.Equal(42f, lastValid["DISK.Temp"]);            // 旧值没被当成当前值返回
        }

        [Fact]
        public void 仅计数器模式_速度与活动率来自Windows计数器()
        {
            var policy = DiskProbePolicy.FromRawValue((int)DiskProbeMode.CountersOnly);
            var counters = new FakeCounterSource();
            var manager = new DiskManager(policy: policy, counters: counters);
            var syncLock = new object();

            Assert.Equal(1024f, manager.GetBestValue("DISK.Read", new Computer(), new Settings(), new Dictionary<string, float>(), syncLock));
            Assert.Equal(2048f, manager.GetBestValue("DISK.Write", new Computer(), new Settings(), new Dictionary<string, float>(), syncLock));
            Assert.Equal(42f, manager.GetBestValue("DISK.Activity", new Computer(), new Settings(), new Dictionary<string, float>(), syncLock));
        }

        [Fact]
        public void 仅计数器模式_关闭Windows计数器时速度缺失而不是0()
        {
            var policy = DiskProbePolicy.FromRawValue((int)DiskProbeMode.CountersOnly);
            var manager = new DiskManager(policy: policy, counters: new FakeCounterSource());
            var cfg = new Settings { UseWinPerCounters = false };

            float? value = manager.GetBestValue("DISK.Read", new Computer(), cfg, new Dictionary<string, float>(), new object());

            Assert.Null(value);
        }

        [Fact]
        public void 兼容模式_磁盘温度仍走传感器路径()
        {
            // 兼容模式下 DiskManager 不施加任何限制（策略为 null 时同样如此 ⇒ 既有调用点行为不变）。
            var manager = new DiskManager();
            var cfg = new Settings();

            float? value = manager.GetBestValue("DISK.Temp", new Computer(), cfg, new Dictionary<string, float>(), new object());

            // 机器上没有存储设备时取不到温度 ⇒ 缺失（而不是 0）。
            Assert.Null(value);
        }

        // =========================================================
        // ④ 库前提守卫：LHM 只在开关打开时构造存储设备
        // =========================================================

        [Fact]
        public void LHM仅在开关打开时才构造存储设备组()
        {
            // ★ 这条用例守的是**本包止血方案的前提**，不是我们自己的实现：
            //   LHM 0.9.6 的 Computer.Open() → AddGroups() 先读 _storageEnabled 字段，
            //   为 false 时用条件分支跳过 StorageGroup 的构造（⇒ 不构造任何存储设备）。
            //   若上游改了这条语义，本包的"仅计数器 ⇒ 不碰设备"结论就**不再成立**，必须重新评估。
            Type computerType = typeof(Computer);
            MethodInfo? addGroups = computerType.GetMethod("AddGroups",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(addGroups);

            IlInspector.Disassembly disassembly = IlInspector.Disassemble(addGroups!);

            int fieldReadIndex = -1;
            int storageGroupCtorIndex = -1;
            int branchIndex = -1;

            for (int i = 0; i < disassembly.Instructions.Count; i++)
            {
                IlInspector.IlInstruction ins = disassembly.Instructions[i];
                if (ins.Field != null && ins.Field.Name == "_storageEnabled" &&
                    ins.Field.DeclaringType == computerType && fieldReadIndex < 0)
                {
                    fieldReadIndex = i;
                    continue;
                }

                if (ins.OpCode == System.Reflection.Emit.OpCodes.Brfalse ||
                    ins.OpCode == System.Reflection.Emit.OpCodes.Brfalse_S)
                {
                    if (fieldReadIndex >= 0 && branchIndex < 0) branchIndex = i;
                    continue;
                }

                if (ins.Target != null && ins.Target.DeclaringType != null &&
                    ins.Target.DeclaringType.FullName == "LibreHardwareMonitor.Hardware.Storage.StorageGroup" &&
                    ins.OpCode == System.Reflection.Emit.OpCodes.Newobj &&
                    storageGroupCtorIndex < 0)
                {
                    storageGroupCtorIndex = i;
                }
            }

            Assert.True(fieldReadIndex >= 0, "LHM 的 AddGroups 必须读取 _storageEnabled 字段");
            Assert.True(storageGroupCtorIndex > fieldReadIndex,
                "存储设备组的构造必须发生在开关判断之后");
            Assert.True(branchIndex > fieldReadIndex && branchIndex < storageGroupCtorIndex,
                "开关为 false 时必须用条件分支跳过存储设备组的构造");
        }
    }
}
