using System.Text.Json;
using System.Xml.Linq;

namespace LiteMonitor.TestSuites;

public sealed class TierManifest
{
    public int Schema { get; set; } = 1;
    public List<string> Assemblies { get; set; } = [];
    public List<ClassRule> Classes { get; set; } = [];
}

public sealed class ClassRule
{
    public string Assembly { get; set; } = "";
    public string Class { get; set; } = "";
    public int Tier { get; set; }
    public List<string> Capabilities { get; set; } = [];
    public List<MethodRule> Methods { get; set; } = [];
}

public sealed class MethodRule
{
    public string Method { get; set; } = "";
    public int? Tier { get; set; }
    // null inherits; [] explicitly removes the class's capabilities.
    public List<string>? Capabilities { get; set; }
    public bool Harness { get; set; }
}

public sealed record CatalogTest(DiscoveredTest Test, int Tier, List<string> Capabilities, bool Harness,
    bool Selected, string? Exclusion);
public sealed record AssemblyArtifact(string Assembly, string Path, string Sha256);
public sealed class RunCatalog
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Configuration { get; set; } = "";
    public string BaseOutputPath { get; set; } = "";
    public string BuildStampPath { get; set; } = "";
    public string BuildStampSha256 { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public string BaselinePath { get; set; } = "";
    public string BaselineSha256 { get; set; } = "";
    public int Tier { get; set; }
    public bool ExactTier { get; set; }
    public List<string> Capabilities { get; set; } = [];
    public List<string> RequiredCapabilities { get; set; } = [];
    public List<AssemblyArtifact> Artifacts { get; set; } = [];
    public List<CatalogTest> Tests { get; set; } = [];
}

public static class Catalog
{
    public static readonly string[] KnownCapabilities = ["Desktop", "Gpu", "Hardware", "Network"];

    public static List<CatalogTest> Classify(TierManifest manifest, IReadOnlyList<DiscoveredTest> tests)
    {
        if (manifest.Schema != 1) throw new InvalidOperationException("Unsupported tier manifest schema");
        if (manifest.Assemblies.Count == 0 || manifest.Assemblies.Distinct(StringComparer.Ordinal).Count() != manifest.Assemblies.Count)
            throw new InvalidOperationException("Invalid or duplicate assembly declarations");
        if (!manifest.Assemblies.Order().SequenceEqual(tests.Select(t => t.Assembly).Distinct().Order()))
            throw new InvalidOperationException("Discovered assemblies do not match the manifest");
        if (tests.GroupBy(t => (t.Assembly, t.UniqueId)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Duplicate xUnit test identity");
        var rules = new Dictionary<(string, string), ClassRule>();
        foreach (ClassRule rule in manifest.Classes)
        {
            ValidateTier(rule.Tier);
            ValidateCapabilities(rule.Capabilities);
            if (!rules.TryAdd((rule.Assembly, rule.Class), rule)) throw new InvalidOperationException("Duplicate class rule: " + rule.Class);
            var methods = tests.Where(t => t.Assembly == rule.Assembly && t.Class == rule.Class).Select(t => t.Method).ToHashSet(StringComparer.Ordinal);
            if (methods.Count == 0) throw new InvalidOperationException("Stale class rule: " + rule.Class);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (MethodRule method in rule.Methods)
            {
                if (!methods.Contains(method.Method) || !seen.Add(method.Method))
                    throw new InvalidOperationException("Stale or duplicate method rule: " + rule.Class + "." + method.Method);
                if (method.Tier.HasValue) ValidateTier(method.Tier.Value);
                if (method.Capabilities is not null) ValidateCapabilities(method.Capabilities);
            }
        }
        return tests.Select(test =>
        {
            if (!rules.TryGetValue((test.Assembly, test.Class), out ClassRule? rule))
                throw new InvalidOperationException("Unclassified class: " + test.Assembly + ":" + test.Class);
            MethodRule? method = rule.Methods.SingleOrDefault(m => m.Method == test.Method);
            return new CatalogTest(test, method?.Tier ?? rule.Tier, [.. method?.Capabilities ?? rule.Capabilities],
                method?.Harness ?? false, false, null);
        }).ToList();
    }

    public static void CheckBaseline(IReadOnlyList<CatalogTest> tests, IReadOnlyDictionary<string, int> baseline)
    {
        var current = Baseline(tests);
        var drift = baseline.Keys.Union(current.Keys, StringComparer.Ordinal)
            .Where(k => baseline.GetValueOrDefault(k) != current.GetValueOrDefault(k)).ToArray();
        if (drift.Length > 0) throw new InvalidOperationException("Test inventory changed; explicitly review and update the baseline:\n" +
            string.Join("\n", drift.Select(k => $"{k}: baseline={baseline.GetValueOrDefault(k)}, discovered={current.GetValueOrDefault(k)}")));
    }

    public static Dictionary<string, int> Baseline(IReadOnlyList<CatalogTest> tests) => tests
        .GroupBy(t => t.Test.MethodKey).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    public static List<CatalogTest> Select(IReadOnlyList<CatalogTest> tests, int tier, bool exact,
        IReadOnlyList<string> capabilities, IReadOnlyList<string> required)
    {
        ValidateTier(tier);
        ValidateCapabilities(capabilities);
        ValidateCapabilities(required);
        if (required.Except(capabilities, StringComparer.Ordinal).Any())
            throw new InvalidOperationException("Required capabilities were not enabled: " + string.Join(", ", required.Except(capabilities)));
        var result = tests.Select(t =>
        {
            string? reason = t.Harness ? "harness-only" : (exact ? t.Tier != tier : t.Tier > tier) ? "outside-tier" :
                t.Capabilities.Except(capabilities, StringComparer.Ordinal).Any() ? "capability-not-enabled:" +
                    string.Join(",", t.Capabilities.Except(capabilities, StringComparer.Ordinal)) : null;
            return t with { Selected = reason is null, Exclusion = reason };
        }).ToList();
        if (!result.Any(t => t.Selected)) throw new InvalidOperationException("Selection contains zero tests");
        foreach (string capability in required)
            if (!result.Any(t => t.Selected && t.Capabilities.Contains(capability, StringComparer.Ordinal)))
                throw new InvalidOperationException("Required capability has no selected verification: " + capability);
        var skipped = result.Where(t => t.Selected && t.Test.SkipReason is not null).ToArray();
        if (skipped.Length > 0) throw new InvalidOperationException("Selected tests would be skipped:\n" +
            string.Join("\n", skipped.Select(t => t.Test.DisplayName + ": " + t.Test.SkipReason)));
        return result;
    }

    public static void WriteSettings(string path, IEnumerable<CatalogTest> tests)
    {
        string[] methods = tests.Where(t => t.Selected).Select(t => t.Test.Class + "." + t.Test.Method)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (methods.Length == 0) throw new InvalidOperationException("Cannot write an empty test filter");
        if (methods.Any(m => m.IndexOfAny(['|', '&', '=', '!', '(', ')']) >= 0))
            throw new InvalidOperationException("Unsupported filter characters in test identity");
        new XDocument(new XElement("RunSettings", new XElement("RunConfiguration",
            new XElement("TestCaseFilter", string.Join("|", methods.Select(m => "FullyQualifiedName=" + m)))),
            new XElement("xUnit", new XElement("PreEnumerateTheories", "true")))).Save(path);
    }

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Program.Json)
        ?? throw new InvalidOperationException("Invalid JSON: " + path);
    public static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Program.Json));

    private static void ValidateTier(int tier)
    {
        if (tier is < 1 or > 5) throw new InvalidOperationException("Tier must be between 1 and 5");
    }
    private static void ValidateCapabilities(IReadOnlyList<string> capabilities)
    {
        if (capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Count || capabilities.Except(KnownCapabilities, StringComparer.Ordinal).Any())
            throw new InvalidOperationException("Unknown or duplicate capabilities: " + string.Join(",", capabilities));
    }
}
