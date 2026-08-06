using ReviewClips.Core.Planning;

namespace ReviewClips.Core.Tests.Planning;

public class SplicePlannerTests
{
    [Theory]
    [InlineData(60, 5)]
    [InlineData(90, 5)]
    [InlineData(37, 4)]
    [InlineData(120, 7)]
    [InlineData(5, 5)]
    public void Plan_SumsExactlyToTheTarget_WithoutJitter(double target, double splice)
    {
        var durations = SplicePlanner.Plan(
            TimeSpan.FromSeconds(target),
            TimeSpan.FromSeconds(splice),
            TimeSpan.Zero,
            new Random(1));

        durations.Sum(d => d.TotalSeconds).ShouldBe(target, 0.0001);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public void Plan_SumsExactlyToTheTarget_WithJitter(int seed)
    {
        var durations = SplicePlanner.Plan(
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1.5),
            new Random(seed));

        durations.Sum(d => d.TotalSeconds).ShouldBe(90d, 0.0001);
    }

    [Fact]
    public void Plan_VariesLengthsWhenJitterIsEnabled()
    {
        var durations = SplicePlanner.Plan(
            TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1),
            new Random(7));

        // Ignore the final clip, which is trimmed to land on the target exactly.
        durations.Take(durations.Count - 1)
            .Select(d => Math.Round(d.TotalSeconds, 3))
            .Distinct()
            .Count()
            .ShouldBeGreaterThan(1);
    }

    [Fact]
    public void Plan_IsDeterministicForAGivenSeed()
    {
        var first = SplicePlanner.Plan(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), new Random(42));
        var second = SplicePlanner.Plan(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), new Random(42));

        first.ShouldBe(second);
    }

    [Fact]
    public void Plan_NeverProducesASegmentBelowTheMinimum()
    {
        // 32 / 5 leaves a 2s remainder; with jitter the tail can get very short.
        for (var seed = 0; seed < 50; seed++)
        {
            var durations = SplicePlanner.Plan(
                TimeSpan.FromSeconds(32),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(2),
                new Random(seed));

            durations.ShouldAllBe(d => d >= SplicePlanner.MinimumSegment);
        }
    }

    [Fact]
    public void Plan_AbsorbsATinyRemainderIntoThePreviousClip()
    {
        // 10.1s of 5s clips: the 0.1s tail must not become its own clip.
        var durations = SplicePlanner.Plan(
            TimeSpan.FromSeconds(10.1),
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero,
            new Random(1));

        durations.Count.ShouldBe(2);
        durations.Sum(d => d.TotalSeconds).ShouldBe(10.1, 0.0001);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(60, 0)]
    [InlineData(-10, 5)]
    public void Plan_ReturnsEmptyForDegenerateInput(double target, double splice) =>
        SplicePlanner.Plan(
                TimeSpan.FromSeconds(target),
                TimeSpan.FromSeconds(splice),
                TimeSpan.Zero,
                new Random(1))
            .ShouldBeEmpty();

    [Fact]
    public void Plan_ClampsJitterSoItCannotExceedTheSpliceLength()
    {
        // Jitter larger than the splice length would otherwise allow a negative duration.
        var durations = SplicePlanner.Plan(
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10),
            new Random(3));

        durations.ShouldAllBe(d => d > TimeSpan.Zero);
        durations.Sum(d => d.TotalSeconds).ShouldBe(60d, 0.0001);
    }
}

/// <summary>
/// Covers the transition compensation. A request is for the runtime of the finished file, so
/// the material planned must exceed it by the amount cross-transitions consume.
/// </summary>
public class PlanForOutputTests
{
    private static double PredictedOutput(IReadOnlyList<TimeSpan> durations, double transitionSeconds)
    {
        var effective = SplicePlanner.EffectiveTransition(durations, TimeSpan.FromSeconds(transitionSeconds));
        return durations.Sum(d => d.TotalSeconds) - (effective.TotalSeconds * (durations.Count - 1));
    }

    /// <summary>
    /// Splice, jitter and transition combinations that between them exercise every regime the
    /// compensation has: a transition far below half a clip, one close to it, one at exactly
    /// half, and two that exceed the splice outright.
    /// </summary>
    public static TheoryData<double, double, double> Cadences =>
        new()
        {
            { 5, 1, 0.4 },      // default
            { 5, 1, 0.6 },      // --profile subtle
            { 7, 1, 0.8 },      // --profile kenburns
            { 3, 0, 1.0 },      // 2T below the splice, no jitter to absorb the tail
            { 2, 0.5, 1.0 },    // 2T exactly the splice
            { 1.5, 0.3, 1.2 },  // 2T above the splice
            { 5, 1, 2.0 },      // a transition nobody should ask for
        };

    /// <summary>
    /// The whole point of <see cref="SplicePlanner.PlanForOutput"/>, swept rather than sampled.
    /// <para>
    /// This was five hand-picked tuples at one seed, and it passed throughout while
    /// <c>--profile kenburns</c> was overshooting on one request in eight — the five happened to
    /// be five that landed. The failure needs a particular leftover on the final clip, so it is a
    /// property of the (target, seed) pair rather than of the settings, and no fixed list of
    /// settings can be trusted to contain one. Hence the sweep: 7 cadences x 8 targets x 12 seeds.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Cadences))]
    public void CompensatesSoTheRenderedRuntimeMatchesTheRequest(
        double splice,
        double jitter,
        double transition)
    {
        double[] targets = [11, 30, 57, 90, 120, 300, 566, 595];

        foreach (var target in targets)
        {
            for (var seed = 1; seed <= 12; seed++)
            {
                var durations = SplicePlanner.PlanForOutput(
                    TimeSpan.FromSeconds(target),
                    TimeSpan.FromSeconds(splice),
                    TimeSpan.FromSeconds(jitter),
                    TimeSpan.FromSeconds(transition),
                    seed);

                PredictedOutput(durations, transition).ShouldBe(
                    target,
                    0.25,
                    $"target {target}s, splice {splice}s, jitter {jitter}s, "
                    + $"transition {transition}s, seed {seed}");
            }
        }
    }

    /// <summary>
    /// The specific shape that used to break it: a leftover short enough to cap the overlap for
    /// every join in the render. This rendered 36s for a 30s request.
    /// </summary>
    [Fact]
    public void DoesNotOvershootWhenTheTailClipWouldHaveCappedTheTransition()
    {
        var durations = SplicePlanner.PlanForOutput(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(3),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            seed: 4);

        PredictedOutput(durations, 1).ShouldBe(30d, 0.25);
    }

    /// <summary>
    /// The mechanism behind the fix, asserted directly so a regression is diagnosed rather than
    /// merely detected.
    /// <para>
    /// The overlap that actually gets applied must be a function of the settings alone. It used
    /// to depend on the leftover that landed on the final clip, which is a function of the target
    /// and the seed — and that is precisely the feedback that made the compensation loop cycle
    /// instead of converge. Note the assertion is not "the clamp never binds": a transition wider
    /// than half the splice still clamps, because no floor can make a clip longer than the splice
    /// that was asked for. What matters is that it clamps to the same value every time.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Cadences))]
    public void TheAppliedTransitionDoesNotDependOnTheSeedOrTheTarget(
        double splice,
        double jitter,
        double transition)
    {
        double[] targets = [30, 90, 300, 595];

        var applied = targets
            .SelectMany(target => Enumerable.Range(1, 12).Select(seed => (target, seed)))
            .Select(x => SplicePlanner.EffectiveTransition(
                SplicePlanner.PlanForOutput(
                    TimeSpan.FromSeconds(x.target),
                    TimeSpan.FromSeconds(splice),
                    TimeSpan.FromSeconds(jitter),
                    TimeSpan.FromSeconds(transition),
                    x.seed),
                TimeSpan.FromSeconds(transition)))
            .Distinct()
            .ToList();

        applied.Count.ShouldBe(1, $"applied overlaps: {string.Join(", ", applied)}");

        // And it is the requested transition unless the splice itself is too short to hold it.
        applied[0].TotalSeconds.ShouldBe(Math.Min(transition, splice / 2d), 0.0001);
    }

    [Fact]
    public void PlansMoreMaterialThanTheTargetWhenTransitionsAreUsed()
    {
        var durations = SplicePlanner.PlanForOutput(
            TimeSpan.FromMinutes(16),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(0.4),
            seed: 1);

        // 193ish clips lose roughly 77s to transitions, so the material must exceed 960s.
        durations.Sum(d => d.TotalSeconds).ShouldBeGreaterThan(1000);
    }

    [Fact]
    public void MatchesTheTargetExactlyWithoutTransitions()
    {
        var durations = SplicePlanner.PlanForOutput(
            TimeSpan.FromSeconds(300),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            seed: 3);

        durations.Sum(d => d.TotalSeconds).ShouldBe(300d, 0.0001);
    }

    [Fact]
    public void IsDeterministicForAGivenSeed()
    {
        var a = SplicePlanner.PlanForOutput(
            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.4), 7);
        var b = SplicePlanner.PlanForOutput(
            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0.4), 7);

        a.ShouldBe(b);
    }

    [Fact]
    public void HandlesATargetShorterThanASingleSplice()
    {
        var durations = SplicePlanner.PlanForOutput(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(0.4),
            seed: 1);

        durations.ShouldNotBeEmpty();
        durations.Sum(d => d.TotalSeconds).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void EffectiveTransition_IsCappedAtHalfTheShortestClip()
    {
        var durations = new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1.2) };

        SplicePlanner.EffectiveTransition(durations, TimeSpan.FromSeconds(0.9))
            .TotalSeconds
            .ShouldBe(0.6, 0.0001);
    }

    [Fact]
    public void EffectiveTransition_IsZeroForASingleClip() =>
        SplicePlanner.EffectiveTransition([TimeSpan.FromSeconds(5)], TimeSpan.FromSeconds(0.4))
            .ShouldBe(TimeSpan.Zero);
}
