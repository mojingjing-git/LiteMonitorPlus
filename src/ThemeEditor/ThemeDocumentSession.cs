using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>保存结果：分别报告验证、写盘与冲突（重做计划 §5.2）。</summary>
    public sealed record ThemeSaveResult
    {
        public bool Success { get; init; }
        public bool ExternalConflict { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
        public string? WrittenSha256 { get; init; }

        public static ThemeSaveResult Ok(string sha256, IReadOnlyList<string> diagnostics) =>
            new() { Success = true, WrittenSha256 = sha256, Diagnostics = diagnostics };

        public static ThemeSaveResult Fail(string error, bool externalConflict = false) =>
            new() { Success = false, Error = error, ExternalConflict = externalConflict };
    }

    /// <summary>一次字段编辑：JSON 补丁值 + 对草稿对象的就地应用。</summary>
    /// <param name="JsonPath">主题 JSON 路径（点分、规范大小写，如 <c>color.background</c>）。</param>
    /// <param name="JsonValue">写入值；null 表示删除该键（按描述器"恢复缺省"语义）。</param>
    /// <param name="ApplyDraft">把值应用到草稿 Theme 的委托（属性目录提供）；null = 仅补丁不落草稿。</param>
    public sealed record ThemeFieldEdit(string JsonPath, JsonNode? JsonValue, Action<Theme>? ApplyDraft);

    /// <summary>
    /// 独立编辑会话（重做计划 §5.1）：只持有配置，不持有主窗口 Theme、Font、GPU 或采集器。
    ///
    /// ★ 补丁式保存（"缺省不被物化"）：
    ///   会话保留**源 JSON 字节**与**覆盖层**（路径 → 值/删除）两张信息：
    ///   - 打开未编辑时保存，写回与源文件逐字节相同的内容；
    ///   - 编辑后保存 = 源 JSON 上就地替换被编辑的键（大小写不敏感定位、保留原键名大小写），
    ///     未触碰的字段（含全部未知扩展键）原样保留，**不**全量序列化带默认值的 Theme；
    ///   - "恢复缺省"= 覆盖层删除该键，写回后该键从 JSON 消失，由模型缺省接管。
    ///   最终载荷重新通过现有解析与校验，再交给 <see cref="ThemeFileService"/> 的共用原子写入核心。
    /// </summary>
    public sealed class ThemeDocumentSession
    {

        private readonly ThemeDocumentHistory _history = new();
        private byte[] _sourceBytes;
        private JsonObject _sourceJson;
        private readonly Dictionary<string, JsonNode?> _overlay = new(StringComparer.Ordinal);

        /// <summary>主题名（同目录唯一文档身份）。</summary>
        public string ThemeName { get; private set; }

        /// <summary>源文件完整路径。</summary>
        public string SourcePath { get; private set; }

        /// <summary>最近一次加载／保存的文件 SHA-256（十六进制小写）。</summary>
        public string SourceSha256 { get; private set; }

        /// <summary>草稿配置（预览与检查器直接读取；运行中主题不受影响）。</summary>
        public Theme Draft { get; private set; }

        /// <summary>文档修订号：每次提交编辑 +1；保存基线另记。</summary>
        public long Revision { get; private set; } = 1;

        /// <summary>已保存基线对应的修订号。</summary>
        public long SavedRevision { get; private set; } = 1;

        /// <summary>是否存在未保存修改。</summary>
        public bool IsDirty => Revision != SavedRevision;

        /// <summary>撤销／重做状态。</summary>
        public int UndoCount => _history.UndoCount;
        public int RedoCount => _history.RedoCount;

        /// <summary>恢复副本标记：源文件摘要冲突时打开的副本，保存前必须另存为新名。</summary>
        public bool IsRecoveredCopy { get; internal set; }

        private ThemeDocumentSession(string themeName, string sourcePath, byte[] sourceBytes)
        {
            ThemeName = themeName;
            SourcePath = sourcePath;
            _sourceBytes = sourceBytes;
            SourceSha256 = Sha256Of(sourceBytes);
            _sourceJson = ParseRoot(sourceBytes);
            Draft = ParseDraft(sourceBytes);
        }

        // ------------------------------------------------------------
        // 打开
        // ------------------------------------------------------------

        /// <summary>从主题目录打开既有主题。</summary>
        public static ThemeDocumentSession Open(string themeName)
        {
            ThemeDocument document = ThemeDocumentStore.Read(themeName);
            return new ThemeDocumentSession(document.ThemeName, document.SourcePath, document.SourceBytes);
        }

        /// <summary>从字节打开（测试与恢复路径）。</summary>
        internal static ThemeDocumentSession FromSourceBytes(string themeName, string sourcePath, byte[] sourceBytes)
        {
            return new ThemeDocumentSession(themeName, sourcePath, sourceBytes);
        }

        private static JsonObject ParseRoot(byte[] bytes)
        {
            JsonNode? root = JsonNode.Parse(
                bytes,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true });
            return root as JsonObject
                ?? throw new InvalidDataException("Theme JSON root must be an object.");
        }

        private static Theme ParseDraft(byte[] bytes)
        {
            string json = System.Text.Encoding.UTF8.GetString(bytes);
            Theme? theme = JsonSerializer.Deserialize<Theme>(json, ThemeManager.CreateSerializerOptions())
                ?? throw new InvalidDataException("Theme JSON parse failed.");
            ThemeManager.PruneExtensionDataConflicts(theme);
            theme.BuildFonts();
            return theme;
        }

        // ------------------------------------------------------------
        // 编辑（含撤销／重做）
        // ------------------------------------------------------------

        /// <summary>
        /// 应用一次字段编辑：写覆盖层、改草稿、提交事务。
        /// 同一 <paramref name="coalesceKey"/> 在合并窗口内的连续调用并入同一可撤销事务。
        /// </summary>
        /// <param name="coalesceKey">合并键（通常是 <paramref name="edit"/>.JsonPath）。</param>
        public void ApplyEdit(ThemeFieldEdit edit, string? coalesceKey = null)
        {
            ArgumentNullException.ThrowIfNull(edit);
            string key = coalesceKey ?? edit.JsonPath;

            ThemeEditTransaction txn = _history.BeginOrMerge(key, Draft);
            JsonNode? overlayBefore = GetEffectiveValue(edit.JsonPath);

            edit.ApplyDraft?.Invoke(Draft);

            _overlay[edit.JsonPath] = edit.JsonValue is null ? null : edit.JsonValue.DeepClone();
            _history.RecordEdit(txn, Draft, new ThemeOverlayChange(edit.JsonPath, overlayBefore, edit.JsonValue?.DeepClone()));
            Revision++;
        }

        /// <summary>撤销最近一个事务；无可撤销时返回 false。</summary>
        public bool Undo()
        {
            ThemeEditTransaction? txn = _history.PopUndo();
            if (txn is null) return false;

            AppearanceThemeConfig.RestoreConfig(Draft, AppearanceThemeConfig.CloneConfig(txn.BeforeSnapshot));
            ApplyOverlayChanges(txn.Changes, useAfter: false);
            Revision++;
            return true;
        }

        /// <summary>重做最近被撤销的事务；无可重做时返回 false。</summary>
        public bool Redo()
        {
            ThemeEditTransaction? txn = _history.PopRedo();
            if (txn is null) return false;

            AppearanceThemeConfig.RestoreConfig(Draft, AppearanceThemeConfig.CloneConfig(txn.AfterSnapshot));
            ApplyOverlayChanges(txn.Changes, useAfter: true);
            Revision++;
            return true;
        }

        private void ApplyOverlayChanges(IReadOnlyList<ThemeOverlayChange> changes, bool useAfter)
        {
            foreach (ThemeOverlayChange change in changes)
            {
                JsonNode? value = useAfter ? change.After : change.Before;
                if (value is null && useAfter == false)
                {
                    // Before 为 null = 该路径此前无覆盖 → 移除覆盖操作本身
                    _overlay.Remove(change.Path);
                    continue;
                }

                _overlay[change.Path] = value?.DeepClone();
            }
        }

        /// <summary>读取某路径当前生效值：覆盖层优先，否则查源 JSON；返回 null 表示无键。</summary>
        public JsonNode? GetEffectiveValue(string jsonPath)
        {
            if (_overlay.TryGetValue(jsonPath, out JsonNode? overlayValue))
            {
                return overlayValue?.DeepClone();
            }

            return GetSourceValue(_sourceJson, jsonPath)?.DeepClone();
        }

        /// <summary>某路径是否存在显式覆盖（含编辑中的删除标记）。</summary>
        public bool HasOverride(string jsonPath) => _overlay.ContainsKey(jsonPath);

        private static JsonNode? GetSourceValue(JsonObject root, string jsonPath)
        {
            string[] segments = jsonPath.Split('.');
            JsonNode? current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (current is not JsonObject obj || !TryGetKey(obj, segments[i], out _, out current))
                {
                    return null;
                }
            }

            if (current is not JsonObject leaf ||
                !TryGetKey(leaf, segments[^1], out string? actualKey, out JsonNode? value))
            {
                return null;
            }

            return value;
        }

        /// <summary>大小写不敏感地在对象中定位键（现有读取口径），返回实际键名。</summary>
        private static bool TryGetKey(JsonObject obj, string name, out string? actualKey, out JsonNode? value)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in obj)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    actualKey = pair.Key;
                    value = pair.Value;
                    return true;
                }
            }

            actualKey = null;
            value = null;
            return false;
        }

        // ------------------------------------------------------------
        // 保存
        // ------------------------------------------------------------

        /// <summary>源文件是否已被外部修改（摘要不一致）。文件被外部删除不算冲突。</summary>
        public bool HasExternalConflict()
        {
            if (!File.Exists(SourcePath)) return false;
            return !string.Equals(Sha256Of(File.ReadAllBytes(SourcePath)), SourceSha256, StringComparison.Ordinal);
        }

        /// <summary>构造最终保存载荷：无覆盖时与源字节一致；有覆盖时补丁写回。</summary>
        internal (byte[] Payload, IReadOnlyList<string> Diagnostics) BuildPayload()
        {
            if (_overlay.Count == 0)
            {
                return (_sourceBytes, Array.Empty<string>());
            }

            JsonObject root = (JsonObject)_sourceJson.DeepClone();
            foreach (KeyValuePair<string, JsonNode?> op in _overlay)
            {
                ApplyOverlayOp(root, op.Key, op.Value);
            }

            string json = root.ToJsonString(ThemeManager.CreateSerializerOptions());
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(json);
            return (payload, Array.Empty<string>());
        }

        private static void ApplyOverlayOp(JsonObject root, string path, JsonNode? value)
        {
            string[] segments = path.Split('.');
            JsonObject current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (TryGetKey(current, segments[i], out string? existingKey, out JsonNode? existing))
                {
                    if (existing is JsonObject child)
                    {
                        current = child;
                        continue;
                    }

                    // 路径中段被非对象占据：以规范名重建对象（与解析器口径一致的新键写入）
                    current[existingKey] = new JsonObject();
                    current = (JsonObject)current[existingKey]!;
                    continue;
                }

                var created = new JsonObject();
                current[segments[i]] = created;
                current = created;
            }

            string leaf = segments[^1];
            if (TryGetKey(current, leaf, out string? leafKey, out _))
            {
                if (value is null)
                {
                    current.Remove(leafKey);
                }
                else
                {
                    current[leafKey] = value.DeepClone();
                }

                return;
            }

            if (value is null)
            {
                // 删除一个本就不存在的键：无操作
                return;
            }

            current[leaf] = value.DeepClone();
        }

        /// <summary>
        /// 保存当前修订：载荷验证 → 原子写入 → 更新保存基线。
        /// 失败保留原文件与草稿；写盘前不标"已保存"。
        /// </summary>
        /// <param name="overwriteExternalChanges">用户在冲突对话框里明确选择覆盖时为 true。</param>
        public ThemeSaveResult Save(bool overwriteExternalChanges = false)
        {
            _history.CloseOpenTransaction();

            if (IsRecoveredCopy)
            {
                return ThemeSaveResult.Fail("恢复副本必须先另存为新主题。");
            }

            if (!overwriteExternalChanges && HasExternalConflict())
            {
                return ThemeSaveResult.Fail("源文件已被外部修改。", externalConflict: true);
            }

            (byte[] payload, IReadOnlyList<string> diagnostics) = BuildPayload();

            try
            {
                _ = ParseDraft(payload);
            }
            catch (Exception ex)
            {
                return ThemeSaveResult.Fail("配置验证失败：" + ex.Message);
            }

            if (Draft.Rendering is not null)
            {
                IReadOnlyList<string> errors = Draft.Rendering.Validate(out IReadOnlyList<string> renderDiag);
                if (errors.Count > 0)
                {
                    return ThemeSaveResult.Fail("配置验证失败：" + string.Join(" ", errors));
                }

                if (renderDiag.Count > 0)
                {
                    diagnostics = renderDiag;
                }
            }

            try
            {
                ThemeDocumentStore.WriteThemeJson(ThemeName, System.Text.Encoding.UTF8.GetString(payload));
            }
            catch (Exception ex)
            {
                return ThemeSaveResult.Fail("写入失败：" + ex.Message);
            }

            // 写盘成功后更新基线；保存期间继续的编辑只体现为 Revision 继续前进
            _sourceBytes = payload;
            _sourceJson = ParseRoot(payload);
            SourceSha256 = Sha256Of(payload);
            SavedRevision = Revision;
            ThemeDocumentStore.DeleteRecovery(ThemeName);
            return ThemeSaveResult.Ok(SourceSha256, diagnostics);
        }

        /// <summary>另存为：新路径写入成功后才切换文档身份；失败保留原身份。</summary>
        public ThemeSaveResult SaveAs(string newThemeName)
        {
            try
            {
                ThemeFileService.ValidateThemeName(newThemeName);
            }
            catch (ArgumentException ex)
            {
                return ThemeSaveResult.Fail("主题名无效：" + ex.Message);
            }

            if (ThemeDocumentStore.Exists(newThemeName))
            {
                return ThemeSaveResult.Fail("同名主题已存在。");
            }

            _history.CloseOpenTransaction();
            (byte[] payload, IReadOnlyList<string> diagnostics) = BuildPayload();

            try
            {
                _ = ParseDraft(payload);
            }
            catch (Exception ex)
            {
                return ThemeSaveResult.Fail("配置验证失败：" + ex.Message);
            }

            try
            {
                ThemeDocumentStore.WriteThemeJson(newThemeName, System.Text.Encoding.UTF8.GetString(payload));
            }
            catch (Exception ex)
            {
                return ThemeSaveResult.Fail("写入失败：" + ex.Message);
            }

            ThemeName = newThemeName;
            SourcePath = ThemeDocumentStore.PathOf(newThemeName);
            IsRecoveredCopy = false;
            _sourceBytes = payload;
            _sourceJson = ParseRoot(payload);
            SourceSha256 = Sha256Of(payload);
            SavedRevision = Revision;
            ThemeDocumentStore.DeleteRecovery(ThemeName);
            return ThemeSaveResult.Ok(SourceSha256, diagnostics);
        }

        // ------------------------------------------------------------
        // 恢复草稿
        // ------------------------------------------------------------

        /// <summary>当前会话的恢复快照（空闲时由窗体持久化）。</summary>
        public ThemeDraftSnapshot CaptureSnapshot()
        {
            _history.CloseOpenTransaction();
            return new ThemeDraftSnapshot
            {
                ThemeName = ThemeName,
                SourcePath = SourcePath,
                SourceSha256 = SourceSha256,
                Revision = Revision,
                SavedRevision = SavedRevision,
                IsRecoveredCopy = IsRecoveredCopy,
                Overlay = new List<ThemeOverlayChange>(
                    _overlay.Select(pair => new ThemeOverlayChange(pair.Key, null, pair.Value?.DeepClone()))),
                DraftConfig = AppearanceThemeConfig.CloneConfig(Draft)
            };
        }

        /// <summary>
        /// 从恢复快照重建会话。源文件摘要一致 → 干净恢复；
        /// 摘要冲突或源文件缺失 → 打开**恢复副本**（草稿配置成为新源，不自动覆盖任何文件）。
        /// </summary>
        public static (ThemeDocumentSession Session, bool ConflictCopied) Restore(ThemeDraftSnapshot snapshot)
        {
            byte[] source;
            string path;
            try
            {
                path = ThemeDocumentStore.PathOf(snapshot.ThemeName);
                source = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            }
            catch
            {
                source = Array.Empty<byte>();
                path = ThemeDocumentStore.PathOf(snapshot.ThemeName);
            }

            bool clean = source.Length > 0 &&
                string.Equals(Sha256Of(source), snapshot.SourceSha256, StringComparison.Ordinal);

            if (clean)
            {
                ThemeDocumentSession session = new(snapshot.ThemeName, path, source)
                {
                    Revision = snapshot.Revision,
                    SavedRevision = snapshot.SavedRevision,
                    IsRecoveredCopy = snapshot.IsRecoveredCopy
                };
                foreach (ThemeOverlayChange change in snapshot.Overlay)
                {
                    session._overlay[change.Path] = change.After?.DeepClone();
                }

                AppearanceThemeConfig.RestoreConfig(session.Draft, snapshot.DraftConfig);
                session.Draft.BuildFonts();
                return (session, ConflictCopied: false);
            }

            // 冲突副本：以快照里的草稿配置为新源（不触碰磁盘上的任何主题文件）
            byte[] copyBytes = System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(snapshot.DraftConfig, ThemeManager.CreateSerializerOptions()));
            ThemeDocumentSession copy = new(snapshot.ThemeName, path, copyBytes)
            {
                Revision = snapshot.Revision,
                SavedRevision = 0,
                IsRecoveredCopy = true
            };
            return (copy, ConflictCopied: true);
        }

        internal static string Sha256Of(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
    }
}
