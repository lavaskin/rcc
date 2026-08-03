using ReviewClips.Core.Options;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Tests.Selection;

/// <summary>
/// Behaviour every strategy must satisfy. These encode three bugs found during end-to-end
/// testing: clips clustering into a few seconds of runtime, clips overlapping each other, and
/// the second distribution pass ignoring spacing already established by the first.
/// </summary>
public class SegmentSelectorTests
{
    public static TheoryData<SelectionStrategy> AllGridStrategies =>
        new(SelectionStrategy.Uniform, SelectionStrategy.Random, SelectionStrategy.Scored);

    private static ISegmentSelector Selector(SelectionStrategy strategy) =>
        SegmentSelectorFactory.CreateDefault().Create(strategy);

    [Theory]
    [MemberData(nameof(AllGridStrategies))]
    public void Segments_NeverOverlap(SelectionStrategy strategy)
    {
        var info = SelectionTestData.Info(600);
        var analysis = SelectionTestData.Analysis(info);
        var context = SelectionTestData.Context([new SourceMedia(info, analysis)], segmentCount: 12);

        var segments = Selector(strategy).SelectSegments(context);

        segments.ShouldNotBeEmpty();

        var ordered = segments.OrderBy(s => s.Start).ToList();
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            ordered[i].End.ShouldBeLessThanOrEqualTo(
                ordered[i + 1].Start,
                $"segment {i} ({ordered[i]}) overlaps segment {i + 1} ({ordered[i + 1]})");
        }
    }

    [Theory]
    [MemberData(nameof(AllGridStrategies))]
    public void Segments_SpreadAcrossTheRuntimeRatherThanClustering(SelectionStrategy strategy)
    {
        var info = SelectionTestData.Info(600);
        var analysis = SelectionTestData.Analysis(info);
        var context = SelectionTestData.Context([new SourceMedia(info, analysis)], segmentCount: 10);

        var segments = Selector(strategy).SelectSegments(context);

        var span = segments.Max(s => s.Start) - segments.Min(s => s.Start);

        // The eligible window is ~522s (600 less 5% head and 8% tail). Ten clips that only
        // covered a small slice of that would indicate the clustering regression.
        span.TotalSeconds.ShouldBeGreaterThan(300);
    }

    [Theory]
    [MemberData(nameof(AllGridStrategies))]
    public void Segments_HonourMinGapWhenItIsAchievable(SelectionStrategy strategy)
    {
        var info = SelectionTestData.Info(1200);
        var analysis = SelectionTestData.Analysis(info);

        // 6 clips across ~1044s of eligible footage: a 30s gap is comfortably achievable.
        var context = SelectionTestData.Context(
            [new SourceMedia(info, analysis)],
            segmentCount: 6,
            options: new SelectionOptions
            {
                Strategy = strategy,
                MinGap = TimeSpan.FromSeconds(30),
            });

        var segments = Selector(strategy).SelectSegments(context);

        segments.Count.ShouldBe(6);
        SelectionTestData.MinimumGapSeconds(segments).ShouldBeGreaterThanOrEqualTo(30);
    }

    [Theory]
    [MemberData(nameof(AllGridStrategies))]
    public void Segments_FallBackToNonOverlapWhenMinGapIsImpossible(SelectionStrategy strategy)
    {
        var info = SelectionTestData.Info(120);
        var analysis = SelectionTestData.Analysis(info);

        // 20 clips of 5s cannot possibly be 60s apart inside ~104s of eligible footage.
        // The requested gap must degrade to non-overlap, never to duplication.
        var context = SelectionTestData.Context(
            [new SourceMedia(info, analysis)],
            segmentCount: 20,
            options: new SelectionOptions
            {
                Strategy = strategy,
                MinGap = TimeSpan.FromSeconds(60),
            });

        var segments = Selector(strategy).SelectSegments(context);

        segments.ShouldNotBeEmpty();
        SelectionTestData.MinimumGapSeconds(segments).ShouldBeGreaterThanOrEqualTo(5);
    }

    [Theory]
    [MemberData(nameof(AllGridStrategies))]
    public void Segments_StayInsideTheSourceRuntime(SelectionStrategy strategy)
    {
        var info = SelectionTestData.Info(90);
        var analysis = SelectionTestData.Analysis(info);
        var context = SelectionTestData.Context([new SourceMedia(info, analysis)], segmentCount: 8);

        foreach (var segment in Selector(strategy).SelectSegments(context))
        {
            segment.Start.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
            segment.End.ShouldBeLessThanOrEqualTo(info.Duration);
        }
    }

    [Theory]
    [MemberData(nameof(AllGridStrategies))]
    public void Segments_RespectSkipHeadAndSkipTail(SelectionStrategy strategy)
    {
        var info = SelectionTestData.Info(1000);
        var analysis = SelectionTestData.Analysis(info);
        var context = SelectionTestData.Context(
            [new SourceMedia(info, analysis)],
            segmentCount: 8,
            options: new SelectionOptions
            {
                Strategy = strategy,
                SkipHead = ReviewClips.Core.Primitives.Offset.FromPercent(10),
                SkipTail = ReviewClips.Core.Primitives.Offset.FromPercent(20),
            });

        foreach (var segment in Selector(strategy).SelectSegments(context))
        {
            segment.Start.TotalSeconds.ShouldBeGreaterThanOrEqualTo(100);
            segment.End.TotalSeconds.ShouldBeLessThanOrEqualTo(800.001);
        }
    }

    [Theory]
    [MemberData(nameof(AllGridStrategies))]
    public void Segments_AvoidBlackAndFrozenStretches(SelectionStrategy strategy)
    {
        var info = SelectionTestData.Info(600);
        var black = SelectionTestData.Range(100, 160);
        var frozen = SelectionTestData.Range(300, 380);

        var analysis = SelectionTestData.Analysis(info, black: [black], frozen: [frozen]);
        var context = SelectionTestData.Context(
            [new SourceMedia(info, analysis)],
            segmentCount: 8,
            options: new SelectionOptions { Strategy = strategy });

        foreach (var segment in Selector(strategy).SelectSegments(context))
        {
            segment.Range.Overlaps(black).ShouldBeFalse($"{segment} overlaps the black stretch");
            segment.Range.Overlaps(frozen).ShouldBeFalse($"{segment} overlaps the frozen stretch");
        }
    }

    [Theory]
    [MemberData(nameof(AllGridStrategies))]
    public void Segments_HonourExplicitIncludeRanges(SelectionStrategy strategy)
    {
        var info = SelectionTestData.Info(1000);
        var analysis = SelectionTestData.Analysis(info);
        var window = SelectionTestData.Range(400, 500);

        var context = SelectionTestData.Context(
            [new SourceMedia(info, analysis)],
            segmentCount: 5,
            options: new SelectionOptions
            {
                Strategy = strategy,
                IncludeRanges = [window],
            });

        var segments = Selector(strategy).SelectSegments(context);

        segments.ShouldNotBeEmpty();
        foreach (var segment in segments)
        {
            segment.Start.TotalSeconds.ShouldBeGreaterThanOrEqualTo(400);
            segment.End.TotalSeconds.ShouldBeLessThanOrEqualTo(500.001);
        }
    }

    [Theory]
    [MemberData(nameof(AllGridStrategies))]
    public void Segments_ExcludeRequestedRanges(SelectionStrategy strategy)
    {
        var info = SelectionTestData.Info(600);
        var analysis = SelectionTestData.Analysis(info);
        var excluded = SelectionTestData.Range(200, 400);

        var context = SelectionTestData.Context(
            [new SourceMedia(info, analysis)],
            segmentCount: 6,
            options: new SelectionOptions
            {
                Strategy = strategy,
                ExcludeRanges = [excluded],
            });

        foreach (var segment in Selector(strategy).SelectSegments(context))
        {
            segment.Range.Overlaps(excluded).ShouldBeFalse();
        }
    }

    [Theory]
    [MemberData(nameof(AllGridStrategies))]
    public void Selection_IsReproducibleForAGivenSeed(SelectionStrategy strategy)
    {
        var info = SelectionTestData.Info(600);
        var analysis = SelectionTestData.Analysis(info);

        var first = Selector(strategy).SelectSegments(
            SelectionTestData.Context([new SourceMedia(info, analysis)], 8, seed: 123));

        var second = Selector(strategy).SelectSegments(
            SelectionTestData.Context([new SourceMedia(info, analysis)], 8, seed: 123));

        first.Select(s => s.Start).ShouldBe(second.Select(s => s.Start));
    }

    [Fact]
    public void MultipleSources_AreAllDrawnFrom()
    {
        var a = SelectionTestData.Info(300, "/movies/a.mkv");
        var b = SelectionTestData.Info(300, "/movies/b.mkv");
        var c = SelectionTestData.Info(300, "/movies/c.mkv");

        var context = SelectionTestData.Context(
            [
                new SourceMedia(a, SelectionTestData.Analysis(a)),
                new SourceMedia(b, SelectionTestData.Analysis(b)),
                new SourceMedia(c, SelectionTestData.Analysis(c)),
            ],
            segmentCount: 12);

        var segments = Selector(SelectionStrategy.Scored).SelectSegments(context);

        segments.Select(s => s.SourcePath).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void MultipleSources_LongerSourcesContributeMoreClips()
    {
        var shortSource = SelectionTestData.Info(120, "/movies/trailer.mkv");
        var longSource = SelectionTestData.Info(1200, "/movies/feature.mkv");

        var context = SelectionTestData.Context(
            [
                new SourceMedia(shortSource, SelectionTestData.Analysis(shortSource)),
                new SourceMedia(longSource, SelectionTestData.Analysis(longSource)),
            ],
            segmentCount: 22);

        var segments = Selector(SelectionStrategy.Uniform).SelectSegments(context);

        var fromLong = segments.Count(s => s.SourcePath == longSource.Path);
        var fromShort = segments.Count(s => s.SourcePath == shortSource.Path);

        fromLong.ShouldBeGreaterThan(fromShort);
    }

    [Fact]
    public void EmptySources_YieldNoSegments() =>
        Selector(SelectionStrategy.Uniform)
            .SelectSegments(SelectionTestData.Context([], 5))
            .ShouldBeEmpty();

    [Fact]
    public void SourceShorterThanTheSpliceLength_YieldsNoSegments()
    {
        var info = SelectionTestData.Info(3);
        var context = SelectionTestData.Context(
            [new SourceMedia(info, SelectionTestData.Analysis(info))],
            segmentCount: 4,
            segmentSeconds: 5);

        Selector(SelectionStrategy.Uniform).SelectSegments(context).ShouldBeEmpty();
    }

    [Fact]
    public void ChronologicalOrder_ReturnsSegmentsInSourceOrder()
    {
        var info = SelectionTestData.Info(600);
        var context = SelectionTestData.Context(
            [new SourceMedia(info, SelectionTestData.Analysis(info))],
            segmentCount: 8,
            options: new SelectionOptions
            {
                Strategy = SelectionStrategy.Uniform,
                Order = SegmentOrder.Chronological,
            });

        var segments = Selector(SelectionStrategy.Uniform).SelectSegments(context);

        segments.Select(s => s.Start).ShouldBe(segments.Select(s => s.Start).OrderBy(s => s));
    }
}
