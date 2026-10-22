using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【GPU T05】<c>rendering.fallbackStyle</c>（legacy 回退样式兄弟块）的确定性单测。
///
/// ★ 覆盖合同 §9.5 / §9.6 的三类情形：缺省、非法值、保存往返；
///   并锁住两条硬依赖：HD-1（<c>rendering.fallback</c> 的 legacy/legacy 规则一字未改）
///   与 HD-3（新块在编辑器克隆 / 保存往返后不丢）。
///
/// ★ 不依赖真实桌面 / GPU：全部是纯数据 + 主题文件往返。
/// </summary>
public sealed class ThemeFallbackStyleTests : IDisposable
{
    private readonly string _dir;

    public ThemeFallbackStyleTests()
    {
        _dir = Path.Combine(
            Path.GetTempPath(),
            "LiteMonitor.Tests",
            "ThemeFallbackStyleTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }
        catch
        {
            // 临时文件清理不能盖掉用例结果
        }
    }

    // ================================================================
    // 1. 缺省
    // ================================================================

    [Fact]
    public void MissingFallbackStyle_UsesNeutralReadableDefaults()
    {
        LegacyFallbackStyle resolved = LegacyFallbackStyleResolver.Resolve(null);

        Assert.Equal(LegacyFallbackStyleResolver.DefaultBackground, resolved.Background);
        Assert.Equal(LegacyFallbackStyleResolver.DefaultPanelBackground, resolved.PanelBackground);
        Assert.Equal(LegacyFallbackStyleResolver.DefaultBorder, resolved.Border);
        Assert.Equal(LegacyFallbackStyleResolver.DefaultText, resolved.Text);
        Assert.Equal(LegacyFallbackStyleResolver.DefaultMutedText, resolved.MutedText);
        Assert.Equal(LegacyFallbackStyleResolver.DefaultAccent, resolved.Accent);
        Assert.Empty(resolved.Diagnostics);
        Assert.False(resolved.HasDiagnostics);

        // 缺省必须是"可读"的：正文对比度 ≥ 4.5，次要文字 ≥ 3.0
        Assert.True(
            ContrastRatio(resolved.Text, resolved.Background) >= 4.5d,
            $"默认正文对比度不足：{ContrastRatio(resolved.Text, resolved.Background):F2}");
        Assert.True(
            ContrastRatio(resolved.MutedText, resolved.Background) >= 3.0d,
            $"默认次要文字对比度不足：{ContrastRatio(resolved.MutedText, resolved.Background):F2}");
    }

    [Fact]
    public void ThemeWithoutFallbackStyleBlock_ResolvesToDefaultsWithoutDiagnostics()
    {
        ThemeRenderingConfig config = JsonSerializer.Deserialize<ThemeRenderingConfig>(
            """
            {
              "version": 1,
              "backend": "gpu",
              "material": "cyber_punk",
              "parameters": { "liveBackground": false },
              "fallback": { "backend": "legacy", "material": "legacy" }
            }
            """)!;

        Assert.Null(config.FallbackStyle);

        LegacyFallbackStyle resolved = LegacyFallbackStyleResolver.Resolve(config);

        Assert.Equal(LegacyFallbackStyleResolver.DefaultBackground, resolved.Background);
        Assert.Equal(LegacyFallbackStyleResolver.DefaultText, resolved.Text);
        Assert.Empty(resolved.Diagnostics);
    }

    // ================================================================
    // 2. 非法值
    // ================================================================

    [Fact]
    public void InvalidColorValue_IsDiagnosedAndFallsBackToDefault_WithoutInvalidatingTheme()
    {
        ThemeRenderingConfig config = CreateGpuConfiguration();
        config.FallbackStyle = new ThemeFallbackStyle
        {
            Background = "not-a-color",
            Text = "#F0F0F0"
        };

        // 非法值是**非致命**问题：不得进入 Validate() 的错误列表（否则整份主题被判不可用）
        Assert.Empty(config.Validate());

        // 但 Validate() 的扩展通道必须给出诊断（合同 §5.3 规则 1：错误列表与诊断分离）
        config.Validate(out IReadOnlyList<string> channelDiagnostics);
        Assert.Contains(
            channelDiagnostics,
            d => d.StartsWith(LegacyFallbackStyleResolver.InvalidStyleDiagnosticCode, StringComparison.Ordinal)
                 && d.Contains("background", StringComparison.Ordinal));

        IReadOnlyList<string> diagnostics = config.ValidateFallbackStyle();
        Assert.Contains(
            diagnostics,
            d => d.StartsWith(LegacyFallbackStyleResolver.InvalidStyleDiagnosticCode, StringComparison.Ordinal)
                 && d.Contains("background", StringComparison.Ordinal));

        LegacyFallbackStyle resolved = LegacyFallbackStyleResolver.Resolve(config);

        Assert.Equal(LegacyFallbackStyleResolver.DefaultBackground, resolved.Background);
        Assert.Equal("#F0F0F0", resolved.Text);
        Assert.True(resolved.HasDiagnostics);
    }

    [Fact]
    public void NonStringFallbackStyleValue_IsIgnoredAndDiagnosed_AndThemeStillLoads()
    {
        const string json =
            """
            {
              "version": 1,
              "backend": "gpu",
              "material": "cyber_punk",
              "parameters": { "liveBackground": false },
              "fallback": { "backend": "legacy", "material": "legacy" },
              "fallbackStyle": {
                "background": 123,
                "text": "#FFFFFF",
                "futureKey": "keep-me"
              }
            }
            """;

        // 非字符串值不得让整份主题加载失败
        ThemeRenderingConfig config = JsonSerializer.Deserialize<ThemeRenderingConfig>(json)!;

        Assert.NotNull(config.FallbackStyle);
        Assert.Null(config.FallbackStyle!.Background);
        Assert.Equal("#FFFFFF", config.FallbackStyle.Text);
        Assert.Equal("keep-me", config.FallbackStyle.ExtensionData["futureKey"].GetString());

        IReadOnlyList<string> diagnostics = config.ValidateFallbackStyle();
        Assert.Contains(
            diagnostics,
            d => d.StartsWith(LegacyFallbackStyleResolver.InvalidStyleDiagnosticCode, StringComparison.Ordinal)
                 && d.Contains("background", StringComparison.Ordinal));

        LegacyFallbackStyle resolved = LegacyFallbackStyleResolver.Resolve(config);
        Assert.Equal(LegacyFallbackStyleResolver.DefaultBackground, resolved.Background);
        Assert.Equal("#FFFFFF", resolved.Text);
    }

    [Fact]
    public void UnsupportedColorFormats_AreRejected()
    {
        Assert.False(LegacyFallbackStyleResolver.IsSupportedColorText("#12345"));
        Assert.False(LegacyFallbackStyleResolver.IsSupportedColorText("#GGGGGG"));
        Assert.False(LegacyFallbackStyleResolver.IsSupportedColorText("rgba(1,2,3)"));
        Assert.False(LegacyFallbackStyleResolver.IsSupportedColorText("rgba(300,0,0,1)"));
        Assert.False(LegacyFallbackStyleResolver.IsSupportedColorText("rgba(0,0,0,2)"));
        Assert.False(LegacyFallbackStyleResolver.IsSupportedColorText("definitely-not-a-color"));

        Assert.True(LegacyFallbackStyleResolver.IsSupportedColorText("#0A1017"));
        Assert.True(LegacyFallbackStyleResolver.IsSupportedColorText("#800A1017"));
        Assert.True(LegacyFallbackStyleResolver.IsSupportedColorText("rgba(22,42,66,0.55)"));
        Assert.True(LegacyFallbackStyleResolver.IsSupportedColorText("CornflowerBlue"));
    }

    // ================================================================
    // 3. 编辑器克隆 / 取消往返（字段保留路径）
    // ================================================================

    [Fact]
    public void EditorCloneAndRestore_PreserveFallbackStyleBlock()
    {
        Theme original = CreateGpuTheme();

        Theme snapshot = AppearanceThemeConfig.CloneConfig(original);

        Assert.NotNull(snapshot.Rendering!.FallbackStyle);
        Assert.NotSame(original.Rendering!.FallbackStyle, snapshot.Rendering.FallbackStyle);
        Assert.Equal("#0A1017", snapshot.Rendering.FallbackStyle!.Background);
        Assert.Equal("#E8F7FA", snapshot.Rendering.FallbackStyle.Text);
        Assert.Equal(
            "keep-me",
            snapshot.Rendering.FallbackStyle.ExtensionData["futureKey"].GetString());

        // 改快照不得影响原主题
        snapshot.Rendering.FallbackStyle.Background = "#000000";
        Assert.Equal("#0A1017", original.Rendering!.FallbackStyle!.Background);

        // 取消（就地恢复）后该块仍在
        AppearanceThemeConfig.RestoreConfig(original, snapshot);
        Assert.NotNull(original.Rendering!.FallbackStyle);
        Assert.Equal("#000000", original.Rendering.FallbackStyle!.Background);
    }

    [Fact]
    public void SaveRoundTrip_PreservesFallbackStyleAndUnknownKeys()
    {
        Theme theme = CreateGpuTheme();

        ThemeFileService.SaveTheme(_dir, "RoundTrip", theme);
        Theme loaded = ThemeFileService.LoadTheme(_dir, "RoundTrip");

        Assert.NotNull(loaded.Rendering!.FallbackStyle);
        Assert.Equal("#0A1017", loaded.Rendering.FallbackStyle!.Background);
        Assert.Equal("#121B24", loaded.Rendering.FallbackStyle.PanelBackground);
        Assert.Equal("#345064", loaded.Rendering.FallbackStyle.Border);
        Assert.Equal("#E8F7FA", loaded.Rendering.FallbackStyle.Text);
        Assert.Equal("#91A7B2", loaded.Rendering.FallbackStyle.MutedText);
        Assert.Equal("#48E6F2", loaded.Rendering.FallbackStyle.Accent);
        Assert.Equal(
            "keep-me",
            loaded.Rendering.FallbackStyle.ExtensionData["futureKey"].GetString());
        Assert.Empty(loaded.Rendering.Validate());
    }

    [Fact]
    public void ThemeWithoutFallbackStyle_DoesNotWriteFallbackStyleKey()
    {
        Theme theme = CreateGpuTheme();
        theme.Rendering!.FallbackStyle = null;

        string json = JsonSerializer.Serialize(theme, ThemeManager.CreateSerializerOptions());

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement rendering = FindProperty(document.RootElement, "Rendering");

        // 结构化判定：旧主题保存后不得多出 fallbackStyle 键（像素输出与保存结果不变）
        foreach (JsonProperty property in rendering.EnumerateObject())
        {
            Assert.False(
                string.Equals(property.Name, "fallbackStyle", StringComparison.OrdinalIgnoreCase),
                $"旧主题不应写出 fallbackStyle 键，实际出现：{property.Name}");
        }
    }

    // ================================================================
    // 4. 既有硬依赖不被破坏
    // ================================================================

    [Fact]
    public void LegacyFallbackDictionaryRule_RemainsUnchanged()
    {
        ThemeRenderingConfig config = CreateGpuConfiguration();

        // 缺省即 legacy/legacy ⇒ 无错
        Assert.Empty(config.Validate());

        // 仍强制 legacy/legacy（HD-1 一字未改）
        config.Fallback["backend"] = JsonSerializer.SerializeToElement("gpu");
        Assert.Contains(
            config.Validate(),
            error => error.Contains("rendering.fallback must select legacy/legacy.", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_DoesNotMutateTheme()
    {
        ThemeRenderingConfig config = CreateGpuConfiguration();
        config.FallbackStyle = new ThemeFallbackStyle { Background = "#0A1017" };

        LegacyFallbackStyleResolver.Resolve(config);

        Assert.Equal("#0A1017", config.FallbackStyle!.Background);
        Assert.Equal("gpu", config.Backend);
        Assert.Equal(ThemeRenderingConfig.CyberPunkMaterial, config.Material);
    }

    // ================================================================
    // 5. 三份内置 GPU 主题的回退配置
    // ================================================================

    [Theory]
    [InlineData("CyberPunk_GPU.json")]
    [InlineData("LiquidGlass_GPU.json")]
    [InlineData("LiquidGlass_Toolbar_GPU.json")]
    public void PackagedGpuThemes_DeclareResolvableAndReadableFallbackStyle(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "resources", "themes", fileName);
        Theme theme = JsonSerializer.Deserialize<Theme>(
            File.ReadAllText(path),
            ThemeManager.CreateSerializerOptions())!;

        Assert.NotNull(theme.Rendering);
        Assert.NotNull(theme.Rendering!.FallbackStyle);

        // 原 GPU 外观不受影响
        Assert.Equal(ThemeRenderingConfig.GpuBackend, theme.Rendering.Backend);
        Assert.Empty(theme.Rendering.Validate());

        LegacyFallbackStyle resolved = LegacyFallbackStyleResolver.Resolve(theme.Rendering);

        Assert.Empty(resolved.Diagnostics);
        Assert.True(
            ContrastRatio(resolved.Text, resolved.Background) >= 4.5d,
            $"{fileName} 正文对比度不足：{ContrastRatio(resolved.Text, resolved.Background):F2}");
        Assert.True(
            ContrastRatio(resolved.MutedText, resolved.Background) >= 3.0d,
            $"{fileName} 次要文字对比度不足：{ContrastRatio(resolved.MutedText, resolved.Background):F2}");
    }

    // ================================================================
    // 辅助
    // ================================================================

    private static ThemeRenderingConfig CreateGpuConfiguration()
    {
        return new ThemeRenderingConfig
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.CyberPunkMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(false)
            }
        };
    }

    private static Theme CreateGpuTheme()
    {
        Theme theme = new Theme { Name = "GpuFallbackProbe", Version = 5 };
        theme.Rendering = CreateGpuConfiguration();
        theme.Rendering.FallbackStyle = new ThemeFallbackStyle
        {
            Background = "#0A1017",
            PanelBackground = "#121B24",
            Border = "#345064",
            Text = "#E8F7FA",
            MutedText = "#91A7B2",
            Accent = "#48E6F2"
        };
        theme.Rendering.FallbackStyle.ExtensionData["futureKey"] =
            JsonSerializer.SerializeToElement("keep-me");
        return theme;
    }

    /// <summary>按属性名（大小写不敏感）取 JSON 子对象。</summary>
    private static JsonElement FindProperty(JsonElement parent, string name)
    {
        foreach (JsonProperty property in parent.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Expected property '{name}' in {parent.GetRawText()}.");
    }

    /// <summary>WCAG 相对亮度对比度。</summary>
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
