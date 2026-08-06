using System.Globalization;
using Microsoft.Extensions.Logging;
using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Ffmpeg.Filters;
using ReviewClips.Ffmpeg.Process;

namespace ReviewClips.Ffmpeg.Extraction;

/// <summary>
/// Cuts one segment out of a source file and normalises it to the target format.
/// <para>
/// Every segment is written with identical geometry, frame rate, pixel format and timebase.
/// That uniformity is what lets the stitcher join them with a stream copy instead of a second
/// full re-encode.
/// </para>
/// </summary>
public sealed class FfmpegSegmentExtractor : ISegmentExtractor
{
    private readonly FfmpegRunner _runner;
    private readonly VideoFilterGraphBuilder _graphBuilder;
    private readonly ILogger<FfmpegSegmentExtractor> _logger;

    public FfmpegSegmentExtractor(
        FfmpegRunner runner,
        VideoFilterGraphBuilder graphBuilder,
        ILogger<FfmpegSegmentExtractor> logger)
    {
        _runner = runner;
        _graphBuilder = graphBuilder;
        _logger = logger;
    }

    public async Task ExtractAsync(
        SegmentExtractionRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var arguments = DescribeArguments(request);

        _logger.LogDebug(
            "Extracting {Source} @ {Start}s for {Duration}s",
            Path.GetFileName(request.Segment.SourcePath),
            request.Segment.Start.TotalSeconds,
            request.Segment.Duration.TotalSeconds);

        await _runner.RunFfmpegCheckedAsync(
            arguments,
            request.Segment.Duration,
            progress,
            cancellationToken,
            requireFrames: true);
    }

    public IReadOnlyList<string> DescribeArguments(SegmentExtractionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var segment = request.Segment;
        var look = request.Look;

        // A speed change means the output length differs from the source length consumed.
        // Read speed * duration seconds so the rendered clip still lands on the target length.
        var readDuration = look.HasSpeedChange
            ? TimeSpan.FromSeconds(segment.Duration.TotalSeconds * look.Speed)
            : segment.Duration;

        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };

        if (request.EncoderOptions.HardwareDecode)
        {
            arguments.AddRange(["-hwaccel", "cuda"]);
        }

        // -ss BEFORE -i is an input seek: FFmpeg jumps to the nearest preceding keyframe and
        // decodes forward, discarding frames until the requested timestamp. Combined with
        // re-encoding this is both fast and frame-accurate, so there is no need for the far
        // slower output-side seek.
        arguments.AddRange(["-ss", Seconds(segment.Start)]);
        arguments.AddRange(["-t", Seconds(readDuration)]);
        arguments.AddRange(["-i", segment.SourcePath]);

        var nextInput = 1;

        // Segment audio is wanted, but this particular source has no audio stream. Both stitchers
        // require every segment to carry the same stream layout — the concat demuxer for a clean
        // stream copy, the filter graph because it references [k:a] for each input and cannot bind
        // if one is absent — so silence stands in and keeps the layout uniform. That is what lets
        // a source set mixing silent and sounded files be joined at all.
        //
        // Bounded by the segment's own length rather than readDuration: anullsrc is infinite, and
        // the silence is generated at output rate, so it needs no speed compensation.
        var silenceIndex = default(int?);
        if (!request.Mute && !request.Source.HasAudio)
        {
            arguments.AddRange(
            [
                "-f", "lavfi",
                "-t", Seconds(segment.Duration),
                "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
            ]);

            silenceIndex = nextInput++;
        }

        var context = new FilterContext
        {
            Source = request.Source,
            Format = request.Format,
            Look = look,
            SegmentDuration = segment.Duration,

            // Content-addressed, so the many parallel extractions of one render all resolve to
            // the same file and write it at most once.
            AttributionTextPath = look.HasAttribution
                ? TextResources.Materialise(look.Attribution!.Trim())
                : null,
        };

        var graph = _graphBuilder.Build(context, inputLabel: "0:v", outputLabel: "vout");

        arguments.AddRange(["-filter_complex", graph]);
        arguments.AddRange(["-map", "[vout]"]);

        // Drop the source's chapter markers. FFmpeg would otherwise copy them from the input
        // and merely shift them to the cut, so a three second segment ends up carrying the
        // whole film's chapter list. They describe footage this segment does not contain, and
        // the filter-graph stitcher would then propagate the first segment's copy into the
        // finished file.
        arguments.AddRange(["-map_chapters", "-1"]);

        if (request.Mute)
        {
            arguments.Add("-an");
        }
        else
        {
            arguments.AddRange(["-map", silenceIndex is { } silence ? $"{silence}:a:0" : "0:a:0?"]);
            arguments.AddRange(["-c:a", "aac"]);
            arguments.AddRange(["-b:a", $"{request.EncoderOptions.AudioBitrateKbps}k"]);
            arguments.AddRange(["-ac", "2"]);

            // Not applied to substituted silence: it was generated at the finished length
            // already, so retiming it would make it disagree with the video.
            if (look.HasSpeedChange && silenceIndex is null)
            {
                // atempo only accepts 0.5-2.0 per instance; chain to reach further extremes.
                arguments.AddRange(["-filter:a", BuildAtempoChain(look.Speed)]);
            }
        }

        arguments.AddRange(["-c:v", request.Encoder.VideoEncoder]);
        arguments.AddRange(request.Encoder.QualityArguments);
        arguments.AddRange(request.Encoder.ExtraArguments);
        arguments.AddRange(["-pix_fmt", request.Encoder.PixelFormat]);

        // Force constant frame rate. Variable timestamps survive the fps filter in some
        // containers and then desynchronise the concatenated result.
        arguments.AddRange(["-fps_mode", "cfr"]);
        arguments.AddRange(["-r", Number(request.Format.FrameRate)]);

        // Cut to an exact frame count rather than trusting -t.
        //
        // -ss/-t bound which *source* timestamps are read, and the fps filter then resamples that
        // window onto the output rate. Both steps round outwards, so a segment reliably comes out
        // one to two frames longer than asked for. One frame is invisible; the concat stitcher
        // sums actual segment lengths, so across a 119-slot render the same bias compounds into
        // five seconds of overshoot on a ten-minute request. The filter-graph stitcher is affected
        // differently but no less: it computes xfade offsets from the planned lengths, so segments
        // that disagree with the plan push the schedule out of step.
        //
        // Asking for fewer frames than FFmpeg would have produced is safe: the read window is
        // always the wider of the two, so the frames exist and the surplus is simply not written.
        var frames = FrameCount(segment.Duration, request.Format.FrameRate);
        if (frames > 0)
        {
            arguments.AddRange(["-frames:v", frames.ToString(CultureInfo.InvariantCulture)]);
        }

        // A short, fixed GOP keeps every segment starting on a keyframe, which the concat
        // demuxer requires for a clean stream-copy join.
        var gop = Math.Max((int)Math.Round(request.Format.FrameRate), 1);
        arguments.AddRange(["-g", gop.ToString(CultureInfo.InvariantCulture)]);

        // Uniform timebase across segments; mismatched timebases break concat.
        arguments.AddRange(["-video_track_timescale", "90000"]);
        arguments.AddRange(["-movflags", "+faststart"]);

        arguments.Add(request.OutputPath);
        return arguments;
    }

    /// <summary>
    /// How many output frames a segment of <paramref name="duration"/> should contain at
    /// <paramref name="frameRate"/>. Returns 0 when the frame rate is unusable, which leaves the
    /// caller to omit the cap rather than write a zero-frame file.
    /// </summary>
    internal static int FrameCount(TimeSpan duration, double frameRate)
    {
        if (duration <= TimeSpan.Zero || frameRate <= 0d)
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Round(duration.TotalSeconds * frameRate, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Builds an <c>atempo</c> chain. A single instance is limited to 0.5-2.0, so larger
    /// changes are decomposed into several stages.
    /// </summary>
    internal static string BuildAtempoChain(double speed)
    {
        var remaining = Math.Clamp(speed, 0.125d, 8d);
        var stages = new List<string>();

        while (remaining > 2d)
        {
            stages.Add("atempo=2.0");
            remaining /= 2d;
        }

        while (remaining < 0.5d)
        {
            stages.Add("atempo=0.5");
            remaining /= 0.5d;
        }

        if (Math.Abs(remaining - 1d) > 0.001d)
        {
            stages.Add($"atempo={remaining.ToString("0.#####", CultureInfo.InvariantCulture)}");
        }

        return stages.Count > 0 ? string.Join(',', stages) : "anull";
    }

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.#####", CultureInfo.InvariantCulture);

    private static string Number(double value) =>
        value.ToString("0.#####", CultureInfo.InvariantCulture);
}
