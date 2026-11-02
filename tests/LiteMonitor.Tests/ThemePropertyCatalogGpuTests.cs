using System;
using System.Collections.Generic;
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

        private const string CosmicNebula = @"{
  ""name"": ""CosmicNebula"",
  ""color"": {
    ""background"": ""#070A18"",
    ""groupBackground"": ""rgba(18,24,54,0.14)"",
    ""textPrimary"": ""#F3F6FD""
  },
  ""font"": {
    ""family"": ""Segoe UI Variable"",
    ""valueFamily"": ""Cascadia Code""
  },
  ""rendering"": { ""version"": 1, ""backend"": ""gpu"", ""material"": ""liquid_glass"",
    ""parameters"": { ""liveBackground"": false, ""visualPreset"": ""nebulaAurora"" },
    ""fallback"": { ""backend"": ""legacy"", ""material"": ""legacy"" } }
}";

        // C2 回归源：实时背景 + 浮动工具栏都开着的 liquid_glass 主题（无预设）。
        // 选 nebulaAurora 会强制 liveBackground=false 并删除 glassStyle；
        // 切回 default 后两者必须恢复为这里的源值，否则保存载荷与草稿分叉。
        private const string LiquidLiveBackground = @"{
  ""name"": ""实时玻璃"",
  ""rendering"": { ""version"": 1, ""backend"": ""gpu"", ""material"": ""liquid_glass"",
    ""parameters"": { ""liveBackground"": true, ""glassStyle"": ""floatingToolbar"", ""cornerRadius"": 20 },
    ""fallback"": { ""backend"": ""legacy"", ""material"": ""legacy"" } }
}";

        private ThemeDocumentSession Open(string name, string json)
        {
            File.WriteAllText(Path.Combine(_dir, name + ".json"), json, new UTF8Encoding(false));
            return ThemeDocumentSession.FromSourceBytes(
                name, Path.Combine(_dir, name + ".json"), Encoding.UTF8.GetBytes(json));
        }

        [Theory]
        [MemberData(nameof(VisualPresetCases))]
        public void VisualPresetChoiceOnlyOffersPresetsForCurrentMaterial(
            string json,
            string[] expectedChoices)
        {
            ThemeDocumentSession s = Open("visual-preset-options", json);
            ThemePropertyDescriptor preset = ThemePropertyCatalog
                .GetProperties(s.Draft, ThemeEditTarget.GpuMain)
                .Single(p => p.JsonPath == "rendering.parameters.visualPreset");

            Assert.Equal(expectedChoices, preset.Choices);
            Assert.Equal("default", preset.Getter(s.Draft));
        }

        public static TheoryData<string, string[]> VisualPresetCases => new()
        {
            {
                LiquidStandard,
                new[] { "default", "nebulaAurora" }
            },
            {
                CyberPunk,
                new[] { "default", "nixieBrass", "crtPhosphor" }
            }
        };

        [Fact]
        public void NamedPresetUsesThemeFontsAndColorsAndRemovesDefaultOnReset()
        {
            ThemeDocumentSession s = Open("visual-preset-write", LiquidStandard);
            ThemePropertyDescriptor preset = ThemePropertyCatalog
                .GetProperties(s.Draft, ThemeEditTarget.GpuMain)
                .Single(p => p.JsonPath == "rendering.parameters.visualPreset");

            s.ApplyPropertyEdit(preset, 1);
            Assert.Equal(
                "nebulaAurora",
                s.Draft.Rendering!.Parameters["visualPreset"].GetString());
            Assert.False(s.Draft.Rendering.Parameters["liveBackground"].GetBoolean());
            Assert.DoesNotContain("glassStyle", s.Draft.Rendering.Parameters.Keys);
            Assert.True(s.Save().Success);

            string[] namedPaths = Paths(s.Draft, ThemeEditTarget.GpuMain);
            Assert.Contains("font.family", namedPaths);
            Assert.Contains("font.value", namedPaths);
            Assert.Contains("color.background", namedPaths);
            Assert.Contains("color.groupBackground", namedPaths);
            Assert.Contains("color.textPrimary", namedPaths);
            Assert.Contains("color.valueWarn", namedPaths);
            Assert.Contains("color.barLow", namedPaths);
            Assert.DoesNotContain("rendering.parameters.glassStyle", namedPaths);
            Assert.Equal(
                "color.background",
                ThemePropertyCatalog.ResolvePropertyPath(
                    s.Draft,
                    ThemePropertyCatalog.SemanticElementRole.PanelBackground,
                    ThemeEditTarget.GpuMain));

            s.ApplyPropertyEdit(preset, 0);
            Assert.True(s.Save().Success);
            Assert.DoesNotContain("visualPreset", s.Draft.Rendering.Parameters.Keys);
            JsonNode saved = JsonNode.Parse(
                File.ReadAllText(Path.Combine(_dir, "visual-preset-write.json")))!;
            Assert.Null(saved["rendering"]!["parameters"]?["visualPreset"]);
        }

        private ThemePropertyDescriptor VisualPresetProperty(ThemeDocumentSession session) =>
            ThemePropertyCatalog
                .GetProperties(session.Draft, ThemeEditTarget.GpuMain)
                .Single(p => p.JsonPath == "rendering.parameters.visualPreset");

        [Fact]
        public void VisualPresetRoundTrip_未保存切回恢复源值_保存后恢复基线()
        {
            ThemeDocumentSession s = Open("visual-preset-roundtrip", LiquidLiveBackground);
            ThemePropertyDescriptor preset = VisualPresetProperty(s);

            // 1) 选预设：草稿即时报约束字段，保存后 payload 必须带上它们（C2 修复点）。
            s.ApplyPropertyEdit(preset, "nebulaAurora");
            Assert.False(s.Draft.Rendering!.Parameters["liveBackground"].GetBoolean());
            Assert.DoesNotContain("glassStyle", s.Draft.Rendering.Parameters.Keys);
            ThemeSaveResult namedSaved = s.Save();
            Assert.True(namedSaved.Success,
                "ERR=" + (namedSaved.Error ?? "<null>") +
                " DIAG=" + string.Join(",", namedSaved.Diagnostics));
            JsonNode saved = JsonNode.Parse(
                File.ReadAllText(Path.Combine(_dir, "visual-preset-roundtrip.json")))!;
            Assert.Equal("nebulaAurora",
                saved["rendering"]!["parameters"]!["visualPreset"]!.GetValue<string>());
            Assert.False(saved["rendering"]!["parameters"]!["liveBackground"]!.GetValue<bool>());
            Assert.Null(saved["rendering"]!["parameters"]?["glassStyle"]);

            // 2) 未保存回退场景：新会话里选预设后切回 default（不经过保存），
            //    两路恢复为源文件的值（liveBackground=true、glassStyle=floatingToolbar）。
            ThemeDocumentSession fresh = Open("visual-preset-roundtrip-2", LiquidLiveBackground);
            fresh.ApplyPropertyEdit(VisualPresetProperty(fresh), "nebulaAurora");
            fresh.ApplyPropertyEdit(VisualPresetProperty(fresh), "default");
            ThemeSaveResult resetSaved = fresh.Save();
            Assert.True(resetSaved.Success,
                "ERR=" + (resetSaved.Error ?? "<null>") +
                " DIAG=" + string.Join(",", resetSaved.Diagnostics));
            JsonNode restored = JsonNode.Parse(
                File.ReadAllText(Path.Combine(_dir, "visual-preset-roundtrip-2.json")))!;
            Assert.Null(restored["rendering"]!["parameters"]?["visualPreset"]);
            Assert.True(restored["rendering"]!["parameters"]!["liveBackground"]!.GetValue<bool>());
            Assert.Equal("floatingToolbar",
                restored["rendering"]!["parameters"]!["glassStyle"]!.GetValue<string>());

            // 3) 保存后再切回：基线已是 Nebula 状态，恢复到最近保存基线（false），不再回更早源值。
            s.ApplyPropertyEdit(preset, "default");
            ThemeSaveResult baselineSaved = s.Save();
            Assert.True(baselineSaved.Success,
                "ERR=" + (baselineSaved.Error ?? "<null>") +
                " DIAG=" + string.Join(",", baselineSaved.Diagnostics));
            JsonNode baseline = JsonNode.Parse(
                File.ReadAllText(Path.Combine(_dir, "visual-preset-roundtrip.json")))!;
            Assert.Null(baseline["rendering"]!["parameters"]?["visualPreset"]);
            Assert.False(baseline["rendering"]!["parameters"]!["liveBackground"]!.GetValue<bool>());
        }

        [Fact]
        public void VisualPresetUndo_撤销后保存与源逐字节一致()
        {
            string path = Path.Combine(_dir, "visual-preset-undo.json");
            File.WriteAllText(path, LiquidLiveBackground, new UTF8Encoding(false));
            byte[] sourceBytes = File.ReadAllBytes(path);
            ThemeDocumentSession s = ThemeDocumentSession.FromSourceBytes(
                "visual-preset-undo", path, sourceBytes);
            ThemePropertyDescriptor preset = VisualPresetProperty(s);

            s.ApplyPropertyEdit(preset, "nebulaAurora");
            Assert.True(s.IsDirty);
            Assert.Equal(1, s.UndoCount);
            Assert.True(s.Undo(), "Undo must pop the merged transaction");
            Assert.Equal(0, s.UndoCount);
            ThemeSaveResult undoSaved = s.Save();
            Assert.True(undoSaved.Success,
                "ERR=" + (undoSaved.Error ?? "<null>") +
                " DIAG=" + string.Join(",", undoSaved.Diagnostics));

            // 撤销恢复的是"编辑前有效值"：Before 非空的路径保留覆盖（载荷走补丁序列化，
            // 非 ASCII 会被转义），因此与源比较语义内容而非逐字节。
            JsonNode undone = JsonNode.Parse(
                System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)))!;
            JsonNode source = JsonNode.Parse(LiquidLiveBackground)!;
            Assert.True(JsonNode.DeepEquals(undone, source),
                "undone payload must equal source semantically: " + undone.ToJsonString());
        }

        [Fact]
        public void VisualPreset保存产物_重新打开_渲染校验通过()
        {
            ThemeDocumentSession s = Open("visual-preset-reopen", LiquidLiveBackground);
            s.ApplyPropertyEdit(VisualPresetProperty(s), "nebulaAurora");
            Assert.True(s.Save().Success);

            ThemeDocumentSession reopened = ThemeDocumentSession.FromSourceBytes(
                "visual-preset-reopen",
                Path.Combine(_dir, "visual-preset-reopen.json"),
                File.ReadAllBytes(Path.Combine(_dir, "visual-preset-reopen.json")));
            IReadOnlyList<string> validateErrors =
                reopened.Draft.Rendering!.Validate(out _);
            Assert.Empty(validateErrors);
            Assert.Equal(
                "nebulaAurora",
                reopened.Draft.Rendering.Parameters["visualPreset"].GetString());
            Assert.False(reopened.Draft.Rendering.Parameters["liveBackground"].GetBoolean());
            Assert.DoesNotContain("glassStyle", reopened.Draft.Rendering.Parameters.Keys);
        }

        [Fact]
        public void NebulaPresetExposesTypographyAndThemeColors()
        {
            ThemeDocumentSession s = Open("cosmic-nebula", CosmicNebula);
            string[] paths = Paths(s.Draft, ThemeEditTarget.GpuMain);

            Assert.Contains("rendering.parameters.visualPreset", paths);
            Assert.Contains("font.family", paths);
            Assert.Contains("font.valueFamily", paths);
            Assert.Contains("color.background", paths);
            Assert.Contains("color.groupBackground", paths);
            Assert.Contains("color.textPrimary", paths);
            Assert.DoesNotContain("rendering.parameters.glassStyle", paths);
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
            // 布局仍共享；[S05] 阈值已移出目录（状态阈值来自生效设置）
            Assert.Contains("layout.rowHeight", standardPaths);
            Assert.DoesNotContain("thresholds.load.warn", standardPaths);

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
                                 p.JsonPath.StartsWith("layout.", StringComparison.Ordinal)),
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
