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

    /// <summary>Gaussian blur sigma. 0 disables.</summary>
    public double Blur { get; init; }

    /// <summary>Film grain strength (FFmpeg <c>noise</c> alls). 0 disables.</summary>
    public int Grain { get; init; }

    /// <summary>Darkens the frame corners. Reads as "cinematic" and further suppresses edge detail.</summary>
    public bool Vignette { get; init; }

    /// <summary>Horizontally mirrors the frame.</summary>
    public bool Mirror { get; init; }

    /// <summary>Slow zoom across each segment (Ken Burns). 1.0 disables; 1.08 is a subtle drift.</summary>
    public double ZoomEnd { get; init; } = 1d;

    /// <summary>Playback rate multiplier. 1.0 disables. 0.5 is half speed.</summary>
    public double Speed { get; init; } = 1d;

    /// <summary>Optional image composited over every segment (watermark, texture, grade overlay).</summary>
    public string? OverlayPath { get; init; }

    /// <summary>Opacity for <see cref="OverlayPath"/>.</summary>
    public double OverlayOpacity { get; init; } = 1d;

    /// <summary>Optional 3D LUT (.cube) applied for colour grading.</summary>
    public string? LutPath { get; init; }

    public bool HasZoom => Math.Abs(ZoomEnd - 1d) > 0.001d;

    public bool HasSpeedChange => Math.Abs(Speed - 1d) > 0.001d;

    public bool AltersColor =>
        Darken > 0.001d
        || Math.Abs(Saturation - 1d) > 0.001d
        || Math.Abs(Contrast - 1d) > 0.001d;
}
