using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Planning;

/// <summary>How much of the supplied footage a render actually consumes.</summary>
public sealed record SourceUsageReport
{
    /// <summary>
    /// Source footage consumed, as the union of the referenced ranges. Each distinct moment is
    /// counted once no matter how many times it appears in the output.
    /// </summary>
    public required TimeSpan Used { get; init; }

    /// <summary>Combined runtime of every source that fed the render.</summary>
    public required TimeSpan Available { get; init; }

    /// <summary>The configured ceiling, as a fraction.</summary>
    public required double Limit { get; init; }

    /// <summary>Fraction of the available footage consumed. 0 when there is nothing to measure.</summary>
    public double Fraction =>
        Available <= TimeSpan.Zero
            ? 0d
            : Math.Min(Used.TotalSeconds / Available.TotalSeconds, 1d);

    public double Percent => Fraction * 100d;

    public bool ExceedsLimit => Limit > 0d && Fraction > Limit;
}

/// <summary>
/// Measures the share of the source material a render consumes, and decides whether that is
/// worth mentioning.
/// <para>
/// The measurement is the <em>union</em> of the referenced source ranges, taken from
/// <see cref="ClipSequencer.DistinctSourceDuration"/>. Multiplying clip count by clip length
/// would be easier and wrong: with <c>--max-clips</c> repeating a pool of 50 clips across 210
/// slots it overstates consumption more than fourfold, and would refuse renders that in fact
/// touch four minutes of a three-hour film. Overlapping clips are likewise counted once.
/// </para>
/// <para>
/// This is an aesthetic and record-keeping aid, not legal advice. Proportion used is one of
/// several factors that matter and a small proportion is not a safe harbour. See LEGAL.md.
/// </para>
/// </summary>
public static class SourceUsageGuard
{
    /// <summary>Share of the source footage a render may consume before it is remarked upon.</summary>
    public const double DefaultLimit = 0.10d;

    public static SourceUsageReport Evaluate(
        IEnumerable<Segment> segments,
        IEnumerable<TimeSpan> sourceDurations,
        double limit)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(sourceDurations);

        var available = TimeSpan.Zero;
        foreach (var duration in sourceDurations)
        {
            if (duration > TimeSpan.Zero)
            {
                available += duration;
            }
        }

        return new SourceUsageReport
        {
            Used = ClipSequencer.DistinctSourceDuration(segments),
            Available = available,
            Limit = limit,
        };
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

        if (!report.ExceedsLimit)
        {
            return null;
        }

        return $"This render uses {report.Percent:0.#}% of the supplied footage "
            + $"({report.Used.TotalSeconds:0.#}s of {report.Available.TotalSeconds:0.#}s), "
            + $"above the {report.Limit * 100d:0.#}% guideline. "
            + "Lower it with --max-clips, a shorter --duration, or more sources.";
    }
}
