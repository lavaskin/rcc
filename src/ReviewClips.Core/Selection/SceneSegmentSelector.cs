using ReviewClips.Core.Analysis;
using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Selection;

/// <summary>
/// Starts every clip a fraction of a second after a detected cut, so segments open on a
/// fresh shot instead of slicing into the middle of a camera move.
/// </summary>
public sealed class SceneSegmentSelector : SegmentSelectorBase
{
    public override SelectionStrategy Strategy => SelectionStrategy.Scene;

    public override bool RequiresAnalysis => true;

    protected override IEnumerable<SegmentCandidate> GenerateCandidates(
        SelectionContext context,
        SourceMedia source,
        TimeRangeSet eligible,
        TimeSpan window) =>
        ShotCandidates(context, source, window);

    /// <summary>
    /// Shared with the scored selector: one candidate per shot long enough to hold a clip,
    /// offset past the cut by the configured lead-in.
    /// </summary>
    internal static IEnumerable<SegmentCandidate> ShotCandidates(
        SelectionContext context,
        SourceMedia source,
        TimeSpan window)
    {
        if (source.Analysis is not { } analysis)
        {
            yield break;
        }

        var options = context.Options;
        var leadIn = options.SceneLeadIn;
        var minShot = options.MinShotDuration;

        foreach (var shot in analysis.Shots())
        {
            if (shot.Duration < minShot)
            {
                continue;
            }

            var start = shot.Start + leadIn;
            var required = window;

            // The shot must still hold a full clip after the lead-in. If it doesn't but the
            // shot itself is long enough, fall back to starting exactly at the cut.
            if (start + required > shot.End)
            {
                if (shot.Duration < required)
                {
                    continue;
                }

                start = shot.Start;
            }

            // Longer shots are safer: less chance of the clip running past the next cut.
            var headroom = (shot.End - (start + required)).TotalSeconds;
            var score = 1d - (1d / (1d + Math.Max(headroom, 0d)));

            yield return new SegmentCandidate(source, start, score, "scene-aligned");
        }
    }
}
