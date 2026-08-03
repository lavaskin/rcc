using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Selection;

/// <summary>
/// Uniformly random starts within the eligible ranges. Reproducible for a given seed.
/// </summary>
public sealed class RandomSegmentSelector : SegmentSelectorBase
{
    public override SelectionStrategy Strategy => SelectionStrategy.Random;

    public override bool RequiresAnalysis => false;

    protected override IEnumerable<SegmentCandidate> GenerateCandidates(
        SelectionContext context,
        SourceMedia source,
        TimeRangeSet eligible,
        TimeSpan window)
    {
        var wanted = Math.Max(context.SegmentCount * 8, context.Options.CandidateDensity);

        // Sample proportionally to range length so long stretches are not under-represented.
        var usable = eligible
            .Select(r => (Range: r, Usable: (r.Duration - window).TotalSeconds))
            .Where(x => x.Usable > 0)
            .ToList();

        if (usable.Count == 0)
        {
            yield break;
        }

        var totalUsable = usable.Sum(x => x.Usable);

        for (var i = 0; i < wanted; i++)
        {
            var target = context.Random.NextDouble() * totalUsable;
            foreach (var (range, length) in usable)
            {
                if (target <= length)
                {
                    yield return new SegmentCandidate(
                        source,
                        range.Start + TimeSpan.FromSeconds(target),
                        context.Random.NextDouble(),
                        "random");
                    break;
                }

                target -= length;
            }
        }
    }
}
