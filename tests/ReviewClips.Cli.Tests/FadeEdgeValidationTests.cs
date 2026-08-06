namespace ReviewClips.Cli.Tests;

/// <summary>
/// <c>--fade-edges</c> against the clip length.
/// <para>
/// The bound has to be half the *shortest* clip, not half <c>--splice</c>. <c>--splice</c> is a
/// midpoint that <c>--splice-jitter</c> varies around by up to a second by default, so checking
/// the nominal figure let through values that were fine for an average clip and consumed a short
/// one entirely — after which the stage silently corrected them and the printed summary went on
/// reporting the figure that had been asked for.
/// </para>
/// </summary>
public class FadeEdgeValidationTests
{
    [Fact]
    public void FadesLongerThanHalfTheJitteredMinimumAreRejected()
    {
        using var harness = new RequestBuilderHarness();

        // --splice 4s with the default 1s jitter means clips as short as 3s, so the ceiling is
        // 1.5s. 2s used to be accepted here and then clamped down inside the filter graph.
        var message = harness.Rejection("-d", "60s", "--splice", "4s", "--fade-edges", "2s");

        message.ShouldContain("--fade-edges");
        message.ShouldContain("shortest clip");
    }

    /// <summary>The message has to name the real constraint, or it cannot be acted on.</summary>
    [Fact]
    public void TheRejectionExplainsWhichSettingsSetTheBound()
    {
        using var harness = new RequestBuilderHarness();

        var message = harness.Rejection("-d", "60s", "--splice", "4s", "--fade-edges", "2s");

        message.ShouldContain("3s");
        message.ShouldContain("--splice-jitter");
    }

    /// <summary>
    /// With jitter off, every clip really is --splice long, so the old bound is the correct one
    /// and the same value is allowed. The check tightens for jitter, not across the board.
    /// </summary>
    [Fact]
    public void TheSameValueIsAllowedWhenJitterIsOff()
    {
        using var harness = new RequestBuilderHarness();

        var request = harness.Build(
            "-d", "60s", "--splice", "4s", "--splice-jitter", "0", "--fade-edges", "2s");

        request.Look.FadeEdges.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AFadeWithinTheJitteredMinimumIsAllowed()
    {
        using var harness = new RequestBuilderHarness();

        var request = harness.Build("-d", "60s", "--splice", "4s", "--fade-edges", "1.5s");

        request.Look.FadeEdges.ShouldBe(TimeSpan.FromSeconds(1.5));
    }

    /// <summary>
    /// Larger jitter tightens the bound further, which is the property the old check lacked
    /// entirely: it gave the same answer whatever the jitter was.
    /// </summary>
    [Fact]
    public void MoreJitterTightensTheBound()
    {
        using var harness = new RequestBuilderHarness();

        // 6s splice, 3s jitter -> clips as short as 3s -> ceiling 1.5s.
        harness.Rejection("-d", "60s", "--splice", "6s", "--splice-jitter", "3s", "--fade-edges", "2s")
            .ShouldContain("--fade-edges");

        // Same splice, no jitter -> ceiling 3s, so 2s is fine.
        harness.Build("-d", "60s", "--splice", "6s", "--splice-jitter", "0", "--fade-edges", "2s")
            .Look.FadeEdges.ShouldBe(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// A negative fade never reaches validation: the duration parser refuses it first. Asserted
    /// so the guard in Validate is not mistaken for the thing doing this work — it is a backstop
    /// for the profile path, which does not go through the parser.
    /// </summary>
    [Fact]
    public void ANegativeFadeIsRefusedByTheParser()
    {
        using var harness = new RequestBuilderHarness();

        Should.Throw<ParseFailedException>(
            () => harness.Build("-d", "60s", "--fade-edges", "-1s"));
    }

    /// <summary>Jitter never drives the floor below the planner's own minimum segment length.</summary>
    [Fact]
    public void JitterLargerThanTheSpliceDoesNotProduceANegativeBound()
    {
        using var harness = new RequestBuilderHarness();

        // SplicePlanner clamps jitter so a clip can never fall under 0.75s; the bound has to
        // agree, rather than computing 2 - 10 = -8s and rejecting every value including zero.
        var message = harness.Rejection(
            "-d", "60s", "--splice", "2s", "--splice-jitter", "10s", "--fade-edges", "1s");

        // The floor, and half of it, both stated as positive figures.
        message.ShouldContain("0.75s");
        message.ShouldContain("0.38s");
    }
}
