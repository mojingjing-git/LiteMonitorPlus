using System.Security.Cryptography;
using System.Text;

namespace GpuHostLifecycleProbe;

/// <summary>
/// Guards the frozen candidate while a probe run is in flight.
///
/// GPU plan §3, frozen-candidate requirement 3: "T01 must not run on sources
/// that are being modified. If the probe detects a change of the measured
/// source-file hashes during the run, the conclusion is void and the run has
/// to be repeated." The check is implemented here so that it cannot be
/// satisfied by a manual declaration.
///
/// The aggregate algorithm is the one frozen by the T00 baseline: sort the
/// group members by relative path, concatenate "&lt;relative path&gt;\0&lt;lower
/// case sha256&gt;\n" and take the SHA-256 of that UTF-8 byte stream.
/// </summary>
internal sealed class CandidateHashGuard
{
    private readonly string _repoRoot;
    private readonly string[] _groups;
    private readonly Dictionary<string, string> _startHashes = new(StringComparer.Ordinal);

    private CandidateHashGuard(string repoRoot, string[] groups)
    {
        _repoRoot = repoRoot;
        _groups = groups;
    }

    public string RepositoryRoot => _repoRoot;

    /// <summary>Groups measured by the guard; the production side of the frozen candidate.</summary>
    public IReadOnlyList<string> Groups => _groups;

    /// <summary>Locates the repository root by walking up from the probe binaries.</summary>
    public static CandidateHashGuard? TryCreate(string startDirectory)
    {
        DirectoryInfo? directory = new(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LiteMonitor.csproj")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "Rendering")))
            {
                return new CandidateHashGuard(directory.FullName, ["src/Rendering", "src/UI"]);
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>Records the aggregate hash of every measured group. Must run before the experiment.</summary>
    public IReadOnlyDictionary<string, string> CaptureStart()
    {
        _startHashes.Clear();
        foreach (string group in _groups)
        {
            _startHashes[group] = ComputeAggregate(group);
        }

        return new Dictionary<string, string>(_startHashes, StringComparer.Ordinal);
    }

    /// <summary>Recomputes the aggregate hashes; true when nothing changed during the run.</summary>
    public bool VerifyUnchanged(out IReadOnlyDictionary<string, string> endHashes)
    {
        Dictionary<string, string> end = new(StringComparer.Ordinal);
        foreach (string group in _groups)
        {
            end[group] = ComputeAggregate(group);
        }

        endHashes = end;
        foreach (KeyValuePair<string, string> entry in _startHashes)
        {
            if (!end.TryGetValue(entry.Key, out string? value) || !string.Equals(value, entry.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Computes the aggregate hash of one directory group (bin/obj excluded).</summary>
    public string ComputeAggregate(string relativeDirectory)
    {
        string absolute = Path.Combine(_repoRoot, relativeDirectory);
        if (!Directory.Exists(absolute))
        {
            return string.Empty;
        }

        List<string> files = Directory
            .EnumerateFiles(absolute, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                            !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => path.Substring(_repoRoot.Length + 1).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        StringBuilder builder = new();
        foreach (string relative in files)
        {
            string full = Path.Combine(_repoRoot, relative);
            string hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(full))).ToLowerInvariant();
            }

            builder.Append(relative).Append('\0').Append(hash).Append('\n');
        }

        using SHA256 aggregate = SHA256.Create();
        return Convert.ToHexString(aggregate.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
