#nullable enable annotations
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P-F08b：控制器探测策略的**配置迁移**护栏（#245 缓解层）。
    ///
    /// <para><b>要守住的</b>：旧配置里没有 <c>ControllerProbeMode</c> 字段时
    /// ① 必须进入**显式迁移状态**；
    /// ② 仍按"自动"运行 —— **与改动前的行为完全一致**（不回退 fork 已有能力）；
    /// ③ <b>用户原有的风扇/水泵偏好与监控项必须原样保留</b>；
    /// ④ 用户明确选择后必须能持久化（Draft-Commit 落盘 + 回读一致）。</para>
    ///
    /// <para>★ 本类真的读写 <c>settings.json</c> ⇒ 挂到 <c>cfgfile</c> 集合串行执行，
    /// 用例前后保存/恢复现场（与 <c>DiskProbeSettingsMigrationTests</c> 同一约定）。</para>
    /// </summary>
    [Collection("cfgfile")]
    public class ControllerProbeSettingsMigrationTests : IDisposable
    {
        private static readonly string MainPath = LiteMonitor.SettingsHelper.FilePath;

        // ★ 本类会调 SettingsHelper.Load(forceReload:true) ⇒ 它会**替换全局 Settings 单例**。
        //   用例结束后必须把单例还原成进来时的那个对象，否则会污染同集合里的其它用例。
        private static readonly FieldInfo InstanceField =
            typeof(LiteMonitor.Settings).GetField("_instance",
                BindingFlags.NonPublic | BindingFlags.Static)!;

        private readonly string _sandbox;
        private readonly bool _mainExisted;
        private readonly string _savedMain;
        private readonly object? _savedInstance;

        public ControllerProbeSettingsMigrationTests()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), "lm-f08b-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sandbox);

            _mainExisted = File.Exists(MainPath);
            _savedMain = Path.Combine(_sandbox, "main.json");
            if (_mainExisted) File.Copy(MainPath, _savedMain, true);

            _savedInstance = InstanceField.GetValue(null);

            LiteMonitor.SettingsHelper.GlobalBlockSave = false;
        }

        public void Dispose()
        {
            try
            {
                LiteMonitor.SettingsHelper.GlobalBlockSave = false;
                InstanceField.SetValue(null, _savedInstance);
                if (_mainExisted) File.Copy(_savedMain, MainPath, true);
                else if (File.Exists(MainPath)) File.Delete(MainPath);
            }
            catch
            {
                // 现场恢复失败只影响本机测试目录，不吞掉断言结果。
            }
        }

        /// <summary>写一份**旧格式**配置：有风扇/水泵偏好、有监控项，但**没有** ControllerProbeMode 字段。</summary>
        private static void WriteLegacyConfig()
        {
            File.WriteAllText(MainPath, """
                {
                  "Version": 9,
                  "PreferredCpuFan": "Legacy Cpu Fan",
                  "PreferredCpuPump": "Legacy Pump",
                  "PreferredCaseFan": "Legacy Case Fan",
                  "MonitorItems": [
                    { "Key": "CPU.Usage", "SortIndex": 1, "TaskbarSortIndex": 1 },
                    { "Key": "CPU.Fan", "SortIndex": 205, "TaskbarSortIndex": 205, "VisibleInPanel": true },
                    { "Key": "CASE.Fan", "SortIndex": 210, "TaskbarSortIndex": 210 }
                  ]
                }
                """);
        }

        [Fact]
        public void 旧配置缺字段_进入显式迁移状态且按自动运行()
        {
            WriteLegacyConfig();

            LiteMonitor.Settings cfg = LiteMonitor.SettingsHelper.Load(forceReload: true);

            // ① 缺字段 ⇒ null（键不存在）。
            Assert.Null(cfg.ControllerProbeMode);

            var policy = ControllerProbePolicy.FromSettings(cfg);

            // ② ★ 关键：自动模式 = **既有行为**（有风扇需求才开）⇒ 不回退已有能力。
            Assert.Equal(ControllerProbeMode.Auto, policy.Mode);
            Assert.Equal(ControllerProbeMigrationState.Unconfigured, policy.MigrationState);
            Assert.False(policy.ShouldProbe(fanMetricsNeeded: false));
            Assert.True(policy.ShouldProbe(fanMetricsNeeded: true));
        }

        [Fact]
        public void 未设置策略时_不往旧配置里写任何新键()
        {
            // ★ 迁移只发生在**内存**里：用户没做选择前，旧配置文件**一个字节都不该被改写**。
            WriteLegacyConfig();

            LiteMonitor.Settings cfg = LiteMonitor.SettingsHelper.Load(forceReload: true);
            cfg.Save();

            string json = File.ReadAllText(MainPath);
            using JsonDocument doc = JsonDocument.Parse(json);
            Assert.False(
                doc.RootElement.TryGetProperty("ControllerProbeMode", out _),
                "用户还没选择策略时，旧配置里不应出现 ControllerProbeMode 键");
            Assert.False(
                doc.RootElement.TryGetProperty("ControllerExclusionPatterns", out _),
                "用户还没设置排除清单时，旧配置里不应出现 ControllerExclusionPatterns 键");
        }

        [Fact]
        public void 旧配置缺字段_风扇偏好与监控项原样保留()
        {
            WriteLegacyConfig();

            LiteMonitor.Settings cfg = LiteMonitor.SettingsHelper.Load(forceReload: true);

            // ③ 迁移**不得**动用户的风扇/水泵偏好，也**不得**删掉风扇相关的监控项。
            Assert.Equal("Legacy Cpu Fan", cfg.PreferredCpuFan);
            Assert.Equal("Legacy Pump", cfg.PreferredCpuPump);
            Assert.Equal("Legacy Case Fan", cfg.PreferredCaseFan);

            var items = cfg.MonitorItems ?? new List<MonitorItemConfig>();
            Assert.Contains(items, i => i.Key == "CPU.Fan");
            Assert.Contains(items, i => i.Key == "CASE.Fan");

            // ★ 只断言"排序位没有被清零"（加载期会做规范化重排，具体数值不是本包要保证的语义）。
            Assert.True(items.First(i => i.Key == "CPU.Fan").SortIndex > 0);
            Assert.True(items.First(i => i.Key == "CASE.Fan").SortIndex > 0);
            Assert.True(items.First(i => i.Key == "CPU.Fan").VisibleInPanel);
        }

        [Fact]
        public void 用户明确选择后_持久化并能回读()
        {
            WriteLegacyConfig();

            LiteMonitor.Settings cfg = LiteMonitor.SettingsHelper.Load(forceReload: true);
            cfg.ControllerProbeMode = (int)ControllerProbeMode.Off;
            cfg.ControllerExclusionPatterns = new List<string> { "daplink", "my-weird-device" };
            cfg.Save();

            // 回读：键保留、值正确。
            LiteMonitor.Settings reread = LiteMonitor.SettingsHelper.Load(forceReload: true);
            Assert.Equal((int)ControllerProbeMode.Off, reread.ControllerProbeMode);
            Assert.NotNull(reread.ControllerExclusionPatterns);
            Assert.Contains("daplink", reread.ControllerExclusionPatterns!);
            Assert.Contains("my-weird-device", reread.ControllerExclusionPatterns!);

            var policy = ControllerProbePolicy.FromSettings(reread);
            Assert.Equal(ControllerProbeMode.Off, policy.Mode);
            Assert.Equal(ControllerProbeMigrationState.None, policy.MigrationState);
            // ★ 关闭模式：即使有风扇需求也不开（#245 的核心语义）。
            Assert.False(policy.ShouldProbe(fanMetricsNeeded: true));
        }

        [Fact]
        public void 排除清单从配置加载_实时生效()
        {
            WriteLegacyConfig();

            LiteMonitor.Settings cfg = LiteMonitor.SettingsHelper.Load(forceReload: true);
            cfg.ControllerExclusionPatterns = new List<string> { "kraken" };
            cfg.Save();

            LiteMonitor.Settings reread = LiteMonitor.SettingsHelper.Load(forceReload: true);

            // ★ 配置里的清单直接喂给排除器 ⇒ 立即生效（不需要重建硬件树）。
            var exclusion = new ControllerDeviceExclusion(reread.ControllerExclusionPatterns);
            Assert.True(exclusion.IsExcluded("/nzxt/kraken/0", "Kraken X63"));
            Assert.False(exclusion.IsExcluded("/hid/daplink/0", "CMSIS-DAP"));
        }
    }
}
