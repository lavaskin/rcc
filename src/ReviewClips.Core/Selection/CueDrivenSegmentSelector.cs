using ReviewClips.Core.Options;

namespace ReviewClips.Core.Selection;

/// <summary>
/// Builds the render from an explicit list of timestamps. Cue order is preserved and the target
/// duration is shared equally between cues.
/// <para>
/// Preferred when the footage accompanies specific commentary: a clip of the scene under
/// discussion illustrates the criticism rather than decorating it, which is the stronger
/// fair-use position.
/// </para>
/// </summary>
public sealed class CueDrivenSegmentSelector : ISegmentSelector
{
    public SelectionStrategy Strategy => SelectionStrategy.Cues;

    // Only needed to snap cues onto shot boundaries; harmless when analysis is absent.
    public bool RequiresAnalysis => true;

    public IReadOnlyList<Segment> SelectSegments(SelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cues = context.Options.Cues;
        if (cues.Count == 0 || context.Sources.Count == 0)
        {
            return [];
        }

        var totalTarget = context.SegmentDurations.Aggregate(TimeSpan.Zero, (a, b) => a + b);
        var perCue = totalTarget > TimeSpan.Zero
            ? TimeSpan.FromSeconds(totalTarget.TotalSeconds / cues.Count)
            : TimeSpan.FromSeconds(5);

        var segments = new List<Segment>(cues.Count);

        foreach (var cue in cues)
        {
            // A cue belongs to the first source long enough to contain it.
            var source = context.Sources.FirstOrDefault(s => cue < s.Info.Duration);
            if (source is null)
            {
                continue;
            }

            var start = cue;
            var reason = "cue";

            if (context.Options.SnapCuesToScene && source.Analysis is { } analysis)
            {
                // Snap back to the start of the shot containing the cue so the clip opens
                // cleanly, but only if that is a small correction rather than a big jump.
                var shot = analysis.Shots().FirstOrDefault(s => s.Contains(cue));
                if (shot.Duration > TimeSpan.Zero && (cue - shot.Start) <= TimeSpan.FromSeconds(3))
                {
                    start = shot.Start;
                    reason = "cue/snapped-to-shot";
                }
            }

            if (start < TimeSpan.Zero)
            {
                start = TimeSpan.Zero;
            }

            // Never run past the end of the file. Measured in source footage, so a retimed clip
            // is held to what it will actually read rather than to what it will play back as.
            var available = source.Info.Duration - start;
            if (available <= TimeSpan.Zero)
            {
                continue;
            }

            var affordable = context.SpeedFactor is > 0d and not 1d
                ? TimeSpan.FromSeconds(available.TotalSeconds / context.SpeedFactor)
                : available;

            var duration = perCue < affordable ? perCue : affordable;
            if (duration <= TimeSpan.Zero)
            {
                continue;
            }

            segments.Add(new Segment
            {
                SourcePath = source.Path,
                Start = start,
                Duration = duration,
                SpeedFactor = context.SpeedFactor,
                Score = 1d,
                Reason = reason,
            });
        }

        return segments;
    }
}
