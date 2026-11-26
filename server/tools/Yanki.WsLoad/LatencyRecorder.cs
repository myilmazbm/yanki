namespace Yanki.WsLoad;

/// <summary>
/// Collects latency samples per connection so the hot path never contends on a shared structure;
/// the samples are merged once, when the run is over.
/// </summary>
internal sealed class LatencyRecorder
{
    private readonly List<double> _samples = [];

    public void Record(double milliseconds) => _samples.Add(milliseconds);

    public IReadOnlyList<double> Samples => _samples;

    public static double[] Merge(IEnumerable<LatencyRecorder> recorders)
    {
        List<double> merged = [];
        foreach (LatencyRecorder recorder in recorders)
        {
            merged.AddRange(recorder.Samples);
        }

        double[] sorted = [.. merged];
        Array.Sort(sorted);
        return sorted;
    }

    /// <summary>Nearest-rank percentile over an already sorted sample set.</summary>
    public static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        int rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
