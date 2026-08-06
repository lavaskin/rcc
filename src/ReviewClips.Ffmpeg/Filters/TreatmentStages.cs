using System.Globalization;
using ReviewClips.Core.Options;

namespace ReviewClips.Ffmpeg.Filters;

/// <summary>
/// Unsharp mask, mostly for recovering a little definition from a soft upscale such as a 720p
/// source pushed into a 1080p frame.
/// </summary>
public sealed class SharpenStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.Sharpen;

    public string Name => "sharpen";

    public bool AppliesTo(FilterContext context) => context.Look.HasSharpen;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel)
    {
        // 5x5 luma matrix, chroma left alone. Sharpening chroma on heavily compressed footage
        // amplifies the blocking artefacts rather than the detail.
        var amount = LookStage.Num(Math.Clamp(context.Look.Sharpen, 0d, 3d));
        writer.AddChain(inputLabel, outputLabel, $"unsharp=5:5:{amount}:5:5:0");
    }
}

/// <summary>Downscales and re-upscales with nearest-neighbour for a chunky, blocky look.</summary>
public sealed class PixelateStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.Pixelate;

    public string Name => "pixelate";

    public bool AppliesTo(FilterContext context) => context.Look.HasPixelate;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel)
    {
        var factor = Math.Clamp(context.Look.Pixelate, 1.1d, 16d);

        // Nearest-neighbour both ways: down so blocks average cleanly, up so they stay
        // hard-edged instead of being smoothed back into a blur. Whole pixels rather than an
        // iw/N expression so the result is always even — an odd intermediate dimension makes the
        // yuv420p conversion at the end of the graph fail outright.
        var width = Even(context.Format.Width / factor);
        var height = Even(context.Format.Height / factor);

        writer.AddChain(
            inputLabel,
            outputLabel,
            $"scale={width}:{height}:flags=neighbor",
            $"scale={context.Format.Width}:{context.Format.Height}:flags=neighbor",
            "setsar=1");
    }

    private static int Even(double value) =>
        Math.Max(2, (int)Math.Round(value / 2d) * 2);
}

/// <summary>
/// Fades every clip in from and out to black.
/// <para>
/// Not <c>--transition</c>: that overlaps two neighbouring clips and forces the filter-graph
/// stitcher, whereas this happens inside each segment during extraction, so the finished render
/// can still be joined by a stream copy.
/// </para>
/// </summary>
public sealed class FadeEdgesStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.FadeEdges;

    public string Name => "fade-edges";

    public bool AppliesTo(FilterContext context) => context.Look.HasFadeEdges;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel)
    {
        var total = context.SegmentDuration.TotalSeconds;

        // Two fades have to fit inside the clip. Past half its length they would meet in the
        // middle and the clip would never reach full brightness.
        var requested = context.Look.FadeEdges.TotalSeconds;
        var duration = Math.Clamp(requested, 0.01d, Math.Max(0.02d, total / 2d));
        var outStart = Math.Max(0d, total - duration);

        writer.AddChain(
            inputLabel,
            outputLabel,
            $"fade=t=in:st=0:d={Num(duration)}",
            $"fade=t=out:st={Num(outStart)}:d={Num(duration)}");
    }

    private static string Num(double value) =>
        value.ToString("0.#####", CultureInfo.InvariantCulture);
}

/// <summary>
/// Burns a credit or disclaimer line into a corner of the frame.
/// <para>
/// The text goes to <c>drawtext</c> through <c>textfile=</c> rather than <c>text=</c> because
/// film titles contain the characters FFmpeg's filter grammar reserves: the colon in
/// <c>Léon: The Professional</c> or the apostrophe in <c>Ocean's Eleven</c> terminates the
/// argument early and fails the whole graph, and escaping them correctly needs three
/// simultaneous levels of quoting. <c>expansion=none</c> likewise stops <c>%</c> and <c>{}</c>
/// in a title being read as strftime and expression syntax.
/// </para>
/// </summary>
public sealed class AttributionStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.Attribution;

    public string Name => "attribution";

    /// <summary>
    /// Requires both the text and a materialised file for it. The extractor owns writing the
    /// file; without one the stage is skipped rather than falling back to inline text.
    /// </summary>
    public bool AppliesTo(FilterContext context) =>
        context.Look.HasAttribution && !string.IsNullOrWhiteSpace(context.AttributionTextPath);

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel)
    {
        var look = context.Look;
        var height = context.Format.Height;
        var width = context.Format.Width;

        // A fraction of the frame height, shrunk for long lines: drawtext cannot wrap, so a long
        // title at 1/28th of the height would simply run off the side of a narrow frame.
        var text = look.Attribution!.Trim();
        var byHeight = height / 28d;
        var byWidth = 1.7d * width / Math.Max(text.Length, 1);
        var size = Math.Max(12d, Math.Min(byHeight, byWidth));

        var margin = Math.Max(12d, height / 40d);
        var (x, y) = Anchor(look.AttributionPosition, margin);

        var path = FilterEscaping.EscapeValue(context.AttributionTextPath!);

        writer.AddChain(
            inputLabel,
            outputLabel,
            $"drawtext=textfile={path}"
            + ":expansion=none"
            + $":fontsize={Num(size)}"
            + ":fontcolor=white@0.75"
            + $":x={x}:y={y}"

            // A plain white line disappears over a bright frame. A drop shadow keeps it legible
            // on any footage without the heaviness of a filled box.
            + ":shadowcolor=black@0.7:shadowx=2:shadowy=2");
    }

    private static (string X, string Y) Anchor(TextPosition position, double margin)
    {
        var m = Num(margin);

        return position switch
        {
            TextPosition.BottomLeft => (m, $"h-th-{m}"),
            TextPosition.TopLeft => (m, m),
            TextPosition.TopRight => ($"w-tw-{m}", m),
            TextPosition.Top => ("(w-tw)/2", m),
            TextPosition.Bottom => ("(w-tw)/2", $"h-th-{m}"),
            TextPosition.Center => ("(w-tw)/2", "(h-th)/2"),
            _ => ($"w-tw-{m}", $"h-th-{m}"),
        };
    }

    private static string Num(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
