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
    /// Shortest clip a plan may contain when cross-transitions are in use: twice the transition.
    /// <para>
    /// <see cref="EffectiveTransition"/> caps the overlap at half the <em>shortest</em> clip, so
    /// one short clip shrinks every join in the render. Since <see cref="PlanForOutput"/> solves
    /// the overlap compensation by iterating, that cap feeds back into its own input and the loop
    /// oscillates instead of converging. Holding every clip to twice the transition stops the cap
    /// ever binding, which makes the iteration a contraction.
    /// </para>
    /// </summary>
    public static TimeSpan MinimumSegmentFor(TimeSpan transition) =>
        transition > TimeSpan.Zero && transition * 2 > MinimumSegment
            ? transition * 2
            : MinimumSegment;

    /// <summary>
    /// Plans durations so the <em>finished render</em> lasts <paramref name="outputTarget"/>,
    /// compensating for the material that cross-transitions consume.
    /// <para>
    /// Each transition overlaps two clips, so N clips lose (N-1) transition durations from the
    /// total. The compensation is solved by fixed-point iteration because the clip count depends
    /// on the amount of material, which in turn depends on the clip count.
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

        // Keeps EffectiveTransition's cap from binding, so the loop below contracts rather than
        // cycles. See MinimumSegmentFor.
        var floor = MinimumSegmentFor(transition);

        var material = outputTarget;
        var best = Plan(material, splice, jitter, new Random(seed), floor);

        // Converges in a few passes; bounded so a pathological configuration cannot spin.
        for (var iteration = 0; iteration < 12; iteration++)
        {
            // Fresh RNG per pass: jitter stays deterministic for a given amount of material,
            // so the iteration cannot oscillate on random noise.
            var durations = Plan(material, splice, jitter, new Random(seed), floor);
            if (durations.Count == 0)
            {
                return best;
            }

            best = durations;

            var error = outputTarget - RenderedDuration(durations, transition);
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
    /// Plans <paramref name="count"/> equal-length clips whose <em>finished render</em> lasts
    /// <paramref name="outputTarget"/>.
    /// <para>
    /// The cue-driven case, where the caller dictates the clip count rather than deriving it from
    /// a splice length. <see cref="PlanForOutput"/> cannot be reused: it solves the overlap
    /// compensation for the clip count <em>it</em> arrives at, and spending that material over a
    /// different count loses a different number of overlaps.
    /// </para>
    /// </summary>
    /// <param name="outputTarget">Desired runtime of the finished file.</param>
    /// <param name="count">Number of clips to divide it between.</param>
    /// <param name="transition">Requested transition duration; pass zero for hard cuts.</param>
    public static IReadOnlyList<TimeSpan> PlanEqualForOutput(
        TimeSpan outputTarget,
        int count,
        TimeSpan transition)
    {
        if (outputTarget <= TimeSpan.Zero || count <= 0)
        {
            return [];
        }

        // MinimumSegment, not the wider MinimumSegmentFor: that floor guards against a *random*
        // tail clip capping the overlap. Here every clip is the same length, so the cap is
        // deterministic and the re-solve below accounts for it exactly.
        var floor = MinimumSegment;

        if (count == 1 || transition <= TimeSpan.Zero)
        {
            // A single clip has nothing to overlap with, and hard cuts consume no material.
            return Equal(outputTarget.TotalSeconds / count, count, floor);
        }

        // N clips lose (N-1) overlaps, so the material needed is target + transition * (N-1).
        var overlaps = count - 1;
        var perClip = (outputTarget.TotalSeconds + (transition.TotalSeconds * overlaps)) / count;

        // EffectiveTransition caps the overlap at half the shortest clip. When that cap binds the
        // arithmetic above is solving the wrong equation, so re-solve against the capped form:
        // rendered = N*p - (p/2)*(N-1) = p*(N+1)/2, hence p = 2*target/(N+1).
        if (perClip < transition.TotalSeconds * 2d)
        {
            perClip = 2d * outputTarget.TotalSeconds / (count + 1);
        }

        return Equal(perClip, count, floor);
    }

    /// <summary>
    /// <paramref name="count"/> clips of <paramref name="seconds"/>, never shorter than
    /// <paramref name="floor"/>.
    /// <para>
    /// The floor binds only when the fair share falls below it — enough cues over a short enough
    /// runtime. Overshooting the request is preferred to silently dropping cues, and the overshoot
    /// is reported. Otherwise the caller's length is already above the floor and passes through.
    /// </para>
    /// </summary>
    private static IReadOnlyList<TimeSpan> Equal(double seconds, int count, TimeSpan floor)
    {
        var length = TimeSpan.FromSeconds(seconds);
        return [.. Enumerable.Repeat(length > floor ? length : floor, count)];
    }

    /// <summary>
    /// Runtime of the finished render for these clip lengths, after cross-transition overlap.
    /// <para>
    /// The single source of this arithmetic: <see cref="Pipeline.RenderPlan.EffectiveDuration"/>
    /// reports it, <see cref="PlanForOutput"/> iterates against it, and the pipeline checks the
    /// finished plan against the request with it. All three readings have to agree.
    /// </para>
    /// </summary>
    public static TimeSpan RenderedDuration(
        IReadOnlyList<TimeSpan> durations,
        TimeSpan transition)
    {
        ArgumentNullException.ThrowIfNull(durations);

        var total = durations.Aggregate(TimeSpan.Zero, (a, b) => a + b);
        var effective = EffectiveTransition(durations, transition);

        return total - (effective * (durations.Count - 1));
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
    /// <param name="minimumSegment">
    /// Shortest clip the plan may contain. Defaults to <see cref="MinimumSegment"/>; callers using
    /// cross-transitions pass <see cref="MinimumSegmentFor"/>.
    /// </param>
    public static IReadOnlyList<TimeSpan> Plan(
        TimeSpan target,
        TimeSpan splice,
        TimeSpan jitter,
        Random random,
        TimeSpan? minimumSegment = null)
    {
        ArgumentNullException.ThrowIfNull(random);

        if (target <= TimeSpan.Zero || splice <= TimeSpan.Zero)
        {
            return [];
        }

        var floor = minimumSegment ?? MinimumSegment;

        // Jitter must not produce a zero or negative length. A floor at or above the splice
        // leaves no room to vary, which is correct: the caller asked for clips no shorter than
        // the nominal length.
        var maxJitter = TimeSpan.FromSeconds(Math.Min(
            jitter.TotalSeconds,
            Math.Max(splice.TotalSeconds - floor.TotalSeconds, 0d)));

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
                if (remaining < floor && durations.Count > 0)
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
