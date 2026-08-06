using System.CommandLine;
using System.Globalization;
using System.Text;
using ReviewClips.Cli.Profiles;
using ReviewClips.Core.Analysis;
using ReviewClips.Core.Options;
using ReviewClips.Core.Planning;
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
    /// <summary>
    /// Everything a request is before any profile or CLI value touches it. Used by
    /// <see cref="DeriveOutputName"/> to decide which settings are unusual enough to name a file after.
    /// </summary>
    private static readonly ClipRequest Defaults = new()
    {
        Sources = [],
        OutputPath = string.Empty,
        TargetDuration = TimeSpan.FromSeconds(60),
    };

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
            Gamma = parse.GetValue(_options.Gamma) ?? request.Look.Gamma,

            // An explicit --saturation settles the grayscale question by itself: asking for a
            // specific amount of colour is incompatible with asking for none, and the typed
            // value is the more recent instruction. Resolving it here rather than leaving both
            // set means the request that reaches the renderer, the summary and the manifest
            // holds no contradiction for any of them to resolve differently.
            Grayscale = parse.GetResult(_options.Saturation) is not null
                ? false
                : Toggle(parse, _options.Grayscale, _options.NoGrayscale, request.Look.Grayscale),
            Blur = parse.GetValue(_options.Blur) ?? request.Look.Blur,
            Sharpen = parse.GetValue(_options.Sharpen) ?? request.Look.Sharpen,
            Pixelate = parse.GetValue(_options.Pixelate) ?? request.Look.Pixelate,
            FadeEdges = parse.GetValue(_options.FadeEdges) ?? request.Look.FadeEdges,
            Grain = parse.GetValue(_options.Grain) ?? request.Look.Grain,
            Vignette = Toggle(parse, _options.Vignette, _options.NoVignette, request.Look.Vignette),
            Mirror = Toggle(parse, _options.Mirror, _options.NoMirror, request.Look.Mirror),
            FlipVertical = Toggle(
                parse, _options.FlipVertical, _options.NoFlipVertical, request.Look.FlipVertical),
            Attribution = parse.GetValue(_options.Attribution) ?? request.Look.Attribution,
            AttributionPosition = parse.GetValue(_options.AttributionPosition)
                ?? request.Look.AttributionPosition,
            ZoomEnd = parse.GetValue(_options.Zoom) ?? request.Look.ZoomEnd,
            Speed = parse.GetValue(_options.Speed) ?? request.Look.Speed,
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

        var audio = ResolveAudio(parse, request.Audio);

        var duration = parse.GetValue(_options.TargetDuration) ?? request.TargetDuration;
        var splice = parse.GetValue(_options.Splice) ?? request.SpliceLength;

        var result = request with
        {
            // Filled in below: the derived name describes the settings, so it can only be
            // built once everything else has been resolved.
            OutputPath = string.Empty,
            TargetDuration = duration,
            SpliceLength = splice,
            SpliceJitter = parse.GetValue(_options.SpliceJitter) ?? request.SpliceJitter,
            MaxDistinctClips = parse.GetValue(_options.MaxClips) ?? request.MaxDistinctClips,

            // Taken as a percentage on the command line because that is how the guideline is
            // always quoted, and stored as a fraction because that is what it is compared against.
            MaxSourceFraction = parse.GetValue(_options.MaxSourcePercent) is { } percent
                ? percent / 100d
                : request.MaxSourceFraction,
            EnforceMaxSourceFraction =
                parse.GetValue(_options.StrictSourceLimit) || request.EnforceMaxSourceFraction,
            Selection = selection,
            Format = format,
            Look = look,
            Transition = transition,
            Encoder = encoder,
            Analysis = analysis,
            Audio = audio,
            Parallelism = parse.GetValue(_options.Parallelism) ?? request.Parallelism,
            IgnoreAnalysisCache = parse.GetValue(_options.NoCache),
            DryRun = parse.GetValue(_options.DryRun),
            KeepTemporaryFiles = parse.GetValue(_options.KeepTemp),
            ManifestPath = parse.GetValue(_options.Manifest),
        };

        // Only an explicit path is resolved here; a derived one is not. The derived name encodes
        // the target duration, which --match-audio takes from the audio track during planning, so
        // the name cannot be settled until the plan is. An empty path means "not chosen yet", and
        // the commands fill it in through EnsureOutputPath once PlanAsync has returned.
        var output = outputOverride ?? parse.GetValue(_options.Output);

        result = result with
        {
            OutputPath = output is null ? string.Empty : Path.GetFullPath(output),
        };

        // Validation messages name the flag a bound belongs to, which is the right hint when the
        // value was typed and the wrong place to look when it came from a profile. The builder
        // tracks no per-field provenance, so the profile is offered as a second candidate rather
        // than asserted as the culprit.
        try
        {
            Validate(result);
        }
        catch (CliUsageException ex) when (!string.IsNullOrWhiteSpace(profileName))
        {
            throw new CliUsageException(
                $"{ex.Message} Check the option, or profile '{profileName}'.",
                ex);
        }

        ValidateAudio(parse, result);
        return result;
    }

    /// <summary>
    /// Resolves a boolean look option that a profile may also set.
    /// <para>
    /// A bare <c>bool</c> flag cannot express "off", only "not mentioned", so ORing it with the
    /// inherited value makes a profile's <c>true</c> permanent and breaks the precedence rule
    /// the rest of this class follows. The paired <c>--no-</c> flag supplies the missing third
    /// state, restoring "CLI beats profile beats default" for these the same as for every
    /// nullable option.
    /// </para>
    /// <para>
    /// Both flags together is a contradiction with no sensible reading, so it is refused rather
    /// than silently resolved in one direction.
    /// </para>
    /// </summary>
    private static bool Toggle(ParseResult parse, Option<bool> on, Option<bool> off, bool inherited)
    {
        var turnedOn = parse.GetValue(on);
        var turnedOff = parse.GetValue(off);

        if (turnedOn && turnedOff)
        {
            throw new CliUsageException($"{on.Name} and {off.Name} cannot both be given.");
        }

        return turnedOn || (!turnedOff && inherited);
    }

    /// <summary>
    /// Resolves the audio mode.
    /// <para>
    /// <c>--audio</c> is the one option here whose meaning depends on whether it was given a
    /// value, so presence is read from the parse result rather than inferred from the value:
    /// a null value means "bare <c>--audio</c>", which is not the same as absent.
    /// </para>
    /// </summary>
    private AudioOptions ResolveAudio(ParseResult parse, AudioOptions inherited)
    {
        var audio = inherited;

        if (parse.GetResult(_options.Audio) is not null)
        {
            var path = parse.GetValue(_options.Audio);

            audio = string.IsNullOrWhiteSpace(path)
                ? AudioOptions.FromSource()
                : AudioOptions.FromFile(Path.GetFullPath(path));
        }

        return audio with
        {
            Offset = parse.GetValue(_options.AudioOffset) ?? audio.Offset,
            Volume = parse.GetValue(_options.AudioVolume) ?? audio.Volume,
            MatchDuration = parse.GetValue(_options.MatchAudio) || audio.MatchDuration,
        };
    }

    /// <summary>
    /// Rejects audio settings that cannot mean anything, before a render is planned.
    /// <para>
    /// Separate from <see cref="Validate"/> because these checks need to know what the user
    /// actually typed, not just what the settings resolved to: <c>--match-audio</c> with an
    /// explicit <c>--duration</c> is a contradiction, but the resolved request cannot tell a
    /// typed duration from the default one.
    /// </para>
    /// </summary>
    private void ValidateAudio(ParseResult parse, ClipRequest request)
    {
        var audio = request.Audio;

        if (audio.HasExternalTrack && !File.Exists(audio.ExternalPath!))
        {
            throw new CliUsageException($"Audio file not found: {audio.ExternalPath}");
        }

        if (audio.Volume < 0d)
        {
            throw new CliUsageException("--audio-volume cannot be negative.");
        }

        if (audio.Offset < TimeSpan.Zero)
        {
            throw new CliUsageException("--audio-offset cannot be negative.");
        }

        if (audio.MatchDuration)
        {
            if (!audio.HasExternalTrack)
            {
                throw new CliUsageException(
                    "--match-audio needs a track to match. Pass one with --audio <path>.");
            }

            if (parse.GetResult(_options.TargetDuration) is not null)
            {
                throw new CliUsageException(
                    "--match-audio and --duration both set the length of the render. "
                    + "Drop one: --match-audio takes the length from the track, "
                    + "--duration states it outright.");
            }
        }

        // These only ever apply to a muxed track, so silently ignoring them would hide a typo.
        if (!audio.HasExternalTrack)
        {
            if (parse.GetResult(_options.AudioOffset) is not null)
            {
                throw new CliUsageException("--audio-offset only applies to --audio <path>.");
            }

            if (parse.GetResult(_options.AudioVolume) is not null)
            {
                throw new CliUsageException("--audio-volume only applies to --audio <path>.");
            }
        }
    }

    /// <summary>
    /// Rejects a value outside <c>[min, max]</c>, and any value that is not a finite number.
    /// <para>
    /// The finiteness test is why this is a helper rather than an inline range check. Every
    /// comparison against NaN is false, so an out-of-range test such as
    /// <c>if (x is &lt; 0d or &gt; 1d)</c> admits NaN instead of rejecting it, and
    /// <c>System.CommandLine</c>'s <c>double</c> binder accepts both "nan" and "infinity". A NaN
    /// that gets past here reaches FFmpeg as the literal string <c>NaN</c>, and as a source-usage
    /// limit it switches the guardrail off entirely, since <c>Limit &gt; 0d</c> is false for it.
    /// </para>
    /// </summary>
    private static void RequireRange(double value, double min, double max, string message)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            throw new CliUsageException(message);
        }
    }

    /// <summary>As <see cref="RequireRange"/>, but the lower bound is exclusive: <c>(min, max]</c>.</summary>
    private static void RequireAbove(double value, double min, double max, string message)
    {
        if (!double.IsFinite(value) || value <= min || value > max)
        {
            throw new CliUsageException(message);
        }
    }

    /// <summary>
    /// The single funnel every request passes through, whatever set its values.
    /// <para>
    /// Phrased against the resolved <see cref="ClipRequest"/> rather than the parse result, so a
    /// value from a profile is held to the same bounds as one typed on the command line. A profile
    /// is a config file rather than a trusted source, and anything it sets that escapes a bound
    /// here surfaces as a mid-render FFmpeg failure instead of a usage error.
    /// </para>
    /// </summary>
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

        // Negative jitter would hand SplicePlanner a reversed range. Only a profile can set it,
        // since the CLI parser rejects a negative duration outright.
        if (request.SpliceJitter < TimeSpan.Zero)
        {
            throw new CliUsageException("--splice-jitter cannot be negative.");
        }

        // A non-positive dimension reaches FFmpeg as scale=-100:-50 and fails there. Only a
        // profile can produce one, since TryParseResolution rejects it on the CLI path, but the
        // bound belongs here so both paths answer the same way.
        if (request.Format.Width <= 0 || request.Format.Height <= 0)
        {
            throw new CliUsageException(
                $"Output resolution must be positive, but is {request.Format.Width}x{request.Format.Height}.");
        }

        RequireAbove(request.Format.FrameRate, 0d, 240d, "--fps must be between 0 and 240.");

        if (request.MaxDistinctClips is { } cap && cap < 1)
        {
            throw new CliUsageException("--max-clips must be at least 1.");
        }

        RequireRange(
            request.MaxSourceFraction, 0d, 1d, "--max-source-percent must be between 0 and 100.");

        if (request.Encoder.Quality is < 0 or > 51)
        {
            throw new CliUsageException("--quality must be between 0 and 51.");
        }

        RequireAbove(request.Look.Speed, 0d, double.MaxValue, "--speed must be greater than zero.");
        RequireAbove(request.Look.Gamma, 0d, 10d, "--gamma must be between 0 and 10.");
        RequireRange(request.Look.Sharpen, 0d, 3d, "--sharpen must be between 0 and 3.");
        RequireRange(request.Look.Darken, 0d, 1d, "--darken must be between 0 and 1.");
        RequireRange(request.Look.Saturation, 0d, 3d, "--saturation must be between 0 and 3.");
        RequireRange(request.Look.Contrast, 0d, 3d, "--contrast must be between 0 and 3.");
        RequireRange(request.Look.Blur, 0d, 50d, "--blur must be between 0 and 50.");
        RequireRange(request.Look.Grain, 0d, 100d, "--grain must be between 0 and 100.");

        // Zoom is a scale factor, so below 1 is a zoom out and exactly 1 is off. Zero or
        // negative is not a slower zoom, it is an inverted or collapsed frame.
        RequireAbove(request.Look.ZoomEnd, 0d, 8d, "--zoom must be between 0 and 8.");

        // 0 is "off" and is the default; anything else has to be a real magnification factor.
        // The floor is 1.1 to match both the help text and the value PixelateStage clamps to, so
        // an accepted value is never quietly adjusted downstream.
        if (request.Look.Pixelate != 0d)
        {
            RequireRange(request.Look.Pixelate, 1.1d, 16d, "--pixelate must be 0, or between 1.1 and 16.");
        }

        if (request.Look.FadeEdges < TimeSpan.Zero)
        {
            throw new CliUsageException("--fade-edges cannot be negative.");
        }

        // Half, not the whole length: --fade-edges is applied twice, once at each end, so a
        // value above half the clip makes the two fades meet in the middle and the clip never
        // reaches full brightness.
        //
        // Measured against the shortest clip the settings can produce, not against --splice.
        // --splice is a midpoint that --splice-jitter varies each clip around, so the nominal
        // figure describes no clip in particular and the short ones are where a fade runs out of
        // room. FadeEdgesStage clamps whatever it is given, so a bound checked against the
        // midpoint would admit values the stage then quietly reduces, while the plan summary goes
        // on reporting the figure that was asked for.
        if (request.Look.HasFadeEdges)
        {
            var shortest = ShortestSegment(request);

            if (request.Look.FadeEdges * 2 > shortest)
            {
                var qualifier = request.SpliceJitter > TimeSpan.Zero
                    ? $" (--splice {request.SpliceLength.TotalSeconds:0.##}s varied by "
                      + $"--splice-jitter {request.SpliceJitter.TotalSeconds:0.##}s)"
                    : string.Empty;

                throw new CliUsageException(
                    $"--fade-edges ({request.Look.FadeEdges.TotalSeconds:0.##}s) must be at most half "
                    + $"of the shortest clip, {shortest.TotalSeconds:0.##}s{qualifier}, i.e. "
                    + $"{(shortest / 2).TotalSeconds:0.##}s. It is applied at both ends, "
                    + "so anything longer means every clip fades for its whole length.");
            }
        }

        if (request.Selection.Strategy == SelectionStrategy.Cues && request.Selection.Cues.Count == 0)
        {
            throw new CliUsageException("--strategy cues requires at least one timestamp via --cues.");
        }

        if (request.Look.LutPath is { } lut && !File.Exists(lut))
        {
            throw new CliUsageException($"LUT file not found: {lut}");
        }

        var floor = request.Selection.Scoring.MotionFloorMultiple;
        var ceiling = request.Selection.Scoring.MotionCeilingMultiple;

        RequireRange(floor, 0d, 100d, "--motion-floor must be between 0 and 100.");
        RequireRange(ceiling, 0d, 100d, "--motion-ceiling must be between 0 and 100.");

        if (floor >= ceiling)
        {
            throw new CliUsageException(
                $"--motion-floor ({floor:0.##}) must be below --motion-ceiling ({ceiling:0.##}).");
        }
    }

    /// <summary>
    /// The shortest clip <see cref="SplicePlanner"/> can produce for these settings.
    /// <para>
    /// Mirrors the planner's own jitter clamp: the offset is bounded so a clip can never fall
    /// below the planner's floor, so the shortest is the nominal splice less the jitter, and never
    /// less than that floor.
    /// </para>
    /// <para>
    /// The floor rises to twice the transition when one is in use, so this has to ask for it the
    /// same way the planner does rather than assuming <see cref="SplicePlanner.MinimumSegment"/>.
    /// Otherwise the two disagree in the direction that matters: this would report a shorter
    /// clip than any the planner will emit, and admit a <c>--fade-edges</c> the render then
    /// clamps while the summary goes on quoting the value that was asked for.
    /// </para>
    /// </summary>
    private static TimeSpan ShortestSegment(ClipRequest request)
    {
        var floor = SplicePlanner.MinimumSegmentFor(
            request.Transition.IsEnabled ? request.Transition.Duration : TimeSpan.Zero);

        var shortest = request.SpliceLength - request.SpliceJitter;

        return shortest > floor ? shortest : floor;
    }

    /// <summary>
    /// Fills in a derived output path when the user did not name one.
    /// <para>
    /// Takes the request the pipeline settled on rather than the one <see cref="Build"/> returned.
    /// The derived name states the render's duration, and <c>--match-audio</c> replaces that
    /// duration during planning, so a name taken from the pre-plan request describes a render that
    /// is not the one about to be produced — and two tracks of different lengths would derive the
    /// same name and write over each other.
    /// </para>
    /// <para>
    /// Idempotent, so a caller that has already resolved a path can pass it through unexamined.
    /// </para>
    /// </summary>
    internal static ClipRequest EnsureOutputPath(ClipRequest request, string? profileName)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrEmpty(request.OutputPath))
        {
            return request;
        }

        return request with
        {
            OutputPath = Path.GetFullPath(DeriveOutputName(request, profileName)),
        };
    }

    /// <summary>
    /// Derives a self-describing output name from the first source and the settings that
    /// distinguish one render from another, e.g. <c>Alien-bg-shorts-1080x1920-1m30s-q20.mp4</c>.
    /// <para>
    /// Source, profile, resolution, runtime and quality are always present. Everything else
    /// appears only when it differs from the built-in default, so an ordinary render keeps a
    /// short name while an unusual one carries the reason it looks different. Renders that
    /// differ in any named setting no longer overwrite each other.
    /// </para>
    /// <para>
    /// Exposed so <c>batch</c> can re-derive a name per variant: each one gets its own seed,
    /// and a name carrying the template's seed would be a lie.
    /// </para>
    /// </summary>
    internal static string DeriveOutputName(ClipRequest request, string? profileName)
    {
        var stem = Path.GetFileNameWithoutExtension(request.Sources[0]);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "clips";
        }

        // Only the first source names the file, so record how many others fed into it.
        if (request.Sources.Count > 1)
        {
            stem += $"+{request.Sources.Count - 1}";
        }

        var parts = new List<string> { stem, "bg" };

        if (Slug(profileName) is { Length: > 0 } profile)
        {
            parts.Add(profile);
        }

        parts.Add(Dimensions(request.Format));
        parts.Add(DurationToken(request.TargetDuration));

        if (request.SpliceLength != Defaults.SpliceLength)
        {
            parts.Add($"cut{DurationToken(request.SpliceLength)}");
        }

        if (request.MaxDistinctClips is { } cap)
        {
            parts.Add($"{cap}clips");
        }

        if (request.Selection.Strategy != Defaults.Selection.Strategy)
        {
            parts.Add(request.Selection.Strategy.ToString().ToLowerInvariant());
        }

        if (Math.Abs(request.Format.FrameRate - Defaults.Format.FrameRate) > 0.01d)
        {
            parts.Add($"{Number(request.Format.FrameRate)}fps");
        }

        if (request.Look.HasSpeedChange)
        {
            parts.Add($"{Number(request.Look.Speed)}x");
        }

        parts.Add($"q{request.Encoder.Quality}");

        if (request.Encoder.Codec != Defaults.Encoder.Codec)
        {
            parts.Add(request.Encoder.Codec.ToString().ToLowerInvariant());
        }

        if (!request.Mute)
        {
            parts.Add("audio");
        }

        // A recorded seed is what makes the file reproducible, so it belongs in the name.
        if (request.Selection.Seed is { } seed)
        {
            parts.Add($"seed{seed}");
        }

        return string.Join('-', parts) + ".mp4";
    }

    /// <summary>Frame size as <c>1080p</c> for 16:9, otherwise <c>1080x1920</c>.</summary>
    private static string Dimensions(OutputFormat format) =>
        format.Width * 9 == format.Height * 16
            ? $"{format.Height}p"
            : $"{format.Width}x{format.Height}";

    /// <summary>Compact runtime: <c>45s</c>, <c>1m30s</c>, <c>16m</c>, <c>1h5m</c>.</summary>
    private static string DurationToken(TimeSpan value)
    {
        var total = Math.Max(0, (int)Math.Round(value.TotalSeconds));
        if (total < 60)
        {
            return $"{total}s";
        }

        var hours = total / 3600;
        var minutes = total / 60 % 60;
        var seconds = total % 60;

        var text = hours > 0 ? $"{hours}h" : string.Empty;
        if (minutes > 0)
        {
            text += $"{minutes}m";
        }

        if (seconds > 0)
        {
            text += $"{seconds}s";
        }

        return text;
    }

    private static string Number(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reduces a profile name to lowercase alphanumerics and single dashes. Profiles can be
    /// declared in <c>appsettings.json</c>, so the name is not guaranteed to be path-safe.
    /// </summary>
    private static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().TrimEnd('-');
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
