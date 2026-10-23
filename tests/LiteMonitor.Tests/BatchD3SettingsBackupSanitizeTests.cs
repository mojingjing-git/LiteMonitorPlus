using System;
using System.IO;
using System.Text.Json;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P2 批次 D · LM-085 护栏：配置**备份**里不得出现**明文凭据**。
    ///
    /// <para><b>缺陷（修复前）：</b></para>
    /// <list type="bullet">
    /// <item><c>SettingsHelper.AtomicWrite</c> 把**旧主文件原样</c>复制成备份；</item>
    /// <item>而旧主文件可能是**加密前**的形态（插件凭据 / Web 口令都是明文）；</item>
    /// <item><c>SanitizeBackupFile</c> <b>只</b>删 <c>WebServerPassword</c>
    ///       ⇒ 插件凭据的明文（更早版本）留在 <c>.bak</c> 里
    ///       ⇒ 备份的泄露面比主文件还大。</item>
    /// </list>
    ///
    /// <para><b>本护栏守四件事：</b></para>
    /// <list type="number">
    /// <item>写盘后备份里**不得出现**明文凭据；</item>
    /// <item>备份里的凭据必须是**可解密的密文**（不是被删掉 —— 备份仍要能回滚）；</item>
    /// <item>旧配置里的明文 Web 口令也不得留在备份里；</item>
    /// <item>备份**仍然是"上一份有效内容"**（不许用"删掉备份"糊弄）。</item>
    /// </list>
    ///
    /// <para>★ 本类会真的读写 <c>settings.json</c> / <c>settings.json.bak</c>
    ///   ⇒ 挂到 <c>cfgfile</c> 集合串行执行，并在每个用例前后保存/恢复现场。</para>
    /// </summary>
    [Collection("cfgfile")]
    public class BatchD3SettingsBackupSanitizeTests : IDisposable
    {
        private static readonly string MainPath = LiteMonitor.SettingsHelper.FilePath;
        private static readonly string BackupPath = Path.Combine(
            Path.GetDirectoryName(LiteMonitor.SettingsHelper.FilePath) ?? ".", "settings.json.bak");

        private readonly string _sandbox;
        private readonly bool _mainExisted;
        private readonly bool _backupExisted;
        private readonly string _savedMain;
        private readonly string _savedBackup;

        private const string Secret = "LM085-PLAINTEXT-SECRET-9f3a";

        public BatchD3SettingsBackupSanitizeTests()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), "lm-lm085-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sandbox);

            _mainExisted = File.Exists(MainPath);
            _backupExisted = File.Exists(BackupPath);
            _savedMain = Path.Combine(_sandbox, "main.json");
            _savedBackup = Path.Combine(_sandbox, "backup.json");
            if (_mainExisted) File.Copy(MainPath, _savedMain, true);
            if (_backupExisted) File.Copy(BackupPath, _savedBackup, true);

            LiteMonitor.SettingsHelper.GlobalBlockSave = false;
        }

        public void Dispose()
        {
            try
            {
                LiteMonitor.SettingsHelper.GlobalBlockSave = false;

                if (_mainExisted) File.Copy(_savedMain, MainPath, true);
                else if (File.Exists(MainPath)) File.Delete(MainPath);

                if (_backupExisted) File.Copy(_savedBackup, BackupPath, true);
                else if (File.Exists(BackupPath)) File.Delete(BackupPath);

                LiteMonitor.Settings.Load(forceReload: true);
            }
            catch { /* 还原失败不应掩盖用例本身的失败 */ }
            finally
            {
                try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, true); } catch { }
            }
        }

        // ================================================================
        //  ① 写盘后备份里不得出现明文凭据
        // ================================================================

        /// <summary>
        /// ★ 盯的缺陷：<c>AtomicWrite</c> 把旧主文件**原样**复制成备份。
        /// ★ 现场：造一份"加密前"的旧配置（插件凭据是明文），然后正常保存一次。
        /// </summary>
        [Fact]
        public void 写盘后备份里不得出现明文凭据()
        {
            // 旧配置：插件凭据仍是**明文**（加密前的真实形态）
            File.WriteAllText(MainPath, LegacySettingsJson(Secret));

            LiteMonitor.Settings cfg = LiteMonitor.Settings.Load(forceReload: true);
            Assert.Equal(Secret, cfg.PluginInstances[0].InputValues["api_key"]);

            cfg.Save();

            Assert.True(File.Exists(BackupPath), "前置条件不成立：本次落盘应当刷新出备份文件。");

            string backup = File.ReadAllText(BackupPath);
            string main = File.ReadAllText(MainPath);

            // ★ 备份里不得有明文凭据
            Assert.DoesNotContain(Secret, backup, StringComparison.Ordinal);
            // ★ 主文件里同样不得有（这是既有契约，顺带守住）
            Assert.DoesNotContain(Secret, main, StringComparison.Ordinal);
        }

        // ================================================================
        //  ② 备份里的凭据必须是"可解密的密文"，而不是被删掉
        // ================================================================

        /// <summary>
        /// ★ 盯的"假修"：把凭据从备份里**删掉**也算"备份里没有明文"，
        ///   但那样备份就不再是有效的回滚材料（用户回滚后插件凭据全没了）。
        /// ★ 这里断言备份里的值是 DPAPI 密文，且**能解回原文**。
        /// </summary>
        [Fact]
        public void 备份里的凭据必须是可解密的密文()
        {
            File.WriteAllText(MainPath, LegacySettingsJson(Secret));

            LiteMonitor.Settings cfg = LiteMonitor.Settings.Load(forceReload: true);
            cfg.Save();

            Assert.True(File.Exists(BackupPath), "前置条件不成立：本次落盘应当刷新出备份文件。");

            string stored = ReadBackupApiKey();
            Assert.NotNull(stored);
            Assert.True(ProtectedSecret.IsProtected(stored!),
                "备份里的凭据应当是 DPAPI 密文（实际值：" + stored + "）");
            Assert.True(ProtectedSecret.TryUnprotect(stored!, out string plain, out string err),
                "备份里的密文必须能解回原文（否则备份不是有效的回滚材料）：" + err);
            Assert.Equal(Secret, plain);
        }

        // ================================================================
        //  ③ 旧配置里的明文 Web 口令也不得留在备份里
        // ================================================================

        /// <summary>
        /// ★ 盯的缺陷：旧主文件里的明文 Web 口令被原样复制进备份。
        /// ★ 备份必须与主文件**同级**：主文件里口令恒为空串，备份里也必须如此。
        /// </summary>
        [Fact]
        public void 旧配置里的明文口令不得留在备份里()
        {
            const string plainPwd = "LM085-legacy-plain-password";
            File.WriteAllText(MainPath,
                "{\"WebServerEnabled\":false,\"WebServerPort\":5000,\"WebServerPassword\":\"" + plainPwd + "\"}");

            LiteMonitor.Settings cfg = LiteMonitor.Settings.Load(forceReload: true);
            Assert.Equal(plainPwd, cfg.WebServerPassword);

            cfg.Save();

            Assert.True(File.Exists(BackupPath), "前置条件不成立：本次落盘应当刷新出备份文件。");
            Assert.DoesNotContain(plainPwd, File.ReadAllText(BackupPath), StringComparison.Ordinal);
            Assert.DoesNotContain(plainPwd, File.ReadAllText(MainPath), StringComparison.Ordinal);
        }

        // ================================================================
        //  ④ 备份仍然是"上一份有效内容"（不许用"删掉备份"糊弄）
        // ================================================================

        /// <summary>
        /// ★ 盯的"假修"：直接把备份删掉（或写成空文件）也能让"备份里没有明文"成立，
        ///   但备份的语义（"上一份有效内容"、主文件损坏时的回落材料）就没了。
        /// ★ 这里断言备份里**仍然有上一版的有效内容**。
        /// </summary>
        [Fact]
        public void 备份仍然是上一份有效内容()
        {
            string marker = "LM085-marker-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(MainPath,
                "{\"PreferredDisk\":\"" + marker + "\",\"PluginInstances\":[]}");

            LiteMonitor.Settings cfg = LiteMonitor.Settings.Load(forceReload: true);
            cfg.PreferredDisk = marker + "-v2";
            cfg.Save();

            Assert.True(File.Exists(BackupPath), "备份文件必须存在（不许用删掉备份代替脱敏）。");

            string backup = File.ReadAllText(BackupPath);
            Assert.Contains(marker, backup, StringComparison.Ordinal);
            Assert.DoesNotContain(marker + "-v2", backup, StringComparison.Ordinal);
            Assert.Contains(marker + "-v2", File.ReadAllText(MainPath), StringComparison.Ordinal);
        }

        // ================================================================
        //  辅助
        // ================================================================

        /// <summary>造一份"加密前"的旧配置：插件凭据是明文。</summary>
        private static string LegacySettingsJson(string plainSecret)
            => "{\"PreferredDisk\":\"LM085\",\"PluginInstances\":[{" +
               "\"Id\":\"lm085_inst\",\"TemplateId\":\"lm085_tpl\",\"Enabled\":false," +
               "\"InputValues\":{\"api_key\":\"" + plainSecret + "\",\"city\":\"Beijing\"}," +
               "\"Targets\":[{\"name\":\"target-1\",\"token\":\"" + plainSecret + "\"}]}]}";

        /// <summary>从备份里读出插件实例的 api_key。</summary>
        private static string? ReadBackupApiKey()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(BackupPath));
            if (!doc.RootElement.TryGetProperty("PluginInstances", out var instances)) return null;
            if (instances.ValueKind != JsonValueKind.Array) return null;

            foreach (var instance in instances.EnumerateArray())
            {
                if (!instance.TryGetProperty("InputValues", out var inputs)) continue;
                if (inputs.ValueKind != JsonValueKind.Object) continue;
                if (!inputs.TryGetProperty("api_key", out var key)) continue;
                return key.GetString();
            }

            return null;
        }
    }
}
