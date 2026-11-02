using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【LM-089 · 发布流水线依赖清单门禁护栏】
/// <para><b>缺陷：</b><c>.github/workflows/ci.yml</c> 里有"校验第三方组件声明与实际依赖一致"
/// （LM-024 护栏）这道门禁，而 <c>.github/workflows/release.yml</c> **完全没有接入**
/// （全文检索 <c>dependency-manifest</c> / <c>generate-dependency</c> / <c>Mode Check</c> 零命中）
/// ⇒ 依赖漏列时 **CI 会红、发布流水线却照样**构建 / 签名 / 上传草稿
/// ⇒ 发布准入与 CI 门禁不一致（正是 LM-056"CI 会红、发布流水线却会绿"的同一族问题）。</para>
/// <para><b>本护栏守三件事：</b></para>
/// <list type="number">
/// <item>发布流水线**必须**接入依赖清单门禁，且与 ci.yml 的同名步骤**逐字一致**；</item>
/// <item>该门禁**必须**排在"跑测试"与"打包发布"之前（红了要挡住发布）；</item>
/// <item>两个工作流的防假绿阈值参数**必须逐字一致**（发布不能比 CI 松）。</item>
/// </list>
/// </summary>
public sealed class BatchEReleaseWorkflowGateTests
{
    private const string DependencyGateName = "Validate dependency manifest";

    [Fact]
    public void 护栏_发布流水线必须接入与CI逐字一致的依赖清单门禁()
    {
        string[] ci = ReadWorkflow("ci.yml");
        string[] release = ReadWorkflow("release.yml");

        string[] ciGate = ExtractStep(ci, DependencyGateName);
        string[] releaseGate = ExtractStep(release, DependencyGateName);

        Assert.True(
            ciGate.Length > 0,
            "ci.yml 里找不到依赖清单门禁 —— 护栏前提被破坏，请同步更新本用例。");
        Assert.True(
            releaseGate.Length > 0,
            "release.yml 里**没有**依赖清单门禁：CI 会红、发布流水线却照样跑。");

        Assert.Equal(ciGate.Length, releaseGate.Length);
        for (int index = 0; index < ciGate.Length; index++)
        {
            Assert.Equal(
                ciGate[index],
                releaseGate[index]);
        }
    }

    [Fact]
    public void 护栏_依赖清单门禁必须在跑测试与打包之前()
    {
        string[] release = ReadWorkflow("release.yml");

        int gate = FindStepIndex(release, DependencyGateName);
        int test = FindStepIndex(release, "Run tests (Debug, Tier 3)");
        int pack = FindStepIndex(release, "Build unsigned release ZIP and SHA-256");

        Assert.True(gate >= 0, "release.yml 里找不到依赖清单门禁 —— 护栏前提被破坏。");
        Assert.True(test >= 0, "release.yml 里找不到'运行单元测试'步骤 —— 护栏前提被破坏。");
        Assert.True(pack >= 0, "release.yml 里找不到'打包发布件并计算 SHA-256'步骤 —— 护栏前提被破坏。");

        Assert.True(
            gate < test,
            "依赖清单门禁必须排在'运行单元测试'之前：门禁在测试之后 ⇒ 依赖漏列时测试已经跑完了。");
        Assert.True(
            gate < pack,
            "依赖清单门禁必须排在'打包发布件'之前：门禁在打包之后 ⇒ 发布包已经打出来了。");
    }

    [Fact]
    public void 护栏_发布流水线与CI共享五档对账门禁()
    {
        string[] ci = ReadWorkflow("ci.yml");
        string[] release = ReadWorkflow("release.yml");

        // 旧计数阈值（MIN_TEST_TOTAL 等）已随 assert-test-counts.ps1 退出工作流；
        // 现行防假绿由固定基线 + 逐条 TRX 对账保证（assert-test-tiers.ps1 内部锚定
        // tests/test-tier-baseline.json），两个工作流必须共享同一对账入口。
        Assert.Contains("assert-test-tiers.ps1", string.Join("\n", ci));
        Assert.Contains("assert-test-tiers.ps1", string.Join("\n", release));
    }

    // ---------------------------------------------------------------- 辅助

    private static string[] ReadWorkflow(string fileName) =>
        File.ReadAllLines(Path.Combine(RepositoryRoot(), ".github", "workflows", fileName));

    /// <summary>取某个 step 的完整文本块（从 <c>- name:</c> 到下一个 <c>- name:</c> 之前）。</summary>
    private static string[] ExtractStep(string[] lines, string stepName)
    {
        int start = FindStepIndex(lines, stepName);
        if (start < 0)
        {
            return [];
        }

        int end = lines.Length;
        for (int index = start + 1; index < lines.Length; index++)
        {
            if (Regex.IsMatch(lines[index], @"^\s*- name: "))
            {
                end = index;
                break;
            }
        }

        List<string> block = [];
        for (int index = start; index < end; index++)
        {
            // 去掉块尾部的空行，避免"最后一行是空行"造成的伪差异。
            if (string.IsNullOrWhiteSpace(lines[index]) && index == end - 1)
            {
                continue;
            }

            block.Add(lines[index]);
        }

        return block.ToArray();
    }

    private static int FindStepIndex(string[] lines, string stepName)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].Contains("- name: " + stepName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>仓库根目录（含 LiteMonitor.sln 的那一层）。</summary>
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LiteMonitor.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（从 " + AppContext.BaseDirectory + " 向上找不到 LiteMonitor.sln）。");
    }
}
