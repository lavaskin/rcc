namespace ReviewClips.Ffmpeg.Filters;

/// <summary>
/// One composable step of the per-segment video treatment.
/// <para>
/// Order matters a great deal and the constants in <see cref="FilterStageOrder"/> encode the
/// reasoning. A stage may emit several chains (see the blur-pad fit, which splits and
/// recombines the stream) provided it consumes its input label and produces its output label.
/// </para>
/// </summary>
public interface IVideoFilterStage
{
    /// <summary>Position in the chain. See <see cref="FilterStageOrder"/>.</summary>
    int Order { get; }

    string Name { get; }

    bool AppliesTo(FilterContext context);

    void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel);
}

/// <summary>
/// Canonical stage ordering.
/// </summary>
public static class FilterStageOrder
{
    /// <summary>Tone-map first, while the data is still in its original wide-gamut form.</summary>
    public const int ToneMap = 100;

    /// <summary>Retime before any frame-rate work, so <c>fps</c> sees the final timeline.</summary>
    public const int Speed = 200;

    /// <summary>Square up anamorphic pixels before any aspect-ratio maths happens.</summary>
    public const int SquarePixels = 300;

    /// <summary>Scale, crop or pad to the target frame.</summary>
    public const int Fit = 400;

    /// <summary>Normalise to constant frame rate after geometry is settled.</summary>
    public const int FrameRate = 500;

    /// <summary>Ken Burns drift, operating at the final resolution.</summary>
    public const int Zoom = 600;

    public const int Mirror = 700;

    /// <summary>Colour grade before creative adjustments so the LUT sees unmodified colour.</summary>
    public const int Lut = 800;

    /// <summary>Brightness, contrast and saturation.</summary>
    public const int Look = 900;

    public const int Blur = 1000;

    public const int Vignette = 1100;

    /// <summary>Grain last among the looks, so it is not blurred away.</summary>
    public const int Grain = 1200;

    public const int Overlay = 1300;

    /// <summary>Force a broadly compatible pixel format immediately before encoding.</summary>
    public const int OutputFormat = 9000;
}
