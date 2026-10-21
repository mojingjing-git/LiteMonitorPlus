using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiteMonitor.AppearanceResourceProbe
{
    /// <summary>
    /// A17「释放压力」实测用的资源采样器。
    ///
    /// 给定一个**正在运行**的 LiteMonitor 进程，按固定间隔采样
    /// 句柄数 / GDI 对象数 / USER 对象数 / 内存，输出时间序列与增长斜率，
    /// 用来判断「反复打开关闭设置页」「反复换肤」「反复隐藏恢复」等场景
    /// 是否出现句柄或内存的**持续增长**。
    ///
    /// 用法：
    ///   AppearanceResourceProbe (--pid &lt;pid&gt; | --name &lt;进程名&gt;)
    ///                       [--label &lt;场景名&gt;] [--interval-ms &lt;n&gt;]
    ///                       [--duration-sec &lt;n&gt;] [--out &lt;证据目录&gt;]
    ///
    /// 退出码：0 = 采样完成；1 = 参数/环境错误；2 = 采样过程中出错。
    ///
    /// ★ 本探针**不引用主工程**：只用 BCL + 三个 Win32 API，
    ///   编译不碰主工程的 obj/ ⇒ 可与其它会话的 build/test 并发执行。
    /// ★ 只**读取**目标进程的计数，不注入、不修改、不抓屏、不碰硬件。
    /// </summary>
    internal static class Program
    {
        // ── Win32 ──
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint GR_GDIOBJECTS = 0;
        private const uint GR_USEROBJECTS = 1;
        private const uint GR_HANDLE = 2;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        private sealed class Sample
        {
            public int Index { get; set; }
            public long ElapsedMs { get; set; }
            public int HandleCount { get; set; }
            public int GdiObjects { get; set; }
            public int UserObjects { get; set; }
            public long PrivateBytes { get; set; }
            public long WorkingSetBytes { get; set; }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception)
            {
                // 无控制台（输出被重定向）时忽略
            }

            int pid = 0;
            string? procName = null;
            string label = "unspecified";
            int intervalMs = 1000;
            int durationSec = 60;
            string outDir = Path.Combine(AppContext.BaseDirectory, "evidence");

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--pid":
                        if (!TryNext(args, ref i, out string? pidText) || !int.TryParse(pidText, out pid))
                        {
                            Console.Error.WriteLine("参数 --pid 需要一个整数。");
                            return 1;
                        }
                        break;
                    case "--name":
                        if (!TryNext(args, ref i, out procName)) { Console.Error.WriteLine("参数 --name 缺少值。"); return 1; }
                        break;
                    case "--label":
                        if (!TryNext(args, ref i, out label)) { Console.Error.WriteLine("参数 --label 缺少值。"); return 1; }
                        break;
                    case "--interval-ms":
                        if (!TryNext(args, ref i, out string? iv) || !int.TryParse(iv, out intervalMs) || intervalMs < 100)
                        {
                            Console.Error.WriteLine("参数 --interval-ms 需要一个 >= 100 的整数。");
                            return 1;
                        }
                        break;
                    case "--duration-sec":
                        if (!TryNext(args, ref i, out string? dv) || !int.TryParse(dv, out durationSec) || durationSec < 1)
                        {
                            Console.Error.WriteLine("参数 --duration-sec 需要一个 >= 1 的整数。");
                            return 1;
                        }
                        break;
                    case "--out":
                        if (!TryNext(args, ref i, out outDir)) { Console.Error.WriteLine("参数 --out 缺少值。"); return 1; }
                        break;
                    case "--help":
                    case "-h":
                        PrintUsage();
                        return 0;
                    default:
                        Console.Error.WriteLine("未知参数：" + args[i]);
                        PrintUsage();
                        return 1;
                }
            }

            if (pid <= 0 && string.IsNullOrEmpty(procName))
            {
                Console.Error.WriteLine("必须给 --pid 或 --name 之一。");
                PrintUsage();
                return 1;
            }

            try
            {
                if (pid <= 0) pid = ResolvePid(procName!);
                Console.WriteLine("目标进程：" + pid + "（" + SafeProcessName(pid) + "）");
                Console.WriteLine("场景标签：" + label);
                Console.WriteLine("采样：" + durationSec + " 秒，间隔 " + intervalMs + " ms");
                Console.WriteLine();

                var samples = CollectSamples(pid, intervalMs, durationSec, label);
                WriteEvidence(outDir, label, pid, samples);
                PrintSummary(samples);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("采样失败：" + ex.Message);
                return 2;
            }
        }

        private static bool TryNext(string[] args, ref int i, out string value)
        {
            if (i + 1 >= args.Length) { value = ""; return false; }
            value = args[i + 1];
            i++;
            return true;
        }

        private static int ResolvePid(string name)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length == 0)
                throw new InvalidOperationException("找不到名为 '" + name + "' 的进程。");

            // 选内存占用最大的那个（主实例），避免选到更新器之类的辅助进程。
            var sorted = procs.OrderByDescending(p =>
            {
                try { return p.WorkingSet64; } catch { return 0L; }
            }).ToArray();

            var chosen = sorted[0];
            foreach (var p in procs) { if (p.Id != chosen.Id) { try { p.Dispose(); } catch { } } }
            return chosen.Id;
        }

        private static string SafeProcessName(int pid)
        {
            try { return Process.GetProcessById(pid).ProcessName; } catch { return "?"; }
        }

        private static List<Sample> CollectSamples(int pid, int intervalMs, int durationSec, string label)
        {
            var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException(
                    "OpenProcess 失败（错误码 " + Marshal.GetLastWin32Error() +
                    "）。目标进程可能以管理员身份运行 —— 采样器也需要**以管理员身份运行**才能查询它的 GDI/USER 计数。");

            var samples = new List<Sample>();
            try
            {
                var sw = Stopwatch.StartNew();
                int total = Math.Max(1, durationSec * 1000 / intervalMs);
                for (int i = 0; i < total; i++)
                {
                    var s = new Sample
                    {
                        Index = i,
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };

                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        proc.Refresh();
                        s.HandleCount = proc.HandleCount;
                        s.PrivateBytes = proc.PrivateMemorySize64;
                        s.WorkingSetBytes = proc.WorkingSet64;
                    }
                    catch (Exception)
                    {
                        // 进程可能刚好退出了 —— 保留已采到的样本。
                    }

                    s.GdiObjects = (int)GetGuiResources(handle, GR_GDIOBJECTS);
                    s.UserObjects = (int)GetGuiResources(handle, GR_USEROBJECTS);

                    samples.Add(s);
                    Console.WriteLine(
                        "  t=" + (s.ElapsedMs / 1000.0).ToString("F1") + "s" +
                        "  句柄=" + s.HandleCount.ToString(CultureInfo.InvariantCulture) +
                        "  GDI=" + s.GdiObjects.ToString(CultureInfo.InvariantCulture) +
                        "  USER=" + s.UserObjects.ToString(CultureInfo.InvariantCulture) +
                        "  私存=" + (s.PrivateBytes / 1024 / 1024) + "MB" +
                        "  工作集=" + (s.WorkingSetBytes / 1024 / 1024) + "MB");

                    if (i < total - 1) Thread.Sleep(intervalMs);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
            return samples;
        }

        /// <summary>
        /// 增长斜率：只看**后半段**，避开启动/预热瞬态。
        /// 单位：个/分钟。
        /// </summary>
        private static double SlopePerMinute(List<Sample> samples, Func<Sample, double> selector)
        {
            if (samples.Count < 4) return 0;
            int from = samples.Count / 2;
            var seg = samples.Skip(from).ToList();
            double dtMs = seg[seg.Count - 1].ElapsedMs - seg[0].ElapsedMs;
            if (dtMs <= 0) return 0;
            double dv = selector(seg[seg.Count - 1]) - selector(seg[0]);
            return dv / dtMs * 60000.0;
        }

        private static void PrintSummary(List<Sample> samples)
        {
            if (samples.Count == 0) { Console.WriteLine("没有采到样本。"); return; }

            var first = samples[0];
            var last = samples[samples.Count - 1];

            double handleSlope = SlopePerMinute(samples, s => s.HandleCount);
            double gdiSlope = SlopePerMinute(samples, s => s.GdiObjects);
            double userSlope = SlopePerMinute(samples, s => s.UserObjects);
            double privSlope = SlopePerMinute(samples, s => s.PrivateBytes);

            Console.WriteLine();
            Console.WriteLine("── 汇总 ──");
            Console.WriteLine("样本数            ：" + samples.Count);
            Console.WriteLine("句柄  起→末       ：" + first.HandleCount + " → " + last.HandleCount + "（Δ " + (last.HandleCount - first.HandleCount) + "）");
            Console.WriteLine("句柄  后半段斜率   ：" + Format(handleSlope) + " 个/分钟");
            Console.WriteLine("GDI   起→末       ：" + first.GdiObjects + " → " + last.GdiObjects + "（Δ " + (last.GdiObjects - first.GdiObjects) + "）");
            Console.WriteLine("GDI   后半段斜率   ：" + Format(gdiSlope) + " 个/分钟");
            Console.WriteLine("USER  起→末       ：" + first.UserObjects + " → " + last.UserObjects + "（Δ " + (last.UserObjects - first.UserObjects) + "）");
            Console.WriteLine("USER  后半段斜率   ：" + Format(userSlope) + " 个/分钟");
            Console.WriteLine("私存  起→末       ：" + (first.PrivateBytes / 1024 / 1024) + "MB → " + (last.PrivateBytes / 1024 / 1024) + "MB");
            Console.WriteLine("私存  后半段斜率   ：" + Format(privSlope / 1024 / 1024) + " MB/分钟");

            Console.WriteLine();
            if (handleSlope > 5.0)
                Console.WriteLine("⚠ 句柄后半段仍在增长（" + Format(handleSlope) + " 个/分钟）—— 判「句柄持续增长」，A17 该项不通过。");
            if (gdiSlope > 5.0)
                Console.WriteLine("⚠ GDI 对象后半段仍在增长（" + Format(gdiSlope) + " 个/分钟）—— 判「GDI 持续增长」，A17 该项不通过。");
            if (userSlope > 5.0)
                Console.WriteLine("⚠ USER 对象后半段仍在增长（" + Format(userSlope) + " 个/分钟）—— 判「USER 持续增长」，A17 该项不通过。");
            if (handleSlope <= 5.0 && gdiSlope <= 5.0 && userSlope <= 5.0)
                Console.WriteLine("✓ 句柄/GDI/USER 的后半段斜率都在阈值内（≤ 5 个/分钟）。");
        }

        private static string Format(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

        private static void WriteEvidence(string outDir, string label, int pid, List<Sample> samples)
        {
            Directory.CreateDirectory(outDir);

            var csv = new StringBuilder();
            csv.AppendLine("index,elapsed_ms,handle_count,gdi_objects,user_objects,private_bytes,working_set_bytes");
            foreach (var s in samples)
            {
                csv.AppendLine(string.Join(",",
                    s.Index, s.ElapsedMs, s.HandleCount, s.GdiObjects, s.UserObjects, s.PrivateBytes, s.WorkingSetBytes));
            }
            File.WriteAllText(Path.Combine(outDir, "resource-samples.csv"), csv.ToString(), new UTF8Encoding(false));

            var arr = new JsonArray();
            foreach (var s in samples)
            {
                arr.Add(new JsonObject
                {
                    ["index"] = s.Index,
                    ["elapsedMs"] = s.ElapsedMs,
                    ["handleCount"] = s.HandleCount,
                    ["gdiObjects"] = s.GdiObjects,
                    ["userObjects"] = s.UserObjects,
                    ["privateBytes"] = s.PrivateBytes,
                    ["workingSetBytes"] = s.WorkingSetBytes,
                });
            }

            var root = new JsonObject
            {
                ["probe"] = "AppearanceResourceProbe",
                ["purpose"] = "A17 释放压力：句柄 / GDI / USER / 内存的时间序列与增长斜率",
                ["label"] = label,
                ["pid"] = pid,
                ["sampleCount"] = samples.Count,
                ["handles"] = new JsonObject
                {
                    ["first"] = samples.Count > 0 ? samples[0].HandleCount : 0,
                    ["last"] = samples.Count > 0 ? samples[samples.Count - 1].HandleCount : 0,
                    ["slopePerMinute"] = SlopePerMinute(samples, s => s.HandleCount),
                },
                ["gdiObjects"] = new JsonObject
                {
                    ["first"] = samples.Count > 0 ? samples[0].GdiObjects : 0,
                    ["last"] = samples.Count > 0 ? samples[samples.Count - 1].GdiObjects : 0,
                    ["slopePerMinute"] = SlopePerMinute(samples, s => s.GdiObjects),
                },
                ["userObjects"] = new JsonObject
                {
                    ["first"] = samples.Count > 0 ? samples[0].UserObjects : 0,
                    ["last"] = samples.Count > 0 ? samples[samples.Count - 1].UserObjects : 0,
                    ["slopePerMinute"] = SlopePerMinute(samples, s => s.UserObjects),
                },
                ["privateBytes"] = new JsonObject
                {
                    ["first"] = samples.Count > 0 ? samples[0].PrivateBytes : 0,
                    ["last"] = samples.Count > 0 ? samples[samples.Count - 1].PrivateBytes : 0,
                    ["slopePerMinute"] = SlopePerMinute(samples, s => s.PrivateBytes),
                },
                ["thresholdNote"] = "后半段斜率 > 5 个/分钟 即判『持续增长』；阈值见 A17 释放压力清单",
                ["samples"] = arr,
            };

            string path = Path.Combine(outDir, "resource-samples.json");
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine("证据：" + path);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("用法：AppearanceResourceProbe (--pid <pid> | --name <进程名>)");
            Console.WriteLine("      [--label <场景名>] [--interval-ms <n>] [--duration-sec <n>] [--out <证据目录>]");
            Console.WriteLine();
            Console.WriteLine("  --pid            目标进程 id");
            Console.WriteLine("  --name           目标进程名（取内存占用最大的那个实例）");
            Console.WriteLine("  --label          场景标签，写进证据（默认 unspecified）");
            Console.WriteLine("  --interval-ms    采样间隔，默认 1000");
            Console.WriteLine("  --duration-sec   采样时长，默认 60");
            Console.WriteLine("  --out            证据目录，默认 <可执行文件目录>\\evidence");
            Console.WriteLine();
            Console.WriteLine("★ 目标进程以管理员身份运行时，采样器也需要**以管理员身份运行**才能读到 GDI/USER 计数。");
        }
    }
}
