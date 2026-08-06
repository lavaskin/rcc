using ReviewClips.Cli.Cli;
using ReviewClips.Core.Options;

namespace ReviewClips.Cli.Tests;

/// <summary>
/// Where the output filename comes from, and when.
/// <para>
/// The timing is the whole subject. The derived name encodes the render's target duration, and
/// <c>--match-audio</c> replaces that duration during planning — so a name chosen at build time
/// carried the placeholder, and two renders matched to tracks of different lengths agreed on a
/// filename and silently overwrote each other.
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
    /// The contract that makes the fix work: no name is chosen during Build, so nothing can
    /// commit to one before the pipeline has settled the duration.
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
    /// The regression test for the naming bug. The pipeline hands EnsureOutputPath a request whose
    /// TargetDuration has already been replaced by the track's length, so the name has to follow
    /// that rather than the 60s default the builder started from.
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

    /// <summary>
    /// The consequence that actually cost data: same source, same settings, two different beds,
    /// one filename. The second render overwrote the first.
    /// </summary>
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
