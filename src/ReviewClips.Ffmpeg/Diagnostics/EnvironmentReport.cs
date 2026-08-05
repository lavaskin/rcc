using ReviewClips.Core.Pipeline;
using ReviewClips.Ffmpeg.Analysis;
using ReviewClips.Ffmpeg.Encoding;
using ReviewClips.Ffmpeg.Process;

namespace ReviewClips.Ffmpeg.Diagnostics;

/// <summary>Whether an external binary could be launched, and what it said its version was.</summary>
public sealed record ToolStatus
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public required bool Available { get; init; }

    /// <summary>Parsed version, e.g. <c>n8.1.2</c>. Null when unavailable or unparseable.</summary>
    public string? Version { get; init; }

    /// <summary>Why the tool could not be run, when it could not.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// The result of a probe-encode for one encoder, plus the flag that selects it.
/// </summary>
public sealed record EncoderStatus
{
    public required string Name { get; init; }

    /// <summary>True only when a real two-frame encode succeeded.</summary>
    public required bool Usable { get; init; }

    /// <summary>The <c>generate</c> flag that would select this encoder.</summary>
    public required string SelectedBy { get; init; }

    /// <summary>
    /// Whether the tool cannot function without it. Only <c>libx264</c> qualifies: it is the
    /// fallback every other path degrades to.
    /// </summary>
    public required bool Required { get; init; }
}

/// <summary>Which of the filters the render graphs depend on are compiled into this FFmpeg.</summary>
public sealed record FilterStatus
{
    public required IReadOnlyList<string> Present { get; init; }

    public required IReadOnlyList<string> Missing { get; init; }

    public bool AllPresent => Missing.Count == 0;
}

/// <summary>Size and population of the on-disk analysis cache.</summary>
public sealed record CacheStatus
{
    public required string Directory { get; init; }

    public required bool Exists { get; init; }

    public required int Entries { get; init; }

    public required long SizeBytes { get; init; }
}

/// <summary>A full picture of what this machine can and cannot do.</summary>
public sealed record EnvironmentReport
{
    public required ToolStatus Ffmpeg { get; init; }

    public required ToolStatus Ffprobe { get; init; }

    public required IReadOnlyList<EncoderStatus> Encoders { get; init; }

    public required FilterStatus Filters { get; init; }

    public required CacheStatus Cache { get; init; }

    /// <summary>
    /// True when a render can actually be produced: both binaries present, every required
    /// filter compiled in, and at least the software fallback encoder working.
    /// </summary>
    public bool IsUsable =>
        Ffmpeg.Available
        && Ffprobe.Available
        && Filters.AllPresent
        && Encoders.Where(e => e.Required).All(e => e.Usable);
}

/// <summary>
/// Builds an <see cref="EnvironmentReport"/> by interrogating the local FFmpeg install.
/// </summary>
public sealed class EnvironmentInspector
{
    /// <summary>
    /// Encoders worth reporting, with the flag that selects each. Ordered software-first so the
    /// row that must work appears first.
    /// </summary>
    private static readonly (string Encoder, string Flag, bool Required)[] KnownEncoders =
    [
        ("libx264", "--encoder x264", true),
        ("libx265", "--encoder x265 --codec hevc", false),
        ("h264_nvenc", "--encoder nvenc", false),
        ("hevc_nvenc", "--encoder nvenc --codec hevc", false),
        ("h264_qsv", "(not yet selectable)", false),
        ("h264_vaapi", "(not yet selectable)", false),
        ("h264_videotoolbox", "(not yet selectable)", false),
    ];

    /// <summary>
    /// Every filter the render graphs actually name. A build missing any of these produces a
    /// confusing mid-render failure, so they are checked up front.
    /// </summary>
    private static readonly string[] RequiredFilters =
    [
        // Look and geometry stages.
        "lutyuv", "gblur", "vignette", "noise", "unsharp", "lut3d", "eq", "hflip", "vflip",
        "zoompan", "overlay", "drawtext", "fade", "pad", "crop", "scale",

        // HDR tone mapping. zscale is the one most often absent, since it needs libzimg.
        "zscale", "setparams", "tonemap",

        // Stitching.
        "xfade", "concat", "acrossfade",

        // Analysis.
        "scdet", "blackdetect", "freezedetect", "signalstats", "metadata",
    ];

    private readonly FfmpegRunner _runner;
    private readonly IEncoderProbe _encoderProbe;
    private readonly IAnalysisCache _cache;

    public EnvironmentInspector(FfmpegRunner runner, IEncoderProbe encoderProbe, IAnalysisCache cache)
    {
        _runner = runner;
        _encoderProbe = encoderProbe;
        _cache = cache;
    }

    public async Task<EnvironmentReport> InspectAsync(CancellationToken cancellationToken)
    {
        var toolset = _runner.Toolset;

        var ffmpeg = await InspectToolAsync("ffmpeg", toolset.FfmpegPath, cancellationToken);
        var ffprobe = await InspectToolAsync("ffprobe", toolset.FfprobePath, cancellationToken);

        // Without ffmpeg there is nothing to probe, and every probe would report a misleading
        // "unusable" rather than the real cause.
        var encoders = ffmpeg.Available
            ? await ProbeEncodersAsync(cancellationToken)
            : KnownEncoders
                .Select(e => new EncoderStatus
                {
                    Name = e.Encoder,
                    Usable = false,
                    SelectedBy = e.Flag,
                    Required = e.Required,
                })
                .ToList();

        var filters = ffmpeg.Available
            ? await InspectFiltersAsync(cancellationToken)
            : new FilterStatus { Present = [], Missing = RequiredFilters };

        return new EnvironmentReport
        {
            Ffmpeg = ffmpeg,
            Ffprobe = ffprobe,
            Encoders = encoders,
            Filters = filters,
            Cache = InspectCache(),
        };
    }

    /// <summary>Deletes the on-disk analysis cache, returning what was removed.</summary>
    public CacheStatus ClearCache()
    {
        var before = InspectCache();

        if (before.Exists)
        {
            Directory.Delete(before.Directory, recursive: true);
        }

        return before;
    }

    public CacheStatus InspectCache()
    {
        var directory = _cache is JsonAnalysisCache disk
            ? disk.CacheDirectory
            : JsonAnalysisCache.DefaultCacheDirectory();

        if (!Directory.Exists(directory))
        {
            return new CacheStatus
            {
                Directory = directory,
                Exists = false,
                Entries = 0,
                SizeBytes = 0,
            };
        }

        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);

        return new CacheStatus
        {
            Directory = directory,
            Exists = true,
            Entries = files.Length,
            SizeBytes = files.Sum(f => SizeOf(f)),
        };
    }

    /// <summary>
    /// Extracts the version from the first line of <c>-version</c> output, which reads
    /// <c>ffmpeg version n8.1.2 Copyright (c) ...</c>. Returns null rather than guessing when
    /// the line does not have that shape.
    /// </summary>
    internal static string? ParseVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var firstLine = output.Split('\n', 2)[0];
        var tokens = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (string.Equals(tokens[i], "version", StringComparison.OrdinalIgnoreCase))
            {
                return tokens[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts filter names from <c>ffmpeg -filters</c>.
    /// <para>
    /// Each filter occupies a line of the form <c>" TSC name  V-&gt;V  description"</c>. The
    /// arrow in the third field is what distinguishes a filter row from the legend FFmpeg
    /// prints above the list, whose rows (<c>"T.. = Timeline support"</c>) are otherwise
    /// shaped identically. Matching on the arrow rather than on a substring search for the
    /// name also avoids <c>unsharp</c> being satisfied by <c>unsharp_opencl</c>.
    /// </para>
    /// </summary>
    internal static IReadOnlyCollection<string> ParseFilterNames(string output)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(output))
        {
            return names;
        }

        foreach (var line in output.Split('\n'))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length >= 3 && tokens[2].Contains("->", StringComparison.Ordinal))
            {
                names.Add(tokens[1]);
            }
        }

        return names;
    }

    private async Task<ToolStatus> InspectToolAsync(
        string name,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            // The same launchability check the render path performs, so a green row here means
            // a render will get past startup.
            await new FfmpegToolset
            {
                FfmpegPath = path,
                FfprobePath = path,
            }.EnsureAvailableAsync(cancellationToken);
        }
        catch (FfmpegNotFoundException ex)
        {
            return new ToolStatus
            {
                Name = name,
                Path = path,
                Available = false,
                Error = ex.Message,
            };
        }

        var result = await _runner.RunAsync(path, ["-version"], null, null, cancellationToken);

        return new ToolStatus
        {
            Name = name,
            Path = path,
            Available = true,
            Version = ParseVersion(result.StandardOutput),
        };
    }

    private async Task<IReadOnlyList<EncoderStatus>> ProbeEncodersAsync(CancellationToken cancellationToken)
    {
        var statuses = new List<EncoderStatus>(KnownEncoders.Length);

        // Sequential on purpose. Probing NVENC concurrently with itself can exhaust the session
        // limit and report a working encoder as broken.
        foreach (var (encoder, flag, required) in KnownEncoders)
        {
            statuses.Add(new EncoderStatus
            {
                Name = encoder,
                Usable = await _encoderProbe.IsUsableAsync(encoder, cancellationToken),
                SelectedBy = flag,
                Required = required,
            });
        }

        return statuses;
    }

    private async Task<FilterStatus> InspectFiltersAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunFfmpegAsync(["-hide_banner", "-filters"], cancellationToken);
        var available = ParseFilterNames(result.StandardOutput);

        return new FilterStatus
        {
            Present = [.. RequiredFilters.Where(available.Contains).Order(StringComparer.Ordinal)],
            Missing = [.. RequiredFilters.Where(f => !available.Contains(f)).Order(StringComparer.Ordinal)],
        };
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            // A cache entry being rewritten by a concurrent run is not worth failing over.
            return 0L;
        }
    }
}
