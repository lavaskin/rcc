using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Selection;

/// <summary>A chosen slice of a source file, destined to become one clip in the final render.</summary>
public sealed record Segment
{
    public required string SourcePath { get; init; }

    public required TimeSpan Start { get; init; }

    /// <summary>How long this clip lasts in the finished render.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Playback rate this clip is retimed by, which is how much source it takes to fill
    /// <see cref="Duration"/> of output. 1 for the ordinary case.
    /// <para>
    /// A clip has two lengths at once: at <c>--speed 2</c> a five second clip occupies five
    /// seconds of the render and ten seconds of the film. Selection reserves the latter, via
    /// <see cref="ReadDuration"/>; the render's runtime is built from the former.
    /// </para>
    /// </summary>
    public double SpeedFactor { get; init; } = 1d;

    /// <summary>Strategy-assigned desirability, for diagnostics and manifest output.</summary>
    public double Score { get; init; }

    /// <summary>Why this segment was picked, surfaced in <c>--dry-run</c> and the manifest.</summary>
    public string? Reason { get; init; }

    /// <summary>Source footage this clip consumes. Equal to <see cref="Duration"/> at normal speed.</summary>
    public TimeSpan ReadDuration => SpeedFactor is > 0d and not 1d
        ? Duration * SpeedFactor
        : Duration;

    /// <summary>
    /// Where the clip stops reading from its source. Source-side, not output-side: fit,
    /// collision and exclusion checks are all questions about the source.
    /// </summary>
    public TimeSpan End => Start + ReadDuration;

    /// <summary>The stretch of source this clip occupies.</summary>
    public TimeRange Range => new(Start, End);

    public override string ToString() =>
        $"{System.IO.Path.GetFileName(SourcePath)} @ {Start.TotalSeconds:0.00}s +{Duration.TotalSeconds:0.00}s";
}
