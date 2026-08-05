using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReviewClips.Cli.Profiles;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Selection;
using ReviewClips.Core.Sources;
using ReviewClips.Ffmpeg.Analysis;
using ReviewClips.Ffmpeg.Diagnostics;
using ReviewClips.Ffmpeg.Encoding;
using ReviewClips.Ffmpeg.Extraction;
using ReviewClips.Ffmpeg.Filters;
using ReviewClips.Ffmpeg.Probe;
using ReviewClips.Ffmpeg.Process;
using ReviewClips.Ffmpeg.Stitching;
using Spectre.Console;

namespace ReviewClips.Cli;

internal static class CompositionRoot
{
    public static ServiceProvider Build(bool verbose)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile(UserConfigPath(), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("RCC_")
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);

        services.AddLogging(builder =>
        {
            builder.ClearProviders();

            // Logs are diagnostics; normal user-facing output goes through Spectre. At default
            // verbosity only warnings and errors are shown so they don't fight the progress lines.
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = verbose ? "HH:mm:ss.fff " : null;
            });

            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);
        });

        services.AddSingleton(AnsiConsole.Console);

        // FFmpeg locations are overridable for non-standard installs.
        services.AddSingleton(_ => new FfmpegToolset
        {
            FfmpegPath = configuration["Ffmpeg:FfmpegPath"] ?? "ffmpeg",
            FfprobePath = configuration["Ffmpeg:FfprobePath"] ?? "ffprobe",
        });

        services.AddSingleton<FfmpegRunner>();
        services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
        services.AddSingleton<IMediaAnalyzer, FfmpegMediaAnalyzer>();
        services.AddSingleton<IEncoderProbe, FfmpegEncoderProbe>();
        services.AddSingleton<IEncoderSelector, FfmpegEncoderSelector>();
        services.AddSingleton<ISegmentExtractor, FfmpegSegmentExtractor>();

        services.AddSingleton<IAnalysisCache>(provider => new JsonAnalysisCache(
            configuration["Cache:Directory"],
            provider.GetRequiredService<ILogger<JsonAnalysisCache>>()));

        // Registration order is significant: the pipeline picks the first stitcher that can
        // handle a request, so the stream-copy fast path must be offered before the
        // general-purpose filter-graph one.
        services.AddSingleton<IStitcher, ConcatDemuxerStitcher>();
        services.AddSingleton<IStitcher, FilterGraphStitcher>();

        services.AddSingleton<EnvironmentInspector>();

        services.AddSingleton(_ => VideoFilterGraphBuilder.CreateDefault());
        services.AddSingleton<ISegmentSelectorFactory>(_ => SegmentSelectorFactory.CreateDefault());
        services.AddSingleton(_ => new SourceResolver());
        services.AddSingleton<RenderPipeline>();

        services.AddSingleton(_ => new ProfileLibrary(
            configuration.GetSection("Profiles").Get<List<RenderProfile>>()));

        return services.BuildServiceProvider();
    }

    private static string UserConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "reviewclips", "appsettings.json");
    }
}
