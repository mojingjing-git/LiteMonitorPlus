using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace LiteMonitor.TestSuites;

public sealed record BuildStamp(string Root, string Configuration, string BaseOutputPath, string SourceFingerprint,
    Dictionary<string, string> Artifacts);

public static class BuildIdentity
{
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "TestResults", ".git", ".worktrees", ".artifacts",
        ".review-scratch", ".nuget-packages", ".dotnet-cli-home", ".cua-helper",
        ".evidence", ".tmp", "dist", "node_modules"
    };

    public static string SourceFingerprint(string root)
    {
        root = Path.GetFullPath(root);
        var inputs = EnumerateInputs(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in inputs.OrderBy(f => Path.GetRelativePath(root, f), StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative is "tests/test-tiers.json" or "tests/test-tier-baseline.json") continue;
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0" + Discovery.Hash(file) + "\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IReadOnlyList<string> EnumerateInputs(string root)
    {
        string[]? gitFiles = TryReadGitFileList(root);
        if (gitFiles is not null)
        {
            var fromGit = new List<string>(gitFiles.Length);
            foreach (string relative in gitFiles)
            {
                if (relative.Length == 0) continue;
                string fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Git returned a path outside the repository: " + relative);
                if (File.Exists(fullPath)) fromGit.Add(fullPath);
            }
            return fromGit;
        }

        var fallback = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? current))
        {
            fallback.AddRange(Directory.EnumerateFiles(current));
            foreach (string child in Directory.EnumerateDirectories(current))
            {
                if (!SkipDirectories.Contains(Path.GetFileName(child)) &&
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(child);
                }
            }
        }
        return fallback;
    }

    private static string[]? TryReadGitFileList(string root)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(root);
            startInfo.ArgumentList.Add("ls-files");
            startInfo.ArgumentList.Add("-z");
            startInfo.ArgumentList.Add("--cached");
            startInfo.ArgumentList.Add("--others");
            startInfo.ArgumentList.Add("--exclude-standard");

            using var process = Process.Start(startInfo);
            if (process is null) return null;
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            string output = outputTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0) return null;
            return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return null;
        }
    }

    public static BuildStamp Create(string root, string configuration, string baseOutput)
    {
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string directory, string name)
        {
            string path = Path.GetFullPath(Path.Combine(root, directory, baseOutput, configuration, "net8.0-windows", name));
            if (!File.Exists(path)) throw new FileNotFoundException("Build artifact is missing", path);
            artifacts[Path.GetRelativePath(root, path).Replace('\\', '/')] = Discovery.Hash(path);
        }
        foreach (string assembly in new[] { "LiteMonitor.Tests", "LiteMonitor.Updater.Tests", "LiteMonitor.GpuNonperformance.Tests" })
        {
            Add("tests/" + assembly, assembly + ".dll");
            Add("tests/" + assembly, "LiteMonitor++.dll");
            Add("tests/" + assembly, "LiteMonitor.Contracts.dll");
        }
        Add("tests/LiteMonitor.Updater.Tests", "LiteMonitor++.Updater.dll");
        Add("tools/TestSuiteCatalog", "TestSuiteCatalog.dll");
        foreach (string name in new[] { "LiteMonitor.HardwareHost.exe", "LiteMonitor.HardwareHost.dll", "LiteMonitor.HardwareHost.deps.json", "LiteMonitor.HardwareHost.runtimeconfig.json", "LiteMonitor.Contracts.dll" })
        {
            Add("src/HardwareHost", name);
            Add(".", name);
            if (artifacts[Path.GetRelativePath(root, Path.Combine(root, "src/HardwareHost", baseOutput, configuration, "net8.0-windows", name)).Replace('\\', '/')]
                != artifacts[Path.GetRelativePath(root, Path.Combine(root, baseOutput, configuration, "net8.0-windows", name)).Replace('\\', '/')])
                throw new InvalidOperationException("Host and main output hashes differ: " + name);
        }
        return new BuildStamp(Path.GetFullPath(root), configuration, baseOutput, SourceFingerprint(root), artifacts);
    }

    public static void Check(BuildStamp stamp, string root, string configuration, string baseOutput)
    {
        if (!string.Equals(stamp.Root, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || stamp.Configuration != configuration || stamp.BaseOutputPath != baseOutput)
            throw new InvalidOperationException("Build stamp belongs to another root, configuration or output profile");
        BuildStamp current = Create(root, configuration, baseOutput);
        if (stamp.SourceFingerprint != current.SourceFingerprint) throw new InvalidOperationException("Sources changed since the recorded build; rebuild before using -NoBuild");
        if (stamp.Artifacts.Count != current.Artifacts.Count || stamp.Artifacts.Any(a => current.Artifacts.GetValueOrDefault(a.Key) != a.Value))
            throw new InvalidOperationException("Build artifacts changed, are missing, or have an incomplete inventory");
    }
}
