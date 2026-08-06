using System.Globalization;
using Microsoft.Extensions.Logging;
using ReviewClips.Core.Analysis;
using ReviewClips.Core.Media;
using ReviewClips.Core.Pipeline;
using ReviewClips.Ffmpeg.Process;

namespace ReviewClips.Ffmpeg.Analysis;

/// <summary>
/// Scans a source in a single FFmpeg pass, extracting scene cuts, a motion curve, and the
/// black and frozen stretches.
/// <para>
/// One pass suffices because <c>scdet</c> exports <c>lavfi.scd.mafd</c> for every frame, which
/// doubles as a motion-energy metric. The stream is decimated and downscaled first.
/// </para>
/// <para>
/// Output routing is specific: per-frame metadata streams over stdout, while the detectors
/// (<c>scdet</c>, <c>blackdetect</c>, <c>freezedetect</c>) report on stderr at <c>info</c>
/// level. Progress is derived from the metadata timestamps rather than <c>-progress</c>,
/// which would otherwise contend for stdout.
/// </para>
/// </summary>
public sealed class FfmpegMediaAnalyzer : IMediaAnalyzer
{
    private readonly FfmpegRunner _runner;
    private readonly ILogger<FfmpegMediaAnalyzer> _logger;

    public FfmpegMediaAnalyzer(FfmpegRunner runner, ILogger<FfmpegMediaAnalyzer> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<MediaAnalysis> AnalyzeAsync(
        MediaInfo info,
        AnalysisSettings settings,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(settings);

        var logParser = new AnalysisLogParser();
        var metadata = new MetadataStreamParser();
        var arguments = BuildArguments(info, settings);

        _logger.LogDebug("Analysing {Path}", info.Path);

        var lastReported = -1d;

        void OnMetadataLine(string line)
        {
            metadata.Feed(line);

            if (progress is null || info.Duration <= TimeSpan.Zero)
            {
                return;
            }

            if (metadata.CurrentTimeSeconds is not { } seconds)
            {
                return;
            }

            var fraction = Math.Clamp(seconds / info.Duration.TotalSeconds, 0d, 1d);

            // Throttle: this fires once per sampled frame, thousands of times per film.
            if (fraction - lastReported < 0.01d)
            {
                return;
            }

            lastReported = fraction;
            progress.Report(fraction);
        }

        var result = await _runner.RunAsync(
            _runner.Toolset.FfmpegPath,
            arguments,
            onStandardOutputLine: OnMetadataLine,
            onStandardErrorLine: logParser.Feed,
            cancellationToken);

        if (!result.Success)
        {
            throw new FfmpegExecutionException(
                _runner.Toolset.FfmpegPath,
                arguments,
                result.ExitCode,
                result.StandardError);
        }

        logParser.Complete(info.Duration);

        var motion = metadata.ToCurve();

        if (motion.Count == 0)
        {
            _logger.LogWarning(
                "No motion samples were captured for {File}; scored selection will fall back to neutral",
                Path.GetFileName(info.Path));
        }

        _logger.LogInformation(
            "Analysed {File}: {Cuts} cuts, {Black} black, {Freeze} frozen, {Samples} motion samples",
            Path.GetFileName(info.Path),
            logParser.SceneCuts.Count,
            logParser.BlackRanges.Count,
            logParser.FreezeRanges.Count,
            motion.Count);

        progress?.Report(1d);

        return new MediaAnalysis
        {
            SourcePath = info.Path,
            SourceSizeBytes = info.FileSizeBytes,
            SourceModifiedUtc = info.LastModifiedUtc,
            Duration = info.Duration,
            SceneCuts = logParser.SceneCuts.OrderBy(c => c).ToList(),
            BlackRanges = logParser.BlackRanges,
            FreezeRanges = logParser.FreezeRanges,
            Motion = motion,
            Settings = settings,
        };
    }

    /// <summary>Builds the analysis command. Exposed for dry-run reporting and tests.</summary>
    public static List<string> BuildArguments(MediaInfo info, AnalysisSettings settings)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(settings);

        var arguments = new List<string> { "-hide_banner", "-nostats" };

        if (settings.UseHardwareDecode)
        {
            // CUDA context setup costs more than it saves on short inputs, hence opt-in.
            arguments.AddRange(["-hwaccel", "cuda"]);
        }

        arguments.AddRange(["-i", info.Path]);

        // Drop everything except video; decoding audio and subtitles is pure waste here.
        arguments.AddRange(["-an", "-sn", "-dn"]);
        arguments.AddRange(["-vf", BuildFilterChain(settings)]);
        arguments.AddRange(["-f", "null", "-"]);

        return arguments;
    }

    internal static string BuildFilterChain(AnalysisSettings settings)
    {
        var fps = Math.Max(settings.SampleFrameRate, 1);
        var width = Math.Max(settings.SampleWidth, 64);

        // Order matters: decimate and downscale first so every detector downstream works on
        // the cheapest possible stream. metadata=print must come last to capture the metadata
        // that scdet and freezedetect attach to each frame.
        return string.Join(
            ',',
            $"fps={fps}",
            $"scale={width}:-2",
            $"scdet=threshold={Number(settings.SceneThreshold)}",
            $"freezedetect=n={settings.FreezeNoiseTolerance}:d={Seconds(settings.FreezeMinDuration)}",
            $"blackdetect=d={Seconds(settings.BlackMinDuration)}"
            + $":pic_th=0.98:pix_th={Number(settings.BlackPixelThreshold)}",

            // '-' means stdout. See the class remarks for why this must not be a file.
            "metadata=print:file=-");
    }

    private static string Number(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
