using LiteMonitor.TestSuites;
using System.Xml.Linq;
using Xunit;

namespace LiteMonitor.Tests;

public class TestTierCatalogTests
{
    private static DiscoveredTest Case(string method = "M", string display = "C.M", string skip = null) =>
        new("A.Tests", "C", method, display, method + ":" + display, skip);
    private static TierManifest Manifest(int tier = 2) => new()
    {
        Assemblies = ["A.Tests"], Classes = [new() { Assembly = "A.Tests", Class = "C", Tier = tier }]
    };
    private static RunCatalog Run(IEnumerable<CatalogTest> tests) => new() { Tests = tests.ToList() };
    private static TestResultRow Result(string display = "C.M", string outcome = "Passed") => new("A.Tests", "C", "M", display, outcome, 0.1);

    [Fact]
    public void NewClassCannotSilentlyEnterAFallbackTier()
    {
        Assert.Throws<InvalidOperationException>(() => Catalog.Classify(Manifest(), [Case() with { Class = "Unclassified" }]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void InvalidTierFails(int tier) => Assert.Throws<InvalidOperationException>(() => Catalog.Classify(Manifest(tier), [Case()]));

    [Fact]
    public void StaleMethodOverrideFails()
    {
        var manifest = Manifest();
        manifest.Classes[0].Methods.Add(new() { Method = "Deleted", Tier = 5 });
        Assert.Throws<InvalidOperationException>(() => Catalog.Classify(manifest, [Case()]));
    }

    [Fact]
    public void MethodCapabilitiesReplaceRatherThanAccumulate()
    {
        var manifest = Manifest(4);
        manifest.Classes[0].Capabilities = ["Desktop", "Gpu"];
        manifest.Classes[0].Methods.Add(new() { Method = "M", Tier = 2, Capabilities = [] });
        var tests = Catalog.Classify(manifest, [Case()]);
        Assert.True(Assert.Single(Catalog.Select(tests, 2, false, [], [])).Selected);
    }

    [Fact]
    public void BaselineRejectsMissingMethodsAndParameterRows()
    {
        var tests = Catalog.Classify(Manifest(), [Case(display: "C.M(x: 1)"), Case(display: "C.M(x: 2)")]);
        var baseline = Catalog.Baseline(tests);
        Assert.Throws<InvalidOperationException>(() => Catalog.CheckBaseline(tests.Take(1).ToList(), baseline));
        Assert.Throws<InvalidOperationException>(() => Catalog.CheckBaseline([], baseline));
        Assert.Throws<InvalidOperationException>(() => Catalog.CheckBaseline(tests, new Dictionary<string, int>()));
    }

    [Fact]
    public void ExactTiersPartitionAndCumulativeSelectionIncludesLowerTiers()
    {
        var tests = Enumerable.Range(1, 5).Select(t => new CatalogTest(Case(t.ToString()), t, [], false, false, null)).ToList();
        var union = new HashSet<string>();
        for (int tier = 1; tier <= 5; tier++)
        {
            var exact = Catalog.Select(tests, tier, true, [], []).Where(t => t.Selected).ToList();
            Assert.True(union.Add(Assert.Single(exact).Test.MethodKey));
            Assert.Equal(tier, Catalog.Select(tests, tier, false, [], []).Count(t => t.Selected));
        }
        Assert.Equal(tests.Count, union.Count);
    }

    [Fact]
    public void HarnessAndDisabledCapabilitiesAreExplicitExclusions()
    {
        var tests = new List<CatalogTest>
        {
            new(Case(), 2, [], false, false, null),
            new(Case("Gpu"), 4, ["Gpu"], false, false, null),
            new(Case("Child"), 5, [], true, false, null)
        };
        var selection = Catalog.Select(tests, 5, false, [], []);
        Assert.Single(selection, t => t.Selected);
        Assert.Equal("harness-only", selection[2].Exclusion);
        Assert.Equal("capability-not-enabled:Gpu", selection[1].Exclusion);
        Assert.Throws<InvalidOperationException>(() => Catalog.Select(tests, 5, false, [], ["Gpu"]));
        Assert.Throws<InvalidOperationException>(() => Catalog.Select(tests, 5, false, ["Typo"], []));
        Assert.Throws<InvalidOperationException>(() => Catalog.Select(tests, 2, false, ["Gpu"], ["Gpu"]));
    }

    [Fact]
    public void ZeroSelectionAndSelectedSkipCannotPass()
    {
        Assert.Throws<InvalidOperationException>(() => Catalog.Select(Catalog.Classify(Manifest(5), [Case()]), 1, false, [], []));
        Assert.Throws<InvalidOperationException>(() => Catalog.Select(Catalog.Classify(Manifest(), [Case(skip: "gate")]), 2, false, [], []));
    }

    [Fact]
    public void TrxMustContainEveryParameterDisplayNameExactlyOnce()
    {
        var tests = Catalog.Select(Catalog.Classify(Manifest(), [Case(display: "custom α"), Case(display: "C.M(x: 2)")]), 2, false, [], []);
        var run = Run(tests);
        var results = new[] { Result("custom α"), Result("C.M(x: 2)") };
        Assert.Equal(2, Verification.Check(run, results).Passed);
        Assert.Throws<InvalidOperationException>(() => Verification.Check(run, results.Take(1).ToList()));
        Assert.Throws<InvalidOperationException>(() => Verification.Check(run, [results[0], results[0]]));
        Assert.Throws<InvalidOperationException>(() => Verification.Check(run, [results[0], Result("C.M(x: 2)", "NotExecuted")]));
        Assert.Throws<InvalidOperationException>(() => Verification.Check(run, [results[0], Result("C.M(x: 2)", "Failed")]));
    }

    [Fact]
    public void TrxFromWrongAssemblyOrOlderRunFails()
    {
        string path = Path.Combine(Path.GetTempPath(), "tier-trx-" + Guid.NewGuid().ToString("N") + ".trx");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var doc = new XDocument(new XElement("TestRun",
                new XElement("Times", new XAttribute("start", now.ToString("O"))),
                new XElement("TestDefinitions", new XElement("UnitTest", new XAttribute("id", "1"),
                    new XElement("TestMethod", new XAttribute("codeBase", "A.Tests.dll"), new XAttribute("className", "C"), new XAttribute("name", "M")))),
                new XElement("Results", new XElement("UnitTestResult", new XAttribute("testId", "1"), new XAttribute("testName", "custom α"),
                    new XAttribute("duration", "00:00:00.100"), new XAttribute("outcome", "Passed"))),
                new XElement("ResultSummary", new XAttribute("outcome", "Completed"),
                    new XElement("Counters", new XAttribute("total", 1), new XAttribute("passed", 1)))));
            doc.Save(path);
            Assert.Equal("custom α", Assert.Single(Verification.ReadTrx(path, "A.Tests", now)).DisplayName);
            Assert.Throws<InvalidOperationException>(() => Verification.ReadTrx(path, "B.Tests", now));
            Assert.Throws<InvalidOperationException>(() => Verification.ReadTrx(path, "A.Tests", now.AddMinutes(1)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RunsettingsUsesExactMethodsWithoutParameterText()
    {
        string path = Path.Combine(Path.GetTempPath(), "tier-filter-" + Guid.NewGuid().ToString("N") + ".runsettings");
        try
        {
            var tests = Catalog.Select(Catalog.Classify(Manifest(), [Case(display: "name with | & parameters")]), 2, false, [], []);
            Catalog.WriteSettings(path, tests);
            Assert.Equal("FullyQualifiedName=C.M", XDocument.Load(path).Descendants("TestCaseFilter").Single().Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NoBuildRejectsAnotherConfigurationBeforeInspectingArtifacts()
    {
        string root = Path.GetTempPath();
        var stamp = new BuildStamp(root, "Debug", "bin/test-tiers/", "", new Dictionary<string, string>());
        Assert.Throws<InvalidOperationException>(() => BuildIdentity.Check(stamp, root, "Release", "bin/test-tiers/"));
        Assert.Throws<InvalidOperationException>(() => BuildIdentity.Check(stamp, root, "Debug", "bin/other/"));
    }

    [Fact]
    public void SourceFingerprintCoversEveryCopiedAndNonTextBuildInput()
    {
        string root = Path.Combine(Path.GetTempPath(), "tier-inputs-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] inputs =
            [
                "LiteMonitor.Updater/Program.cs",
                "resources/LiteMonitor++.Updater.exe",
                "使用说明(必读).txt",
                "resources/assets/fixture.png",
                "tests/LiteMonitor.Tests/fixtures/image.jpg",
            ];
            foreach (string relative in inputs)
            {
                string path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [1, 2, 3, 4]);
            }

            string fingerprint = BuildIdentity.SourceFingerprint(root);
            foreach (string relative in inputs)
            {
                string path = Path.Combine(root, relative);
                File.AppendAllText(path, "changed");
                string changed = BuildIdentity.SourceFingerprint(root);
                Assert.NotEqual(fingerprint, changed);
                fingerprint = changed;
            }

            string generated = Path.Combine(root, "bin", "generated.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(generated)!);
            File.WriteAllText(generated, "ignored output");
            Assert.Equal(fingerprint, BuildIdentity.SourceFingerprint(root));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
