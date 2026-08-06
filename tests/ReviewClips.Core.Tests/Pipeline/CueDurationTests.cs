using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;

namespace ReviewClips.Core.Tests.Pipeline;

/// <summary>
/// A cue-driven render has to last as long as it was asked to.
/// <para>
/// The clip count for this strategy comes from the cue list rather than from the splice cadence,
/// which makes the transition-overlap compensation a different sum. Getting that wrong is not
/// visible in the clip list — every clip looks reasonable — it only shows up in the length of
/// the finished file, which is exactly the number nobody re-measures.
/// </para>
/// </summary>
public class CueDurationTests
{
    private static ClipRequest CueRequest(
        PipelineHarness harness,
        string source,
        double durationSeconds,
        double[] cueSeconds,
        double transitionSeconds = 0.4,
        int? maxClips = null)
    {
        _ = harness;

        return PipelineHarness.Request(source, durationSeconds, maxClips: maxClips) with
        {
            Selection = PipelineHarness.Request(source).Selection with
            {
                Strategy = SelectionStrategy.Cues,
                Cues = [.. cueSeconds.Select(TimeSpan.FromSeconds)],

                // Snapping moves a cue's start, never its length, and needs analysis the harness
                // does not produce. Off, so the arithmetic under test is the only variable.
                SnapCuesToScene = false,
            },
            Transition = new TransitionOptions
            {
                Kind = transitionSeconds > 0 ? TransitionKind.DipToBlack : TransitionKind.None,
                Duration = TimeSpan.FromSeconds(transitionSeconds),
                FadeIn = TimeSpan.Zero,
                FadeOut = TimeSpan.Zero,
            },
        };
    }

    [Theory]
    [InlineData(40, new[] { 330d, 1330d, 3945d, 2400d })]
    [InlineData(120, new[] { 330d, 1330d, 3945d })]
    [InlineData(300, new[] { 330d, 1330d })]
    [InlineData(90, new[] { 100d, 200d, 300d, 400d, 500d, 600d, 700d })]
    public async Task ACueRenderLastsAsLongAsRequested(double target, double[] cues)
    {
        var harness = new PipelineHarness();
        var source = harness.AddSource("film.mkv", 5000);

        var plan = await harness.PlanAsync(CueRequest(harness, source, target, cues));

        plan.Segments.Count.ShouldBe(cues.Length);
        plan.EffectiveDuration.TotalSeconds.ShouldBe(target, 0.0001);
    }

    /// <summary>
    /// The specific regression: two cues over five minutes used to be handed a budget planned
    /// for sixty-odd five-second clips, and paid back only one of the sixty transitions it had
    /// been charged for. The result overshot by more than twenty-five seconds.
    /// </summary>
    [Fact]
    public async Task TwoCuesOverALongRenderDoNotOvershoot()
    {
        var harness = new PipelineHarness();
        var source = harness.AddSource("film.mkv", 5000);

        var plan = await harness.PlanAsync(CueRequest(harness, source, 300, [330d, 1330d]));

        plan.EffectiveDuration.TotalSeconds.ShouldBeLessThan(301d);
        plan.EffectiveDuration.TotalSeconds.ShouldBeGreaterThan(299d);
    }

    [Fact]
    public async Task HardCutsBetweenCuesStillLandOnTheTarget()
    {
        var harness = new PipelineHarness();
        var source = harness.AddSource("film.mkv", 5000);

        var plan = await harness.PlanAsync(
            CueRequest(harness, source, 40, [330d, 1330d, 3945d, 2400d], transitionSeconds: 0));

        plan.Segments.ShouldAllBe(s => s.Duration == TimeSpan.FromSeconds(10));
        plan.EffectiveDuration.TotalSeconds.ShouldBe(40d, 0.0001);
    }

    /// <summary>
    /// With a cap the runtime is filled by repetition instead of by longer clips, which is a
    /// different code path through <c>ClipSequencer</c>. It was already correct; pinned so the
    /// fix to the uncapped path cannot quietly break it.
    /// </summary>
    [Fact]
    public async Task CappedCuesStillLandOnTheTarget()
    {
        var harness = new PipelineHarness();
        var source = harness.AddSource("film.mkv", 5000);

        var plan = await harness.PlanAsync(
            CueRequest(harness, source, 40, [330d, 1330d, 3945d, 2400d], maxClips: 2));

        plan.DistinctClipCount.ShouldBe(2);
        plan.EffectiveDuration.TotalSeconds.ShouldBe(40d, 0.05d);
    }

    /// <summary>
    /// A cue near the end of a file gets a shorter clip than its share, because there is no more
    /// footage. The render is then shorter than requested — unavoidable, but it must not be
    /// silently reported as if it were not.
    /// </summary>
    [Fact]
    public async Task ACueTooCloseToTheEndShortensTheRenderRatherThanRunningPastIt()
    {
        var harness = new PipelineHarness();
        var source = harness.AddSource("short.mkv", 100);

        var plan = await harness.PlanAsync(CueRequest(harness, source, 60, [10d, 95d]));

        plan.Segments[^1].Duration.ShouldBe(TimeSpan.FromSeconds(5));
        plan.EffectiveDuration.ShouldBeLessThan(TimeSpan.FromSeconds(60));

        // Whatever it came to, the reported figure is the one the stitcher will produce.
        var expected = plan.Segments.Sum(s => s.Duration.TotalSeconds) - 0.4d;
        plan.EffectiveDuration.TotalSeconds.ShouldBe(expected, 0.0001);
    }
}
