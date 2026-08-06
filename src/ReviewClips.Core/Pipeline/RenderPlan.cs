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

    /// <summary>
    /// Total source footage consumed, counting each distinct clip once. Read off
    /// <see cref="SourceUsage"/> rather than computed again, so the summary's figure and the
    /// guardrail's are the same number by construction.
    /// </summary>
    public TimeSpan DistinctSourceDuration => SourceUsage.Used;

    /// <summary>
    /// What share of the supplied footage this render consumes. Derived rather than stored so
    /// the summary, the manifest and the guardrail can never report different numbers.
    /// </summary>
    public Planning.SourceUsageReport SourceUsage => Planning.SourceUsageGuard.Evaluate(
        Segments,
        Sources.Select(s => (s.Info.Path, s.Info.Duration)),
        Request.MaxSourceFraction);

    /// <summary>How many times the average clip appears. 1.0 means no repetition.</summary>
    public double RepeatFactor =>
        DistinctClipCount == 0 ? 0d : (double)Segments.Count / DistinctClipCount;

    /// <summary>
    /// Runtime after transition overlaps are accounted for.
    /// <para>
    /// Computed the same way the stitcher computes it, via
    /// <see cref="Planning.SplicePlanner.EffectiveTransition"/>: a transition longer than half the
    /// shortest clip is clamped, because it cannot consume more of a clip than the clip has.
    /// Subtracting the requested duration unclamped makes the summary and the manifest under-report
    /// a render whose clips are shorter than twice the transition, which is exactly the case a
    /// reader would be checking the number for.
    /// </para>
    /// </summary>
    public TimeSpan EffectiveDuration =>
        Planning.SplicePlanner.RenderedDuration(
            [.. Segments.Select(s => s.Duration)],
            Request.Transition.IsEnabled ? Request.Transition.Duration : TimeSpan.Zero);
}

public sealed record RenderResult
{
    public required RenderPlan Plan { get; init; }

    public required string OutputPath { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public required long OutputSizeBytes { get; init; }
}
