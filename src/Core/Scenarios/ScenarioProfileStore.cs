using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LiteMonitor.src.Core.Scenarios
{
    /// <summary>
    /// 版本化 profiles.json 库。写入使用同目录临时文件、回读验证、备份和替换；
    /// 库读写不与采样线程共享锁，调用方应在 UI/后台配置线程使用。
    /// </summary>
    public sealed class ScenarioProfileStore
    {
        private static readonly object _gate = new();

        public string FilePath { get; }
        public string BackupPath => FilePath + ".bak";
        public string? LastLoadWarning { get; private set; }
        public string? LastQuarantinePath { get; private set; }
        /// <summary>
        /// 主库/备份包含未知高版本时，库进入只读模式：保留原文件，不隔离、不覆盖。
        /// </summary>
        public bool IsReadOnly { get; private set; }

        public ScenarioProfileStore(string? filePath = null)
        {
            FilePath = string.IsNullOrWhiteSpace(filePath)
                ? Path.Combine(Path.GetDirectoryName(SettingsHelper.FilePath) ?? AppContext.BaseDirectory, "profiles.json")
                : Path.GetFullPath(filePath);
        }

        public IReadOnlyList<ScenarioProfile> LoadAll()
        {
            lock (_gate)
            {
                LastLoadWarning = null;
                LastQuarantinePath = null;
                IsReadOnly = false;
                if (!File.Exists(FilePath))
                {
                    // R05：主库缺失时也必须继续检查备份；否则损坏隔离后的
                    // 第二次 LoadAll 会直接返回空集合，后续 Save 从空库覆盖。
                    if (File.Exists(BackupPath))
                    {
                        if (TryRead(BackupPath, out var missingBackup, out var missingBackupError, out bool missingUnsupported))
                        {
                            LastLoadWarning = "场景库主文件不存在；已读取备份。";
                            TryRestoreBackup();
                            return missingBackup;
                        }

                        if (missingUnsupported)
                        {
                            IsReadOnly = true;
                            LastLoadWarning = "场景库备份包含未知高版本；已保留原文件并进入只读模式。";
                            return Array.Empty<ScenarioProfile>();
                        }

                        LastLoadWarning = $"场景库主文件不存在；备份也不可用：{missingBackupError}";
                    }
                    return Array.Empty<ScenarioProfile>();
                }

                if (TryRead(FilePath, out var profiles, out var error, out bool unsupportedVersion))
                    return profiles;

                if (unsupportedVersion)
                {
                    // 未知高版本不是损坏：不隔离、不覆盖，保留文件等待新版本读取。
                    IsReadOnly = true;
                    LastLoadWarning = error + "；已保留原文件并进入只读模式。";
                    return Array.Empty<ScenarioProfile>();
                }

                LastLoadWarning = error;
                Quarantine(FilePath);
                if (File.Exists(BackupPath))
                {
                    if (TryRead(BackupPath, out var backup, out var backupError, out bool backupUnsupported))
                    {
                        LastLoadWarning = $"{error}；已回退到备份。";
                        TryRestoreBackup();
                        return backup;
                    }

                    if (backupUnsupported)
                    {
                        IsReadOnly = true;
                        LastLoadWarning = "场景库备份包含未知高版本；已保留原文件并进入只读模式。";
                        return Array.Empty<ScenarioProfile>();
                    }

                    LastLoadWarning += $"；备份也不可用：{backupError}";
                }

                return Array.Empty<ScenarioProfile>();
            }
        }

        public ScenarioProfile? Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return LoadAll().FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal));
        }

        /// <summary>
        /// 新建传 expectedRevision=0；更新必须传当前 DefinitionRevision。
        /// 冲突不会写入文件。
        /// </summary>
        public ScenarioStoreResult Save(ScenarioProfile profile, long expectedRevision)
        {
            if (profile == null) return ScenarioStoreResult.Fail("invalid-profile", "场景不能为空。");
            var nameError = ValidateName(profile.Name);
            if (nameError != null) return ScenarioStoreResult.Fail("invalid-name", nameError);
            if (profile.SchemaVersion != ScenarioFieldCatalog.CurrentSchemaVersion)
                return ScenarioStoreResult.Fail("unsupported-schema", $"不支持场景版本 {profile.SchemaVersion}。");
            try
            {
                ScenarioProjection.Validate(profile.Settings ?? new ScenarioSettingsProjection());
            }
            catch (Exception ex)
            {
                return ScenarioStoreResult.Fail("invalid-settings", ex.Message);
            }

            lock (_gate)
            {
                var all = LoadAll().ToList();
                if (IsReadOnly)
                    return ScenarioStoreResult.Fail("read-only", LastLoadWarning ?? "场景库为只读。");
                var index = all.FindIndex(item => string.Equals(item.Id, profile.Id, StringComparison.Ordinal));
                if (index >= 0)
                {
                    if (expectedRevision != all[index].DefinitionRevision)
                        return ScenarioStoreResult.Fail("revision-conflict", "场景已被其他窗口修改，请重新载入。");
                }
                else if (expectedRevision != 0)
                {
                    return ScenarioStoreResult.Fail("revision-conflict", "新建场景的预期修订必须为 0。");
                }

                var nextRevision = index >= 0 ? all[index].DefinitionRevision + 1 : 1;
                var candidate = profile.Clone();
                if (string.IsNullOrWhiteSpace(candidate.Id))
                    candidate.Id = Guid.NewGuid().ToString("N");
                var idError = ValidateId(candidate.Id);
                if (idError != null) return ScenarioStoreResult.Fail("invalid-id", idError);
                candidate.Name = candidate.Name.Trim();
                candidate.DefinitionRevision = nextRevision;
                candidate.SchemaVersion = ScenarioFieldCatalog.CurrentSchemaVersion;
                candidate.UpdatedAtUtc = DateTimeOffset.UtcNow;
                if (index < 0)
                {
                    if (candidate.CreatedAtUtc == default) candidate.CreatedAtUtc = candidate.UpdatedAtUtc;
                    all.Add(candidate);
                }
                else
                {
                    candidate.CreatedAtUtc = all[index].CreatedAtUtc;
                    all[index] = candidate;
                }

                if (all.Count > ScenarioFieldCatalog.MaxProfiles)
                    return ScenarioStoreResult.Fail("too-many-profiles", $"场景上限为 {ScenarioFieldCatalog.MaxProfiles} 个。");
                if (all.Any(item => !item.IsSupportedVersion))
                    return ScenarioStoreResult.Fail("unsupported-schema", "库中存在未知高版本场景，拒绝覆盖。");

                var json = JsonSerializer.Serialize(all, ScenarioProfileJson.Options);
                var bytes = Encoding.UTF8.GetByteCount(json);
                if (bytes > ScenarioFieldCatalog.MaxFileBytes)
                    return ScenarioStoreResult.Fail("too-large", "场景库超过 2 MiB 上限。");

                try
                {
                    AtomicWrite(json);
                }
                catch (Exception ex)
                {
                    return ScenarioStoreResult.Fail("io-error", ex.Message);
                }

                return ScenarioStoreResult.Ok(candidate.Clone());
            }
        }

        public ScenarioStoreResult Delete(string id, long expectedRevision)
        {
            if (string.IsNullOrWhiteSpace(id)) return ScenarioStoreResult.Fail("invalid-id", "缺少场景 Id。");
            lock (_gate)
            {
                var all = LoadAll().ToList();
                if (IsReadOnly)
                    return ScenarioStoreResult.Fail("read-only", LastLoadWarning ?? "场景库为只读。");
                var index = all.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
                if (index < 0) return ScenarioStoreResult.Fail("not-found", "场景不存在。");
                if (expectedRevision != all[index].DefinitionRevision)
                    return ScenarioStoreResult.Fail("revision-conflict", "场景已被其他窗口修改，请重新载入。");
                all.RemoveAt(index);
                var json = JsonSerializer.Serialize(all, ScenarioProfileJson.Options);
                if (Encoding.UTF8.GetByteCount(json) > ScenarioFieldCatalog.MaxFileBytes)
                    return ScenarioStoreResult.Fail("too-large", "场景库超过 2 MiB 上限。");
                try
                {
                    AtomicWrite(json);
                    return ScenarioStoreResult.Ok(null!);
                }
                catch (Exception ex)
                {
                    return ScenarioStoreResult.Fail("io-error", ex.Message);
                }
            }
        }

        private bool TryRead(
            string path,
            out IReadOnlyList<ScenarioProfile> profiles,
            out string error,
            out bool unsupportedVersion)
        {
            profiles = Array.Empty<ScenarioProfile>();
            error = "";
            unsupportedVersion = false;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > ScenarioFieldCatalog.MaxFileBytes)
                {
                    error = "场景库超过 2 MiB 上限。";
                    return false;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<List<ScenarioProfile>>(json, ScenarioProfileJson.Options);
                if (parsed == null)
                {
                    error = "场景库为空或格式无效。";
                    return false;
                }
                if (parsed.Count > ScenarioFieldCatalog.MaxProfiles)
                {
                    error = $"场景数量超过上限 {ScenarioFieldCatalog.MaxProfiles}。";
                    return false;
                }
                if (parsed.Any(item => item == null))
                {
                    error = "场景库包含空对象，未应用。";
                    return false;
                }
                if (parsed.Any(item => !item.IsSupportedVersion))
                {
                    unsupportedVersion = true;
                    error = "场景库包含未知高版本，未应用。";
                    return false;
                }
                if (parsed.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != parsed.Count)
                {
                    error = "场景库存在重复 Id。";
                    return false;
                }
                profiles = parsed;
                return true;
            }
            catch (Exception ex)
            {
                error = "场景库无法解析：" + ex.Message;
                return false;
            }
        }

        private void TryRestoreBackup()
        {
            try
            {
                if (!File.Exists(BackupPath)) return;
                string json = File.ReadAllText(BackupPath, Encoding.UTF8);
                AtomicWrite(json);
            }
            catch (Exception ex)
            {
                LastLoadWarning += $"；主库恢复失败：{ex.Message}";
            }
        }

        private void AtomicWrite(string json)
        {
            var dir = Path.GetDirectoryName(FilePath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, $"profiles.json.{Environment.ProcessId}.tmp");
            try
            {
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                var verify = JsonSerializer.Deserialize<List<ScenarioProfile>>(
                    File.ReadAllText(temp, Encoding.UTF8), ScenarioProfileJson.Options);
                if (verify == null || verify.Count > ScenarioFieldCatalog.MaxProfiles)
                    throw new InvalidDataException("场景库回读验证失败。");

                if (File.Exists(FilePath))
                    File.Copy(FilePath, BackupPath, true);
                File.Move(temp, FilePath, true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private void Quarantine(string path)
        {
            try
            {
                var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var target = $"{path}.corrupt-{suffix}";
                File.Move(path, target, true);
                LastQuarantinePath = target;
            }
            catch
            {
                // 隔离失败不覆盖原文件；后续保存仍会走临时文件+验证。
            }
        }

        private static string? ValidateName(string? name)
        {
            var trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0) return "场景名称不能为空。";
            if (trimmed.Length > 80) return "场景名称不能超过 80 个字符。";
            if (trimmed.Any(char.IsControl)) return "场景名称不能包含控制字符。";
            return null;
        }

        private static string? ValidateId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "场景 Id 不能为空。";
            if (id.Length > 64) return "场景 Id 过长。";
            if (id.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')))
                return "场景 Id 只能包含字母、数字、'-' 和 '_'。";
            return null;
        }
    }
}
