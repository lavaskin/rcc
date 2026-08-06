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
        SourceUsageGuard.Evaluate(segments, [(Film, TimeSpan.FromSeconds(sourceSeconds))], limit);

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

        // Counting slots would have reported 50s (25%), refusing a render that touches a
        // twentieth of the film.
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
            [("/a.mkv", TimeSpan.FromSeconds(100)), ("/b.mkv", TimeSpan.FromSeconds(100))],
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
            [
                (Film, TimeSpan.FromSeconds(100)),
                ("/empty.mkv", TimeSpan.Zero),
                ("/negative.mkv", TimeSpan.FromSeconds(-5)),
            ],
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

    // --- Per-source measurement --------------------------------------------

    [Fact]
    public void AnUnusedSourceDoesNotDiluteTheFigureTheLimitTests()
    {
        // The loophole an aggregate measurement leaves open: 80% of one film hidden behind nine
        // untouched hours.
        var segments = new[] { Clip(0, 80, "/a.mkv") };

        var report = SourceUsageGuard.Evaluate(
            segments,
            [("/a.mkv", TimeSpan.FromSeconds(100)), ("/unused.mkv", TimeSpan.FromSeconds(9900))],
            SourceUsageGuard.DefaultLimit);

        // Diluted to under one per cent, which is why the aggregate is not what the limit tests.
        report.Fraction.ShouldBeLessThan(0.01d);

        report.PeakFraction.ShouldBe(0.80d, 0.0001d);
        report.ExceedsLimit.ShouldBeTrue();
    }

    [Fact]
    public void PeakIsTheHeaviestSourceNotTheFirst()
    {
        var segments = new[] { Clip(0, 5, "/light.mkv"), Clip(0, 90, "/heavy.mkv") };

        var report = SourceUsageGuard.Evaluate(
            segments,
            [("/light.mkv", TimeSpan.FromSeconds(100)), ("/heavy.mkv", TimeSpan.FromSeconds(100))],
            SourceUsageGuard.DefaultLimit);

        report.Peak.ShouldNotBeNull();
        report.Peak.Path.ShouldBe("/heavy.mkv");
        report.Peak.Fraction.ShouldBe(0.90d, 0.0001d);
    }

    [Fact]
    public void EverySourceGetsAnEntryIncludingUntouchedOnes()
    {
        var report = SourceUsageGuard.Evaluate(
            [Clip(0, 5, "/a.mkv")],
            [("/a.mkv", TimeSpan.FromSeconds(100)), ("/b.mkv", TimeSpan.FromSeconds(100))],
            SourceUsageGuard.DefaultLimit);

        report.Sources.Count.ShouldBe(2);

        var untouched = report.Sources.Single(s => s.Path == "/b.mkv");
        untouched.Used.ShouldBe(TimeSpan.Zero);
        untouched.Available.ShouldBe(TimeSpan.FromSeconds(100));
        untouched.Fraction.ShouldBe(0d);
    }

    [Fact]
    public void MessageNamesTheHeaviestSourceWhenThereAreSeveral()
    {
        var report = SourceUsageGuard.Evaluate(
            [Clip(0, 5, "/movies/light.mkv"), Clip(0, 90, "/movies/heavy.mkv")],
            [
                ("/movies/light.mkv", TimeSpan.FromSeconds(100)),
                ("/movies/heavy.mkv", TimeSpan.FromSeconds(100)),
            ],
            SourceUsageGuard.DefaultLimit);

        var message = SourceUsageGuard.Describe(report);

        message.ShouldNotBeNull();
        message.ShouldContain("heavy.mkv");
        message.ShouldContain("90%");
    }

    [Fact]
    public void ASegmentFromAnUndeclaredSourceStillCounts()
    {
        // Cannot happen through the pipeline, but silently discarding used footage is the one
        // failure mode this measurement must not have.
        var report = SourceUsageGuard.Evaluate(
            [Clip(0, 5, "/ghost.mkv")],
            [],
            SourceUsageGuard.DefaultLimit);

        report.Used.ShouldBe(TimeSpan.FromSeconds(5));
        report.Sources.ShouldHaveSingleItem();
    }

    [Fact]
    public void ADuplicatedSourceIsCountedOnce()
    {
        var report = SourceUsageGuard.Evaluate(
            [Clip(0, 5)],
            [(Film, TimeSpan.FromSeconds(100)), (Film, TimeSpan.FromSeconds(100))],
            SourceUsageGuard.DefaultLimit);

        report.Sources.ShouldHaveSingleItem();
        report.Available.ShouldBe(TimeSpan.FromSeconds(100));
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

    /// <summary>
    /// The union arithmetic, which also lived in <c>ClipSequencer.DistinctSourceDuration</c>.
    /// Both fed the same summary, so this is now the only implementation.
    /// </summary>
    [Fact]
    public void RepeatedFootageIsCountedOnce()
    {
        var pool = Enumerable.Range(0, 10).Select(i => Clip(100 + (i * 60))).ToList();
        var repeated = pool.Concat(pool).Concat(pool);

        // 10 clips of 5s regardless of how many times they appear.
        Evaluate(repeated, 2000).Used.TotalSeconds.ShouldBe(50d, 0.0001);
    }

    [Fact]
    public void OverlappingClipsAreMerged()
    {
        var a = Clip(100, 10);
        var overlapping = a with { Start = TimeSpan.FromSeconds(105) };

        // 100-110 and 105-115 cover 15s of footage, not 20s.
        Evaluate([a, overlapping], 2000).Used.TotalSeconds.ShouldBe(15d, 0.0001);
    }

    [Fact]
    public void IdenticalTimestampsInDifferentFilesAreDifferentFootage()
    {
        var a = Clip(100, 5, "/movies/a.mkv");
        var b = Clip(100, 5, "/movies/b.mkv");

        SourceUsageGuard.Evaluate(
                [a, b],
                [("/movies/a.mkv", TimeSpan.FromSeconds(200)), ("/movies/b.mkv", TimeSpan.FromSeconds(200))],
                limit: 0d)
            .Used.TotalSeconds
            .ShouldBe(10d, 0.0001);
    }

    /// <summary>A retimed clip consumes more source than it plays back as.</summary>
    [Fact]
    public void ARetimedClipIsMeasuredByTheFootageItReads()
    {
        var doubled = Clip(100, 5) with { SpeedFactor = 2d };

        Evaluate([doubled], 200).Used.TotalSeconds.ShouldBe(10d, 0.0001);
    }
}
