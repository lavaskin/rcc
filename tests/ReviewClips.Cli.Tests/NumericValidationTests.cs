using ReviewClips.Cli.Profiles;

namespace ReviewClips.Cli.Tests;

/// <summary>
/// The numeric bounds, and the two ways a value can reach them other than by being typed as a
/// plain number: as NaN or infinity, and as a setting layered in from a profile.
/// </summary>
public class NumericValidationTests
{
    /// <summary>
    /// Every option that takes a double, with a value that is out of range in the ordinary way.
    /// </summary>
    [Theory]
    [InlineData("--fps", "0")]
    [InlineData("--fps", "500")]
    [InlineData("--gamma", "0")]
    [InlineData("--gamma", "50")]
    [InlineData("--sharpen", "-1")]
    [InlineData("--sharpen", "9")]
    [InlineData("--darken", "-0.5")]
    [InlineData("--darken", "2")]
    [InlineData("--saturation", "-1")]
    [InlineData("--contrast", "-1")]
    [InlineData("--blur", "-1")]
    [InlineData("--overlay-opacity", "9")]
    [InlineData("--grain", "5000")]
    [InlineData("--zoom", "-3")]
    [InlineData("--zoom", "0")]
    [InlineData("--speed", "0")]
    [InlineData("--speed", "-2")]
    [InlineData("--max-source-percent", "-5")]
    [InlineData("--max-source-percent", "150")]
    [InlineData("--quality", "99")]
    public void AnOutOfRangeValueIsRejected(string option, string value)
    {
        using var harness = new RequestBuilderHarness();

        harness.Rejection("-d", "20s", option, value)
            .ShouldContain(option);
    }

    /// <summary>
    /// The same options, given NaN or infinity.
    /// <para>
    /// Reachable from the command line: <c>System.CommandLine</c>'s double binder parses both.
    /// They need their own coverage because every comparison against NaN is false, so a bound
    /// expressed as <c>is &lt; 0d or &gt; 1d</c> admits NaN rather than rejecting it. A NaN that
    /// gets through reaches FFmpeg as the literal <c>NaN</c>, and as <c>--max-source-percent</c>
    /// it disables the guardrail outright, since <c>Limit &gt; 0d</c> is false for it too.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("--fps")]
    [InlineData("--gamma")]
    [InlineData("--sharpen")]
    [InlineData("--darken")]
    [InlineData("--saturation")]
    [InlineData("--contrast")]
    [InlineData("--blur")]
    [InlineData("--overlay-opacity")]
    [InlineData("--zoom")]
    [InlineData("--speed")]
    [InlineData("--max-source-percent")]
    [InlineData("--pixelate")]
    public void ANonFiniteValueIsRejected(string option)
    {
        using var harness = new RequestBuilderHarness();

        foreach (var value in new[] { "nan", "infinity", "-infinity", "1e400" })
        {
            harness.Rejection("-d", "20s", option, value)
                .ShouldContain(option, customMessage: $"{option} accepted '{value}'");
        }
    }

    /// <summary>
    /// The specific case that made NaN worse than a crash: a guardrail turned off by a value the
    /// tool accepted without comment.
    /// </summary>
    [Fact]
    public void NaNCannotBeUsedToDisableTheSourceUsageGuard()
    {
        using var harness = new RequestBuilderHarness();

        harness.Rejection("-d", "20s", "--max-source-percent", "nan")
            .ShouldContain("--max-source-percent");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.05")]
    [InlineData("20")]
    [InlineData("-2")]
    public void PixelateOutsideItsDocumentedRangeIsRejected(string value)
    {
        using var harness = new RequestBuilderHarness();

        // The help text says 1.1-16 and the stage clamps to 1.1, so validation has to agree with
        // both: a value it accepts must be one the stage will use unchanged.
        harness.Rejection("-d", "20s", "--pixelate", value)
            .ShouldContain("--pixelate");
    }

    [Fact]
    public void PixelateZeroMeansOffAndIsAllowed()
    {
        using var harness = new RequestBuilderHarness();

        var request = harness.Build("-d", "20s", "--pixelate", "0");

        request.Look.Pixelate.ShouldBe(0d);
        request.Look.HasPixelate.ShouldBeFalse();
    }

    [Fact]
    public void ValuesInsideTheBoundsSurviveUntouched()
    {
        using var harness = new RequestBuilderHarness();

        var request = harness.Build(
            "-d", "20s",
            "--fps", "24",
            "--gamma", "1.2",
            "--sharpen", "1.5",
            "--pixelate", "4",
            "--zoom", "1.1",
            "--max-source-percent", "25");

        request.Format.FrameRate.ShouldBe(24d);
        request.Look.Gamma.ShouldBe(1.2d);
        request.Look.Sharpen.ShouldBe(1.5d);
        request.Look.Pixelate.ShouldBe(4d);
        request.Look.ZoomEnd.ShouldBe(1.1d);
        request.MaxSourceFraction.ShouldBe(0.25d);
    }

    // --- Profile-supplied values -------------------------------------------

    /// <summary>
    /// A profile is a config file, not a trusted source. These values never pass through the CLI
    /// parsers, so before validation moved onto the resolved request they reached FFmpeg unchecked
    /// and failed there — as a tool error, several hundred encoded frames later.
    /// </summary>
    [Fact]
    public void ANegativeResolutionFromAProfileIsRejected()
    {
        using var harness = new RequestBuilderHarness(new RenderProfile
        {
            Name = "broken",
            Width = -100,
            Height = -50,
        });

        harness.Rejection("-d", "20s", "-p", "broken")
            .ShouldContain("resolution");
    }

    [Fact]
    public void AnOutOfRangeLookValueFromAProfileIsRejected()
    {
        using var harness = new RequestBuilderHarness(new RenderProfile
        {
            Name = "broken",
            Gamma = 50,
        });

        harness.Rejection("-d", "20s", "-p", "broken")
            .ShouldContain("--gamma");
    }

    [Fact]
    public void ANegativeSpliceJitterFromAProfileIsRejected()
    {
        using var harness = new RequestBuilderHarness(new RenderProfile
        {
            Name = "broken",
            SpliceJitterSeconds = -5,
        });

        harness.Rejection("-d", "20s", "-p", "broken")
            .ShouldContain("--splice-jitter");
    }

    /// <summary>
    /// Naming only the flag sends the user to look for something they never typed.
    /// </summary>
    [Fact]
    public void ARejectionCausedByAProfileNamesTheProfile()
    {
        using var harness = new RequestBuilderHarness(new RenderProfile
        {
            Name = "broken",
            Gamma = 50,
        });

        harness.Rejection("-d", "20s", "-p", "broken")
            .ShouldContain("broken");
    }

    /// <summary>And the hint is not bolted onto errors that had nothing to do with a profile.</summary>
    [Fact]
    public void ARejectionWithNoProfileInPlayMentionsNoProfile()
    {
        using var harness = new RequestBuilderHarness();

        harness.Rejection("-d", "20s", "--gamma", "50")
            .ShouldNotContain("profile");
    }
}
