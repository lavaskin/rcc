namespace ReviewClips.Core.Options;

public enum TransitionKind
{
    /// <summary>Hard cuts. Allows the whole stitch to be a stream copy, which is effectively instant.</summary>
    None,

    /// <summary>Direct cross-dissolve between segments.</summary>
    Fade,

    /// <summary>Fade down to black and back up. Reads as a deliberate edit rather than a glitch.</summary>
    DipToBlack,

    /// <summary>Fade through white.</summary>
    DipToWhite,

    Wipeleft,
    Wiperight,
    SlideUp,
    SlideDown,
    Dissolve,
    SmoothLeft,
    CircleClose,
}

public sealed record TransitionOptions
{
    public static TransitionOptions None { get; } = new() { Kind = TransitionKind.None };

    public TransitionKind Kind { get; init; } = TransitionKind.DipToBlack;

    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(0.4);

    /// <summary>Fade in from black at the very start of the render.</summary>
    public TimeSpan FadeIn { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>Fade out to black at the very end of the render.</summary>
    public TimeSpan FadeOut { get; init; } = TimeSpan.FromSeconds(0.5);

    public bool IsEnabled => Kind != TransitionKind.None && Duration > TimeSpan.Zero;

    /// <summary>Maps to the FFmpeg <c>xfade</c> transition name.</summary>
    public string XfadeName => Kind switch
    {
        TransitionKind.Fade => "fade",
        TransitionKind.DipToBlack => "fadeblack",
        TransitionKind.DipToWhite => "fadewhite",
        TransitionKind.Wipeleft => "wipeleft",
        TransitionKind.Wiperight => "wiperight",
        TransitionKind.SlideUp => "slideup",
        TransitionKind.SlideDown => "slidedown",
        TransitionKind.Dissolve => "dissolve",
        TransitionKind.SmoothLeft => "smoothleft",
        TransitionKind.CircleClose => "circleclose",
        TransitionKind.None => "fade",
        _ => "fade",
    };
}
