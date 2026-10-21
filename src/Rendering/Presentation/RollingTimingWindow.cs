namespace LiteMonitor.Rendering.Presentation;

public readonly record struct TimingPercentiles(
    int SampleCount,
    double? P50Milliseconds,
    double? P95Milliseconds,
    double? P99Milliseconds,
    double? MaxMilliseconds);

/// <summary>
/// Bounded timing samples for production diagnostics. Rendering owns this
/// window on the UI thread, so sampling does not introduce a worker thread or
/// another synchronization boundary.
/// </summary>
public sealed class RollingTimingWindow
{
    private readonly int _capacity;
    private readonly Queue<double> _samples;

    public RollingTimingWindow(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _samples = new Queue<double>(capacity);
    }

    public void Add(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
        {
            return;
        }

        if (_samples.Count == _capacity)
        {
            _samples.Dequeue();
        }

        _samples.Enqueue(milliseconds);
    }

    public void Reset()
    {
        _samples.Clear();
    }

    public TimingPercentiles Snapshot()
    {
        int count = _samples.Count;
        if (count == 0)
        {
            return default;
        }

        double[] ordered = _samples.ToArray();
        Array.Sort(ordered);
        return new TimingPercentiles(
            count,
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.99),
            ordered[^1]);
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        int index = Math.Max(
            0,
            Math.Min(
                ordered.Length - 1,
                (int)Math.Ceiling(percentile * ordered.Length) - 1));
        return ordered[index];
    }
}
