using System.Globalization;
using ReviewClips.Core.Options;

namespace ReviewClips.Ffmpeg.Filters;

/// <summary>
/// Converts HDR (PQ / HLG) sources to SDR BT.709 via linear light, a floating-point tone-map,
/// and a return to BT.709. Without it an HDR remux re-encoded as SDR renders washed-out and grey.
/// </summary>
public sealed class ToneMapStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.ToneMap;

    public string Name => "tonemap";

    public bool AppliesTo(FilterContext context) => context.NeedsToneMapping;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel)
    {
        var source = context.Source;
        var isHdr = source.IsHdr;

        // setparams stamps the input characteristics onto the frames before zscale runs. zscale
        // fails outright with "no path between colorspaces" whenever transfer, matrix or
        // primaries is unspecified, and real HDR rips frequently carry partial metadata (a PQ
        // transfer tag with no primaries, for instance). zscale's own tin/min/pin options do not
        // resolve the path on FFmpeg 8.1; tagging upstream works for PQ, HLG and forced-SDR alike.
        var transferIn = Coalesce(source.ColorTransfer, isHdr ? "smpte2084" : "bt709");
        var matrixIn = Coalesce(source.ColorSpace, isHdr ? "bt2020nc" : "bt709");
        var primariesIn = Coalesce(source.ColorPrimaries, isHdr ? "bt2020" : "bt709");

        writer.AddChain(
            inputLabel,
            outputLabel,
            $"setparams=color_trc={transferIn}:colorspace={matrixIn}:color_primaries={primariesIn}",

            // Convert to linear light. npl=100 is the standard nominal peak for an SDR target.
            "zscale=t=linear:npl=100",

            // Tone-map in floating point to avoid banding in the highlights.
            "format=gbrpf32le",
            "zscale=p=bt709",
            $"tonemap=tonemap={context.ToneMapOperator}:desat=0",
            "zscale=t=bt709:m=bt709:r=tv",
            "format=yuv420p");
    }

    /// <summary>
    /// Treats FFmpeg's placeholders for missing metadata as absent: ffprobe reports
    /// <c>unknown</c> or <c>reserved</c> rather than omitting the field.
    /// </summary>
    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value)
        || value is "unknown" or "reserved" or "N/A" or "unspecified"
            ? fallback
            : value;
}

/// <summary>
/// Retimes the segment. The extractor compensates by reading <c>duration * speed</c> seconds of
/// source, so the rendered clip still lands on the requested length.
/// </summary>
public sealed class SpeedStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.Speed;

    public string Name => "speed";

    public bool AppliesTo(FilterContext context) => context.Look.HasSpeedChange;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel)
    {
        var speed = context.Look.Speed;
        writer.AddChain(
            inputLabel,
            outputLabel,
            $"setpts=PTS/{Format(speed)}");
    }

    internal static string Format(double value) =>
        value.ToString("0.#####", CultureInfo.InvariantCulture);
}

/// <summary>
/// Corrects non-square pixels before any aspect-ratio arithmetic.
/// <para>
/// Anamorphic sources (most DVDs, some broadcast captures) store 720x480 pixels meant to be
/// displayed as 16:9, and FFmpeg's <c>scale</c> works on stored dimensions and ignores the
/// sample aspect ratio, so skipping this silently produces horizontally squashed output.
/// </para>
/// </summary>
public sealed class SquarePixelStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.SquarePixels;

    public string Name => "square-pixels";

    public bool AppliesTo(FilterContext context) => context.Source.IsAnamorphic;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel) =>
        writer.AddChain(
            inputLabel,
            outputLabel,
            "scale=trunc(iw*sar/2)*2:ih",
            "setsar=1");
}

/// <summary>Scales the source into the target frame according to the chosen <see cref="FitMode"/>.</summary>
public sealed class FitStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.Fit;

    public string Name => "fit";

    public bool AppliesTo(FilterContext context) => true;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel)
    {
        var w = context.Format.Width;
        var h = context.Format.Height;

        switch (context.Format.Fit)
        {
            case FitMode.Crop:
                writer.AddChain(
                    inputLabel,
                    outputLabel,
                    $"scale={w}:{h}:force_original_aspect_ratio=increase",
                    $"crop={w}:{h}",
                    "setsar=1");
                break;

            case FitMode.Pad:
                writer.AddChain(
                    inputLabel,
                    outputLabel,
                    $"scale={w}:{h}:force_original_aspect_ratio=decrease",
                    $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:{context.Format.PadColor}",
                    "setsar=1");
                break;

            case FitMode.Stretch:
                writer.AddChain(inputLabel, outputLabel, $"scale={w}:{h}", "setsar=1");
                break;

            case FitMode.BlurPad:
                EmitBlurPad(writer, context, inputLabel, outputLabel, w, h);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(context), context.Format.Fit, "Unsupported fit mode.");
        }
    }

    /// <summary>
    /// The standard vertical-video treatment: a blurred, cropped copy fills the frame with the
    /// unmodified source centred on top. Branches, so it emits several chains.
    /// </summary>
    private static void EmitBlurPad(
        FilterGraphWriter writer,
        FilterContext context,
        string inputLabel,
        string outputLabel,
        int w,
        int h)
    {
        var background = writer.NewLabel();
        var foreground = writer.NewLabel();
        var blurred = writer.NewLabel();
        var scaled = writer.NewLabel();
        var sigma = SpeedStage.Format(context.Format.BlurPadSigma);

        writer.AddRaw($"[{inputLabel}]split=2[{background}][{foreground}]");

        writer.AddChain(
            background,
            blurred,
            $"scale={w}:{h}:force_original_aspect_ratio=increase",
            $"crop={w}:{h}",
            $"gblur=sigma={sigma}");

        var foregroundScale = Math.Clamp(context.Format.BlurPadForegroundScale, 1d, 4d);

        if (foregroundScale <= 1.001d)
        {
            // Plain fit: the whole frame is visible.
            writer.AddChain(
                foreground,
                scaled,
                $"scale={w}:{h}:force_original_aspect_ratio=decrease");
        }
        else
        {
            // Scale beyond the frame, then crop back to it: a very wide source fills more of a
            // very tall frame at the cost of some width, rather than mostly blurred filler.
            var scaledW = (int)Math.Round(w * foregroundScale);
            var scaledH = (int)Math.Round(h * foregroundScale);

            writer.AddChain(
                foreground,
                scaled,
                $"scale={scaledW}:{scaledH}:force_original_aspect_ratio=decrease",

                // min() keeps the crop valid when only one axis actually overflows.
                $"crop=min(iw\\,{w}):min(ih\\,{h})");
        }

        writer.AddRaw($"[{blurred}][{scaled}]overlay=(W-w)/2:(H-h)/2,setsar=1[{outputLabel}]");
    }
}

/// <summary>
/// Forces a constant frame rate.
/// <para>
/// Correctness, not just consistency: variable-frame-rate sources produce segments whose
/// timestamps do not line up, and the concat demuxer then drifts audio and video apart or
/// stalls on a long gap.
/// </para>
/// </summary>
public sealed class FrameRateStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.FrameRate;

    public string Name => "fps";

    public bool AppliesTo(FilterContext context) => context.Format.FrameRate > 0;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel) =>
        writer.AddChain(
            inputLabel,
            outputLabel,
            $"fps={SpeedStage.Format(context.Format.FrameRate)}");
}

/// <summary>Slow zoom across the segment, at the final output resolution.</summary>
public sealed class ZoomStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.Zoom;

    public string Name => "zoom";

    public bool AppliesTo(FilterContext context) => context.Look.HasZoom;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel)
    {
        var fps = context.Format.FrameRate > 0 ? context.Format.FrameRate : 30d;
        var frames = Math.Max(context.SegmentDuration.TotalSeconds * fps, 1d);

        // Pace the zoom so it reaches exactly ZoomEnd on the final frame.
        var perFrame = (context.Look.ZoomEnd - 1d) / frames;
        var target = SpeedStage.Format(context.Look.ZoomEnd);
        var step = perFrame.ToString("0.#########", CultureInfo.InvariantCulture);

        writer.AddChain(
            inputLabel,
            outputLabel,
            $"zoompan=z='min(zoom+{step},{target})'"
            + ":x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)'"
            + $":d=1:s={context.Format.Width}x{context.Format.Height}"
            + $":fps={SpeedStage.Format(fps)}");
    }
}

/// <summary>Mirrors the frame horizontally, vertically, or both.</summary>
public sealed class MirrorStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.Mirror;

    public string Name => "mirror";

    public bool AppliesTo(FilterContext context) =>
        context.Look.Mirror || context.Look.FlipVertical;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel)
    {
        var filters = new List<string>(2);

        if (context.Look.Mirror)
        {
            filters.Add("hflip");
        }

        if (context.Look.FlipVertical)
        {
            filters.Add("vflip");
        }

        writer.AddChain(inputLabel, outputLabel, [.. filters]);
    }
}

/// <summary>Guarantees an encoder-safe pixel format as the last step.</summary>
public sealed class OutputFormatStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.OutputFormat;

    public string Name => "output-format";

    public bool AppliesTo(FilterContext context) => true;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel) =>
        writer.AddChain(inputLabel, outputLabel, "format=yuv420p");
}
