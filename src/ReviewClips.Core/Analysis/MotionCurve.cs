using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Analysis;

/// <summary>One sampled frame-difference reading from the analysis pass.</summary>
public readonly record struct MotionSample(double AtSeconds, double Mafd);

/// <summary>
/// A time-ordered curve of mean-absolute-frame-difference readings.
/// <para>
/// This comes free from the same FFmpeg pass that finds scene cuts (<c>scdet</c> exports
/// <c>lavfi.scd.mafd</c> per frame), which is why analysis is a single pass rather than two.
/// </para>
/// </summary>
public sealed class MotionCurve
{
    private readonly double[] _times;
    private readonly double[] _values;

    public MotionCurve(IEnumerable<MotionSample> samples)
    {
        var ordered = samples.OrderBy(s => s.AtSeconds).ToArray();
        _times = [.. ordered.Select(s => s.AtSeconds)];
        _values = [.. ordered.Select(s => s.Mafd)];
    }

    public static MotionCurve Empty { get; } = new([]);

    public int Count => _times.Length;

    public IReadOnlyList<MotionSample> Samples =>
        _times.Select((t, i) => new MotionSample(t, _values[i])).ToArray();

    /// <summary>Mean motion across a range, or <c>null</c> when the curve has no samples there.</summary>
    public double? MeanOver(TimeRange range)
    {
        var (from, to) = IndexRange(range);
        if (to <= from)
        {
            return null;
        }

        double sum = 0;
        for (var i = from; i < to; i++)
        {
            sum += _values[i];
        }

        return sum / (to - from);
    }

    /// <summary>Peak motion across a range, or <c>null</c> when the curve has no samples there.</summary>
    public double? MaxOver(TimeRange range)
    {
        var (from, to) = IndexRange(range);
        if (to <= from)
        {
            return null;
        }

        var max = double.MinValue;
        for (var i = from; i < to; i++)
        {
            if (_values[i] > max)
            {
                max = _values[i];
            }
        }

        return max;
    }

    /// <summary>
    /// Population standard deviation of motion over a range. Low values indicate an evenly
    /// paced shot; high values indicate the window straddles a cut or a sudden burst.
    /// </summary>
    public double? StdDevOver(TimeRange range)
    {
        var mean = MeanOver(range);
        if (mean is null)
        {
            return null;
        }

        var (from, to) = IndexRange(range);
        double sumSquares = 0;
        for (var i = from; i < to; i++)
        {
            var delta = _values[i] - mean.Value;
            sumSquares += delta * delta;
        }

        return Math.Sqrt(sumSquares / (to - from));
    }

    /// <summary>Median of all samples. Used to normalise thresholds per-title rather than globally.</summary>
    public double Median()
    {
        if (_values.Length == 0)
        {
            return 0d;
        }

        var sorted = _values.ToArray();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2d;
    }

    private (int From, int To) IndexRange(TimeRange range)
    {
        if (_times.Length == 0)
        {
            return (0, 0);
        }

        var from = Array.BinarySearch(_times, range.Start.TotalSeconds);
        if (from < 0)
        {
            from = ~from;
        }

        var to = Array.BinarySearch(_times, range.End.TotalSeconds);
        if (to < 0)
        {
            to = ~to;
        }

        return (Math.Clamp(from, 0, _times.Length), Math.Clamp(to, 0, _times.Length));
    }
}
