using ReviewClips.Core.Media;
using ReviewClips.Core.Options;

namespace ReviewClips.Ffmpeg.Filters;

/// <summary>Everything a filter stage needs to decide whether and how to apply itself.</summary>
public sealed record FilterContext
{
    public required MediaInfo Source { get; init; }

    public required OutputFormat Format { get; init; }

    public required LookOptions Look { get; init; }

    /// <summary>Output duration of the segment. Needed to pace time-based effects like Ken Burns.</summary>
    public required TimeSpan SegmentDuration { get; init; }

    /// <summary>FFmpeg input index of the overlay image, when <see cref="LookOptions.OverlayPath"/> is set.</summary>
    public int? OverlayInputIndex { get; init; }

    /// <summary>
    /// Path to the file holding <see cref="LookOptions.Attribution"/>, when one has been
    /// written. Supplied by the caller for the same reason as <see cref="OverlayInputIndex"/>:
    /// the graph builder describes a graph and does not touch the disk.
    /// </summary>
    public string? AttributionTextPath { get; init; }

    /// <summary>
    /// True when the source needs tone-mapping: an HDR transfer function, unless the user
    /// explicitly disabled it.
    /// </summary>
    public bool NeedsToneMapping =>
        Format.ToneMap != ToneMapMode.None
        && (Format.ToneMap != ToneMapMode.Auto || Source.IsHdr);

    /// <summary>The operator to use once tone-mapping is known to apply.</summary>
    public string ToneMapOperator => Format.ToneMap switch
    {
        ToneMapMode.Hable => "hable",
        ToneMapMode.Mobius => "mobius",
        ToneMapMode.Reinhard => "reinhard",
        _ => "hable",
    };
}

/// <summary>
/// Accumulates the chains of a <c>-filter_complex</c> graph and hands out unique
/// intermediate labels.
/// </summary>
public sealed class FilterGraphWriter
{
    private readonly List<string> _chains = [];
    private int _nextLabel;

    public IReadOnlyList<string> Chains => _chains;

    /// <summary>Allocates a unique intermediate label, e.g. <c>fx3</c>.</summary>
    public string NewLabel() => $"fx{_nextLabel++}";

    /// <summary>Adds a linear chain consuming <paramref name="input"/> and producing <paramref name="output"/>.</summary>
    public void AddChain(string input, string output, params string[] filters)
    {
        var used = filters.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
        if (used.Length == 0)
        {
            // Nothing to do: alias the labels with a no-op so the graph stays connected.
            _chains.Add($"[{input}]null[{output}]");
            return;
        }

        _chains.Add($"[{input}]{string.Join(',', used)}[{output}]");
    }

    /// <summary>Adds a pre-formed chain for stages that need multiple inputs or branches.</summary>
    public void AddRaw(string chain)
    {
        if (!string.IsNullOrWhiteSpace(chain))
        {
            _chains.Add(chain);
        }
    }

    public string Build() => string.Join(';', _chains);
}
