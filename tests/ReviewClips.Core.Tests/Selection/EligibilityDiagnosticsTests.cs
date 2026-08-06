using ReviewClips.Core.Analysis;
using ReviewClips.Core.Media;
using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Tests.Selection;

/// <summary>
/// The "no usable segments" message used to name every filter that happened to be switched on,
/// several of which are on by default. These tests hold the explanation to the filter that is
/// actually responsible.
/// </summary>
public class EligibilityDiagnosticsTests
{
    private const double SourceSeconds = 1000d;

    private static SelectionContext Context(
        SelectionOptions options,
        double windowSeconds = 5d,
        IReadOnlyList<TimeRange>? black = null,
        IReadOnlyList<TimeRange>? frozen = null,
        IReadOnlyList<Chapter>? chapters = null)
    {
        var info = new MediaInfo
        {
            Path = "/movies/film.mkv",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTimeOffset.UnixEpoch,
            Duration = TimeSpan.FromSeconds(SourceSeconds),
            Width = 1920,
            Height = 1080,
            FrameRate = 24d,
            VideoCodec = "h264",
            PixelFormat = "yuv420p",
            SampleAspectRatio = Ratio.One,
            HasAudio = true,
            Chapters = chapters ?? [],
        };

        var analysis = new MediaAnalysis
        {
            SourcePath = info.Path,
            SourceSizeBytes = info.FileSizeBytes,
            SourceModifiedUtc = info.LastModifiedUtc,
            Duration = info.Duration,
            AnalysedAtUtc = DateTimeOffset.UnixEpoch,
            Settings = new AnalysisSettings(),
            SceneCuts = [],
            BlackRanges = black ?? [],
            FreezeRanges = frozen ?? [],
            Motion = MotionCurve.Empty,
        };

        return new SelectionContext
        {
            Sources = [new SourceMedia(info, analysis)],
            Options = options,
            SegmentDurations = [TimeSpan.FromSeconds(windowSeconds)],
            Random = new Random(1),
        };
    }

    private static SelectionOptions Permissive() => new()
    {
        SkipHead = Offset.Zero,
        SkipTail = Offset.Zero,
        ChapterSkip = ChapterSkipMode.Off,
        RejectBlack = false,
        RejectFrozen = false,
    };

    private static TimeRange Range(double from, double to) =>
        new(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to));

    [Fact]
    public void NothingIsSaidWhenFootageIsActuallyEligible() =>
        EligibilityDiagnostics.Explain(Context(Permissive())).ShouldBeNull();

    [Fact]
    public void ARangeTooShortForTheClipIsNamed()
    {
        var options = Permissive() with { IncludeRanges = [Range(100, 102)] };

        EligibilityDiagnostics.Explain(Context(options)).ShouldNotBeNull().ShouldContain("--range");
    }

    [Fact]
    public void AnExcludeThatSwallowsTheSourceIsNamed()
    {
        var options = Permissive() with { ExcludeRanges = [Range(0, SourceSeconds)] };

        EligibilityDiagnostics.Explain(Context(options)).ShouldNotBeNull().ShouldContain("--exclude");
    }

    [Fact]
    public void ChaptersSkippedByTitleAreNamed()
    {
        var options = Permissive() with { SkipChapterPatterns = ["*"] };

        var context = Context(
            options,
            chapters:
            [
                new Chapter { Index = 1, Range = Range(0, SourceSeconds), Title = "Opening Titles" },
            ]);

        EligibilityDiagnostics.Explain(context).ShouldNotBeNull().ShouldContain("--chapters off");
    }

    /// <summary>
    /// A render refused by a two-second <c>--range</c> was advised to try
    /// <c>--no-reject-black</c>: wrong, and expensive to act on. Detection that is on by default
    /// must not be blamed for a restriction the user typed.
    /// </summary>
    [Fact]
    public void DefaultOnDetectorsAreNotBlamedForAUserSuppliedRestriction()
    {
        var options = new SelectionOptions
        {
            SkipHead = Offset.Zero,
            SkipTail = Offset.Zero,
            ChapterSkip = ChapterSkipMode.Off,
            IncludeRanges = [Range(100, 102)],
        };

        var cause = EligibilityDiagnostics.Explain(Context(options)).ShouldNotBeNull();

        cause.ShouldContain("--range");
        cause.ShouldNotContain("--no-reject-black");
        cause.ShouldNotContain("--no-reject-frozen");
    }

    [Fact]
    public void FrozenFootageIsNotAlsoAccusedOfBeingBlack()
    {
        var options = new SelectionOptions
        {
            SkipHead = Offset.Zero,
            SkipTail = Offset.Zero,
            ChapterSkip = ChapterSkipMode.Off,
        };

        var context = Context(options, frozen: [Range(0, SourceSeconds)]);
        var cause = EligibilityDiagnostics.Explain(context).ShouldNotBeNull();

        cause.ShouldContain("--no-reject-frozen");
        cause.ShouldNotContain("--no-reject-black");
    }

    [Fact]
    public void BlackFootageIsNamedAsBlack()
    {
        var options = new SelectionOptions
        {
            SkipHead = Offset.Zero,
            SkipTail = Offset.Zero,
            ChapterSkip = ChapterSkipMode.Off,
            RejectFrozen = false,
        };

        var context = Context(options, black: [Range(0, SourceSeconds)]);

        EligibilityDiagnostics.Explain(context).ShouldNotBeNull().ShouldContain("--no-reject-black");
    }

    /// <summary>
    /// When no filter is responsible the source is simply too short, and saying so is more use
    /// than offering a relaxation that would change nothing.
    /// </summary>
    [Fact]
    public void ASourceShorterThanTheClipIsReportedAsSuch()
    {
        var cause = EligibilityDiagnostics
            .Explain(Context(Permissive(), windowSeconds: SourceSeconds + 100))
            .ShouldNotBeNull();

        cause.ShouldContain("--splice");
        cause.ShouldContain("long enough");
    }
}
