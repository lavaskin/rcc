namespace ReviewClips.Ffmpeg.Filters;

/// <summary>
/// One composable step of the per-segment video treatment.
/// <para>
/// Ordering is significant; <see cref="FilterStageOrder"/> encodes the reasoning. A stage may
/// emit several chains (see the blur-pad fit, which splits and recombines the stream) provided
/// it consumes its input label and produces its output label.
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

/// <summary>Canonical stage ordering.</summary>
public static class FilterStageOrder
{
    /// <summary>Tone-map first, while the data is still in its original wide-gamut form.</summary>
    public const int ToneMap = 100;

    /// <summary>Retime before any frame-rate work, so <c>fps</c> sees the final timeline.</summary>
    public const int Speed = 200;

    /// <summary>Square up anamorphic pixels before any aspect-ratio math happens.</summary>
    public const int SquarePixels = 300;

    /// <summary>Scale, crop or pad to the target frame.</summary>
    public const int Fit = 400;

    /// <summary>Normalize to constant frame rate after geometry is settled.</summary>
    public const int FrameRate = 500;

    /// <summary>Ken Burns drift, operating at the final resolution.</summary>
    public const int Zoom = 600;

    public const int Mirror = 700;

    /// <summary>Color grade before creative adjustments so the LUT sees unmodified color.</summary>
    public const int Lut = 800;

    /// <summary>Brightness, contrast and saturation.</summary>
    public const int Look = 900;

    /// <summary>
    /// After <see cref="Look"/> so it sharpens the graded image, but before <see cref="Blur"/>
    /// so an explicit <c>--blur</c> still dominates.
    /// </summary>
    public const int Sharpen = 950;

    public const int Blur = 1000;

    /// <summary>
    /// Immediately after <see cref="Blur"/>: pixelation is a downscale and upscale, so blurring
    /// first softens the block edges, while the other order blurs the blocks away entirely.
    /// </summary>
    public const int Pixelate = 1010;

    public const int Vignette = 1100;

    /// <summary>
    /// Alongside <see cref="Vignette"/>: feathering after the corners are darkened keeps the
    /// fade the outermost of the two edge treatments.
    /// </summary>
    public const int FadeEdges = 1150;

    /// <summary>Grain last among the looks, so it is not blurred away.</summary>
    public const int Grain = 1200;

    /// <summary>
    /// After every look and well before <see cref="OutputFormat"/>: a credit line exists to be
    /// read, so nothing that obscures the frame may run after it.
    /// </summary>
    public const int Attribution = 1350;

    /// <summary>Force a broadly compatible pixel format immediately before encoding.</summary>
    public const int OutputFormat = 9000;
}
