using System.Security.Cryptography;
using System.Reflection;
using System.Runtime.Loader;
using Xunit;
using Xunit.Abstractions;

namespace LiteMonitor.TestSuites;

public sealed record DiscoveredTest(string Assembly, string Class, string Method, string DisplayName,
    string UniqueId, string? SkipReason)
{
    public string MethodKey => $"{Assembly}|{Class}.{Method}";
}

public static class Discovery
{
    public static List<DiscoveredTest> Read(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Test assembly is missing", path);
        string directory = Path.GetDirectoryName(path)!;
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            string dependency = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(dependency) ? context.LoadFromAssemblyPath(dependency) : null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        try { return ReadCore(path); }
        finally { AssemblyLoadContext.Default.Resolving -= Resolve; }
    }

    private static List<DiscoveredTest> ReadCore(string path)
    {
        AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        string assemblyName = Path.GetFileNameWithoutExtension(path);
        using var sink = new DiscoverySink();
        using var controller = new XunitFrontController(AppDomainSupport.Denied, path);
        var options = TestFrameworkOptions.ForDiscovery(new TestAssemblyConfiguration
        {
            PreEnumerateTheories = true,
            MethodDisplay = TestMethodDisplay.ClassAndMethod,
            MethodDisplayOptions = TestMethodDisplayOptions.None
        });
        controller.Find(false, sink, options);
        if (!sink.Done.WaitOne(TimeSpan.FromSeconds(60))) throw new TimeoutException("Test discovery timed out");
        if (sink.Errors.Count != 0) throw new InvalidOperationException(string.Join("\n", sink.Errors));
        var result = new List<DiscoveredTest>();
        foreach (ITestCase test in sink.Cases)
        {
            if (test.GetType().Name.Contains("TheoryTestCase", StringComparison.Ordinal))
                throw new InvalidOperationException("Theory cannot be pre-enumerated: " + test.DisplayName);
            result.Add(new DiscoveredTest(assemblyName, test.TestMethod.TestClass.Class.Name,
                test.TestMethod.Method.Name, test.DisplayName, test.UniqueID, test.SkipReason));
        }
        if (result.Count == 0) throw new InvalidOperationException("No tests discovered in " + path);
        return result.OrderBy(t => t.MethodKey, StringComparer.Ordinal)
            .ThenBy(t => t.DisplayName, StringComparer.Ordinal).ToList();
    }

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class DiscoverySink : Xunit.Sdk.LongLivedMarshalByRefObject, IMessageSink, IDisposable
    {
        public ManualResetEvent Done { get; } = new(false);
        public List<ITestCase> Cases { get; } = [];
        public List<string> Errors { get; } = [];
        public bool OnMessage(IMessageSinkMessage message)
        {
            if (message is ITestCaseDiscoveryMessage found) Cases.Add(found.TestCase);
            if (message is IErrorMessage error) Errors.AddRange(error.Messages);
            if (message is IDiscoveryCompleteMessage) Done.Set();
            return true;
        }
        public void Dispose() => Done.Dispose();
    }
}
