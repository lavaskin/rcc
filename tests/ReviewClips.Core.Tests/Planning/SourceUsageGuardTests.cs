using ReviewClips.Core.Planning;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Tests.Planning;

public class SourceUsageGuardTests
{
    private const string Film = "/movies/film.mkv";

    private static Segment Clip(double startSeconds, double seconds = 5, string source = Film) => new()
    {
        SourcePath = source,
        Start = TimeSpan.FromSeconds(startSeconds),
        Duration = TimeSpan.FromSeconds(seconds),
    };

    private static SourceUsageReport Evaluate(
        IEnumerable<Segment> segments,
        double sourceSeconds,
        double limit = SourceUsageGuard.DefaultLimit) =>
        SourceUsageGuard.Evaluate(segments, [TimeSpan.FromSeconds(sourceSeconds)], limit);

    // --- Measurement -------------------------------------------------------

    [Fact]
    public void MeasuresTheUnionOfReferencedRanges()
    {
        // 4 clips x 5s from a 200s film = 20s = 10%.
        var report = Evaluate([Clip(0), Clip(20), Clip(40), Clip(60)], 200);

        report.Used.ShouldBe(TimeSpan.FromSeconds(20));
        report.Available.ShouldBe(TimeSpan.FromSeconds(200));
        report.Fraction.ShouldBe(0.10d, 0.0001d);
    }

    [Fact]
    public void RepeatsDoNotDoubleCount()
    {
        // The distinction from pi's SourceUsageFraction, which multiplies splice count by
        // splice length. A pool of 2 clips filling 10 slots still touches only 10s of film.
        var pool = new[] { Clip(0), Clip(60) };
        var filled = Enumerable.Range(0, 5).SelectMany(_ => pool).ToList();

        filled.Count.ShouldBe(10);

        var report = Evaluate(filled, 200);

        report.Used.ShouldBe(TimeSpan.FromSeconds(10));
        report.Fraction.ShouldBe(0.05d, 0.0001d);

        // Counting slots instead would have reported 50 seconds, i.e. 25%, and a strict run
        // would have refused a render that touches a twentieth of the film.
        report.ExceedsLimit.ShouldBeFalse();
    }

    [Fact]
    public void OverlappingClipsAreCountedOnce()
    {
        // 0-5s and 3-8s overlap by 2s, so the union is 8s rather than 10s.
        var report = Evaluate([Clip(0), Clip(3)], 200);

        report.Used.ShouldBe(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void SumsAcrossSources()
    {
        var segments = new[] { Clip(0, 5, "/a.mkv"), Clip(0, 5, "/b.mkv") };

        var report = SourceUsageGuard.Evaluate(
            segments,
            [TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(100)],
            SourceUsageGuard.DefaultLimit);

        report.Used.ShouldBe(TimeSpan.FromSeconds(10));
        report.Available.ShouldBe(TimeSpan.FromSeconds(200));
        report.Fraction.ShouldBe(0.05d, 0.0001d);
    }

    [Fact]
    public void IgnoresSourcesReportingNoDuration()
    {
        var report = SourceUsageGuard.Evaluate(
            [Clip(0)],
            [TimeSpan.FromSeconds(100), TimeSpan.Zero, TimeSpan.FromSeconds(-5)],
            SourceUsageGuard.DefaultLimit);

        report.Available.ShouldBe(TimeSpan.FromSeconds(100));
    }

    [Fact]
    public void ReportsZeroRatherThanDividingByZero()
    {
        var report = SourceUsageGuard.Evaluate([Clip(0)], [], SourceUsageGuard.DefaultLimit);

        report.Fraction.ShouldBe(0d);
        report.ExceedsLimit.ShouldBeFalse();
    }

    [Fact]
    public void NeverReportsMoreThanAllOfIt()
    {
        // Clips can legitimately run past the reported duration on a mistagged container.
        var report = Evaluate([Clip(0, 500)], 100);

        report.Fraction.ShouldBe(1d);
    }

    // --- Threshold ---------------------------------------------------------

    [Fact]
    public void ExactlyAtTheLimitIsAllowed()
    {
        // 20s of 200s is precisely 10%. The boundary is inclusive: a guideline of "up to 10%"
        // that refuses 10% would be surprising.
        var report = Evaluate([Clip(0, 20)], 200);

        report.Fraction.ShouldBe(0.10d, 0.0001d);
        report.ExceedsLimit.ShouldBeFalse();
        SourceUsageGuard.Describe(report).ShouldBeNull();
    }

    [Fact]
    public void JustOverTheLimitIsFlagged()
    {
        var report = Evaluate([Clip(0, 21)], 200);

        report.ExceedsLimit.ShouldBeTrue();
        SourceUsageGuard.Describe(report).ShouldNotBeNull();
    }

    [Fact]
    public void ZeroLimitDisablesTheCheck()
    {
        var report = Evaluate([Clip(0, 199)], 200, limit: 0d);

        report.ExceedsLimit.ShouldBeFalse();
        SourceUsageGuard.Describe(report).ShouldBeNull();
    }

    [Fact]
    public void ARaisedLimitPermitsMore()
    {
        Evaluate([Clip(0, 40)], 200, limit: 0.10d).ExceedsLimit.ShouldBeTrue();
        Evaluate([Clip(0, 40)], 200, limit: 0.25d).ExceedsLimit.ShouldBeFalse();
    }

    [Fact]
    public void MessageNamesTheNumbersAndAWayOut()
    {
        var message = SourceUsageGuard.Describe(Evaluate([Clip(0, 60)], 200));

        message.ShouldNotBeNull();
        message.ShouldContain("30%");
        message.ShouldContain("60s of 200s");
        message.ShouldContain("10%");
        message.ShouldContain("--max-clips");
    }

    [Fact]
    public void NoSegmentsUsesNothing()
    {
        var report = Evaluate([], 200);

        report.Used.ShouldBe(TimeSpan.Zero);
        report.ExceedsLimit.ShouldBeFalse();
    }
}
