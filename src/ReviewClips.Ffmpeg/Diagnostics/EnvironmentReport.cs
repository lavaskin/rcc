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

/// <summary>The result of a probe-encode for one encoder, plus the flag that selects it.</summary>
public sealed record EncoderStatus
{
    public required string Name { get; init; }

    /// <summary>True only when a real two-frame encode succeeded.</summary>
    public required bool Usable { get; init; }

    /// <summary>
    /// True when the probe was abandoned rather than answered.
    /// <para>
    /// Separate from <see cref="Usable"/> because the remedies differ: a failed probe means this
    /// FFmpeg build cannot do it, a timed-out probe means the driver stopped responding.
    /// </para>
    /// </summary>
    public bool TimedOut { get; init; }

    /// <summary>The <c>generate</c> flag that would select this encoder.</summary>
    public required string SelectedBy { get; init; }

    /// <summary>
    /// Whether the tool cannot function without it. Only <c>libx264</c> qualifies: it is the
    /// fallback every other path degrades to.
    /// </summary>
    public required bool Required { get; init; }
}

/// <summary>An optional filter that is absent, and the feature that consequently will not work.</summary>
public sealed record MissingFeature
{
    public required string Filter { get; init; }

    /// <summary>What stops working, phrased for someone reading a diagnostic.</summary>
    public required string Feature { get; init; }
}

/// <summary>
/// Which of the filters the render graphs depend on are compiled into this FFmpeg.
/// <para>
/// Two tiers, mirroring <see cref="EncoderStatus.Required"/>. Only filters a default render
/// cannot avoid count against usability: <c>zscale</c> needs libzimg and is the component most
/// often left out, notably on Alpine and minimal container images, yet its absence costs only
/// HDR tone-mapping and leaves every SDR render working.
/// </para>
/// </summary>
public sealed record FilterStatus
{
    public required IReadOnlyList<string> Present { get; init; }

    /// <summary>Absent filters that no render can avoid. Any entry here is fatal.</summary>
    public required IReadOnlyList<string> Missing { get; init; }

    /// <summary>Absent filters that only gate an opt-in feature.</summary>
    public required IReadOnlyList<MissingFeature> MissingOptional { get; init; }

    /// <summary>True when nothing at all is absent.</summary>
    public bool AllPresent => Missing.Count == 0 && MissingOptional.Count == 0;

    /// <summary>True when every filter a default render needs is compiled in.</summary>
    public bool RequiredPresent => Missing.Count == 0;
}

/// <summary>
/// Whether <c>--attribution</c> will actually work.
/// <para>
/// Two conditions, not one: <c>drawtext</c> must be compiled in, and fontconfig must resolve a
/// default family, because rcc deliberately does not pin a <c>fontfile</c> and so takes whatever
/// the system uses. A container can have libfreetype and no installed font, at which point the
/// filter exists and still fails.
/// </para>
/// </summary>
public sealed record TextRenderStatus
{
    /// <summary>Whether the <c>drawtext</c> filter is compiled in at all.</summary>
    public required bool FilterPresent { get; init; }

    /// <summary>True only when a real one-frame caption was drawn.</summary>
    public required bool Usable { get; init; }

    public string? Error { get; init; }
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

    public required TextRenderStatus Text { get; init; }

    public required CacheStatus Cache { get; init; }

    /// <summary>
    /// True when a render can actually be produced: both binaries present, every filter a
    /// default render needs compiled in, and at least the software fallback encoder working.
    /// <para>
    /// Deliberately indifferent to <see cref="FilterStatus.MissingOptional"/> and to
    /// <see cref="Text"/>: those gate individual opt-in flags and do not stop the tool working.
    /// </para>
    /// </summary>
    public bool IsUsable =>
        Ffmpeg.Available
        && Ffprobe.Available
        && Filters.RequiredPresent
        && Encoders.Where(e => e.Required).All(e => e.Usable);

    /// <summary>True when something works but not everything does.</summary>
    public bool HasLimitations =>
        IsUsable && (Filters.MissingOptional.Count > 0 || !Text.Usable);
}

/// <summary>Builds an <see cref="EnvironmentReport"/> by interrogating the local FFmpeg install.</summary>
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
    /// Filters a render cannot avoid, whatever the flags. Absence is fatal, and reported up
    /// front rather than as a failure partway through a long render.
    /// </summary>
    private static readonly string[] RequiredFilters =
    [
        // Geometry and grading applied to every segment.
        "scale", "crop", "pad", "eq", "lutyuv", "fade", "overlay",

        // Stitching. xfade is here rather than below because the default transition uses it.
        "concat", "xfade", "acrossfade",

        // Analysis.
        "scdet", "blackdetect", "freezedetect", "signalstats", "metadata",
    ];

    /// <summary>
    /// Filters that gate one opt-in feature each. Absence limits the tool rather than breaking
    /// it, so each is reported with the flag it disables, not as an error.
    /// </summary>
    private static readonly (string Filter, string Feature)[] OptionalFilters =
    [
        // libzimg. See FilterStatus.
        ("zscale", "HDR tone mapping (--tonemap)"),
        ("tonemap", "HDR tone mapping (--tonemap)"),
        ("setparams", "HDR tone mapping (--tonemap)"),

        // libfreetype.
        ("drawtext", "burned-in credit lines (--attribution)"),

        ("gblur", "blur (--blur, and the --fit blur-pad used by --profile shorts)"),
        ("unsharp", "sharpening (--sharpen)"),
        ("vignette", "vignette (--vignette)"),
        ("noise", "film grain (--grain)"),
        ("lut3d", "3D LUTs (--lut)"),
        ("zoompan", "Ken Burns zoom (--zoom)"),
        ("hflip", "horizontal mirroring (--mirror)"),
        ("vflip", "vertical flipping (--flip)"),
    ];

    /// <summary>Every filter checked, in either tier.</summary>
    private static IEnumerable<string> AllFilters =>
        RequiredFilters.Concat(OptionalFilters.Select(f => f.Filter));

    /// <summary>
    /// The optional filters and the feature text each is reported with, present or not.
    /// <para>
    /// The feature text names command-line flags, but they are plain strings in a different
    /// assembly from the option definitions, so nothing keeps them correct on their own.
    /// Exposing the list lets the CLI's tests check every flag named here against the options
    /// that actually exist.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MissingFeature> OptionalFeatures =>
        [.. OptionalFilters.Select(f => new MissingFeature { Filter = f.Filter, Feature = f.Feature })];

    /// <summary>
    /// How long any single probe may take before it is abandoned.
    /// <para>
    /// An unresponsive GPU driver does not fail a probe, it stops answering, and <c>doctor</c> is
    /// what you run on a broken environment. Generous next to a healthy probe, which encodes two
    /// frames in well under a second. Applied per probe rather than to the run as a whole, so one
    /// unresponsive encoder costs its own row rather than the rest of the report.
    /// </para>
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

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
        // "unusable" instead of the real cause.
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
            : new FilterStatus
            {
                Present = [],
                Missing = RequiredFilters,
                MissingOptional =
                [
                    .. OptionalFilters.Select(f => new MissingFeature
                    {
                        Filter = f.Filter,
                        Feature = f.Feature,
                    }),
                ],
            };

        // Only worth probing when drawtext exists; otherwise the filter row already explains it.
        var text = filters.Present.Contains("drawtext")
            ? await InspectTextRenderingAsync(cancellationToken)
            : new TextRenderStatus
            {
                FilterPresent = false,
                Usable = false,
                Error = "the drawtext filter is not compiled into this FFmpeg",
            };

        return new EnvironmentReport
        {
            Ffmpeg = ffmpeg,
            Ffprobe = ffprobe,
            Encoders = encoders,
            Filters = filters,
            Text = text,
            Cache = InspectCache(),
        };
    }

    /// <summary>
    /// Deletes the cache entries, returning what was removed.
    /// <para>
    /// Deliberately not <c>Directory.Delete(recursive: true)</c>: the directory comes from
    /// <c>Cache:Directory</c> and is used verbatim, and <c>--clear-cache</c> asks for no
    /// confirmation, so removing only the files this cache owns keeps the blast radius matching
    /// the promise. The directory itself is left in place because the next run needs it.
    /// </para>
    /// </summary>
    public CacheStatus ClearCache()
    {
        var before = InspectCache();

        if (!before.Exists)
        {
            return before;
        }

        var removed = 0;
        var freed = 0L;

        foreach (var file in EnumerateEntries(before.Directory))
        {
            var size = SizeOf(file);

            try
            {
                File.Delete(file);
                removed++;
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked entry is no reason to abandon the rest; the returned totals report
                // what was actually removed rather than what was found.
            }
        }

        return before with { Entries = removed, SizeBytes = freed };
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

        var files = EnumerateEntries(directory);

        return new CacheStatus
        {
            Directory = directory,
            Exists = true,
            Entries = files.Count,
            SizeBytes = files.Sum(SizeOf),
        };
    }

    /// <summary>
    /// The files this cache owns, and the only ones <see cref="ClearCache"/> will remove.
    /// <para>
    /// Shared by reporting and clearing so the two cannot describe different sets.
    /// <see cref="JsonAnalysisCache"/> writes <c>&lt;name&gt;.&lt;hash&gt;.json</c> flat in this
    /// directory, plus a <c>.tmp</c> staging file it normally moves away; anything else in there
    /// belongs to somebody else and is left alone.
    /// </para>
    /// </summary>
    private static List<string> EnumerateEntries(string directory)
    {
        try
        {
            return
            [
                .. Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly),
                .. Directory.EnumerateFiles(directory, "*.json.tmp", SearchOption.TopDirectoryOnly),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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
    /// Rows read <c>" TSC name  V-&gt;V  description"</c>. The arrow in the third field is what
    /// distinguishes a filter row from the identically shaped legend FFmpeg prints above the list
    /// (<c>"T.. = Timeline support"</c>). Matching on it rather than substring-searching for the
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
        FfmpegRunResult result;

        try
        {
            // One launch of this binary serves as both the launchability check and the version
            // source. FfmpegToolset.EnsureAvailableAsync would instead verify whichever pair that
            // toolset was configured with rather than `path`, and runs -version once per property.
            // Bounded like the encoder probes: a binary on an unresponsive network mount hangs
            // here, before anything has been printed.
            result = await WithTimeoutAsync(
                token => _runner.RunAsync(path, ["-version"], null, null, token),
                onTimeout: (FfmpegRunResult?)null,
                map: r => (FfmpegRunResult?)r,
                cancellationToken)
                ?? throw new TimeoutException(
                    $"'{path} -version' did not respond within {ProbeTimeout.TotalSeconds:0}s.");
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
        catch (TimeoutException ex)
        {
            return new ToolStatus
            {
                Name = name,
                Path = path,
                Available = false,
                Error = ex.Message,
            };
        }

        // Launching is not the same as working: a build missing a shared library, or one for the
        // wrong architecture, starts and then exits non-zero.
        if (!result.Success)
        {
            return new ToolStatus
            {
                Name = name,
                Path = path,
                Available = false,
                Error = $"'{path} -version' exited with code {result.ExitCode}. {LastLine(result.StandardError)}".TrimEnd(),
            };
        }

        return new ToolStatus
        {
            Name = name,
            Path = path,
            Available = true,
            Version = ParseVersion(result.StandardOutput),
        };
    }

    /// <summary>
    /// The last non-empty line of stderr, where the cause sits. Kept to one line because the
    /// doctor table renders <see cref="ToolStatus.Error"/> inline.
    /// </summary>
    private static string LastLine(string standardError) =>
        standardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(string.Empty);

    private async Task<IReadOnlyList<EncoderStatus>> ProbeEncodersAsync(CancellationToken cancellationToken)
    {
        var statuses = new List<EncoderStatus>(KnownEncoders.Length);

        // Sequential on purpose. Probing NVENC concurrently with itself can exhaust the session
        // limit and report a working encoder as broken.
        foreach (var (encoder, flag, required) in KnownEncoders)
        {
            var outcome = await WithTimeoutAsync(
                token => _encoderProbe.IsUsableAsync(encoder, token),
                onTimeout: (Usable: false, TimedOut: true),
                map: usable => (Usable: usable, TimedOut: false),
                cancellationToken);

            statuses.Add(new EncoderStatus
            {
                Name = encoder,
                Usable = outcome.Usable,
                TimedOut = outcome.TimedOut,
                SelectedBy = flag,
                Required = required,
            });
        }

        return statuses;
    }

    /// <summary>
    /// Runs one probe under <see cref="ProbeTimeout"/>, substituting a result if it overruns.
    /// <para>
    /// Only the linked timeout is absorbed; the caller's own cancellation must propagate as a
    /// cancellation rather than be reported as a timed-out probe.
    /// </para>
    /// </summary>
    private static async Task<TResult> WithTimeoutAsync<T, TResult>(
        Func<CancellationToken, Task<T>> probe,
        TResult onTimeout,
        Func<T, TResult> map,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            return map(await probe(timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return onTimeout;
        }
    }

    private async Task<FilterStatus> InspectFiltersAsync(CancellationToken cancellationToken)
    {
        // On a timeout, treat every filter as absent: the required ones are reported missing and
        // IsUsable goes false, the honest reading of "FFmpeg would not tell us".
        var output = await WithTimeoutAsync(
            token => _runner.RunFfmpegAsync(["-hide_banner", "-filters"], token),
            onTimeout: string.Empty,
            map: r => r.StandardOutput,
            cancellationToken);

        var available = ParseFilterNames(output);

        return new FilterStatus
        {
            Present = [.. AllFilters.Where(available.Contains).Order(StringComparer.Ordinal)],
            Missing = [.. RequiredFilters.Where(f => !available.Contains(f)).Order(StringComparer.Ordinal)],
            MissingOptional =
            [
                .. OptionalFilters
                    .Where(f => !available.Contains(f.Filter))
                    .OrderBy(f => f.Filter, StringComparer.Ordinal)
                    .Select(f => new MissingFeature { Filter = f.Filter, Feature = f.Feature }),
            ],
        };
    }

    /// <summary>
    /// Draws one frame of text over a synthetic source. Same shape as the encoder probe and for
    /// the same reason: a compiled-in <c>drawtext</c> can still fail here. See
    /// <see cref="TextRenderStatus"/>.
    /// </summary>
    private async Task<TextRenderStatus> InspectTextRenderingAsync(CancellationToken cancellationToken)
    {
        // Plain ASCII with no reserved characters, so nothing here depends on escaping.
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi",
            "-i", "color=c=black:s=128x72:d=1",
            "-vf", "drawtext=text=rcc:fontcolor=white:fontsize=16",
            "-frames:v", "1",
            "-f", "null", "-",
        ];

        try
        {
            // Bounded like the encoder probes, with a reason of its own: with no fontfile pinned
            // this goes through fontconfig, which builds its cache on first use and is then the
            // slowest thing doctor does.
            var result = await WithTimeoutAsync(
                token => _runner.RunFfmpegAsync(arguments, token),
                onTimeout: (FfmpegRunResult?)null,
                map: r => (FfmpegRunResult?)r,
                cancellationToken);

            if (result is null)
            {
                return new TextRenderStatus
                {
                    FilterPresent = true,
                    Usable = false,
                    Error = $"drawtext did not respond within {ProbeTimeout.TotalSeconds:0}s, "
                        + "which usually means fontconfig is building its cache or cannot read a "
                        + "font directory.",
                };
            }

            return new TextRenderStatus
            {
                FilterPresent = true,
                Usable = result.Success,
                Error = result.Success ? null : Summarise(result.StandardError),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TextRenderStatus
            {
                FilterPresent = true,
                Usable = false,
                Error = ex.Message,
            };
        }
    }

    /// <summary>First non-empty line of an FFmpeg error, which is the part worth showing.</summary>
    private static string Summarise(string standardError)
    {
        var line = standardError
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);

        return string.IsNullOrEmpty(line) ? "no default font could be resolved" : line;
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
