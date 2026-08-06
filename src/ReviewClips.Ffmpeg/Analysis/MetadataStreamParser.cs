using System.Globalization;
using ReviewClips.Core.Analysis;

namespace ReviewClips.Ffmpeg.Analysis;

/// <summary>
/// Streams the per-frame metadata that <c>metadata=print:file=-</c> writes to stdout.
/// <para>
/// The format alternates a frame header with zero or more <c>key=value</c> lines:
/// </para>
/// <code>
/// frame:0    pts:0       pts_time:0
/// lavfi.scd.mafd=0.000
/// lavfi.scd.score=0.000
/// </code>
/// <para>
/// Note the stdout form uses <c>key=value</c>, while the same data logged to stderr uses
/// <c>key: value</c>.
/// </para>
/// <para>
/// Reading from stdout rather than a temp file is load-bearing: FFmpeg reopens the target of
/// <c>file=</c> in truncate mode whenever the filter graph is reinitialized, which any
/// mid-stream format change triggers — concatenated files routinely do — leaving only the
/// frames after the final reinit. Streaming is immune, since nothing can be rewound.
/// </para>
/// <para>
/// Frame indices reset on reinitialization, so only <c>pts_time</c> is trusted for positioning.
/// </para>
/// </summary>
public sealed class MetadataStreamParser
{
    private const string MafdKey = "lavfi.scd.mafd";
    private const string FrameMarker = "frame:";
    private const string TimeMarker = "pts_time:";

    private readonly List<MotionSample> _samples = [];
    private double? _currentTime;

    /// <summary>Timestamp of the most recent frame seen, used to derive progress.</summary>
    public double? CurrentTimeSeconds => _currentTime;

    public int SampleCount => _samples.Count;

    public void Feed(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        if (line.StartsWith(FrameMarker, StringComparison.Ordinal))
        {
            _currentTime = ExtractPtsTime(line);
            return;
        }

        if (_currentTime is null || line[0] != 'l')
        {
            return;
        }

        var separator = line.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return;
        }

        if (!line.AsSpan(0, separator).Equals(MafdKey, StringComparison.Ordinal))
        {
            return;
        }

        if (double.TryParse(
                line.AsSpan(separator + 1),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var mafd))
        {
            _samples.Add(new MotionSample(_currentTime.Value, mafd));
        }
    }

    public MotionCurve ToCurve() => new(_samples);

    /// <summary>Pulls <c>pts_time:</c> out of a frame header line.</summary>
    internal static double? ExtractPtsTime(string line)
    {
        var index = line.IndexOf(TimeMarker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var span = line.AsSpan(index + TimeMarker.Length).TrimStart();

        var end = 0;
        while (end < span.Length && !char.IsWhiteSpace(span[end]))
        {
            end++;
        }

        return double.TryParse(
            span[..end],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }
}
