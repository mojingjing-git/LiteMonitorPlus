using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace LiteMonitor.AppearanceM2Probe
{
    /// <summary>
    /// M2 合同 §9「验收门槛」自证探针入口。
    ///
    /// 覆盖两组门槛：
    ///   · 源码级（第 7 条 绘制路径无捕获调用 / 第 10 条 无新增 NuGet 依赖）
    ///   · 运行时（第 1/2/3/4/5/6/8/9 条：IsPreview、预览零调用、Source 三值、
    ///     代次回收、pending 上限、显示亲和性恢复、两种计量口径、缓冲预算）
    ///
    /// 用法：
    ///   AppearanceM2Probe [--repo-root &lt;仓库根&gt;] [--out &lt;证据目录&gt;]
    ///
    /// 退出码：0 = 全部用例通过；1 = 有用例失败；2 = 探针自身异常。
    /// </summary>
    internal static class Program
    {
        /// <summary>探针入口。</summary>
        private static int Main(string[] args)
        {
            // ★ 控制台编码只是"看起来好看"：stdout 被重定向（没有控制台）时，
            //   设置 Console.OutputEncoding 会直接抛异常并让整个探针崩掉
            //   （证据 JSON 本身是 UTF-8 无 BOM 写盘，不依赖控制台编码）—— 所以这里必须吞掉。
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception)
            {
                // 无控制台（输出被重定向）时忽略
            }

            string outDir = Path.Combine(AppContext.BaseDirectory, "evidence");
            string? repoRootOverride = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--repo-root")
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("参数 --repo-root 缺少路径。");
                        PrintUsage();
                        return 2;
                    }
                    repoRootOverride = args[i + 1];
                    i++;
                }
                else if (args[i] == "--out")
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("参数 --out 缺少路径。");
                        PrintUsage();
                        return 2;
                    }
                    outDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--help" || args[i] == "-h")
                {
                    PrintUsage();
                    return 0;
                }
                else
                {
                    Console.Error.WriteLine("忽略未知参数: " + args[i]);
                }
            }

            try
            {
                Directory.CreateDirectory(outDir);

                RepoRootResolution resolution = RepoRootResolver.Resolve(repoRootOverride);

                List<ProbeCase> sourceCases = new();
                List<ProbeCase> runtimeCases = new();
                Stopwatch sw = Stopwatch.StartNew();
                if (resolution.Root == null)
                {
                    // 定位不到仓库根就必须显式失败，不允许静默通过
                    foreach (string name in new[]
                             {
                                 "M2 门槛#7 绘制路径无捕获调用",
                                 "M2 门槛#10 无新增 NuGet 依赖"
                             })
                    {
                        ProbeCase failed = new ProbeCase { Name = name };
                        failed.Fail("未定位到仓库根目录：" + resolution.Error);
                        failed.Detail("gate", name.Contains("#7") ? "M2 合同 §9 第 7 条" : "M2 合同 §9 第 10 条");
                        failed.Detail("repoRootResolved", false);
                        sourceCases.Add(failed);
                    }
                }
                else
                {
                    SourceGateRunner runner = new SourceGateRunner(resolution.Root);
                    runner.RunAll();
                    sourceCases.AddRange(runner.Cases);
                }

                // ★ 运行时门槛不依赖仓库根：它只看主工程的实现行为（假后端驱动，全程不抓屏）
                RuntimeGateRunner runtimeRunner = new RuntimeGateRunner();
                runtimeRunner.RunAll();
                runtimeCases.AddRange(runtimeRunner.Cases);

                sw.Stop();

                List<ProbeCase> allCases = new();
                allCases.AddRange(sourceCases);
                allCases.AddRange(runtimeCases);

                bool allPassed = true;
                foreach (ProbeCase c in allCases) allPassed &= c.Passed;

                JsonObject sourceRoot = BuildEvidence(
                    kind: "appearance_m2_source_gate_probe_result",
                    purpose: "验证 M2 合同 §9 中两条与捕获后端实现无关的源码级门槛：" +
                             "第 7 条（绘制路径无捕获调用）与第 10 条（无新增 NuGet 依赖）。",
                    isolationNote: "本组只读取源码文本与 csproj 文本；不创建 HardwareMonitor、不 Open 硬件、" +
                                   "不做任何屏幕捕获、不读也不写用户配置目录、不引入任何 NuGet 依赖。",
                    resolution: resolution,
                    cases: sourceCases,
                    elapsedMs: sw.ElapsedMilliseconds,
                    assembly: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                    baseDirectory: AppContext.BaseDirectory,
                    startedAt: DateTime.Now.ToString("o"));

                JsonObject runtimeRoot = BuildEvidence(
                    kind: "appearance_m2_runtime_gate_probe_result",
                    purpose: "验证 M2 合同 §9 中依赖运行时行为的八条门槛：" +
                             "第 1/2/3/4/5/6/8/9 条（IsPreview、预览路径零后端调用、Source 三值语义、" +
                             "代次不匹配帧的丢弃与回收、pending 上限、取消后恢复显示亲和性、" +
                             "CPU 与经过时间分别计量、缓冲预算有界）。" +
                             "全部由探针自有的假后端驱动，绝不进行真实屏幕捕获。",
                    isolationNote: "本组的\"捕获\"全部由探针自有的假后端现画小位图产生：" +
                                   "不调用 CopyFromScreen / BitBlt / 任何抓屏 API；" +
                                   "不创建 HardwareMonitor、不 Open 硬件；" +
                                   "不读也不写用户配置目录（只用内存里的 Theme 对象）；不引入任何 NuGet 依赖。",
                    resolution: resolution,
                    cases: runtimeCases,
                    elapsedMs: sw.ElapsedMilliseconds,
                    assembly: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                    baseDirectory: AppContext.BaseDirectory,
                    startedAt: DateTime.Now.ToString("o"));

                string sourceJson = sourceRoot.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                string sourceEvidencePath = Path.Combine(outDir, "m2-source-gate-evidence.json");
                File.WriteAllText(sourceEvidencePath, sourceJson, new UTF8Encoding(false));

                string runtimeJson = runtimeRoot.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                string runtimeEvidencePath = Path.Combine(outDir, "m2-runtime-gate-evidence.json");
                File.WriteAllText(runtimeEvidencePath, runtimeJson, new UTF8Encoding(false));

                Console.WriteLine();
                Console.WriteLine("仓库根: " + (resolution.Root ?? "(未定位) " + resolution.Error));
                foreach (ProbeCase c in allCases)
                {
                    Console.WriteLine();
                    Console.WriteLine((c.Passed ? "[通过] " : "[失败] ") + c.Name);
                    foreach (string f in c.Failures) Console.WriteLine("    - " + f);
                }

                Console.WriteLine();
                Console.WriteLine("证据文件: " + sourceEvidencePath);
                Console.WriteLine("证据文件: " + runtimeEvidencePath);
                Console.WriteLine(allPassed ? "结果: 全部通过" : "结果: 存在失败用例");
                return allPassed ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("探针异常: " + ex);
                return 2;
            }
        }

        /// <summary>
        /// 组装一份证据 JSON（源码级与运行时两组共用同一结构）。
        /// </summary>
        private static JsonObject BuildEvidence(
            string kind,
            string purpose,
            string isolationNote,
            RepoRootResolution resolution,
            List<ProbeCase> cases,
            long elapsedMs,
            string assembly,
            string baseDirectory,
            string startedAt)
        {
            JsonArray caseArray = new();
            bool allPassed = true;
            foreach (ProbeCase c in cases)
            {
                allPassed &= c.Passed;
                caseArray.Add(new JsonObject
                {
                    ["name"] = c.Name,
                    ["passed"] = c.Passed,
                    ["failures"] = new JsonArray(c.Failures.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                    ["details"] = c.Details
                });
            }

            return new JsonObject
            {
                ["kind"] = kind,
                ["probeVersion"] = 1,
                ["startedAt"] = startedAt,
                ["assembly"] = assembly,
                ["baseDirectory"] = baseDirectory,
                ["elapsedMs"] = elapsedMs,
                ["repoRoot"] = resolution.Root,
                ["repoRootSource"] = resolution.Source,
                ["purpose"] = purpose,
                ["isolation"] = new JsonObject
                {
                    ["screenCapturePerformed"] = false,
                    ["hardwareMonitorCreated"] = false,
                    ["hardwareOpened"] = false,
                    ["userSettingsRead"] = false,
                    ["userSettingsWritten"] = false,
                    ["nuGetPackagesAdded"] = 0,
                    ["note"] = isolationNote
                },
                ["cases"] = caseArray,
                ["caseCount"] = cases.Count,
                ["allPassed"] = allPassed
            };
        }

        /// <summary>打印用法。</summary>
        private static void PrintUsage()
        {
            Console.WriteLine("用法: AppearanceM2Probe [--repo-root <仓库根>] [--out <证据目录>]");
            Console.WriteLine("  --repo-root  指定仓库根目录（默认从程序所在目录向上逐级查找）");
            Console.WriteLine("  --out        指定证据输出目录（默认 output\\evidence）");
            Console.WriteLine("退出码: 0 = 全部通过; 1 = 有用例失败; 2 = 探针自身异常");
        }
    }

    /// <summary>仓库根定位结果。</summary>
    internal sealed class RepoRootResolution
    {
        /// <summary>定位到的仓库根；为 null 表示定位失败。</summary>
        public string? Root { get; init; }

        /// <summary>定位方式说明（"--repo-root" / "向上查找"）。</summary>
        public string Source { get; init; } = "";

        /// <summary>定位失败的原因。</summary>
        public string Error { get; init; } = "";
    }

    /// <summary>
    /// 仓库根定位器。
    ///
    /// 判定标准：目录里**同时**存在 <c>src/</c> 目录与 <c>LiteMonitor.csproj</c> 文件。
    /// 找不到就返回失败原因，由调用方把两个用例都判失败 —— 不允许静默通过。
    /// </summary>
    internal static class RepoRootResolver
    {
        /// <summary>定位仓库根。</summary>
        public static RepoRootResolution Resolve(string? overridePath)
        {
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                string full = Path.GetFullPath(overridePath);
                string? reason = Validate(full);
                if (reason == null)
                {
                    return new RepoRootResolution { Root = full, Source = "--repo-root 参数" };
                }
                return new RepoRootResolution
                {
                    Root = null,
                    Source = "--repo-root 参数",
                    Error = "指定的路径不满足仓库根判定（" + reason + "）：" + full
                };
            }

            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Validate(dir.FullName) == null)
                {
                    return new RepoRootResolution { Root = dir.FullName, Source = "从程序所在目录向上查找" };
                }
                dir = dir.Parent;
            }

            return new RepoRootResolution
            {
                Root = null,
                Source = "从程序所在目录向上查找",
                Error = "自 " + AppContext.BaseDirectory + " 向上逐级查找，未找到同时包含 src/ 目录与 LiteMonitor.csproj 的祖先目录"
            };
        }

        /// <summary>校验目录是否像仓库根；返回 null 表示合格，否则返回不合格原因。</summary>
        private static string? Validate(string directory)
        {
            if (!Directory.Exists(Path.Combine(directory, "src"))) return "缺少 src 目录";
            if (!File.Exists(Path.Combine(directory, "LiteMonitor.csproj"))) return "缺少 LiteMonitor.csproj";
            return null;
        }
    }
}
