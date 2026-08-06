using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Tests.Primitives;

public class TimeRangeSetTests
{
    private static TimeRange R(double start, double end) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));

    [Fact]
    public void From_MergesOverlappingAndAdjacentRanges()
    {
        var set = TimeRangeSet.From([R(0, 10), R(5, 15), R(15, 20), R(30, 40)]);

        set.Count.ShouldBe(2);
        set[0].ShouldBe(R(0, 20));
        set[1].ShouldBe(R(30, 40));
    }

    [Fact]
    public void From_SortsOutOfOrderInput()
    {
        var set = TimeRangeSet.From([R(50, 60), R(0, 10)]);

        set[0].Start.ShouldBe(TimeSpan.Zero);
        set[1].Start.ShouldBe(TimeSpan.FromSeconds(50));
    }

    [Fact]
    public void Subtract_SplitsARangeWhenTheHoleIsInTheMiddle()
    {
        var set = TimeRangeSet.Of(R(0, 100)).Subtract([R(40, 60)]);

        set.Count.ShouldBe(2);
        set[0].ShouldBe(R(0, 40));
        set[1].ShouldBe(R(60, 100));
    }

    [Fact]
    public void Subtract_RemovesTheRangeEntirelyWhenFullyCovered() =>
        TimeRangeSet.Of(R(10, 20)).Subtract([R(0, 100)]).Count.ShouldBe(0);

    [Fact]
    public void TotalDuration_SumsAllRanges()
    {
        TimeRangeSet.Of(R(0, 10), R(20, 35))
            .TotalDuration
            .ShouldBe(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public void CanFit_RequiresTheWholeWindowInsideOneRange()
    {
        var set = TimeRangeSet.Of(R(0, 10), R(12, 30));

        set.CanFit(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10)).ShouldBeTrue();

        // Would need to span the gap between the two ranges.
        set.CanFit(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(6)).ShouldBeFalse();
    }

    [Fact]
    public void WhereLongerThan_DropsShortRanges()
    {
        var set = TimeRangeSet.Of(R(0, 2), R(10, 30)).WhereLongerThan(TimeSpan.FromSeconds(5));

        set.Count.ShouldBe(1);
        set[0].ShouldBe(R(10, 30));
    }

    [Fact]
    public void Project_SkipsExcludedGaps()
    {
        // Two eligible ranges of 10s each, separated by a 40s hole.
        var set = TimeRangeSet.Of(R(0, 10), R(50, 60));

        set.Project(TimeSpan.FromSeconds(5)).ShouldBe(TimeSpan.FromSeconds(5));

        // The start of the second range sits at 10s of *eligible* time, not 50s of wall clock.
        set.Project(TimeSpan.FromSeconds(50)).ShouldBe(TimeSpan.FromSeconds(10));
        set.Project(TimeSpan.FromSeconds(55)).ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Project_ReturnsNullOutsideEligibleRanges()
    {
        var set = TimeRangeSet.Of(R(0, 10), R(50, 60));

        set.Project(TimeSpan.FromSeconds(25)).ShouldBeNull();
        set.Project(TimeSpan.FromSeconds(100)).ShouldBeNull();
    }

    [Fact]
    public void Pad_ClampsAtZeroSoAnEarlyRangeCannotGoNegative()
    {
        var padded = R(1, 5).Pad(TimeSpan.FromSeconds(10));

        padded.Start.ShouldBe(TimeSpan.Zero);
        padded.End.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Constructor_RejectsInvertedRanges() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new TimeRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
}
