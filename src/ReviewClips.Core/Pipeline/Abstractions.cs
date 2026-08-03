using ReviewClips.Core.Analysis;
using ReviewClips.Core.Media;
using ReviewClips.Core.Options;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Pipeline;

/// <summary>Reads technical metadata from a media file.</summary>
public interface IMediaProbe
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Scans a file for scene cuts, motion, and unusable stretches.</summary>
public interface IMediaAnalyzer
{
    Task<MediaAnalysis> AnalyzeAsync(
        MediaInfo info,
        AnalysisSettings settings,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Persists analysis results so repeat renders of the same title skip the scan.</summary>
public interface IAnalysisCache
{
    Task<MediaAnalysis?> TryGetAsync(MediaInfo info, AnalysisSettings settings, CancellationToken cancellationToken);

    Task SaveAsync(MediaAnalysis analysis, CancellationToken cancellationToken);
}

/// <summary>Picks a concrete encoder after probing what the local FFmpeg build supports.</summary>
public interface IEncoderSelector
{
    Task<EncoderProfile> SelectAsync(EncoderOptions options, CancellationToken cancellationToken);
}

public sealed record SegmentExtraction
{
    public required Segment Segment { get; init; }

    public required string OutputPath { get; init; }

    public required int Index { get; init; }
}

/// <summary>Cuts one segment out of a source and normalises it to the target format.</summary>
public interface ISegmentExtractor
{
    Task ExtractAsync(
        SegmentExtractionRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    /// <summary>Builds the command line without running it, for <c>--dry-run</c>.</summary>
    IReadOnlyList<string> DescribeArguments(SegmentExtractionRequest request);
}

public sealed record SegmentExtractionRequest
{
    public required Segment Segment { get; init; }

    public required MediaInfo Source { get; init; }

    public required string OutputPath { get; init; }

    public required OutputFormat Format { get; init; }

    public required LookOptions Look { get; init; }

    public required EncoderProfile Encoder { get; init; }

    public required EncoderOptions EncoderOptions { get; init; }

    public required bool Mute { get; init; }
}

public sealed record StitchRequest
{
    public required IReadOnlyList<string> SegmentPaths { get; init; }

    public required string OutputPath { get; init; }

    public required IReadOnlyList<TimeSpan> SegmentDurations { get; init; }

    public required TransitionOptions Transition { get; init; }

    public required OutputFormat Format { get; init; }

    public required EncoderProfile Encoder { get; init; }

    public required EncoderOptions EncoderOptions { get; init; }

    public required bool Mute { get; init; }

    public required string WorkingDirectory { get; init; }
}

/// <summary>Joins normalised segments into the finished render.</summary>
public interface IStitcher
{
    bool CanHandle(StitchRequest request);

    Task StitchAsync(
        StitchRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    IReadOnlyList<string> DescribeArguments(StitchRequest request);
}
