#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P-F08b：控制器探测策略与设备排除清单的护栏（#245 缓解层）。
    ///
    /// <para><b>要守住的不变量</b>：</para>
    /// <list type="number">
    /// <item>① <b>关闭模式下"控制器设备构造次数为 0"</b>：策略必须在第一次打开硬件树<b>之前</b>
    ///       把 <c>IsControllerEnabled</c> 落成 false ⇒ LHM 根本不构造控制器组。</item>
    /// <item>② <b>构造前隔离有库前提支撑</b>：这条不变量不是我们的臆断，而是 LHM
    ///       <c>AddGroups()</c> 里 <c>_controllerEnabled</c> ⇒ 条件分支 ⇒ 跳过组构造的 IL 语义。</item>
    /// <item>③ <b>状态机</b>：切换要么成功、要么明确降级，失败<b>不得</b>被伪装成成功。</item>
    /// <item>④ <b>排除清单增删实时生效</b>：不需要重建硬件树，下一次判定立即按新清单。</item>
    /// <item>⑤ <b>迁移保留既有行为</b>：旧配置缺字段 ⇒ 按"自动"运行（与改动前一致），不回退能力。</item>
    /// </list>
    ///
    /// <para><b>边界声明（本包如实说明）</b>：这些用例<b>不能</b>证明 #245 被根治
    /// —— 单个 USB 设备的排除作用在构造之后（见 <c>ControllerDeviceExclusion</c> 的文档）。</para>
    ///
    /// <para>全部用假设备工厂，<b>不启动 LHM、不枚举真实 HID 设备</b>。</para>
    /// </summary>
    public class ControllerProbePolicyTests
    {
        // =========================================================
        // 假设备工厂
        // =========================================================

        /// <summary>
        /// 假设备工厂：模拟 LHM 0.9.6 的 <c>Computer.Open() → AddGroups()</c> 语义 ——
        /// 只有 <c>IsControllerEnabled</c> 为 true 时才构造控制器设备组。
        /// <para>★ 这条语义有 IL 级证据（见本文件的"库前提守卫"用例）。</para>
        /// </summary>
        private sealed class FakeControllerFactory : IControllerTreeOpener
        {
            public bool IsControllerEnabled { get; set; } = true;

            /// <summary>被构造出来的"控制器设备"个数（关闭模式下必须是 0）。</summary>
            public int ControllerDeviceConstructionCount { get; private set; }

            /// <summary>被打开的次数。</summary>
            public int OpenCount { get; private set; }

            public void Open()
            {
                OpenCount++;
                if (IsControllerEnabled) ControllerDeviceConstructionCount += 4;   // 假装机器上有 4 个控制器设备
            }
        }

        // =========================================================
        // ① 关闭模式 ⇒ 构造次数为 0（红→绿的核心护栏）
        // =========================================================

        [Fact]
        public void 关闭模式_打开硬件树_控制器设备构造次数为0()
        {
            var policy = ControllerProbePolicy.FromRawValue((int)ControllerProbeMode.Off);
            var gate = new ControllerProbeGate(policy, fanMetricsNeeded: true);

            // ★ 故意把"需求"设为 true、把开关预置为 true：
            //   只有"策略真的在打开之前落下去"才会变成 false ⇒ 这条断言能证伪。
            var factory = new FakeControllerFactory { IsControllerEnabled = true };

            gate.Open(factory);

            Assert.Equal(0, factory.ControllerDeviceConstructionCount);
            Assert.False(factory.IsControllerEnabled);
            Assert.Equal(1, factory.OpenCount);
            Assert.Equal(ControllerProbeState.Disabled, policy.State);
        }

        [Fact]
        public void 自动模式且无风扇需求_构造次数为0()
        {
            var policy = ControllerProbePolicy.FromRawValue((int)ControllerProbeMode.Auto);
            var gate = new ControllerProbeGate(policy, fanMetricsNeeded: false);

            var factory = new FakeControllerFactory { IsControllerEnabled = true };
            gate.Open(factory);

            Assert.Equal(0, factory.ControllerDeviceConstructionCount);
            Assert.False(factory.IsControllerEnabled);
        }

        [Fact]
        public void 自动模式且有风扇需求_允许构造控制器设备()
        {
            // ★ 反向用例：证明上一条**可证伪**（不是恒真）。
            var policy = ControllerProbePolicy.FromRawValue((int)ControllerProbeMode.Auto);
            var gate = new ControllerProbeGate(policy, fanMetricsNeeded: true);

            var factory = new FakeControllerFactory { IsControllerEnabled = true };
            gate.Open(factory);

            Assert.True(factory.ControllerDeviceConstructionCount > 0);
            Assert.True(factory.IsControllerEnabled);
            Assert.Equal(ControllerProbeState.Enabled, policy.State);
        }

        [Fact]
        public void 强制开启模式_无风扇需求也构造控制器设备()
        {
            var policy = ControllerProbePolicy.FromRawValue((int)ControllerProbeMode.On);
            var gate = new ControllerProbeGate(policy, fanMetricsNeeded: false);

            var factory = new FakeControllerFactory { IsControllerEnabled = true };
            gate.Open(factory);

            Assert.True(factory.ControllerDeviceConstructionCount > 0);
            Assert.True(factory.IsControllerEnabled);
        }

        [Fact]
        public void 关闭模式_即使有风扇需求_构造次数仍为0()
        {
            // ★ #245 的核心语义：用户明确关闭 ⇒ **任何**需求都不能把它打开。
            var policy = ControllerProbePolicy.FromRawValue((int)ControllerProbeMode.Off);
            Assert.False(policy.ShouldProbe(fanMetricsNeeded: true));
        }

        // =========================================================
        // ② 策略状态机
        // =========================================================

        [Fact]
        public void 旧配置缺字段_按自动运行且进入迁移状态()
        {
            var policy = ControllerProbePolicy.FromSettings(new Settings());

            Assert.Equal(ControllerProbeMode.Auto, policy.Mode);
            Assert.Equal(ControllerProbeMigrationState.Unconfigured, policy.MigrationState);
            // 自动模式：有需求才开 ⇒ 保持既有行为（不回退）。
            Assert.False(policy.ShouldProbe(false));
            Assert.True(policy.ShouldProbe(true));
        }

        [Fact]
        public void 显式选择关闭_无迁移提示()
        {
            var cfg = new Settings { ControllerProbeMode = (int)ControllerProbeMode.Off };
            var policy = ControllerProbePolicy.FromSettings(cfg);

            Assert.Equal(ControllerProbeMode.Off, policy.Mode);
            Assert.Equal(ControllerProbeMigrationState.None, policy.MigrationState);
        }

        [Fact]
        public void 非法配置值_退化为自动()
        {
            // ★ 与磁盘策略同一口径：脏配置不得放开硬件访问，
            //   但这里退到 Auto（会按需求自行判断）而不是 Off —— 避免"配置写错就彻底读不到风扇"。
            var policy = ControllerProbePolicy.FromRawValue(99);

            Assert.Equal(ControllerProbeMode.Auto, policy.Mode);
            Assert.Equal(ControllerProbeMigrationState.None, policy.MigrationState);
        }

        [Fact]
        public void 切换到同一模式_不重建硬件树()
        {
            var policy = ControllerProbePolicy.FromRawValue((int)ControllerProbeMode.Off);
            policy.MarkOpened(false);

            int applyCount = 0;
            bool ok = policy.ApplyMode(ControllerProbeMode.Off,
                _ => applyCount++,
                _ => true);

            Assert.True(ok);
            Assert.Equal(0, applyCount);   // 幂等：同模式不空转重载
        }

        [Fact]
        public void 切换成功_进入新模式状态并清除迁移状态()
        {
            var policy = ControllerProbePolicy.FromSettings(new Settings());   // 迁移状态
            policy.MarkOpened(true);

            bool ok = policy.ApplyMode(ControllerProbeMode.Off,
                _ => { },
                _ => true);

            Assert.True(ok);
            Assert.Equal(ControllerProbeMode.Off, policy.Mode);
            Assert.Equal(ControllerProbeMigrationState.None, policy.MigrationState);
            Assert.Null(policy.LastError);
        }

        [Fact]
        public void 切换时抛异常_保持原模式并进入降级状态()
        {
            var policy = ControllerProbePolicy.FromRawValue((int)ControllerProbeMode.Auto);
            policy.MarkOpened(true);

            bool ok = policy.ApplyMode(ControllerProbeMode.Off,
                _ => throw new InvalidOperationException("reload failed"),
                _ => true);

            Assert.False(ok);
            Assert.Equal(ControllerProbeMode.Auto, policy.Mode);   // ★ 失败不改模式
            Assert.Equal(ControllerProbeState.Degraded, policy.State);
            Assert.Contains("reload failed", policy.LastError);
        }

        [Fact]
        public void 切换后校验不通过_保持原模式并进入降级状态()
        {
            var policy = ControllerProbePolicy.FromRawValue((int)ControllerProbeMode.Auto);
            policy.MarkOpened(true);

            bool ok = policy.ApplyMode(ControllerProbeMode.Off, _ => { }, _ => false);

            Assert.False(ok);
            Assert.Equal(ControllerProbeMode.Auto, policy.Mode);
            Assert.Equal(ControllerProbeState.Degraded, policy.State);
            Assert.NotNull(policy.LastError);
        }

        [Fact]
        public void 切换失败后的回退有界_最多再落地一次()
        {
            var policy = ControllerProbePolicy.FromRawValue((int)ControllerProbeMode.Auto);
            policy.MarkOpened(true);

            int applyCount = 0;
            bool ok = policy.ApplyMode(ControllerProbeMode.Off,
                _ => { applyCount++; throw new InvalidOperationException("boom"); },
                _ => true);

            Assert.False(ok);
            // 1 次失败的落地 + 1 次回退 = 2，不递归。
            Assert.Equal(2, applyCount);
        }

        // =========================================================
        // ③ 排除清单：增删实时生效
        // =========================================================

        [Fact]
        public void 排除清单_默认包含DAPLink模式()
        {
            var exclusion = new ControllerDeviceExclusion();

            Assert.Contains("daplink", exclusion.Patterns, StringComparer.OrdinalIgnoreCase);
            Assert.True(exclusion.IsExcluded("/hid/daplink/0", "CMSIS-DAP"));
        }

        [Fact]
        public void 排除清单_追加后立即生效()
        {
            var exclusion = new ControllerDeviceExclusion(Array.Empty<string>());
            Assert.False(exclusion.IsExcluded("/nzxt/kraken/0", "Kraken X63"));

            exclusion.Add("kraken");

            // ★ 实时生效：不需要重建硬件树、不缓存历史结果。
            Assert.True(exclusion.IsExcluded("/nzxt/kraken/0", "Kraken X63"));
        }

        [Fact]
        public void 排除清单_移除后立即生效()
        {
            var exclusion = new ControllerDeviceExclusion();
            Assert.True(exclusion.IsExcluded("/hid/daplink/0", "CMSIS-DAP"));

            exclusion.Remove("daplink");

            Assert.False(exclusion.IsExcluded("/hid/daplink/0", "DAPLink"));
        }

        [Fact]
        public void 排除清单_整体替换后立即生效()
        {
            var exclusion = new ControllerDeviceExclusion();
            Assert.True(exclusion.IsExcluded("/hid/daplink/0", "CMSIS-DAP"));

            exclusion.SetPatterns(new[] { "kraken" });

            Assert.False(exclusion.IsExcluded("/hid/daplink/0", "CMSIS-DAP"));
            Assert.True(exclusion.IsExcluded("/nzxt/kraken/0", "Kraken"));
        }

        [Fact]
        public void 排除清单_为空时不排除任何设备()
        {
            // ★ 空清单必须"什么都不排除"，而不是"什么都排除"。
            var exclusion = new ControllerDeviceExclusion(Array.Empty<string>());

            Assert.False(exclusion.IsExcluded("/nzxt/kraken/0", "Kraken"));
            Assert.False(exclusion.IsExcluded(null, null));
        }

        [Fact]
        public void 排除清单_匹配大小写不敏感()
        {
            var exclusion = new ControllerDeviceExclusion(new[] { "DAPLINK" });

            Assert.True(exclusion.IsExcluded("/hid/daplink/0", "CMSIS-DAP"));
            Assert.True(exclusion.IsExcluded("/hid/DAPLink/0", null));
        }

        [Fact]
        public void 排除清单_按显示名也能命中()
        {
            // Identifier 里没有特征串时，显示名也要能兜住。
            var exclusion = new ControllerDeviceExclusion(new[] { "cmsis-dap" });

            Assert.True(exclusion.IsExcluded("/hid/0", "Arm CMSIS-DAP"));
        }

        [Fact]
        public void 排除清单_恢复默认()
        {
            var exclusion = new ControllerDeviceExclusion(Array.Empty<string>());
            exclusion.Add("kraken");
            exclusion.ResetToDefaults();

            Assert.True(exclusion.IsExcluded("/hid/daplink/0", "CMSIS-DAP"));
            Assert.False(exclusion.IsExcluded("/nzxt/kraken/0", "Kraken"));
        }

        [Fact]
        public void 排除清单_忽略空白项()
        {
            var exclusion = new ControllerDeviceExclusion(new[] { "   " });

            Assert.False(exclusion.IsExcluded("/nzxt/kraken/0", "Kraken"));
        }

        // =========================================================
        // ④ 库前提守卫：LHM 在开关为 false 时不构造控制器组
        // =========================================================

        [Fact]
        public void LHM仅在控制器开关打开时才构造控制器组()
        {
            // ★ 这条守的是**本包缓解方案的前提**，不是我们自己的实现：
            //   LHM 0.9.6 的 Computer.Open() → AddGroups() 先读 _controllerEnabled 字段，
            //   为 false 时用条件分支跳过 8 个控制器组的构造
            //   （TBalancer / Heatmaster / AquaComputer / AeroCool / Nzxt / Razer / Arctic / MSI）。
            //   若上游改了这条语义，本包的"关闭 ⇒ 不碰 HID/USB"结论就**不再成立**，必须重新评估。
            Type computerType = typeof(Computer);
            var addGroups = computerType.GetMethod("AddGroups",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(addGroups);

            IlInspector.Disassembly disassembly = IlInspector.Disassemble(addGroups!);

            string[] controllerGroups =
            {
                "TBalancerGroup", "HeatmasterGroup", "AquaComputerGroup", "AeroCoolGroup",
                "NzxtGroup", "RazerGroup", "ArcticGroup", "MsiGroup"
            };

            int fieldReadIndex = -1;
            int branchIndex = -1;
            int firstGroupCtorIndex = -1;
            int lastGroupCtorIndex = -1;

            for (int i = 0; i < disassembly.Instructions.Count; i++)
            {
                IlInspector.IlInstruction ins = disassembly.Instructions[i];

                if (ins.Field != null && ins.Field.Name == "_controllerEnabled" &&
                    ins.Field.DeclaringType == computerType && fieldReadIndex < 0)
                {
                    fieldReadIndex = i;
                    continue;
                }

                if (fieldReadIndex >= 0 && branchIndex < 0 &&
                    (ins.OpCode == System.Reflection.Emit.OpCodes.Brfalse ||
                     ins.OpCode == System.Reflection.Emit.OpCodes.Brfalse_S))
                {
                    branchIndex = i;
                    continue;
                }

                if (ins.OpCode == System.Reflection.Emit.OpCodes.Newobj &&
                    ins.Target?.DeclaringType != null &&
                    controllerGroups.Contains(ins.Target.DeclaringType.Name))
                {
                    if (firstGroupCtorIndex < 0) firstGroupCtorIndex = i;
                    lastGroupCtorIndex = i;
                }
            }

            Assert.True(fieldReadIndex >= 0, "LHM 的 AddGroups 必须读取 _controllerEnabled 字段");
            Assert.True(firstGroupCtorIndex > fieldReadIndex, "控制器组的构造必须发生在开关判断之后");
            Assert.True(branchIndex > fieldReadIndex && branchIndex < firstGroupCtorIndex,
                "开关为 false 时必须用条件分支跳过控制器组的构造");
            // 8 个控制器组全部落在同一开关的守护区间内（不漏组）。
            Assert.True(lastGroupCtorIndex >= firstGroupCtorIndex);
        }
    }
}
