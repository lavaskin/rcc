using System.Text;
using Microsoft.Extensions.Logging;
using ReviewClips.Core.Pipeline;
using ReviewClips.Ffmpeg.Process;

namespace ReviewClips.Ffmpeg.Stitching;

/// <summary>
/// Joins segments with the concat demuxer and a stream copy.
/// <para>
/// This is the fast path: no re-encoding at all, so it finishes in well under a second
/// regardless of runtime. It only applies when there are no transitions and no fades, since
/// both require pixels to be touched. It is safe here because the extractor already normalised
/// every segment to identical codec parameters.
/// </para>
/// </summary>
public sealed class ConcatDemuxerStitcher : IStitcher
{
    private readonly FfmpegRunner _runner;
    private readonly ILogger<ConcatDemuxerStitcher> _logger;

    public ConcatDemuxerStitcher(FfmpegRunner runner, ILogger<ConcatDemuxerStitcher> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public bool CanHandle(StitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return !request.Transition.IsEnabled
            && request.Transition.FadeIn <= TimeSpan.Zero
            && request.Transition.FadeOut <= TimeSpan.Zero;
    }

    public async Task StitchAsync(
        StitchRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var listPath = WriteListFile(request);
        var total = request.SegmentDurations.Aggregate(TimeSpan.Zero, (a, b) => a + b);

        _logger.LogDebug("Concatenating {Count} segments by stream copy", request.SegmentPaths.Count);

        await _runner.RunFfmpegCheckedAsync(
            BuildArguments(listPath, request),
            total,
            progress,
            cancellationToken);
    }

    public IReadOnlyList<string> DescribeArguments(StitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return BuildArguments(Path.Combine(request.WorkingDirectory, "segments.txt"), request);
    }

    private static IReadOnlyList<string> BuildArguments(string listPath, StitchRequest request) =>
    [
        "-hide_banner", "-loglevel", "error", "-y",
        "-f", "concat",

        // Absolute paths are rejected by the demuxer unless safety is relaxed.
        "-safe", "0",
        "-i", listPath,
        "-c", "copy",

        // The concat demuxer happens not to forward chapters today, but the output must not
        // depend on that: state it, so both stitchers make the same guarantee.
        "-map_chapters", "-1",
        "-movflags", "+faststart",
        request.OutputPath,
    ];

    private string WriteListFile(StitchRequest request)
    {
        var listPath = Path.Combine(request.WorkingDirectory, "segments.txt");
        var builder = new StringBuilder();

        foreach (var path in request.SegmentPaths)
        {
            // The demuxer's own quoting rules: wrap in single quotes and escape any
            // single quote by closing, inserting an escaped quote, and reopening.
            var escaped = path.Replace("'", @"'\''", StringComparison.Ordinal);
            builder.Append("file '").Append(escaped).Append('\'').Append('\n');
        }

        File.WriteAllText(listPath, builder.ToString());
        _logger.LogDebug("Wrote concat list {Path}", listPath);

        return listPath;
    }
}
