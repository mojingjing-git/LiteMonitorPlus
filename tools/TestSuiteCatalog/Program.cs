using System.Text.Json;

namespace LiteMonitor.TestSuites;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true
    };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "discover-raw")
            {
                File.WriteAllText(args[2], JsonSerializer.Serialize(Discovery.Read(args[1]), Json));
                return 0;
            }
            if (args.Length == 4 && args[0] is "stamp" or "check-stamp")
            {
                string stampPath = Path.Combine(args[1], args[3], args[2], "test-build.json");
                if (args[0] == "stamp") Catalog.Write(stampPath, BuildIdentity.Create(args[1], args[2], args[3]));
                else BuildIdentity.Check(Catalog.Read<BuildStamp>(stampPath), args[1], args[2], args[3]);
                return 0;
            }
            if (args.Length == 4 && args[0] == "verify")
            {
                RunSummary summary;
                try { summary = Verification.CheckDirectory(args[1], args[2]); }
                catch (Exception validationError)
                {
                    RunCatalog catalog = Catalog.Read<RunCatalog>(args[1]);
                    var outcomes = new List<string?>();
                    if (Directory.Exists(args[2]))
                        foreach (string path in Directory.EnumerateFiles(args[2], "*.trx", SearchOption.AllDirectories))
                            try
                            {
                                outcomes.AddRange(System.Xml.Linq.XDocument.Load(path).Descendants()
                                    .Where(e => e.Name.LocalName == "UnitTestResult").Select(e => (string?)e.Attribute("outcome")));
                            }
                            catch (System.Xml.XmlException) { /* Still write the original validation failure. */ }
                    var failed = new { catalog.RunId, catalog.Configuration, completed = false, discovered = catalog.Tests.Count,
                        selected = catalog.Tests.Count(t => t.Selected), excluded = catalog.Tests.Count(t => !t.Selected),
                        passed = outcomes.Count(o => o == "Passed"), skipped = outcomes.Count(o => o == "NotExecuted"),
                        failed = outcomes.Count(o => o is not "Passed" and not "NotExecuted"), error = validationError.Message };
                    Catalog.Write(Path.Combine(args[3], "summary.json"), failed);
                    File.WriteAllText(Path.Combine(args[3], "summary.md"), "# Test run failed validation\n\n" + validationError.Message);
                    throw;
                }
                Catalog.Write(Path.Combine(args[3], "summary.json"), summary);
                Verification.WriteReport(Path.Combine(args[3], "summary.md"), summary);
                Console.WriteLine($"Passed {summary.Passed}; skipped {summary.Skipped}; failed {summary.Failed}; excluded {summary.Excluded}");
                return 0;
            }
            if (args.Length >= 8 && args[0] is "prepare" or "update-baseline")
            {
                // root, configuration, relative BaseOutputPath, manifest, baseline, output, tier, exact, capabilities, required
                string root = Path.GetFullPath(args[1]), configuration = args[2], baseOutput = args[3];
                if (configuration is not "Debug" and not "Release") throw new ArgumentException("Invalid build configuration");
                TierManifest manifest = Catalog.Read<TierManifest>(args[4]);
                var tests = new List<DiscoveredTest>();
                var artifacts = new List<AssemblyArtifact>();
                foreach (string assembly in manifest.Assemblies)
                {
                    string path = Path.GetFullPath(Path.Combine(root, "tests", assembly, baseOutput, configuration, "net8.0-windows", assembly + ".dll"));
                    tests.AddRange(Discovery.Read(path));
                    artifacts.Add(new AssemblyArtifact(assembly, path, Discovery.Hash(path)));
                }
                List<CatalogTest> classified = Catalog.Classify(manifest, tests);
                if (args[0] == "update-baseline")
                {
                    Catalog.Write(args[5], Catalog.Baseline(classified));
                    Console.WriteLine($"Explicitly recorded {tests.Count} cases / {Catalog.Baseline(classified).Count} methods");
                    return 0;
                }
                Catalog.CheckBaseline(classified, Catalog.Read<Dictionary<string, int>>(args[5]));
                int tier = int.Parse(args[7], System.Globalization.CultureInfo.InvariantCulture);
                bool exact = args.Length > 8 && bool.Parse(args[8]);
                List<string> caps = args.Length > 9 ? args[9].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() : [];
                List<string> required = args.Length > 10 ? args[10].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() : [];
                string stampPath = Path.Combine(root, baseOutput, configuration, "test-build.json");
                var catalog = new RunCatalog { Configuration = configuration, BaseOutputPath = baseOutput,
                    BuildStampPath = stampPath, BuildStampSha256 = Discovery.Hash(stampPath),
                    ManifestPath = Path.GetFullPath(args[4]), ManifestSha256 = Discovery.Hash(args[4]),
                    BaselinePath = Path.GetFullPath(args[5]), BaselineSha256 = Discovery.Hash(args[5]),
                    Tier = tier, ExactTier = exact, Capabilities = caps,
                    RequiredCapabilities = required, Artifacts = artifacts, Tests = Catalog.Select(classified, tier, exact, caps, required) };
                Directory.CreateDirectory(args[6]);
                Catalog.Write(Path.Combine(args[6], "catalog.json"), catalog);
                foreach (string assembly in manifest.Assemblies)
                {
                    var selected = catalog.Tests.Where(t => t.Selected && t.Test.Assembly == assembly).ToList();
                    if (selected.Count > 0) Catalog.WriteSettings(Path.Combine(args[6], assembly + ".runsettings"), selected);
                }
                Console.WriteLine($"Discovered {tests.Count}; selected {catalog.Tests.Count(t => t.Selected)}; excluded {catalog.Tests.Count(t => !t.Selected)}");
                foreach (var group in catalog.Tests.GroupBy(t => t.Tier).OrderBy(g => g.Key))
                    Console.WriteLine($"Tier {group.Key}: {group.Count(t => t.Selected)} selected / {group.Count()} discovered");
                return 0;
            }
            throw new ArgumentException("Usage: TestSuiteCatalog discover-raw | prepare | update-baseline | verify (see scripts/run-tests.ps1)");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
