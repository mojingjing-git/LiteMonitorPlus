using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.Rendering;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 主题工作室下一阶段（B1 / B2）：
    /// N03 回退同源预览、N04 GPU 横向预览、N05 点选定位、N06 应用结果细分、N07 导出服务接线 端到端回归测试。
    /// </summary>
    public class ThemeStudioNextStageWorkflowTests
    {
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

        private static string CreateTempThemeDir(out string dir, out string themeName)
        {
            dir = Path.Combine(Path.GetTempPath(), "LiteMonitor_NextStage_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            themeName = "赛博GPU";
            string json = """
            {
              "version": 3,
              "name": "赛博GPU",
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
            File.WriteAllText(Path.Combine(dir, $"{themeName}.json"), json);
            return dir;
        }

        [Fact]
        public void N03_兼容外观切换_状态区显示真实同源且消除待开发文案()
        {
            RunSta(() =>
            {
                string themeDir = CreateTempThemeDir(out string dir, out string themeName);
                string recoveryDir = Path.Combine(dir, "drafts");
                ThemeDocumentStore.SetThemeDirForTests(themeDir);
                ThemeDocumentStore.SetRecoveryDirForTests(recoveryDir);

                try
                {
                    using var form = new ThemeStudioForm();
                    form.OpenThemeForTesting(themeName);

                    // 1. 默认主效果
                    Assert.Contains("GPU", form.RouteStatusTextForTesting);

                    // 2. 切换到兼容外观
                    form.SelectTargetForTesting(ThemeEditTarget.GpuFallback);

                    // 断言：必须显示“兼容外观 · 常规绘制（生产同源）”
                    Assert.Equal("预览路线：兼容外观 · 常规绘制（生产同源）", form.RouteStatusTextForTesting);

                    // 断言：旧的过渡文案绝对不能再出现
                    Assert.DoesNotContain("画布仍为主效果", form.RouteStatusTextForTesting);
                    Assert.DoesNotContain("回退预览待 TE04", form.RouteStatusTextForTesting);
                    Assert.DoesNotContain("待 TE04", form.RouteStatusTextForTesting);
                }
                finally
                {
                    ThemeDocumentStore.SetThemeDirForTests(null);
                    ThemeDocumentStore.SetRecoveryDirForTests(null);
                    try { Directory.Delete(dir, true); } catch { }
                }
            });
        }

        [Fact]
        public void N04_GPU横向预览_形态切换接入同源GPU场景且消除待开发文案()
        {
            RunSta(() =>
            {
                string themeDir = CreateTempThemeDir(out string dir, out string themeName);
                string recoveryDir = Path.Combine(dir, "drafts");
                ThemeDocumentStore.SetThemeDirForTests(themeDir);
                ThemeDocumentStore.SetRecoveryDirForTests(recoveryDir);

                try
                {
                    using var form = new ThemeStudioForm();
                    form.OpenThemeForTesting(themeName);

                    // 切换到横向形态（index = 1）
                    form.SelectPreviewShapeForTesting(1);

                    // 断言：消除“GPU 横向预览待 TE04”
                    Assert.DoesNotContain("待 TE04", form.RouteStatusTextForTesting);
                    Assert.Contains("横向", form.RouteStatusTextForTesting);
                }
                finally
                {
                    ThemeDocumentStore.SetThemeDirForTests(null);
                    ThemeDocumentStore.SetRecoveryDirForTests(null);
                    try { Directory.Delete(dir, true); } catch { }
                }
            });
        }

        [Fact]
        public void N05_点选定位属性_按角色成功解析并聚焦检查器对应路径()
        {
            RunSta(() =>
            {
                string themeDir = CreateTempThemeDir(out string dir, out string themeName);
                string recoveryDir = Path.Combine(dir, "drafts");
                ThemeDocumentStore.SetThemeDirForTests(themeDir);
                ThemeDocumentStore.SetRecoveryDirForTests(recoveryDir);

                try
                {
                    using var form = new ThemeStudioForm();
                    form.OpenThemeForTesting(themeName);

                    // 1. 切换到兼容外观，测试回退样式角色映射
                    form.SelectTargetForTesting(ThemeEditTarget.GpuFallback);

                    bool focusedAccent = form.SelectPropertyByRole(ThemePropertyCatalog.SemanticElementRole.Title);
                    Assert.True(focusedAccent, "Title 角色应聚焦到 rendering.fallbackStyle.accent");
                    Assert.Contains("rendering.fallbackStyle.accent", form.SavedStatusTextForTesting);

                    bool focusedText = form.SelectPropertyByRole(ThemePropertyCatalog.SemanticElementRole.ItemValue);
                    Assert.True(focusedText, "ItemValue 角色应聚焦到 rendering.fallbackStyle.text");
                    Assert.Contains("rendering.fallbackStyle.text", form.SavedStatusTextForTesting);

                    // 2. 纯函数映射断言（覆盖经典与兼容外观）
                    string classicBg = ThemePropertyCatalog.ResolvePropertyPath(ThemePropertyCatalog.SemanticElementRole.PanelBackground, ThemeEditTarget.Classic);
                    Assert.Equal("color.background", classicBg);

                    string fallbackBg = ThemePropertyCatalog.ResolvePropertyPath(ThemePropertyCatalog.SemanticElementRole.PanelBackground, ThemeEditTarget.GpuFallback);
                    Assert.Equal("rendering.fallbackStyle.background", fallbackBg);
                }
                finally
                {
                    ThemeDocumentStore.SetThemeDirForTests(null);
                    ThemeDocumentStore.SetRecoveryDirForTests(null);
                    try { Directory.Delete(dir, true); } catch { }
                }
            });
        }

        private sealed class MockThemeApplyGateway : ThemeStudioForm.IThemeApplyGateway
        {
            public ThemeStudioForm.ThemeApplyResult ResultToReturn { get; set; } =
                new(true, ThemeStudioForm.ThemeApplyStage.ThemeApplied, null, "TestTheme", "GPU 场景生效", 1);

            public int CallCount { get; private set; }
            public string? LastAppliedTheme { get; private set; }
            public long LastRevision { get; private set; }

            public ThemeStudioForm.ThemeApplyResult ApplySavedTheme(string themeName, long revision)
            {
                CallCount++;
                LastAppliedTheme = themeName;
                LastRevision = revision;
                return ResultToReturn;
            }
        }

        [Fact]
        public void N06_应用结果细分_阶段失败与成功如实呈现()
        {
            RunSta(() =>
            {
                string themeDir = CreateTempThemeDir(out string dir, out string themeName);
                string recoveryDir = Path.Combine(dir, "drafts");
                ThemeDocumentStore.SetThemeDirForTests(themeDir);
                ThemeDocumentStore.SetRecoveryDirForTests(recoveryDir);

                var gateway = new MockThemeApplyGateway();

                try
                {
                    using var form = new ThemeStudioForm(gateway);
                    form.OpenThemeForTesting(themeName);

                    // 1. 测试 Settings 持久化阶段失败
                    gateway.ResultToReturn = new ThemeStudioForm.ThemeApplyResult(
                        Success: false,
                        Stage: ThemeStudioForm.ThemeApplyStage.SettingsPersisted,
                        Error: "磁盘写保护",
                        Revision: 1);

                    form.ApplyForTesting();
                    Assert.Contains("Settings 持久化失败", form.SavedStatusTextForTesting);
                    Assert.Contains("磁盘写保护", form.SavedStatusTextForTesting);

                    // 2. 测试运行时应用阶段失败
                    gateway.ResultToReturn = new ThemeStudioForm.ThemeApplyResult(
                        Success: false,
                        Stage: ThemeStudioForm.ThemeApplyStage.ThemeApplied,
                        Error: "D3D 设备丢失",
                        Revision: 1);

                    form.ApplyForTesting();
                    Assert.Contains("运行时应用失败", form.SavedStatusTextForTesting);
                    Assert.Contains("D3D 设备丢失", form.SavedStatusTextForTesting);

                    // 3. 测试正常应用成功
                    gateway.ResultToReturn = new ThemeStudioForm.ThemeApplyResult(
                        Success: true,
                        Stage: ThemeStudioForm.ThemeApplyStage.ThemeApplied,
                        EffectiveTheme: themeName,
                        EffectiveRoute: "GPU 场景生效",
                        Revision: 1);

                    form.ApplyForTesting();
                    Assert.Contains("已保存并应用", form.SavedStatusTextForTesting);
                    Assert.Contains("GPU 场景生效", form.SavedStatusTextForTesting);

                    // 4. 部分成功：主题已生效，后续菜单/任务栏失败；不得显示为完整成功
                    gateway.ResultToReturn = new ThemeStudioForm.ThemeApplyResult(
                        Success: false,
                        Partial: true,
                        Stage: ThemeStudioForm.ThemeApplyStage.ThemeApplied,
                        Error: "Menus：重建失败",
                        EffectiveTheme: themeName,
                        Revision: 1);
                    form.ApplyForTesting();
                    Assert.Contains("主题已生效", form.SavedStatusTextForTesting);
                    Assert.Contains("部分模块失败", form.SavedStatusTextForTesting);
                    Assert.Contains("不存在原子回滚", form.SavedStatusTextForTesting);
                }
                finally
                {
                    ThemeDocumentStore.SetThemeDirForTests(null);
                    ThemeDocumentStore.SetRecoveryDirForTests(null);
                    try { Directory.Delete(dir, true); } catch { }
                }
            });
        }

        [Fact]
        public void N07_导出与投稿准备_真实服务调用生成完整产物()
        {
            RunSta(() =>
            {
                string themeDir = CreateTempThemeDir(out string dir, out string themeName);
                string recoveryDir = Path.Combine(dir, "drafts");
                ThemeDocumentStore.SetThemeDirForTests(themeDir);
                ThemeDocumentStore.SetRecoveryDirForTests(recoveryDir);

                string outDir = Path.Combine(dir, "export_output");
                Directory.CreateDirectory(outDir);

                try
                {
                    using var form = new ThemeStudioForm();
                    form.OpenThemeForTesting(themeName);

                    // 1. 导出 JSON
                    string jsonOut = Path.Combine(outDir, "exported.json");
                    form.ExportJsonForTesting(jsonOut);
                    Assert.True(File.Exists(jsonOut), "JSON 导出文件必须存在");
                    string content = File.ReadAllText(jsonOut);
                    Assert.Contains(themeName, content);

                    // 2. 导出效果图
                    string pngOut = Path.Combine(outDir, "preview.png");
                    form.ExportEffectImageForTesting(pngOut);
                    Assert.True(File.Exists(pngOut), "效果图 PNG 文件必须存在");
                    Assert.True(new FileInfo(pngOut).Length > 0, "效果图文件不能为 0 字节");

                    // 3. 准备投稿资料
                    string subDir = Path.Combine(outDir, "submission");
                    form.PrepareSubmissionForTesting(subDir);
                    Assert.True(File.Exists(Path.Combine(subDir, "theme.json")), "投稿 theme.json 必须存在");
                    Assert.True(File.Exists(Path.Combine(subDir, "submission.json")), "投稿 submission.json 必须存在");
                    Assert.True(File.Exists(Path.Combine(subDir, "README.txt")), "投稿 README.txt 必须存在");
                    Assert.True(File.Exists(Path.Combine(subDir, "preview.png")), "投稿 preview.png 必须存在");
                }
                finally
                {
                    ThemeDocumentStore.SetThemeDirForTests(null);
                    ThemeDocumentStore.SetRecoveryDirForTests(null);
                    try { Directory.Delete(dir, true); } catch { }
                }
            });
        }
    }
}
