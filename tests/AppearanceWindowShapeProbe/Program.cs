using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace LiteMonitor.AppearanceWindowShapeProbe
{
    /// <summary>
    /// A09 窗口轮廓策略自证探针入口。
    ///
    /// 用法：
    ///   AppearanceWindowShapeProbe [--out &lt;证据目录&gt;]
    ///
    /// 退出码：0 = 全部用例通过；1 = 有用例失败；2 = 探针自身异常。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string outDir = Path.Combine(AppContext.BaseDirectory, "evidence");

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length)
                {
                    outDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--help" || args[i] == "-h")
                {
                    Console.WriteLine("用法: AppearanceWindowShapeProbe [--out <证据目录>]");
                    return 0;
                }
            }

            try
            {
                Directory.CreateDirectory(outDir);

                WindowShapeProbeRunner runner = new WindowShapeProbeRunner();
                Stopwatch sw = Stopwatch.StartNew();
                runner.RunAll();
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

                JsonObject root = new()
                {
                    ["kind"] = "appearance_window_shape_probe_result",
                    ["probeVersion"] = 1,
                    ["startedAt"] = DateTime.Now.ToString("o"),
                    ["os"] = Environment.OSVersion.Version.ToString(),
                    ["assembly"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                    ["baseDirectory"] = AppContext.BaseDirectory,
                    ["elapsedMs"] = sw.ElapsedMilliseconds,
                    ["cases"] = cases,
                    ["allPassed"] = allPassed
                };

                string json = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                string evidencePath = Path.Combine(outDir, "window-shape-evidence.json");
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
