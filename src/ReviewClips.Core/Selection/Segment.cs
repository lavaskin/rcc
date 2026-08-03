using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Selection;

/// <summary>A chosen slice of a source file, destined to become one clip in the final render.</summary>
public sealed record Segment
{
    public required string SourcePath { get; init; }

    public required TimeSpan Start { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>Strategy-assigned desirability, for diagnostics and manifest output.</summary>
    public double Score { get; init; }

    /// <summary>Why this segment was picked, surfaced in <c>--dry-run</c> and the manifest.</summary>
    public string? Reason { get; init; }

    public TimeSpan End => Start + Duration;

    public TimeRange Range => new(Start, End);

    public override string ToString() =>
        $"{System.IO.Path.GetFileName(SourcePath)} @ {Start.TotalSeconds:0.00}s +{Duration.TotalSeconds:0.00}s";
}
