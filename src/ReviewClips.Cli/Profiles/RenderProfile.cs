using ReviewClips.Core.Options;

namespace ReviewClips.Cli.Profiles;

/// <summary>
/// A named bundle of defaults. Every field is nullable: a profile only states what it cares
/// about, and anything it leaves unset keeps the built-in default. Explicit CLI options always
/// win over a profile.
/// </summary>
public sealed record RenderProfile
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    // Format
    public int? Width { get; init; }

    public int? Height { get; init; }

    public double? Fps { get; init; }

    public FitMode? Fit { get; init; }

    public ToneMapMode? ToneMap { get; init; }

    public double? ForegroundScale { get; init; }

    // Look
    public double? Darken { get; init; }

    public double? Saturation { get; init; }

    public double? Contrast { get; init; }

    public double? Blur { get; init; }

    public int? Grain { get; init; }

    public bool? Vignette { get; init; }

    public bool? Mirror { get; init; }

    public double? Zoom { get; init; }

    public double? Speed { get; init; }

    // Cadence
    public double? SpliceSeconds { get; init; }

    public double? SpliceJitterSeconds { get; init; }

    // Transitions
    public TransitionKind? Transition { get; init; }

    public double? TransitionSeconds { get; init; }

    public double? FadeInSeconds { get; init; }

    public double? FadeOutSeconds { get; init; }

    // Selection
    public SelectionStrategy? Strategy { get; init; }

    public double? MinGapSeconds { get; init; }

    // Encoding
    public int? Quality { get; init; }

    public VideoCodecKind? Codec { get; init; }

    public bool? Mute { get; init; }

    /// <summary>Folds this profile into a request, leaving unset fields untouched.</summary>
    public ClipRequest ApplyTo(ClipRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var format = request.Format with
        {
            Width = Width ?? request.Format.Width,
            Height = Height ?? request.Format.Height,
            FrameRate = Fps ?? request.Format.FrameRate,
            Fit = Fit ?? request.Format.Fit,
            BlurPadForegroundScale = ForegroundScale ?? request.Format.BlurPadForegroundScale,
            ToneMap = ToneMap ?? request.Format.ToneMap,
        };

        var look = request.Look with
        {
            Darken = Darken ?? request.Look.Darken,
            Saturation = Saturation ?? request.Look.Saturation,
            Contrast = Contrast ?? request.Look.Contrast,
            Blur = Blur ?? request.Look.Blur,
            Grain = Grain ?? request.Look.Grain,
            Vignette = Vignette ?? request.Look.Vignette,
            Mirror = Mirror ?? request.Look.Mirror,
            ZoomEnd = Zoom ?? request.Look.ZoomEnd,
            Speed = Speed ?? request.Look.Speed,
        };

        var transition = request.Transition with
        {
            Kind = Transition ?? request.Transition.Kind,
            Duration = Seconds(TransitionSeconds) ?? request.Transition.Duration,
            FadeIn = Seconds(FadeInSeconds) ?? request.Transition.FadeIn,
            FadeOut = Seconds(FadeOutSeconds) ?? request.Transition.FadeOut,
        };

        var selection = request.Selection with
        {
            Strategy = Strategy ?? request.Selection.Strategy,
            MinGap = Seconds(MinGapSeconds) ?? request.Selection.MinGap,
        };

        var encoder = request.Encoder with
        {
            Quality = Quality ?? request.Encoder.Quality,
            Codec = Codec ?? request.Encoder.Codec,
        };

        return request with
        {
            Format = format,
            Look = look,
            Transition = transition,
            Selection = selection,
            Encoder = encoder,
            SpliceLength = Seconds(SpliceSeconds) ?? request.SpliceLength,
            SpliceJitter = Seconds(SpliceJitterSeconds) ?? request.SpliceJitter,
            Audio = Mute switch
            {
                true => AudioOptions.Muted,
                false => AudioOptions.FromSource(),
                null => request.Audio,
            },
        };
    }

    private static TimeSpan? Seconds(double? value) =>
        value.HasValue ? TimeSpan.FromSeconds(value.Value) : null;
}
