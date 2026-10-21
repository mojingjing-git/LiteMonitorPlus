using System;
using System.IO;
using System.Text.Json;
using LiteMonitor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 配置故障降级的回归用例（计划 W2）。
    ///
    /// 覆盖两条原本会**静默丢配置**的路径：
    ///
    /// 1. 主文件损坏、备份有效 —— 修复前保存时会把坏主文件复制进备份，
    ///    于是「上一次的好配置」被自己的坏副本顶掉，回滚能力等于不存在。
    /// 2. 主备同时被独占锁（典型场景：另一个实例或杀软正在读）——
    ///    修复前会当成新装，用户随后任何一次改设置都会用默认值覆盖真实配置。
    ///
    /// <c>SettingsHelper</c> 的路径固定在 <c>AppContext.BaseDirectory</c>，
    /// 因此挂到 "cfgfile" 集合串行执行，并在每个用例前后保存/恢复现场。
    /// </summary>
    [Collection("cfgfile")]
    public class SettingsRecoveryTests : IDisposable
    {
        private static readonly string MainPath = SettingsHelper.FilePath;
        private static readonly string BackupPath = Path.Combine(
            Path.GetDirectoryName(SettingsHelper.FilePath) ?? ".", "settings.json.bak");
        private static readonly string TempPath = Path.Combine(
            Path.GetDirectoryName(SettingsHelper.FilePath) ?? ".", "settings.json.tmp");

        private readonly string _sandbox;

        public SettingsRecoveryTests()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), "lmsettings-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sandbox);

            if (File.Exists(MainPath)) File.Copy(MainPath, Path.Combine(_sandbox, "main.json"), true);
            if (File.Exists(BackupPath)) File.Copy(BackupPath, Path.Combine(_sandbox, "backup.json"), true);

            // 上一个用例可能留下禁写状态，每个用例从干净状态开始
            SettingsHelper.GlobalBlockSave = false;
        }

        public void Dispose()
        {
            try
            {
                foreach (string f in Directory.GetFiles(
                    Path.GetDirectoryName(MainPath) ?? ".", "settings.json.corrupt-*"))
                {
                    File.Delete(f);
                }

                DeleteIfExists(MainPath);
                DeleteIfExists(BackupPath);
                DeleteIfExists(TempPath);

                string savedMain = Path.Combine(_sandbox, "main.json");
                string savedBackup = Path.Combine(_sandbox, "backup.json");
                if (File.Exists(savedMain)) File.Copy(savedMain, MainPath, true);
                if (File.Exists(savedBackup)) File.Copy(savedBackup, BackupPath, true);

                Directory.Delete(_sandbox, true);
            }
            catch
            {
                // 清理失败不应让用例判负
            }
            finally
            {
                SettingsHelper.GlobalBlockSave = false;
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private static string NewMarker() => "DISK-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        [Fact]
        public void 主文件损坏时从备份恢复_且保存后不把坏内容写进备份()
        {
            string marker = NewMarker();

            // 备份：一份有效配置
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(new Settings { PreferredDisk = marker }));
            // 主文件：明确的坏 JSON
            File.WriteAllText(MainPath, "{ this is clearly not valid json ");

            Settings loaded = SettingsHelper.Load(forceReload: true);

            // 从备份恢复出用户数据
            Assert.Equal(marker, loaded.PreferredDisk);

            // 损坏的主文件已被改名留证，不再占着主文件位置
            Assert.False(File.Exists(MainPath), "损坏的主文件应被改名留证，而不是留在原位");
            Assert.NotEmpty(Directory.GetFiles(
                Path.GetDirectoryName(MainPath) ?? ".", "settings.json.corrupt-*"));

            // 损坏不是 IO 故障，不应禁止保存
            Assert.False(SettingsHelper.GlobalBlockSave);

            // 保存一份新配置
            loaded.PreferredDisk = marker + "-edited";
            loaded.Save();

            // 关键断言：备份里必须仍是那份**有效**配置，不能被坏主文件的残骸污染
            string backupText = File.ReadAllText(BackupPath);
            Assert.Contains(marker, backupText, StringComparison.Ordinal);
            Assert.DoesNotContain("clearly not valid json", backupText);

            // 主文件已被新内容接管，且仍可解析
            Assert.Contains(marker + "-edited", File.ReadAllText(MainPath), StringComparison.Ordinal);
        }

        [Fact]
        public void 主备同时被独占锁时按IO故障处理_不覆盖用户配置()
        {
            string marker = NewMarker();
            string original = JsonSerializer.Serialize(new Settings { PreferredDisk = marker });
            File.WriteAllText(MainPath, original);
            File.WriteAllText(BackupPath, original);

            using (new FileStream(MainPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (new FileStream(BackupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Settings loaded = SettingsHelper.Load(forceReload: true);

                // 读不到就只能是默认值 —— 但不能把这个默认值当成用户配置
                Assert.NotEqual(marker, loaded.PreferredDisk);
                Assert.True(SettingsHelper.GlobalBlockSave, "IO 故障后必须禁止本次会话自动落盘");

                // 用户在锁定期间改了设置：不得落盘
                loaded.PreferredDisk = "SHOULD-NOT-REACH-DISK";
                loaded.Save();
            }

            // 解锁后：磁盘上的用户配置必须原封不动
            Assert.Contains(marker, File.ReadAllText(MainPath), StringComparison.Ordinal);
            Assert.DoesNotContain("SHOULD-NOT-REACH-DISK", File.ReadAllText(MainPath));

            // 再次加载能读到真实配置，并解除禁写（否则一次瞬时故障会让本次会话永久无法保存）
            Settings reloaded = SettingsHelper.Load(forceReload: true);
            Assert.Equal(marker, reloaded.PreferredDisk);
            Assert.False(SettingsHelper.GlobalBlockSave, "读取成功后必须解除禁写");

            // 第二次保存：备份应该拿到「上一版有效内容」，而不是默认值
            reloaded.PreferredDisk = marker + "-v2";
            reloaded.Save();

            string backupText = File.ReadAllText(BackupPath);
            Assert.Contains(marker, backupText, StringComparison.Ordinal);
            Assert.Contains(marker + "-v2", File.ReadAllText(MainPath), StringComparison.Ordinal);
        }

        [Fact]
        public void 未经加载主文件已被外部写坏时_保存不得把坏内容复制进备份()
        {
            string marker = NewMarker();

            // 备份：有效配置
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(new Settings { PreferredDisk = marker }));
            // 主文件：本次运行期间被外部写坏（不经过 Load 的坏文件留证路径）
            File.WriteAllText(MainPath, "{ broken while the app was running ");

            // 直接保存，模拟此时用户改了一项设置触发落盘
            new Settings { PreferredDisk = "NEW-VALUE" }.Save();

            // 备份必须仍是那份有效内容：坏主文件不能被当成「上一版」复制进去
            string backupText = File.ReadAllText(BackupPath);
            Assert.Contains(marker, backupText, StringComparison.Ordinal);
            Assert.DoesNotContain("broken while the app was running", backupText);

            // 主文件被新内容接管
            Assert.Contains("NEW-VALUE", File.ReadAllText(MainPath), StringComparison.Ordinal);
        }

        [Fact]
        public void 重置配置设置的强制禁写_不被一次成功的读取撤销()
        {
            File.WriteAllText(MainPath, JsonSerializer.Serialize(new Settings { PreferredDisk = NewMarker() }));

            // 先回到干净状态：外部把 GlobalBlockSave 置假会同时清掉「本次禁写是谁置的」记录
            SettingsHelper.GlobalBlockSave = false;
            SettingsHelper.Load(forceReload: true);

            // 模拟 SettingsForm 的「重置配置」：删配置之后、进程退出之前强制禁写
            SettingsHelper.GlobalBlockSave = true;

            Settings loaded = SettingsHelper.Load(forceReload: true);

            // 读取本身必须成功
            Assert.False(string.IsNullOrEmpty(loaded.PreferredDisk));

            // 关键：这条强制禁写不是本类置的，读取成功不得把它撤销 ——
            // 否则重置流程在 Application.Restart() 之前会把刚删掉的配置用内存值写回来。
            Assert.True(SettingsHelper.GlobalBlockSave,
                "外部设置的强制禁写不能被 SettingsHelper.Load 撤销");
        }
    }
}
