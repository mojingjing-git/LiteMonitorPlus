using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 主题工作室验收整改（2026-10-26 整改计划 §5.3 Phase C）的回归护栏：
    /// <para>
    /// C-1a / C-1b：语义角色 → JSON 路径的映射**必须是该编辑目标下真实存在的描述符**。
    ///   旧实现对 GPU 主效果返回 <c>rendering.parameters.panel_color</c> /
    ///   <c>card_color</c> —— 这两个路径在主题模型里根本不存在（虚构路径），
    ///   且 GPU 两个目标下不存在任何 <c>color.*</c> 描述符（配色项 Scope=Classic，不共享）。
    /// </para>
    /// <para>
    /// C-2：点选定位的焦点必须落在**可编辑控件**上（旧实现取行首显示名 Label，不可聚焦）。
    /// </para>
    /// </summary>
    public class ThemeStudioAcceptanceRemediationTests
    {
        private static readonly ThemeEditTarget[] AllTargets =
        [
            ThemeEditTarget.Classic,
            ThemeEditTarget.GpuMain,
            ThemeEditTarget.GpuFallback
        ];

        private static readonly ThemePropertyCatalog.SemanticElementRole[] AllRoles =
            (ThemePropertyCatalog.SemanticElementRole[])Enum.GetValues(typeof(ThemePropertyCatalog.SemanticElementRole));

        private static Theme CreateGpuTheme(string material) => new()
        {
            Name = "整改自检_" + material,
            Rendering = new ThemeRenderingConfig
            {
                Backend = ThemeRenderingConfig.GpuBackend,
                Material = material
            }
        };

        private static Theme CreateClassicTheme() => new() { Name = "整改自检_经典" };

        /// <summary>
        /// C-1a 自检：全部角色 × 全部目标，解析出的路径必须真实存在于该目标的描述符集。
        /// </summary>
        [Fact]
        public void C1a_语义角色映射_全部角色乘全部目标_解析结果必须真实存在于该目标描述符集()
        {
            Theme[] themes = [CreateGpuTheme(ThemeRenderingConfig.CyberPunkMaterial), CreateClassicTheme()];

            foreach (Theme theme in themes)
            {
                foreach (ThemeEditTarget target in AllTargets)
                {
                    foreach (ThemePropertyCatalog.SemanticElementRole role in AllRoles)
                    {
                        string path = ThemePropertyCatalog.ResolvePropertyPath(theme, role, target);

                        // 空串 = 该目标下确实没有对应可编辑字段（允许，且是诚实结论）。
                        if (path.Length == 0) continue;

                        Assert.True(
                            ThemePropertyCatalog.HasDescriptor(theme, path, target),
                            $"角色 {role} 在目标 {target} 解析到 {path}，但该目标描述符集里没有它。");

                        // 虚构路径回归护栏（曾经的 bug）。
                        Assert.DoesNotContain("panel_color", path, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("card_color", path, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }

        /// <summary>
        /// C-1a 自检：GPU 两个目标下不得解析出经典作用域的配色路径
        /// （GPU 目标的配色由材质着色器决定，没有 color.* 描述符）。
        /// </summary>
        [Fact]
        public void C1a_GPU目标下不得解析出经典作用域配色路径()
        {
            Theme gpuTheme = CreateGpuTheme(ThemeRenderingConfig.CyberPunkMaterial);

            foreach (ThemeEditTarget target in new[] { ThemeEditTarget.GpuMain, ThemeEditTarget.GpuFallback })
            {
                foreach (ThemePropertyCatalog.SemanticElementRole role in AllRoles)
                {
                    string path = ThemePropertyCatalog.ResolvePropertyPath(gpuTheme, role, target);
                    if (path.Length == 0) continue;

                    Assert.False(
                        path.StartsWith("color.", StringComparison.Ordinal),
                        $"GPU 目标 {target} 的角色 {role} 解析到了经典作用域路径 {path}");
                }
            }
        }

        /// <summary>
        /// C-1b 自检：经典主题 + 经典目标下，六个角色全部能解析到真实可编辑字段。
        /// </summary>
        [Fact]
        public void C1b_经典主题经典目标_六个角色全部解析到真实可编辑字段()
        {
            Theme classicTheme = CreateClassicTheme();

            foreach (ThemePropertyCatalog.SemanticElementRole role in AllRoles)
            {
                string path = ThemePropertyCatalog.ResolvePropertyPath(classicTheme, role, ThemeEditTarget.Classic);

                Assert.NotEmpty(path);
                Assert.True(
                    ThemePropertyCatalog.HasDescriptor(classicTheme, path, ThemeEditTarget.Classic),
                    $"角色 {role} 在经典目标解析到 {path}，但经典描述符集里没有它。");
            }
        }

        /// <summary>
        /// C-1b 自检：GPU 主效果（cyber_punk）下的角色映射必须落在真实材质参数上。
        /// </summary>
        [Fact]
        public void C1b_GPU主效果_面板角色必须落到真实材质参数()
        {
            Theme gpuTheme = CreateGpuTheme(ThemeRenderingConfig.CyberPunkMaterial);

            string panel = ThemePropertyCatalog.ResolvePropertyPath(
                gpuTheme, ThemePropertyCatalog.SemanticElementRole.PanelBackground, ThemeEditTarget.GpuMain);

            Assert.NotEmpty(panel);
            Assert.StartsWith("rendering.parameters.", panel, StringComparison.Ordinal);
            Assert.True(ThemePropertyCatalog.HasDescriptor(gpuTheme, panel, ThemeEditTarget.GpuMain));

            // 兼容外观只有六槽：进度条颜色没有可编辑字段 ⇒ 必须诚实地返回空。
            string bar = ThemePropertyCatalog.ResolvePropertyPath(
                gpuTheme, ThemePropertyCatalog.SemanticElementRole.ProgressBar, ThemeEditTarget.GpuFallback);

            Assert.Empty(bar);
        }

        /// <summary>
        /// C-2 回归：点选定位的焦点必须落在可编辑控件（TextBox），而不是行首显示名 Label。
        /// </summary>
        [Fact]
        public void C2_点选定位_焦点必须落在可编辑控件而不是行首标签()
        {
            RunSta(() =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "LiteMonitor_Remediation_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                string themeName = "整改GPU";
                File.WriteAllText(Path.Combine(dir, themeName + ".json"), GpuThemeJson(themeName));

                ThemeDocumentStore.SetThemeDirForTests(dir);
                ThemeDocumentStore.SetRecoveryDirForTests(Path.Combine(dir, "drafts"));

                try
                {
                    using var form = new ThemeStudioForm();
                    form.Show();
                    form.OpenThemeForTesting(themeName);

                    // 切到兼容外观目标（该目标下 ItemValue → rendering.fallbackStyle.text）
                    form.SelectTargetForTesting(ThemeEditTarget.GpuFallback);

                    Assert.True(form.SelectPropertyByRole(ThemePropertyCatalog.SemanticElementRole.ItemValue));

                    Control? editor = form.InspectorForTesting.LastFocusedEditor;
                    Assert.NotNull(editor);
                    Assert.IsNotType<Label>(editor);
                    // 编辑器必须是真正的输入控件（TextBox / ComboBox / CheckBox 家族），不是展示控件。
                    Assert.IsAssignableFrom<TextBoxBase>(editor);
                }
                finally
                {
                    ThemeDocumentStore.SetThemeDirForTests(null);
                    ThemeDocumentStore.ResetRecoveryDir();
                    try { Directory.Delete(dir, true); } catch { }
                }
            });
        }

        /// <summary>
        /// E-1（G1）回归：DPI 变化后固定几何必须重算。
        /// <para>
        /// 旧实现只在构造时按 DeviceDpi 换算一次几何（DIP 合同 §7），
        /// 窗口跨不同 DPI 的显示器后分栏宽度、列表行高、检查器布局都不会变 —— 与合同相悖。
        /// </para>
        /// </summary>
        [Fact]
        public void E1_DPI变化后_分栏宽度与列表行高必须按新DPI重算()
        {
            RunSta(() =>
            {
                using var form = new ThemeStudioForm();

                int baseLibrary = form.LibraryPanelWidthForTesting;
                int baseInspector = form.InspectorWidthForTesting;
                int baseItemHeight = form.LibraryItemHeightForTesting;

                Assert.True(baseLibrary > 0, "资料库分栏宽度应为正");
                Assert.True(baseInspector > 0, "检查器分栏宽度应为正");
                Assert.True(baseItemHeight > 0, "资料库列表行高应为正");

                // 模拟 DPI 翻倍（真实触发源是 WM_DPICHANGED / DpiChangedAfterParent）。
                form.ApplyDpiScaleForTesting(form.DeviceDpi * 2);

                Assert.InRange(form.LibraryPanelWidthForTesting, baseLibrary * 2 - 2, baseLibrary * 2 + 2);
                Assert.InRange(form.InspectorWidthForTesting, baseInspector * 2 - 2, baseInspector * 2 + 2);
                Assert.InRange(form.LibraryItemHeightForTesting, baseItemHeight * 2 - 2, baseItemHeight * 2 + 2);

                // 还原后必须回到原值（重算是幂等的）。
                form.ApplyDpiScaleForTesting(form.DeviceDpi);
                Assert.InRange(form.LibraryPanelWidthForTesting, baseLibrary - 2, baseLibrary + 2);
            });
        }

        private static string GpuThemeJson(string name) => $$"""
        {
          "version": 3,
          "name": "{{name}}",
          "rendering": {
            "version": 1,
            "backend": "gpu",
            "material": "cyber_punk",
            "parameters": { "liveBackground": false, "primaryCut": 14, "secondaryCut": 9, "outlineWidth": 1, "glow": 12, "ambientRatio": 0.12, "panelOpacity": 0.965 },
            "motion": { "targetFps": 30, "stateTransitionMs": 140, "scannerPeriodMs": 2800, "motionEnabled": true },
            "fallback": { "backend": "legacy", "material": "legacy" },
            "fallbackStyle": { "background": "#0A1017", "panelBackground": "#121B24", "border": "#345064", "text": "#E8F7FA", "mutedText": "#91A7B2", "accent": "#48E6F2" }
          },
          "layout": { "rowHeight": 40, "padding": 12 },
          "font": { "family": "Microsoft YaHei UI", "value": 14.0 },
          "color": { "background": "#0A1017", "textTitle": "#E8F7FA", "textPrimary": "#FFFFFF" }
        }
        """;

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
            thread.Join(TimeSpan.FromMinutes(2));

            if (captured != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
            }
        }
    }
}
