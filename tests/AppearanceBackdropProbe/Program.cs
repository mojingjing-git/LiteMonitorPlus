using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AppearanceBackdropProbe.Evidence;
using AppearanceBackdropProbe.Scenarios;

namespace AppearanceBackdropProbe
{
    /// <summary>命令行参数。</summary>
    internal sealed class Options
    {
        public string Scenario { get; set; } = "all";
        public string OutDir { get; set; } = "";
        public int DisplayIndex { get; set; } = -1;
        public int Width { get; set; } = 240;
        public int Height { get; set; } = 788;
        public int DurationSec { get; set; } = 30;
        public double Fps { get; set; } = 2.0;
        public int Margin { get; set; } = 120;
        public double CaptureScale { get; set; } = 1.0;
        public int Blur { get; set; } = 12;
        public string PwshPath { get; set; } = "";
        public string ScriptDir { get; set; } = "";

        /// <summary>
        /// 窗口策略：<c>topmost</c>（历史行为，置顶并可能激活）或
        /// <c>nonactivating</c>（不置顶、不抢前台焦点，用于不得干扰用户桌面的验收）。
        /// </summary>
        public string WindowMode { get; set; } = "topmost";

        /// <summary>是否置顶试验窗口。</summary>
        public bool UsesTopMost => !string.Equals(WindowMode, "nonactivating", StringComparison.OrdinalIgnoreCase);

        /// <summary>是否允许试验窗口激活并抢走前台焦点。</summary>
        public bool AllowsActivation => UsesTopMost;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next(string name) => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"参数 {name} 缺少值");

                switch (a.ToLowerInvariant())
                {
                    case "--scenario": o.Scenario = Next(a).ToLowerInvariant(); break;
                    case "--window-mode": o.WindowMode = Next(a).ToLowerInvariant(); break;
                    case "--out": o.OutDir = Next(a); break;
                    case "--display": o.DisplayIndex = int.Parse(Next(a)); break;
                    case "--width": o.Width = int.Parse(Next(a)); break;
                    case "--height": o.Height = int.Parse(Next(a)); break;
                    case "--duration": o.DurationSec = int.Parse(Next(a)); break;
                    case "--fps": o.Fps = double.Parse(Next(a)); break;
                    case "--margin": o.Margin = int.Parse(Next(a)); break;
                    case "--capture-scale": o.CaptureScale = double.Parse(Next(a)); break;
                    case "--blur": o.Blur = int.Parse(Next(a)); break;
                    case "--pwsh": o.PwshPath = Next(a); break;
                    case "--script-dir": o.ScriptDir = Next(a); break;
                    default:
                        Console.WriteLine($"[warn] 未知参数: {a}");
                        break;
                }
            }
            return o;
        }
    }

    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        private const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        private static int Main(string[] args)
        {
            try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Options opts;
            try { opts = Options.Parse(args); }
            catch (Exception ex) { Console.WriteLine($"参数解析失败: {ex.Message}"); return 2; }

            string outDir = string.IsNullOrWhiteSpace(opts.OutDir)
                ? Path.Combine(AppContext.BaseDirectory, "output", DateTime.Now.ToString("yyyyMMdd-HHmmss"))
                : Path.GetFullPath(opts.OutDir);
            Directory.CreateDirectory(outDir);

            var log = new LogWriter(Path.Combine(outDir, "probe.log"));
            log.Info($"A06 探针启动 scenario={opts.Scenario} out={outDir}");

            try
            {
                var env = EnvProbe.Capture(opts.Scenario == "env" ? "asInvoker" : "asInvoker(默认；生产为 requireAdministrator)");
                EvidenceWriter.WriteJson(Path.Combine(outDir, "environment.json"), env);
                log.Info($"环境已记录: OS={env.OsVersion} build={env.OsBuild} elev={env.IsElevated} rdp={env.IsRemoteSession}");

                switch (opts.Scenario)
                {
                    case "env":
                        break;

                    case "dwm":
                        DwmScenario.Run(opts, outDir, log);
                        break;

                    case "capture":
                        CaptureScenario.Run(opts, outDir, log);
                        break;

                    case "blur":
                        BlurScenario.Run(opts, outDir, log);
                        break;

                    case "perf":
                        PerfScenario.Run(opts, outDir, log);
                        break;

                    case "diag":
                        DiagScenario.Run(opts, outDir, log);
                        break;

                    case "all":
                        DwmScenario.Run(opts, outDir, log);
                        CaptureScenario.Run(opts, outDir, log);
                        DiagScenario.Run(opts, outDir, log);
                        BlurScenario.Run(opts, outDir, log);
                        PerfScenario.Run(opts, outDir, log);
                        break;

                    default:
                        log.Info($"未知场景: {opts.Scenario}");
                        return 3;
                }

                log.Info("场景执行完成");
                return 0;
            }
            catch (Exception ex)
            {
                log.Info($"致命错误: {ex}");
                return 1;
            }
            finally
            {
                try { Application.ExitThread(); } catch { }
            }
        }
    }
}
