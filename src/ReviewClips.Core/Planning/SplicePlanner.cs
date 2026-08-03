namespace ReviewClips.Core.Planning;

/// <summary>
/// Works out how long each spliced clip should be so the render lands on the requested
/// runtime exactly, while keeping the individual lengths slightly irregular.
/// </summary>
public static class SplicePlanner
{
    /// <summary>Clips shorter than this are not worth cutting to; the remainder is absorbed instead.</summary>
    public static TimeSpan MinimumSegment { get; } = TimeSpan.FromSeconds(0.75);

    /// <summary>
    /// Plans durations so the <em>finished render</em> lasts <paramref name="outputTarget"/>,
    /// compensating for the material that cross-transitions consume.
    /// <para>
    /// Each transition overlaps two clips, so N clips lose (N-1) transition durations from the
    /// total. Ignoring that makes a request for 16 minutes with 193 clips produce under 15,
    /// an 8% shortfall. The compensation is solved by fixed-point iteration because the clip
    /// count depends on the amount of material, which depends on the clip count.
    /// </para>
    /// </summary>
    /// <param name="outputTarget">Desired runtime of the finished file.</param>
    /// <param name="splice">Nominal clip length.</param>
    /// <param name="jitter">Maximum deviation either side of <paramref name="splice"/>.</param>
    /// <param name="transition">Requested transition duration; pass zero for hard cuts.</param>
    /// <param name="seed">Seed used to keep the jitter pattern stable across iterations.</param>
    public static IReadOnlyList<TimeSpan> PlanForOutput(
        TimeSpan outputTarget,
        TimeSpan splice,
        TimeSpan jitter,
        TimeSpan transition,
        int seed)
    {
        if (outputTarget <= TimeSpan.Zero || splice <= TimeSpan.Zero)
        {
            return [];
        }

        if (transition <= TimeSpan.Zero)
        {
            return Plan(outputTarget, splice, jitter, new Random(seed));
        }

        var material = outputTarget;
        var best = Plan(material, splice, jitter, new Random(seed));

        // Converges in a handful of passes; the loop is bounded so a pathological
        // configuration cannot spin.
        for (var iteration = 0; iteration < 12; iteration++)
        {
            // A fresh RNG per pass keeps the jitter pattern deterministic for a given amount
            // of material, so the iteration cannot oscillate on random noise.
            var durations = Plan(material, splice, jitter, new Random(seed));
            if (durations.Count == 0)
            {
                return best;
            }

            best = durations;

            var effective = EffectiveTransition(durations, transition);
            var predicted = durations.Aggregate(TimeSpan.Zero, (a, b) => a + b)
                - (effective * (durations.Count - 1));

            var error = outputTarget - predicted;
            if (Math.Abs(error.TotalSeconds) < 0.05d)
            {
                break;
            }

            material += error;

            if (material <= splice)
            {
                material = splice;
                break;
            }
        }

        return best;
    }

    /// <summary>
    /// Mirrors the stitcher's clamp: a transition longer than half the shortest clip would
    /// consume it, so the effective duration is capped.
    /// </summary>
    internal static TimeSpan EffectiveTransition(
        IReadOnlyList<TimeSpan> durations,
        TimeSpan requested)
    {
        if (durations.Count < 2 || requested <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var ceiling = TimeSpan.FromSeconds(durations.Min().TotalSeconds / 2);
        return requested > ceiling ? ceiling : requested;
    }

    /// <summary>
    /// Produces segment durations summing to exactly <paramref name="target"/>.
    /// </summary>
    /// <param name="target">Total runtime to fill.</param>
    /// <param name="splice">Nominal clip length.</param>
    /// <param name="jitter">Maximum deviation either side of <paramref name="splice"/>.</param>
    /// <param name="random">Seeded RNG, so a seed reproduces the same cadence.</param>
    public static IReadOnlyList<TimeSpan> Plan(
        TimeSpan target,
        TimeSpan splice,
        TimeSpan jitter,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        if (target <= TimeSpan.Zero || splice <= TimeSpan.Zero)
        {
            return [];
        }

        // Jitter must not be able to produce a zero or negative length.
        var maxJitter = TimeSpan.FromSeconds(Math.Min(
            jitter.TotalSeconds,
            Math.Max(splice.TotalSeconds - MinimumSegment.TotalSeconds, 0d)));

        var durations = new List<TimeSpan>();
        var accumulated = TimeSpan.Zero;

        while (accumulated < target)
        {
            var offset = maxJitter > TimeSpan.Zero
                ? TimeSpan.FromSeconds(((random.NextDouble() * 2) - 1) * maxJitter.TotalSeconds)
                : TimeSpan.Zero;

            var length = splice + offset;
            var remaining = target - accumulated;

            if (length >= remaining)
            {
                // Final clip: trim to land exactly on target.
                if (remaining < MinimumSegment && durations.Count > 0)
                {
                    // Too short to be its own clip; lengthen the previous one instead.
                    durations[^1] += remaining;
                }
                else
                {
                    durations.Add(remaining);
                }

                break;
            }

            durations.Add(length);
            accumulated += length;
        }

        return durations;
    }
}
