using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Options;

/// <summary>How source footage is made to fit a target frame whose aspect ratio differs.</summary>
public enum FitMode
{
    /// <summary>Scale to cover, then centre-crop. Fills the frame, loses the edges.</summary>
    Crop,

    /// <summary>Scale to fit over a blurred, zoomed copy of itself. The standard vertical/Shorts look.</summary>
    BlurPad,

    /// <summary>Scale to fit and letterbox with solid bars.</summary>
    Pad,

    /// <summary>Ignore aspect ratio and stretch. Almost never what you want.</summary>
    Stretch,
}

/// <summary>HDR to SDR tone-mapping operator.</summary>
public enum ToneMapMode
{
    /// <summary>Tone-map only when the source is detected as PQ or HLG.</summary>
    Auto,

    None,
    Hable,
    Mobius,
    Reinhard,
}

/// <summary>Geometry, timing and colour of the rendered output.</summary>
public sealed record OutputFormat
{
    public static OutputFormat Youtube { get; } = new() { Width = 1920, Height = 1080 };

    public static OutputFormat Shorts { get; } = new()
    {
        Width = 1080,
        Height = 1920,
        Fit = FitMode.BlurPad,
    };

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public double FrameRate { get; init; } = 30d;

    public FitMode Fit { get; init; } = FitMode.Crop;

    public ToneMapMode ToneMap { get; init; } = ToneMapMode.Auto;

    /// <summary>Gaussian sigma for the <see cref="FitMode.BlurPad"/> backdrop.</summary>
    public double BlurPadSigma { get; init; } = 24d;

    /// <summary>
    /// How much larger than "fit" to scale the foreground in <see cref="FitMode.BlurPad"/>,
    /// cropping the overflow.
    /// <para>
    /// 1.0 fits the whole frame, which is correct but wasteful for a very wide source in a very
    /// tall target: a 2.35:1 film in 9:16 fills only about a quarter of the height and the rest
    /// is blurred filler. Raising this trades some width for a taller, more legible image.
    /// Around 1.5 is a reasonable compromise for scope material.
    /// </para>
    /// </summary>
    public double BlurPadForegroundScale { get; init; } = 1d;

    public string PadColor { get; init; } = "black";

    public Ratio AspectRatio => new(Width, Height);

    /// <summary>Applies an aspect ratio while preserving the long edge, snapping to even dimensions.</summary>
    public OutputFormat WithAspect(Ratio aspect)
    {
        if (!aspect.IsValid)
        {
            return this;
        }

        var longEdge = Math.Max(Width, Height);
        int width, height;

        if (aspect.Value >= 1d)
        {
            width = longEdge;
            height = (int)Math.Round(longEdge / aspect.Value);
        }
        else
        {
            height = longEdge;
            width = (int)Math.Round(longEdge * aspect.Value);
        }

        return this with { Width = MakeEven(width), Height = MakeEven(height) };
    }

    /// <summary>H.264/HEVC chroma subsampling requires even dimensions.</summary>
    public static int MakeEven(int value) => value % 2 == 0 ? value : value + 1;
}
