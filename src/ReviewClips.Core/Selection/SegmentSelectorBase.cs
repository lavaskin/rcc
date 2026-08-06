using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Selection;

/// <summary>
/// Shared machinery for every strategy: eligibility, quota distribution across multiple
/// sources, minimum-gap enforcement with graceful relaxation, and final ordering.
/// Subclasses only decide which candidate starts exist and how desirable each one is.
/// </summary>
public abstract class SegmentSelectorBase : ISegmentSelector
{
    public abstract SelectionStrategy Strategy { get; }

    public abstract bool RequiresAnalysis { get; }

    public IReadOnlyList<Segment> SelectSegments(SelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.SegmentCount == 0 || context.Sources.Count == 0)
        {
            return [];
        }

        // Size eligibility by the longest window so every pick is valid for any duration.
        var window = context.LongestSegment;

        var pools = new List<CandidatePool>();
        foreach (var source in context.Sources)
        {
            var eligible = context.EligibleRanges(source, window);
            if (eligible.Count == 0)
            {
                continue;
            }

            var candidates = GenerateCandidates(context, source, eligible, window)
                .Where(c => eligible.CanFit(c.Start, window))
                .ToList();

            if (candidates.Count > 0)
            {
                pools.Add(new CandidatePool(source, eligible, candidates));
            }
        }

        if (pools.Count == 0)
        {
            return [];
        }

        var quotas = DistributeByWeight(context.SegmentCount, pools.Select(p => p.Eligible.TotalDuration.TotalSeconds).ToList());
        var picked = new List<SegmentCandidate>(context.SegmentCount);

        // First pass: honour each pool's quota.
        for (var i = 0; i < pools.Count; i++)
        {
            picked.AddRange(PickFrom(pools[i], quotas[i], context, window, picked));
        }

        // Second pass: if some pools under-delivered, let the others make up the shortfall.
        // The running list of picks is passed in so spacing is enforced against everything
        // chosen so far, not just against this call's own selections.
        if (picked.Count < context.SegmentCount)
        {
            foreach (var pool in pools)
            {
                if (picked.Count >= context.SegmentCount)
                {
                    break;
                }

                picked.AddRange(PickFrom(pool, context.SegmentCount - picked.Count, context, window, picked));
            }
        }

        if (picked.Count == 0)
        {
            return [];
        }

        var ordered = context.Options.Order == SegmentOrder.Shuffled
            ? Shuffle(picked, context.Random)
            : [.. picked.OrderBy(c => c.SourcePath, StringComparer.Ordinal).ThenBy(c => c.Start)];

        return Materialise(ordered, context);
    }

    /// <summary>Produces the possible start times for one source, each with a desirability score.</summary>
    protected abstract IEnumerable<SegmentCandidate> GenerateCandidates(
        SelectionContext context,
        SourceMedia source,
        TimeRangeSet eligible,
        TimeSpan window);

    /// <summary>
    /// Orders candidates for greedy picking. The default is descending score with a random
    /// tie-break, so equal-scoring candidates still vary between seeds.
    /// </summary>
    protected virtual IEnumerable<SegmentCandidate> Rank(
        IReadOnlyList<SegmentCandidate> candidates,
        SelectionContext context) =>
        candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(_ => context.Random.Next());

    /// <summary>
    /// Chooses up to <paramref name="wanted"/> further candidates from one source, spread
    /// across the usable footage.
    /// <para>
    /// Uses stratified sampling rather than plain score-greedy selection. Greedy picking with a
    /// relaxing minimum gap collapses badly: when the requested spacing cannot be satisfied it
    /// degrades to "take the N highest scores", and because scores are spatially correlated
    /// those all come from the same few seconds of film. Dividing the eligible footage into one
    /// stratum per required segment and taking the best candidate from each guarantees coverage
    /// while still preferring good footage locally.
    /// </para>
    /// </summary>
    /// <param name="alreadyPicked">
    /// Everything chosen so far, across all sources. Used only as a spacing baseline and to
    /// avoid duplicates; these do not count toward <paramref name="wanted"/>.
    /// </param>
    private List<SegmentCandidate> PickFrom(
        CandidatePool pool,
        int wanted,
        SelectionContext context,
        TimeSpan window,
        IReadOnlyList<SegmentCandidate> alreadyPicked)
    {
        if (wanted <= 0)
        {
            return [];
        }

        var taken = alreadyPicked.ToHashSet();
        var available = pool.Candidates.Where(c => !taken.Contains(c)).ToList();

        if (available.Count == 0)
        {
            return [];
        }

        var totalEligible = pool.Eligible.TotalDuration;

        // The widest spacing that is arithmetically achievable. Asking for more than this is
        // not an error, it is simply impossible, so clamp instead of failing or collapsing.
        var feasibleGap = wanted > 1
            ? TimeSpan.FromSeconds(totalEligible.TotalSeconds / wanted)
            : TimeSpan.Zero;

        var preferredGap = context.Options.MinGap < feasibleGap
            ? context.Options.MinGap
            : feasibleGap;

        // Hard floor, never relaxed: two picks closer together than one segment length would
        // overlap and put literally the same footage on screen twice. Returning fewer segments
        // and warning is strictly better than emitting a duplicate.
        if (preferredGap < window)
        {
            preferredGap = window;
        }

        // Spacing is evaluated against prior picks as well as new ones.
        var spacing = alreadyPicked.ToList();
        var accepted = new List<SegmentCandidate>(wanted);

        bool TryAccept(SegmentCandidate candidate, TimeSpan gap)
        {
            if (!IsFarEnough(candidate, spacing, gap))
            {
                return false;
            }

            spacing.Add(candidate);
            accepted.Add(candidate);
            return true;
        }

        // Project each candidate into eligible-time and assign it to a stratum.
        var stratumSize = totalEligible.TotalSeconds / wanted;
        var strata = new Dictionary<int, List<SegmentCandidate>>(wanted);

        foreach (var candidate in available)
        {
            var projected = pool.Eligible.Project(candidate.Start);
            if (projected is null)
            {
                continue;
            }

            var index = stratumSize > 0
                ? Math.Clamp((int)(projected.Value.TotalSeconds / stratumSize), 0, wanted - 1)
                : 0;

            if (!strata.TryGetValue(index, out var bucket))
            {
                bucket = [];
                strata[index] = bucket;
            }

            bucket.Add(candidate);
        }

        // One pass over the strata in order, taking the best candidate from each.
        for (var i = 0; i < wanted; i++)
        {
            if (accepted.Count >= wanted)
            {
                break;
            }

            if (!strata.TryGetValue(i, out var bucket))
            {
                continue;
            }

            foreach (var candidate in Rank(bucket, context))
            {
                if (TryAccept(candidate, preferredGap))
                {
                    break;
                }
            }
        }

        // Strata can be empty when footage is unevenly distributed. Backfill by score from
        // whatever is left, keeping the non-overlap floor so a constrained source yields fewer
        // clips rather than repeated ones.
        if (accepted.Count < wanted)
        {
            foreach (var candidate in Rank(available, context))
            {
                if (accepted.Count >= wanted)
                {
                    break;
                }

                TryAccept(candidate, window);
            }
        }

        return accepted;
    }

    private static bool IsFarEnough(
        SegmentCandidate candidate,
        IReadOnlyList<SegmentCandidate> accepted,
        TimeSpan minGap)
    {
        if (minGap <= TimeSpan.Zero)
        {
            return true;
        }

        foreach (var taken in accepted)
        {
            if (!string.Equals(taken.SourcePath, candidate.SourcePath, StringComparison.Ordinal))
            {
                continue;
            }

            if ((candidate.Start - taken.Start).Duration() < minGap)
            {
                return false;
            }
        }

        return true;
    }

    private static List<Segment> Materialise(
        List<SegmentCandidate> ordered,
        SelectionContext context)
    {
        var segments = new List<Segment>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            // Durations were computed up front; reuse them cyclically if we picked fewer
            // candidates than planned so the render still fills as much as it can.
            var duration = context.SegmentDurations[i % context.SegmentDurations.Count];
            var candidate = ordered[i];

            segments.Add(new Segment
            {
                SourcePath = candidate.SourcePath,
                Start = candidate.Start,
                Duration = duration,

                // Placement above reserved the retimed length, so the segment has to carry the
                // factor that produced it: everything downstream that asks how much source this
                // clip touches gets the same answer selection just used.
                SpeedFactor = context.SpeedFactor,
                Score = candidate.Score,
                Reason = candidate.Reason,
            });
        }

        return segments;
    }

    /// <summary>Largest-remainder apportionment, so proportional quotas sum exactly to <paramref name="total"/>.</summary>
    internal static int[] DistributeByWeight(int total, IReadOnlyList<double> weights)
    {
        var counts = new int[weights.Count];
        if (weights.Count == 0 || total <= 0)
        {
            return counts;
        }

        var sum = weights.Sum();
        if (sum <= 0)
        {
            // Degenerate weights: spread evenly.
            for (var i = 0; i < total; i++)
            {
                counts[i % weights.Count]++;
            }

            return counts;
        }

        var remainders = new List<(int Index, double Remainder)>(weights.Count);
        var assigned = 0;

        for (var i = 0; i < weights.Count; i++)
        {
            var exact = total * weights[i] / sum;
            var floor = (int)Math.Floor(exact);
            counts[i] = floor;
            assigned += floor;
            remainders.Add((i, exact - floor));
        }

        foreach (var (index, _) in remainders.OrderByDescending(r => r.Remainder))
        {
            if (assigned >= total)
            {
                break;
            }

            counts[index]++;
            assigned++;
        }

        return counts;
    }

    private static List<T> Shuffle<T>(IEnumerable<T> items, Random random)
    {
        var list = items.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    private sealed record CandidatePool(
        SourceMedia Source,
        TimeRangeSet Eligible,
        IReadOnlyList<SegmentCandidate> Candidates);
}
