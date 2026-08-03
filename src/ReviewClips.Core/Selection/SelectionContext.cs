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

    public int SegmentCount => SegmentDurations.Count;

    public TimeSpan LongestSegment =>
        SegmentDurations.Count == 0 ? TimeSpan.Zero : SegmentDurations.Max();

    /// <summary>
    /// Computes the ranges of a source that are legal to sample from: inside the skip-head/tail
    /// window, inside any explicit includes, and outside excludes plus black/frozen stretches.
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
