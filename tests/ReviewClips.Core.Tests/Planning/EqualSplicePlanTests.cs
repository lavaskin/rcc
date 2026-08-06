using ReviewClips.Core.Planning;

namespace ReviewClips.Core.Tests.Planning;

/// <summary>
/// Covers <see cref="SplicePlanner.PlanEqualForOutput"/>, the arithmetic behind a cue-driven
/// render's clip length.
/// </summary>
public class EqualSplicePlanTests
{
    /// <summary>
    /// The property that matters: what comes out of the stitcher is what was asked for. Stated
    /// the same way the stitcher states it, so a change to one has to be a change to both.
    /// </summary>
    private static double Rendered(IReadOnlyList<TimeSpan> durations, double transition)
    {
        var effective = SplicePlanner.EffectiveTransition(durations, TimeSpan.FromSeconds(transition));
        return durations.Sum(d => d.TotalSeconds) - (effective.TotalSeconds * (durations.Count - 1));
    }

    [Theory]
    [InlineData(40, 4, 0.4)]
    [InlineData(120, 3, 0.4)]
    [InlineData(300, 2, 0.4)]
    [InlineData(90, 7, 0.8)]
    [InlineData(60, 1, 0.4)]
    [InlineData(45, 12, 0.25)]
    [InlineData(30, 5, 0)]
    public void PlanEqualForOutput_RendersToExactlyTheTarget(double target, int count, double transition)
    {
        var durations = SplicePlanner.PlanEqualForOutput(
            TimeSpan.FromSeconds(target),
            count,
            TimeSpan.FromSeconds(transition));

        durations.Count.ShouldBe(count);
        Rendered(durations, transition).ShouldBe(target, 0.0001);
    }

    /// <summary>
    /// A transition wider than half a clip is clamped by the stitcher, so the plan has to solve
    /// against the clamped form or it lands somewhere else entirely.
    /// </summary>
    [Theory]
    [InlineData(8, 4, 3)]
    [InlineData(10, 6, 5)]
    [InlineData(4, 2, 10)]
    public void PlanEqualForOutput_StillLandsOnTargetWhenTheTransitionIsClamped(
        double target,
        int count,
        double transition)
    {
        var durations = SplicePlanner.PlanEqualForOutput(
            TimeSpan.FromSeconds(target),
            count,
            TimeSpan.FromSeconds(transition));

        Rendered(durations, transition).ShouldBe(target, 0.0001);
    }

    [Fact]
    public void PlanEqualForOutput_GivesEveryClipTheSameLength()
    {
        var durations = SplicePlanner.PlanEqualForOutput(
            TimeSpan.FromSeconds(97),
            5,
            TimeSpan.FromSeconds(0.4));

        durations.Distinct().Count().ShouldBe(1);
    }

    /// <summary>
    /// Hard cuts consume no material, so the clips simply divide the runtime. Worth pinning
    /// separately: it is the one case where the answer is obvious, and a regression there would
    /// be a regression in the common no-transition render.
    /// </summary>
    [Fact]
    public void PlanEqualForOutput_DividesPlainlyWhenThereIsNoTransition()
    {
        var durations = SplicePlanner.PlanEqualForOutput(TimeSpan.FromSeconds(40), 4, TimeSpan.Zero);

        durations.ShouldAllBe(d => d == TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void PlanEqualForOutput_TreatsASingleClipAsHavingNothingToOverlapWith()
    {
        var durations = SplicePlanner.PlanEqualForOutput(
            TimeSpan.FromSeconds(30),
            1,
            TimeSpan.FromSeconds(0.4));

        durations.ShouldHaveSingleItem().ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(-10, 4)]
    [InlineData(60, 0)]
    [InlineData(60, -1)]
    public void PlanEqualForOutput_ReturnsNothingForAnImpossibleRequest(double target, int count)
    {
        SplicePlanner
            .PlanEqualForOutput(TimeSpan.FromSeconds(target), count, TimeSpan.FromSeconds(0.4))
            .ShouldBeEmpty();
    }

    /// <summary>
    /// The bug this method exists to prevent: spending a budget solved for one clip count over a
    /// different clip count. Recreated here as the arithmetic it replaced, so the regression is
    /// pinned rather than merely fixed.
    /// </summary>
    [Fact]
    public void SpendingASpliceDerivedBudgetOverFewerClipsWouldOvershoot()
    {
        var target = TimeSpan.FromSeconds(300);
        var transition = TimeSpan.FromSeconds(0.4);

        var spliceDerived = SplicePlanner.PlanForOutput(
            target,
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero,
            transition,
            seed: 1);

        // What the old cue selector did: take the total, divide it between the cues.
        var budget = spliceDerived.Sum(d => d.TotalSeconds);
        var naive = Enumerable.Repeat(TimeSpan.FromSeconds(budget / 2), 2).ToList();

        Rendered(naive, 0.4).ShouldBeGreaterThan(320d);
        Rendered(SplicePlanner.PlanEqualForOutput(target, 2, transition), 0.4).ShouldBe(300d, 0.0001);
    }

    /// <summary>
    /// Enough cues over a short enough runtime and the equal share stops being a clip at all.
    /// Nothing in the arithmetic prevents that, because the arithmetic is solving for the target
    /// and the target is what has to give.
    /// </summary>
    [Theory]
    [InlineData(5, 15)]
    [InlineData(2, 40)]
    [InlineData(0.5, 100)]
    public void PlanEqualForOutput_NeverPlansAClipBelowTheMinimum(double target, int count)
    {
        var durations = SplicePlanner.PlanEqualForOutput(
            TimeSpan.FromSeconds(target),
            count,
            TimeSpan.FromSeconds(0.4));

        durations.Count.ShouldBe(count, "no cue is dropped to make the arithmetic work");
        durations.ShouldAllBe(d => d >= SplicePlanner.MinimumSegment);
    }

    /// <summary>
    /// And the floor is not reached in the ordinary case, so it cannot be quietly inflating
    /// renders that were landing exactly.
    /// </summary>
    [Theory]
    [InlineData(300, 3)]
    [InlineData(90, 8)]
    [InlineData(60, 20)]
    public void PlanEqualForOutput_LeavesAnOrdinaryDivisionAlone(double target, int count)
    {
        Rendered(
                SplicePlanner.PlanEqualForOutput(
                    TimeSpan.FromSeconds(target), count, TimeSpan.FromSeconds(0.4)),
                0.4)
            .ShouldBe(target, 0.0001);
    }
}
