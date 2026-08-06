using ReviewClips.Core.Primitives;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Planning;

/// <summary>
/// Fills a runtime by cycling through a limited pool of distinct clips.
/// <para>
/// This exists so a long render can be built from a small amount of source footage. Filling
/// 16 minutes with unique 5s clips consumes over 17 minutes of the original; capping the pool at
/// 50 clips consumes about 4 minutes of it instead. Beyond the aesthetic question, the total
/// amount taken from a work is one of the factors that matters most, so being able to bound it
/// is useful in its own right.
/// </para>
/// </summary>
public static class ClipSequencer
{
    /// <summary>
    /// Repeats <paramref name="pool"/> so the <em>finished render</em> lasts
    /// <paramref name="outputTarget"/>, compensating for transition overlap.
    /// <para>
    /// Solved by fixed-point iteration for the same reason as
    /// <see cref="SplicePlanner.PlanForOutput"/>: the number of slots determines how much
    /// material the transitions consume, and the amount of material determines the number of
    /// slots. A fresh RNG per pass keeps the sequence deterministic so the loop cannot
    /// oscillate on shuffle noise.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Segment> FillForOutput(
        IReadOnlyList<Segment> pool,
        TimeSpan outputTarget,
        TimeSpan transition,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(pool);

        if (pool.Count == 0 || outputTarget <= TimeSpan.Zero)
        {
            return [];
        }

        // As in SplicePlanner.PlanForOutput: the trimmed final slot is what would otherwise cap
        // EffectiveTransition for the whole sequence and turn this loop into a cycle. See
        // SplicePlanner.MinimumSegmentFor.
        var floor = SplicePlanner.MinimumSegmentFor(transition);

        var material = outputTarget;
        var best = Fill(pool, material, new Random(seed), floor);

        for (var iteration = 0; iteration < 12; iteration++)
        {
            var sequence = Fill(pool, material, new Random(seed), floor);
            if (sequence.Count == 0)
            {
                return best;
            }

            best = sequence;

            var error = outputTarget - SplicePlanner.RenderedDuration(
                [.. sequence.Select(s => s.Duration)],
                transition);
            if (Math.Abs(error.TotalSeconds) < 0.05d)
            {
                break;
            }

            material += error;

            if (material <= TimeSpan.Zero)
            {
                break;
            }
        }

        return best;
    }

    /// <summary>
    /// Repeats <paramref name="distinct"/> until the durations sum to <paramref name="materialTotal"/>.
    /// <para>
    /// The pool is reshuffled on every pass and a clip is never allowed to follow itself across
    /// a cycle boundary, so the repetition is much less obvious than a plain loop. Repeated
    /// entries keep identical start and duration, which lets the pipeline encode each one once
    /// and reference it many times.
    /// </para>
    /// </summary>
    /// <param name="minimumSegment">
    /// Shortest slot the sequence may contain. Defaults to <see cref="SplicePlanner.MinimumSegment"/>;
    /// callers using cross-transitions pass <see cref="SplicePlanner.MinimumSegmentFor"/>.
    /// </param>
    public static IReadOnlyList<Segment> Fill(
        IReadOnlyList<Segment> distinct,
        TimeSpan materialTotal,
        Random random,
        TimeSpan? minimumSegment = null)
    {
        ArgumentNullException.ThrowIfNull(distinct);
        ArgumentNullException.ThrowIfNull(random);

        if (distinct.Count == 0 || materialTotal <= TimeSpan.Zero)
        {
            return [];
        }

        var floor = minimumSegment ?? SplicePlanner.MinimumSegment;

        var result = new List<Segment>();
        var running = TimeSpan.Zero;
        Segment? previous = null;

        while (running < materialTotal)
        {
            var cycle = Shuffle(distinct, random);

            // Prevent the same clip bridging two cycles, which would read as a stutter.
            if (previous is not null && cycle.Count > 1 && IsSameClip(cycle[0], previous))
            {
                (cycle[0], cycle[^1]) = (cycle[^1], cycle[0]);
            }

            var addedThisCycle = false;

            foreach (var clip in cycle)
            {
                var remaining = materialTotal - running;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                if (clip.Duration >= remaining)
                {
                    // Final slot: trim to land exactly on the target.
                    if (remaining < floor && result.Count > 0)
                    {
                        // Too short to stand alone; lengthen the previous entry instead.
                        result[^1] = result[^1] with { Duration = result[^1].Duration + remaining };
                    }
                    else
                    {
                        result.Add(clip with { Duration = remaining });
                    }

                    running = materialTotal;
                    addedThisCycle = true;
                    break;
                }

                result.Add(clip);
                running += clip.Duration;
                previous = clip;
                addedThisCycle = true;
            }

            // Defensive: a pool of zero-length clips would otherwise spin forever.
            if (!addedThisCycle)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Counts distinct positions in the source, which is the pool size that <c>--max-clips</c>
    /// controls. Two entries starting at the same timestamp are the same clip even if one was
    /// trimmed shorter to land on the target duration.
    /// </summary>
    public static int CountDistinct(IEnumerable<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        return segments
            .Select(s => (s.SourcePath, s.Start))
            .Distinct()
            .Count();
    }

    /// <summary>
    /// How much of the source material was actually used.
    /// <para>
    /// Computed as the union of the referenced time ranges, not the sum of clip lengths, so
    /// repeats and any overlap between clips are each counted once. This is the number that
    /// answers "how much of this film is in my video".
    /// </para>
    /// </summary>
    public static TimeSpan DistinctSourceDuration(IEnumerable<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var total = TimeSpan.Zero;

        foreach (var bySource in segments.GroupBy(s => s.SourcePath, StringComparer.Ordinal))
        {
            total += TimeRangeSet.From(bySource.Select(s => s.Range)).TotalDuration;
        }

        return total;
    }

    private static bool IsSameClip(Segment a, Segment b) =>
        string.Equals(a.SourcePath, b.SourcePath, StringComparison.Ordinal)
        && a.Start == b.Start;

    private static List<Segment> Shuffle(IReadOnlyList<Segment> items, Random random)
    {
        var list = items.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}
