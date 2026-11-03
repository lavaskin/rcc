using ReviewClips.Core.Analysis;
using ReviewClips.Core.Media;
using ReviewClips.Core.Options;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Pipeline;

/// <summary>Reads technical metadata from a media file.</summary>
public interface IMediaProbe
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads only the container duration; this is what <c>--match-audio</c> asks.
    /// <para>
    /// Separate from <see cref="ProbeAsync"/> because <see cref="MediaInfo"/> requires a video
    /// stream — width, height, frame rate and pixel format are all non-nullable — so a bare
    /// <c>.wav</c> cannot go through it.
    /// </para>
    /// </summary>
    Task<TimeSpan> ProbeDurationAsync(string path, CancellationToken cancellationToken);
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

/// <summary>Cuts one segment out of a source and normalizes it to the target format.</summary>
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

    /// <summary>
    /// True when some slot plays only the opening of this encode. Written without B-frames,
    /// because the concat demuxer cuts by decode timestamp and that matches display order only
    /// when no frame is reordered.
    /// </summary>
    public bool Trimmed { get; init; }
}

public sealed record StitchRequest
{
    public required IReadOnlyList<string> SegmentPaths { get; init; }

    public required string OutputPath { get; init; }

    public required IReadOnlyList<TimeSpan> SegmentDurations { get; init; }

    /// <summary>
    /// How long each slot's file was encoded, where a file can be longer than its slot. A slot
    /// plays only the opening <see cref="SegmentDurations"/> of its file. Null when every file is
    /// exactly as long as its slot.
    /// </summary>
    public IReadOnlyList<TimeSpan>? EncodedDurations { get; init; }

    public required TransitionOptions Transition { get; init; }

    public required OutputFormat Format { get; init; }

    public required EncoderProfile Encoder { get; init; }

    public required EncoderOptions EncoderOptions { get; init; }

    public required AudioOptions Audio { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>True when the finished file should carry no audio stream.</summary>
    public bool Mute => Audio.IsMuted;

    /// <summary>True when the segments' own audio should be joined.</summary>
    public bool UsesSegmentAudio => Audio.UsesSegmentAudio;

    /// <summary>True when slot <paramref name="index"/> plays less than its whole file.</summary>
    public bool IsTrimmed(int index) =>
        EncodedDurations is { } encoded && encoded[index] > SegmentDurations[index];
}

/// <summary>Joins normalized segments into the finished render.</summary>
public interface IStitcher
{
    bool CanHandle(StitchRequest request);

    Task StitchAsync(
        StitchRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    IReadOnlyList<string> DescribeArguments(StitchRequest request);
}
