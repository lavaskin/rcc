using ReviewClips.Cli.Profiles;

namespace ReviewClips.Cli.Tests;

/// <summary>
/// <c>--grayscale</c> against <c>--saturation</c> and <c>--no-grayscale</c>.
/// <para>
/// Two settings for one thing, so the request that reaches the renderer must hold only one of
/// them: if the contradiction survived, the printed summary and the filter graph would be free
/// to settle it differently from each other.
/// </para>
/// </summary>
public class GrayscaleResolutionTests
{
    [Fact]
    public void GrayscaleAloneDropsAllColour()
    {
        using var harness = new RequestBuilderHarness();

        var look = harness.Build("--grayscale").Look;

        look.Grayscale.ShouldBeTrue();
        look.EffectiveSaturation.ShouldBe(0d);
    }

    /// <summary>An explicit amount of colour is the more recent and more specific instruction.</summary>
    [Fact]
    public void AnExplicitSaturationBeatsGrayscale()
    {
        using var harness = new RequestBuilderHarness();

        var look = harness.Build("--grayscale", "--saturation", "0.5").Look;

        look.Grayscale.ShouldBeFalse();
        look.EffectiveSaturation.ShouldBe(0.5d);
    }

    /// <summary>And it beats a profile that drops colour, the same way.</summary>
    [Fact]
    public void AnExplicitSaturationBeatsAProfileThatDropsColour()
    {
        using var harness = new RequestBuilderHarness(
            new RenderProfile { Name = "mono", Grayscale = true });

        harness.Build("--profile", "mono").Look.Grayscale.ShouldBeTrue();

        var look = harness.Build("--profile", "mono", "--saturation", "0.9").Look;

        look.Grayscale.ShouldBeFalse();
        look.EffectiveSaturation.ShouldBe(0.9d);
    }

    [Fact]
    public void NoGrayscaleOverridesAProfile()
    {
        using var harness = new RequestBuilderHarness(
            new RenderProfile { Name = "mono", Grayscale = true });

        harness.Build("--profile", "mono", "--no-grayscale").Look.Grayscale.ShouldBeFalse();
    }

    /// <summary>
    /// Both forms at once is refused, as for every other <c>--no-</c> pair, and stays refused
    /// when <c>--saturation</c> settles the value anyway: this pair being the one exception would
    /// be arbitrary.
    /// </summary>
    [Theory]
    [InlineData()]
    [InlineData("--saturation", "0.5")]
    public void BothFormsTogetherAreRefused(params string[] extra)
    {
        using var harness = new RequestBuilderHarness();

        harness.Rejection([.. new[] { "--grayscale", "--no-grayscale" }, .. extra])
            .ShouldContain("grayscale");
    }
}
