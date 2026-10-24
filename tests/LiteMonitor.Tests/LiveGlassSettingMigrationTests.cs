using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// R3 改动三（移除旧「实时玻璃」开关）的迁移回归。
    ///
    /// <para><b>为什么迁移落点是本护栏的核心：</b>UI 开关已删——若配置里残留
    /// <c>LiveGlassEnabled=true</c>，实时玻璃会继续运行且**无任何入口可关闭**（R3 §4.3-3.2）。
    /// 迁移必须落在**每次加载都执行**的路径上；绝不允许退回
    /// <c>RebuildAndMigrateSettings</c>（那只在 isLegacyConfig——TaskbarSortIndex 全零的
    /// 遗留配置——时执行，普通用户永远不会被迁移，R3 §4.3-3.1）。</para>
    ///
    /// <para>★ 本类真的读写 <c>settings.json</c> ⇒ 挂到 <c>cfgfile</c> 集合串行执行，
    /// 用例前后保存/恢复现场（与 BatchD3 相同的接缝）。</para>
    /// </summary>
    [Collection("cfgfile")]
    public class LiveGlassSettingMigrationTests : IDisposable
    {
        private static readonly string MainPath = LiteMonitor.SettingsHelper.FilePath;

        private readonly string _sandbox;
        private readonly bool _mainExisted;
        private readonly string _savedMain;

        public LiveGlassSettingMigrationTests()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), "lm-r3-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sandbox);

            _mainExisted = File.Exists(MainPath);
            _savedMain = Path.Combine(_sandbox, "main.json");
            if (_mainExisted) File.Copy(MainPath, _savedMain, true);

            LiteMonitor.SettingsHelper.GlobalBlockSave = false;
        }

        public void Dispose()
        {
            try
            {
                LiteMonitor.SettingsHelper.GlobalBlockSave = false;

                if (_mainExisted) File.Copy(_savedMain, MainPath, true);
                else if (File.Exists(MainPath)) File.Delete(MainPath);
            }
            catch
            {
                // 现场恢复失败只影响本机测试目录，不吞掉断言结果。
            }
        }

        private static string WriteMain(string json)
        {
            File.WriteAllText(MainPath, json);
            return MainPath;
        }

        [Fact]
        public void LegacyTrueValueIsForcedToFalseOnEveryLoad()
        {
            // 现代格式配置（MonitorItems 带排序值）+ LiveGlassEnabled=true：
            // 这类用户**不会**走 RebuildAndMigrateSettings ⇒ 迁移必须仍在别处生效。
            WriteMain("""
                {
                  "Version": 9,
                  "LiveGlassEnabled": true,
                  "MonitorItems": [
                    { "Key": "CPU.Usage", "SortIndex": 1, "TaskbarSortIndex": 1 }
                  ]
                }
                """);

            LiteMonitor.Settings cfg = LiteMonitor.SettingsHelper.Load(forceReload: true);

            Assert.False(cfg.LiveGlassEnabled);
        }

        [Fact]
        public void LegacyFormatConfigWithTrueIsAlsoForcedToFalse()
        {
            // 遗留格式（TaskbarSortIndex 全零）：两条路径都必须收敛到 false。
            WriteMain("""
                {
                  "Version": 9,
                  "LiveGlassEnabled": true,
                  "MonitorItems": [
                    { "Key": "CPU.Usage", "SortIndex": 3, "TaskbarSortIndex": 0 }
                  ]
                }
                """);

            LiteMonitor.Settings cfg = LiteMonitor.SettingsHelper.Load(forceReload: true);

            Assert.False(cfg.LiveGlassEnabled);
        }

        [Fact]
        public void FalseValueStaysFalseAndFieldSurvivesSave()
        {
            // 字段必须**保留**（schema 兼容）：迁移写 false 后，Save 回盘的 JSON 里
            // 该键仍存在且为 false —— 不是被删掉。
            WriteMain("""{ "Version": 9, "LiveGlassEnabled": false }""");

            LiteMonitor.Settings cfg = LiteMonitor.SettingsHelper.Load(forceReload: true);
            Assert.False(cfg.LiveGlassEnabled);

            cfg.Save();
            string json = File.ReadAllText(MainPath);
            using JsonDocument doc = JsonDocument.Parse(json);
            Assert.True(
                doc.RootElement.TryGetProperty("LiveGlassEnabled", out JsonElement value),
                "LiveGlassEnabled 键必须保留在 settings.json 中（schema 兼容）");
            Assert.Equal(JsonValueKind.False, value.ValueKind);
        }
    }
}
