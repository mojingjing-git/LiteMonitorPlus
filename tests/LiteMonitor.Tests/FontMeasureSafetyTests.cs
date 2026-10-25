using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【F12】字体生命周期与 DPI 测量安全 —— #574（<c>Parameter is not valid.</c>）的**已知结构风险**。
///
/// <para><b>取证事实（本文件里的用例逐条复核过，不是抄来的结论）：</b></para>
/// <list type="number">
/// <item><c>Font.GetHeight(Graphics)</c> 在 Graphics 已释放时抛 <c>ArgumentException</c>（ParamName = "graphics"）。</item>
/// <item><c>Font.GetHeight(Graphics)</c> 与 <c>Font.GetHeight(float dpi)</c> 在**同一 DPI 下逐位相等**。</item>
/// <item><c>Font.Height</c> / <c>Font.GetHeight(float)</c> 在**字体已被释放**时抛
/// <c>ArgumentException("Parameter is not valid.")</c> —— 这正是 #574 的异常形态。</item>
/// <item>Point 单位字体（主题字体的实际单位）的行高随 DPI **线性**放大（96 → 144 → 192 = 1× / 1.5× / 2×），
/// 而 <c>Font.Height</c> 恒为 96 DPI 基线 ⇒ 用 <c>Font.Height</c> 排版会在 150%/200% 下**裁切行高**。</item>
/// </list>
///
/// <para><b>本包关闭的是结构风险</b>：绘制/布局路径不再把 Graphics 交给字体度量；
/// 度量移到受控布局阶段并按 (字体/字号/单位/样式/DPI/主题代次) 缓存；
/// 句柄不存在/销毁中时推迟测量并保留最小布局。
/// #574 的**原始崩溃**仍需日志与触发链对应（归 P-F15 取证）。</para>
///
/// <para><b>不依赖真实桌面 / GPU / 窗口</b>：DPI 通过可注入的来源提供，
/// 控件路径只用"没有句柄的控件"来验证推迟测量。</para>
/// </summary>
public sealed class FontMeasureSafetyTests
{
    // ================================================================
    // ① 取证：平台行为（红→绿的事实基础）
    // ================================================================

    [Fact]
    public void 取证_GetHeight用Graphics与用DPI数值在同一DPI下完全等价()
    {
        using Font font = new Font("Segoe UI", 10f);
        using Graphics graphics = Graphics.FromHwnd(IntPtr.Zero);

        Assert.Equal(font.GetHeight(graphics), font.GetHeight(graphics.DpiY));
    }

    [Fact]
    public void 取证_已释放的Graphics传给GetHeight会抛ArgumentException()
    {
        using Font font = new Font("Segoe UI", 10f);
        Graphics graphics = Graphics.FromHwnd(IntPtr.Zero);
        graphics.Dispose();

        ArgumentException ex = Assert.Throws<ArgumentException>(() => font.GetHeight(graphics));
        Assert.Contains("graphics", ex.Message);
    }

    [Fact]
    public void 取证_已释放的字体读度量抛ArgumentException且消息为ParameterIsNotValid()
    {
        // ★ 这就是 #574 的异常形态：ArgumentException("Parameter is not valid.")。
        Font font = new Font("Segoe UI", 10f);
        font.Dispose();

        ArgumentException height = Assert.Throws<ArgumentException>(() => _ = font.Height);
        Assert.Equal("Parameter is not valid.", height.Message);

        Assert.Throws<ArgumentException>(() => font.GetHeight(96f));
    }

    [Fact]
    public void 取证_Point单位字体的行高随DPI线性放大而FontHeight恒为96基线()
    {
        using Font font = new Font("Segoe UI", 10f);

        float at96 = font.GetHeight(96f);
        float at144 = font.GetHeight(144f);
        float at192 = font.GetHeight(192f);

        Assert.Equal(at96 * 1.5f, at144, 3);
        Assert.Equal(at96 * 2.0f, at192, 3);

        // ★ Font.Height 是 96 DPI 基线（ceil）——它**不含**设备 DPI。
        Assert.Equal((int)Math.Ceiling(at96), font.Height);
    }

    // ================================================================
    // ② 结构护栏（红→绿核心）：绘制/布局路径不得再把 Graphics 交给字体度量
    // ================================================================

    [Fact]
    public void 护栏_HorizontalLayout不得再调用GetHeight_Graphics重载()
    {
        List<string> offenders = FindFontGetHeightWithGraphicsCalls(typeof(HorizontalLayout));

        Assert.True(
            offenders.Count == 0,
            "HorizontalLayout 不得再调用 Font.GetHeight(Graphics)：" + string.Join(", ", offenders) +
            "。该重载在 Graphics 已释放时抛 ArgumentException（#574 的崩溃形态之一）；" +
            "必须改用 Font.GetHeight(float dpi) —— Graphics 无关且同 DPI 等价。");
    }

    [Fact]
    public void 护栏_产品程序集里不得再出现GetHeight_Graphics调用()
    {
        List<string> offenders = FindFontGetHeightWithGraphicsCallsInProductAssembly();

        Assert.True(
            offenders.Count == 0,
            "产品程序集里仍有 Font.GetHeight(Graphics) 调用：" + string.Join(", ", offenders) +
            "。字体度量必须与 Graphics 解耦（#574 的已知结构风险）。");
    }

    [Fact]
    public void 护栏_横排布局只有一个构造点_行高不再随代码路径漂移()
    {
        List<string> constructionSites = new List<string>();
        foreach (MethodBase method in AllMethods(typeof(UIController)))
        {
            foreach (IlInspector.IlInstruction instruction in IlInspector.Disassemble(method).Instructions)
            {
                if (instruction.OpCode != OpCodes.Newobj) continue;
                if (instruction.Target is not ConstructorInfo ctor) continue;
                if (ctor.DeclaringType != typeof(HorizontalLayout)) continue;
                constructionSites.Add(method.Name);
            }
        }

        // ★ 过去有两处（legacy 绘制路径 / GPU 场景路径），参数还不一样：
        //   legacy 侧不传 Settings（去读磁盘 settings.json）也不传测量控件（按 96 DPI 算行高）。
        //   两处都用 ??=，谁先跑到谁说了算 ⇒ 行高随"GPU 路线还是 legacy 路线"而变。
        Assert.True(
            constructionSites.Count == 1,
            "HorizontalLayout 必须只有**一个**构造点（当前 " + constructionSites.Count + " 个：" +
            string.Join(", ", constructionSites) + "）—— 多构造点会让行高/列间距随代码路径漂移。");
        Assert.Equal("CreateHorizontalLayout", constructionSites[0]);
    }

    [Fact]
    public void 护栏_横排布局构造点必须传实时配置与测量控件()
    {
        MethodInfo factory = typeof(UIController).GetMethod(
            "CreateHorizontalLayout",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(factory != null, "UIController.CreateHorizontalLayout 不存在 —— 护栏前提被破坏。");

        List<string> loadedFields = new List<string>();
        foreach (IlInspector.IlInstruction instruction in IlInspector.Disassemble(factory).Instructions)
        {
            if (instruction.Field != null) loadedFields.Add(instruction.Field.Name);
        }

        Assert.Contains("_cfg", loadedFields);        // 必须是**实时**配置，不是 Settings.Load()
        Assert.Contains("_form", loadedFields);       // 测量控件 ⇒ 设备 DPI ⇒ 150%/200% 不裁切
        Assert.Contains("_fontMetrics", loadedFields); // 共享度量缓存 ⇒ 主题代次统一
    }

    // ================================================================
    // ③ DPI 行高：100 / 150 / 200% 无裁切
    // ================================================================

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void 行高按设备DPI计算_不小于字体在该DPI下的渲染高度(int dpi)
    {
        using Font font = new Font("Segoe UI", 10f);
        Theme theme = CreateThemeWithFonts(font);
        FontMetricsCache cache = new FontMetricsCache();

        HorizontalLayout layout = new HorizontalLayout(
            theme, 300, LayoutMode.Horizontal, new Settings(), null,
            new FixedDisplayDpiSource(dpi), cache);

        int required = (int)Math.Ceiling(font.GetHeight(dpi));

        Assert.True(
            layout.RowHeight >= required,
            $"DPI={dpi}：行高 {layout.RowHeight} 小于字体渲染高度 {required} ⇒ 文字会被裁切。");
        Assert.Equal(dpi / 96f, layout.DpiScale, 3);
    }

    [Theory]
    [InlineData(144)]
    [InlineData(192)]
    public void 行高在高DPI下严格大于96基线的FontHeight_旧的FontHeight路径会裁切(int dpi)
    {
        using Font font = new Font("Segoe UI", 10f);
        Theme theme = CreateThemeWithFonts(font);

        HorizontalLayout layout = new HorizontalLayout(
            theme, 300, LayoutMode.Horizontal, new Settings(), null,
            new FixedDisplayDpiSource(dpi), new FontMetricsCache());

        // ★ 旧实现（无测量控件时）用 Math.Max(FontItem.Height, FontValue.Height)，
        //   那是 96 DPI 基线 ⇒ 在 150%/200% 下必然小于真实渲染高度 ⇒ 裁切。
        Assert.True(
            layout.RowHeight > font.Height,
            $"DPI={dpi}：行高 {layout.RowHeight} 没有超过 96 基线的 Font.Height {font.Height} ⇒ " +
            "测量仍在用 96 DPI 基线，文字会被裁切。");
    }

    [Fact]
    public void 行高随DPI单调不减()
    {
        using Font font = new Font("Segoe UI", 10f);
        Theme theme = CreateThemeWithFonts(font);

        int at96 = MeasureRowHeight(theme, 96);
        int at144 = MeasureRowHeight(theme, 144);
        int at192 = MeasureRowHeight(theme, 192);

        Assert.True(at144 > at96, $"144 DPI 行高 {at144} 未大于 96 DPI 行高 {at96}。");
        Assert.True(at192 > at144, $"192 DPI 行高 {at192} 未大于 144 DPI 行高 {at144}。");
    }

    // ================================================================
    // ④ 句柄不存在 / 销毁中 ⇒ 推迟测量 + 保留最小布局
    // ================================================================

    [Fact]
    public void 句柄不存在时推迟测量并保留最小布局()
    {
        using Font font = new Font("Segoe UI", 10f);
        Theme theme = CreateThemeWithFonts(font);
        FontMetricsCache cache = new FontMetricsCache();

        // ★ Panel 不创建句柄 ⇒ IsHandleCreated == false ⇒ 测量必须推迟，
        //   而且**不得**为了取 DPI 去创建句柄。
        using Panel control = new Panel();
        Assert.False(control.IsHandleCreated);

        HorizontalLayout layout = new HorizontalLayout(
            theme, 300, LayoutMode.Horizontal, new Settings(), control,
            new ControlDisplayDpiSource(control), cache);

        Assert.Equal(0, layout.RowHeight);
        Assert.False(control.IsHandleCreated);      // 推迟测量不得顺带建 HWND
        Assert.True(layout.MinPanelHeight >= 24, "空态最小高度必须保留。");
        Assert.Equal(layout.MinPanelHeight, layout.Build(new List<Column>()));
    }

    [Fact]
    public void 控件已释放时DPI来源报告不可用而不抛异常()
    {
        Panel control = new Panel();
        control.Dispose();

        ControlDisplayDpiSource source = new ControlDisplayDpiSource(control);

        Assert.False(source.TryGetDpi(out int dpi));
        Assert.Equal(0, dpi);
    }

    [Fact]
    public void 没有测量控件时用基线DPI做确定性测量()
    {
        using Font font = new Font("Segoe UI", 10f);
        Theme theme = CreateThemeWithFonts(font);

        HorizontalLayout layout = new HorizontalLayout(
            theme, 300, LayoutMode.Horizontal, new Settings(), null, null, new FontMetricsCache());

        Assert.Equal((int)Math.Ceiling(font.GetHeight(96f)), layout.RowHeight);
    }

    // ================================================================
    // ⑤ 已释放字体：不得访问、不得冒泡异常
    // ================================================================

    [Fact]
    public void 主题字体已被释放时不抛异常且保留最小布局()
    {
        Theme theme = CreateThemeWithFonts(new Font("Segoe UI", 10f));
        FontMetricsCache cache = new FontMetricsCache();

        // ★ 模拟换肤：主题的运行期字体被释放，而布局还握着这个主题。
        theme.DisposeFonts();

        HorizontalLayout layout = new HorizontalLayout(
            theme, 300, LayoutMode.Horizontal, new Settings(), null,
            new FixedDisplayDpiSource(96), cache);

        Assert.Equal(0, layout.RowHeight);                       // 推迟测量
        Assert.True(layout.MinPanelHeight >= 24, "空态最小高度必须保留。");
        Assert.NotEmpty(cache.Diagnostics);                      // 必须留下诊断而不是静默
        Assert.Equal(layout.MinPanelHeight, layout.Build(new List<Column>()));
    }

    [Fact]
    public void 高频换肤时已释放字体的度量失败不会无限重复()
    {
        FontMetricsCache cache = new FontMetricsCache();

        for (int i = 0; i < 50; i++)
        {
            Theme theme = CreateThemeWithFonts(new Font("Segoe UI", 10f));
            theme.DisposeFonts();

            // 每一代主题都重新测一次（换肤 ⇒ 主题代次变化）。
            cache.AdvanceThemeGeneration();
            int height = cache.MeasureFont(theme.FontItem, 96);
            Assert.Equal(0, height);
        }

        // ★ 诊断必须有界：高频路径上不能刷屏。
        Assert.True(
            cache.Diagnostics.Count <= FontMetricsCache.MaxDiagnostics,
            $"诊断条数 {cache.Diagnostics.Count} 超出上限 {FontMetricsCache.MaxDiagnostics}。");
    }

    // ================================================================
    // ⑥ 缓存：键含字体/字号/单位/样式/DPI/主题代次
    // ================================================================

    [Fact]
    public void 同一字体同一DPI命中缓存_换DPI或换主题代次则重新测量()
    {
        using Font font = new Font("Segoe UI", 10f);
        FontMetricsCache cache = new FontMetricsCache();

        int first = cache.MeasureFont(font, 96);
        Assert.Equal(first, cache.MeasureFont(font, 96));
        Assert.Equal(1, cache.CacheCount);

        // 换 DPI ⇒ 不同的键 ⇒ 重新测量。
        cache.MeasureFont(font, 144);
        Assert.Equal(2, cache.CacheCount);

        // 换主题代次 ⇒ 缓存整体失效 ⇒ 重新测量（不会命中上一代的度量）。
        cache.AdvanceThemeGeneration();
        Assert.Equal(0, cache.CacheCount);
        Assert.Equal(1, cache.ThemeGeneration);
        cache.MeasureFont(font, 96);
        Assert.Equal(1, cache.CacheCount);
    }

    [Fact]
    public void 不同字号或不同字体族落在不同的缓存键上()
    {
        using Font small = new Font("Segoe UI", 10f);
        using Font large = new Font("Segoe UI", 20f);
        using Font other = new Font("Consolas", 10f);
        FontMetricsCache cache = new FontMetricsCache();

        cache.MeasureFont(small, 96);
        cache.MeasureFont(large, 96);
        cache.MeasureFont(other, 96);

        Assert.Equal(3, cache.CacheCount);
    }

    [Fact]
    public void 主题代次推进后同一字体的度量按新代次重算()
    {
        using Font first = new Font("Segoe UI", 10f);
        using Font second = new Font("Segoe UI", 20f);
        FontMetricsCache cache = new FontMetricsCache();

        int h1 = cache.MeasureFont(first, 96);
        cache.AdvanceThemeGeneration();
        int h2 = cache.MeasureFont(second, 96);

        Assert.True(h2 > h1, $"换肤后度量应按新字体重算（{h1} → {h2}）。");
    }

    [Fact]
    public void 缓存键不含字体实例_因此不会把已释放字体留在缓存里()
    {
        Font font = new Font("Segoe UI", 10f);
        FontMetricsCache cache = new FontMetricsCache();

        int measured = cache.MeasureFont(font, 96);
        Assert.True(measured > 0);
        Assert.Equal(1, cache.CacheCount);

        font.Dispose();

        // ★ 缓存命中路径**完全不碰字体对象** ⇒ 已释放字体也不会被访问，
        //   而且度量值（字体描述 + DPI 的纯函数）依然有效。
        Assert.Equal(measured, cache.MeasureFont(font, 96));

        // ★ 从未测过的已释放字体 ⇒ 度量失败（记诊断 + 负缓存），不抛异常。
        Font neverMeasured = new Font("Segoe UI", 20f);
        neverMeasured.Dispose();
        Assert.Equal(0, cache.MeasureFont(neverMeasured, 96));
        Assert.NotEmpty(cache.Diagnostics);
    }

    // ================================================================
    // ⑦ 空状态最小高度保留 + 列布局仍能产出宽度
    // ================================================================

    [Fact]
    public void 空状态返回最小高度而不是零()
    {
        using Font font = new Font("Segoe UI", 10f);
        Theme theme = CreateThemeWithFonts(font);

        HorizontalLayout layout = new HorizontalLayout(
            theme, 300, LayoutMode.Horizontal, new Settings(), null,
            new FixedDisplayDpiSource(96), new FontMetricsCache());

        Assert.True(layout.MinPanelHeight >= 24);
        Assert.Equal(layout.MinPanelHeight, layout.Build(new List<Column>()));
        Assert.True(layout.Build(new List<Column>()) > 0);
    }

    [Fact]
    public void 有列时仍能算出列宽且高度为行高的整数倍()
    {
        using Font font = new Font("Segoe UI", 10f);
        Theme theme = CreateThemeWithFonts(font);

        HorizontalLayout layout = new HorizontalLayout(
            theme, 300, LayoutMode.Horizontal, new Settings(), null,
            new FixedDisplayDpiSource(96), new FontMetricsCache());

        List<Column> columns = new List<Column>
        {
            new Column
            {
                Top = new MetricItem { Key = "DASH.Test", ShortLabel = "CPU", TextValue = "42" },
                Bottom = new MetricItem { Key = "DASH.Test2", ShortLabel = " ", TextValue = "7" }
            }
        };

        int height = layout.Build(columns);

        Assert.True(columns[0].ColumnWidth > 0, "列宽必须大于 0。");
        Assert.True(height >= layout.RowHeight * 2, $"面板高度 {height} 至少要装下两行 {layout.RowHeight}。");
        Assert.True(columns[0].Bounds.Height > 0, "列高度必须大于 0。");
    }

    // ================================================================
    // 辅助
    // ================================================================

    private static int MeasureRowHeight(Theme theme, int dpi)
    {
        HorizontalLayout layout = new HorizontalLayout(
            theme, 300, LayoutMode.Horizontal, new Settings(), null,
            new FixedDisplayDpiSource(dpi), new FontMetricsCache());
        return layout.RowHeight;
    }

    /// <summary>造一个带**真实**运行期字体的主题（与 DerivedThemeRuntimeFontTests 同一口径）。</summary>
    private static Theme CreateThemeWithFonts(Font itemFont)
    {
        Theme theme = new Theme { Name = "F12Theme" };
        theme.FontTitle = new Font("Segoe UI", 10f);
        theme.FontGroup = new Font("Segoe UI", 10f);
        theme.FontItem = itemFont;
        theme.FontValue = new Font("Consolas", 10f);
        theme.FontTaskbar = new Font("Segoe UI", 10f);
        return theme;
    }

    private static IEnumerable<MethodBase> AllMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (MethodInfo method in type.GetMethods(flags)) yield return method;
        foreach (ConstructorInfo ctor in type.GetConstructors(flags)) yield return ctor;
    }

    private static bool IsFontGetHeightWithGraphics(MethodBase call)
    {
        if (call.DeclaringType != typeof(Font)) return false;
        if (call.Name != "GetHeight") return false;
        ParameterInfo[] parameters = call.GetParameters();
        return parameters.Length == 1 && parameters[0].ParameterType == typeof(Graphics);
    }

    private static List<string> FindFontGetHeightWithGraphicsCalls(Type type)
    {
        List<string> offenders = new List<string>();
        foreach (MethodBase method in AllMethods(type))
        {
            foreach (MethodBase call in IlInspector.Scan(method).Calls)
            {
                if (!IsFontGetHeightWithGraphics(call)) continue;
                offenders.Add($"{type.FullName}.{method.Name}");
            }
        }
        return offenders;
    }

    /// <summary>
    /// 扫描**产品程序集**（LiteMonitor* 命名空间）里所有方法，
    /// 找出把 Graphics 传给 <c>Font.GetHeight</c> 的调用点。
    /// </summary>
    private static List<string> FindFontGetHeightWithGraphicsCallsInProductAssembly()
    {
        List<string> offenders = new List<string>();
        Assembly assembly = typeof(UIController).Assembly;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = Array.FindAll(ex.Types, t => t != null)!;
        }

        foreach (Type type in types)
        {
            if (type.Namespace is null) continue;
            if (!type.Namespace.StartsWith("LiteMonitor", StringComparison.Ordinal)) continue;
            offenders.AddRange(FindFontGetHeightWithGraphicsCalls(type));
        }
        return offenders;
    }
}
