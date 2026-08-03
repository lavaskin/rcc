using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReviewClips.Core.Media;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Primitives;
using ReviewClips.Ffmpeg.Process;

namespace ReviewClips.Ffmpeg.Probe;

public sealed class FfprobeMediaProbe : IMediaProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly FfmpegRunner _runner;
    private readonly ILogger<FfprobeMediaProbe> _logger;

    public FfprobeMediaProbe(FfmpegRunner runner, ILogger<FfprobeMediaProbe> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Source file not found: {full}", full);
        }

        string[] arguments =
        [
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            full,
        ];

        var result = await _runner.RunFfprobeAsync(arguments, cancellationToken);
        if (!result.Success)
        {
            throw new FfmpegExecutionException(
                _runner.Toolset.FfprobePath,
                arguments,
                result.ExitCode,
                result.StandardError);
        }

        FfprobeOutput? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<FfprobeOutput>(result.StandardOutput, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FfmpegExecutionException($"Could not parse ffprobe output for '{full}'.", ex);
        }

        if (parsed is null)
        {
            throw new FfmpegExecutionException($"ffprobe returned no data for '{full}'.");
        }

        // Skip cover art, which ffprobe also reports with codec_type=video.
        var video = parsed.Streams.FirstOrDefault(s => s.IsVideo && !s.IsAttachedPicture)
            ?? throw new FfmpegExecutionException($"'{full}' contains no video stream.");

        var fileInfo = new FileInfo(full);
        var duration = ResolveDuration(parsed, video);
        var frameRate = ResolveFrameRate(video, duration);

        var info = new MediaInfo
        {
            Path = full,
            FileSizeBytes = fileInfo.Length,
            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
            Duration = duration,
            Width = video.Width ?? 0,
            Height = video.Height ?? 0,
            FrameRate = frameRate,
            VideoCodec = video.CodecName ?? "unknown",
            PixelFormat = video.PixelFormat ?? "unknown",
            SampleAspectRatio = ParseRatio(video.SampleAspectRatio) ?? Ratio.One,
            HasAudio = parsed.Streams.Any(s => s.IsAudio),
            ColorTransfer = video.ColorTransfer,
            ColorPrimaries = video.ColorPrimaries,
            ColorSpace = video.ColorSpace,
        };

        if (info.IsHdr)
        {
            _logger.LogInformation(
                "{File} is HDR ({Transfer}); tone-mapping will be applied",
                Path.GetFileName(full),
                info.ColorTransfer);
        }

        return info;
    }

    /// <summary>
    /// Prefers the container duration, which is reliable; falls back to the stream duration.
    /// </summary>
    private static TimeSpan ResolveDuration(FfprobeOutput output, FfprobeStream video)
    {
        if (TryParseSeconds(output.Format?.Duration, out var formatDuration))
        {
            return formatDuration;
        }

        return TryParseSeconds(video.Duration, out var streamDuration)
            ? streamDuration
            : TimeSpan.Zero;
    }

    /// <summary>
    /// Resolves a usable frame rate. <c>avg_frame_rate</c> is preferred because it is correct
    /// for variable-frame-rate sources, where <c>r_frame_rate</c> reports the peak instead.
    /// </summary>
    private static double ResolveFrameRate(FfprobeStream video, TimeSpan duration)
    {
        if (TryParseRational(video.AvgFrameRate, out var average) && average is > 0 and < 1000)
        {
            return average;
        }

        if (TryParseRational(video.RFrameRate, out var nominal) && nominal is > 0 and < 1000)
        {
            return nominal;
        }

        // Last resort: derive from the frame count.
        if (duration > TimeSpan.Zero
            && long.TryParse(video.FrameCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames)
            && frames > 0)
        {
            return frames / duration.TotalSeconds;
        }

        return 25d;
    }

    internal static bool TryParseRational(string? text, out double value)
    {
        value = 0d;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('/');
        if (parts.Length == 1)
        {
            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            || denominator == 0)
        {
            return false;
        }

        value = numerator / denominator;
        return true;
    }

    internal static Ratio? ParseRatio(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "N/A")
        {
            return null;
        }

        // ffprobe emits 0:1 for "unspecified", which must not be treated as a real ratio.
        return Ratio.TryParse(text, out var ratio, out _) && ratio.IsValid ? ratio : null;
    }

    private static bool TryParseSeconds(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)
            || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || double.IsNaN(seconds)
            || seconds <= 0)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
