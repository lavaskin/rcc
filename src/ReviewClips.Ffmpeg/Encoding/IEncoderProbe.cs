using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ReviewClips.Ffmpeg.Process;

namespace ReviewClips.Ffmpeg.Encoding;

/// <summary>
/// Answers whether a named encoder actually works on this machine.
/// <para>
/// Presence in <c>ffmpeg -encoders</c> is not sufficient: NVENC is compiled into most builds but
/// fails at runtime without a suitable driver or GPU, or when every session is already in use, so
/// the only reliable test is to encode something. Shared by the encoder selector and by
/// <c>rcc doctor</c>, so the diagnostic reports what a render will really do.
/// </para>
/// </summary>
public interface IEncoderProbe
{
    Task<bool> IsUsableAsync(string encoder, CancellationToken cancellationToken);
}

/// <summary>Probes an encoder by encoding two synthetic frames and discarding them.</summary>
public sealed class FfmpegEncoderProbe : IEncoderProbe
{
    private readonly FfmpegRunner _runner;
    private readonly ILogger<FfmpegEncoderProbe> _logger;

    // Plain concurrent map rather than a lock: a duplicate probe under a race is cheap and
    // harmless, whereas caching a Task would let one caller's cancellation poison the result.
    private readonly ConcurrentDictionary<string, bool> _results = new(StringComparer.Ordinal);

    public FfmpegEncoderProbe(FfmpegRunner runner, ILogger<FfmpegEncoderProbe> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<bool> IsUsableAsync(string encoder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoder);

        if (_results.TryGetValue(encoder, out var cached))
        {
            return cached;
        }

        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi",
            "-i", "testsrc2=size=256x144:rate=25",
            "-frames:v", "2",
            "-c:v", encoder,
            "-f", "null", "-",
        ];

        bool usable;
        try
        {
            var result = await _runner.RunFfmpegAsync(arguments, cancellationToken);
            usable = result.Success;

            if (!usable)
            {
                _logger.LogDebug("Probe for {Encoder} failed: {Error}", encoder, result.StandardError);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Probe for {Encoder} threw", encoder);
            usable = false;
        }

        _results[encoder] = usable;
        return usable;
    }
}
