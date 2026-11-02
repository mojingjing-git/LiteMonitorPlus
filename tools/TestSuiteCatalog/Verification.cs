using System.Globalization;
using System.Xml.Linq;

namespace LiteMonitor.TestSuites;

public sealed record TestResultRow(string Assembly, string Class, string Method, string DisplayName,
    string Outcome, double Seconds);
public sealed record TierSummary(int Tier, int Discovered, int Selected, int Excluded,
    int Passed, int Skipped, int Failed, double Seconds);
public sealed record RunSummary(string RunId, string Configuration, int Discovered, int Selected, int Excluded,
    int Passed, int Skipped, int Failed, List<TierSummary> Tiers, List<TestResultRow> Slowest);

public static class Verification
{
    public static List<TestResultRow> ReadTrx(string path, string assembly, DateTimeOffset notBefore)
    {
        XDocument document = XDocument.Load(path);
        IEnumerable<XElement> Nodes(string name) => document.Descendants().Where(e => e.Name.LocalName == name);
        XElement times = Nodes("Times").Single();
        if (!DateTimeOffset.TryParse((string?)times.Attribute("start"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || start < notBefore.AddSeconds(-2)) throw new InvalidOperationException("Stale TRX: " + path);
        var definitions = new Dictionary<string, (string Class, string Method)>();
        foreach (XElement definition in Nodes("UnitTest"))
        {
            XElement method = definition.Elements().Single(e => e.Name.LocalName == "TestMethod");
            string codeBase = ((string?)method.Attribute("codeBase") ?? (string?)definition.Attribute("storage") ?? "").Replace('\\', '/');
            if (!string.Equals(Path.GetFileNameWithoutExtension(codeBase), assembly, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Wrong assembly in TRX: " + path);
            definitions.Add((string)definition.Attribute("id")!, ((string)method.Attribute("className")!, (string)method.Attribute("name")!));
        }
        var rows = Nodes("UnitTestResult").Select(result =>
        {
            var identity = definitions[(string)result.Attribute("testId")!];
            return new TestResultRow(assembly, identity.Class, identity.Method, (string)result.Attribute("testName")!,
                (string)result.Attribute("outcome")!, TimeSpan.Parse((string?)result.Attribute("duration") ?? "00:00:00", CultureInfo.InvariantCulture).TotalSeconds);
        }).ToList();
        XElement counters = Nodes("Counters").Single();
        if ((int?)counters.Attribute("total") != rows.Count || (int?)counters.Attribute("passed") != rows.Count(r => r.Outcome == "Passed"))
            throw new InvalidOperationException("TRX counters do not match result details: " + path);
        string? overall = (string?)Nodes("ResultSummary").Single().Attribute("outcome");
        if (overall is not "Completed" and not "Passed") throw new InvalidOperationException("TRX run did not complete successfully: " + path);
        return rows;
    }

    public static RunSummary Check(RunCatalog catalog, IReadOnlyList<TestResultRow> rows)
    {
        static string Identity(string assembly, string cls, string method, string display) => string.Join('\0', assembly, cls, method, display);
        var expected = catalog.Tests.Where(t => t.Selected).GroupBy(t => Identity(t.Test.Assembly, t.Test.Class, t.Test.Method, t.Test.DisplayName))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var actual = rows.GroupBy(t => Identity(t.Assembly, t.Class, t.Method, t.DisplayName)).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        if (expected.Count == 0) throw new InvalidOperationException("Expected test set is empty");
        var differences = expected.Keys.Union(actual.Keys, StringComparer.Ordinal).Where(k => expected.GetValueOrDefault(k) != actual.GetValueOrDefault(k)).ToArray();
        if (differences.Length > 0) throw new InvalidOperationException("Discovered tests and TRX results differ (missing, duplicate or unexpected rows):\n" +
            string.Join("\n", differences.Take(20).Select(k => k.Replace('\0', '|') + $": expected={expected.GetValueOrDefault(k)}, actual={actual.GetValueOrDefault(k)}")));
        if (rows.Any(r => r.Outcome != "Passed")) throw new InvalidOperationException("Selected tests failed or were skipped:\n" +
            string.Join("\n", rows.Where(r => r.Outcome != "Passed").Select(r => r.DisplayName + ": " + r.Outcome)));
        var tiers = Enumerable.Range(1, 5).Select(tier =>
        {
            var tests = catalog.Tests.Where(t => t.Tier == tier).ToList();
            var methods = tests.Where(t => t.Selected).Select(t => t.Test.MethodKey).ToHashSet(StringComparer.Ordinal);
            var matched = rows.Where(r => methods.Contains(r.Assembly + "|" + r.Class + "." + r.Method)).ToList();
            return new TierSummary(tier, tests.Count, tests.Count(t => t.Selected), tests.Count(t => !t.Selected),
                matched.Count(r => r.Outcome == "Passed"), matched.Count(r => r.Outcome == "NotExecuted"),
                matched.Count(r => r.Outcome is not "Passed" and not "NotExecuted"), matched.Sum(r => r.Seconds));
        }).ToList();
        return new RunSummary(catalog.RunId, catalog.Configuration, catalog.Tests.Count, rows.Count,
            catalog.Tests.Count(t => !t.Selected), rows.Count, 0, 0, tiers, rows.OrderByDescending(r => r.Seconds).Take(20).ToList());
    }

    public static RunSummary CheckDirectory(string catalogPath, string resultRoot)
    {
        RunCatalog catalog = Catalog.Read<RunCatalog>(catalogPath);
        foreach (var (path, hash) in new[] { (catalog.BuildStampPath, catalog.BuildStampSha256),
            (catalog.ManifestPath, catalog.ManifestSha256), (catalog.BaselinePath, catalog.BaselineSha256) })
            if (!File.Exists(path) || Discovery.Hash(path) != hash)
                throw new InvalidOperationException("Run input changed or is missing: " + path);
        BuildStamp stamp = Catalog.Read<BuildStamp>(catalog.BuildStampPath);
        BuildIdentity.Check(stamp, stamp.Root, catalog.Configuration, catalog.BaseOutputPath);
        foreach (AssemblyArtifact artifact in catalog.Artifacts)
            if (!File.Exists(artifact.Path) || Discovery.Hash(artifact.Path) != artifact.Sha256)
                throw new InvalidOperationException("Test binary changed after discovery: " + artifact.Path);
        var rows = new List<TestResultRow>();
        string[] assemblies = catalog.Tests.Where(t => t.Selected).Select(t => t.Test.Assembly).Distinct().Order().ToArray();
        var expectedPaths = assemblies.Select(a => Path.GetFullPath(Path.Combine(resultRoot, a, "results.trx"))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var foundPaths = Directory.EnumerateFiles(resultRoot, "*.trx", SearchOption.AllDirectories).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedPaths.SetEquals(foundPaths)) throw new InvalidOperationException("Current run is missing TRX files or contains unexpected TRX files");
        foreach (string assembly in assemblies) rows.AddRange(ReadTrx(Path.Combine(resultRoot, assembly, "results.trx"), assembly, catalog.CreatedUtc));
        return Check(catalog, rows);
    }

    public static void WriteReport(string path, RunSummary summary)
    {
        var lines = new List<string> { "# Test tier results", "", $"Run: {summary.RunId}; configuration: {summary.Configuration}", "",
            $"Discovered {summary.Discovered}; selected {summary.Selected}; excluded {summary.Excluded}; passed {summary.Passed}; skipped {summary.Skipped}; failed {summary.Failed}.", "",
            "| Tier | Discovered | Selected | Excluded | Passed | Seconds |", "|---|---:|---:|---:|---:|---:|" };
        lines.AddRange(summary.Tiers.Select(t => $"| {t.Tier} | {t.Discovered} | {t.Selected} | {t.Excluded} | {t.Passed} | {t.Seconds:F3} |"));
        lines.AddRange(["", "## Slowest 20", "", "| Test | Seconds |", "|---|---:|"]);
        lines.AddRange(summary.Slowest.Select(t => $"| {t.Assembly}: {t.DisplayName.Replace("|", "\\|").Replace("\n", " ")} | {t.Seconds:F3} |"));
        File.WriteAllLines(path, lines);
    }
}
