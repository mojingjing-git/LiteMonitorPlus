using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 三款旗舰级 GPU 着色器主题自动化测试套件：
/// 1. NixieTube_Brass_GPU (辉光管与柴油朋克)
/// 2. CosmicAurora_Nebula_GPU (深空极光与流体)
/// 3. RetroCRT_Phosphor_GPU (复古冷战 CRT 终端)
///
/// 覆盖门禁 G-TH1 ~ G-TH4：
/// a) 文件存在性与反序列化 (version == 5, backend == "gpu", rendering.material 为有效枚举)。
/// b) rendering.Validate() 必须产出 0 个错误，非致命诊断为 0。
/// c) fallbackStyle 6 色齐备且均为不透明色 (A == 255)，满足 WCAG AA 可读性。
/// d) LegacyFallbackStyleResolver.BuildDerivedTheme(theme) 派生纯 CLR Legacy 主题强一致性。
/// e) ThemeExportService.ExportEffectImage / ThemeDocumentStore / Session 读写与往返保存。
/// f) Layout、Font、Color 结构与字段符合 ThemeConfig 契约。
/// </summary>
public sealed class FlagshipGpuThemeTests : IDisposable
{
    private static readonly string[] FlagshipThemes =
    [
        "NixieTube_Brass_GPU",
        "CosmicAurora_Nebula_GPU",
        "RetroCRT_Phosphor_GPU"
    ];

    private readonly string _tempDir;

    public FlagshipGpuThemeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LiteMonitor.Tests", "FlagshipGpuThemes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        ThemeDocumentStore.SetThemeDirForTests(null);
        ThemeDocumentStore.ResetRecoveryDir();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // 清理临时文件异常不覆盖测试结果
        }
    }

    private static string ResolveThemesDirectory()
    {
        // 1. 优先尝试测试运行根目录的 resources/themes
        string direct = Path.Combine(AppContext.BaseDirectory, "resources", "themes");
        if (Directory.Exists(direct) && File.Exists(Path.Combine(direct, "CyberPunk_GPU.json")))
        {
            return direct;
        }

        // 2. 逐级向上查找仓库根目录中的 resources/themes
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "resources", "themes");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "CyberPunk_GPU.json")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("未能定位到 resources/themes 目录。");
    }

    private static Theme LoadThemeFromFile(string themeName)
    {
        string themesDir = ResolveThemesDirectory();
        string path = Path.Combine(themesDir, $"{themeName}.json");
        Assert.True(File.Exists(path), $"主题文件不存在: {path}");

        string json = File.ReadAllText(path);
        Theme? theme = JsonSerializer.Deserialize<Theme>(json, ThemeManager.CreateSerializerOptions());
        Assert.NotNull(theme);
        theme.BuildFonts();
        return theme;
    }

    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(
            thread.Join(TimeSpan.FromMinutes(2)),
            "STA test thread did not terminate within two minutes.");

        if (captured != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    // ================================================================
    // A. 文件存在性与反序列化
    // ================================================================

    [Theory]
    [InlineData("NixieTube_Brass_GPU", "cyber_punk", "nixieBrass")]
    [InlineData("CosmicAurora_Nebula_GPU", "liquid_glass", "nebulaAurora")]
    [InlineData("RetroCRT_Phosphor_GPU", "cyber_punk", "crtPhosphor")]
    public void Theme_FileExists_AndDeserializesSuccessfully(
        string themeName,
        string expectedMaterial,
        string expectedVisualPreset)
    {
        Theme theme = LoadThemeFromFile(themeName);

        Assert.Equal(5, theme.Version);
        Assert.False(string.IsNullOrWhiteSpace(theme.Name));
        Assert.NotNull(theme.Rendering);
        Assert.Equal(ThemeRenderingConfig.CurrentVersion, theme.Rendering.Version);
        Assert.Equal(ThemeRenderingConfig.GpuBackend, theme.Rendering.Backend);
        Assert.Equal(expectedMaterial, theme.Rendering.Material);
        Assert.Equal(
            expectedVisualPreset,
            theme.Rendering.Parameters["visualPreset"].GetString());
    }

    // ================================================================
    // B. rendering.Validate() 必须产出 0 个错误，非致命诊断为 0
    // ================================================================

    [Theory]
    [InlineData("NixieTube_Brass_GPU")]
    [InlineData("CosmicAurora_Nebula_GPU")]
    [InlineData("RetroCRT_Phosphor_GPU")]
    public void Theme_RenderingValidate_ZeroErrorsAndZeroDiagnostics(string themeName)
    {
        Theme theme = LoadThemeFromFile(themeName);
        Assert.NotNull(theme.Rendering);

        IReadOnlyList<string> errors = theme.Rendering.Validate(out IReadOnlyList<string> diagnostics);

        Assert.True(errors.Count == 0, $"主题 {themeName} 存在 rendering 校验错误: {string.Join("; ", errors)}");
        Assert.True(diagnostics.Count == 0, $"主题 {themeName} 存在非致命诊断: {string.Join("; ", diagnostics)}");
    }

    // ================================================================
    // C. fallbackStyle 6 色齐备且均为不透明色 (A == 255)，满足 WCAG AA
    // ================================================================

    [Theory]
    [InlineData("NixieTube_Brass_GPU")]
    [InlineData("CosmicAurora_Nebula_GPU")]
    [InlineData("RetroCRT_Phosphor_GPU")]
    public void Theme_FallbackStyle_CompleteOpaqueAndReadable(string themeName)
    {
        Theme theme = LoadThemeFromFile(themeName);
        Assert.NotNull(theme.Rendering);
        ThemeFallbackStyle? style = theme.Rendering.FallbackStyle;

        Assert.NotNull(style);
        Assert.False(string.IsNullOrWhiteSpace(style.Background), "fallbackStyle.background 不能为空");
        Assert.False(string.IsNullOrWhiteSpace(style.PanelBackground), "fallbackStyle.panelBackground 不能为空");
        Assert.False(string.IsNullOrWhiteSpace(style.Border), "fallbackStyle.border 不能为空");
        Assert.False(string.IsNullOrWhiteSpace(style.Text), "fallbackStyle.text 不能为空");
        Assert.False(string.IsNullOrWhiteSpace(style.MutedText), "fallbackStyle.mutedText 不能为空");
        Assert.False(string.IsNullOrWhiteSpace(style.Accent), "fallbackStyle.accent 不能为空");

        // 验证支持的颜色文本格式
        Assert.True(LegacyFallbackStyleResolver.IsSupportedColorText(style.Background));
        Assert.True(LegacyFallbackStyleResolver.IsSupportedColorText(style.PanelBackground));
        Assert.True(LegacyFallbackStyleResolver.IsSupportedColorText(style.Border));
        Assert.True(LegacyFallbackStyleResolver.IsSupportedColorText(style.Text));
        Assert.True(LegacyFallbackStyleResolver.IsSupportedColorText(style.MutedText));
        Assert.True(LegacyFallbackStyleResolver.IsSupportedColorText(style.Accent));

        // 验证全部为不透明色 (A == 255)
        Color bg = ThemeManager.ParseColor(style.Background);
        Color pbg = ThemeManager.ParseColor(style.PanelBackground);
        Color border = ThemeManager.ParseColor(style.Border);
        Color text = ThemeManager.ParseColor(style.Text);
        Color muted = ThemeManager.ParseColor(style.MutedText);
        Color accent = ThemeManager.ParseColor(style.Accent);

        Assert.Equal(255, bg.A);
        Assert.Equal(255, pbg.A);
        Assert.Equal(255, border.A);
        Assert.Equal(255, text.A);
        Assert.Equal(255, muted.A);
        Assert.Equal(255, accent.A);

        // WCAG AA 可读性断言：正文对比度 >= 4.5，次要文字 >= 3.0
        double textContrast = ContrastRatio(style.Text, style.Background);
        double mutedContrast = ContrastRatio(style.MutedText, style.Background);

        Assert.True(textContrast >= 4.5d, $"主题 {themeName} 正文对比度不足 4.5: 实测 {textContrast:F2}");
        Assert.True(mutedContrast >= 3.0d, $"主题 {themeName} 次要文字对比度不足 3.0: 实测 {mutedContrast:F2}");
    }

    // ================================================================
    // D. LegacyFallbackStyleResolver.BuildDerivedTheme 强一致性
    // ================================================================

    [Theory]
    [InlineData("NixieTube_Brass_GPU")]
    [InlineData("CosmicAurora_Nebula_GPU")]
    [InlineData("RetroCRT_Phosphor_GPU")]
    public void Theme_BuildDerivedTheme_MatchesFallbackStyleAndSharesFonts(string themeName)
    {
        Theme theme = LoadThemeFromFile(themeName);
        Assert.NotNull(theme.Rendering?.FallbackStyle);
        ThemeFallbackStyle fallback = theme.Rendering.FallbackStyle;

        Theme derived = LegacyFallbackStyleResolver.BuildDerivedTheme(theme);

        Assert.NotNull(derived);
        Assert.NotNull(derived.Color);

        // 强一致性映射验证
        Assert.Equal(fallback.Background, derived.Color.Background);
        Assert.Equal(fallback.PanelBackground, derived.Color.GroupBackground);
        Assert.Equal(fallback.Border, derived.Color.Border);
        Assert.Equal(fallback.Text, derived.Color.TextPrimary);
        Assert.Equal(fallback.MutedText, derived.Color.TextSecondary);
        Assert.Equal(fallback.Accent, derived.Color.TextTitle);

        // 共享源主题 5 个 Font 实例所有权合同
        Assert.Same(theme.FontTitle, derived.FontTitle);
        Assert.Same(theme.FontGroup, derived.FontGroup);
        Assert.Same(theme.FontItem, derived.FontItem);
        Assert.Same(theme.FontValue, derived.FontValue);
        Assert.Same(theme.FontTaskbar, derived.FontTaskbar);
    }

    // ================================================================
    // E. ThemeExportService / ThemeDocumentStore / Session 往返与导出
    // ================================================================

    [Theory]
    [InlineData("NixieTube_Brass_GPU")]
    [InlineData("CosmicAurora_Nebula_GPU")]
    [InlineData("RetroCRT_Phosphor_GPU")]
    public void Theme_DocumentSession_RoundTripSaveAndExport(string themeName)
    {
        string themesDir = ResolveThemesDirectory();
        string sourcePath = Path.Combine(themesDir, $"{themeName}.json");
        string testThemesDir = Path.Combine(_tempDir, "themes");
        string testRecoveryDir = Path.Combine(_tempDir, "recovery");
        Directory.CreateDirectory(testThemesDir);
        Directory.CreateDirectory(testRecoveryDir);

        // 复制到隔离的测试目录
        string copiedPath = Path.Combine(testThemesDir, $"{themeName}.json");
        File.Copy(sourcePath, copiedPath, true);

        ThemeDocumentStore.SetThemeDirForTests(testThemesDir);
        ThemeDocumentStore.SetRecoveryDirForTests(testRecoveryDir);

        try
        {
            // 1. Session 打开与属性校验
            var session = ThemeDocumentSession.Open(themeName);
            Assert.NotNull(session);
            Assert.Equal(themeName, session.ThemeName);
            Assert.False(session.IsDirty);
            Assert.Equal(5, session.Draft.Version);

            // 2. 模拟编辑与保存往返
            string originalMaterial = session.Draft.Rendering?.Material ?? string.Empty;
            var rowHeightProp = FindProperty("layout.rowHeight");
            Assert.NotNull(rowHeightProp);
            double originalRowHeight = Convert.ToDouble(rowHeightProp.Getter(session.Draft));
            session.ApplyEdit(new ThemeFieldEdit(
                rowHeightProp.JsonPath,
                rowHeightProp.ToJson(originalRowHeight + 2),
                t => rowHeightProp.Setter(t, originalRowHeight + 2)));

            Assert.True(session.IsDirty);
            var saveResult = session.Save();
            Assert.True(saveResult.Success, saveResult.Error);
            Assert.False(session.IsDirty);

            // 3. 重新打开并验证保存后的内容
            var reopened = ThemeDocumentSession.Open(themeName);
            Assert.Equal(originalRowHeight + 2, Convert.ToDouble(rowHeightProp.Getter(reopened.Draft)));
            Assert.NotNull(reopened.Draft.Rendering);
            Assert.Equal(originalMaterial, reopened.Draft.Rendering.Material);

            // 4. 导出效果图验证 (竖向与横向)
            RunSta(() =>
            {
                string verticalPng = Path.Combine(_tempDir, $"{themeName}_vert.png");
                var vertResult = ThemeExportService.ExportEffectImage(
                    session,
                    verticalPng,
                    ImageRoutePreference.Classic,
                    ThemeExportImageOrientation.Vertical);

                Assert.True(vertResult.Success, vertResult.Error);
                Assert.True(File.Exists(verticalPng));
                Assert.True(new FileInfo(verticalPng).Length > 0);
                Assert.Equal(ThemeExportImageRoute.Fallback, vertResult.Route);

                string horizontalPng = Path.Combine(_tempDir, $"{themeName}_horiz.png");
                var horizResult = ThemeExportService.ExportEffectImage(
                    session,
                    horizontalPng,
                    ImageRoutePreference.Classic,
                    ThemeExportImageOrientation.Horizontal);

                Assert.True(horizResult.Success, horizResult.Error);
                Assert.True(File.Exists(horizontalPng));
                Assert.True(new FileInfo(horizontalPng).Length > 0);
                Assert.Equal(ThemeExportImageRoute.Fallback, horizResult.Route);
            });
        }
        finally
        {
            ThemeDocumentStore.SetThemeDirForTests(null);
            ThemeDocumentStore.ResetRecoveryDir();
        }
    }

    // ================================================================
    // F. Layout, Font, Color 各属性均符合 ThemeConfig 契约
    // ================================================================

    [Theory]
    [InlineData("NixieTube_Brass_GPU")]
    [InlineData("CosmicAurora_Nebula_GPU")]
    [InlineData("RetroCRT_Phosphor_GPU")]
    public void Theme_LayoutFontColor_ConformToContract(string themeName)
    {
        Theme theme = LoadThemeFromFile(themeName);

        // Layout 约束
        Assert.NotNull(theme.Layout);
        Assert.True(theme.Layout.RowHeight >= 20 && theme.Layout.RowHeight <= 100);
        Assert.True(theme.Layout.Padding >= 0 && theme.Layout.Padding <= 50);
        Assert.True(theme.Layout.GroupPadding >= 0 && theme.Layout.GroupPadding <= 50);
        Assert.True(theme.Layout.GroupSpacing >= 0 && theme.Layout.GroupSpacing <= 100);
        Assert.True(theme.Layout.ItemGap >= 0 && theme.Layout.ItemGap <= 30);
        Assert.True(theme.Layout.Scanline >= 0 && theme.Layout.Scanline <= 100);
        Assert.True(theme.Layout.Glow >= 0 && theme.Layout.Glow <= 100);
        Assert.True(theme.Layout.Grid >= 0 && theme.Layout.Grid <= 100);

        // Font 约束
        Assert.NotNull(theme.Font);
        Assert.False(string.IsNullOrWhiteSpace(theme.Font.Family));
        Assert.False(string.IsNullOrWhiteSpace(theme.Font.ValueFamily));
        Assert.True(theme.Font.Title > 0 && theme.Font.Title <= 36);
        Assert.True(theme.Font.Group > 0 && theme.Font.Group <= 36);
        Assert.True(theme.Font.Item > 0 && theme.Font.Item <= 36);
        Assert.True(theme.Font.Value > 0 && theme.Font.Value <= 36);

        // Color 约束
        Assert.NotNull(theme.Color);
        Assert.NotEqual(Color.Transparent, ThemeManager.ParseColor(theme.Color.Background));
        Assert.NotEqual(Color.Transparent, ThemeManager.ParseColor(theme.Color.TextTitle));
        Assert.NotEqual(Color.Transparent, ThemeManager.ParseColor(theme.Color.TextPrimary));
        Assert.NotEqual(Color.Transparent, ThemeManager.ParseColor(theme.Color.ValueSafe));
        Assert.NotEqual(Color.Transparent, ThemeManager.ParseColor(theme.Color.ValueWarn));
        Assert.NotEqual(Color.Transparent, ThemeManager.ParseColor(theme.Color.ValueCrit));
        Assert.NotEqual(Color.Transparent, ThemeManager.ParseColor(theme.Color.BarLow));
        Assert.NotEqual(Color.Transparent, ThemeManager.ParseColor(theme.Color.BarMid));
        Assert.NotEqual(Color.Transparent, ThemeManager.ParseColor(theme.Color.BarHigh));
    }

    // ================================================================
    // 工具方法
    // ================================================================

    [Fact]
    public void 预设下拉展开中切换选项_异步重建不崩溃()
    {
        // 回归：用户在下拉展开状态下切换预设（键盘导航/鼠标点击都会在展开中触发
        // SelectedIndexChanged）。结构变更的重建异步派发后，回调会 Dispose 全部行控件；
        // 若提交时下拉仍展开（键盘捕获未释放），Dispose 展开中的 ComboBox 会触发
        // 原生访问冲突导致整个进程闪退。提交路径必须先收起下拉再进入异步重建。
        RunSta(() =>
        {
            const string themeName = "EditableGpuTheme";
            string themeDir = Path.Combine(_tempDir, "studio-themes-dd");
            string recoveryDir = Path.Combine(_tempDir, "studio-recovery-dd");
            Directory.CreateDirectory(themeDir);
            Directory.CreateDirectory(recoveryDir);
            File.WriteAllText(
                Path.Combine(themeDir, themeName + ".json"),
                EditableGpuThemeJson,
                new UTF8Encoding(false));

            ThemeDocumentStore.SetThemeDirForTests(themeDir);
            ThemeDocumentStore.SetRecoveryDirForTests(recoveryDir);
            try
            {
                using var form = new ThemeStudioForm();
                form.Show();
                form.OpenThemeForTesting(themeName);
                form.SelectTargetForTesting(ThemeEditTarget.GpuMain);

                const string presetPath = "rendering.parameters.visualPreset";
                PropertyInspector inspector = form.InspectorForTesting;
                ComboBox combo =
                    Assert.IsType<ComboBox>(inspector.EditorForTesting(presetPath));

                // 模拟真实用户操作：展开下拉 → 展开中切换到命名预设。
                combo.DroppedDown = true;
                Assert.True(combo.DroppedDown, "前提：下拉已展开");
                combo.SelectedIndex = 1;

                // 泵消息：此时执行异步重建（Dispose 展开中的旧树）。
                Application.DoEvents();
                Application.DoEvents();

                ComboBox rebuilt =
                    Assert.IsType<ComboBox>(inspector.EditorForTesting(presetPath));
                Assert.Equal(
                    ThemeRenderingConfig.NebulaAuroraVisualPreset,
                    rebuilt.SelectedItem);
                Assert.False(rebuilt.DroppedDown, "重建后的下拉不得保持展开");
                Assert.Equal(
                    ThemeRenderingConfig.NebulaAuroraVisualPreset,
                    form.SessionForTesting!.Draft.Rendering!.Parameters["visualPreset"].GetString());
            }
            finally
            {
                ThemeDocumentStore.SetThemeDirForTests(null);
                ThemeDocumentStore.ResetRecoveryDir();
            }
        });
    }

    [Fact]
    public void ThemeStudioVisualPresetChoiceRebuildsPropertiesAcrossResetUndoAndRedo()
    {
        RunSta(() =>
        {
            const string themeName = "EditableGpuTheme";
            string themeDir = Path.Combine(_tempDir, "studio-themes");
            string recoveryDir = Path.Combine(_tempDir, "studio-recovery");
            Directory.CreateDirectory(themeDir);
            Directory.CreateDirectory(recoveryDir);
            File.WriteAllText(
                Path.Combine(themeDir, themeName + ".json"),
                EditableGpuThemeJson,
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(themeDir, "CaseNebulaTheme.json"),
                CaseVariantNebulaJson,
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(themeDir, "CaseToolbarTheme.json"),
                CaseVariantToolbarJson,
                new UTF8Encoding(false));

            ThemeDocumentStore.SetThemeDirForTests(themeDir);
            ThemeDocumentStore.SetRecoveryDirForTests(recoveryDir);
            try
            {
                using var form = new ThemeStudioForm();
                form.Show();
                form.OpenThemeForTesting(themeName);
                form.SelectTargetForTesting(ThemeEditTarget.GpuMain);

                const string presetPath = "rendering.parameters.visualPreset";
                PropertyInspector inspector = form.InspectorForTesting;
                ComboBox initial =
                    Assert.IsType<ComboBox>(inspector.EditorForTesting(presetPath));
                Assert.Equal(ThemeRenderingConfig.DefaultVisualPreset, initial.SelectedItem);
                Assert.NotNull(inspector.EditorForTesting("rendering.parameters.glassStyle"));
                Assert.Null(inspector.EditorForTesting("font.family"));

                TextBox continuous =
                    Assert.IsType<TextBox>(
                        inspector.EditorForTesting("rendering.parameters.cornerRadius"));
                continuous.Focus();
                continuous.SelectAll();
                continuous.SelectedText = "3";
                Assert.Same(
                    continuous,
                    inspector.EditorForTesting("rendering.parameters.cornerRadius"));
                Assert.True(continuous.Focused, "The first numeric edit must keep the TextBox focused.");
                continuous.Text = continuous.Text + "2";
                Assert.Same(
                    continuous,
                    inspector.EditorForTesting("rendering.parameters.cornerRadius"));
                Assert.True(continuous.Focused, "Continuous numeric editing must keep the same focused TextBox.");
                Assert.Equal("32", continuous.Text);

                initial.SelectedItem = ThemeRenderingConfig.NebulaAuroraVisualPreset;
                // 结构变更的重绑已改为异步派发（避免在 ComboBox 事件栈内销毁事件源控件）；
                // 泵一次消息让检查器完成重建后再断言新控件树。
                Application.DoEvents();

                ComboBox named =
                    Assert.IsType<ComboBox>(inspector.EditorForTesting(presetPath));
                Assert.Equal(ThemeRenderingConfig.NebulaAuroraVisualPreset, named.SelectedItem);
                Assert.Null(inspector.EditorForTesting("rendering.parameters.glassStyle"));
                Assert.NotNull(inspector.EditorForTesting("font.family"));
                Assert.NotNull(inspector.EditorForTesting("color.background"));

                Assert.True(inspector.ResetPropertyForTesting(presetPath));
                Application.DoEvents();
                ComboBox reset =
                    Assert.IsType<ComboBox>(inspector.EditorForTesting(presetPath));
                Assert.Equal(ThemeRenderingConfig.DefaultVisualPreset, reset.SelectedItem);
                Assert.NotNull(inspector.EditorForTesting("rendering.parameters.glassStyle"));
                Assert.Null(inspector.EditorForTesting("font.family"));

                form.UndoForTesting();
                ComboBox undo =
                    Assert.IsType<ComboBox>(inspector.EditorForTesting(presetPath));
                Assert.Equal(ThemeRenderingConfig.NebulaAuroraVisualPreset, undo.SelectedItem);
                Assert.NotNull(inspector.EditorForTesting("font.family"));
                Assert.Null(inspector.EditorForTesting("rendering.parameters.glassStyle"));

                form.RedoForTesting();
                ComboBox redo =
                    Assert.IsType<ComboBox>(inspector.EditorForTesting(presetPath));
                Assert.Equal(ThemeRenderingConfig.DefaultVisualPreset, redo.SelectedItem);
                Assert.Null(inspector.EditorForTesting("font.family"));
                Assert.NotNull(inspector.EditorForTesting("rendering.parameters.glassStyle"));

                using var caseForm = new ThemeStudioForm();
                caseForm.Show();
                caseForm.OpenThemeForTesting("CaseNebulaTheme");
                caseForm.SelectTargetForTesting(ThemeEditTarget.GpuMain);
                PropertyInspector caseNebulaInspector = caseForm.InspectorForTesting;
                ComboBox caseNebula =
                    Assert.IsType<ComboBox>(
                        caseNebulaInspector.EditorForTesting(presetPath));
                Assert.Equal(
                    ThemeRenderingConfig.NebulaAuroraVisualPreset,
                    caseNebula.SelectedItem);
                Assert.NotNull(caseNebulaInspector.EditorForTesting("font.family"));

                caseForm.OpenThemeForTesting("CaseToolbarTheme");
                caseForm.SelectTargetForTesting(ThemeEditTarget.GpuMain);
                PropertyInspector caseToolbarInspector = caseForm.InspectorForTesting;
                ComboBox caseToolbar =
                    Assert.IsType<ComboBox>(
                        caseToolbarInspector.EditorForTesting("rendering.parameters.glassStyle"));
                Assert.Equal(
                    ThemeRenderingConfig.FloatingToolbarGlassStyle,
                    caseToolbar.SelectedItem);
                Assert.NotNull(caseToolbarInspector.EditorForTesting("font.family"));
            }
            finally
            {
                ThemeDocumentStore.SetThemeDirForTests(null);
                ThemeDocumentStore.ResetRecoveryDir();
            }
        });
    }

    private static ThemePropertyDescriptor FindProperty(string path) =>
        System.Linq.Enumerable.Single(
            ThemePropertyCatalog.ClassicProperties,
            p => p.JsonPath == path);

    private const string EditableGpuThemeJson = """
    {
      "version": 5,
      "name": "EditableGpuTheme",
      "rendering": {
        "version": 1,
        "backend": "gpu",
        "material": "liquid_glass",
        "parameters": {
          "liveBackground": false
        },
        "fallback": {
          "backend": "legacy",
          "material": "legacy"
        }
      }
    }
    """;

    private const string CaseVariantNebulaJson = """
    {
      "version": 5,
      "name": "Case Nebula",
      "rendering": {
        "version": 1,
        "backend": "gpu",
        "material": "liquid_glass",
        "parameters": {
          "liveBackground": false,
          "visualPreset": "NebulaAurora"
        },
        "fallback": {
          "backend": "legacy",
          "material": "legacy"
        }
      }
    }
    """;

    private const string CaseVariantToolbarJson = """
    {
      "version": 5,
      "name": "Case Toolbar",
      "rendering": {
        "version": 1,
        "backend": "gpu",
        "material": "liquid_glass",
        "parameters": {
          "liveBackground": false,
          "glassStyle": "FloatingToolbar"
        },
        "fallback": {
          "backend": "legacy",
          "material": "legacy"
        }
      }
    }
    """;

    private static double ContrastRatio(string foreground, string background)
    {
        Color front = ThemeManager.ParseColor(foreground);
        Color back = ThemeManager.ParseColor(background);
        double light = Math.Max(RelativeLuminance(front), RelativeLuminance(back));
        double dark = Math.Min(RelativeLuminance(front), RelativeLuminance(back));
        return (light + 0.05d) / (dark + 0.05d);
    }

    private static double RelativeLuminance(Color color)
    {
        return 0.2126d * Channel(color.R)
             + 0.7152d * Channel(color.G)
             + 0.0722d * Channel(color.B);
    }

    private static double Channel(byte value)
    {
        double v = value / 255d;
        return v <= 0.03928d ? v / 12.92d : Math.Pow((v + 0.055d) / 1.055d, 2.4d);
    }
}
