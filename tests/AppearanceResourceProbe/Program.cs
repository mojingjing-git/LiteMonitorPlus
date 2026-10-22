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
    ///                       [--idle-baseline]
    ///
    /// 退出码：
    ///   0 = 全部门槛通过；
    ///   1 = 参数/环境错误；
    ///   2 = 采样过程中出错；
    ///   3 = 有门槛未通过**或样本不足无法判定**（★ 不作通过结论）。
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

        // ── 判定门槛（统一口径，来源见 A17 清单 §2.4）──

        /// <summary>句柄 / GDI / USER：后半段最小二乘斜率上限（个/分钟）。</summary>
        private const double SlopeThresholdPerMinute = 5.0;

        /// <summary>私存：单场景增量上限（字节）= 30 MB。</summary>
        private const long PrivateDeltaThresholdBytes = 30L * 1024 * 1024;

        /// <summary>后半段最少样本数 —— 少于这个数，斜率不可判定。</summary>
        private const int MinSecondHalfSamples = 4;

        /// <summary>后半段最少时间跨度（毫秒）—— 跨度太短，外推「每分钟」没有意义。</summary>
        private const long MinSecondHalfSpanMs = 5000;

        private sealed class Sample
        {
            public int Index { get; set; }
            public long ElapsedMs { get; set; }
            public int HandleCount { get; set; }
            public int GdiObjects { get; set; }
            public int UserObjects { get; set; }
            public long PrivateBytes { get; set; }
            public long WorkingSetBytes { get; set; }

            /// <summary>本次采样没能读到目标进程（进程可能已退出）⇒ 读数不可信。</summary>
            public bool ReadFailed { get; set; }
        }

        /// <summary>一次采样的判定结果。</summary>
        private sealed class Verdict
        {
            /// <summary>样本是否足以判定（不足 ⇒ false，**不作通过结论**）。</summary>
            public bool Judgable { get; set; } = true;

            /// <summary>可判定，且所有门槛都过。</summary>
            public bool Passed { get; set; } = true;

            /// <summary>进程退出码：0 = 全通过；3 = 未通过或不可判定。</summary>
            public int ExitCode { get; set; }

            /// <summary>门槛未通过。</summary>
            public List<string> Failures { get; } = new List<string>();

            /// <summary>样本不足 / 读数不可信 ⇒ 不可判定（≠ 未通过，但同样不作通过结论）。</summary>
            public List<string> Undetermined { get; } = new List<string>();

            public List<string> Notes { get; } = new List<string>();
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
            bool idleBaseline = false;

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
                    case "--idle-baseline":
                        idleBaseline = true;
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
                Console.WriteLine("场景标签：" + label + (idleBaseline ? "（空闲基线）" : ""));
                Console.WriteLine("采样：" + durationSec + " 秒，间隔 " + intervalMs + " ms");
                Console.WriteLine();

                var samples = CollectSamples(pid, intervalMs, durationSec, label);
                var verdict = Evaluate(samples, idleBaseline);
                WriteEvidence(outDir, label, pid, samples, verdict, idleBaseline);
                PrintSummary(samples, verdict, idleBaseline);
                return verdict.ExitCode;
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
                        // 进程可能刚好退出了 —— 保留已采到的样本，但标记读数不可信。
                        s.ReadFailed = true;
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
                        "  工作集=" + (s.WorkingSetBytes / 1024 / 1024) + "MB" +
                        (s.ReadFailed ? "  ⚠ 本次未读到目标进程" : ""));

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
        /// 用**最小二乘直线拟合**（不是两端点差分）—— 单次 GC 或一次 UI 拖动
        /// 在两端点差分里能被放大成 ±几十，30 分钟长时场景尤易误判。
        ///
        /// x 用相对首样本的毫秒数，避免绝对时间戳带来的数值稳定性问题。
        /// 返回 null = **不可判定**（样本不足 / 跨度太短）。
        /// 单位：个/分钟。
        /// </summary>
        private static double? SlopePerMinute(List<Sample> samples, Func<Sample, double> selector)
        {
            if (samples.Count < 2) return null;

            var seg = samples.Skip(samples.Count / 2).ToList();
            if (seg.Count < MinSecondHalfSamples) return null;

            double spanMs = seg[seg.Count - 1].ElapsedMs - seg[0].ElapsedMs;
            if (spanMs < MinSecondHalfSpanMs) return null;

            double x0 = seg[0].ElapsedMs;
            double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
            foreach (var s in seg)
            {
                double x = s.ElapsedMs - x0;
                double y = selector(s);
                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumXX += x * x;
            }

            double n = seg.Count;
            double denom = n * sumXX - sumX * sumX;
            if (Math.Abs(denom) < 1e-9) return null;

            double perMs = (n * sumXY - sumX * sumY) / denom;
            return perMs * 60000.0;
        }

        /// <summary>后半段的样本数与时间跨度（用于报告与不可判定说明）。</summary>
        private static (int Count, long SpanMs) SecondHalfShape(List<Sample> samples)
        {
            if (samples.Count < 2) return (0, 0);
            var seg = samples.Skip(samples.Count / 2).ToList();
            return (seg.Count, seg[seg.Count - 1].ElapsedMs - seg[0].ElapsedMs);
        }

        /// <summary>
        /// 统一门槛判定：
        ///   - 句柄 / GDI / USER：后半段最小二乘斜率 ≤ 5 个/分钟；
        ///   - 私存：单场景增量 ≤ 30 MB；
        ///   - 样本不足 ⇒ **不可判定**，不作出通过结论（退出码 3）。
        /// </summary>
        private static Verdict Evaluate(List<Sample> samples, bool idleBaseline)
        {
            var v = new Verdict();

            if (samples.Count == 0)
            {
                v.Judgable = false;
                v.Passed = false;
                v.Undetermined.Add("没有采到任何样本 —— 无法判定。");
                v.ExitCode = 3;
                return v;
            }

            // 目标进程中途退出 ⇒ 读数不可信
            // （句柄/GDI/USER 会掉到 0，看起来像「不增长」⇒ 假通过）。
            if (samples.Any(s => s.ReadFailed))
            {
                v.Judgable = false;
                v.Passed = false;
                v.Undetermined.Add("有样本读不到目标进程（进程可能已退出）—— 读数不可信，无法判定。");
            }

            // 三项资源指标共用同一个门槛；不可判定时合并成一条，避免刷屏。
            var unjudgeable = new List<string>();
            EvaluateSlope(v, "句柄", samples, s => s.HandleCount, unjudgeable);
            EvaluateSlope(v, "GDI 对象", samples, s => s.GdiObjects, unjudgeable);
            EvaluateSlope(v, "USER 对象", samples, s => s.UserObjects, unjudgeable);

            if (unjudgeable.Count > 0)
            {
                v.Judgable = false;
                v.Passed = false;
                v.Undetermined.Add(
                    string.Join(" / ", unjudgeable) + " —— 样本不足，无法判定" +
                    "（后半段需要 ≥ " + MinSecondHalfSamples + " 个样本且跨度 ≥ " +
                    (MinSecondHalfSpanMs / 1000) + " 秒）。");
            }

            long privateDelta = samples[samples.Count - 1].PrivateBytes - samples[0].PrivateBytes;
            if (privateDelta > PrivateDeltaThresholdBytes)
            {
                v.Passed = false;
                v.Failures.Add(
                    "私存 —— 单场景增量 " + (privateDelta / 1024 / 1024) + "MB > 门槛 " +
                    (PrivateDeltaThresholdBytes / 1024 / 1024) + "MB，判「内存持续增长」。");
            }

            if (idleBaseline)
            {
                v.Notes.Add(
                    "空闲基线：绝对增量 Δ 应接近 0 —— 上面每个指标的「起→末 Δ」即为 Δ，" +
                    "工具只对**斜率**做硬门槛，Δ 单独报告供人工确认。");
            }

            v.ExitCode = (v.Judgable && v.Passed) ? 0 : 3;
            return v;
        }

        private static void EvaluateSlope(Verdict v, string name, List<Sample> samples, Func<Sample, double> selector, List<string> unjudgeable)
        {
            double? slope = SlopePerMinute(samples, selector);
            if (slope == null)
            {
                unjudgeable.Add(name);
                return;
            }

            if (slope.Value > SlopeThresholdPerMinute)
            {
                v.Passed = false;
                v.Failures.Add(
                    name + " —— 后半段斜率 " + Format(slope.Value) + " 个/分钟 > 门槛 " +
                    Format(SlopeThresholdPerMinute) + " 个/分钟，判「" + name + "持续增长」。");
            }
        }

        private static void PrintSummary(List<Sample> samples, Verdict verdict, bool idleBaseline)
        {
            Console.WriteLine();
            Console.WriteLine("── 汇总 ──");
            Console.WriteLine("样本数            ：" + samples.Count);

            var shape = SecondHalfShape(samples);
            Console.WriteLine("后半段            ：" + shape.Count + " 个样本 / 跨度 " +
                              (shape.SpanMs / 1000.0).ToString("F1") + " 秒" +
                              "（判定要求 ≥ " + MinSecondHalfSamples + " 个样本 / ≥ " +
                              (MinSecondHalfSpanMs / 1000) + " 秒）");

            if (samples.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("── 判定 ──");
                foreach (var u in verdict.Undetermined) Console.WriteLine("⚠ " + u);
                return;
            }

            var first = samples[0];
            var last = samples[samples.Count - 1];

            double? handleSlope = SlopePerMinute(samples, s => s.HandleCount);
            double? gdiSlope = SlopePerMinute(samples, s => s.GdiObjects);
            double? userSlope = SlopePerMinute(samples, s => s.UserObjects);
            double? privSlope = SlopePerMinute(samples, s => s.PrivateBytes);

            Console.WriteLine("句柄  起→末       ：" + first.HandleCount + " → " + last.HandleCount + "（Δ " + (last.HandleCount - first.HandleCount) + "）");
            Console.WriteLine("句柄  后半段斜率   ：" + FormatSlope(handleSlope) + " 个/分钟");
            Console.WriteLine("GDI   起→末       ：" + first.GdiObjects + " → " + last.GdiObjects + "（Δ " + (last.GdiObjects - first.GdiObjects) + "）");
            Console.WriteLine("GDI   后半段斜率   ：" + FormatSlope(gdiSlope) + " 个/分钟");
            Console.WriteLine("USER  起→末       ：" + first.UserObjects + " → " + last.UserObjects + "（Δ " + (last.UserObjects - first.UserObjects) + "）");
            Console.WriteLine("USER  后半段斜率   ：" + FormatSlope(userSlope) + " 个/分钟");
            Console.WriteLine("私存  起→末       ：" + (first.PrivateBytes / 1024 / 1024) + "MB → " + (last.PrivateBytes / 1024 / 1024) + "MB（Δ " +
                              ((last.PrivateBytes - first.PrivateBytes) / 1024 / 1024) + "MB）");
            Console.WriteLine("私存  后半段斜率   ：" + FormatSlope(privSlope.HasValue ? privSlope.Value / 1024 / 1024 : (double?)null) + " MB/分钟");

            Console.WriteLine();
            Console.WriteLine("── 判定 ──");
            Console.WriteLine("门槛：句柄/GDI/USER 后半段最小二乘斜率 ≤ " + Format(SlopeThresholdPerMinute) +
                              " 个/分钟；私存单场景增量 ≤ " + (PrivateDeltaThresholdBytes / 1024 / 1024) + "MB。");

            foreach (var f in verdict.Failures) Console.WriteLine("✗ " + f);
            foreach (var u in verdict.Undetermined) Console.WriteLine("⚠ " + u);
            foreach (var n in verdict.Notes) Console.WriteLine("· " + n);

            if (verdict.Judgable && verdict.Passed)
            {
                Console.WriteLine("✓ 全部门槛通过（斜率均在门槛内，私存单场景增量在 " +
                                  (PrivateDeltaThresholdBytes / 1024 / 1024) + "MB 内）。");
            }
            else if (!verdict.Judgable)
            {
                Console.WriteLine("⚠ 样本不足 / 读数不可信 —— **无法判定**，不作通过结论（退出码 3）。");
            }
            else
            {
                Console.WriteLine("✗ 有门槛未通过 —— A17「无句柄/内存持续增长」该项不通过（退出码 3）。");
            }
        }

        private static string Format(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

        /// <summary>斜率格式化；不可判定时明确写「不可判定」而不是 0.00（避免读成「不增长」）。</summary>
        private static string FormatSlope(double? v) => v.HasValue ? Format(v.Value) : "不可判定";

        private static void WriteEvidence(string outDir, string label, int pid, List<Sample> samples, Verdict verdict, bool idleBaseline)
        {
            Directory.CreateDirectory(outDir);

            var csv = new StringBuilder();
            csv.AppendLine("index,elapsed_ms,handle_count,gdi_objects,user_objects,private_bytes,working_set_bytes,read_failed");
            foreach (var s in samples)
            {
                csv.AppendLine(string.Join(",",
                    s.Index, s.ElapsedMs, s.HandleCount, s.GdiObjects, s.UserObjects, s.PrivateBytes, s.WorkingSetBytes,
                    s.ReadFailed ? 1 : 0));
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
                    ["readFailed"] = s.ReadFailed,
                });
            }

            double? handleSlope = SlopePerMinute(samples, s => s.HandleCount);
            double? gdiSlope = SlopePerMinute(samples, s => s.GdiObjects);
            double? userSlope = SlopePerMinute(samples, s => s.UserObjects);
            double? privSlope = SlopePerMinute(samples, s => s.PrivateBytes);
            var shape = SecondHalfShape(samples);

            var root = new JsonObject
            {
                ["probe"] = "AppearanceResourceProbe",
                ["purpose"] = "A17 释放压力：句柄 / GDI / USER / 内存的时间序列与增长斜率",
                ["label"] = label,
                ["pid"] = pid,
                ["idleBaseline"] = idleBaseline,
                ["sampleCount"] = samples.Count,
                ["secondHalf"] = new JsonObject
                {
                    ["sampleCount"] = shape.Count,
                    ["spanMs"] = shape.SpanMs,
                },
                ["handles"] = new JsonObject
                {
                    ["first"] = samples.Count > 0 ? samples[0].HandleCount : 0,
                    ["last"] = samples.Count > 0 ? samples[samples.Count - 1].HandleCount : 0,
                    ["delta"] = samples.Count > 0 ? samples[samples.Count - 1].HandleCount - samples[0].HandleCount : 0,
                    ["slopePerMinute"] = NullableNode(handleSlope),
                    ["slopeJudgable"] = handleSlope.HasValue,
                },
                ["gdiObjects"] = new JsonObject
                {
                    ["first"] = samples.Count > 0 ? samples[0].GdiObjects : 0,
                    ["last"] = samples.Count > 0 ? samples[samples.Count - 1].GdiObjects : 0,
                    ["delta"] = samples.Count > 0 ? samples[samples.Count - 1].GdiObjects - samples[0].GdiObjects : 0,
                    ["slopePerMinute"] = NullableNode(gdiSlope),
                    ["slopeJudgable"] = gdiSlope.HasValue,
                },
                ["userObjects"] = new JsonObject
                {
                    ["first"] = samples.Count > 0 ? samples[0].UserObjects : 0,
                    ["last"] = samples.Count > 0 ? samples[samples.Count - 1].UserObjects : 0,
                    ["delta"] = samples.Count > 0 ? samples[samples.Count - 1].UserObjects - samples[0].UserObjects : 0,
                    ["slopePerMinute"] = NullableNode(userSlope),
                    ["slopeJudgable"] = userSlope.HasValue,
                },
                ["privateBytes"] = new JsonObject
                {
                    ["first"] = samples.Count > 0 ? samples[0].PrivateBytes : 0,
                    ["last"] = samples.Count > 0 ? samples[samples.Count - 1].PrivateBytes : 0,
                    ["delta"] = samples.Count > 0 ? samples[samples.Count - 1].PrivateBytes - samples[0].PrivateBytes : 0,
                    ["slopePerMinute"] = NullableNode(privSlope),
                    ["slopeJudgable"] = privSlope.HasValue,
                },
                ["judgement"] = new JsonObject
                {
                    ["judgable"] = verdict.Judgable,
                    ["passed"] = verdict.Passed,
                    ["exitCode"] = verdict.ExitCode,
                    ["failures"] = ToJsonArray(verdict.Failures),
                    ["undetermined"] = ToJsonArray(verdict.Undetermined),
                    ["notes"] = ToJsonArray(verdict.Notes),
                    ["thresholds"] = new JsonObject
                    {
                        ["slopePerMinute"] = SlopeThresholdPerMinute,
                        ["privateDeltaBytes"] = PrivateDeltaThresholdBytes,
                        ["minSecondHalfSamples"] = MinSecondHalfSamples,
                        ["minSecondHalfSpanMs"] = MinSecondHalfSpanMs,
                    },
                },
                ["thresholdNote"] = "统一门槛：后半段最小二乘斜率 ≤ 5 个/分钟（句柄/GDI/USER）；私存单场景增量 ≤ 30MB；样本不足 ⇒ 不可判定（退出码 3）。阈值见 A17 释放压力清单 §2.4",
                ["samples"] = arr,
            };

            string path = Path.Combine(outDir, "resource-samples.json");
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine("证据：" + path);
        }

        private static JsonNode? NullableNode(double? v) => v.HasValue ? JsonValue.Create(v.Value) : null;

        private static JsonArray ToJsonArray(IEnumerable<string> items)
        {
            var arr = new JsonArray();
            foreach (var item in items) arr.Add(JsonValue.Create(item));
            return arr;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("用法：AppearanceResourceProbe (--pid <pid> | --name <进程名>)");
            Console.WriteLine("      [--label <场景名>] [--interval-ms <n>] [--duration-sec <n>] [--out <证据目录>]");
            Console.WriteLine("      [--idle-baseline]");
            Console.WriteLine();
            Console.WriteLine("  --pid            目标进程 id");
            Console.WriteLine("  --name           目标进程名（取内存占用最大的那个实例）");
            Console.WriteLine("  --label          场景标签，写进证据（默认 unspecified）");
            Console.WriteLine("  --interval-ms    采样间隔，默认 1000");
            Console.WriteLine("  --duration-sec   采样时长，默认 60");
            Console.WriteLine("  --out            证据目录，默认 <可执行文件目录>\\evidence");
            Console.WriteLine("  --idle-baseline  标记本轮为「空闲基线」：额外报告绝对增量 Δ，供人工确认");
            Console.WriteLine();
            Console.WriteLine("退出码：0 = 全部门槛通过；1 = 参数/环境错误；2 = 采样出错；");
            Console.WriteLine("        3 = 有门槛未通过**或样本不足无法判定**。");
            Console.WriteLine();
            Console.WriteLine("★ 目标进程以管理员身份运行时，采样器也需要**以管理员身份运行**才能读到 GDI/USER 计数。");
        }
    }
}
