using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【LM-071 / R07】派生主题必须带上**运行期字体**。
/// <para><b>缺陷：</b><c>Theme</c> 的 5 个运行期字体是**字段**
/// （<c>FontTitle / FontGroup / FontItem / FontValue / FontTaskbar</c>），
/// 而 <c>LegacyFallbackStyleBridge.DeriveConfig</c> 用 <c>GetProperties</c> 只复制
/// **public 属性** ⇒ 派生主题的 5 个字体全部退回 <c>SystemFonts.CaptionFont</c> ⇒
/// <c>UIRenderer</c> / <c>HorizontalLayout</c> 用默认字体绘制（字号、字体、粗体、高 DPI 缩放全丢）。</para>
/// <para><b>主审裁决（计划 §4.8.1 (2)）：</b>**共享引用，不新建** ——
/// 派生主题显式复制源主题的 5 个字体**字段**（同一 <c>Font</c> 实例）；
/// 释放责任仍在源主题；派生主题**不得** <c>BuildFonts()</c> / <c>Scale()</c> / <c>DisposeFonts()</c>；
/// 旧主题的字体释放必须**晚于**派生覆盖的重算。</para>
/// <para><b>不依赖真实桌面 / GPU / 窗口：</b>用 <see cref="RuntimeHelpers.GetUninitializedObject"/>
/// 造一个**不跑构造函数**的 <c>UIController</c>，只驱动 <c>RefreshLegacyFallbackTheme</c> 这条纯状态路径；
/// 字体用**真实**的 <see cref="Font"/> 实例，"是否已被释放"用
/// <see cref="IsReleased"/>（读取已释放字体的度量会失败）判定 —— 不是只断言引用相等。</para>
/// </summary>
public sealed class DerivedThemeRuntimeFontTests
{
    // ================================================================
    // ① 派生主题的 5 个字体字段与源主题是同一实例
    // ================================================================

    [Fact]
    public void 派生主题的5个运行期字体字段与源主题是同一实例()
    {
        Theme source = CreateGpuThemeWithRuntimeFonts();

        Theme derived = LegacyFallbackStyleBridge.BuildDerivedTheme(source, CreateStyle());

        Assert.Same(source.FontTitle, derived.FontTitle);
        Assert.Same(source.FontGroup, derived.FontGroup);
        Assert.Same(source.FontItem, derived.FontItem);
        Assert.Same(source.FontValue, derived.FontValue);
        Assert.Same(source.FontTaskbar, derived.FontTaskbar);

        // ★ 派生主题不得退回 SystemFonts.CaptionFont（那正是 R07 的症状）
        Assert.NotSame(SystemFonts.CaptionFont, derived.FontTitle);
        Assert.NotSame(SystemFonts.CaptionFont, derived.FontGroup);
        Assert.NotSame(SystemFonts.CaptionFont, derived.FontItem);
        Assert.NotSame(SystemFonts.CaptionFont, derived.FontValue);
        Assert.NotSame(SystemFonts.CaptionFont, derived.FontTaskbar);
    }

    // ================================================================
    // ③ 派生主题**不得**释放源主题的字体
    // ================================================================

    [Fact]
    public void 派生主题构建后源主题的字体未被释放()
    {
        Theme source = CreateGpuThemeWithRuntimeFonts();

        Theme derived = LegacyFallbackStyleBridge.BuildDerivedTheme(source, CreateStyle());
        Assert.NotNull(derived);

        // 派生主题**共享**同一批 Font 实例 ⇒ 它一旦释放就是释放源主题的字体。
        // 这里断言构建过程**没有**释放它们（释放责任仍在源主题 / ThemeManager.Current）。
        Assert.False(IsReleased(source.FontTitle), "派生主题构建后源主题的 FontTitle 已被释放。");
        Assert.False(IsReleased(source.FontGroup), "派生主题构建后源主题的 FontGroup 已被释放。");
        Assert.False(IsReleased(source.FontItem), "派生主题构建后源主题的 FontItem 已被释放。");
        Assert.False(IsReleased(source.FontValue), "派生主题构建后源主题的 FontValue 已被释放。");
        Assert.False(IsReleased(source.FontTaskbar), "派生主题构建后源主题的 FontTaskbar 已被释放。");
    }

    // ================================================================
    // ② 换肤（旧主题 DisposeFonts() 之后）派生主题引用的字体**未被释放**
    // ================================================================

    [Fact]
    public void 换肤后派生主题引用的字体未被释放()
    {
        Theme oldTheme = CreateGpuThemeWithRuntimeFonts();
        Theme newTheme = CreateGpuThemeWithRuntimeFonts();

        using ThemeManagerCurrentScope scope = new ThemeManagerCurrentScope(newTheme);
        UIController controller = CreateController();

        // 处于 GPU 回退态：派生覆盖引用的是**旧主题**的字体
        SetLegacyFallbackTheme(
            controller,
            LegacyFallbackStyleBridge.BuildDerivedTheme(oldTheme, CreateStyle()));

        // 换肤：ApplyTheme 先重算派生覆盖（RefreshLegacyFallbackTheme(t)），
        //       **之后**才释放旧主题的字体。
        RefreshLegacyFallbackTheme(controller, newTheme);
        oldTheme.DisposeFonts();

        // 旧主题的字体确实被释放了（否则本用例就是空断言）
        Assert.True(IsReleased(oldTheme.FontItem), "旧主题的 FontItem 应当已被 DisposeFonts 释放。");
        Assert.True(IsReleased(oldTheme.FontTitle), "旧主题的 FontTitle 应当已被 DisposeFonts 释放。");

        // 派生覆盖现在引用**新主题**的字体，且**未被释放**（不是悬垂引用）
        Theme derived = GetLegacyFallbackTheme(controller);
        Assert.NotNull(derived);
        Assert.Same(newTheme.FontTitle, derived.FontTitle);
        Assert.Same(newTheme.FontItem, derived.FontItem);
        Assert.False(IsReleased(derived.FontTitle), "派生主题引用的 FontTitle 已被释放（悬垂引用）。");
        Assert.False(IsReleased(derived.FontItem), "派生主题引用的 FontItem 已被释放（悬垂引用）。");

        // 字体仍可用（度量可读）
        Assert.True(derived.FontItem.Height > 0);
        Assert.True(derived.FontTitle.Height > 0);
    }

    // ================================================================
    // 护栏 1：换肤必须**先**重算派生回退主题，**再**释放旧主题的字体
    // ================================================================

    [Fact]
    public void 护栏_换肤必须先重算派生回退主题再释放旧主题字体()
    {
        MethodInfo applyTheme = typeof(UIController).GetMethod(
            "ApplyTheme",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.True(applyTheme != null, "UIController.ApplyTheme 不存在 —— 护栏前提被破坏。");

        IlInspector.Disassembly body = IlInspector.Disassemble(applyTheme);

        int disposeOffset = -1;
        int refreshOffset = -1;
        foreach (IlInspector.IlInstruction instruction in body.Instructions)
        {
            if (instruction.Target is not MethodInfo called) continue;

            if (called.DeclaringType == typeof(Theme) && called.Name == "DisposeFonts")
            {
                disposeOffset = instruction.Offset;
            }
            else if (called.DeclaringType == typeof(UIController) &&
                     called.Name == "RefreshLegacyFallbackTheme")
            {
                refreshOffset = instruction.Offset;
            }
        }

        Assert.True(disposeOffset >= 0, "UIController.ApplyTheme 里找不到旧主题字体的释放点。");
        Assert.True(refreshOffset >= 0, "UIController.ApplyTheme 里找不到派生回退主题的重算点。");
        Assert.True(
            disposeOffset > refreshOffset,
            "旧主题的字体必须在派生回退主题重算**之后**才释放（LM-071）—— " +
            $"否则派生主题仍引用着已被释放的字体（悬垂引用）。当前 dispose@{disposeOffset} / refresh@{refreshOffset}。");
    }

    // ================================================================
    // 护栏 2：派生主题不得自己 BuildFonts / Scale / 释放源主题字体
    // ================================================================

    [Fact]
    public void 护栏_派生主题不得自己BuildFonts或Scale或释放字体()
    {
        MethodInfo build = typeof(LegacyFallbackStyleBridge).GetMethod(
            "BuildDerivedTheme",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(build != null, "LegacyFallbackStyleBridge.BuildDerivedTheme 不存在 —— 护栏前提被破坏。");

        IlInspector.Disassembly body = IlInspector.Disassemble(build);

        foreach (IlInspector.IlInstruction instruction in body.Instructions)
        {
            if (instruction.Target is not MethodInfo called) continue;
            if (called.DeclaringType != typeof(Theme)) continue;

            Assert.False(
                called.Name is "BuildFonts" or "Scale" or "DisposeFonts",
                "派生主题**不得**自己 BuildFonts()/Scale()/DisposeFonts()（LM-071）：" +
                $"BuildDerivedTheme 里出现了对 {called.Name} 的调用。" +
                "BuildFonts()/Scale() 会二次缩放（且纯 CLR 的协调器拿不到 dpi×user 系数），" +
                "DisposeFonts() 会释放源主题的字体（共享实例 ⇒ 悬垂引用）。");
        }
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>
    /// 判断一个运行期字体是否**已被释放**：已释放的字体读度量会失败
    /// （原生句柄已被销毁）⇒ 这是"字体仍可用"的**可观测**判据，而不是只比引用相等。
    /// </summary>
    private static bool IsReleased(Font font)
    {
        try
        {
            _ = font.Height;
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>造一个带 5 个**真实**运行期字体的 GPU 主题。</summary>
    private static Theme CreateGpuThemeWithRuntimeFonts()
    {
        Theme theme = new Theme { Name = "Lm071GpuTheme" };
        theme.Rendering = new LiteMonitor.Rendering.Configuration.ThemeRenderingConfig
        {
            Backend = LiteMonitor.Rendering.Configuration.ThemeRenderingConfig.GpuBackend,
            Material = LiteMonitor.Rendering.Configuration.ThemeRenderingConfig.CyberPunkMaterial,
            Parameters = new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>
            {
                ["liveBackground"] = System.Text.Json.JsonSerializer.SerializeToElement(false)
            }
        };

        // ★ 必须是**非系统字体**：Theme.DisposeFonts() 只释放非系统字体。
        theme.FontTitle = new Font("Segoe UI", 12f);
        theme.FontGroup = new Font("Segoe UI", 11f);
        theme.FontItem = new Font("Segoe UI", 10f);
        theme.FontValue = new Font("Consolas", 10f);
        theme.FontTaskbar = new Font("Segoe UI", 9f);
        return theme;
    }

    private static LiteMonitor.Rendering.LegacyFallbackStyle CreateStyle()
    {
        return new LiteMonitor.Rendering.LegacyFallbackStyle(
            Background: "#0A1017",
            PanelBackground: "#121B24",
            Border: "#345064",
            Text: "#E8F7FA",
            MutedText: "#91A7B2",
            Accent: "#48E6F2",
            Diagnostics: Array.Empty<string>());
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
}
