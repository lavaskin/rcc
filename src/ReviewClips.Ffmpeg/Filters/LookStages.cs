using System.Globalization;

namespace ReviewClips.Ffmpeg.Filters;

/// <summary>
/// Brightness, contrast and saturation.
/// <para>
/// The defaults darken and desaturate, which is the point of the tool: footage sitting behind
/// narration should recede. Bright, saturated B-roll competes with your captions and voice and
/// tends to hurt retention rather than help it.
/// </para>
/// </summary>
public sealed class LookStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.Look;

    public string Name => "look";

    public bool AppliesTo(FilterContext context) => context.Look.AltersColor;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel)
    {
        var look = context.Look;
        var filters = new List<string>(2);

        if (look.Darken > 0.001d)
        {
            // Darkening scales luma multiplicatively about the black pedestal:
            //   y = minval + (val - minval) * (1 - darken)
            //
            // Three deliberate choices here.
            //
            // Multiplicative, not additive: eq's 'brightness' subtracts a constant, which
            // crushes everything below it to pure black. On a film with dark scenes that drove
            // the median pixel to 0 and made the footage unusable. Scaling preserves shadow
            // structure and behaves the same regardless of how bright the scene is.
            //
            // lutyuv, not colorlevels: colorlevels is the more obvious choice and gives nearly
            // identical numbers, but it segfaults FFmpeg 8.1 in this position in the graph.
            // lutyuv is a plain lookup table, so it is both robust and fast.
            //
            // Anchored on minval and luma-only: this keeps the black pedestal legal, works
            // at any bit depth, and leaves chroma untouched so colours do not wash out.
            var scale = Num(Math.Clamp(1d - look.Darken, 0d, 1d));
            filters.Add($"lutyuv=y=minval+(val-minval)*{scale}");
        }

        var eq = new List<string>(2);

        if (Math.Abs(look.Saturation - 1d) > 0.001d)
        {
            eq.Add($"saturation={Num(Math.Clamp(look.Saturation, 0d, 3d))}");
        }

        if (Math.Abs(look.Contrast - 1d) > 0.001d)
        {
            eq.Add($"contrast={Num(Math.Clamp(look.Contrast, 0d, 3d))}");
        }

        if (eq.Count > 0)
        {
            filters.Add("eq=" + string.Join(':', eq));
        }

        if (filters.Count == 0)
        {
            writer.AddChain(inputLabel, outputLabel);
            return;
        }

        writer.AddChain(inputLabel, outputLabel, [.. filters]);
    }

    internal static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}

public sealed class BlurStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.Blur;

    public string Name => "blur";

    public bool AppliesTo(FilterContext context) => context.Look.Blur > 0.01d;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel) =>
        writer.AddChain(inputLabel, outputLabel, $"gblur=sigma={LookStage.Num(context.Look.Blur)}");
}

public sealed class VignetteStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.Vignette;

    public string Name => "vignette";

    public bool AppliesTo(FilterContext context) => context.Look.Vignette;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel) =>
        writer.AddChain(inputLabel, outputLabel, "vignette=PI/5");
}

/// <summary>Adds film grain. Temporal noise also helps mask compression artefacts on dark footage.</summary>
public sealed class GrainStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.Grain;

    public string Name => "grain";

    public bool AppliesTo(FilterContext context) => context.Look.Grain > 0;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel)
    {
        var strength = Math.Clamp(context.Look.Grain, 1, 100);
        writer.AddChain(inputLabel, outputLabel, $"noise=alls={strength}:allf=t+u");
    }
}

/// <summary>Applies a 3D LUT for colour grading.</summary>
public sealed class LutStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.Lut;

    public string Name => "lut";

    public bool AppliesTo(FilterContext context) => !string.IsNullOrWhiteSpace(context.Look.LutPath);

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel)
    {
        // Colons and backslashes are structural in filter syntax and must be escaped in paths.
        var path = FilterEscaping.EscapeValue(context.Look.LutPath!);
        writer.AddChain(inputLabel, outputLabel, $"lut3d=file={path}");
    }
}

/// <summary>Composites a still image over every segment (watermark, texture, grade overlay).</summary>
public sealed class OverlayStage : IVideoFilterStage
{
    public int Order => FilterStageOrder.Overlay;

    public string Name => "overlay";

    public bool AppliesTo(FilterContext context) =>
        !string.IsNullOrWhiteSpace(context.Look.OverlayPath) && context.OverlayInputIndex.HasValue;

    public void Emit(FilterGraphWriter writer, FilterContext context, string inputLabel, string outputLabel)
    {
        var index = context.OverlayInputIndex!.Value;
        var scaled = writer.NewLabel();
        var faded = writer.NewLabel();
        var opacity = Math.Clamp(context.Look.OverlayOpacity, 0d, 1d);

        // Stretch the overlay to the output frame and apply opacity via the alpha channel.
        writer.AddChain(
            $"{index}:v",
            scaled,
            $"scale={context.Format.Width}:{context.Format.Height}",
            "format=rgba");

        writer.AddChain(
            scaled,
            faded,
            $"colorchannelmixer=aa={LookStage.Num(opacity)}");

        writer.AddRaw($"[{inputLabel}][{faded}]overlay=0:0:shortest=1[{outputLabel}]");
    }
}

public static class FilterEscaping
{
    /// <summary>
    /// Escapes a value for use inside a filter argument. Backslash, colon, comma, brackets and
    /// quotes all carry meaning in FFmpeg's filter grammar.
    /// </summary>
    public static string EscapeValue(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
}
