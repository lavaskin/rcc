using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Selection;

/// <summary>
/// Spreads candidates evenly across the eligible runtime. Needs no analysis, so it is the
/// fastest strategy and a good sanity check when diagnosing the others.
/// </summary>
public sealed class UniformSegmentSelector : SegmentSelectorBase
{
    public override SelectionStrategy Strategy => SelectionStrategy.Uniform;

    public override bool RequiresAnalysis => false;

    protected override IEnumerable<SegmentCandidate> GenerateCandidates(
        SelectionContext context,
        SourceMedia source,
        TimeRangeSet eligible,
        TimeSpan window)
    {
        // Oversample relative to what we need so the gap and fit filters have room to reject.
        var wanted = Math.Max(context.SegmentCount * 3, 12);
        var total = eligible.TotalDuration - (window * eligible.Count);
        if (total <= TimeSpan.Zero)
        {
            total = eligible.TotalDuration;
        }

        var step = total.TotalSeconds / wanted;
        if (step <= 0)
        {
            yield break;
        }

        // Walk a single virtual timeline over the concatenated eligible ranges, then map back.
        for (var i = 0; i < wanted; i++)
        {
            var offset = TimeSpan.FromSeconds((i + 0.5) * step);
            if (MapToRanges(eligible, offset, window) is { } start)
            {
                yield return new SegmentCandidate(source, start, 1d, "uniform");
            }
        }
    }

    protected override IEnumerable<SegmentCandidate> Rank(
        IReadOnlyList<SegmentCandidate> candidates,
        SelectionContext context) =>
        // Preserve chronological spread rather than ranking, so picks stay evenly distributed.
        candidates.OrderBy(c => c.Start);

    /// <summary>
    /// Maps an offset in "eligible time" onto a real timestamp, skipping the gaps between
    /// eligible ranges so excluded stretches never consume candidate slots.
    /// </summary>
    internal static TimeSpan? MapToRanges(TimeRangeSet eligible, TimeSpan offset, TimeSpan window)
    {
        var remaining = offset;
        foreach (var range in eligible)
        {
            var usable = range.Duration - window;
            if (usable <= TimeSpan.Zero)
            {
                continue;
            }

            if (remaining <= usable)
            {
                return range.Start + remaining;
            }

            remaining -= usable;
        }

        return null;
    }
}
