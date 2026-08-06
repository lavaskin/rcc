using ReviewClips.Core.Primitives;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Planning;

/// <summary>
/// Fills a runtime by cycling through a limited pool of distinct clips.
/// <para>
/// Exists so a long render can be built from a small amount of source footage: without a cap,
/// footage consumed tracks runtime roughly one for one. Beyond the aesthetic question, bounding
/// the total taken from a work is useful in its own right.
/// </para>
/// </summary>
public static class ClipSequencer
{
    /// <summary>
    /// Repeats <paramref name="pool"/> so the <em>finished render</em> lasts
    /// <paramref name="outputTarget"/>, compensating for transition overlap.
    /// <para>
    /// Solved by fixed-point iteration for the same reason as
    /// <see cref="SplicePlanner.PlanForOutput"/>: slot count and material each determine the
    /// other. A fresh RNG per pass keeps the sequence deterministic so the loop cannot oscillate
    /// on shuffle noise.
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

        // The trimmed final slot would otherwise cap EffectiveTransition for the whole sequence
        // and turn this loop into a cycle. See SplicePlanner.MinimumSegmentFor.
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
    /// The pool is reshuffled on every pass and a clip never follows itself across a cycle
    /// boundary. Repeated entries keep identical start and duration, which lets the pipeline
    /// encode each one once and reference it many times.
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
