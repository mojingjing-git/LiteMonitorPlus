using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using LiteMonitor.ThemeEditor;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// TE03 GPU 视图回归（TE00 能力矩阵硬规则）：
    /// 材质分列可见性、固定前景不显示字体、fallbackStyle 六槽、切目标不改 backend。
    /// </summary>
    public class ThemePropertyCatalogGpuTests : IDisposable
    {
        private readonly string _dir;

        public ThemePropertyCatalogGpuTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "litemonitor-te03gpu-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            ThemeDocumentStore.SetThemeDirForTests(_dir);
        }

        public void Dispose()
        {
            ThemeDocumentStore.SetThemeDirForTests(null);
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private const string LiquidStandard = @"{
  ""name"": ""玻璃"",
  ""rendering"": { ""version"": 1, ""backend"": ""gpu"", ""material"": ""liquid_glass"",
    ""parameters"": { ""liveBackground"": false, ""cornerRadius"": 20 },
    ""fallback"": { ""backend"": ""legacy"", ""material"": ""legacy"" } }
}";

        private const string LiquidToolbar = @"{
  ""name"": ""浮动"",
  ""rendering"": { ""version"": 1, ""backend"": ""gpu"", ""material"": ""liquid_glass"",
    ""parameters"": { ""liveBackground"": false, ""glassStyle"": ""floatingToolbar"" },
    ""fallback"": { ""backend"": ""legacy"", ""material"": ""legacy"" } }
}";

        private const string CyberPunk = @"{
  ""name"": ""赛博"",
  ""rendering"": { ""version"": 1, ""backend"": ""gpu"", ""material"": ""cyber_punk"",
    ""parameters"": { ""primaryCut"": 14 },
    ""fallback"": { ""backend"": ""legacy"", ""material"": ""legacy"" } }
}";

        private ThemeDocumentSession Open(string name, string json)
        {
            File.WriteAllText(Path.Combine(_dir, name + ".json"), json, new UTF8Encoding(false));
            return ThemeDocumentSession.FromSourceBytes(
                name, Path.Combine(_dir, name + ".json"), Encoding.UTF8.GetBytes(json));
        }

        private static string[] Paths(Theme theme, ThemeEditTarget target) =>
            ThemePropertyCatalog.GetProperties(theme, target).Select(p => p.JsonPath).ToArray();

        [Fact]
        public void 液态玻璃主效果_只含运行时消费参数()
        {
            ThemeDocumentSession s = Open("玻璃", LiquidStandard);
            string[] paths = Paths(s.Draft, ThemeEditTarget.GpuMain);

            Assert.Contains("rendering.parameters.cornerRadius", paths);
            Assert.Contains("rendering.parameters.refraction", paths);
            Assert.Contains("rendering.parameters.glassStyle", paths);
            // 校验允许但运行时固定 0/1 的交叉项不得出现
            Assert.DoesNotContain("rendering.parameters.primaryCut", paths);
            Assert.DoesNotContain("rendering.parameters.glow", paths);
            Assert.DoesNotContain("rendering.parameters.panelOpacity", paths);
        }

        [Fact]
        public void 赛博朋克主效果_只含其消费参数_不含玻璃样式()
        {
            ThemeDocumentSession s = Open("赛博", CyberPunk);
            string[] paths = Paths(s.Draft, ThemeEditTarget.GpuMain);

            Assert.Contains("rendering.parameters.primaryCut", paths);
            Assert.Contains("rendering.parameters.outlineWidth", paths);
            Assert.Contains("rendering.parameters.panelOpacity", paths);
            Assert.DoesNotContain("rendering.parameters.cornerRadius", paths);
            Assert.DoesNotContain("rendering.parameters.refraction", paths);
            // cyber_punk 的 glassStyle 被生产强制忽略（TE00 §2.1）
            Assert.DoesNotContain("rendering.parameters.glassStyle", paths);
        }

        [Fact]
        public void 固定前景_不显示主题字体控件_floatingToolbar显示()
        {
            ThemeDocumentSession standard = Open("玻璃", LiquidStandard);
            string[] standardPaths = Paths(standard.Draft, ThemeEditTarget.GpuMain);
            Assert.DoesNotContain(standardPaths, p => p.StartsWith("font.", StringComparison.Ordinal));
            // 布局/阈值仍共享
            Assert.Contains("layout.rowHeight", standardPaths);
            Assert.Contains("thresholds.load.warn", standardPaths);

            ThemeDocumentSession toolbar = Open("浮动", LiquidToolbar);
            string[] toolbarPaths = Paths(toolbar.Draft, ThemeEditTarget.GpuMain);
            Assert.Contains("font.family", toolbarPaths);
            Assert.Contains("font.value", toolbarPaths);
        }

        [Fact]
        public void 兼容外观_六槽齐全_accent标注为标题强调色_共享项带Shared()
        {
            ThemeDocumentSession s = Open("玻璃", LiquidStandard);
            var props = ThemePropertyCatalog.GetProperties(s.Draft, ThemeEditTarget.GpuFallback);

            string[] slots = props
                .Where(p => p.JsonPath.StartsWith("rendering.fallbackStyle.", StringComparison.Ordinal))
                .Select(p => p.JsonPath["rendering.fallbackStyle.".Length..])
                .ToArray();
            Assert.Equal(
                new[] { "background", "panelBackground", "border", "text", "mutedText", "accent" },
                slots);

            ThemePropertyDescriptor accent = props.Single(p => p.JsonPath == "rendering.fallbackStyle.accent");
            Assert.Equal("标题强调色", accent.DisplayName);

            Assert.All(
                props.Where(p => p.JsonPath.StartsWith("font.", StringComparison.Ordinal) ||
                                 p.JsonPath.StartsWith("layout.", StringComparison.Ordinal) ||
                                 p.JsonPath.StartsWith("thresholds.", StringComparison.Ordinal)),
                p => Assert.True(p.Scope.HasFlag(ThemePropertyScope.Shared), p.JsonPath));
        }

        [Fact]
        public void 切换编辑目标_不改rendering_backend_且无编辑时载荷等于源字节()
        {
            ThemeDocumentSession s = Open("玻璃", LiquidStandard);
            string backendBefore = s.Draft.Rendering!.Backend;
            long revisionBefore = s.Revision;

            // 模拟 UI 在两个目标间来回切换
            _ = ThemePropertyCatalog.GetProperties(s.Draft, ThemeEditTarget.GpuMain);
            _ = ThemePropertyCatalog.GetProperties(s.Draft, ThemeEditTarget.GpuFallback);
            _ = ThemePropertyCatalog.GetProperties(s.Draft, ThemeEditTarget.GpuMain);

            Assert.Equal(backendBefore, s.Draft.Rendering!.Backend);
            Assert.Equal(revisionBefore, s.Revision);
            Assert.False(s.IsDirty);

            (byte[] payload, _) = s.BuildPayload();
            Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "玻璃.json")), payload);
        }

        [Fact]
        public void 编辑GPU参数_保存只写该键_其它未知键保留()
        {
            string json = LiquidStandard.Replace(
                "\"cornerRadius\": 20",
                "\"cornerRadius\": 20, \"futureParam\": { \"keep\": true }");
            ThemeDocumentSession s = Open("玻璃", json);

            ThemePropertyDescriptor corner = ThemePropertyCatalog
                .GetProperties(s.Draft, ThemeEditTarget.GpuMain)
                .Single(p => p.JsonPath == "rendering.parameters.cornerRadius");
            s.ApplyEdit(new ThemeFieldEdit(
                corner.JsonPath,
                corner.ToJson(42.0),
                theme => corner.Setter(theme, 42.0)));

            Assert.True(s.Save().Success);

            JsonNode saved = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "玻璃.json")))!;
            Assert.Equal(42, saved["rendering"]!["parameters"]!["cornerRadius"]!.GetValue<int>());
            Assert.True(saved["rendering"]!["parameters"]!["futureParam"]!["keep"]!.GetValue<bool>());
            Assert.Equal("gpu", saved["rendering"]!["backend"]!.GetValue<string>());
        }

        [Fact]
        public void 兼容外观六槽_写入与恢复默认_键消失()
        {
            ThemeDocumentSession s = Open("玻璃", LiquidStandard);
            var props = ThemePropertyCatalog.GetProperties(s.Draft, ThemeEditTarget.GpuFallback);
            ThemePropertyDescriptor accent = props.Single(p => p.JsonPath == "rendering.fallbackStyle.accent");

            s.ApplyEdit(new ThemeFieldEdit(
                accent.JsonPath,
                accent.ToJson("#8FB4D9"),
                theme => accent.Setter(theme, "#8FB4D9")));
            Assert.True(s.Save().Success);

            JsonNode saved = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "玻璃.json")))!;
            Assert.Equal("#8FB4D9", saved["rendering"]!["fallbackStyle"]!["accent"]!.GetValue<string>());
            Assert.True(s.HasOverride(accent.JsonPath));

            // 恢复默认 = 删除显式覆盖
            s.ApplyEdit(new ThemeFieldEdit(
                accent.JsonPath,
                (JsonNode?)null,
                theme => accent.Setter(theme, accent.DefaultGetter())));
            Assert.True(s.Save().Success);

            JsonNode after = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "玻璃.json")))!;
            Assert.Null(after["rendering"]!["fallbackStyle"]?["accent"]);
        }
    }
}
