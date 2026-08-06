using ReviewClips.Core.Analysis;
using ReviewClips.Core.Media;
using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Selection;

/// <summary>A source file paired with its analysis, when one was needed and produced.</summary>
public sealed record SourceMedia(MediaInfo Info, MediaAnalysis? Analysis)
{
    public string Path => Info.Path;

    public bool HasAnalysis => Analysis is not null;
}

/// <summary>Everything a selector needs. Selectors are pure with respect to this input.</summary>
public sealed record SelectionContext
{
    public required IReadOnlyList<SourceMedia> Sources { get; init; }

    public required SelectionOptions Options { get; init; }

    /// <summary>The durations to fill, already jittered. One segment is chosen per entry.</summary>
    public required IReadOnlyList<TimeSpan> SegmentDurations { get; init; }

    /// <summary>Seeded RNG. Shared so a single seed reproduces the whole render.</summary>
    public required Random Random { get; init; }

    /// <summary>
    /// Playback rate the clips will be retimed by, from <c>--speed</c>. 1 leaves everything as it
    /// was. See <see cref="Segment.SpeedFactor"/>.
    /// </summary>
    public double SpeedFactor { get; init; } = 1d;

    public int SegmentCount => SegmentDurations.Count;

    /// <summary>
    /// The stretch of source a single clip needs, which is the longest planned clip after
    /// retiming.
    /// <para>
    /// Every eligibility question is asked with this: whether a range is long enough to sample
    /// from, whether a candidate start has room for a clip, and how far apart two picks must sit
    /// to avoid overlapping. All three are about source footage, so all three have to be scaled
    /// by the speed. Leaving it at the output length is what let a clip at <c>--speed 2</c> run
    /// off the end of its file, past an excluded range, and over its neighbour.
    /// </para>
    /// </summary>
    public TimeSpan LongestSegment =>
        SegmentDurations.Count == 0
            ? TimeSpan.Zero
            : ScaleToSource(SegmentDurations.Max());

    /// <summary>Converts an output duration into the source footage needed to produce it.</summary>
    public TimeSpan ScaleToSource(TimeSpan output) =>
        SpeedFactor is > 0d and not 1d ? output * SpeedFactor : output;

    /// <summary>
    /// The chapters of a source that the current options exclude, in file order. Empty when the
    /// source has no chapters, none are named, or no pattern matches.
    /// </summary>
    public IReadOnlyList<Media.Chapter> SkippedChapters(SourceMedia source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Media.ChapterFilter.Matching(source.Info.Chapters, Options.EffectiveChapterPatterns);
    }

    /// <summary>
    /// Computes the ranges of a source that are legal to sample from: inside the skip-head/tail
    /// window, inside any explicit includes, and outside excludes, skipped chapters, and
    /// black/frozen stretches.
    /// </summary>
    public TimeRangeSet EligibleRanges(SourceMedia source, TimeSpan windowLength)
    {
        var duration = source.Info.Duration;
        if (duration <= TimeSpan.Zero)
        {
            return TimeRangeSet.Empty;
        }

        var head = Options.SkipHead.Resolve(duration);
        var tail = Options.SkipTail.Resolve(duration);
        var start = head;
        var end = duration - tail;

        if (end <= start)
        {
            // Skips swallowed the whole file; fall back to the full runtime rather than failing.
            start = TimeSpan.Zero;
            end = duration;
        }

        var eligible = TimeRangeSet.Of(new TimeRange(start, end));

        if (Options.IncludeRanges.Count > 0)
        {
            eligible = eligible.Intersect(Options.IncludeRanges);
        }

        if (Options.ExcludeRanges.Count > 0)
        {
            eligible = eligible.Subtract(Options.ExcludeRanges);
        }

        // Chapter bounds are stated by the container rather than inferred, so they are subtracted
        // un-padded. A clip still cannot bleed into one, because selectors require a candidate
        // window to fit entirely inside a single eligible range.
        var skippedChapters = SkippedChapters(source);
        if (skippedChapters.Count > 0)
        {
            eligible = eligible.Subtract(skippedChapters.Select(c => c.Range));
        }

        if (source.Analysis is { } analysis)
        {
            var holes = new List<TimeRange>();
            if (Options.RejectBlack)
            {
                // Pad so a clip cannot bleed into the fade either.
                holes.AddRange(analysis.BlackRanges.Select(r => r.Pad(windowLength)));
            }

            if (Options.RejectFrozen)
            {
                holes.AddRange(analysis.FreezeRanges);
            }

            if (holes.Count > 0)
            {
                eligible = eligible.Subtract(holes);
            }
        }

        return eligible.WhereLongerThan(windowLength);
    }
}
