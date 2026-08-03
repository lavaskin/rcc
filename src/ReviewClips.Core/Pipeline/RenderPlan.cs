using ReviewClips.Core.Options;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Pipeline;

/// <summary>
/// The decided-upon shape of a render: which slices, from which files, with which encoder.
/// Produced before any encoding happens so it can be printed by <c>--dry-run</c>.
/// </summary>
public sealed record RenderPlan
{
    public required ClipRequest Request { get; init; }

    public required IReadOnlyList<SourceMedia> Sources { get; init; }

    public required IReadOnlyList<Segment> Segments { get; init; }

    public required EncoderProfile Encoder { get; init; }

    public required int Seed { get; init; }

    /// <summary>Non-fatal issues worth surfacing, e.g. relaxed spacing or a shortfall of usable footage.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public TimeSpan TotalDuration =>
        Segments.Aggregate(TimeSpan.Zero, (total, s) => total + s.Duration);

    /// <summary>Number of clips that will actually be cut, ignoring repeats.</summary>
    public int DistinctClipCount => Planning.ClipSequencer.CountDistinct(Segments);

    /// <summary>Total source footage consumed, counting each distinct clip once.</summary>
    public TimeSpan DistinctSourceDuration => Planning.ClipSequencer.DistinctSourceDuration(Segments);

    /// <summary>How many times the average clip appears. 1.0 means no repetition.</summary>
    public double RepeatFactor =>
        DistinctClipCount == 0 ? 0d : (double)Segments.Count / DistinctClipCount;

    /// <summary>Runtime after transition overlaps are accounted for.</summary>
    public TimeSpan EffectiveDuration
    {
        get
        {
            if (!Request.Transition.IsEnabled || Segments.Count < 2)
            {
                return TotalDuration;
            }

            var overlap = Request.Transition.Duration * (Segments.Count - 1);
            var result = TotalDuration - overlap;
            return result > TimeSpan.Zero ? result : TotalDuration;
        }
    }
}

public sealed record RenderResult
{
    public required RenderPlan Plan { get; init; }

    public required string OutputPath { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public required long OutputSizeBytes { get; init; }
}
