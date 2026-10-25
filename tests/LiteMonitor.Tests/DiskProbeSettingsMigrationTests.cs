using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P-F08a：磁盘探测策略的**配置迁移**护栏。
    ///
    /// <para><b>要守住的</b>：旧配置里没有 <c>DiskProbeMode</c> 字段时
    /// ① 必须进入**显式迁移状态**（不是静默当成"用户选了仅计数器"）；
    /// ② 首次打开硬件前仍按仅计数器运行；
    /// ③ <b>用户原有的磁盘偏好与监控项必须原样保留</b>（不清空、不改绑到别的设备）；
    /// ④ 用户明确选择后必须能持久化（Draft-Commit 落盘 + 回读一致）。</para>
    ///
    /// <para>★ 本类真的读写 <c>settings.json</c> ⇒ 挂到 <c>cfgfile</c> 集合串行执行，
    /// 用例前后保存/恢复现场（与 <c>LiveGlassSettingMigrationTests</c> 同一约定）。</para>
    /// </summary>
    [Collection("cfgfile")]
    public class DiskProbeSettingsMigrationTests : IDisposable
    {
        private static readonly string MainPath = LiteMonitor.SettingsHelper.FilePath;

        // ★ 本类会调 SettingsHelper.Load(forceReload:true) ⇒ 它会**替换全局 Settings 单例**。
        //   用例结束后必须把单例还原成进来时的那个对象，否则会污染同集合里的其它用例
        //   （与 ReviewMarketPersistenceRegressionTests.PersistenceScope 同一约定）。
        private static readonly System.Reflection.FieldInfo InstanceField =
            typeof(LiteMonitor.Settings).GetField("_instance",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        private readonly string _sandbox;
        private readonly bool _mainExisted;
        private readonly string _savedMain;
        private readonly object? _savedInstance;

        public DiskProbeSettingsMigrationTests()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), "lm-f08a-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sandbox);

            _mainExisted = File.Exists(MainPath);
            _savedMain = Path.Combine(_sandbox, "main.json");
            if (_mainExisted) File.Copy(MainPath, _savedMain, true);

            _savedInstance = InstanceField?.GetValue(null);

            LiteMonitor.SettingsHelper.GlobalBlockSave = false;
        }

        public void Dispose()
        {
            try
            {
                LiteMonitor.SettingsHelper.GlobalBlockSave = false;

                // 还原全局单例：进来时是什么，出去时还是什么。
                InstanceField?.SetValue(null, _savedInstance);

                if (_mainExisted) File.Copy(_savedMain, MainPath, true);
                else if (File.Exists(MainPath)) File.Delete(MainPath);
            }
            catch
            {
                // 现场恢复失败只影响本机测试目录，不吞掉断言结果。
            }
        }

        /// <summary>写一份**旧格式**配置：有磁盘偏好、有监控项，但**没有** DiskProbeMode 字段。</summary>
        private static void WriteLegacyConfig()
        {
            File.WriteAllText(MainPath, """
                {
                  "Version": 9,
                  "PreferredDisk": "E: LegacyDisk",
                  "LastAutoDisk": "E: LegacyDisk",
                  "MonitorItems": [
                    { "Key": "CPU.Usage", "SortIndex": 1, "TaskbarSortIndex": 1 },
                    { "Key": "DISK.Temp", "SortIndex": 405, "TaskbarSortIndex": 405, "VisibleInPanel": true },
                    { "Key": "DISK.Read", "SortIndex": 601, "TaskbarSortIndex": 601 }
                  ]
                }
                """);
        }

        [Fact]
        public void 旧配置缺字段_进入显式迁移状态且首次按仅计数器运行()
        {
            WriteLegacyConfig();

            LiteMonitor.Settings cfg = LiteMonitor.SettingsHelper.Load(forceReload: true);

            // ① 缺字段 ⇒ 哨兵值 -1（**不是** 0）：必须能与"用户明确选了仅计数器"区分开。
            Assert.Equal(DiskProbePolicy.UnconfiguredModeValue, cfg.DiskProbeMode);

            var policy = DiskProbePolicy.FromSettings(cfg);
            Assert.Equal(DiskProbeMode.CountersOnly, policy.Mode);
            Assert.Equal(DiskProbeMigrationState.Unconfigured, policy.MigrationState);
            Assert.False(policy.StorageProbeEnabled);
        }

        [Fact]
        public void 旧配置缺字段_磁盘偏好与监控项原样保留()
        {
            WriteLegacyConfig();

            LiteMonitor.Settings cfg = LiteMonitor.SettingsHelper.Load(forceReload: true);

            // ③ 迁移**不得**动用户的磁盘偏好，也**不得**删掉磁盘相关的监控项。
            Assert.Equal("E: LegacyDisk", cfg.PreferredDisk);
            Assert.Equal("E: LegacyDisk", cfg.LastAutoDisk);

            List<string> keys = cfg.MonitorItems.Select(x => x.Key).ToList();
            Assert.Contains("DISK.Temp", keys);
            Assert.Contains("DISK.Read", keys);

            var diskTemp = cfg.MonitorItems.First(x => x.Key == "DISK.Temp");
            Assert.True(diskTemp.VisibleInPanel);
            // 排序位不因迁移被清零（具体数值可能被"补齐默认项"的既有逻辑整体后移，与本包无关）。
            Assert.NotEqual(0, diskTemp.SortIndex);
        }

        [Fact]
        public void 用户明确选择后_持久化并能回读()
        {
            WriteLegacyConfig();

            LiteMonitor.Settings cfg = LiteMonitor.SettingsHelper.Load(forceReload: true);
            cfg.DiskProbeMode = (int)DiskProbeMode.SmartAllowed;
            cfg.Save();

            // ④ 落盘后键必须存在（schema 兼容：不是被删掉），且值正确。
            string json = File.ReadAllText(MainPath);
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                Assert.True(
                    doc.RootElement.TryGetProperty("DiskProbeMode", out JsonElement value),
                    "DiskProbeMode 键必须保留在 settings.json 中");
                Assert.Equal((int)DiskProbeMode.SmartAllowed, value.GetInt32());
            }

            LiteMonitor.Settings reloaded = LiteMonitor.SettingsHelper.Load(forceReload: true);
            var policy = DiskProbePolicy.FromSettings(reloaded);
            Assert.Equal(DiskProbeMode.SmartAllowed, policy.Mode);
            Assert.Equal(DiskProbeMigrationState.None, policy.MigrationState);
        }

        [Fact]
        public void 显式选择仅计数器_持久化后不再显示迁移提示()
        {
            WriteLegacyConfig();

            LiteMonitor.Settings cfg = LiteMonitor.SettingsHelper.Load(forceReload: true);
            cfg.DiskProbeMode = (int)DiskProbeMode.CountersOnly;
            cfg.Save();

            LiteMonitor.Settings reloaded = LiteMonitor.SettingsHelper.Load(forceReload: true);
            var policy = DiskProbePolicy.FromSettings(reloaded);

            Assert.Equal(DiskProbeMode.CountersOnly, policy.Mode);
            Assert.Equal(DiskProbeMigrationState.None, policy.MigrationState);
            Assert.False(policy.StorageProbeEnabled);
        }
    }
}
