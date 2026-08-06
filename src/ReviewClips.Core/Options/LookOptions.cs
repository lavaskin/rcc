namespace ReviewClips.Core.Options;

/// <summary>
/// Visual treatment applied to every segment. The defaults deliberately push footage into the
/// background rather than let it compete with narration and captions.
/// <para>
/// An aesthetic tool, not a legal one: obscuring footage does not create a licence, and using
/// it to evade automated matching carries its own risks. See the README's copyright section.
/// </para>
/// </summary>
public sealed record LookOptions
{
    public static LookOptions None { get; } = new()
    {
        Darken = 0d,
        Saturation = 1d,
    };

    /// <summary>0 = untouched, 1 = fully black. Applied as a brightness reduction.</summary>
    public double Darken { get; init; } = 0.35d;

    /// <summary>1 = untouched. Below 1 desaturates.</summary>
    public double Saturation { get; init; } = 0.80d;

    /// <summary>1 = untouched.</summary>
    public double Contrast { get; init; } = 1d;

    /// <summary>1 = untouched. Above 1 lifts the midtones, below 1 deepens them.</summary>
    public double Gamma { get; init; } = 1d;

    /// <summary>Drops all colour; equivalent to <see cref="Saturation"/> 0.</summary>
    public bool Grayscale { get; init; }

    /// <summary>Gaussian blur sigma. 0 disables.</summary>
    public double Blur { get; init; }

    /// <summary>Unsharp-mask strength. 0 disables.</summary>
    public double Sharpen { get; init; }

    /// <summary>Downscale-and-upscale factor for a chunky, low-resolution look. 0 or 1 disables.</summary>
    public double Pixelate { get; init; }

    /// <summary>
    /// Fades each clip in and out by this much. Unlike a transition it applies within a segment
    /// rather than between two, so it survives the stream-copy stitch transitions cannot use.
    /// </summary>
    public TimeSpan FadeEdges { get; init; }

    /// <summary>
    /// A credit or disclaimer burned into a corner of every clip, e.g.
    /// <c>Clip from Heat (1995), dir. Michael Mann</c>.
    /// </summary>
    public string? Attribution { get; init; }

    /// <summary>Where <see cref="Attribution"/> sits in the frame.</summary>
    public TextPosition AttributionPosition { get; init; } = TextPosition.BottomRight;

    /// <summary>Film grain strength (FFmpeg <c>noise</c> alls). 0 disables.</summary>
    public int Grain { get; init; }

    /// <summary>Darkens the frame corners, further suppressing edge detail.</summary>
    public bool Vignette { get; init; }

    /// <summary>Horizontally mirrors the frame.</summary>
    public bool Mirror { get; init; }

    /// <summary>Vertically flips the frame.</summary>
    public bool FlipVertical { get; init; }

    /// <summary>Slow zoom across each segment (Ken Burns). 1.0 disables; 1.08 is a subtle drift.</summary>
    public double ZoomEnd { get; init; } = 1d;

    /// <summary>Playback rate multiplier. 1.0 disables. 0.5 is half speed.</summary>
    public double Speed { get; init; } = 1d;

    /// <summary>Optional 3D LUT (.cube) applied for colour grading.</summary>
    public string? LutPath { get; init; }

    public bool HasZoom => Math.Abs(ZoomEnd - 1d) > 0.001d;

    public bool HasSpeedChange => Math.Abs(Speed - 1d) > 0.001d;

    public bool HasSharpen => Sharpen > 0.001d;

    public bool HasPixelate => Pixelate > 1.001d;

    public bool HasFadeEdges => FadeEdges > TimeSpan.Zero;

    public bool HasAttribution => !string.IsNullOrWhiteSpace(Attribution);

    /// <summary>
    /// Saturation as the filter graph applies it: <see cref="Grayscale"/> wins over
    /// <see cref="Saturation"/>, being the more specific request.
    /// <para>
    /// A last-resort tie-break; callers that know the user's intent settle it first (the CLI
    /// clears <see cref="Grayscale"/> when <c>--saturation</c> is given explicitly). Anything
    /// reporting on these options must read this rather than <see cref="Saturation"/>.
    /// </para>
    /// </summary>
    public double EffectiveSaturation => Grayscale ? 0d : Saturation;

    public bool AltersColor =>
        Darken > 0.001d
        || Math.Abs(EffectiveSaturation - 1d) > 0.001d
        || Math.Abs(Contrast - 1d) > 0.001d
        || Math.Abs(Gamma - 1d) > 0.001d;
}

/// <summary>Where burned-in text sits in the frame.</summary>
public enum TextPosition
{
    BottomRight,
    BottomLeft,
    TopLeft,
    TopRight,
    Top,
    Bottom,
    Center,
}
