using Microsoft.Extensions.DependencyInjection;
using ReviewClips.Core.Pipeline;
using ReviewClips.Ffmpeg.Analysis;

namespace ReviewClips.Cli.Presentation;

internal static class CachePaths
{
    public static string Describe(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.GetRequiredService<IAnalysisCache>() is JsonAnalysisCache cache
            ? cache.CacheDirectory
            : "(not on disk)";
    }
}
