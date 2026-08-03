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
    /// Repeats <paramref name="distinct"/> until the durations sum to <paramref name="materialTotal"/>.
    /// <para>
    /// The pool is reshuffled on every pass and a clip is never allowed to follow itself across
    /// a cycle boundary, so the repetition is much less obvious than a plain loop. Repeated
    /// entries keep identical start and duration, which lets the pipeline encode each one once
    /// and reference it many times.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Segment> Fill(
        IReadOnlyList<Segment> distinct,
        TimeSpan materialTotal,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(distinct);
        ArgumentNullException.ThrowIfNull(random);

        if (distinct.Count == 0 || materialTotal <= TimeSpan.Zero)
        {
            return [];
        }

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
                    if (remaining < SplicePlanner.MinimumSegment && result.Count > 0)
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
    /// Counts distinct clips in a sequence, treating identical source and timing as the same clip.
    /// </summary>
    public static int CountDistinct(IEnumerable<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        return segments
            .Select(s => (s.SourcePath, s.Start, s.Duration))
            .Distinct()
            .Count();
    }

    /// <summary>Total unique source footage referenced, ignoring repeats.</summary>
    public static TimeSpan DistinctSourceDuration(IEnumerable<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        return segments
            .Select(s => (s.SourcePath, s.Start, s.Duration))
            .Distinct()
            .Aggregate(TimeSpan.Zero, (total, s) => total + s.Duration);
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
