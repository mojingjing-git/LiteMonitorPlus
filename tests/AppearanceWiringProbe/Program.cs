using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace LiteMonitor.AppearanceWiringProbe
{
    /// <summary>
    /// A13 主面板接线自证探针入口。
    ///
    /// 用法：
    ///   AppearanceWiringProbe [--out &lt;证据目录&gt;]
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
                    Console.WriteLine("用法: AppearanceWiringProbe [--out <证据目录>]");
                    return 0;
                }
            }

            try
            {
                Directory.CreateDirectory(outDir);

                // 隔离：不读也不写用户常用配置目录
                WiringFixture.InstallSettingsSingleton();

                WiringProbeRunner runner = new WiringProbeRunner { PngDir = Path.Combine(outDir, "png") };
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
                }

                JsonObject root = new()
                {
                    ["kind"] = "appearance_wiring_probe_result",
                    ["probeVersion"] = 1,
                    ["startedAt"] = DateTime.Now.ToString("o"),
                    ["assembly"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                    ["baseDirectory"] = AppContext.BaseDirectory,
                    ["elapsedMs"] = sw.ElapsedMilliseconds,
                    ["purpose"] = "验证 A13 接线：旧主题走新入口时逐像素等于旧入口；机能主题的新装饰真的生效；" +
                                 "背景层接管后旧底板让位；窗口轮廓的切角 / 圆角切换正确。",
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
                    ["cases"] = cases,
                    ["caseCount"] = runner.Cases.Count,
                    ["allPassed"] = allPassed
                };

                string json = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                string evidencePath = Path.Combine(outDir, "wiring-evidence.json");
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
