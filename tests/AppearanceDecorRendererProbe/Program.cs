using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using LiteMonitor.AppearanceDecorRendererProbe;

namespace LiteMonitor.AppearanceDecorRendererProbe
{
    /// <summary>
    /// A07 机能装饰 / 进度条渲染器自证探针入口。
    ///
    /// 用法：
    ///   AppearanceDecorRendererProbe [--out &lt;证据目录&gt;] [--groups &lt;functional|performance|all&gt;]
    ///
    /// 组选择：
    ///   functional   —— 只跑功能用例（默认推荐给非性能验收），跳过每帧成本预算；
    ///   performance  —— 只跑每帧成本预算（用户已延期，非性能验收不得使用）；
    ///   all          —— 全量（兼容历史调用）。
    ///
    /// 退出码：0 = 全部用例通过；1 = 有用例失败；2 = 探针自身异常。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string outDir = Path.Combine(AppContext.BaseDirectory, "evidence");
            string groups = "all";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length)
                {
                    outDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--groups" && i + 1 < args.Length)
                {
                    groups = args[i + 1].Trim().ToLowerInvariant();
                    i++;
                }
                else if (args[i] == "--help" || args[i] == "-h")
                {
                    Console.WriteLine("用法: AppearanceDecorRendererProbe [--out <证据目录>] [--groups <functional|performance|all>]");
                    return 0;
                }
            }

            try
            {
                Directory.CreateDirectory(outDir);

                // 隔离：不读也不写用户常用配置目录
                ProbeFixture.InstallSettingsSingleton();

                DecorRendererProbeRunner runner = new DecorRendererProbeRunner();
                Stopwatch sw = Stopwatch.StartNew();

                bool performanceExecuted;
                List<string> deferredCases = new();
                switch (groups)
                {
                    case "functional":
                        runner.RunFunctional();
                        performanceExecuted = false;
                        deferredCases.AddRange(DecorRendererProbeRunner.PerformanceCaseNames);
                        break;
                    case "performance":
                        runner.RunPerformance();
                        performanceExecuted = true;
                        break;
                    case "all":
                        runner.RunAll();
                        performanceExecuted = true;
                        break;
                    default:
                        Console.Error.WriteLine("未知 --groups: " + groups);
                        return 2;
                }
                sw.Stop();

                JsonArray cases = new();
                bool allPassed = true;
                foreach (ProbeCase c in runner.Cases)
                {
                    allPassed &= c.Passed;
                    cases.Add(new JsonObject
                    {
                        ["name"] = c.Name,
                        ["passed"] = c.Passed,
                        ["failures"] = new JsonArray(c.Failures.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                        ["details"] = c.Details
                    });

                    Console.WriteLine($"[{(c.Passed ? "PASS" : "FAIL")}] {c.Name}");
                    foreach (string f in c.Failures) Console.WriteLine("        - " + f);
                }

                JsonArray deferred = new();
                foreach (string name in deferredCases)
                {
                    deferred.Add(new JsonObject
                    {
                        ["name"] = name,
                        ["reason"] = DecorRendererProbeRunner.PerformanceDeferralReason
                    });
                }

                JsonObject root = new()
                {
                    ["kind"] = "appearance_decor_renderer_probe_result",
                    ["probeVersion"] = 1,
                    ["startedAt"] = DateTime.Now.ToString("o"),
                    ["assembly"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                    ["baseDirectory"] = AppContext.BaseDirectory,
                    ["elapsedMs"] = sw.ElapsedMilliseconds,
                    ["isolation"] = new JsonObject
                    {
                        ["hardwareMonitorCreated"] = false,
                        ["hardwareOpened"] = false,
                        ["screenCapturePerformed"] = false,
                        ["userSettingsRead"] = false,
                        ["userSettingsWritten"] = false,
                        ["note"] = "探针不创建 HardwareMonitor、不 Open 硬件、不做任何屏幕捕获；" +
                                  "Settings 由探针自己 new 出来并注入 Settings._instance 单例，不读也不写用户常用配置目录。"
                    },
                    ["selectedGroups"] = groups,
                    ["performanceExecuted"] = performanceExecuted,
                    ["deferredCases"] = deferred,
                    ["caseCount"] = runner.Cases.Count,
                    ["cases"] = cases,
                    ["allPassed"] = allPassed
                };

                string json = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                string evidencePath = Path.Combine(outDir, "decor-renderer-evidence.json");
                File.WriteAllText(evidencePath, json, new UTF8Encoding(false));

                Console.WriteLine();
                Console.WriteLine("证据文件: " + evidencePath);
                Console.WriteLine(allPassed ? "结果: 全部通过" : "结果: 存在失败用例");
                return allPassed ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("探针异常: " + ex);
                return 2;
            }
        }
    }
}
