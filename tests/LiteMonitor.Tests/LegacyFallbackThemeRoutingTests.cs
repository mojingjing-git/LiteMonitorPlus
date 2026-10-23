using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【LM-070 / R06】原生旧主题**不得**被套上 GPU 回退配色。
/// <para><b>缺陷：</b>GPU 路线下换到**无 <c>rendering</c> 块**的原生旧主题 ⇒
/// <c>LegacyFallbackStyleResolver.Resolve</c> 返回**中性默认值** ⇒
/// <c>LegacyFallbackStyleBridge.BuildDerivedTheme</c> 无条件覆盖 6 个色槽 ⇒
/// 旧主题自身配色**整组**丢失；后续换肤经 <c>UIController.RefreshLegacyFallbackTheme</c>
/// **继续**重算覆盖，直到重回 GPU 或重启。</para>
/// <para><b>主审裁决（计划 §4.8.1 (1)）：</b>判别式 = <c>theme.Rendering?.IsGpuRequested == true</c>；
/// GPU 主题**被迫回退** ⇒ 套 <c>rendering.fallbackStyle</c>；
/// **原生 legacy 主题** ⇒ **不得**套任何回退配色 ⇒ 直接用主题本身。</para>
/// <para><b>不依赖真实桌面 / GPU / 窗口：</b><c>UIController</c> 构造函数会启动硬件监控与 UI 定时器
/// ⇒ 用 <see cref="RuntimeHelpers.GetUninitializedObject"/> 造一个**不跑构造函数**的实例，
/// 只驱动 <c>RefreshLegacyFallbackTheme</c> / <c>CurrentRenderTheme</c> 这条**纯状态**路径。</para>
/// </summary>
public sealed class LegacyFallbackThemeRoutingTests
{
    // ================================================================
    // ① GPU 主题被迫回退 ⇒ 派生主题的 6 个色槽 == 该主题的 fallbackStyle
    //    （这一条锁住"该套的仍然要套"：LM-070 的修复**不得**误伤 GPU 回退路径）
    // ================================================================

    [Fact]
    public void GPU主题被迫回退_派生主题6个色槽等于该主题的fallbackStyle()
    {
        Theme gpuTheme = CreateGpuTheme();

        using ThemeManagerCurrentScope scope = new ThemeManagerCurrentScope(gpuTheme);
        UIController controller = CreateController();

        // 处于 legacy 回退态（派生覆盖已存在）
        SetLegacyFallbackTheme(
            controller,
            LegacyFallbackStyleBridge.BuildDerivedTheme(
                gpuTheme,
                LegacyFallbackStyleBridge.Resolve(gpuTheme.Rendering, out _)));

        RefreshLegacyFallbackTheme(controller, gpuTheme);

        Theme derived = GetLegacyFallbackTheme(controller);
        Assert.NotNull(derived);
        Assert.NotSame(gpuTheme, derived);

        // 6 个色槽 == 该 GPU 主题声明的 fallbackStyle
        Assert.Equal("#0A1017", derived.Color.Background);
        Assert.Equal("#121B24", derived.Color.GroupBackground);
        Assert.Equal("#345064", derived.Color.Border);
        Assert.Equal("#E8F7FA", derived.Color.TextPrimary);
        Assert.Equal("#91A7B2", derived.Color.TextSecondary);
        Assert.Equal("#48E6F2", derived.Color.TextTitle);
    }

    // ================================================================
    // ② 原生旧主题 ⇒ 渲染用主题**就是源主题本身**（同一实例），色槽未被中性默认值覆盖
    // ================================================================

    [Fact]
    public void 原生旧主题_渲染用主题就是源主题本身且色槽未被中性默认值覆盖()
    {
        Theme native = CreateNativeLegacyTheme();

        using ThemeManagerCurrentScope scope = new ThemeManagerCurrentScope(native);
        UIController controller = CreateController();

        // 从 GPU 回退态进入换肤（派生覆盖已存在 ⇒ 正是 R06 的触发前提）
        SetLegacyFallbackTheme(controller, CreateStaleDerivedOverride());

        RefreshLegacyFallbackTheme(controller, native);

        // 派生覆盖必须被清空 ⇒ 渲染用的就是主题本身（同一实例）
        Assert.Null(GetLegacyFallbackTheme(controller));
        Theme renderTheme = GetCurrentRenderTheme(controller);
        Assert.Same(native, renderTheme);
        Assert.Same(ThemeManager.Current, renderTheme);

        // 6 个色槽**未被**中性默认值覆盖
        Assert.NotEqual(LegacyFallbackStyleResolver.DefaultBackground, renderTheme.Color.Background);
        Assert.NotEqual(LegacyFallbackStyleResolver.DefaultPanelBackground, renderTheme.Color.GroupBackground);
        Assert.NotEqual(LegacyFallbackStyleResolver.DefaultBorder, renderTheme.Color.Border);
        Assert.NotEqual(LegacyFallbackStyleResolver.DefaultText, renderTheme.Color.TextPrimary);
        Assert.NotEqual(LegacyFallbackStyleResolver.DefaultMutedText, renderTheme.Color.TextSecondary);
        Assert.NotEqual(LegacyFallbackStyleResolver.DefaultAccent, renderTheme.Color.TextTitle);
    }

    // ================================================================
    // ③ 原生旧主题 ⇒ 6 个色槽与源主题逐个相等
    // ================================================================

    [Fact]
    public void 原生旧主题_6个色槽与源主题逐个相等()
    {
        Theme native = CreateNativeLegacyTheme();

        // 先把源主题的 6 个色槽取值记下来（比的是**值**，不是同一实例）
        string expectedBackground = native.Color.Background;
        string expectedPanel = native.Color.GroupBackground;
        string expectedBorder = native.Color.Border;
        string expectedText = native.Color.TextPrimary;
        string expectedMuted = native.Color.TextSecondary;
        string expectedTitle = native.Color.TextTitle;

        using ThemeManagerCurrentScope scope = new ThemeManagerCurrentScope(native);
        UIController controller = CreateController();
        SetLegacyFallbackTheme(controller, CreateStaleDerivedOverride());

        RefreshLegacyFallbackTheme(controller, native);

        Theme renderTheme = GetCurrentRenderTheme(controller);
        Assert.Equal(expectedBackground, renderTheme.Color.Background);
        Assert.Equal(expectedPanel, renderTheme.Color.GroupBackground);
        Assert.Equal(expectedBorder, renderTheme.Color.Border);
        Assert.Equal(expectedText, renderTheme.Color.TextPrimary);
        Assert.Equal(expectedMuted, renderTheme.Color.TextSecondary);
        Assert.Equal(expectedTitle, renderTheme.Color.TextTitle);
    }

    // ================================================================
    // ④ 换肤（从 GPU 回退态切到原生旧主题）⇒ 派生覆盖被**清空**
    // ================================================================

    [Fact]
    public void 换肤到原生旧主题_派生覆盖被清空不再用中性配色盖住旧主题()
    {
        Theme gpuTheme = CreateGpuTheme();
        Theme native = CreateNativeLegacyTheme();

        // 换肤后 ThemeManager.Current 指向新主题（原生旧主题）
        using ThemeManagerCurrentScope scope = new ThemeManagerCurrentScope(native);
        UIController controller = CreateController();

        // ① 先进入 GPU 回退态：派生覆盖来自 GPU 主题的 fallbackStyle
        SetLegacyFallbackTheme(
            controller,
            LegacyFallbackStyleBridge.BuildDerivedTheme(
                gpuTheme,
                LegacyFallbackStyleBridge.Resolve(gpuTheme.Rendering, out _)));
        Assert.NotNull(GetLegacyFallbackTheme(controller));

        // ② 换肤到原生旧主题
        RefreshLegacyFallbackTheme(controller, native);

        // ③ 派生覆盖被清空
        Assert.Null(GetLegacyFallbackTheme(controller));

        // ④ 不再被中性配色盖住旧主题
        Theme renderTheme = GetCurrentRenderTheme(controller);
        Assert.Same(native, renderTheme);
        Assert.Equal(native.Color.Background, renderTheme.Color.Background);
        Assert.NotEqual(LegacyFallbackStyleResolver.DefaultBackground, renderTheme.Color.Background);
    }

    // ================================================================
    // 护栏：两个落点（缺一不可）都必须先判别目标主题是不是 GPU 主题
    // ================================================================

    [Fact]
    public void 护栏_两个落点都必须先判别目标主题是不是GPU主题()
    {
        AssertUsesGpuThemeGuard("CompleteLegacyTransition");
        AssertUsesGpuThemeGuard("RefreshLegacyFallbackTheme");
    }

    // ---------------------------------------------------------------- 辅助

    private static void AssertUsesGpuThemeGuard(string methodName)
    {
        MethodInfo method = typeof(UIController).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(method != null, $"UIController.{methodName} 不存在 —— 护栏前提被破坏。");

        IlInspector.Disassembly body = IlInspector.Disassemble(method);

        int guardOffset = -1;
        int buildOffset = -1;
        foreach (IlInspector.IlInstruction instruction in body.Instructions)
        {
            if (instruction.Target is not MethodInfo called) continue;
            if (called.DeclaringType != typeof(LegacyFallbackStyleBridge)) continue;

            if (called.Name == "IsGpuTheme")
            {
                guardOffset = instruction.Offset;
            }
            else if (called.Name == "BuildDerivedTheme")
            {
                buildOffset = instruction.Offset;
            }
        }

        Assert.True(
            guardOffset >= 0,
            $"UIController.{methodName} 必须先用「目标主题**本身**是不是 GPU 主题」判别（LM-070）——" +
            "少了这一步，原生旧主题会被套上 GPU 回退的中性默认配色（R06）。");

        Assert.True(
            buildOffset < 0 || guardOffset < buildOffset,
            $"UIController.{methodName} 里判别必须**早于**派生回退主题的构建。");
    }

    /// <summary>
    /// 造一个**不跑构造函数**的 <see cref="UIController"/>：
    /// 构造函数会启动硬件监控 + UI 定时器 + <c>ApplyTheme</c>（需要真实窗口），
    /// 而本用例只驱动 <c>_legacyFallbackTheme</c> 这条**纯状态**路径。
    /// </summary>
    private static UIController CreateController()
    {
        return (UIController)RuntimeHelpers.GetUninitializedObject(typeof(UIController));
    }

    private static FieldInfo LegacyFallbackThemeField =>
        typeof(UIController).GetField(
            "_legacyFallbackTheme",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static MethodInfo RefreshLegacyFallbackThemeMethod =>
        typeof(UIController).GetMethod(
            "RefreshLegacyFallbackTheme",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(Theme) },
            null)!;

    private static PropertyInfo CurrentRenderThemeProperty =>
        typeof(UIController).GetProperty(
            "CurrentRenderTheme",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void SetLegacyFallbackTheme(UIController controller, Theme theme)
    {
        LegacyFallbackThemeField.SetValue(controller, theme);
    }

    private static Theme GetLegacyFallbackTheme(UIController controller)
    {
        return (Theme)LegacyFallbackThemeField.GetValue(controller)!;
    }

    private static void RefreshLegacyFallbackTheme(UIController controller, Theme theme)
    {
        RefreshLegacyFallbackThemeMethod.Invoke(controller, new object[] { theme });
    }

    private static Theme GetCurrentRenderTheme(UIController controller)
    {
        return (Theme)CurrentRenderThemeProperty.GetValue(controller)!;
    }

    /// <summary>临时替换 <see cref="ThemeManager.Current"/>（测试结束自动还原）。</summary>
    private sealed class ThemeManagerCurrentScope : IDisposable
    {
        private readonly Theme _original;

        internal ThemeManagerCurrentScope(Theme theme)
        {
            _original = ThemeManager.Current;
            Set(theme);
        }

        public void Dispose() => Set(_original);

        private static void Set(Theme theme)
        {
            PropertyInfo property = typeof(ThemeManager).GetProperty(
                "Current",
                BindingFlags.Static | BindingFlags.Public);
            Assert.True(property != null, "ThemeManager.Current 不存在 —— 护栏前提被破坏。");

            MethodInfo setter = property.GetSetMethod(nonPublic: true);
            Assert.True(setter != null, "ThemeManager.Current 的 setter 不存在 —— 护栏前提被破坏。");

            setter.Invoke(null, new object[] { theme });
        }
    }

    /// <summary>GPU 主题（带 <c>rendering.fallbackStyle</c> 兄弟块）。</summary>
    private static Theme CreateGpuTheme()
    {
        Theme theme = new Theme { Name = "Lm070GpuTheme" };
        theme.Rendering = new ThemeRenderingConfig
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.CyberPunkMaterial,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(false)
            }
        };
        theme.Rendering.FallbackStyle = new ThemeFallbackStyle
        {
            Background = "#0A1017",
            PanelBackground = "#121B24",
            Border = "#345064",
            Text = "#E8F7FA",
            MutedText = "#91A7B2",
            Accent = "#48E6F2"
        };
        return theme;
    }

    /// <summary>原生 legacy 主题（**无 <c>rendering</c> 块**）。</summary>
    private static Theme CreateNativeLegacyTheme()
    {
        return new Theme
        {
            Name = "Lm070NativeLegacyTheme",
            Rendering = null,
            Color = new ColorConfig
            {
                Background = "#101820",
                GroupBackground = "#1B2430",
                Border = "#33475E",
                TextPrimary = "#E8EEF5",
                TextSecondary = "#9FB0C3",
                TextTitle = "#F2C14E",
                ValueSafe = "#66FF99"
            }
        };
    }

    /// <summary>
    /// 一份"上一轮 GPU 回退"留下的派生覆盖（6 个色槽取中性默认值）。
    /// 用来复现 R06 的触发前提：**派生覆盖已存在**时换肤到原生旧主题。
    /// </summary>
    private static Theme CreateStaleDerivedOverride()
    {
        Theme source = CreateGpuTheme();
        return LegacyFallbackStyleBridge.BuildDerivedTheme(
            source,
            LegacyFallbackStyleBridge.Resolve(null, out _));
    }
}
