using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// R10：GPU 资源门禁不能只看句柄/设备数；必须在真实 GPU 上验证
    /// 首帧、目标切换后的持续出帧，以及热更新不重建设备。
    /// </summary>
    public sealed class R10GpuFrameGateTests
    {
        private const string GpuThemeName = "CyberPunk_GPU";

        [GpuFact]
        public void R10_真实GPU_首帧与目标切换后持续出帧_且不重建设备()
        {
            string themesDir = ResolveGpuThemesDirectory();
            ThemeDocumentStore.SetThemeDirForTests(themesDir);
            Settings cfg = Settings.Load();
            bool previous = cfg.GpuThemeEffectsEnabled;

            try
            {
                cfg.GpuThemeEffectsEnabled = true;

                RunSta(() =>
                {
                    try { Application.EnableVisualStyles(); } catch { }
                    using var form = new ThemeStudioForm();
                    form.Show();
                    Application.DoEvents();

                    form.OpenThemeForTesting(GpuThemeName);
                    form.RefreshPreviewForTesting();
                    ThemePreviewControl? preview = WaitForPreview(form, TimeSpan.FromSeconds(15));
                    Assert.NotNull(preview);

                    int firstFrames = WaitForFrames(preview!, 1, TimeSpan.FromSeconds(15));
                    Assert.True(preview!.IsGpuPreviewActive,
                        "GPU 预览没有激活：" + (form.PreviewErrorForTesting ?? preview.GpuPreviewError ?? "unknown"));
                    Assert.True(firstFrames > 0, "没有观察到首帧。");
                    int devicesBefore = preview.DeviceCreationCount;
                    Assert.True(devicesBefore >= 1, "GPU 设备创建次数应至少为 1。");

                    // 真实目标下拉：先切兼容外观（GPU 预览应隐藏），再切回主效果；
                    // 只有回到主效果后才要求继续出帧。不能把“切到兼容外观后 GPU 停帧”误判为失败。
                    for (int i = 0; i < 3; i++)
                    {
                        form.TargetComboForTesting.SelectedIndex = 1;
                        Application.DoEvents();
                        Thread.Sleep(50);

                        int before = preview.PresentedFrameCount;
                        form.TargetComboForTesting.SelectedIndex = 0;
                        Application.DoEvents();
                        Assert.True(WaitForGpuActive(preview, TimeSpan.FromSeconds(5)),
                            $"第 {i + 1} 次切回主效果后 GPU 预览未恢复：{form.PreviewErrorForTesting ?? preview.GpuPreviewError}");
                        int after = WaitForFrames(preview, before + 1, TimeSpan.FromSeconds(10));
                        Assert.True(after > before, $"第 {i + 1} 次切回主效果后没有新帧。");
                    }

                    Assert.Equal(devicesBefore, preview.DeviceCreationCount);
                    form.Close();
                    Application.DoEvents();
                });
            }
            finally
            {
                cfg.GpuThemeEffectsEnabled = previous;
                ThemeDocumentStore.SetThemeDirForTests(null);
            }
        }

        private static ThemePreviewControl? WaitForPreview(ThemeStudioForm form, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                Application.DoEvents();
                ThemePreviewControl? preview = form.PreviewControlForTesting;
                if (preview != null && preview.IsHandleCreated && preview.PresentedFrameCount > 0)
                    return preview;
                Thread.Sleep(20);
            }

            return form.PreviewControlForTesting;
        }

        private static bool WaitForGpuActive(ThemePreviewControl preview, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                Application.DoEvents();
                if (preview.IsGpuPreviewActive) return true;
                Thread.Sleep(20);
            }

            return preview.IsGpuPreviewActive;
        }

        private static int WaitForFrames(ThemePreviewControl preview, int target, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                Application.DoEvents();
                if (preview.PresentedFrameCount >= target) return preview.PresentedFrameCount;
                Thread.Sleep(20);
            }

            return preview.PresentedFrameCount;
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
            thread.IsBackground = true;
            thread.Start();

            if (!thread.Join(TimeSpan.FromMinutes(2)))
                throw new TimeoutException("R10 GPU 门禁 STA 线程未在 2 分钟内完成。");

            if (captured != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
        }

        private static string ResolveGpuThemesDirectory()
        {
            string direct = Path.Combine(AppContext.BaseDirectory, "resources", "themes");
            if (Directory.Exists(direct) && File.Exists(Path.Combine(direct, GpuThemeName + ".json")))
                return direct;

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "themes");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, GpuThemeName + ".json")))
                    return candidate;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("未能定位到 resources/themes 目录。");
        }
    }
}
