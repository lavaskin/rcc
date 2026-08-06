using ReviewClips.Core.Analysis;
using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Selection;

/// <summary>
/// The default strategy. Prefers shot-aligned windows whose motion sits in a "pleasant"
/// band and which are evenly paced.
/// <para>
/// Every threshold is relative to the title's own median motion: absolute frame-difference
/// values differ by an order of magnitude between a slow drama and an action film, so
/// <see cref="ScoringOptions"/> has no single correct value across genres.
/// </para>
/// </summary>
public sealed class ScoredSegmentSelector : SegmentSelectorBase
{
    public override SelectionStrategy Strategy => SelectionStrategy.Scored;

    public override bool RequiresAnalysis => true;

    protected override IEnumerable<SegmentCandidate> GenerateCandidates(
        SelectionContext context,
        SourceMedia source,
        TimeRangeSet eligible,
        TimeSpan window)
    {
        if (source.Analysis is not { } analysis)
        {
            yield break;
        }

        var scoring = context.Options.Scoring;
        var median = analysis.Motion.Median();

        // Shot-aligned candidates get a bonus; a grid fills in when cut detection is sparse
        // (animation, long takes, or footage with soft dissolves instead of hard cuts).
        var candidates = SceneSegmentSelector
            .ShotCandidates(context, source, window)
            .Select(c => (c.Start, Aligned: true))
            .Concat(GridStarts(eligible, window, context.Options.CandidateDensity)
                .Select(s => (Start: s, Aligned: false)));

        var seen = new HashSet<long>();

        foreach (var (start, aligned) in candidates)
        {
            // Deduplicate to ~40ms buckets so grid and shot candidates don't pile up.
            if (!seen.Add((long)(start.TotalSeconds * 25)))
            {
                continue;
            }

            var range = TimeRange.FromDuration(start, window);
            var score = ScoreWindow(analysis.Motion, range, median, scoring);
            if (score is null)
            {
                continue;
            }

            var final = aligned ? Math.Min(1d, score.Value * 1.15d) : score.Value;
            yield return new SegmentCandidate(
                source,
                start,
                final,
                aligned ? "scored/scene-aligned" : "scored/grid");
        }
    }

    /// <summary>
    /// Returns a 0..1 desirability, or <c>null</c> when the window should be rejected outright
    /// for being too static or too frantic.
    /// </summary>
    internal static double? ScoreWindow(
        MotionCurve motion,
        TimeRange range,
        double median,
        ScoringOptions scoring)
    {
        var mean = motion.MeanOver(range);
        if (mean is null)
        {
            return null;
        }

        // No usable baseline (e.g. a completely static source): accept everything neutrally
        // rather than dividing by zero and rejecting the entire file.
        if (median <= double.Epsilon)
        {
            return 0.5d;
        }

        var ratio = mean.Value / median;
        if (ratio < scoring.MotionFloorMultiple || ratio > scoring.MotionCeilingMultiple)
        {
            return null;
        }

        var target = scoring.TargetMotionMultiple <= 0 ? 1d : scoring.TargetMotionMultiple;

        // Peaks at 1.0 when ratio == target, decaying smoothly either side.
        var normalized = (ratio - target) / target;
        var score = 1d / (1d + (normalized * normalized));

        // Penalize uneven windows: high variance usually means the window straddles a cut
        // or contains a flash, both of which look like glitches in a background loop.
        var stdDev = motion.StdDevOver(range);
        if (stdDev is not null && mean.Value > double.Epsilon)
        {
            var coefficientOfVariation = Math.Clamp(stdDev.Value / mean.Value, 0d, 1d);
            score *= 1d - (scoring.StabilityWeight * coefficientOfVariation);
        }

        return Math.Clamp(score, 0d, 1d);
    }

    private static IEnumerable<TimeSpan> GridStarts(TimeRangeSet eligible, TimeSpan window, int density)
    {
        if (density <= 0)
        {
            yield break;
        }

        var totalUsable = eligible.Sum(r => Math.Max((r.Duration - window).TotalSeconds, 0d));
        if (totalUsable <= 0)
        {
            yield break;
        }

        var step = totalUsable / density;
        if (step <= 0)
        {
            yield break;
        }

        foreach (var range in eligible)
        {
            var usable = (range.Duration - window).TotalSeconds;
            for (var offset = 0d; offset <= usable; offset += step)
            {
                yield return range.Start + TimeSpan.FromSeconds(offset);
            }
        }
    }
}
