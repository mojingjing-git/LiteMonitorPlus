using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class ReviewReleaseValidationGateTests
{
    private const string E2E = "LiteMonitor.Tests.MarketE2ETests.端到端_拉索引_安装_校验_卸载";
    private static readonly string[] Assemblies =
        ["LiteMonitor.Tests", "LiteMonitor.Updater.Tests", "LiteMonitor.GpuNonperformance.Tests"];

    [Theory]
    [InlineData("none", true)]
    [InlineData("e2e", true)]
    [InlineData("other", false)]
    [InlineData("excess", false)]
    [InlineData("market-skip", false)]
    [InlineData("market-missing", false)]
    [InlineData("no-skip-detail", false)]
    [InlineData("wrong-assembly", false)]
    public void 计数门禁验证跳过身份及市场契约(string scenario, bool succeeds)
    {
        using var scope = new TemporaryResults();
        WriteResults(scope.Path, scenario);
        var result = RunGate(scope.Path);
        Assert.True((result.Code == 0) == succeeds, result.Output);
    }

    [Fact]
    public void Release失败不能被另一个目录的Debug通过覆盖()
    {
        using var scope = new TemporaryResults();
        string debug = System.IO.Path.Combine(scope.Path, "Debug");
        string release = System.IO.Path.Combine(scope.Path, "Release");
        WriteResults(debug, "e2e");
        WriteResults(release, "failed");
        // 故意让 Debug 更新，重现把配置混在一个根下时“最新结果”掩盖失败的风险。
        foreach (string file in Directory.GetFiles(debug, "*.trx"))
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(0, RunGate(debug).Code);
        var failed = RunGate(release);
        Assert.NotEqual(0, failed.Code);
        Assert.Contains("失败用例", failed.Output);
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("release.yml")]
    public void 两份流水线分别执行和校验双配置且Release失败会中止(string workflow)
    {
        string text = File.ReadAllText(System.IO.Path.Combine(Root(), ".github", "workflows", workflow));
        string debug = StepScript(text, "运行单元测试");
        string release = StepScript(text, "运行单元测试（Release）");
        foreach (var (script, config) in new[] { (debug, "Debug"), (release, "Release") })
        {
            Assert.Contains("-c " + config, script);
            Assert.Contains("ci-artifacts/test-results/" + config + "/$assembly", script);
            foreach (string assembly in Assemblies) Assert.Contains("'" + assembly + "'", script);
            Assert.Contains("$code = $LASTEXITCODE", script);
            Assert.Contains("if ($code -ne 0) { throw", script);
        }
        foreach (var (name, config) in new[]
        {
            ("强制校验测试发现数（防假绿）", "Debug"),
            ("强制校验测试发现数（Release）", "Release")
        })
        {
            string gate = StepScript(text, name);
            Assert.Contains("'-TrxRoot', 'ci-artifacts/test-results/" + config + "'", gate);
            Assert.Contains("'-MinTotal', $env:MIN_TEST_TOTAL", gate);
            Assert.Contains("'-ExpectedAssembly', $env:EXPECTED_TEST_ASSEMBLIES", gate);
            Assert.Contains("'-MaxSkipped', $env:MAX_SKIPPED_TESTS", gate);
            Assert.Contains("'-MustExecuteTestClass', $env:MUST_EXECUTE_TEST_CLASS", gate);
            Assert.Contains("'-MustExecuteTestClassMinCount', $env:MUST_EXECUTE_TEST_CLASS_MIN_COUNT", gate);
            Assert.Contains("& pwsh @assertArgs", gate);
            Assert.Contains("if ($LASTEXITCODE -ne 0)", gate);
        }

        // 运行实际工作流的 Release 脚本，用故意失败的命令替代 dotnet，不递归启动构建。
        using var scope = new TemporaryResults();
        Directory.CreateDirectory(System.IO.Path.Combine(scope.Path, "ci-artifacts"));
        string scriptPath = System.IO.Path.Combine(scope.Path, "workflow.ps1");
        File.WriteAllText(scriptPath,
            "function dotnet { $global:LASTEXITCODE = 7; Write-Output 'synthetic test failure' }\n" + release);
        var result = RunPowerShell(scriptPath, [], scope.Path);
        Assert.NotEqual(0, result.Code);
        Assert.Contains("退出码 7", result.Output);
    }

    private static string StepScript(string workflow, string step)
    {
        string[] lines = workflow.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, line => line.Trim() == "- name: " + step);
        Assert.True(start >= 0, "缺少工作流步骤：" + step);
        var result = new List<string>();
        bool inScript = false;
        for (int i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("      - name:")) break;
            if (!inScript) { inScript = lines[i].Trim() == "run: |"; continue; }
            if (lines[i].Length == 0) result.Add("");
            else if (lines[i].StartsWith("          ")) result.Add(lines[i][10..]);
            else break;
        }
        Assert.NotEmpty(result);
        return string.Join("\n", result);
    }

    private static void WriteResults(string root, string scenario)
    {
        Directory.CreateDirectory(root);
        foreach (string assembly in Assemblies)
        {
            var cases = new List<(string Name, string Outcome)> { (assembly + ".Smoke.Run", "Passed") };
            if (assembly == Assemblies[0])
            {
                if (scenario != "market-missing")
                    for (int i = 0; i < 6; i++)
                        cases.Add(($"LiteMonitor.Tests.MarketIndexContractTests.Contract{i}",
                            scenario == "market-skip" && i == 0 ? "NotExecuted" : "Passed"));
                if (scenario is "e2e" or "excess") cases.Add((E2E, "NotExecuted"));
                if (scenario is "other" or "excess")
                    cases.Add(("LiteMonitor.Tests.HttpLifecycleTests.关键资源释放", "NotExecuted"));
                if (scenario == "failed") cases.Add(("LiteMonitor.Tests.HttpLifecycleTests.Release", "Failed"));
            }
            if (scenario == "wrong-assembly" && assembly == Assemblies[1]) cases.Add((E2E, "NotExecuted"));
            int missingSkip = scenario == "no-skip-detail" && assembly == Assemblies[0] ? 1 : 0;
            var doc = new XDocument(new XElement("TestRun",
                new XElement("TestDefinitions", new XElement("UnitTest",
                    new XElement("TestMethod", new XAttribute("codeBase", assembly + ".dll")))),
                new XElement("Results", cases.Select(c => new XElement("UnitTestResult",
                    new XAttribute("testName", c.Name), new XAttribute("outcome", c.Outcome)))),
                new XElement("ResultSummary", new XElement("Counters",
                    new XAttribute("total", cases.Count + missingSkip),
                    new XAttribute("executed", cases.Count(c => c.Outcome != "NotExecuted")),
                    new XAttribute("passed", cases.Count(c => c.Outcome == "Passed")),
                    new XAttribute("failed", cases.Count(c => c.Outcome == "Failed"))))));
            doc.Save(System.IO.Path.Combine(root, assembly + ".trx"));
        }
    }

    private static (int Code, string Output) RunGate(string results) => RunPowerShell(
        System.IO.Path.Combine(Root(), ".github", "workflows", "assert-test-counts.ps1"),
        ["-TrxRoot", results, "-MinTotal", "3", "-MaxSkipped", "1",
         "-MustExecuteTestClass", "MarketIndexContractTests", "-MustExecuteTestClassMinCount", "6"]);

    private static (int Code, string Output) RunPowerShell(string script, string[] args, string workspace = null)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        if (workspace != null) start.Environment["GITHUB_WORKSPACE"] = workspace;
        using var process = Process.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("门禁反例进程超时");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "LiteMonitor.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("找不到仓库根目录");
    }

    private sealed class TemporaryResults : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "LiteMonitor-review-gate-" + Guid.NewGuid().ToString("N"));
        public TemporaryResults() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
