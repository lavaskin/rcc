using System.CommandLine;
using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Cli.Cli;

/// <summary>
/// The option surface shared by <c>generate</c> and <c>batch</c>.
/// <para>
/// Every option is nullable and has no parser-level default. That is deliberate: it makes
/// "was this specified?" unambiguous, so precedence can be resolved cleanly as
/// CLI value, then profile value, then built-in default.
/// </para>
/// </summary>
internal sealed class GenerateOptions
{
    public Option<List<string>> Input { get; } = new("--input", "-i")
    {
        Description = "Source file, directory or glob. Repeatable.",
        HelpName = "path",
        Required = true,
        AllowMultipleArgumentsPerToken = true,
        Arity = ArgumentArity.OneOrMore,
    };

    public Option<string?> Output { get; } = new("--output", "-o")
    {
        Description = "Output file path.",
        HelpName = "path",
    };

    public Option<TimeSpan?> TargetDuration { get; } =
        ValueParsers.Duration("--duration", "Total length of the finished render, e.g. 90s or 2m30s.", "-d");

    public Option<TimeSpan?> Splice { get; } =
        ValueParsers.Duration("--splice", "Length of each spliced clip.", "-s");

    public Option<int?> MaxClips { get; } = new("--max-clips")
    {
        Description = "Cap the number of distinct clips, repeating them to fill the runtime. "
            + "Lets a long render use far less of the source footage.",
        HelpName = "count",
    };

    public Option<TimeSpan?> SpliceJitter { get; } =
        ValueParsers.Duration("--splice-jitter", "Random variation applied to each splice length.");

    public Option<double?> MaxSourcePercent { get; } = new("--max-source-percent")
    {
        Description = "Warn when the render consumes more than this share of the source footage. "
            + "Pairs with --max-clips, which is how you lower it. 0 disables the check.",
        HelpName = "0-100",
    };

    public Option<bool> StrictSourceLimit { get; } = new("--strict-source-limit")
    {
        Description = "Fail rather than warn when --max-source-percent is exceeded.",
    };

    public Option<string?> Profile { get; } = new("--profile", "-p")
    {
        Description = "Named preset to start from. See 'rcc profiles'.",
        HelpName = "name",
    };

    // Selection -------------------------------------------------------------

    public Option<SelectionStrategy?> Strategy { get; } =
        ValueParsers.EnumOption<SelectionStrategy>("--strategy", "How segments are chosen.");

    public Option<int?> Seed { get; } = new("--seed")
    {
        Description = "Seed for reproducible renders. Recorded in the manifest when omitted.",
        HelpName = "int",
    };

    public Option<Offset?> SkipHead { get; } = ValueParsers.OffsetOption(
        "--skip-head",
        "Ignore this much from the start of each source (clears logos and titles).");

    public Option<Offset?> SkipTail { get; } = ValueParsers.OffsetOption(
        "--skip-tail",
        "Ignore this much from the end of each source (clears end credits).");

    public Option<List<TimeRange>> Range { get; } = ValueParsers.RangeListOption(
        "--range",
        "Only sample inside this range. Repeatable.");

    public Option<List<TimeRange>> Exclude { get; } = ValueParsers.RangeListOption(
        "--exclude",
        "Never sample inside this range. Repeatable.");

    public Option<ChapterSkipMode?> Chapters { get; } = ValueParsers.EnumOption<ChapterSkipMode>(
        "--chapters",
        "Whether chapters titled as openings, credits or recaps are skipped.");

    public Option<List<string>> SkipChapter { get; } = ValueParsers.TextListOption(
        "--skip-chapter",
        "Also skip chapters whose title matches. Substring, or a glob if it contains * or ?. "
        + "Repeatable. Run 'rcc probe --chapters' to see the titles.",
        "pattern");

    public Option<TimeSpan?> MinGap { get; } = ValueParsers.Duration(
        "--min-gap",
        "Minimum spacing between chosen clips, so they don't come from the same moment.");

    public Option<bool> NoRejectBlack { get; } = new("--no-reject-black")
    {
        Description = "Allow clips from stretches detected as black.",
    };

    public Option<bool> NoRejectFrozen { get; } = new("--no-reject-frozen")
    {
        Description = "Allow clips from static or frozen shots.",
    };

    public Option<bool> NoShuffle { get; } = new("--no-shuffle")
    {
        Description = "Keep clips in chronological order instead of shuffling.",
    };

    public Option<List<TimeSpan>> Cues { get; } = ValueParsers.CuesOption(
        "--cues",
        "Timestamps to build the render from. Implies --strategy cues.");

    // Scoring ---------------------------------------------------------------

    public Option<double?> MotionTarget { get; } = new("--motion-target")
    {
        Description = "Ideal motion level, as a multiple of the title's median. Tune per genre.",
        HelpName = "multiple",
    };

    public Option<double?> MotionFloor { get; } = new("--motion-floor")
    {
        Description = "Reject clips quieter than this multiple of the median.",
        HelpName = "multiple",
    };

    public Option<double?> MotionCeiling { get; } = new("--motion-ceiling")
    {
        Description = "Reject clips busier than this multiple of the median.",
        HelpName = "multiple",
    };

    // Format ----------------------------------------------------------------

    public Option<Ratio?> Aspect { get; } =
        ValueParsers.AspectOption("--aspect", "Target aspect ratio, e.g. 16:9 or 9:16.");

    public Option<(int Width, int Height)?> Resolution { get; } =
        ValueParsers.ResolutionOption("--resolution", "Output resolution.");

    public Option<double?> FrameRate { get; } = new("--fps")
    {
        Description = "Output frame rate.",
        HelpName = "fps",
    };

    public Option<FitMode?> Fit { get; } =
        ValueParsers.EnumOption<FitMode>("--fit", "How to fit source footage into a different aspect ratio.");

    public Option<double?> ForegroundScale { get; } = new("--fg-scale")
    {
        Description = "With --fit blur-pad, how much to enlarge the image over the blurred backdrop. "
            + "1 fits the whole frame; ~1.5 suits a scope film in a vertical frame.",
        HelpName = "1-4",
    };

    public Option<ToneMapMode?> ToneMap { get; } =
        ValueParsers.EnumOption<ToneMapMode>("--tonemap", "HDR to SDR tone-mapping operator.");

    // Look ------------------------------------------------------------------

    public Option<double?> Darken { get; } = new("--darken")
    {
        Description = "Darken the footage, 0 to 1.",
        HelpName = "0-1",
    };

    public Option<double?> Saturation { get; } = new("--saturation")
    {
        Description = "Saturation multiplier; 1 leaves it untouched.",
        HelpName = "multiplier",
    };

    public Option<double?> Contrast { get; } = new("--contrast")
    {
        Description = "Contrast multiplier; 1 leaves it untouched.",
        HelpName = "multiplier",
    };

    public Option<double?> Blur { get; } = new("--blur")
    {
        Description = "Gaussian blur sigma; 0 disables.",
        HelpName = "sigma",
    };

    public Option<int?> Grain { get; } = new("--grain")
    {
        Description = "Film grain strength, 0 to 100.",
        HelpName = "0-100",
    };

    public Option<bool> Vignette { get; } = new("--vignette")
    {
        Description = "Darken the frame corners.",
    };

    public Option<bool> Mirror { get; } = new("--mirror")
    {
        Description = "Horizontally mirror every clip.",
    };

    public Option<double?> Zoom { get; } = new("--zoom")
    {
        Description = "Slow zoom target across each clip, e.g. 1.08. 1 disables.",
        HelpName = "factor",
    };

    public Option<double?> Speed { get; } = new("--speed")
    {
        Description = "Playback rate multiplier; 1 disables.",
        HelpName = "multiplier",
    };

    public Option<string?> Overlay { get; } = new("--overlay")
    {
        Description = "Image composited over every clip.",
        HelpName = "path",
    };

    public Option<double?> OverlayOpacity { get; } = new("--overlay-opacity")
    {
        Description = "Opacity of the overlay image.",
        HelpName = "0-1",
    };

    public Option<string?> Lut { get; } = new("--lut")
    {
        Description = "3D LUT (.cube) applied for colour grading.",
        HelpName = "path",
    };

    // Transitions -----------------------------------------------------------

    public Option<TransitionKind?> Transition { get; } =
        ValueParsers.EnumOption<TransitionKind>("--transition", "Transition between clips.");

    public Option<TimeSpan?> TransitionDuration { get; } = ValueParsers.Duration(
        "--transition-duration",
        "Length of each transition.");

    public Option<TimeSpan?> FadeIn { get; } = ValueParsers.Duration("--fade-in", "Fade in from black at the start.");

    public Option<TimeSpan?> FadeOut { get; } = ValueParsers.Duration("--fade-out", "Fade out to black at the end.");

    // Encoding --------------------------------------------------------------

    public Option<EncoderPreference?> Encoder { get; } =
        ValueParsers.EnumOption<EncoderPreference>("--encoder", "Video encoder to use.");

    public Option<VideoCodecKind?> Codec { get; } =
        ValueParsers.EnumOption<VideoCodecKind>("--codec", "Output video codec.");

    public Option<int?> Quality { get; } = new("--quality", "-q")
    {
        Description = "Perceptual quality, 0 (best) to 51 (worst).",
        HelpName = "0-51",
    };

    public Option<string?> Preset { get; } = new("--preset")
    {
        Description = "Encoder speed preset (e.g. medium for x264, p5 for NVENC).",
        HelpName = "name",
    };

    /// <summary>
    /// Bare <c>--audio</c> keeps the source audio; <c>--audio track.wav</c> muxes that file
    /// instead.
    /// <para>
    /// <c>ZeroOrOne</c> arity is what makes both spellings work from one option, and is the
    /// reason external audio is not a breaking change. pi spells this <c>--audio MODE|FILE</c>
    /// with <c>mute</c> as an explicit mode, which cannot be adopted without changing what an
    /// existing bare <c>--audio</c> means.
    /// </para>
    /// </summary>
    public Option<string?> Audio { get; } = new("--audio")
    {
        Description = "Keep the source audio, or give a file to mux in instead. "
            + "Off by default, since this footage sits under narration.",
        HelpName = "path",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public Option<TimeSpan?> AudioOffset { get; } = ValueParsers.Duration(
        "--audio-offset",
        "Start this far into the external audio track.");

    public Option<double?> AudioVolume { get; } = new("--audio-volume")
    {
        Description = "Linear gain applied to the muxed audio; 1 leaves it untouched.",
        HelpName = "multiplier",
    };

    public Option<bool> MatchAudio { get; } = new("--match-audio")
    {
        Description = "Take the render's length from the external audio track "
            + "instead of from --duration.",
    };

    public Option<bool> HardwareDecode { get; } = new("--hwdecode")
    {
        Description = "Use CUDA for decoding. Only helps on long sources; has fixed setup cost.",
    };

    // Analysis --------------------------------------------------------------

    public Option<int?> AnalysisFps { get; } = new("--analysis-fps")
    {
        Description = "Frames per second sampled during analysis. Higher is slower but more precise.",
        HelpName = "fps",
    };

    public Option<double?> SceneThreshold { get; } = new("--scene-threshold")
    {
        Description = "Scene-cut sensitivity, 0 to 100. Lower finds more cuts.",
        HelpName = "0-100",
    };

    public Option<bool> NoCache { get; } = new("--no-cache")
    {
        Description = "Ignore and overwrite any cached analysis.",
    };

    // Plumbing --------------------------------------------------------------

    public Option<int?> Parallelism { get; } = new("--parallelism", "-j")
    {
        Description = "Concurrent FFmpeg extraction processes.",
        HelpName = "count",
    };

    public Option<bool> DryRun { get; } = new("--dry-run")
    {
        Description = "Print the plan and the exact FFmpeg commands without rendering.",
    };

    public Option<bool> KeepTemp { get; } = new("--keep-temp")
    {
        Description = "Leave intermediate segment files on disk.",
    };

    public Option<string?> Manifest { get; } = new("--manifest")
    {
        Description = "Write a JSON manifest of exactly which clips were used.",
        HelpName = "path",
    };

    public Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Verbose logging, including every FFmpeg command.",
    };

    /// <summary>Registers every option on a command.</summary>
    public void AddTo(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);

        foreach (var option in All())
        {
            command.Add(option);
        }
    }

    public IEnumerable<Option> All()
    {
        yield return Input;
        yield return Output;
        yield return TargetDuration;
        yield return Splice;
        yield return MaxClips;
        yield return SpliceJitter;
        yield return MaxSourcePercent;
        yield return StrictSourceLimit;
        yield return Profile;
        yield return Strategy;
        yield return Seed;
        yield return SkipHead;
        yield return SkipTail;
        yield return Range;
        yield return Exclude;
        yield return Chapters;
        yield return SkipChapter;
        yield return MinGap;
        yield return NoRejectBlack;
        yield return NoRejectFrozen;
        yield return NoShuffle;
        yield return Cues;
        yield return MotionTarget;
        yield return MotionFloor;
        yield return MotionCeiling;
        yield return Aspect;
        yield return Resolution;
        yield return FrameRate;
        yield return Fit;
        yield return ForegroundScale;
        yield return ToneMap;
        yield return Darken;
        yield return Saturation;
        yield return Contrast;
        yield return Blur;
        yield return Grain;
        yield return Vignette;
        yield return Mirror;
        yield return Zoom;
        yield return Speed;
        yield return Overlay;
        yield return OverlayOpacity;
        yield return Lut;
        yield return Transition;
        yield return TransitionDuration;
        yield return FadeIn;
        yield return FadeOut;
        yield return Encoder;
        yield return Codec;
        yield return Quality;
        yield return Preset;
        yield return Audio;
        yield return AudioOffset;
        yield return AudioVolume;
        yield return MatchAudio;
        yield return HardwareDecode;
        yield return AnalysisFps;
        yield return SceneThreshold;
        yield return NoCache;
        yield return Parallelism;
        yield return DryRun;
        yield return KeepTemp;
        yield return Manifest;
        yield return Verbose;
    }
}
