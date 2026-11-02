using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using LiteMonitor.Contracts;

namespace LiteMonitor.Core;

/// <summary>
/// Immutable lookup view over one published hardware snapshot.
/// </summary>
internal sealed class HardwareSnapshotValueIndex
{
    private readonly Dictionary<string, List<MetricSample>> _samplesByKey;

    public HardwareSnapshotValueIndex(HardwareSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        var samplesByKey = new Dictionary<string, List<MetricSample>>(StringComparer.OrdinalIgnoreCase);
        var duplicateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (MetricSample sample in snapshot.Samples ?? Array.Empty<MetricSample>())
        {
            if (sample is null || string.IsNullOrWhiteSpace(sample.Key)) continue;

            if (!samplesByKey.TryGetValue(sample.Key, out List<MetricSample>? samples))
            {
                samples = new List<MetricSample>(1);
                samplesByKey.Add(sample.Key, samples);
            }
            else
            {
                duplicateKeys.Add(sample.Key);
            }

            samples.Add(sample);
        }

        _samplesByKey = samplesByKey;
        AmbiguousKeys = new ReadOnlyCollection<string>(new List<string>(duplicateKeys));
        GpuCount = CountGpus(snapshot.Samples ?? Array.Empty<MetricSample>());
    }

    public HardwareSnapshot Snapshot { get; }

    /// <summary>Keys that occur more than once. Consumers can surface this as a contract diagnostic.</summary>
    public IReadOnlyList<string> AmbiguousKeys { get; }

    public int GpuCount { get; }

    /// <summary>
    /// Returns the deterministic first sample for a key. When duplicate device identities exist,
    /// callers that know the expected device must use <see cref="TryGetSample(string,string?,out MetricSample?)"/>.
    /// </summary>
    public bool TryGetSample(string? key, out MetricSample? sample)
    {
        sample = null;
        if (string.IsNullOrWhiteSpace(key) ||
            !_samplesByKey.TryGetValue(key, out List<MetricSample>? samples) ||
            samples.Count == 0)
        {
            return false;
        }

        sample = samples[0];
        return true;
    }

    /// <summary>Returns the first sample matching both key and stable device identity.</summary>
    public bool TryGetSample(string? key, string? stableDeviceId, out MetricSample? sample)
    {
        sample = null;
        if (string.IsNullOrWhiteSpace(key) ||
            !_samplesByKey.TryGetValue(key, out List<MetricSample>? samples))
        {
            return false;
        }

        if (stableDeviceId is null)
        {
            sample = samples.Count > 0 ? samples[0] : null;
            return sample is not null;
        }

        foreach (MetricSample candidate in samples)
        {
            if (string.Equals(candidate.StableDeviceId, stableDeviceId, StringComparison.Ordinal))
            {
                sample = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns a usable panel value. Stale values remain visible; missing/error samples are not
    /// converted into the valid numeric value zero.
    /// </summary>
    public bool TryGetValue(string? key, out float value) =>
        TryGetValue(key, stableDeviceId: null, out value);

    public bool TryGetValue(string? key, string? stableDeviceId, out float value)
    {
        value = 0;
        if (!TryGetSample(key, stableDeviceId, out MetricSample? sample) ||
            sample is null ||
            sample.Quality is not (SampleQuality.Good or SampleQuality.Stale) ||
            !sample.Value.HasValue)
        {
            return false;
        }

        value = sample.Value.Value;
        return true;
    }

    public bool TryGetGoodValue(string? key, out float value) =>
        TryGetGoodValue(key, stableDeviceId: null, out value);

    public bool TryGetGoodValue(string? key, string? stableDeviceId, out float value)
    {
        value = 0;
        if (!TryGetSample(key, stableDeviceId, out MetricSample? sample) ||
            sample is null ||
            sample.Quality != SampleQuality.Good ||
            !sample.Value.HasValue)
        {
            return false;
        }

        value = sample.Value.Value;
        return true;
    }

    private static int CountGpus(IReadOnlyList<MetricSample> samples)
    {
        int maxIndex = -1;
        bool hasUsableLegacyGpu = false;

        foreach (MetricSample sample in samples)
        {
            if (sample is null || string.IsNullOrWhiteSpace(sample.Key)) continue;

            if (TryParseIndexedGpuKey(sample.Key, out int index))
            {
                if (index > maxIndex) maxIndex = index;
                continue;
            }

            if (sample.Key.StartsWith("GPU.", StringComparison.OrdinalIgnoreCase) &&
                sample.Quality is SampleQuality.Good or SampleQuality.Stale &&
                sample.Value.HasValue)
            {
                hasUsableLegacyGpu = true;
            }
        }

        return maxIndex >= 0 ? maxIndex + 1 : hasUsableLegacyGpu ? 1 : 0;
    }

    private static bool TryParseIndexedGpuKey(string key, out int index)
    {
        index = -1;
        const string prefix = "GPU.";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        int dot = key.IndexOf('.', prefix.Length);
        if (dot <= prefix.Length) return false;

        ReadOnlySpan<char> indexText = key.AsSpan(prefix.Length, dot - prefix.Length);
        return int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out index)
            && index >= 0
            && dot + 1 < key.Length;
    }
}
