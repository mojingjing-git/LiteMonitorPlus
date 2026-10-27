using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [S05] 阈值覆盖清理测试：工作室不再提供阈值编辑入口，会话级清理保证
    /// 旧版阈值编辑（含大小写变体/子树/删除标记/撤销重做/恢复快照）不会复活，
    /// 源文件已存阈值原样保留，源无 thresholds 块时不合成。
    /// </summary>
    [Collection("cfgfile")]
    public class ThemeThresholdsCleanupTests : IDisposable
    {
        private const string SourceJson = """
            {
              "name": "demo",
              "version": 3,
              "color": { "background": "#FF101010", "textTitle": "#FFE0E0E0" },
              "thresholds": { "load": { "warn": 80, "crit": 92 } }
            }
            """;

        private const string SourceJsonNoThresholds = """
            {
              "name": "demo",
              "version": 3,
              "color": { "background": "#FF101010" }
            }
            """;

        private readonly string _sandbox;
        private readonly string _themeDir;
        private readonly byte[] _sourceBytes;

        public ThemeThresholdsCleanupTests()
        {
            _sandbox = Path.Combine(AppContext.BaseDirectory, "s05-thresholds-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sandbox);
            _themeDir = Path.Combine(_sandbox, "themes");
            Directory.CreateDirectory(_themeDir);
            ThemeDocumentStore.SetThemeDirForTests(_themeDir);
            ThemeDocumentStore.SetRecoveryDirForTests(Path.Combine(_sandbox, "drafts"));

            _sourceBytes = System.Text.Encoding.UTF8.GetBytes(SourceJson);
        }

        public void Dispose()
        {
            ThemeDocumentStore.ResetRecoveryDir();
            ThemeDocumentStore.SetThemeDirForTests(null);
            try { Directory.Delete(_sandbox, recursive: true); } catch { /* 沙箱清理失败不影响断言 */ }
        }

        private static ThemeDocumentSession NewSession(byte[]? bytes = null, string sourceName = "demo")
        {
            string path = ThemeDocumentStore.PathOf(sourceName);
            return ThemeDocumentSession.FromSourceBytes(sourceName, path, bytes ?? System.Text.Encoding.UTF8.GetBytes(SourceJson));
        }

        private static void EditThreshold(ThemeDocumentSession session, double value) =>
            session.ApplyEdit(new ThemeFieldEdit(
                "thresholds.load.warn", JsonValue.Create(value),
                t => t.Thresholds.Load.Warn = value));

        private static void EditColor(ThemeDocumentSession session) =>
            session.ApplyEdit(new ThemeFieldEdit(
                "color.background", JsonValue.Create("#FF202020"),
                t => t.Color.Background = "#FF202020"));

        [Fact]
        public void Remove_CleansThresholdOverrides_AndRestoresSourceValues()
        {
            ThemeDocumentSession session = NewSession();
            EditThreshold(session, 70);
            Assert.Equal(70, session.Draft.Thresholds.Load.Warn);
            Assert.True(session.HasOverride("thresholds.load.warn"));

            int removed = session.RemoveStaleThresholdOverrides();

            Assert.Equal(1, removed);
            Assert.False(session.HasOverride("thresholds.load.warn"));
            Assert.Equal(80, session.Draft.Thresholds.Load.Warn);
        }

        [Fact]
        public void Remove_IsCaseInsensitiveOnRootSegment()
        {
            ThemeDocumentSession session = NewSession();
            session.ApplyEdit(new ThemeFieldEdit(
                "Thresholds.LOAD.Warn", JsonValue.Create(70),
                t => t.Thresholds.Load.Warn = 70));

            int removed = session.RemoveStaleThresholdOverrides();

            Assert.Equal(1, removed);
            Assert.False(session.HasOverride("Thresholds.LOAD.Warn"));
            Assert.Equal(80, session.Draft.Thresholds.Load.Warn);
        }

        [Fact]
        public void Remove_PreservesOtherOverrides()
        {
            ThemeDocumentSession session = NewSession();
            EditColor(session);
            EditThreshold(session, 70);

            session.RemoveStaleThresholdOverrides();

            Assert.True(session.HasOverride("color.background"));
            Assert.Equal("#FF202020", session.Draft.Color.Background);
            Assert.False(session.HasOverride("thresholds.load.warn"));
            Assert.Equal(80, session.Draft.Thresholds.Load.Warn);
        }

        [Fact]
        public void Remove_CleansUndoHistory_NoThresholdRevival()
        {
            ThemeDocumentSession session = NewSession();
            EditThreshold(session, 70); // 事务 T1：阈值
            EditColor(session);         // 事务 T2：颜色
            int undoBefore = session.UndoCount;

            session.RemoveStaleThresholdOverrides();

            Assert.Equal(undoBefore - 1, session.UndoCount);

            // 撤销掉颜色事务后，阈值必须仍是源值（T1 已从历史移除，快照不复活）。
            Assert.True(session.Undo());
            Assert.False(session.HasOverride("thresholds.load.warn"));
            Assert.Equal(80, session.Draft.Thresholds.Load.Warn);
        }

        [Fact]
        public void Remove_HandlesDeleteMarkers()
        {
            ThemeDocumentSession session = NewSession();
            // 删除标记：JsonValue=null（编辑意图=删掉该覆盖/键）
            session.ApplyEdit(new ThemeFieldEdit("thresholds.load.warn", null, null));

            int removed = session.RemoveStaleThresholdOverrides();

            Assert.Equal(1, removed);
            Assert.False(session.HasOverride("thresholds.load.warn"));
            Assert.Equal(80, session.Draft.Thresholds.Load.Warn);
        }

        [Fact]
        public void Restore_SnapshotCleansThresholdOverrides()
        {
            // 沙箱写入磁盘源（Restore 从 ThemeDocumentStore.PathOf 读源做摘要比对）。
            string name = "restore-demo";
            File.WriteAllText(ThemeDocumentStore.PathOf(name), SourceJson);

            ThemeDocumentSession original = ThemeDocumentSession.FromSourceBytes(
                name, ThemeDocumentStore.PathOf(name), _sourceBytes);
            EditThreshold(original, 70);
            ThemeDraftSnapshot snapshot = original.CaptureSnapshot();

            (ThemeDocumentSession restored, bool conflict) = ThemeDocumentSession.Restore(snapshot);

            Assert.False(conflict);
            Assert.False(restored.HasOverride("thresholds.load.warn"));
            Assert.Equal(80, restored.Draft.Thresholds.Load.Warn);
        }

        [Fact]
        public void ConflictCopy_KeepsThresholdsAsSource()
        {
            string name = "conflict-demo";
            string path = ThemeDocumentStore.PathOf(name);
            File.WriteAllText(path, SourceJson);

            ThemeDocumentSession original = ThemeDocumentSession.FromSourceBytes(name, path, _sourceBytes);
            EditThreshold(original, 70);
            ThemeDraftSnapshot snapshot = original.CaptureSnapshot();

            // 外部改源 → 摘要冲突 → 恢复副本（草稿成为新源，阈值烘进源字节，保留不猜原值）。
            File.WriteAllText(path, SourceJson.Replace("80", "81"));

            (ThemeDocumentSession copy, bool conflict) = ThemeDocumentSession.Restore(snapshot);

            Assert.True(conflict);
            Assert.True(copy.IsRecoveredCopy);
            // 阈值编辑保留为源值语义（恢复副本的 Draft 即源），不回滚、不猜原值。
            Assert.Equal(70, copy.Draft.Thresholds.Load.Warn);
        }

        [Fact]
        public void BuildPayload_DropsThresholdOverrides_PayloadEqualsSource()
        {
            ThemeDocumentSession session = NewSession();
            EditThreshold(session, 70);

            (byte[] payload, IReadOnlyList<string> _) = session.BuildPayload();

            Assert.Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_sourceBytes)),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)),
                ignoreCase: true);
        }

        [Fact]
        public void SourceWithoutThresholds_NoSynthesis()
        {
            ThemeDocumentSession session = NewSession(System.Text.Encoding.UTF8.GetBytes(SourceJsonNoThresholds));
            EditThreshold(session, 70);

            session.RemoveStaleThresholdOverrides();

            Assert.False(session.HasOverride("thresholds.load.warn"));
            // 源没有 thresholds 块：保持模型缺省实例（Load 默认 Warn=65），不合成任何编辑值。
            Assert.NotNull(session.Draft.Thresholds);
            Assert.Equal(65, session.Draft.Thresholds.Load.Warn);
        }

        [Fact]
        public void Catalog_DoesNotExposeThresholdProperties()
        {
            var descriptors = ThemePropertyCatalog.GetProperties(
                new Theme { Name = "demo", Color = new ColorConfig() }, ThemeEditTarget.Classic);

            Assert.DoesNotContain(descriptors, d => d.JsonPath.StartsWith("thresholds", StringComparison.OrdinalIgnoreCase));
        }
    }
}
