using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Planning;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Tests.Pipeline;

/// <summary>
/// <see cref="RenderPlan.EffectiveDuration"/> is what <c>--dry-run</c> prints and what the
/// manifest records, so it has to agree with what the stitcher actually produces. The two are
/// computed in different assemblies from the same rule; these tests pin the rule.
/// </summary>
public class EffectiveDurationTests
{
    private static RenderPlan PlanWith(double[] segmentSeconds, double transitionSeconds)
    {
        var request = new ClipRequest
        {
            Sources = ["/movies/film.mkv"],
            OutputPath = "/out/render.mp4",
            TargetDuration = TimeSpan.FromSeconds(segmentSeconds.Sum()),
            Transition = new TransitionOptions
            {
                Kind = transitionSeconds > 0 ? TransitionKind.DipToBlack : TransitionKind.None,
                Duration = TimeSpan.FromSeconds(transitionSeconds),
            },
        };

        return new RenderPlan
        {
            Request = request,
            Sources = [],
            Segments = [.. segmentSeconds.Select(s => new Segment
            {
                SourcePath = "/movies/film.mkv",
                Start = TimeSpan.Zero,
                Duration = TimeSpan.FromSeconds(s),
                Score = 1d,
                Reason = "test",
            })],
            Encoder = new EncoderProfile
            {
                VideoEncoder = "libx264",
                IsHardware = false,
                QualityArguments = [],
                ExtraArguments = [],
            },
            Seed = 1,
        };
    }

    [Fact]
    public void OverlapIsSubtractedOncePerJoin()
    {
        var plan = PlanWith([5d, 5d, 5d, 5d], 0.4d);

        plan.EffectiveDuration.TotalSeconds.ShouldBe(20d - (0.4d * 3), 0.0001);
    }

    /// <summary>
    /// The regression. A transition cannot consume more of a clip than the clip contains, so the
    /// stitcher caps it at half the shortest. Subtracting the requested figure regardless
    /// under-reports the render — and does so precisely in the case a reader would be checking
    /// the number to understand.
    /// </summary>
    [Fact]
    public void OverlapIsClampedToHalfTheShortestClip()
    {
        // 2s transition against 1s clips: the stitcher will only overlap 0.5s.
        var plan = PlanWith([1d, 1d, 1d, 1d], 2d);

        plan.EffectiveDuration.TotalSeconds.ShouldBe(4d - (0.5d * 3), 0.0001);
    }

    [Fact]
    public void TheClampMatchesTheOneThePlannerSolvesAgainst()
    {
        double[] segments = [3d, 4d, 2.5d, 6d];
        var requested = TimeSpan.FromSeconds(1.8);

        var plan = PlanWith(segments, requested.TotalSeconds);

        var effective = SplicePlanner.EffectiveTransition(
            [.. segments.Select(TimeSpan.FromSeconds)],
            requested);

        plan.EffectiveDuration.TotalSeconds
            .ShouldBe(segments.Sum() - (effective.TotalSeconds * (segments.Length - 1)), 0.0001);
    }

    /// <summary>
    /// With the clamp in place the result can no longer go non-positive, so the old
    /// "fall back to the untrimmed total" guard is gone. Pinned because losing it silently would
    /// turn an under-report into a wildly over-stated one.
    /// </summary>
    [Fact]
    public void AnAbsurdlyLongTransitionStillLeavesAPositiveRuntime()
    {
        var plan = PlanWith([1d, 1d, 1d, 1d, 1d], 600d);

        plan.EffectiveDuration.ShouldBeGreaterThan(TimeSpan.Zero);
        plan.EffectiveDuration.ShouldBeLessThan(plan.TotalDuration);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.4d)]
    public void ASingleClipHasNothingToOverlapWith(double transition) =>
        PlanWith([7d], transition).EffectiveDuration.ShouldBe(TimeSpan.FromSeconds(7));

    [Fact]
    public void HardCutsLeaveTheTotalAlone()
    {
        var plan = PlanWith([5d, 5d, 5d], 0d);

        plan.EffectiveDuration.ShouldBe(plan.TotalDuration);
    }
}
