using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.SettingsPage;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 市场卡片 GPU 快照回归（Lane B 复审发现的句柄缺陷）：
    /// 未显示窗体不建 HWND ⇒ 渲染器从未启动并静默回退经典帧。
    /// 不变量：**直接 GPU 捕获能成功时，服务快照必须也成功**（无 GPU 环境自动跳过）。
    /// </summary>
    public class ThemePreviewServiceGpuSnapshotTests
    {
        private static Theme CreateGpuTheme()
        {
            var theme = new Theme { Name = "玻璃" };
            theme.Rendering = new ThemeRenderingConfig
            {
                Backend = ThemeRenderingConfig.GpuBackend,
                Material = ThemeRenderingConfig.LiquidGlassMaterial
            };
            theme.Rendering.Parameters["liveBackground"] = System.Text.Json.JsonSerializer.SerializeToElement(false);
            theme.Rendering.Parameters["glassStyle"] = System.Text.Json.JsonSerializer.SerializeToElement("standard");
            theme.Rendering.Fallback = new()
            {
                ["backend"] = System.Text.Json.JsonSerializer.SerializeToElement("legacy"),
                ["material"] = System.Text.Json.JsonSerializer.SerializeToElement("legacy")
            };
            theme.BuildFonts();
            return theme;
        }

        [Fact]
        public void 直接GPU捕获可行时_服务快照同样成功()
        {
            string dir = Path.Combine(Path.GetTempPath(), "litemonitor-snapshot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string directPng = Path.Combine(dir, "direct.png");
            string servicePng = Path.Combine(dir, "service.png");
            bool directOk = false;
            Exception? failure = null;

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    Theme theme = CreateGpuTheme();

                    // 1) 直接路径探针（同 Lane B 的建链方式）
                    directOk = TryDirectCapture(theme, directPng);

                    if (directOk)
                    {
                        // 2) 服务路径必须同样成功（回归：句柄未建 ⇒ 静默 false）
                        Assert.True(ThemePreviewService.TryRenderGpuSnapshot(theme, servicePng));
                        Assert.True(new FileInfo(servicePng).Length > 0);
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(60));

            try
            {
                Assert.Null(failure);
                if (!directOk)
                {
                    Console.WriteLine("[GPU snapshot] 本机 GPU 捕获不可用，按环境跳过服务断言。");
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        private static bool TryDirectCapture(Theme theme, string pngPath)
        {
            string guardDir = Path.Combine(Path.GetTempPath(), "litemonitor-guard-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var owner = new Form
                {
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-30000, -30000),
                    Size = new Size(280, 480),
                    FormBorderStyle = FormBorderStyle.None,
                    Opacity = 0d
                };
                var guard = new GpuStartupGuard(guardDir, AppContext.BaseDirectory);
                var host = new GpuThemePreviewControl(theme, ThemePreviewControl.CreateMockGroups(), guard)
                {
                    Bounds = new Rectangle(10, 10, 240, 420)
                };
                owner.Controls.Add(host);
                _ = owner.Handle;
                _ = host.Handle; // CreateControl() 对不可见控件空操作；必须强制建句柄
                if (!host.IsHandleCreated) return false;
                return host.TryCaptureOnce(pngPath);
            }
            catch
            {
                return false;
            }
            finally
            {
                try { if (Directory.Exists(guardDir)) Directory.Delete(guardDir, recursive: true); } catch { }
            }
        }
    }
}
