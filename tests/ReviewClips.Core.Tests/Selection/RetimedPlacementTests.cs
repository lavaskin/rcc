using ReviewClips.Core.Options;
using ReviewClips.Core.Planning;
using ReviewClips.Core.Primitives;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Tests.Selection;

/// <summary>
/// Placement under <c>--speed</c>.
/// <para>
/// A clip is two lengths at once when it is retimed: at <c>--speed 2</c> a five second clip
/// occupies five seconds of the render and ten seconds of the film. Selection reserved the
/// output length and the extractor read the source length, and nothing reconciled them — so a
/// clip near the end of a file was silently truncated, clips read past the ranges that were
/// supposed to exclude them, two clips could read the same footage while satisfying
/// <c>--min-gap</c>, and the usage guardrail under-reported by the speed factor.
/// </para>
/// <para>
/// Every assertion here is about the source side, because that is the side all four of those
/// were wrong on.
/// </para>
/// </summary>
public class RetimedPlacementTests
{
    private static readonly SelectionStrategy[] Strategies =
    [
        SelectionStrategy.Uniform,
        SelectionStrategy.Random,
        SelectionStrategy.Scene,
        SelectionStrategy.Scored,
    ];

    private static IReadOnlyList<Segment> Select(
        SelectionStrategy strategy,
        SelectionOptions options,
        double sourceSeconds = 600,
        double segmentSeconds = 4,
        int segmentCount = 6,
        double speed = 2d)
    {
        var info = SelectionTestData.Info(sourceSeconds);
        var analysis = SelectionTestData.Analysis(info, cutsAtSeconds: Cuts(sourceSeconds));

        var context = SelectionTestData.Context(
            [new SourceMedia(info, analysis)],
            segmentCount,
            segmentSeconds,
            options with { Strategy = strategy },
            speed: speed);

        return SegmentSelectorFactory.CreateDefault().Create(strategy).SelectSegments(context);
    }

    private static IEnumerable<double> Cuts(double sourceSeconds)
    {
        for (var t = 5d; t < sourceSeconds; t += 11d)
        {
            yield return t;
        }
    }

    private static SelectionOptions Options() => new()
    {
        Seed = 7,
        MinGap = TimeSpan.Zero,
        SkipHead = Offset.Zero,
        SkipTail = Offset.Zero,
        ChapterSkip = ChapterSkipMode.Off,
    };

    /// <summary>
    /// The overrun that started this. Placement used to reserve the output length, so a clip
    /// could begin close enough to the end of the file that its read window ran past it — and the
    /// extractor then produced a short clip with no warning.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryStrategy))]
    public void AClipNeverReadsPastTheEndOfItsSource(SelectionStrategy strategy)
    {
        var segments = Select(strategy, Options(), sourceSeconds: 200, segmentCount: 20);

        segments.ShouldNotBeEmpty();

        foreach (var segment in segments)
        {
            segment.End.ShouldBeLessThanOrEqualTo(
                TimeSpan.FromSeconds(200),
                $"{segment} reads to {segment.End.TotalSeconds:0.##}s");
        }
    }

    /// <summary>An excluded range is excluded from the read, not merely from the playback.</summary>
    [Theory]
    [MemberData(nameof(EveryStrategy))]
    public void AClipNeverReadsIntoAnExcludedRange(SelectionStrategy strategy)
    {
        var excluded = SelectionTestData.Range(200, 400);

        var segments = Select(
            strategy,
            Options() with { ExcludeRanges = [excluded] },
            segmentCount: 12);

        segments.ShouldNotBeEmpty();
        segments.ShouldAllBe(s => !s.Range.Overlaps(excluded));
    }

    /// <summary><c>--range</c> bounds the footage read, so the read window has to fit inside it.</summary>
    [Theory]
    [MemberData(nameof(EveryStrategy))]
    public void AClipNeverReadsPastTheEndOfAnIncludedRange(SelectionStrategy strategy)
    {
        var window = SelectionTestData.Range(100, 140);

        var segments = Select(strategy, Options() with { IncludeRanges = [window] }, segmentCount: 6);

        segments.ShouldNotBeEmpty();

        foreach (var segment in segments)
        {
            segment.Start.ShouldBeGreaterThanOrEqualTo(window.Start);
            segment.End.ShouldBeLessThanOrEqualTo(
                window.End,
                $"{segment} reads to {segment.End.TotalSeconds:0.##}s, past the range end");
        }
    }

    /// <summary>
    /// The non-overlap floor is documented as never relaxed, and it was — quietly, whenever the
    /// speed was above one. Two clips four seconds apart at <c>--speed 2</c> each read eight
    /// seconds, so half of one is literally the same footage as the other.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryStrategy))]
    public void TwoClipsNeverReadTheSameFootage(SelectionStrategy strategy)
    {
        var segments = Select(strategy, Options(), segmentCount: 20);

        segments.ShouldNotBeEmpty();

        var ordered = segments.OrderBy(s => s.Start).ToList();

        for (var i = 0; i < ordered.Count - 1; i++)
        {
            ordered[i].End.ShouldBeLessThanOrEqualTo(
                ordered[i + 1].Start,
                $"{ordered[i]} and {ordered[i + 1]} read overlapping footage");
        }
    }

    /// <summary>
    /// The guardrail exists to answer how much of a work was used, and at <c>--speed 2</c> it was
    /// answering with half. Its numerator comes from the segment ranges, so the fix is the same
    /// one: a range is a stretch of source.
    /// </summary>
    [Fact]
    public void TheUsageGuardMeasuresTheFootageActuallyRead()
    {
        var segments = Select(SelectionStrategy.Uniform, Options(), segmentCount: 10, speed: 2d);

        var report = SourceUsageGuard.Evaluate(
            segments,
            [("/movies/film.mkv", TimeSpan.FromSeconds(600))],
            limit: 0d);

        var outputSeconds = segments.Sum(s => s.Duration.TotalSeconds);

        report.Used.TotalSeconds.ShouldBe(outputSeconds * 2d, 0.001);
    }

    /// <summary>
    /// A cue close to the end of its source is capped at what remains — measured in source
    /// footage, so a retimed cue is capped at half of it.
    /// </summary>
    [Fact]
    public void ACueIsCappedByTheSourceItCanActuallyRead()
    {
        var info = SelectionTestData.Info(300);

        var context = SelectionTestData.Context(
            [new SourceMedia(info, null)],
            segmentCount: 1,
            segmentSeconds: 100,
            options: Options() with
            {
                Strategy = SelectionStrategy.Cues,
                SnapCuesToScene = false,
                Cues = [TimeSpan.FromSeconds(240)],
            },
            speed: 2d);

        var segments = new CueDrivenSegmentSelector().SelectSegments(context);

        // 60s of source remain after the cue, which at double speed is 30s of output.
        segments.Single().Duration.TotalSeconds.ShouldBe(30d, 0.001);
        segments.Single().End.ShouldBe(TimeSpan.FromSeconds(300));
    }

    /// <summary>Normal speed is untouched: the read window is the output length.</summary>
    [Theory]
    [MemberData(nameof(EveryStrategy))]
    public void AtNormalSpeedTheReadWindowIsTheOutputLength(SelectionStrategy strategy)
    {
        var segments = Select(strategy, Options(), speed: 1d);

        segments.ShouldNotBeEmpty();
        segments.ShouldAllBe(s => s.ReadDuration == s.Duration);
    }

    public static TheoryData<SelectionStrategy> EveryStrategy => [.. Strategies];
}
