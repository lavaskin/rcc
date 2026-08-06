using ReviewClips.Core.Primitives;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Planning;

/// <summary>How much of one source a render consumes.</summary>
public sealed record SourceUsageEntry
{
    public required string Path { get; init; }

    /// <summary>Union of the ranges referenced in this source. Each moment counted once.</summary>
    public required TimeSpan Used { get; init; }

    /// <summary>This source's full runtime.</summary>
    public required TimeSpan Available { get; init; }

    public double Fraction =>
        Available <= TimeSpan.Zero
            ? 0d
            : Math.Min(Used.TotalSeconds / Available.TotalSeconds, 1d);

    public double Percent => Fraction * 100d;
}

/// <summary>How much of the supplied footage a render actually consumes.</summary>
public sealed record SourceUsageReport
{
    /// <summary>One entry per source that fed the render, including those never drawn from.</summary>
    public required IReadOnlyList<SourceUsageEntry> Sources { get; init; }

    /// <summary>The configured ceiling, as a fraction.</summary>
    public required double Limit { get; init; }

    /// <summary>
    /// Source footage consumed across every source, as the union of the referenced ranges. Each
    /// distinct moment is counted once no matter how many times it appears in the output.
    /// </summary>
    public TimeSpan Used =>
        Sources.Aggregate(TimeSpan.Zero, (total, s) => total + s.Used);

    /// <summary>Combined runtime of every source that fed the render.</summary>
    public TimeSpan Available =>
        Sources.Aggregate(TimeSpan.Zero, (total, s) => total + s.Available);

    /// <summary>
    /// Fraction of the combined available footage consumed. 0 when there is nothing to measure.
    /// <para>
    /// Reported, but deliberately not what the limit is tested against — see
    /// <see cref="PeakFraction"/>.
    /// </para>
    /// </summary>
    public double Fraction =>
        Available <= TimeSpan.Zero
            ? 0d
            : Math.Min(Used.TotalSeconds / Available.TotalSeconds, 1d);

    public double Percent => Fraction * 100d;

    /// <summary>The source this render draws on most heavily, or null when there are none.</summary>
    public SourceUsageEntry? Peak =>
        Sources.Count == 0 ? null : Sources.MaxBy(s => s.Fraction);

    /// <summary>
    /// The largest share taken from any single source. This is what the limit is tested against.
    /// <para>
    /// Aggregating instead would let the check be defeated by supplying footage the render never
    /// touches: eight tenths of one film pooled with nine unused sources reports eight per cent.
    /// The guardrail is about how much of <em>a work</em> was used, and a pile of separate works
    /// has no combined runtime that means anything.
    /// </para>
    /// </summary>
    public double PeakFraction => Peak?.Fraction ?? 0d;

    public double PeakPercent => PeakFraction * 100d;

    public bool ExceedsLimit => Limit > 0d && PeakFraction > Limit;
}

/// <summary>
/// Measures the share of the source material a render consumes, and decides whether that is
/// worth mentioning.
/// <para>
/// The measurement is the <em>union</em> of the referenced source ranges, taken per source.
/// Clip count times clip length would be easier and wrong: it overstates consumption wherever
/// <c>--max-clips</c> repeats a pool. Overlapping clips are likewise counted once.
/// </para>
/// <para>
/// This is an aesthetic and record-keeping aid, not legal advice. Proportion used is one of
/// several factors that matter and a small proportion is not a safe harbour. See LEGAL.md.
/// </para>
/// </summary>
public static class SourceUsageGuard
{
    /// <summary>Share of a source a render may consume before it is remarked upon.</summary>
    public const double DefaultLimit = 0.10d;

    public static SourceUsageReport Evaluate(
        IEnumerable<Segment> segments,
        IEnumerable<(string Path, TimeSpan Duration)> sources,
        double limit)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(sources);

        var used = UsedBySource(segments);
        var entries = new List<SourceUsageEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, duration) in sources)
        {
            if (!seen.Add(path))
            {
                continue;
            }

            entries.Add(new SourceUsageEntry
            {
                Path = path,
                Used = used.GetValueOrDefault(path),

                // A source reporting no duration cannot be measured against; zero rather than
                // dropping it keeps it visible in the manifest with a fraction of 0.
                Available = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
            });
        }

        // Defensive: a segment whose source was never declared still consumed something, and
        // dropping it would understate the total. It cannot have a fraction, only a duration.
        foreach (var (path, duration) in used)
        {
            if (seen.Add(path))
            {
                entries.Add(new SourceUsageEntry
                {
                    Path = path,
                    Used = duration,
                    Available = TimeSpan.Zero,
                });
            }
        }

        return new SourceUsageReport { Sources = entries, Limit = limit };
    }

    /// <summary>
    /// The message to warn or fail with, or null when the render is inside the limit.
    /// <para>
    /// Shared between the warning and the strict-mode failure so the two can never describe the
    /// same situation differently.
    /// </para>
    /// </summary>
    public static string? Describe(SourceUsageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!report.ExceedsLimit || report.Peak is not { } peak)
        {
            return null;
        }

        // Named only when there is more than one source, since with a single source the name
        // adds nothing and the shorter sentence reads better.
        var subject = report.Sources.Count > 1
            ? $"'{System.IO.Path.GetFileName(peak.Path)}'"
            : "the supplied footage";

        return $"This render uses {peak.Percent:0.#}% of {subject} "
            + $"({peak.Used.TotalSeconds:0.#}s of {peak.Available.TotalSeconds:0.#}s), "
            + $"above the {report.Limit * 100d:0.#}% guideline. "
            + "Lower it with --max-clips, a shorter --duration, or more sources.";
    }

    /// <summary>
    /// Union of the referenced ranges within each source, keyed by path. Per source rather than
    /// summed: see <see cref="SourceUsageReport.PeakFraction"/>.
    /// </summary>
    private static Dictionary<string, TimeSpan> UsedBySource(IEnumerable<Segment> segments)
    {
        var result = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

        foreach (var bySource in segments.GroupBy(s => s.SourcePath, StringComparer.Ordinal))
        {
            result[bySource.Key] = TimeRangeSet.From(bySource.Select(s => s.Range)).TotalDuration;
        }

        return result;
    }
}
