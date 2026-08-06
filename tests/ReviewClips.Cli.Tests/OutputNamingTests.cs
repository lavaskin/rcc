using ReviewClips.Cli.Cli;
using ReviewClips.Core.Options;

namespace ReviewClips.Cli.Tests;

/// <summary>
/// Where the output filename comes from, and when.
/// <para>
/// A derived name states the render's target duration, and <c>--match-audio</c> takes that
/// duration from the audio track during planning. A name chosen any earlier describes a render
/// that will not be produced, and two tracks of different lengths derive one name and write over
/// each other.
/// </para>
/// </summary>
public class OutputNamingTests
{
    [Fact]
    public void AnExplicitOutputIsUsedAsGiven()
    {
        using var harness = new RequestBuilderHarness();
        var target = harness.PathFor("chosen.mp4");

        var request = harness.Build("-d", "20s", "-o", target);

        request.OutputPath.ShouldBe(target);
    }

    /// <summary>
    /// The contract the rest of this rests on: nothing can commit to a name before the pipeline
    /// has settled the duration.
    /// </summary>
    [Fact]
    public void NoNameIsChosenDuringBuildWhenNoneWasGiven()
    {
        using var harness = new RequestBuilderHarness();

        var request = harness.Build("-d", "20s");

        request.OutputPath.ShouldBeEmpty();
    }

    [Fact]
    public void TheDerivedNameCarriesTheDurationItIsGiven()
    {
        using var harness = new RequestBuilderHarness();
        var request = harness.Build("-d", "20s");

        var named = ClipRequestBuilder.EnsureOutputPath(request, profileName: null);

        Path.GetFileName(named.OutputPath).ShouldContain("20s");
    }

    /// <summary>
    /// The pipeline hands EnsureOutputPath a request whose TargetDuration is already the track's
    /// length, so the name follows that rather than the 60s default the builder started from.
    /// </summary>
    [Fact]
    public void TheDerivedNameFollowsAMatchedAudioDuration()
    {
        using var harness = new RequestBuilderHarness();
        var request = harness.Build("--audio", harness.AudioPath, "--match-audio");

        // What RenderPipeline.PlanAsync does once it has probed the track.
        var settled = request with { TargetDuration = TimeSpan.FromSeconds(17) };
        var named = ClipRequestBuilder.EnsureOutputPath(settled, profileName: null);

        var name = Path.GetFileName(named.OutputPath);
        name.ShouldContain("17s");
        name.ShouldNotContain("1m");
    }

    [Fact]
    public void TracksOfDifferentLengthsDoNotCollideOnOneName()
    {
        using var harness = new RequestBuilderHarness();
        var request = harness.Build("--audio", harness.AudioPath, "--match-audio");

        string NameFor(int seconds) => Path.GetFileName(
            ClipRequestBuilder.EnsureOutputPath(
                request with { TargetDuration = TimeSpan.FromSeconds(seconds) },
                profileName: null).OutputPath);

        NameFor(17).ShouldNotBe(NameFor(33));
    }

    [Fact]
    public void EnsureOutputPathLeavesAnExplicitPathAlone()
    {
        using var harness = new RequestBuilderHarness();
        var target = harness.PathFor("chosen.mp4");
        var request = harness.Build("-d", "20s", "-o", target);

        // Idempotent, so a caller need not track whether it has already run.
        ClipRequestBuilder.EnsureOutputPath(request, null).OutputPath.ShouldBe(target);
        ClipRequestBuilder.EnsureOutputPath(
            ClipRequestBuilder.EnsureOutputPath(request, null), null).OutputPath.ShouldBe(target);
    }

    [Fact]
    public void TheDerivedNameIsAbsolute()
    {
        using var harness = new RequestBuilderHarness();
        var request = harness.Build("-d", "20s");

        var named = ClipRequestBuilder.EnsureOutputPath(request, profileName: null);

        Path.IsPathFullyQualified(named.OutputPath).ShouldBeTrue();
    }
}
