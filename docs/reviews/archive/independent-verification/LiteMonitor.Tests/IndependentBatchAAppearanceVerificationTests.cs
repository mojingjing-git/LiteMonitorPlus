using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【独立复核 · 批次 A】LM-070 / LM-071 / LM-072 的**独立**钉住用例。
/// <para>★ 本文件由复核者（verifier）自行编写，**不复用**实现者的测试文件；
/// 结论只以本文件的用例 + 自设计变异的实测结果为准。</para>
/// </summary>
[Collection("LiveGlass STA")]
public sealed class IndependentBatchAAppearanceVerificationTests
{
    // ================================================================
    // LM-070：原生旧主题不得被套 GPU 回退配色
    // ================================================================

    /// <summary>
    /// LM-070 ①：**带 rendering 块但 backend != gpu** 的主题也是"原生旧主题"
    /// ⇒ 换肤后派生覆盖必须被清空，渲染用主题必须是源主题本身，6 个色槽逐个等于源主题。
    /// </summary>
    [Fact]
    public void LM070_带rendering块但backend非gpu的主题不得被套回退配色()
    {
        Theme native = CreateNativeLegacyThemeWithRenderingBlock();

        // 前置：判别式必须认为它**不是** GPU 主题。
        Assert.False(native.Rendering!.IsGpuRequested, "前置条件：本主题不应被判定为 GPU 主题。");
        Assert.False(LegacyFallbackStyleBridge.IsGpuTheme(native), "前置条件：判别式应返回 false。");

        using ThemeManagerCurrentScope scope = new(native);
        UIController controller = CreateUninitializedController();

        // 处于"从 GPU 回退态换肤"的触发前提：派生覆盖已存在。
        SetLegacyFallbackTheme(controller, CreateStaleDerivedOverride());

        InvokeRefreshLegacyFallbackTheme(controller, native);

        // ★ 派生覆盖必须被清空（否则旧主题自身配色被整组盖掉）。
        Assert.Null(GetLegacyFallbackTheme(controller));

        Theme renderTheme = GetCurrentRenderTheme(controller);
        Assert.Same(native, renderTheme);
        Assert.Same(ThemeManager.Current, renderTheme);

        // ★ 6 个色槽与源主题**逐个相等**，且**不等于**该主题声明的 fallbackStyle。
        Assert.Equal(native.Color.Background, renderTheme.Color.Background);
        Assert.Equal(native.Color.GroupBackground, renderTheme.Color.GroupBackground);
        Assert.Equal(native.Color.Border, renderTheme.Color.Border);
        Assert.Equal(native.Color.TextPrimary, renderTheme.Color.TextPrimary);
        Assert.Equal(native.Color.TextSecondary, renderTheme.Color.TextSecondary);
        Assert.Equal(native.Color.TextTitle, renderTheme.Color.TextTitle);

        Assert.NotEqual("#0A0B0C", renderTheme.Color.Background);
        Assert.NotEqual("#111213", renderTheme.Color.GroupBackground);
    }

    /// <summary>
    /// LM-070 ②（不得项 · 不得误伤）：**GPU 主题被迫回退**时**应当**套用
    /// rendering.fallbackStyle ⇒ 派生主题的 6 个色槽必须等于该 fallbackStyle。
    /// </summary>
    [Fact]
    public void LM070_GPU主题被迫回退时派生主题六色槽必须等于fallbackStyle()
    {
        Theme gpu = CreateGpuTheme();

        Assert.True(gpu.Rendering!.IsGpuRequested, "前置条件：本主题应被判定为 GPU 主题。");
        Assert.True(LegacyFallbackStyleBridge.IsGpuTheme(gpu), "前置条件：判别式应返回 true。");

        using ThemeManagerCurrentScope scope = new(gpu);
        UIController controller = CreateUninitializedController();
        SetLegacyFallbackTheme(controller, CreateStaleDerivedOverride());

        InvokeRefreshLegacyFallbackTheme(controller, gpu);

        Theme? derived = GetLegacyFallbackTheme(controller);
        Assert.NotNull(derived);
        Assert.NotSame(gpu, derived);

        Assert.Equal("#0A1017", derived!.Color.Background);
        Assert.Equal("#121B24", derived.Color.GroupBackground);
        Assert.Equal("#345064", derived.Color.Border);
        Assert.Equal("#E8F7FA", derived.Color.TextPrimary);
        Assert.Equal("#91A7B2", derived.Color.TextSecondary);
        Assert.Equal("#48E6F2", derived.Color.TextTitle);
    }

    /// <summary>
    /// LM-070 ③：判别式 IsGpuTheme 的**边界**（纯函数，可确定性断言）。
    /// </summary>
    [Fact]
    public void LM070_判别式只认backend为gpu的主题()
    {
        Assert.False(LegacyFallbackStyleBridge.IsGpuTheme(null));
        Assert.False(LegacyFallbackStyleBridge.IsGpuTheme(new Theme()));

        Theme legacyBackend = new()
        {
            Rendering = new ThemeRenderingConfig { Backend = ThemeRenderingConfig.LegacyBackend }
        };
        Assert.False(LegacyFallbackStyleBridge.IsGpuTheme(legacyBackend));

        Theme gpuBackend = new()
        {
            Rendering = new ThemeRenderingConfig { Backend = ThemeRenderingConfig.GpuBackend }
        };
        Assert.True(LegacyFallbackStyleBridge.IsGpuTheme(gpuBackend));
    }

    // ================================================================
    // LM-071：派生主题必须带上运行期字体
    // ================================================================

    /// <summary>
    /// LM-071 ① + ②：**按生产顺序**（Load → BuildFonts → Scale → 派生）造出派生主题，
    /// 钉住：5 个运行期字体字段与源主题**同一实例**、字体**未被释放**、
    /// 且**没有**退回 SystemFonts.CaptionFont。
    /// </summary>
    [Fact]
    public void LM071_派生主题的五个运行期字体字段与源主题同一实例且未被释放()
    {
        Theme source = new Theme { Name = "Lm071VerifyGpu" };
        source.Font.Family = "Microsoft YaHei UI";
        source.Font.ValueFamily = "Consolas";
        source.Font.Title = 11.5;
        source.Font.Group = 10.5;
        source.Font.Item = 10.0;
        source.Font.Value = 10.5;
        source.Font.Bold = true;
        source.BuildFonts();

        // ★ 生产顺序：ApplyTheme 里 t.Scale(...) 发生在派生主题构建**之前**。
        source.Scale(1.25f, 1f);

        LegacyFallbackStyle style = LegacyFallbackStyleBridge.Resolve(
            new ThemeRenderingConfig { Backend = ThemeRenderingConfig.GpuBackend },
            out _);

        Theme derived = LegacyFallbackStyleBridge.BuildDerivedTheme(source, style);
        Assert.NotNull(derived);

        Assert.Same(source.FontTitle, derived.FontTitle);
        Assert.Same(source.FontGroup, derived.FontGroup);
        Assert.Same(source.FontItem, derived.FontItem);
        Assert.Same(source.FontValue, derived.FontValue);
        Assert.Same(source.FontTaskbar, derived.FontTaskbar);

        // ★ 未被释放（悬垂引用会让字体读度量失败）。
        Assert.False(IsReleased(source.FontTitle), "源主题 FontTitle 已被释放 ⇒ 派生主题将持有悬垂字体。");
        Assert.False(IsReleased(source.FontGroup), "源主题 FontGroup 已被释放。");
        Assert.False(IsReleased(source.FontItem), "源主题 FontItem 已被释放。");
        Assert.False(IsReleased(source.FontValue), "源主题 FontValue 已被释放。");

        // ★ 不得退回系统默认字体（那正是 LM-071 的原始症状）。
        Assert.NotSame(SystemFonts.CaptionFont, derived.FontTitle);
        Assert.NotSame(SystemFonts.CaptionFont, derived.FontItem);
        Assert.True(derived.FontTitle.Height > 0, "派生主题的字体必须仍然可用。");
    }

    /// <summary>
    /// LM-071 ③：**换肤**（旧主题 DisposeFonts() 之后）派生主题引用的字体**未被释放**。
    /// </summary>
    [Fact]
    public void LM071_换肤释放旧主题字体后派生主题引用的字体仍未被释放()
    {
        Theme oldTheme = CreateGpuTheme();
        Theme newTheme = CreateGpuTheme();
        oldTheme.BuildFonts();
        newTheme.BuildFonts();

        using ThemeManagerCurrentScope scope = new(newTheme);
        UIController controller = CreateUninitializedController();

        // 派生覆盖引用的是**旧主题**的字体。
        SetLegacyFallbackTheme(
            controller,
            LegacyFallbackStyleBridge.BuildDerivedTheme(
                oldTheme,
                LegacyFallbackStyleBridge.Resolve(oldTheme.Rendering, out _)));

        // 换肤：先重算派生覆盖，再释放旧主题字体。
        InvokeRefreshLegacyFallbackTheme(controller, newTheme);
        oldTheme.DisposeFonts();

        // 旧主题的字体确实被释放了（否则本用例是空断言）。
        Assert.True(IsReleased(oldTheme.FontTitle), "旧主题的 FontTitle 应当已被 DisposeFonts 释放。");
        Assert.True(IsReleased(oldTheme.FontItem), "旧主题的 FontItem 应当已被 DisposeFonts 释放。");

        Theme? derived = GetLegacyFallbackTheme(controller);
        Assert.NotNull(derived);
        Assert.Same(newTheme.FontTitle, derived!.FontTitle);
        Assert.Same(newTheme.FontItem, derived.FontItem);
        Assert.False(IsReleased(derived.FontTitle), "派生主题引用的 FontTitle 已被释放（悬垂引用）。");
        Assert.False(IsReleased(derived.FontItem), "派生主题引用的 FontItem 已被释放（悬垂引用）。");
    }

    // ================================================================
    // LM-072：受控重建后必须按配置恢复实时玻璃
    // ================================================================

    /// <summary>
    /// LM-072 ①：**临时停机**（句柄销毁）不得清掉配置态开关；**整停**（真关闭）必须清掉。
    /// </summary>
    [Fact]
    public void LM072_临时停机保留配置态开关而整停清掉它()
    {
        RunOnSta(() =>
        {
            var factory = new CountingServiceFactory();
            LiveGlassController? controller = null;
            try
            {
                using Form form = CreateOffscreenForm();
                ThemeAppearanceOptions options = CreateOptions(form);
                controller = new LiveGlassController(form, () => options, factory.Create);

                controller.SetEnabled(true);
                Assert.True(controller.Enabled, "前置条件：开关应当已打开。");
                Assert.Equal(1, factory.CreatedCount);

                // 临时停机（句柄销毁 / 受控重建）。
                controller.StopForHandleRecreate();

                Assert.True(controller.Enabled, "临时停机不得清掉配置态开关（LM-072）—— 重建后无人能把它拉回来。");
                Assert.Null(controller.CaptureService);

                // 整停（真关闭）。
                controller.SetEnabled(false);
                Assert.False(controller.Enabled, "真关闭必须把配置态开关清掉。");
            }
            finally
            {
                controller?.Dispose();
            }
        });
    }

    /// <summary>
    /// LM-072 ②：配置为"开"但服务已经停了 ⇒ SetEnabled(true) **确实**把服务拉起来。
    /// </summary>
    [Fact]
    public void LM072_配置为开且服务已停时SetEnabled为真必须重启服务()
    {
        RunOnSta(() =>
        {
            // 第一次创建服务时抛异常 ⇒ 落到"配置为开、服务为空"的真实状态。
            var factory = new CountingServiceFactory { FailFirstCreate = true };
            LiveGlassController? controller = null;
            try
            {
                using Form form = CreateOffscreenForm();
                ThemeAppearanceOptions options = CreateOptions(form);
                controller = new LiveGlassController(form, () => options, factory.Create);

                controller.SetEnabled(true);

                // 前置：配置为开，但服务确实没起来。
                Assert.True(controller.Enabled);
                Assert.Null(controller.CaptureService);
                Assert.Equal(1, factory.CreatedCount);

                // ★ 配置没变（仍是开）⇒ 必须按配置重启服务。
                controller.SetEnabled(true);

                Assert.Equal(2, factory.CreatedCount);
                Assert.NotNull(controller.CaptureService);
            }
            finally
            {
                controller?.Dispose();
            }
        });
    }

    /// <summary>
    /// LM-072 ③：**用户显式关闭** ⇒ "按配置恢复"绝不允许把服务拉起来。
    /// </summary>
    [Fact]
    public void LM072_用户显式关闭后按配置恢复不得把服务拉起来()
    {
        RunOnSta(() =>
        {
            var factory = new CountingServiceFactory();
            LiveGlassController? controller = null;
            try
            {
                using Form form = CreateOffscreenForm();
                ThemeAppearanceOptions options = CreateOptions(form);
                controller = new LiveGlassController(form, () => options, factory.Create);

                controller.SetEnabled(true);
                Assert.True(controller.Enabled);
                Assert.Equal(1, factory.CreatedCount);

                controller.SetEnabled(false);
                Assert.False(controller.Enabled);
                Assert.Null(controller.CaptureService);

                // ★ 按配置恢复：配置已经是"关" ⇒ 绝不允许把服务拉起来。
                controller.RestoreIfEnabled();

                Assert.False(controller.Enabled);
                Assert.Null(controller.CaptureService);
                Assert.Equal(1, factory.CreatedCount);
            }
            finally
            {
                controller?.Dispose();
            }
        });
    }

    /// <summary>
    /// LM-072 ④（接线）：**句柄销毁 = 临时停机**、**真关闭 = 整停**，
    /// 两个入口必须分别接到 StopLiveGlassForHandleRecreate / StopLiveGlass。
    /// </summary>
    [Fact]
    public void LM072_句柄销毁走临时停机而真关闭走整停()
    {
        IlInspector.Disassembly destroyed = IlInspector.Disassemble(
            RequireMethod(typeof(MainForm), "OnHandleDestroyed"));
        Assert.True(
            CallOffsetOf(destroyed, typeof(UIController), "StopLiveGlassForHandleRecreate") >= 0,
            "MainForm.OnHandleDestroyed 必须调用 StopLiveGlassForHandleRecreate（临时停机）—— "
            + "句柄销毁只停服务、保留配置态开关（LM-072）。");
        Assert.True(
            CallOffsetOf(destroyed, typeof(UIController), "StopLiveGlass") < 0,
            "MainForm.OnHandleDestroyed 不得调用 StopLiveGlass（整停）—— "
            + "整停会把配置态开关清成 false，重建完成后永远恢复不了（LM-072）。");

        IlInspector.Disassembly closed = IlInspector.Disassemble(
            RequireMethod(typeof(MainForm), "OnFormClosed"));
        Assert.True(
            CallOffsetOf(closed, typeof(UIController), "StopLiveGlass") >= 0,
            "MainForm.OnFormClosed 是真关闭，必须整停实时玻璃（LM-072）—— "
            + "退出时既停捕获、也把配置态开关清掉。");
    }

    // ================================================================
    // 辅助
    // ================================================================

    /// <summary>造一个**不跑构造函数**的 UIController（构造函数会启动监控与定时器）。</summary>
    private static UIController CreateUninitializedController() =>
        (UIController)RuntimeHelpers.GetUninitializedObject(typeof(UIController));

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

    private static void SetLegacyFallbackTheme(UIController controller, Theme theme) =>
        LegacyFallbackThemeField.SetValue(controller, theme);

    private static Theme? GetLegacyFallbackTheme(UIController controller) =>
        (Theme?)LegacyFallbackThemeField.GetValue(controller);

    private static void InvokeRefreshLegacyFallbackTheme(UIController controller, Theme theme) =>
        RefreshLegacyFallbackThemeMethod.Invoke(controller, new object[] { theme });

    private static Theme GetCurrentRenderTheme(UIController controller) =>
        (Theme)CurrentRenderThemeProperty.GetValue(controller)!;

    /// <summary>
    /// 原生旧主题：**带** rendering 块，但 backend = legacy，
    /// 且声明了一份自己的 fallbackStyle（一旦被误套，色槽立刻变形）。
    /// </summary>
    private static Theme CreateNativeLegacyThemeWithRenderingBlock()
    {
        Theme theme = new() { Name = "Lm070NativeLegacy" };
        theme.Color.Background = "#202225";
        theme.Color.GroupBackground = "#2B2D31";
        theme.Color.Border = "#3A3D42";
        theme.Color.TextPrimary = "#EAEAEA";
        theme.Color.TextSecondary = "#B0B0B0";
        theme.Color.TextTitle = "#FFFFFF";
        theme.Rendering = new ThemeRenderingConfig
        {
            Backend = ThemeRenderingConfig.LegacyBackend,
            Material = ThemeRenderingConfig.LegacyMaterial,
            FallbackStyle = new ThemeFallbackStyle
            {
                Background = "#0A0B0C",
                PanelBackground = "#111213",
                Border = "#222324",
                Text = "#333435",
                MutedText = "#444546",
                Accent = "#555657",
            },
        };
        return theme;
    }

    /// <summary>GPU 主题（backend = gpu），带一份显式的 fallbackStyle。</summary>
    private static Theme CreateGpuTheme()
    {
        Theme theme = new() { Name = "Lm070GpuTheme" };
        theme.Color.Background = "#111111";
        theme.Color.GroupBackground = "#222222";
        theme.Color.Border = "#333333";
        theme.Color.TextPrimary = "#444444";
        theme.Color.TextSecondary = "#555555";
        theme.Color.TextTitle = "#666666";
        theme.Rendering = new ThemeRenderingConfig
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = ThemeRenderingConfig.LiquidGlassMaterial,
            FallbackStyle = new ThemeFallbackStyle
            {
                Background = "#0A1017",
                PanelBackground = "#121B24",
                Border = "#345064",
                Text = "#E8F7FA",
                MutedText = "#91A7B2",
                Accent = "#48E6F2",
            },
        };
        return theme;
    }

    /// <summary>一个"陈旧的"派生覆盖（用于制造"处于 GPU 回退态"的前提）。</summary>
    private static Theme CreateStaleDerivedOverride()
    {
        Theme gpu = CreateGpuTheme();
        return LegacyFallbackStyleBridge.BuildDerivedTheme(
            gpu,
            LegacyFallbackStyleBridge.Resolve(gpu.Rendering, out _));
    }

    /// <summary>字体是否**已被释放**（已释放的字体读度量会失败）。</summary>
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

    private static Form CreateOffscreenForm()
    {
        var form = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -28000),
            ClientSize = new Size(96, 64),
        };
        _ = form.Handle;
        return form;
    }

    private static ThemeAppearanceOptions CreateOptions(Form form)
    {
        using var runtime = new AppearanceRuntime();
        var theme = new Theme { Name = "Lm072Verify", Version = 4 };
        theme.Backdrop = new BackdropConfig
        {
            Mode = ThemeAppearanceOptions.BackdropModeGlass,
            Opacity = 0,
            Blur = 8,
            Fps = 6,
        };
        return runtime.Update(theme, 1f, 1f, form.ClientRectangle);
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        })
        {
            IsBackground = true,
            Name = "Lm072VerifySta",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "STA 用例超时。");
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    /// <summary>可注入的捕获服务工厂（记录创建次数，可选让第一次创建失败）。</summary>
    private sealed class CountingServiceFactory
    {
        private int _createdCount;

        public int CreatedCount => Volatile.Read(ref _createdCount);

        public bool FailFirstCreate { get; init; }

        public BackdropCaptureService Create()
        {
            int index = Interlocked.Increment(ref _createdCount);
            if (FailFirstCreate && index == 1)
            {
                throw new InvalidOperationException("合成故障：第一次创建服务失败。");
            }

            return new BackdropCaptureService(new NullBackdropBackend());
        }
    }

    /// <summary>最简捕获后端：永远不产出帧，只用于观察"服务是否被创建/启动"。</summary>
    private sealed class NullBackdropBackend : IBackdropCaptureBackend
    {
        public bool IsReady => true;

        public string UnavailableReason => string.Empty;

        public bool TryCapture(
            BackdropServiceRequest request,
            out Image? frame,
            out BackdropServiceTiming timing)
        {
            frame = null;
            timing = BackdropServiceTiming.Empty;
            return false;
        }

        public void RestoreDisplayAffinity()
        {
        }
    }

    /// <summary>临时替换 ThemeManager.Current（测试结束自动还原）。</summary>
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
                BindingFlags.Static | BindingFlags.Public)!;
            property.SetValue(null, theme);
        }
    }

    private static MethodInfo RequireMethod(Type type, string name)
    {
        MethodInfo[] candidates = Array.FindAll(
            type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.Name == name);

        Assert.True(candidates.Length > 0, $"{type.Name}.{name} 不存在 —— 护栏前提被破坏。");
        return candidates[0];
    }

    private static int CallOffsetOf(
        IlInspector.Disassembly body,
        Type declaringType,
        string name)
    {
        foreach (IlInspector.IlInstruction instruction in body.Instructions)
        {
            if (instruction.OpCode != System.Reflection.Emit.OpCodes.Call &&
                instruction.OpCode != System.Reflection.Emit.OpCodes.Callvirt)
            {
                continue;
            }

            if (instruction.Target is not MethodInfo called)
            {
                continue;
            }

            if (called.DeclaringType == declaringType && called.Name == name)
            {
                return instruction.Offset;
            }
        }

        return -1;
    }
}
