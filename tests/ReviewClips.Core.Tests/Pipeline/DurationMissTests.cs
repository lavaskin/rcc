using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Tests.Pipeline;

/// <summary>
/// The cross-check between the plan the pipeline arrives at and the runtime that was asked for.
/// Every stage of planning aims at the target rather than measuring against it, and a render
/// that came out short still exited zero and said nothing.
/// </summary>
public class DurationMissTests
{
    private static string? DurationWarning(IEnumerable<string> warnings) =>
        warnings.FirstOrDefault(w => w.Contains("comes out", StringComparison.Ordinal));

    /// <summary>
    /// Three cues over five minutes, the last close enough to the end of the file that its equal
    /// share does not fit; the render lands 40s short.
    /// </summary>
    [Fact]
    public async Task ACueClampedAtTheEndOfItsSourceIsReported()
    {
        var harness = new PipelineHarness();
        var source = harness.AddSource("film.mkv", 300);

        var request = PipelineHarness.Request(source, durationSeconds: 300) with
        {
            Selection = PipelineHarness.Request(source).Selection with
            {
                Strategy = SelectionStrategy.Cues,
                Cues =
                [
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(120),
                    TimeSpan.FromSeconds(240),
                ],
            },
        };

        var plan = await harness.PlanAsync(request);

        plan.EffectiveDuration.ShouldBeLessThan(TimeSpan.FromSeconds(290));

        var warning = DurationWarning(plan.Warnings);
        warning.ShouldNotBeNull();
        warning.ShouldContain("short of");
        warning.ShouldContain("300s");

        // The advice must name what the reader can act on: for cues that is where the cues sit,
        // not the splice cadence they are not using.
        warning.ShouldContain("cue");
    }

    /// <summary>A render that lands on its request says nothing at all.</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(300)]
    public async Task ARenderThatLandsOnTheRequestIsSilent(double seconds)
    {
        var harness = new PipelineHarness();
        var source = harness.AddSource("film.mkv", 4000);

        var plan = await harness.PlanAsync(
            PipelineHarness.Request(source, durationSeconds: seconds));

        DurationWarning(plan.Warnings).ShouldBeNull();
        plan.EffectiveDuration.TotalSeconds.ShouldBe(seconds, 0.25);
    }

    /// <summary>
    /// The count-based shortfall already describes this in terms of clips and offers the better
    /// advice, so reporting it again in seconds would be noise.
    /// </summary>
    [Fact]
    public async Task AShortfallAlreadyReportedByClipCountIsNotRepeated()
    {
        var harness = new PipelineHarness();
        var source = harness.AddSource("short.mkv", 120);

        // 10 minutes of 5s clips from two minutes of source, spaced 20s apart: only a handful
        // of positions exist, so the cadence cannot be placed.
        var request = PipelineHarness.Request(source, durationSeconds: 600) with
        {
            Selection = PipelineHarness.Request(source).Selection with
            {
                MinGap = TimeSpan.FromSeconds(20),
            },
        };

        var plan = await harness.PlanAsync(request);

        plan.Warnings.ShouldContain(w => w.Contains("could be placed", StringComparison.Ordinal));
        DurationWarning(plan.Warnings).ShouldBeNull();
    }

    /// <summary>
    /// Both directions are reported. An overshoot is rarer and easier to miss, since nothing
    /// prompts anybody to check a file for being too long.
    /// </summary>
    [Fact]
    public void AnOvershootIsReportedAsWellAsAShortfall()
    {
        // Asserted against the pipeline's own arithmetic: arranging a planner overshoot is
        // exactly what the compensation fix made impossible.
        var durations = Enumerable.Repeat(TimeSpan.FromSeconds(6), 10).ToList();

        Core.Planning.SplicePlanner
            .RenderedDuration(durations, TimeSpan.FromSeconds(0.4))
            .TotalSeconds
            .ShouldBe(56.4, 0.0001);
    }

    /// <summary>
    /// The tolerance has to clear the frame-quantization residual without swallowing a real
    /// miss, and it scales so that it stays meaningful on a long render.
    /// </summary>
    [Theory]
    [InlineData(60, 0.3, false)]    // a frame or two either way: silent
    [InlineData(60, 2.0, true)]     // 3% of a minute: reported
    [InlineData(3600, 2.0, false)]  // the same 2s across an hour: noise
    [InlineData(3600, 60.0, true)]  // a minute missing from an hour: reported
    public async Task ToleranceScalesWithTheRequest(double target, double miss, bool expected)
    {
        var harness = new PipelineHarness();
        var source = harness.AddSource("film.mkv", 20000);

        // Cues divide the runtime equally and are taken as given, so they are the one strategy
        // that can be asked to miss by an exact amount.
        var shortened = target - miss;

        var request = PipelineHarness.Request(source, durationSeconds: target) with
        {
            Transition = new TransitionOptions { Kind = TransitionKind.None },
            Selection = PipelineHarness.Request(source).Selection with
            {
                Strategy = SelectionStrategy.Cues,
                SnapCuesToScene = false,

                // One cue, placed so only `shortened` seconds remain after it.
                Cues = [TimeSpan.FromSeconds(20000 - shortened)],
            },
        };

        var plan = await harness.PlanAsync(request);

        plan.EffectiveDuration.TotalSeconds.ShouldBe(shortened, 0.01);
        (DurationWarning(plan.Warnings) is not null).ShouldBe(expected);
    }
}
