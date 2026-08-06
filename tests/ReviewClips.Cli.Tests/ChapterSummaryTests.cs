using ReviewClips.Cli.Presentation;
using ReviewClips.Core.Media;
using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Primitives;
using ReviewClips.Core.Selection;

namespace ReviewClips.Cli.Tests;

/// <summary>
/// The plan summary's chapter line: the only place the tool says what it did with a source's
/// markers. "none matched" and "matching was switched off" are different facts that a reader
/// would act on differently.
/// </summary>
public class ChapterSummaryTests
{
    private static RenderPlan PlanWith(SelectionOptions selection, params string[] chapterTitles)
    {
        var chapters = chapterTitles
            .Select((title, i) => new Chapter
            {
                Index = i + 1,
                Range = new TimeRange(TimeSpan.FromSeconds(i * 100), TimeSpan.FromSeconds((i + 1) * 100)),
                Title = title,
            })
            .ToList();

        var info = new MediaInfo
        {
            Path = "/movies/film.mkv",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTimeOffset.UnixEpoch,
            Duration = TimeSpan.FromSeconds(chapterTitles.Length * 100),
            Width = 1920,
            Height = 1080,
            FrameRate = 24d,
            VideoCodec = "h264",
            PixelFormat = "yuv420p",
            SampleAspectRatio = Ratio.One,
            HasAudio = true,
            Chapters = chapters,
        };

        return new RenderPlan
        {
            Request = new ClipRequest
            {
                Sources = [info.Path],
                OutputPath = "/out/render.mp4",
                TargetDuration = TimeSpan.FromSeconds(60),
                Selection = selection,
            },
            Sources = [new SourceMedia(info, null)],
            Segments = [],
            Encoder = new EncoderProfile
            {
                VideoEncoder = "libx264",
                IsHardware = false,
                QualityArguments = [],
                ExtraArguments = [],
            },
            Seed = 1,
        };
    }

    [Fact]
    public void NoChaptersMeansNothingToReport() =>
        PlanPrinter.DescribeChapters(PlanWith(new SelectionOptions())).ShouldBeNull();

    [Fact]
    public void SkippedChaptersAreNamed()
    {
        var summary = PlanPrinter
            .DescribeChapters(PlanWith(new SelectionOptions(), "Opening Titles", "Act One"))
            .ShouldNotBeNull();

        summary.ShouldContain("skipped 1");
        summary.ShouldContain("Opening Titles");
    }

    [Fact]
    public void NamedChaptersThatMatchNothingSaySo()
    {
        var summary = PlanPrinter
            .DescribeChapters(PlanWith(new SelectionOptions(), "Act One", "Act Two"))
            .ShouldNotBeNull();

        summary.ShouldContain("none matched");
    }

    /// <summary>
    /// The regression. With <c>--chapters off</c> and no <c>--skip-chapter</c> there are no
    /// patterns at all, so nothing was compared; "none matched" would claim a search ran and came
    /// back empty, sending a reader after badly named chapters instead of the flag they passed.
    /// </summary>
    [Fact]
    public void TurningChapterSkippingOffIsNotReportedAsAFailedMatch()
    {
        var selection = new SelectionOptions { ChapterSkip = ChapterSkipMode.Off };

        var summary = PlanPrinter
            .DescribeChapters(PlanWith(selection, "Opening Titles", "Act One", "End Credits"))
            .ShouldNotBeNull();

        summary.ShouldContain("--chapters off");
        summary.ShouldNotContain("none matched");
    }

    /// <summary>
    /// <c>--chapters off</c> disables the built-in titles but leaves explicit patterns in force,
    /// so with one supplied a match really was attempted and the "off" wording would be wrong.
    /// </summary>
    [Fact]
    public void AnExplicitPatternUnderChaptersOffIsStillAMatchAttempt()
    {
        var selection = new SelectionOptions
        {
            ChapterSkip = ChapterSkipMode.Off,
            SkipChapterPatterns = ["stinger"],
        };

        var summary = PlanPrinter
            .DescribeChapters(PlanWith(selection, "Act One", "Act Two"))
            .ShouldNotBeNull();

        summary.ShouldContain("none matched");
        summary.ShouldNotContain("--chapters off");
    }

    [Fact]
    public void UnnamedChaptersAreReportedAsHavingNothingToMatchAgainst()
    {
        var summary = PlanPrinter
            .DescribeChapters(PlanWith(new SelectionOptions(), null!, null!))
            .ShouldNotBeNull();

        summary.ShouldContain("unnamed");
    }
}
