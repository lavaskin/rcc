namespace ReviewClips.Core.Options;

/// <summary>
/// Visual treatment applied to every segment.
/// <para>
/// The defaults deliberately push footage into the background: background B-roll that
/// competes with your voice-over and captions hurts retention rather than helping it.
/// </para>
/// <para>
/// Note: these are an aesthetic tool, not a legal one. Obscuring footage does not create a
/// licence, and using them specifically to evade automated matching carries its own risks.
/// See the copyright section of the README.
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

    /// <summary>
    /// Drops all colour. A convenience for <see cref="Saturation"/> 0, which is worth naming
    /// because it is a deliberate look rather than a point on a scale.
    /// </summary>
    public bool Grayscale { get; init; }

    /// <summary>Gaussian blur sigma. 0 disables.</summary>
    public double Blur { get; init; }

    /// <summary>Unsharp-mask strength. 0 disables.</summary>
    public double Sharpen { get; init; }

    /// <summary>
    /// Downscale-and-upscale factor for a chunky, low-resolution look. 0 or 1 disables.
    /// </summary>
    public double Pixelate { get; init; }

    /// <summary>
    /// Fades each clip in and out by this much. Distinct from a transition: this applies within
    /// every segment rather than between neighbouring ones, so it survives the stream-copy
    /// stitch that transitions cannot use.
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

    /// <summary>Darkens the frame corners. Reads as "cinematic" and further suppresses edge detail.</summary>
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
    /// Saturation as the filter should apply it. <see cref="Grayscale"/> wins over
    /// <see cref="Saturation"/>, being the more specific request.
    /// <para>
    /// This is a last-resort tie-break, not the place the conflict is meant to be settled. A
    /// caller that knows the user's intent resolves it first — the CLI clears
    /// <see cref="Grayscale"/> when <c>--saturation</c> is typed explicitly, so a resolved
    /// <see cref="LookOptions"/> arriving from there never holds both. Anything reporting on
    /// these options must read this rather than <see cref="Saturation"/>, or it will describe a
    /// value the filter graph does not apply.
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
