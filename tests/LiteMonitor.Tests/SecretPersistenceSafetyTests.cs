using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// S01：凭据持久化安全回归。
    /// ★ DPAPI 保护失败 ⇒ 整体保存失败（SettingsSaveOutcome.CredentialProtectionFailed），
    ///   主文件与备份字节不动、无 tmp 残留、绝不落明文；
    /// ★ 内存克隆（DeepClone / CloneForDiff，MemoryClone 用途转换器）0 次 Protect/Unprotect，
    ///   明文/密文逐值原样保留、是独立副本、保护失败不影响克隆；
    /// ★ 旧明文配置可读，下一次保存成功后迁移为密文；
    /// ★ 已有密文解密失败 ⇒ 保留密文 + 报错状态，不覆盖成明文。
    /// </summary>
    [Collection("cfgfile")]
    public class SecretPersistenceSafetyTests
    {
        private static readonly FieldInfo InstanceField =
            typeof(Settings).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);

        private const string PlainApiKey = "sk-plain-secret-value";
        private const string PlainTargetToken = "Bearer plain-target-token";

        [Fact]
        public void Save_PersistsSecretsAsCiphertext_PlaintextNeverOnDisk()
        {
            using var scope = new SecretScope();
            scope.AddInstanceWithSecrets();

            int protectBefore = ProtectedSecret.ProtectCallCount;
            Assert.True(scope.Config.TrySave());
            Assert.True(ProtectedSecret.ProtectCallCount - protectBefore >= 2, "持久化写入必须调用 Protect（两个凭据参数）。");

            string disk = File.ReadAllText(SettingsHelper.FilePath);
            Assert.DoesNotContain(PlainApiKey, disk);
            Assert.DoesNotContain(PlainTargetToken, disk);
            Assert.Contains(ProtectedSecret.Prefix, disk);

            Settings loaded = scope.ReloadFromDisk();
            var inst = Assert.Single(loaded.PluginInstances);
            Assert.Equal(PlainApiKey, inst.InputValues["api_key"]);
            Assert.Equal(PlainTargetToken, inst.Targets[0]["authorization"]);
            Assert.Equal("https://example.invalid", inst.Targets[0]["endpoint"]); // 非凭据参数原样
        }

        [Fact]
        public void Save_WhenProtectFails_FailsEntireSaveAndDiskUntouched()
        {
            using var scope = new SecretScope();
            scope.AddInstanceWithSecrets();
            Assert.True(scope.Config.TrySave());
            byte[] before = File.ReadAllBytes(SettingsHelper.FilePath);
            byte[] backupBefore = File.Exists(SettingsHelper.BackupProbePath)
                ? File.ReadAllBytes(SettingsHelper.BackupProbePath)
                : null;

            ProtectedSecret.ProtectHookForTests = _ => throw new CryptographicException("boom");
            SettingsSaveOutcome outcome = scope.Config.TrySaveWithOutcome();

            Assert.Equal(SettingsSaveOutcome.CredentialProtectionFailed, outcome);
            Assert.Equal(before, File.ReadAllBytes(SettingsHelper.FilePath));       // 主文件字节不动
            if (backupBefore != null)
                Assert.Equal(backupBefore, File.ReadAllBytes(SettingsHelper.BackupProbePath)); // 备份不动
            Assert.Empty(Directory.GetFiles(scope.Root, "*.tmp"));                  // 无临时文件残留

            string error = SettingsHelper.LastCredentialProtectionError ?? "";
            Assert.Contains("api_key", error);                       // 可定位到参数
            Assert.DoesNotContain(PlainApiKey, error);               // 绝不含凭据值
        }

        [Fact]
        public void Save_WhenProtectFails_NoFileIsCreatedAtAll()
        {
            using var scope = new SecretScope();
            scope.AddInstanceWithSecrets();
            ProtectedSecret.ProtectHookForTests = _ => throw new CryptographicException("boom");

            SettingsSaveOutcome outcome = scope.Config.TrySaveWithOutcome();

            Assert.Equal(SettingsSaveOutcome.CredentialProtectionFailed, outcome);
            Assert.False(File.Exists(SettingsHelper.FilePath));
            Assert.False(File.Exists(SettingsHelper.BackupProbePath));
            Assert.Empty(Directory.GetFiles(scope.Root, "*.tmp"));
        }

        [Fact]
        public void DeepClone_And_CloneForDiff_ZeroProtectOrUnprotectCalls()
        {
            using var scope = new SecretScope();
            scope.AddInstanceWithSecrets();
            // 混合形态：明文凭据 + 伪密文（MemoryClone 必须原样保留，且不因解密失败留痕）
            string fakeCipher = ProtectedSecret.Prefix + Convert.ToBase64String(new byte[] { 1, 2, 3 });
            scope.Config.PluginInstances[0].InputValues["legacy_token"] = fakeCipher;
            PluginCredentialState.TakeFailures(); // 清空遗留记录

            int protectBefore = ProtectedSecret.ProtectCallCount;
            int unprotectBefore = ProtectedSecret.UnprotectCallCount;

            Settings clone = scope.Config.DeepClone();
            Settings diff = scope.Config.CloneForDiff();

            Assert.Equal(protectBefore, ProtectedSecret.ProtectCallCount);       // 0 次保护调用
            Assert.Equal(unprotectBefore, ProtectedSecret.UnprotectCallCount);   // 0 次解密调用
            Assert.Empty(PluginCredentialState.TakeFailures());                  // 克隆无副作用

            foreach (Settings snapshot in new[] { clone, diff })
            {
                var inst = Assert.Single(snapshot.PluginInstances);
                Assert.Equal(PlainApiKey, inst.InputValues["api_key"]);          // 明文保留明文
                Assert.Equal(PlainTargetToken, inst.Targets[0]["authorization"]);
                Assert.Equal(fakeCipher, inst.InputValues["legacy_token"]);      // 密文保留密文
            }
        }

        [Fact]
        public void DeepClone_IsIndependentCopy()
        {
            using var scope = new SecretScope();
            scope.AddInstanceWithSecrets();

            Settings clone = scope.Config.DeepClone();
            clone.PluginInstances[0].InputValues["api_key"] = "changed-in-clone";
            clone.PluginInstances[0].Targets[0]["authorization"] = "changed-too";
            clone.PluginInstances[0].InputValues["extra"] = "new";

            Assert.Equal(PlainApiKey, scope.Config.PluginInstances[0].InputValues["api_key"]);
            Assert.Equal(PlainTargetToken, scope.Config.PluginInstances[0].Targets[0]["authorization"]);
            Assert.False(scope.Config.PluginInstances[0].InputValues.ContainsKey("extra"));
        }

        [Fact]
        public void DeepClone_WhenProtectHookFails_StillSucceeds()
        {
            using var scope = new SecretScope();
            scope.AddInstanceWithSecrets();
            ProtectedSecret.ProtectHookForTests = _ => throw new CryptographicException("boom");

            Settings clone = scope.Config.DeepClone();

            Assert.Equal(PlainApiKey, clone.PluginInstances[0].InputValues["api_key"]);
            Assert.Equal(PlainTargetToken, clone.PluginInstances[0].Targets[0]["authorization"]);
        }

        [Fact]
        public void Load_OldPlaintextConfig_ReadsAsIs_NextSaveMigratesToCiphertext()
        {
            using var scope = new SecretScope();
            File.WriteAllText(SettingsHelper.FilePath,
                "{\"PluginInstances\":[{\"Id\":\"p1\",\"InputValues\":{\"api_key\":\"" + PlainApiKey + "\"}}]}");

            Settings loaded = scope.ReloadFromDisk();
            Assert.Equal(PlainApiKey, loaded.PluginInstances[0].InputValues["api_key"]); // 旧明文可读

            Assert.True(loaded.TrySave()); // 下一次保护成功 ⇒ 迁移为密文
            string disk = File.ReadAllText(SettingsHelper.FilePath);
            Assert.DoesNotContain(PlainApiKey, disk);
            Assert.Contains(ProtectedSecret.Prefix, disk);
        }

        [Fact]
        public void Load_UnreadableCiphertext_KeepsCiphertextAndDisablesInstance()
        {
            using var scope = new SecretScope();
            string brokenCipher = ProtectedSecret.Prefix + "!!!not-valid-base64!!!";
            File.WriteAllText(SettingsHelper.FilePath,
                "{\"PluginInstances\":[{\"Id\":\"p1\",\"Enabled\":true,\"InputValues\":{\"api_key\":\"" + brokenCipher + "\"}}]}");

            Settings loaded = scope.ReloadFromDisk();

            Assert.Equal(brokenCipher, loaded.PluginInstances[0].InputValues["api_key"]); // 密文原样保留
            Assert.True(ProtectedSecret.IsProtected(loaded.PluginInstances[0].InputValues["api_key"]));
            Assert.False(loaded.PluginInstances[0].Enabled);                              // 明确报错状态：实例停用
        }

        [Fact]
        public void Save_WhenProtectFails_MarketRecordMutationRollsBack()
        {
            using var scope = new SecretScope();
            scope.AddInstanceWithSecrets(); // 凭据实例必须在场：Protect 失败才谈得上整体保存失败
            var record = new MarketPluginRecord
            {
                Id = "rec-1", File = "rec-1.json", Version = "1.0", Sha256 = new string('a', 64)
            };
            scope.Config.MarketPlugins.Add(record);
            Assert.True(scope.Config.TrySave());
            byte[] before = File.ReadAllBytes(SettingsHelper.FilePath);

            ProtectedSecret.ProtectHookForTests = _ => throw new CryptographicException("boom");
            var replacement = new MarketPluginRecord
            {
                Id = "rec-1", File = "rec-1.json", Version = "2.0", Sha256 = new string('b', 64)
            };

            Assert.False(SettingsHelper.TryChangeMarketRecord("rec-1", replacement)); // 保存失败 ⇒ 返回 false
            Assert.Equal("1.0", scope.Config.MarketPlugins.Single(r => r.Id == "rec-1").Version); // 内存回滚
            Assert.Equal(before, File.ReadAllBytes(SettingsHelper.FilePath));         // 磁盘不动
            Assert.Equal(PlainApiKey, scope.Config.PluginInstances[0].InputValues["api_key"]); // 凭据实例未受影响
        }

        /// <summary>沙箱：主/备/tmp 全部重定向到独立目录，恢复单例与全部测试接缝。</summary>
        private sealed class SecretScope : IDisposable
        {
            private readonly string? _savedPathOverride;
            private readonly object? _savedInstance;
            private readonly bool _savedBlock;
            private readonly Action? _savedBeforeSave;
            private readonly Action? _savedBeforeSerialize;
            private readonly Func<string, string>? _savedHook;
            private readonly string? _savedLastCredentialError;

            internal SecretScope()
            {
                Root = Path.Combine(AppContext.BaseDirectory, "secret-persist-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);

                _savedPathOverride = SettingsHelper.PathOverride;
                _savedInstance = InstanceField.GetValue(null);
                _savedBlock = SettingsHelper.GlobalBlockSave;
                _savedBeforeSave = SettingsHelper.BeforeSaveGateForTests;
                _savedBeforeSerialize = SettingsHelper.BeforeSettingsSerializeForTests;
                _savedHook = ProtectedSecret.ProtectHookForTests;
                _savedLastCredentialError = SettingsHelper.LastCredentialProtectionError;

                SettingsHelper.PathOverride = Root;
                SettingsHelper.GlobalBlockSave = false;
                SettingsHelper.BeforeSaveGateForTests = null;
                SettingsHelper.BeforeSettingsSerializeForTests = null;
                ProtectedSecret.ProtectHookForTests = null;
                SettingsHelper.LastCredentialProtectionError = null;
                foreach (string path in SettingsPaths()) File.Delete(path);

                Config = new Settings();
                InstanceField.SetValue(null, Config);
            }

            internal string Root { get; }
            internal Settings Config { get; }

            internal void AddInstanceWithSecrets()
            {
                Config.PluginInstances.Add(new PluginInstanceConfig
                {
                    Id = "inst-1",
                    Enabled = true,
                    InputValues = new Dictionary<string, string> { ["api_key"] = PlainApiKey },
                    Targets = new List<Dictionary<string, string>>
                    {
                        new Dictionary<string, string>
                        {
                            ["authorization"] = PlainTargetToken,
                            ["endpoint"] = "https://example.invalid"
                        }
                    }
                });
            }

            /// <summary>清空单例后从磁盘重新 Load（Load 会重设单例）。</summary>
            internal Settings ReloadFromDisk()
            {
                InstanceField.SetValue(null, null);
                return Settings.Load();
            }

            private static IEnumerable<string> SettingsPaths()
            {
                yield return SettingsHelper.FilePath;
                yield return SettingsHelper.BackupProbePath;
            }

            public void Dispose()
            {
                SettingsHelper.PathOverride = _savedPathOverride;
                InstanceField.SetValue(null, _savedInstance);
                SettingsHelper.GlobalBlockSave = _savedBlock;
                SettingsHelper.BeforeSaveGateForTests = _savedBeforeSave;
                SettingsHelper.BeforeSettingsSerializeForTests = _savedBeforeSerialize;
                ProtectedSecret.ProtectHookForTests = _savedHook;
                SettingsHelper.LastCredentialProtectionError = _savedLastCredentialError;
                try { Directory.Delete(Root, true); } catch { }
            }
        }
    }
}
