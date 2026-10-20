using System.Diagnostics;
using System.Drawing;
using System.Text.Json.Nodes;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.PerformanceProbe;

/// <summary>
/// 两个与采集路径分离的微基准：
///  ① 单设备 Update 成本：定位到具体是哪个硬件对象的更新昂贵；
///  ② 兼容缓冲绘制成本：WinForms 兼容缓冲画布上绘制默认可见指标的 CPU 成本。
/// 两者都不包含窗口呈现、桌面合成、透明效果、任务栏。
/// </summary>
internal static class MicroBenchmarks
{
    public static JsonArray RunDeviceUpdateBench(ProbeEnvironment env, int callsPerDevice)
    {
        var results = new JsonArray();
        var hardware = env.Monitor.ComputerInstance.Hardware;
        using var process = Process.GetCurrentProcess();

        foreach (var hw in hardware)
        {
            MeasureDevice(env, process, results, hw, hw.Name, hw.HardwareType.ToString(), callsPerDevice);
            foreach (var sub in hw.SubHardware)
                MeasureDevice(env, process, results, sub, $"{hw.Name} / {sub.Name}", sub.HardwareType.ToString(), callsPerDevice);
        }
        return results;
    }

    private static void MeasureDevice(ProbeEnvironment env, Process process, JsonArray results,
        LibreHardwareMonitor.Hardware.IHardware hw, string name, string type, int calls)
    {
        try
        {
            for (int i = 0; i < 3; i++) hw.Update();  // 预热

            double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < calls; i++) hw.Update();
            clock.Stop();
            process.Refresh();
            double cpuAfter = process.TotalProcessorTime.TotalMilliseconds;

            results.Add(new JsonObject
            {
                ["kind"] = "device_update_cost",
                ["device"] = name,
                ["type"] = type,
                ["calls"] = calls,
                ["wallMsPerUpdate"] = Math.Round(clock.Elapsed.TotalMilliseconds / calls, 3),
                ["cpuMsPerUpdate"] = Math.Round((cpuAfter - cpuBefore) / calls, 3),
            });
        }
        catch (Exception ex)
        {
            results.Add(new JsonObject
            {
                ["kind"] = "device_update_cost",
                ["device"] = name,
                ["type"] = type,
                ["error"] = $"{ex.GetType().Name}: {ex.Message}",
            });
        }
    }

    /// <summary>
    /// 兼容缓冲绘制微基准。使用真实 UILayout / UIRenderer / HorizontalRenderer。
    /// </summary>
    public static JsonArray RunRenderBench(ProbeEnvironment env, double[] scales, double secondsPerCase)
    {
        var results = new JsonArray();
        var cfg = env.Cfg;

        foreach (double scale in scales)
        {
            foreach (bool horizontal in new[] { false, true })
            {
                results.Add(RenderCase(env, cfg, scale, horizontal, secondsPerCase));
            }
        }
        return results;
    }

    private static JsonObject RenderCase(ProbeEnvironment env, Settings cfg, double scale, bool horizontal, double seconds)
    {
        ThemeManager.Load("DarkFlat_Classic");
        var theme = ThemeManager.Current;
        theme.Scale(1f, (float)scale);
        theme.Layout.Width = (int)(cfg.PanelWidth * scale);

        var groups = cfg.MonitorItems
            .Where(x => x.VisibleInPanel)
            .GroupBy(x => x.UIGroup)
            .Select(g => new GroupLayoutInfo(g.Key, g.Select(x => new MetricItem
            {
                Key = x.Key,
                BoundConfig = x,
                Label = LanguageManager.T("Items." + LiteMonitor.src.Core.GpuKey.ToBaseKey(x.Key)),
                ShortLabel = LanguageManager.T("Short." + LiteMonitor.src.Core.GpuKey.ToBaseKey(x.Key)),
                DisplayValue = 40f,
                Value = 40f,
                TextValue = x.Key.StartsWith("DASH.", StringComparison.Ordinal) ? "12h 34m" : null!,
            }).ToList()) { Label = g.Key })
            .ToList();

        var items = groups.SelectMany(g => g.Items).ToList();
        if (items.Count == 0)
        {
            theme.DisposeFonts();
            return new JsonObject { ["kind"] = "render_bench", ["error"] = "没有可见监控项，未测量。" };
        }

        int height = new UILayout(theme).Build(groups);
        var columns = new List<Column>();
        for (int i = 0; i < items.Count; i += 2)
            columns.Add(new Column { Top = items[i], Bottom = i + 1 < items.Count ? items[i + 1] : null });

        var horizontalLayout = new HorizontalLayout(theme, theme.Layout.Width, LayoutMode.Horizontal, cfg);
        if (horizontal) height = horizontalLayout.Build(columns);
        int width = horizontal ? horizontalLayout.PanelWidth : theme.Layout.Width;

        using var bitmap = new Bitmap(Math.Max(1, width), Math.Max(1, height));
        using var form = new Form { ClientSize = new Size(width, height), ShowInTaskbar = false };
        using var targetGraphics = form.CreateGraphics();
        using var buffer = BufferedGraphicsManager.Current.Allocate(targetGraphics, new Rectangle(0, 0, width, height));
        using var bitmapGraphics = Graphics.FromImage(bitmap);
        var graphics = buffer.Graphics;   // WinForms 兼容缓冲画布

        int frame = 0;
        void Draw()
        {
            foreach (var item in items) item.DisplayValue = 40f + (++frame % 100) * 0.1f;
            if (horizontal) HorizontalRenderer.Render(graphics, theme, columns, width);
            else UIRenderer.Render(graphics, groups, theme);
        }

        for (int i = 0; i < 25; i++) Draw();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        using var process = Process.GetCurrentProcess();
        double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        int draws = 0;
        while (clock.Elapsed.TotalSeconds < seconds) { Draw(); draws++; }
        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        process.Refresh();
        double cpuMsPerDraw = (process.TotalProcessorTime.TotalMilliseconds - cpuBefore) / draws;

        theme.DisposeFonts();

        return new JsonObject
        {
            ["kind"] = "render_bench",
            ["surface"] = "winforms-compatible-buffer",
            ["mode"] = horizontal ? "horizontal" : "vertical",
            ["scale"] = scale,
            ["itemCount"] = items.Count,
            ["width"] = width,
            ["height"] = height,
            ["draws"] = draws,
            ["wallMsPerDraw"] = Math.Round(clock.Elapsed.TotalMilliseconds / draws, 4),
            ["cpuMsPerDraw"] = Math.Round(cpuMsPerDraw, 4),
            ["allocatedBytesPerDraw"] = Math.Round((double)allocated / draws, 1),
            ["projectedWholeMachineCpuAt10Hz"] = Math.Round(cpuMsPerDraw * 10 / 1000 / Environment.ProcessorCount * 100, 4),
            ["excludes"] = "窗口呈现、桌面合成、透明效果、任务栏、硬件采集、插件",
        };
    }
}
