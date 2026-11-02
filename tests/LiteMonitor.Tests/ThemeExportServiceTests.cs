using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// TE07 导出与投稿准备回归：
    /// - JSON 导出与会话当前修订（BuildPayload）逐字节一致，未知扩展键保留、缺省不被物化；
    /// - 导出**不修改**会话（身份 / 脏状态 / 源文件字节都不变）；
    /// - 效果图必须带**真实**路线标注（GPU 不可用时不得虚标 GPU）；
    /// - 投稿资料目录齐备，且 README 不含任何"已上架／已发布／已通过校验"式的结论。
    ///
    /// 依据：docs/specs/theme-system.md 中主题导出与投稿边界的现行约束。
    /// </summary>
    public class ThemeExportServiceTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _outDir;

        public ThemeExportServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "litemonitor-te07-" + Guid.NewGuid().ToString("N"));
            _outDir = Path.Combine(_dir, "out");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        // ------------------------------------------------------------
        // 素材
        // ------------------------------------------------------------

        private const string ClassicJson = @"{
  ""version"": 3,
  ""name"": ""经典蓝"",
  ""layout"": { ""rowHeight"": 40, ""padding"": 12 },
  ""font"": { ""family"": ""Microsoft YaHei UI"", ""value"": 14.0, ""bold"": true },
  ""color"": { ""background"": ""#3C3C3C"", ""textTitle"": ""#FFFFFF"", ""futureKey"": { ""nested"": [1, 2, 3] } },
  ""x-unknown"": { ""deep"": true }
}";

        private const string GpuJson = @"{
  ""version"": 3,
  ""name"": ""赛博GPU"",
  ""rendering"": {
    ""version"": 1,
    ""backend"": ""gpu"",
    ""material"": ""cyber_punk"",
    ""parameters"": { ""liveBackground"": false, ""primaryCut"": 14, ""secondaryCut"": 9, ""outlineWidth"": 1, ""glow"": 12, ""ambientRatio"": 0.12, ""panelOpacity"": 0.965 },
    ""motion"": { ""targetFps"": 30, ""stateTransitionMs"": 140, ""scannerPeriodMs"": 2800, ""motionEnabled"": true },
    ""fallback"": { ""backend"": ""legacy"", ""material"": ""legacy"" },
    ""fallbackStyle"": { ""background"": ""#0A1017"", ""panelBackground"": ""#121B24"", ""border"": ""#345064"", ""text"": ""#E8F7FA"", ""mutedText"": ""#91A7B2"", ""accent"": ""#48E6F2"" }
  },
  ""layout"": { ""rowHeight"": 40, ""padding"": 12 },
  ""font"": { ""family"": ""Microsoft YaHei UI"", ""value"": 14.0 },
  ""color"": { ""background"": ""#0A1017"", ""textTitle"": ""#E8F7FA"" }
}";

        /// <summary>渲染参数越界（cornerRadius 999）⇒ rendering.Validate() 必须报错。</summary>
        private const string BadGpuJson = @"{
  ""version"": 3,
  ""name"": ""坏GPU"",
  ""rendering"": {
    ""version"": 1,
    ""backend"": ""gpu"",
    ""material"": ""cyber_punk"",
    ""parameters"": { ""liveBackground"": false, ""cornerRadius"": 999, ""primaryCut"": 14, ""secondaryCut"": 9, ""outlineWidth"": 1, ""glow"": 12, ""ambientRatio"": 0.12, ""panelOpacity"": 0.965 },
    ""motion"": { ""targetFps"": 30, ""stateTransitionMs"": 140, ""scannerPeriodMs"": 2800, ""motionEnabled"": true },
    ""fallback"": { ""backend"": ""legacy"", ""material"": ""legacy"" }
  },
  ""layout"": { ""rowHeight"": 40, ""padding"": 12 },
  ""font"": { ""family"": ""Microsoft YaHei UI"", ""value"": 14.0 },
  ""color"": { ""background"": ""#0A1017"", ""textTitle"": ""#E8F7FA"" }
}";

        /// <summary>含远程引用（市场合同 F7 禁止主题包引用远程资源）。</summary>
        private const string RemoteRefJson = @"{
  ""version"": 3,
  ""name"": ""远程引用"",
  ""layout"": { ""rowHeight"": 40, ""padding"": 12 },
  ""font"": { ""family"": ""Microsoft YaHei UI"", ""value"": 14.0 },
  ""color"": { ""background"": ""#202020"", ""textTitle"": ""#FFFFFF"" },
  ""x-remote"": ""https://example.invalid/font.png""
}";

        // ------------------------------------------------------------
        // 辅助
        // ------------------------------------------------------------

        private ThemeDocumentSession OpenSession(string name, string json)
        {
            string path = Path.Combine(_dir, name + ".json");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return ThemeDocumentSession.FromSourceBytes(name, path, File.ReadAllBytes(path));
        }

        private static string Sha256(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        /// <summary>在 STA 线程上跑（与生产 UI 线程同源；WinForms 控件创建需要 STA）。</summary>
        private static T RunSta<T>(Func<T> action)
        {
            T result = default;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    try { Application.EnableVisualStyles(); } catch { /* 已启用则忽略 */ }
                    result = action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(120)))
            {
                throw new TimeoutException("导出操作在 STA 线程上超时。");
            }

            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            return result;
        }

        /// <summary>%LOCALAPPDATA% 下 GPU 预览守卫目录的快照（用于"导出不得污染本地状态"断言）。</summary>
        private static string SnapshotLocalGpuGuardState()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiteMonitor++",
                "gpu-startup-preview");
            if (!Directory.Exists(dir)) return "<missing>";

            var sb = new StringBuilder();
            foreach (string file in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                sb.Append(Path.GetFileName(file))
                  .Append('|').Append(info.Length)
                  .Append('|').Append(info.LastWriteTimeUtc.Ticks)
                  .Append(';');
            }

            return sb.ToString();
        }

        // ============================================================
        // JSON 导出
        // ============================================================

        [Fact]
        public void JSON导出_与BuildPayload字节一致()
        {
            ThemeDocumentSession session = OpenSession("经典蓝", ClassicJson);
            (byte[] payload, _) = session.BuildPayload();

            string outPath = Path.Combine(_outDir, "export.json");
            ThemeExportResult result = ThemeExportService.ExportJson(session, outPath);

            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(outPath));

            byte[] written = File.ReadAllBytes(outPath);
            Assert.Equal(payload, written);
            Assert.Equal(Sha256(written), result.Sha256);
            Assert.Equal(written.Length, result.ByteCount);
        }

        [Fact]
        public void JSON导出_未知扩展键保留_缺省不被物化()
        {
            ThemeDocumentSession session = OpenSession("经典蓝", ClassicJson);

            string outPath = Path.Combine(_outDir, "export.json");
            Assert.True(ThemeExportService.ExportJson(session, outPath).Success);

            string text = File.ReadAllText(outPath);
            // 未知顶层键与未知嵌套键都保留
            Assert.Contains("\"x-unknown\"", text);
            Assert.Contains("\"futureKey\"", text);
            Assert.Contains("\"deep\": true", text);
            // 缺省不被物化：源里没有的块不允许被写出来
            Assert.DoesNotContain("\"rendering\"", text);
            Assert.DoesNotContain("\"backdrop\"", text);
        }

        [Fact]
        public void JSON导出_未保存草稿也可导出_内容是补丁写回()
        {
            ThemeDocumentSession session = OpenSession("经典蓝", ClassicJson);
            session.ApplyEdit(new ThemeFieldEdit(
                "color.background",
                JsonValue.Create("#101010"),
                theme => theme.Color.Background = "#101010"));
            Assert.True(session.IsDirty);

            string outPath = Path.Combine(_outDir, "export.json");
            ThemeExportResult result = ThemeExportService.ExportJson(session, outPath);

            Assert.True(result.Success, result.Error);
            string text = File.ReadAllText(outPath);
            Assert.Contains("\"#101010\"", text);
            Assert.Contains("\"x-unknown\"", text);
            // 导出不改会话：仍是脏的
            Assert.True(session.IsDirty);
        }

        [Fact]
        public void 导出到不存在的目录_自动创建()
        {
            ThemeDocumentSession session = OpenSession("经典蓝", ClassicJson);

            string nested = Path.Combine(_outDir, "a", "b", "c", "theme.json");
            ThemeExportResult result = ThemeExportService.ExportJson(session, nested);

            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(nested));
        }

        // ============================================================
        // 效果图
        // ============================================================

        [Fact]
        public void 经典主题_效果图PNG非空且尺寸固定()
        {
            ThemeDocumentSession session = OpenSession("经典蓝", ClassicJson);
            string png = Path.Combine(_outDir, "classic.png");

            ThemeImageExportResult result = RunSta(() =>
                ThemeExportService.ExportEffectImage(session, png, ImageRoutePreference.Auto));

            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(png));
            Assert.True(new FileInfo(png).Length > 0);
            Assert.Equal(ThemeExportImageRoute.Classic, result.Route);
            Assert.False(result.IsFallback);
            Assert.Equal(Sha256(File.ReadAllBytes(png)), result.Sha256);

            using var bitmap = new Bitmap(png);
            Assert.Equal(ThemeExportService.VerticalImageWidth, bitmap.Width);
            Assert.Equal(ThemeExportService.VerticalImageHeight, bitmap.Height);
        }

        [Fact]
        public void 横向画幅_尺寸为400x120()
        {
            ThemeDocumentSession session = OpenSession("经典蓝", ClassicJson);
            string png = Path.Combine(_outDir, "classic-h.png");

            ThemeImageExportResult result = RunSta(() =>
                ThemeExportService.ExportEffectImage(
                    session, png, ImageRoutePreference.Auto, ThemeExportImageOrientation.Horizontal));

            Assert.True(result.Success, result.Error);
            using var bitmap = new Bitmap(png);
            Assert.Equal(ThemeExportService.HorizontalImageWidth, bitmap.Width);
            Assert.Equal(ThemeExportService.HorizontalImageHeight, bitmap.Height);
        }

        [Fact]
        public void 经典主题_强制GPU路线_明确失败且不冒充GPU()
        {
            ThemeDocumentSession session = OpenSession("经典蓝", ClassicJson);
            string png = Path.Combine(_outDir, "classic-gpu.png");

            ThemeImageExportResult result = RunSta(() =>
                ThemeExportService.ExportEffectImage(session, png, ImageRoutePreference.Gpu));

            Assert.False(result.Success);
            Assert.NotEqual(ThemeExportImageRoute.Gpu, result.Route);
            Assert.False(result.IsFallback);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }

        /// <summary>
        /// GPU 主题：GPU 真能用 ⇒ 路线必须是 Gpu；GPU 不可用 ⇒ 明确失败或标记为回退。
        /// 任何情况下都不得"把经典图标成 GPU"。
        /// </summary>
        [Fact]
        public void GPU主题_可用则路线为Gpu_不可用则失败或标记回退()
        {
            ThemeDocumentSession session = OpenSession("赛博GPU", GpuJson);
            string strictPath = Path.Combine(_outDir, "gpu-strict.png");
            string autoPath = Path.Combine(_outDir, "gpu-auto.png");

            ThemeImageExportResult strict = RunSta(() =>
                ThemeExportService.ExportEffectImage(session, strictPath, ImageRoutePreference.Gpu));
            ThemeImageExportResult auto = RunSta(() =>
                ThemeExportService.ExportEffectImage(session, autoPath, ImageRoutePreference.Auto));

            if (strict.Success)
            {
                // GPU 真的可用：两条偏好都必须走 GPU 路线，且有真实文件
                Console.WriteLine("[TE07] GPU 路线实际结果：可用（Route=Gpu，文件 " + strictPath + "）");
                Assert.Equal(ThemeExportImageRoute.Gpu, strict.Route);
                Assert.False(strict.IsFallback);
                Assert.True(File.Exists(strictPath));
                Assert.True(new FileInfo(strictPath).Length > 0);

                Assert.True(auto.Success, auto.Error);
                Assert.Equal(ThemeExportImageRoute.Gpu, auto.Route);
                Assert.False(auto.IsFallback);
                Assert.True(File.Exists(autoPath));
                Assert.True(new FileInfo(autoPath).Length > 0);
            }
            else
            {
                // GPU 不可用：强制 GPU 必须失败，且不得声称 GPU
                Console.WriteLine("[TE07] GPU 路线实际结果：不可用（" + strict.Error + "）");
                Assert.NotEqual(ThemeExportImageRoute.Gpu, strict.Route);
                Assert.False(string.IsNullOrWhiteSpace(strict.Error));

                // 自动路线必须明确回退（而不是把经典图标成 GPU）
                Assert.True(auto.Success, auto.Error);
                Assert.Equal(ThemeExportImageRoute.Fallback, auto.Route);
                Assert.True(auto.IsFallback);
                Assert.True(File.Exists(autoPath));
                Assert.True(new FileInfo(autoPath).Length > 0);
                Assert.Contains(auto.Diagnostics, d => d.Contains("GPU"));
            }
        }

        [Fact]
        public void GPU导出_不污染LOCALAPPDATA的守卫状态()
        {
            string before = SnapshotLocalGpuGuardState();

            ThemeDocumentSession session = OpenSession("赛博GPU", GpuJson);
            string png = Path.Combine(_outDir, "gpu-guard.png");
            RunSta(() => ThemeExportService.ExportEffectImage(session, png, ImageRoutePreference.Auto));

            Assert.Equal(before, SnapshotLocalGpuGuardState());
        }

        // ============================================================
        // 投稿资料
        // ============================================================

        [Fact]
        public void 投稿资料_目录结构齐全_摘要与themejson实际文件一致()
        {
            ThemeDocumentSession session = OpenSession("经典蓝", ClassicJson);
            string dir = Path.Combine(_outDir, "submission");

            ThemeSubmissionResult result = ThemeExportService.PrepareSubmissionMaterials(session, dir);

            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(Path.Combine(dir, "theme.json")));
            Assert.True(File.Exists(Path.Combine(dir, "submission.json")));
            Assert.True(File.Exists(Path.Combine(dir, "README.txt")));

            byte[] themeBytes = File.ReadAllBytes(Path.Combine(dir, "theme.json"));
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "submission.json")));
            JsonElement root = doc.RootElement;

            Assert.Equal(Sha256(themeBytes), root.GetProperty("sha256").GetString());
            Assert.Equal(themeBytes.Length, root.GetProperty("byteCount").GetInt64());
            Assert.Equal("经典蓝", root.GetProperty("name").GetString());
            Assert.Equal("legacy", root.GetProperty("material").GetString());
            Assert.Equal("legacy", root.GetProperty("route").GetString());
            Assert.True(root.TryGetProperty("author", out _));
            Assert.True(root.TryGetProperty("version", out _));
            Assert.True(root.TryGetProperty("capabilities", out JsonElement caps));
            Assert.NotEmpty(caps.EnumerateArray().ToList());
            Assert.True(root.TryGetProperty("exportedAtUtc", out JsonElement exportedAt));
            Assert.False(string.IsNullOrWhiteSpace(exportedAt.GetString()));
        }

        [Fact]
        public void 投稿资料_TM02元数据写入submission且发布版本与主题格式版本分离()
        {
            ThemeDocumentSession session = OpenSession("经典蓝", ClassicJson);
            string dir = Path.Combine(_outDir, "submission-meta");
            var metadata = new ThemeSubmissionMetadata
            {
                Id = "classic-blue",
                Name = "经典蓝",
                NameEn = "Classic Blue",
                Description = "经典蓝色外观",
                DescriptionEn = "Classic blue appearance",
                Author = "Tester",
                MarketVersion = "2.3.4",
                License = "MIT"
            };

            ThemeSubmissionResult result = ThemeExportService.PrepareSubmissionMaterials(session, dir, metadata);

            Assert.True(result.Success, result.Error);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "submission.json")));
            JsonElement root = doc.RootElement;
            Assert.Equal("classic-blue", root.GetProperty("id").GetString());
            Assert.Equal("Classic Blue", root.GetProperty("name_en").GetString());
            Assert.Equal("Classic blue appearance", root.GetProperty("description_en").GetString());
            Assert.Equal("2.3.4", root.GetProperty("market_version").GetString());
            Assert.Equal("MIT", root.GetProperty("license").GetString());
            Assert.Equal("Tester", root.GetProperty("author").GetString());
            Assert.Equal(JsonValueKind.Number, root.GetProperty("version").ValueKind);
        }

        [Fact]
        public void 投稿资料_TM02非法投稿ID被拒绝()
        {
            ThemeDocumentSession session = OpenSession("经典蓝", ClassicJson);
            var metadata = new ThemeSubmissionMetadata
            {
                Id = "Bad ID",
                Name = "经典蓝",
                MarketVersion = "1.0.0",
                License = "MIT"
            };

            ThemeSubmissionResult result = ThemeExportService.PrepareSubmissionMaterials(
                session, Path.Combine(_outDir, "submission-bad-meta"), metadata);

            Assert.False(result.Success);
            Assert.Contains("投稿 ID", result.Error);
        }

        [Fact]
        public void 投稿资料_README说明投稿入口且不含发布结论()
        {
            ThemeDocumentSession session = OpenSession("经典蓝", ClassicJson);
            string dir = Path.Combine(_outDir, "submission");

            Assert.True(ThemeExportService.PrepareSubmissionMaterials(session, dir).Success);

            string readme = File.ReadAllText(Path.Combine(dir, "README.txt"));
            // 投稿入口三要素：人工提交 PR、市场 CI、维护者审核
            Assert.Contains("Pull Request", readme);
            Assert.Contains("CI", readme);
            Assert.Contains("维护者", readme);
            // 绝不出现"已上架 / 已发布 / 已通过校验"式的结论
            Assert.DoesNotContain("已上架", readme);
            Assert.DoesNotContain("已发布", readme);
            Assert.DoesNotContain("已通过校验", readme);
        }

        [Fact]
        public void 投稿资料_渲染校验失败_不生成资料()
        {
            ThemeDocumentSession session = OpenSession("坏GPU", BadGpuJson);
            string dir = Path.Combine(_outDir, "submission-bad");

            ThemeSubmissionResult result = ThemeExportService.PrepareSubmissionMaterials(session, dir);

            Assert.False(result.Success);
            Assert.Contains("cornerRadius", result.Error);
            Assert.False(File.Exists(Path.Combine(dir, "submission.json")));
            Assert.False(File.Exists(Path.Combine(dir, "README.txt")));
        }

        [Fact]
        public void 投稿资料_远程引用_报需人工确认()
        {
            ThemeDocumentSession session = OpenSession("远程引用", RemoteRefJson);
            string dir = Path.Combine(_outDir, "submission-remote");

            ThemeSubmissionResult result = ThemeExportService.PrepareSubmissionMaterials(session, dir);

            Assert.True(result.Success, result.Error);
            Assert.Contains(result.ReviewRequired, item => item.Contains("远程引用需人工确认"));
        }

        [Fact]
        public void 投稿资料_目录不存在_自动创建()
        {
            ThemeDocumentSession session = OpenSession("经典蓝", ClassicJson);
            string dir = Path.Combine(_outDir, "deep", "nested", "submission");

            ThemeSubmissionResult result = ThemeExportService.PrepareSubmissionMaterials(session, dir);

            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(Path.Combine(dir, "theme.json")));
        }

        // ============================================================
        // 导出不得修改会话
        // ============================================================

        [Fact]
        public void 全部导出_不改会话身份脏状态与源文件()
        {
            ThemeDocumentSession session = OpenSession("经典蓝", ClassicJson);
            session.ApplyEdit(new ThemeFieldEdit(
                "color.background",
                JsonValue.Create("#101010"),
                theme => theme.Color.Background = "#101010"));

            string sourcePath = session.SourcePath;
            byte[] sourceBefore = File.ReadAllBytes(sourcePath);
            string nameBefore = session.ThemeName;
            string shaBefore = session.SourceSha256;
            long revBefore = session.Revision;
            long savedRevBefore = session.SavedRevision;
            bool dirtyBefore = session.IsDirty;
            Assert.True(dirtyBefore);

            ThemeExportService.ExportJson(session, Path.Combine(_outDir, "a.json"));
            RunSta(() => ThemeExportService.ExportEffectImage(session, Path.Combine(_outDir, "a.png"), ImageRoutePreference.Auto));
            ThemeExportService.PrepareSubmissionMaterials(session, Path.Combine(_outDir, "sub"));

            Assert.Equal(nameBefore, session.ThemeName);
            Assert.Equal(shaBefore, session.SourceSha256);
            Assert.Equal(revBefore, session.Revision);
            Assert.Equal(savedRevBefore, session.SavedRevision);
            Assert.Equal(dirtyBefore, session.IsDirty);
            Assert.Equal(sourceBefore, File.ReadAllBytes(sourcePath));
        }
    }
}
