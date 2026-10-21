using System.Collections.Immutable;
using LiteMonitor.Rendering.Contracts;

namespace LiteMonitor.Rendering.Gpu.Device;

public enum GpuResourceKind
{
    Device,
    SwapChain,
    Texture,
    RenderTarget,
    Shader,
    Buffer,
    Query,
    GlyphCache,
    Capture,
    Host
}

public sealed record GpuResourceSnapshot(
    long ActiveResourceCount,
    long ActiveBytes,
    long PeakResourceCount,
    long PeakBytes,
    long TotalReservations,
    long TotalReleases,
    long RejectedReservations,
    IReadOnlyDictionary<GpuResourceKind, long> BytesByKind);

public sealed class GpuResourceLease : IDisposable
{
    private readonly GpuResourceLedger _owner;
    private int _disposed;

    internal GpuResourceLease(
        GpuResourceLedger owner,
        long id,
        GpuResourceKind kind,
        long bytes,
        string label,
        RenderGenerations generations)
    {
        _owner = owner;
        Id = id;
        Kind = kind;
        Bytes = bytes;
        Label = label;
        Generations = generations;
    }

    public long Id { get; }

    public GpuResourceKind Kind { get; }

    public long Bytes { get; }

    public string Label { get; }

    public RenderGenerations Generations { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _owner.Release(this);
    }
}

public sealed class GpuResourceLedger
{
    private readonly object _gate = new();
    private readonly long _byteBudget;
    private readonly int _resourceBudget;
    private readonly Dictionary<long, GpuResourceLease> _active = [];
    private long _nextId;
    private long _activeBytes;
    private long _peakResourceCount;
    private long _peakBytes;
    private long _totalReservations;
    private long _totalReleases;
    private long _rejectedReservations;

    public GpuResourceLedger(long byteBudget, int resourceBudget)
    {
        if (byteBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteBudget));
        }

        if (resourceBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resourceBudget));
        }

        _byteBudget = byteBudget;
        _resourceBudget = resourceBudget;
    }

    public long ByteBudget => _byteBudget;

    public int ResourceBudget => _resourceBudget;

    public bool TryReserve(
        GpuResourceKind kind,
        long bytes,
        string label,
        RenderGenerations generations,
        out GpuResourceLease? lease,
        out string? rejection)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        lock (_gate)
        {
            if (_active.Count >= _resourceBudget ||
                bytes > _byteBudget - _activeBytes)
            {
                _rejectedReservations++;
                lease = null;
                rejection = _active.Count >= _resourceBudget
                    ? "gpu_resource_count_budget_exceeded"
                    : "gpu_resource_byte_budget_exceeded";
                return false;
            }

            long id = ++_nextId;
            lease = new GpuResourceLease(this, id, kind, bytes, label, generations);
            _active.Add(id, lease);
            _activeBytes += bytes;
            _peakResourceCount = Math.Max(_peakResourceCount, _active.Count);
            _peakBytes = Math.Max(_peakBytes, _activeBytes);
            _totalReservations++;
            rejection = null;
            return true;
        }
    }

    public GpuResourceLease Reserve(
        GpuResourceKind kind,
        long bytes,
        string label,
        RenderGenerations generations)
    {
        if (!TryReserve(kind, bytes, label, generations, out GpuResourceLease? lease, out string? rejection))
        {
            throw new InvalidOperationException(rejection);
        }

        return lease!;
    }

    public GpuResourceSnapshot Snapshot()
    {
        lock (_gate)
        {
            ImmutableDictionary<GpuResourceKind, long>.Builder byKind =
                ImmutableDictionary.CreateBuilder<GpuResourceKind, long>();
            foreach (GpuResourceLease resource in _active.Values)
            {
                byKind[resource.Kind] = byKind.TryGetValue(resource.Kind, out long bytes)
                    ? bytes + resource.Bytes
                    : resource.Bytes;
            }

            return new GpuResourceSnapshot(
                _active.Count,
                _activeBytes,
                _peakResourceCount,
                _peakBytes,
                _totalReservations,
                _totalReleases,
                _rejectedReservations,
                byKind.ToImmutable());
        }
    }

    public bool ReleaseGeneration(RenderGenerations generations)
    {
        List<GpuResourceLease> resources;
        lock (_gate)
        {
            resources = _active.Values
                .Where(resource => !resource.Generations.CanAccept(generations))
                .ToList();
        }

        foreach (GpuResourceLease resource in resources)
        {
            resource.Dispose();
        }

        return resources.Count > 0;
    }

    internal void Release(GpuResourceLease resource)
    {
        lock (_gate)
        {
            if (!_active.Remove(resource.Id))
            {
                return;
            }

            _activeBytes -= resource.Bytes;
            _totalReleases++;
        }
    }
}
