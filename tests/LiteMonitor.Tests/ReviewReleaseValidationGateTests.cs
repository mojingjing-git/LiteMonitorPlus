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
        string debug = StepScript(text, "Run tests (Debug, Tier 3)");
        string release = StepScript(text, "Run tests (Release, Tier 3)");
        foreach (var (script, config) in new[] { (debug, "Debug"), (release, "Release") })
        {
            Assert.Contains("'scripts/run-tests.ps1'", script);
            Assert.Contains("-Configuration " + config, script);
            Assert.Contains("-NoBuild", script);
            Assert.Contains("'ci-artifacts/test-results/" + config + "'", script);
            Assert.Contains("-Tier 3", script);
            Assert.Contains("$code = $LASTEXITCODE", script);
            Assert.Contains("if ($code -ne 0) { throw", script);
        }
        foreach (var (name, config) in new[]
        {
            ("Verify exact test inventory (Debug, Tier 3)", "Debug"),
            ("Verify exact test inventory (Release, Tier 3)", "Release")
        })
        {
            string gate = StepScript(text, name);
            Assert.Contains("'.github/workflows/assert-test-tiers.ps1'", gate);
            Assert.Contains("-ResultsRoot 'ci-artifacts/test-results/" + config + "'", gate);
            Assert.Contains("-Configuration " + config, gate);
            Assert.Contains("-Tier 3", gate);
            Assert.Contains("if ($LASTEXITCODE -ne 0)", gate);
        }
        if (workflow == "release.yml")
        {
            Assert.True(text.IndexOf("- name: Verify exact test inventory (Release, Tier 3)", StringComparison.Ordinal) <
                text.IndexOf("- name: Build unsigned release ZIP and SHA-256", StringComparison.Ordinal));
        }
        string runner = File.ReadAllText(System.IO.Path.Combine(Root(), "scripts", "run-tests.ps1"));
        Assert.Contains("--no-build", runner);
        Assert.Contains("--settings $settings", runner);
        Assert.Contains("verify (Join-Path $runDir 'catalog.json')", runner);

        // 运行实际工作流的 Release 脚本，用故意失败的命令替代 dotnet，不递归启动构建。
        using var scope = new TemporaryResults();
        Directory.CreateDirectory(System.IO.Path.Combine(scope.Path, "ci-artifacts"));
        string scriptPath = System.IO.Path.Combine(scope.Path, "workflow.ps1");
        File.WriteAllText(scriptPath,
            "function pwsh { $global:LASTEXITCODE = 7; Write-Output 'synthetic test failure' }\n" + release);
        var result = RunPowerShell(scriptPath, [], scope.Path);
        Assert.NotEqual(0, result.Code);
        Assert.Contains("exit 7", result.Output);
    }

    [Fact]
    public void Release要求同SHA的精确四档五档产物后才允许签名()
    {
        string text = File.ReadAllText(System.IO.Path.Combine(Root(), ".github", "workflows", "release.yml"));
        string fast = JobBlock(text, "fast-validation", "system-validation");
        string system = JobBlock(text, "system-validation", "release");
        string release = JobBlock(text, "release", null);

        Assert.Contains("runs-on: windows-latest", fast);
        Assert.Contains("-Tier 3 -Configuration Debug", fast);
        Assert.Contains("-Tier 3 -Configuration Release", fast);
        Assert.Contains("runs-on: [self-hosted, Windows, X64, LiteMonitor-SystemValidation]", system);
        Assert.Contains("-Tier 4 -ExactTier", system);
        Assert.Contains("-Tier 5 -ExactTier", system);
        Assert.Contains("-Capabilities Desktop,Gpu,Hardware", system);
        Assert.Contains("-RequiredCapabilities Desktop,Gpu,Hardware", system);
        Assert.Contains("Build unsigned release ZIP and SHA-256", system);
        Assert.Contains("validated-release-manifest.json", system);
        Assert.DoesNotContain("secrets.", system);
        Assert.DoesNotContain("contents: write", system);

        Assert.Contains("needs: [fast-validation, system-validation]", release);
        Assert.Contains("actions/download-artifact@v4", release);
        Assert.Contains("manifest.commit -cne $env:GITHUB_SHA", release);
        Assert.Contains("Get-FileHash", release);
        Assert.Contains("scripts/sign-release.ps1", release);
        Assert.Contains("verify-release-signature.ps1", release);
        Assert.DoesNotContain("scripts/run-tests.ps1", release);
        Assert.DoesNotContain("-Tier 5", release);
    }

    [Fact]
    public void 扩展测试只使用显式能力Runner且始终精确选档()
    {
        string text = File.ReadAllText(System.IO.Path.Combine(Root(), ".github", "workflows", "extended-tests.yml"));
        Assert.Contains("runs-on: [self-hosted, Windows, X64, LiteMonitor-SystemValidation]", text);
        Assert.DoesNotContain("runs-on: windows-latest", text);
        Assert.Contains("options: ['4', '5']", text);
        Assert.Contains("default: '4'", text);
        Assert.Contains("-ExactTier", text);
        Assert.Contains("description: 'Enable network-dependent tests'", text);
        // 能力是逗号分隔字符串（不能用 PowerShell 数组直传 pwsh -File）；
        // 联网勾选时在 job 级能力串上追加 Network。
        Assert.Contains("$capabilities = \"$capabilities,Network\"", text);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("multiple")]
    [InlineData("wrong-configuration")]
    public void 分档门禁拒绝缺失重复及其他配置的目录(string scenario)
    {
        using var scope = new TemporaryResults();
        if (scenario != "empty")
        {
            foreach (string name in scenario == "multiple" ? new[] { "one", "two" } : new[] { "one" })
            {
                string run = System.IO.Path.Combine(scope.Path, name);
                Directory.CreateDirectory(run);
                File.WriteAllText(System.IO.Path.Combine(run, "catalog.json"),
                    """{"configuration":"Debug","tier":3,"exactTier":false,"baseOutputPath":"bin/"}""");
            }
        }
        var result = RunPowerShell(System.IO.Path.Combine(Root(), ".github", "workflows", "assert-test-tiers.ps1"),
            ["-ResultsRoot", scope.Path, "-Configuration", "Release", "-Tier", "3"]);
        Assert.NotEqual(0, result.Code);
        Assert.Contains(scenario == "wrong-configuration" ? "another configuration" : "exactly one current run", result.Output);
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

    private static string JobBlock(string workflow, string jobName, string? nextJobName)
    {
        string text = workflow.Replace("\r\n", "\n");
        int start = text.IndexOf("\n  " + jobName + ":\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "Missing workflow job: " + jobName);
        start++;
        int end = nextJobName == null
            ? text.Length
            : text.IndexOf("\n  " + nextJobName + ":\n", start, StringComparison.Ordinal);
        Assert.True(end > start, "Missing next workflow job: " + nextJobName);
        return text[start..end];
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
        if (workspace != null)
        {
            start.Environment["GITHUB_WORKSPACE"] = workspace;
            start.WorkingDirectory = workspace;
        }
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
