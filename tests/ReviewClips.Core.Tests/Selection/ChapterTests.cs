using ReviewClips.Core.Media;
using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Tests.Selection;

/// <summary>
/// Chapter title matching. Skipping a real chapter costs a few minutes of eligible footage,
/// whereas failing to skip the credits puts a text crawl in the render, so the patterns lean
/// towards skipping — but not so loosely that an ordinary content chapter is caught.
/// </summary>
public class ChapterFilterTests
{
    [Theory]
    [InlineData("End Credits")]
    [InlineData("end credits")]
    [InlineData("Closing Credits")]
    [InlineData("Credits")]
    [InlineData("Opening Credits")]
    [InlineData("Opening")]
    [InlineData("Main Titles")]
    [InlineData("Title Sequence")]
    [InlineData("Studio Logos")]
    [InlineData("Universal Logo")]
    [InlineData("Ending")]
    [InlineData("Outro")]
    [InlineData("Intro")]
    [InlineData("Recap")]
    [InlineData("Previously On...")]
    [InlineData("Next Episode Preview")]
    [InlineData("Gag Reel")]
    [InlineData("Outtakes")]
    public void DefaultPatterns_CatchStructuralTitles(string title) =>
        ChapterFilter.MatchesAny(title, ChapterFilter.IntroOutroPatterns).ShouldBeTrue(title);

    [Theory]
    [InlineData("The Heist")]
    [InlineData("Arrival at the Station")]
    [InlineData("Final Confrontation")]
    [InlineData("Escape from the Compound")]
    [InlineData("Prologue")]
    [InlineData("The Verdict")]
    [InlineData("Act Two")]
    public void DefaultPatterns_LeaveContentChaptersAlone(string title) =>
        ChapterFilter.MatchesAny(title, ChapterFilter.IntroOutroPatterns).ShouldBeFalse(title);

    [Fact]
    public void PatternWithoutMetacharacters_MatchesAsASubstring() =>
        // Typing "credits" and getting only an exactly-named chapter would surprise everyone.
        ChapterFilter.Matches("Cast and Credits Roll", "credits").ShouldBeTrue();

    [Fact]
    public void PatternWithMetacharacters_MatchesTheWholeTitleAsAGlob()
    {
        ChapterFilter.Matches("Ending Theme", "ending*").ShouldBeTrue();
        ChapterFilter.Matches("The Ending Theme", "ending*").ShouldBeFalse();
        ChapterFilter.Matches("OP", "?p").ShouldBeTrue();
        ChapterFilter.Matches("Stop the Car", "?p").ShouldBeFalse();
    }

    [Fact]
    public void Matching_IsCaseInsensitive() =>
        ChapterFilter.Matches("END CREDITS", "End Credits").ShouldBeTrue();

    [Theory]
    [InlineData("Chapter 07")]
    [InlineData("chapter 7")]
    [InlineData("Chapter 12")]
    [InlineData("12")]
    [InlineData("Part 3")]
    [InlineData("00:14:32.100")]
    [InlineData("1:02:03")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void GenericTitles_AreRecognised(string? title) =>
        ChapterFilter.IsGenericTitle(title).ShouldBeTrue();

    [Theory]
    [InlineData("End Credits")]
    [InlineData("Chapter 7: The Reveal")]
    [InlineData("Act 2")]
    public void RealTitles_AreNotTreatedAsGeneric(string title) =>
        ChapterFilter.IsGenericTitle(title).ShouldBeFalse();

    [Fact]
    public void GenericTitles_AreNeverMatched()
    {
        // A wildcard must not be able to catch "Chapter 12" just because it is a title.
        var chapters = SelectionTestData.Chapters(
            ("Chapter 01", 100),
            ("Chapter 02", 200));

        ChapterFilter.Matching(chapters, ["*"]).ShouldBeEmpty();
    }

    [Fact]
    public void Matching_ReturnsChaptersInFileOrder()
    {
        var chapters = SelectionTestData.Chapters(
            ("Opening Credits", 90),
            ("The Job", 600),
            ("Interlude", 700),
            ("End Credits", 900));

        var matched = ChapterFilter.Matching(chapters, ChapterFilter.IntroOutroPatterns);

        matched.Select(c => c.Index).ShouldBe([0, 3]);
    }

    [Fact]
    public void NoPatterns_MatchesNothing() =>
        ChapterFilter.Matching(SelectionTestData.Chapters(("End Credits", 100)), []).ShouldBeEmpty();
}

/// <summary>
/// Chapter markers as they affect eligibility: a percentage trim is a guess, whereas a named
/// chapter states where the credits are.
/// </summary>
public class ChapterEligibilityTests
{
    /// <summary>
    /// A 100-minute feature with a 90s logo/title block up front and a 5-minute credit roll,
    /// both named. Selection must not be able to touch either.
    /// </summary>
    private static MediaInfo ChapteredFeature() =>
        SelectionTestData.Info(
            6000,
            chapters: SelectionTestData.Chapters(
                ("Opening Titles", 90),
                ("Act One", 2000),
                ("Act Two", 4000),
                ("Act Three", 5700),
                ("End Credits", 6000)));

    private static SelectionContext Context(MediaInfo info, SelectionOptions? options = null) =>
        SelectionTestData.Context([new SourceMedia(info, null)], segmentCount: 8, options: options);

    [Fact]
    public void NamedIntroAndCreditsChapters_AreExcludedByDefault()
    {
        var info = ChapteredFeature();
        var context = Context(info);

        var eligible = context.EligibleRanges(new SourceMedia(info, null), TimeSpan.FromSeconds(5));

        eligible.Contains(TimeSpan.FromSeconds(30)).ShouldBeFalse("inside the opening titles");
        eligible.Contains(TimeSpan.FromSeconds(5800)).ShouldBeFalse("inside the end credits");
        eligible.Contains(TimeSpan.FromSeconds(3000)).ShouldBeTrue("mid-film content");
    }

    [Fact]
    public void CreditsAreExcludedEvenWhenTheTailTrimIsTurnedOff()
    {
        // The whole point: with chapters, the blind percentage trims become unnecessary.
        var info = ChapteredFeature();
        var context = Context(info, new SelectionOptions
        {
            SkipHead = Offset.Zero,
            SkipTail = Offset.Zero,
        });

        var eligible = context.EligibleRanges(new SourceMedia(info, null), TimeSpan.FromSeconds(5));

        eligible.Contains(TimeSpan.FromSeconds(5800)).ShouldBeFalse("inside the end credits");
        eligible.Contains(TimeSpan.FromSeconds(10)).ShouldBeFalse("inside the opening titles");

        // With the trims gone, footage the percentages would have discarded is now usable.
        eligible.Contains(TimeSpan.FromSeconds(120)).ShouldBeTrue();
        eligible.Contains(TimeSpan.FromSeconds(5600)).ShouldBeTrue();
    }

    [Fact]
    public void ChaptersOff_RestoresPurelyPercentageBasedTrimming()
    {
        var info = ChapteredFeature();
        var context = Context(info, new SelectionOptions
        {
            ChapterSkip = ChapterSkipMode.Off,
            SkipHead = Offset.Zero,
            SkipTail = Offset.Zero,
        });

        var eligible = context.EligibleRanges(new SourceMedia(info, null), TimeSpan.FromSeconds(5));

        eligible.Contains(TimeSpan.FromSeconds(5800)).ShouldBeTrue();
        eligible.TotalDuration.ShouldBe(info.Duration);
    }

    [Fact]
    public void ExplicitPatterns_ApplyEvenWithChaptersOff()
    {
        var info = SelectionTestData.Info(
            1200,
            chapters: SelectionTestData.Chapters(
                ("Cold Open", 120),
                ("The Case", 1000),
                ("Stinger", 1200)));

        var context = Context(info, new SelectionOptions
        {
            ChapterSkip = ChapterSkipMode.Off,
            SkipChapterPatterns = ["stinger"],
            SkipHead = Offset.Zero,
            SkipTail = Offset.Zero,
        });

        var eligible = context.EligibleRanges(new SourceMedia(info, null), TimeSpan.FromSeconds(5));

        eligible.Contains(TimeSpan.FromSeconds(1100)).ShouldBeFalse();
        eligible.Contains(TimeSpan.FromSeconds(500)).ShouldBeTrue();
        eligible.Contains(TimeSpan.FromSeconds(10)).ShouldBeTrue("Cold Open is not a default pattern");
    }

    [Fact]
    public void ExplicitPatterns_AddToTheBuiltInSet()
    {
        var info = SelectionTestData.Info(
            1200,
            chapters: SelectionTestData.Chapters(
                ("Cold Open", 120),
                ("The Case", 1000),
                ("End Credits", 1200)));

        var options = new SelectionOptions { SkipChapterPatterns = ["cold open"] };
        var skipped = ChapterFilter.Matching(info.Chapters, options.EffectiveChapterPatterns);

        skipped.Select(c => c.Title).ShouldBe(["Cold Open", "End Credits"]);
    }

    [Fact]
    public void UnnamedChapters_ChangeNothing()
    {
        // The common disc-rip case. Chapter filtering must degrade to a no-op, not to a guess.
        var info = SelectionTestData.Info(
            6000,
            chapters: SelectionTestData.Chapters(
                ("Chapter 01", 1200),
                ("Chapter 02", 2400),
                ("Chapter 03", 3600),
                ("Chapter 04", 4800),
                ("Chapter 05", 6000)));

        info.HasChapters.ShouldBeTrue();
        info.HasNamedChapters.ShouldBeFalse();

        var context = Context(info, new SelectionOptions
        {
            SkipHead = Offset.Zero,
            SkipTail = Offset.Zero,
        });

        var eligible = context.EligibleRanges(new SourceMedia(info, null), TimeSpan.FromSeconds(5));

        eligible.TotalDuration.ShouldBe(info.Duration);
    }

    [Fact]
    public void SourcesWithoutChapters_ChangeNothing()
    {
        var info = SelectionTestData.Info(6000);
        var context = Context(info, new SelectionOptions
        {
            SkipHead = Offset.Zero,
            SkipTail = Offset.Zero,
        });

        context.EligibleRanges(new SourceMedia(info, null), TimeSpan.FromSeconds(5))
            .TotalDuration.ShouldBe(info.Duration);
    }

    [Fact]
    public void SkippedChaptersAreReportedForDisplay()
    {
        var info = ChapteredFeature();
        var context = Context(info);

        var skipped = context.SkippedChapters(new SourceMedia(info, null));

        skipped.Select(c => c.Title).ShouldBe(["Opening Titles", "End Credits"]);
    }

    [Theory]
    [InlineData(SelectionStrategy.Uniform)]
    [InlineData(SelectionStrategy.Random)]
    [InlineData(SelectionStrategy.Scored)]
    public void NoSelectedClipLandsInASkippedChapter(SelectionStrategy strategy)
    {
        var info = ChapteredFeature();
        var analysis = SelectionTestData.Analysis(info);

        var context = SelectionTestData.Context(
            [new SourceMedia(info, analysis)],
            segmentCount: 12,
            options: new SelectionOptions
            {
                Strategy = strategy,

                // Removing the percentage trims leaves the chapters solely responsible.
                SkipHead = Offset.Zero,
                SkipTail = Offset.Zero,
            });

        var segments = SegmentSelectorFactory.CreateDefault().Create(strategy).SelectSegments(context);

        segments.ShouldNotBeEmpty();

        var forbidden = ChapterFilter.Matching(info.Chapters, ChapterFilter.IntroOutroPatterns)
            .Select(c => c.Range)
            .ToList();

        foreach (var segment in segments)
        {
            foreach (var hole in forbidden)
            {
                // A clip must not even partially overlap: a five-second clip beginning four
                // seconds before the credit roll would still show it.
                segment.Range.Overlaps(hole)
                    .ShouldBeFalse($"{segment} runs into skipped chapter {hole}");
            }
        }
    }
}
