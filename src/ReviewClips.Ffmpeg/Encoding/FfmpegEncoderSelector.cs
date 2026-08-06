using System.Globalization;
using Microsoft.Extensions.Logging;
using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Ffmpeg.Process;

namespace ReviewClips.Ffmpeg.Encoding;

/// <summary>
/// Chooses a video encoder by actually trying it.
/// <para>
/// Presence in <c>ffmpeg -encoders</c> is not sufficient: NVENC is compiled into most builds
/// but fails at runtime without a suitable driver or GPU, or when all sessions are in use.
/// A two-frame probe encode is the only reliable test, which is what <see cref="IEncoderProbe"/>
/// does, once, cached. <c>rcc doctor</c> shares that probe, so its report and this selection
/// can never disagree.
/// </para>
/// </summary>
public sealed class FfmpegEncoderSelector : IEncoderSelector
{
    private readonly IEncoderProbe _probe;
    private readonly ILogger<FfmpegEncoderSelector> _logger;

    public FfmpegEncoderSelector(IEncoderProbe probe, ILogger<FfmpegEncoderSelector> logger)
    {
        _probe = probe;
        _logger = logger;
    }

    public async Task<EncoderProfile> SelectAsync(
        EncoderOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        switch (options.Preference)
        {
            case EncoderPreference.X264:
                return Software("libx264", options);

            case EncoderPreference.X265:
                return Software("libx265", options);

            case EncoderPreference.Nvenc:
                {
                    var name = NvencName(options.Codec);
                    if (!await _probe.IsUsableAsync(name, cancellationToken))
                    {
                        throw new FfmpegExecutionException(
                            $"'{name}' was requested but is not usable on this machine. "
                            + "Check the NVIDIA driver, or use --encoder auto.");
                    }

                    return Hardware(name, options);
                }

            case EncoderPreference.Auto:
            default:
                {
                    var name = NvencName(options.Codec);
                    if (await _probe.IsUsableAsync(name, cancellationToken))
                    {
                        _logger.LogInformation("Using hardware encoder {Encoder}", name);
                        return Hardware(name, options);
                    }

                    var fallback = options.Codec == VideoCodecKind.Hevc ? "libx265" : "libx264";
                    _logger.LogInformation("Hardware encoding unavailable; using {Encoder}", fallback);
                    return Software(fallback, options);
                }
        }
    }

    private static string NvencName(VideoCodecKind codec) =>
        codec == VideoCodecKind.Hevc ? "hevc_nvenc" : "h264_nvenc";

    private static EncoderProfile Software(string encoder, EncoderOptions options) => new()
    {
        VideoEncoder = encoder,
        IsHardware = false,
        QualityArguments =
        [
            "-crf", options.Quality.ToString(CultureInfo.InvariantCulture),
            "-preset", options.Preset ?? "medium",
        ],
        ExtraArguments = [],
    };

    private static EncoderProfile Hardware(string encoder, EncoderOptions options) => new()
    {
        VideoEncoder = encoder,
        IsHardware = true,
        QualityArguments =
        [
            // Constant-quality NVENC: VBR rate control with a target CQ and no bitrate cap.
            // Omitting '-b:v 0' makes NVENC honour a default bitrate instead of the CQ value.
            "-rc", "vbr",
            "-cq", options.Quality.ToString(CultureInfo.InvariantCulture),
            "-b:v", "0",
            "-preset", options.Preset ?? "p5",
        ],
        ExtraArguments = [],
    };
}
