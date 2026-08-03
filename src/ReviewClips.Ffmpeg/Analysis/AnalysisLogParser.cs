using System.Globalization;
using System.Text.RegularExpressions;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Ffmpeg.Analysis;

/// <summary>
/// Parses the detector output FFmpeg writes to stderr during the analysis pass.
/// <para>
/// The exact shapes, verified against FFmpeg 8.1:
/// </para>
/// <code>
/// [Parsed_scdet_2 @ ...] lavfi.scd.score: 36.217, lavfi.scd.time: 4
/// [Parsed_freezedetect_3 @ ...] lavfi.freezedetect.freeze_start: 4
/// [Parsed_blackdetect_4 @ ...] black_start:4 black_end:6.75 black_duration:2.75
/// </code>
/// <para>
/// These are emitted at <c>info</c> level, so the analysis pass must not be run with
/// <c>-loglevel error</c>.
/// </para>
/// </summary>
public sealed partial class AnalysisLogParser
{
    private readonly List<TimeSpan> _sceneCuts = [];
    private readonly List<TimeRange> _blackRanges = [];
    private readonly List<TimeRange> _freezeRanges = [];
    private TimeSpan? _pendingFreezeStart;

    [GeneratedRegex(
        @"lavfi\.scd\.time:\s*(?<t>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SceneCutPattern { get; }

    [GeneratedRegex(
        @"black_start:(?<start>[0-9]+(?:\.[0-9]+)?)\s+black_end:(?<end>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex BlackPattern { get; }

    [GeneratedRegex(
        @"freeze_(?<kind>start|end|duration):\s*(?<value>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex FreezePattern { get; }

    public IReadOnlyList<TimeSpan> SceneCuts => _sceneCuts;

    public IReadOnlyList<TimeRange> BlackRanges => _blackRanges;

    public IReadOnlyList<TimeRange> FreezeRanges => _freezeRanges;

    public void Feed(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        var scene = SceneCutPattern.Match(line);
        if (scene.Success && TryParse(scene.Groups["t"].Value, out var cutAt))
        {
            _sceneCuts.Add(TimeSpan.FromSeconds(cutAt));
        }

        var black = BlackPattern.Match(line);
        if (black.Success
            && TryParse(black.Groups["start"].Value, out var blackStart)
            && TryParse(black.Groups["end"].Value, out var blackEnd)
            && blackEnd > blackStart)
        {
            _blackRanges.Add(new TimeRange(
                TimeSpan.FromSeconds(blackStart),
                TimeSpan.FromSeconds(blackEnd)));
        }

        foreach (var freeze in FreezePattern.Matches(line).Cast<Match>())
        {
            if (!TryParse(freeze.Groups["value"].Value, out var value))
            {
                continue;
            }

            switch (freeze.Groups["kind"].Value)
            {
                case "start":
                    _pendingFreezeStart = TimeSpan.FromSeconds(value);
                    break;

                case "end" when _pendingFreezeStart is { } start:
                    var end = TimeSpan.FromSeconds(value);
                    if (end > start)
                    {
                        _freezeRanges.Add(new TimeRange(start, end));
                    }

                    _pendingFreezeStart = null;
                    break;
            }
        }
    }

    /// <summary>
    /// Closes any freeze region still open at end of stream.
    /// <para>
    /// freezedetect reports <c>freeze_start</c> immediately but only reports the matching
    /// <c>freeze_end</c> when motion resumes. A static shot that runs to the end of the file
    /// therefore never gets an end, and would be silently dropped without this.
    /// </para>
    /// </summary>
    public void Complete(TimeSpan duration)
    {
        if (_pendingFreezeStart is { } start && duration > start)
        {
            _freezeRanges.Add(new TimeRange(start, duration));
            _pendingFreezeStart = null;
        }
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
