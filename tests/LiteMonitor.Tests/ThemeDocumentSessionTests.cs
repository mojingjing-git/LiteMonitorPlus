using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LiteMonitor.ThemeEditor;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// TE01 文档模型回归：独立会话、补丁式保存、撤销／重做、外部冲突、恢复草稿。
    /// 依据 docs/design/theme-studio/TE00-capability-contract.md §4 与重做计划 §5.1/§5.2。
    /// </summary>
    public class ThemeDocumentSessionTests : IDisposable
    {
        private readonly string _dir;

        public ThemeDocumentSessionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "litemonitor-te01-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            ThemeDocumentStore.SetThemeDirForTests(_dir);
            ThemeDocumentStore.SetRecoveryDirForTests(
                Path.Combine(_dir, "recovery"));
        }

        public void Dispose()
        {
            ThemeDocumentStore.ResetRecoveryDir();
            ThemeDocumentStore.SetThemeDirForTests(null);
            ThemeFileService.ResetTestHooks();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static string Sha(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        private string WriteTheme(string name, string json)
        {
            string path = Path.Combine(_dir, name + ".json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }

        private const string SourceJson = @"{
  ""version"": 3,
  ""name"": ""经典蓝"",
  ""layout"": { ""rowHeight"": 40, ""padding"": 12 },
  ""font"": { ""family"": ""Microsoft YaHei UI"", ""value"": 14.0, ""bold"": true },
  ""color"": { ""background"": ""#3C3C3C"", ""textTitle"": ""#FFFFFF"", ""futureKey"": { ""nested"": [1, 2, 3] } },
  ""someUnknown"": { ""deep"": true }
}";

        private ThemeDocumentSession OpenSession() =>
            ThemeDocumentSession.FromSourceBytes("经典蓝", Path.Combine(_dir, "经典蓝.json"), File.ReadAllBytes(Path.Combine(_dir, "经典蓝.json")));

        // ============================================================
        // 打开与会话隔离
        // ============================================================

        [Fact]
        public void 打开_不改运行主题_不碰ThemeManagerCurrent()
        {
            Theme before = ThemeManager.Current;
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = ThemeDocumentSession.FromSourceBytes(
                "经典蓝", Path.Combine(_dir, "经典蓝.json"), File.ReadAllBytes(Path.Combine(_dir, "经典蓝.json")));

            session.ApplyEdit(new ThemeFieldEdit(
                "color.background",
                JsonValue.Create("#101010"),
                theme => theme.Color.Background = "#101010"));

            Assert.Same(before, ThemeManager.Current);
            Assert.NotEqual("#101010", ThemeManager.Current.Color.Background);
        }

        [Fact]
        public void 打开_未知顶层与嵌套扩展键保留_草稿可读()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();

            Assert.Equal("#3C3C3C", session.Draft.Color.Background);
            Assert.True(session.Draft.ExtensionData.ContainsKey("someUnknown"));
            Assert.True(session.Draft.Layout.ExtensionData.Count == 0);
            Assert.True(session.Draft.Color.ExtensionData.ContainsKey("futureKey"));
        }

        // ============================================================
        // 补丁式保存：缺省不被物化
        // ============================================================

        [Fact]
        public void 未编辑保存_载荷与源字节一致()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();

            ThemeSaveResult result = session.Save();

            Assert.True(result.Success);
            Assert.Equal(Sha(Path.Combine(_dir, "经典蓝.json")), result.WrittenSha256);
            Assert.False(session.IsDirty);
        }

        [Fact]
        public void 编辑保存_未知键原样保留_未触碰键大小写不变()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();
            session.ApplyEdit(new ThemeFieldEdit(
                "color.background",
                JsonValue.Create("#101010"),
                theme => theme.Color.Background = "#101010"));

            Assert.True(session.Save().Success);

            string saved = File.ReadAllText(Path.Combine(_dir, "经典蓝.json"));
            Assert.Contains("\"futureKey\"", saved);
            Assert.Contains("\"deep\": true", saved);
            Assert.Contains("\"textTitle\"", saved);
            Assert.Contains("\"#101010\"", saved);
            // 源里是 camelCase rowHeight，未被编辑 ⇒ 不应出现 PascalCase 变体
            Assert.DoesNotContain("\"RowHeight\"", saved);
            Assert.DoesNotContain("\"Background\"", saved);
        }

        [Fact]
        public void 恢复缺省_写null覆盖_保存后键从JSON消失()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();
            // background 不是 ColorConfig 的可空属性 ⇒ 用文本编辑把键删除，草稿回模型默认
            session.ApplyEdit(new ThemeFieldEdit(
                "color.background",
                (JsonNode?)null,
                theme => theme.Color.Background = new ColorConfig().Background));

            Assert.True(session.Save().Success);

            string saved = File.ReadAllText(Path.Combine(_dir, "经典蓝.json"));
            Assert.DoesNotContain("\"background\"", saved);
            Assert.Contains("\"textTitle\"", saved);
        }

        [Fact]
        public void 保存中继续编辑_已写入修订为基线_后续修改仍脏()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();
            session.ApplyEdit(new ThemeFieldEdit("font.value", JsonValue.Create(16d), t => t.Font.Value = 16));

            long revAtSave = session.Revision;
            ThemeSaveResult result = session.Save();

            Assert.True(result.Success);
            Assert.Equal(revAtSave, session.SavedRevision);
            Assert.False(session.IsDirty);

            session.ApplyEdit(new ThemeFieldEdit("font.value", JsonValue.Create(18d), t => t.Font.Value = 18));
            Assert.True(session.IsDirty);
            Assert.Equal(revAtSave, session.SavedRevision);
        }

        [Fact]
        public void GPU主题保存_渲染校验失败_不写盘不标已保存()
        {
            string gpu = @"{ ""name"": ""G"", ""rendering"": { ""version"": 1, ""backend"": ""gpu"",
""material"": ""liquid_glass"", ""parameters"": { ""liveBackground"": false, ""cornerRadius"": 999 } } }";
            WriteTheme("GpuBad", gpu);
            byte[] before = File.ReadAllBytes(Path.Combine(_dir, "GpuBad.json"));
            ThemeDocumentSession session = ThemeDocumentSession.FromSourceBytes(
                "GpuBad", Path.Combine(_dir, "GpuBad.json"), before);
            session.ApplyEdit(new ThemeFieldEdit(
                "color.textTitle", JsonValue.Create("#FFFFFF"), t => t.Color.TextTitle = "#FFFFFF"));

            ThemeSaveResult result = session.Save();

            Assert.False(result.Success);
            Assert.Contains("cornerRadius", result.Error);
            Assert.Equal(Sha256Hash(before), session.SourceSha256);
            Assert.True(session.IsDirty);
        }

        // ============================================================
        // 另存为
        // ============================================================

        [Fact]
        public void 另存为_新文件写入成功才切换身份()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();
            session.ApplyEdit(new ThemeFieldEdit(
                "color.background", JsonValue.Create("#202020"), t => t.Color.Background = "#202020"));

            ThemeSaveResult result = session.SaveAs("新主题甲");

            Assert.True(result.Success);
            Assert.Equal("新主题甲", session.ThemeName);
            Assert.False(session.IsDirty);
            Assert.True(File.Exists(Path.Combine(_dir, "新主题甲.json")));
            Assert.True(File.Exists(Path.Combine(_dir, "经典蓝.json"))); // 原文件不动
        }

        [Fact]
        public void 另存为_重名_失败保留原身份()
        {
            WriteTheme("经典蓝", SourceJson);
            WriteTheme("已存在", "{}");
            ThemeDocumentSession session = OpenSession();
            session.ApplyEdit(new ThemeFieldEdit(
                "color.background", JsonValue.Create("#111111"), t => t.Color.Background = "#111111"));

            ThemeSaveResult result = session.SaveAs("已存在");

            Assert.False(result.Success);
            Assert.Equal("经典蓝", session.ThemeName);
            Assert.True(session.IsDirty); // 另存失败不改写盘状态，草稿修改保留
        }

        // ============================================================
        // 撤销／重做与事务合并
        // ============================================================

        [Fact]
        public void 连续滑块拖动_同键合并为一个事务()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();

            for (int i = 1; i <= 5; i++)
            {
                double v = 40 + i;
                session.ApplyEdit(new ThemeFieldEdit("layout.rowHeight", JsonValue.Create(v), t => t.Layout.RowHeight = (int)v));
            }

            Assert.Equal(1, session.UndoCount);
            Assert.True(session.Undo());
            Assert.Equal(40, session.Draft.Layout.RowHeight);
            Assert.True(session.Redo());
            Assert.Equal(45, session.Draft.Layout.RowHeight);
        }

        [Fact]
        public void 撤销重做_跨多事务_草稿与覆盖层同步()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();
            session.ApplyEdit(new ThemeFieldEdit("color.background", JsonValue.Create("#111111"), t => t.Color.Background = "#111111"));
            session.ApplyEdit(new ThemeFieldEdit("color.textTitle", JsonValue.Create("#222222"), t => t.Color.TextTitle = "#222222"));
            session.ApplyEdit(new ThemeFieldEdit("font.value", JsonValue.Create(18d), t => t.Font.Value = 18));

            Assert.True(session.Undo());
            Assert.Equal(14.0, session.Draft.Font.Value);
            Assert.True(session.Undo());
            Assert.Equal("#FFFFFF", session.Draft.Color.TextTitle);
            Assert.True(session.Redo());
            Assert.Equal("#222222", session.Draft.Color.TextTitle);
            Assert.True(session.Redo());
            Assert.Equal(18.0, session.Draft.Font.Value);
            // 三次撤销走完后：无可撤销，三个事务全部进入重做栈
            Assert.True(session.Undo());
            Assert.True(session.Undo());
            Assert.True(session.Undo());
            Assert.Equal(14.0, session.Draft.Font.Value);
            Assert.Equal(0, session.UndoCount);
            Assert.Equal(3, session.RedoCount);
        }

        [Fact]
        public void 撤销跨保存点_重新变脏()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();
            session.ApplyEdit(new ThemeFieldEdit("color.background", JsonValue.Create("#111111"), t => t.Color.Background = "#111111"));
            Assert.True(session.Save().Success);
            Assert.False(session.IsDirty);

            Assert.True(session.Undo());
            Assert.True(session.IsDirty);
            Assert.Equal("#3C3C3C", session.Draft.Color.Background);

            Assert.True(session.Save().Success);
            string saved = File.ReadAllText(Path.Combine(_dir, "经典蓝.json"));
            Assert.Contains("\"#3C3C3C\"", saved);
        }

        [Fact]
        public void 历史上限_超过100事务淘汰最旧_撤销不越界()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();

            for (int i = 0; i < ThemeDocumentHistory.MaxTransactions + 25; i++)
            {
                string key = "color.background";
                double v = i;
                session.ApplyEdit(
                    new ThemeFieldEdit(key, JsonValue.Create("#" + (v % 10).ToString("0") + "00000"),
                        t => t.Color.Background = "#" + (v % 10).ToString("0") + "00000"),
                    coalesceKey: key + ":" + i); // 每次不同键 ⇒ 每个编辑独立事务
            }

            int undoBudget = session.UndoCount;
            Assert.True(undoBudget <= ThemeDocumentHistory.MaxTransactions);
            for (int i = 0; i < undoBudget; i++)
            {
                Assert.True(session.Undo());
            }

            Assert.False(session.Undo());
            // 有界历史：125 个编辑淘汰最旧 25 个后，满撤销落在第 24 次编辑之后的状态，
            // 不会越过存活历史回到源值——这正是容量上限的语义。
            Assert.Equal("#400000", session.Draft.Color.Background);
            Assert.Equal(40, session.Draft.Layout.RowHeight); // 未编辑字段不受历史影响
            Assert.Equal(14.0, session.Draft.Font.Value);
        }

        // ============================================================
        // 外部冲突
        // ============================================================

        [Fact]
        public void 外部修改_保存进冲突_明确覆盖才写盘()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();
            session.ApplyEdit(new ThemeFieldEdit("color.background", JsonValue.Create("#111111"), t => t.Color.Background = "#111111"));

            // 外部改动（模拟另一个进程保存）
            File.WriteAllText(Path.Combine(_dir, "经典蓝.json"), SourceJson.Replace("#3C3C3C", "#999999"), new UTF8Encoding(false));

            ThemeSaveResult blocked = session.Save();
            Assert.False(blocked.Success);
            Assert.True(blocked.ExternalConflict);

            ThemeSaveResult forced = session.Save(overwriteExternalChanges: true);
            Assert.True(forced.Success);
            string saved = File.ReadAllText(Path.Combine(_dir, "经典蓝.json"));
            Assert.Contains("\"#111111\"", saved);
        }

        [Fact]
        public void 外部删除源文件_不算冲突_可直接保存()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();
            session.ApplyEdit(new ThemeFieldEdit("color.background", JsonValue.Create("#111111"), t => t.Color.Background = "#111111"));

            File.Delete(Path.Combine(_dir, "经典蓝.json"));

            Assert.False(session.HasExternalConflict());
            Assert.True(session.Save().Success);
        }

        // ============================================================
        // 恢复草稿
        // ============================================================

        [Fact]
        public void 恢复_源摘要一致_干净恢复覆盖层与草稿()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();
            session.ApplyEdit(new ThemeFieldEdit("color.background", JsonValue.Create("#111111"), t => t.Color.Background = "#111111"));
            session.ApplyEdit(new ThemeFieldEdit("font.value", JsonValue.Create(16d), t => t.Font.Value = 16));

            ThemeDraftSnapshot snapshot = session.CaptureSnapshot();
            ThemeDocumentStore.PersistRecovery(snapshot);

            Assert.Contains("经典蓝", ThemeDocumentStore.ListRecoveries());

            (ThemeDocumentSession restored, bool conflict) = ThemeDocumentSession.Restore(snapshot);
            Assert.False(conflict);
            Assert.Equal("#111111", restored.Draft.Color.Background);
            Assert.Equal(16.0, restored.Draft.Font.Value);
            Assert.True(restored.IsDirty);
            Assert.False(restored.IsRecoveredCopy);
        }

        [Fact]
        public void 恢复_源被外部改写_打开恢复副本_不覆盖磁盘()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();
            session.ApplyEdit(new ThemeFieldEdit("color.background", JsonValue.Create("#111111"), t => t.Color.Background = "#111111"));

            ThemeDraftSnapshot snapshot = session.CaptureSnapshot();
            ThemeDocumentStore.PersistRecovery(snapshot);
            File.WriteAllText(Path.Combine(_dir, "经典蓝.json"), SourceJson.Replace("#3C3C3C", "#999999"), new UTF8Encoding(false));

            (ThemeDocumentSession restored, bool conflict) = ThemeDocumentSession.Restore(snapshot);

            Assert.True(conflict);
            Assert.True(restored.IsRecoveredCopy);
            Assert.Equal("#111111", restored.Draft.Color.Background);
            // 恢复副本保存被拒，必须另存
            ThemeSaveResult saveBlocked = restored.Save();
            Assert.False(saveBlocked.Success);
            ThemeSaveResult saveAs = restored.SaveAs("恢复副本乙");
            Assert.True(saveAs.Success);
            Assert.False(restored.IsRecoveredCopy);
        }

        [Fact]
        public void 保存成功_恢复草稿被清理()
        {
            WriteTheme("经典蓝", SourceJson);
            ThemeDocumentSession session = OpenSession();
            session.ApplyEdit(new ThemeFieldEdit("color.background", JsonValue.Create("#111111"), t => t.Color.Background = "#111111"));
            ThemeDocumentStore.PersistRecovery(session.CaptureSnapshot());
            Assert.Contains("经典蓝", ThemeDocumentStore.ListRecoveries());

            Assert.True(session.Save().Success);

            Assert.DoesNotContain("经典蓝", ThemeDocumentStore.ListRecoveries());
        }

        [Fact]
        public void 恢复文件损坏_按不存在处理不抛()
        {
            Directory.CreateDirectory(Path.Combine(_dir, "recovery"));
            File.WriteAllText(
                Path.Combine(_dir, "recovery", "损坏.recovery.json"),
                "{ not valid json");
            Assert.False(ThemeDocumentStore.TryLoadRecovery("损坏", out ThemeDraftSnapshot? snapshot));
            Assert.Null(snapshot);
        }

        private static string Sha256Hash(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
