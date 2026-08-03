using System.CommandLine;
using ReviewClips.Cli.Profiles;
using ReviewClips.Core.Analysis;
using ReviewClips.Core.Options;
using ReviewClips.Core.Sources;

namespace ReviewClips.Cli.Cli;

/// <summary>
/// Turns a parsed command line into a <see cref="ClipRequest"/>.
/// <para>
/// Precedence is explicit and one-directional: built-in defaults, then the named profile,
/// then any option the user actually typed.
/// </para>
/// </summary>
internal sealed class ClipRequestBuilder
{
    private readonly GenerateOptions _options;
    private readonly ProfileLibrary _profiles;
    private readonly SourceResolver _resolver;

    public ClipRequestBuilder(GenerateOptions options, ProfileLibrary profiles, SourceResolver resolver)
    {
        _options = options;
        _profiles = profiles;
        _resolver = resolver;
    }

    public ClipRequest Build(ParseResult parse, string? outputOverride = null)
    {
        ArgumentNullException.ThrowIfNull(parse);

        var inputs = parse.GetValue(_options.Input) ?? [];
        var sources = _resolver.Resolve(inputs);

        // Start from the defaults baked into the domain records.
        var request = new ClipRequest
        {
            Sources = sources,
            OutputPath = "output.mp4",
            TargetDuration = TimeSpan.FromSeconds(60),
        };

        // Layer the profile.
        var profileName = parse.GetValue(_options.Profile);
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            if (!_profiles.TryGet(profileName, out var profile))
            {
                throw new CliUsageException(
                    $"Unknown profile '{profileName}'. Available: {string.Join(", ", _profiles.Names)}.");
            }

            request = profile.ApplyTo(request);
        }

        // Layer explicit CLI values.
        var cues = parse.GetValue(_options.Cues) ?? [];

        var strategy = parse.GetValue(_options.Strategy)
            // Supplying cues without naming the strategy is unambiguous, so infer it.
            ?? (cues.Count > 0 ? SelectionStrategy.Cues : request.Selection.Strategy);

        var selection = request.Selection with
        {
            Strategy = strategy,
            Seed = parse.GetValue(_options.Seed) ?? request.Selection.Seed,
            SkipHead = parse.GetValue(_options.SkipHead) ?? request.Selection.SkipHead,
            SkipTail = parse.GetValue(_options.SkipTail) ?? request.Selection.SkipTail,
            IncludeRanges = Or(parse.GetValue(_options.Range), request.Selection.IncludeRanges),
            ExcludeRanges = Or(parse.GetValue(_options.Exclude), request.Selection.ExcludeRanges),
            ChapterSkip = parse.GetValue(_options.Chapters) ?? request.Selection.ChapterSkip,
            SkipChapterPatterns = Or(parse.GetValue(_options.SkipChapter), request.Selection.SkipChapterPatterns),
            MinGap = parse.GetValue(_options.MinGap) ?? request.Selection.MinGap,
            RejectBlack = !parse.GetValue(_options.NoRejectBlack) && request.Selection.RejectBlack,
            RejectFrozen = !parse.GetValue(_options.NoRejectFrozen) && request.Selection.RejectFrozen,
            Order = parse.GetValue(_options.NoShuffle) ? SegmentOrder.Chronological : request.Selection.Order,
            Cues = cues.Count > 0 ? cues : request.Selection.Cues,
            Scoring = request.Selection.Scoring with
            {
                TargetMotionMultiple = parse.GetValue(_options.MotionTarget)
                    ?? request.Selection.Scoring.TargetMotionMultiple,
                MotionFloorMultiple = parse.GetValue(_options.MotionFloor)
                    ?? request.Selection.Scoring.MotionFloorMultiple,
                MotionCeilingMultiple = parse.GetValue(_options.MotionCeiling)
                    ?? request.Selection.Scoring.MotionCeilingMultiple,
            },
        };

        var format = request.Format;

        if (parse.GetValue(_options.Resolution) is { } size)
        {
            format = format with { Width = size.Width, Height = size.Height };
        }

        // Applied after an explicit resolution so --aspect reshapes it rather than being lost.
        if (parse.GetValue(_options.Aspect) is { } aspect)
        {
            format = format.WithAspect(aspect);
        }

        format = format with
        {
            FrameRate = parse.GetValue(_options.FrameRate) ?? format.FrameRate,
            Fit = parse.GetValue(_options.Fit) ?? format.Fit,
            BlurPadForegroundScale = parse.GetValue(_options.ForegroundScale) ?? format.BlurPadForegroundScale,
            ToneMap = parse.GetValue(_options.ToneMap) ?? format.ToneMap,
            Width = OutputFormat.MakeEven(format.Width),
            Height = OutputFormat.MakeEven(format.Height),
        };

        var look = request.Look with
        {
            Darken = parse.GetValue(_options.Darken) ?? request.Look.Darken,
            Saturation = parse.GetValue(_options.Saturation) ?? request.Look.Saturation,
            Contrast = parse.GetValue(_options.Contrast) ?? request.Look.Contrast,
            Blur = parse.GetValue(_options.Blur) ?? request.Look.Blur,
            Grain = parse.GetValue(_options.Grain) ?? request.Look.Grain,
            Vignette = parse.GetValue(_options.Vignette) || request.Look.Vignette,
            Mirror = parse.GetValue(_options.Mirror) || request.Look.Mirror,
            ZoomEnd = parse.GetValue(_options.Zoom) ?? request.Look.ZoomEnd,
            Speed = parse.GetValue(_options.Speed) ?? request.Look.Speed,
            OverlayPath = parse.GetValue(_options.Overlay) ?? request.Look.OverlayPath,
            OverlayOpacity = parse.GetValue(_options.OverlayOpacity) ?? request.Look.OverlayOpacity,
            LutPath = parse.GetValue(_options.Lut) ?? request.Look.LutPath,
        };

        var transition = request.Transition with
        {
            Kind = parse.GetValue(_options.Transition) ?? request.Transition.Kind,
            Duration = parse.GetValue(_options.TransitionDuration) ?? request.Transition.Duration,
            FadeIn = parse.GetValue(_options.FadeIn) ?? request.Transition.FadeIn,
            FadeOut = parse.GetValue(_options.FadeOut) ?? request.Transition.FadeOut,
        };

        var encoder = request.Encoder with
        {
            Preference = parse.GetValue(_options.Encoder) ?? request.Encoder.Preference,
            Codec = parse.GetValue(_options.Codec) ?? request.Encoder.Codec,
            Quality = parse.GetValue(_options.Quality) ?? request.Encoder.Quality,
            Preset = parse.GetValue(_options.Preset) ?? request.Encoder.Preset,
            HardwareDecode = parse.GetValue(_options.HardwareDecode) || request.Encoder.HardwareDecode,
        };

        var analysis = new AnalysisSettings
        {
            SampleFrameRate = parse.GetValue(_options.AnalysisFps) ?? request.Analysis.SampleFrameRate,
            SceneThreshold = parse.GetValue(_options.SceneThreshold) ?? request.Analysis.SceneThreshold,
            UseHardwareDecode = parse.GetValue(_options.HardwareDecode),
        };

        var duration = parse.GetValue(_options.TargetDuration) ?? request.TargetDuration;
        var splice = parse.GetValue(_options.Splice) ?? request.SpliceLength;

        var output = outputOverride
            ?? parse.GetValue(_options.Output)
            ?? DefaultOutputName(sources, duration);

        var result = request with
        {
            OutputPath = Path.GetFullPath(output),
            TargetDuration = duration,
            SpliceLength = splice,
            SpliceJitter = parse.GetValue(_options.SpliceJitter) ?? request.SpliceJitter,
            MaxDistinctClips = parse.GetValue(_options.MaxClips) ?? request.MaxDistinctClips,
            Selection = selection,
            Format = format,
            Look = look,
            Transition = transition,
            Encoder = encoder,
            Analysis = analysis,
            Mute = !parse.GetValue(_options.Audio) && request.Mute,
            Parallelism = parse.GetValue(_options.Parallelism) ?? request.Parallelism,
            IgnoreAnalysisCache = parse.GetValue(_options.NoCache),
            DryRun = parse.GetValue(_options.DryRun),
            KeepTemporaryFiles = parse.GetValue(_options.KeepTemp),
            ManifestPath = parse.GetValue(_options.Manifest),
        };

        Validate(result);
        return result;
    }

    private static void Validate(ClipRequest request)
    {
        if (request.TargetDuration <= TimeSpan.Zero)
        {
            throw new CliUsageException("--duration must be greater than zero.");
        }

        if (request.SpliceLength <= TimeSpan.Zero)
        {
            throw new CliUsageException("--splice must be greater than zero.");
        }

        if (request.SpliceLength > request.TargetDuration)
        {
            throw new CliUsageException(
                $"--splice ({request.SpliceLength.TotalSeconds:0.#}s) cannot exceed "
                + $"--duration ({request.TargetDuration.TotalSeconds:0.#}s).");
        }

        if (request.Format.FrameRate is <= 0 or > 240)
        {
            throw new CliUsageException("--fps must be between 0 and 240.");
        }

        if (request.MaxDistinctClips is { } cap && cap < 1)
        {
            throw new CliUsageException("--max-clips must be at least 1.");
        }

        if (request.Encoder.Quality is < 0 or > 51)
        {
            throw new CliUsageException("--quality must be between 0 and 51.");
        }

        if (request.Look.Speed <= 0)
        {
            throw new CliUsageException("--speed must be greater than zero.");
        }

        if (request.Selection.Strategy == SelectionStrategy.Cues && request.Selection.Cues.Count == 0)
        {
            throw new CliUsageException("--strategy cues requires at least one timestamp via --cues.");
        }

        if (request.Look.OverlayPath is { } overlay && !File.Exists(overlay))
        {
            throw new CliUsageException($"Overlay image not found: {overlay}");
        }

        if (request.Look.LutPath is { } lut && !File.Exists(lut))
        {
            throw new CliUsageException($"LUT file not found: {lut}");
        }

        var floor = request.Selection.Scoring.MotionFloorMultiple;
        var ceiling = request.Selection.Scoring.MotionCeilingMultiple;
        if (floor >= ceiling)
        {
            throw new CliUsageException(
                $"--motion-floor ({floor:0.##}) must be below --motion-ceiling ({ceiling:0.##}).");
        }
    }

    /// <summary>Derives a sensible output name from the first source, e.g. <c>Alien-bg-90s.mp4</c>.</summary>
    private static string DefaultOutputName(IReadOnlyList<string> sources, TimeSpan duration)
    {
        var stem = Path.GetFileNameWithoutExtension(sources[0]);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "clips";
        }

        return $"{stem}-bg-{(int)duration.TotalSeconds}s.mp4";
    }

    private static IReadOnlyList<T> Or<T>(List<T>? candidate, IReadOnlyList<T> fallback) =>
        candidate is { Count: > 0 } ? candidate : fallback;
}

internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message)
    {
    }

    public CliUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CliUsageException()
    {
    }
}
