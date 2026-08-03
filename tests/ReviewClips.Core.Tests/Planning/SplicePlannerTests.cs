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

    [Theory]
    [InlineData(960, 5, 0.4)]
    [InlineData(90, 5, 0.4)]
    [InlineData(300, 7, 0.8)]
    [InlineData(60, 4, 0.5)]
    [InlineData(30, 5, 0.4)]
    public void CompensatesSoTheRenderedRuntimeMatchesTheRequest(
        double target,
        double splice,
        double transition)
    {
        var durations = SplicePlanner.PlanForOutput(
            TimeSpan.FromSeconds(target),
            TimeSpan.FromSeconds(splice),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(transition),
            seed: 42);

        PredictedOutput(durations, transition).ShouldBe(target, 0.25);
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
