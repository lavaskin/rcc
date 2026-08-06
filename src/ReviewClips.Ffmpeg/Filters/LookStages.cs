using System.Globalization;

namespace ReviewClips.Ffmpeg.Filters;

/// <summary>
/// Brightness, contrast and saturation. The defaults darken and desaturate, so footage sitting
/// behind narration recedes rather than competing with it.
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
            // Multiplicative, not additive: eq's 'brightness' subtracts a constant, crushing
            // everything below it to pure black, whereas scaling preserves shadow structure at
            // any scene brightness. lutyuv, not colorlevels: colorlevels gives near-identical
            // numbers but segfaults FFmpeg 8.1 in this position in the graph. Anchoring on
            // minval keeps the black pedestal legal at any bit depth, and staying luma-only
            // leaves chroma untouched so colours do not wash out.
            var scale = Num(Math.Clamp(1d - look.Darken, 0d, 1d));
            filters.Add($"lutyuv=y=minval+(val-minval)*{scale}");
        }

        var eq = new List<string>(3);

        if (Math.Abs(look.EffectiveSaturation - 1d) > 0.001d)
        {
            eq.Add($"saturation={Num(Math.Clamp(look.EffectiveSaturation, 0d, 3d))}");
        }

        if (Math.Abs(look.Contrast - 1d) > 0.001d)
        {
            eq.Add($"contrast={Num(Math.Clamp(look.Contrast, 0d, 3d))}");
        }

        if (Math.Abs(look.Gamma - 1d) > 0.001d)
        {
            // eq's gamma is a true power curve: unlike its brightness it scales rather than
            // offsets, so it does not crush the shadows.
            eq.Add($"gamma={Num(Math.Clamp(look.Gamma, 0.1d, 10d))}");
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
