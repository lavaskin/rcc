using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using ReviewClips.Core.Pipeline;
using ReviewClips.Ffmpeg.Process;

namespace ReviewClips.Ffmpeg.Stitching;

/// <summary>
/// Joins segments with the concat demuxer and a stream copy.
/// <para>
/// The fast path: no re-encoding at all. Applies only when there are no transitions and no
/// fades, since both require pixels to be touched, and is safe because the extractor already
/// normalized every segment to identical codec parameters.
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
            cancellationToken,
            requireFrames: true);
    }

    public IReadOnlyList<string> DescribeArguments(StitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return BuildArguments(Path.Combine(request.WorkingDirectory, "segments.txt"), request);
    }

    private static List<string> BuildArguments(string listPath, StitchRequest request)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "concat",

            // Absolute paths are rejected by the demuxer unless safety is relaxed.
            "-safe", "0",
            "-i", listPath,
        };

        var audio = request.Audio;

        if (audio.HasExternalTrack)
        {
            // Seeking the audio input rather than trimming later keeps this a single pass.
            if (audio.Offset > TimeSpan.Zero)
            {
                arguments.AddRange(["-ss", Seconds(audio.Offset)]);
            }

            arguments.AddRange(["-i", audio.ExternalPath!]);

            // The video is still copied, never re-encoded; only the audio is touched.
            //
            // ':a:0' not ':a': the bare specifier matches every audio stream in the input, so a
            // track with commentary or several dubs would contribute all of them, each at -b:a.
            arguments.AddRange(["-map", "0:v", "-map", "1:a:0"]);
            arguments.AddRange(["-c:v", "copy"]);
            arguments.AddRange(["-c:a", "aac"]);
            arguments.AddRange(["-b:a", $"{request.EncoderOptions.AudioBitrateKbps}k"]);
            arguments.AddRange(["-ac", "2"]);

            // Always a chain, because apad is unconditional.
            var audioFilters = new List<string>(2);

            if (audio.AltersVolume)
            {
                audioFilters.Add($"volume={Number(audio.Volume)}");
            }

            // Pads with silence so the track can never be the shorter stream, which would make
            // -shortest below end the file wherever the audio ran out.
            audioFilters.Add("apad");

            arguments.AddRange(["-filter:a", string.Join(',', audioFilters)]);

            // apad leaves the audio effectively endless, so this always ends the file on the
            // video: a longer track is trimmed to the render, a shorter one filled with silence.
            arguments.Add("-shortest");
        }
        else
        {
            arguments.AddRange(["-c", "copy"]);

            if (request.Mute)
            {
                arguments.Add("-an");
            }
        }

        // The concat demuxer happens not to forward chapters today; stated anyway so the output
        // does not depend on that and both stitchers make the same guarantee.
        arguments.AddRange(["-map_chapters", "-1"]);
        arguments.AddRange(["-movflags", "+faststart"]);
        arguments.Add(request.OutputPath);

        return arguments;
    }

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.#####", CultureInfo.InvariantCulture);

    private static string Number(double value) =>
        value.ToString("0.#####", CultureInfo.InvariantCulture);

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
