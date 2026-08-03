using ReviewClips.Core.Analysis;
using ReviewClips.Core.Media;
using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Tests.Selection;

internal static class SelectionTestData
{
    public static MediaInfo Info(
        double durationSeconds,
        string path = "/movies/film.mkv",
        IReadOnlyList<Chapter>? chapters = null) => new()
    {
        Path = path,
        FileSizeBytes = 1_000_000,
        LastModifiedUtc = DateTimeOffset.UnixEpoch,
        Duration = TimeSpan.FromSeconds(durationSeconds),
        Width = 1920,
        Height = 1080,
        FrameRate = 24,
        VideoCodec = "h264",
        PixelFormat = "yuv420p",
        SampleAspectRatio = Ratio.One,
        HasAudio = true,
        Chapters = chapters ?? [],
    };

    /// <summary>Builds contiguous chapters from <c>(title, endSeconds)</c> pairs.</summary>
    public static IReadOnlyList<Chapter> Chapters(params (string? Title, double EndSeconds)[] parts)
    {
        var chapters = new List<Chapter>(parts.Length);
        var start = 0d;

        for (var i = 0; i < parts.Length; i++)
        {
            chapters.Add(new Chapter
            {
                Index = i,
                Range = Range(start, parts[i].EndSeconds),
                Title = parts[i].Title,
            });

            start = parts[i].EndSeconds;
        }

        return chapters;
    }

    /// <summary>
    /// Builds an analysis with a synthetic motion curve. <paramref name="motionAt"/> maps a
    /// timestamp to a mafd value, letting a test shape the "interest" landscape directly.
    /// </summary>
    public static MediaAnalysis Analysis(
        MediaInfo info,
        IEnumerable<double>? cutsAtSeconds = null,
        IEnumerable<TimeRange>? black = null,
        IEnumerable<TimeRange>? frozen = null,
        Func<double, double>? motionAt = null,
        double sampleRate = 4)
    {
        var samples = new List<MotionSample>();
        var step = 1d / sampleRate;

        for (var t = 0d; t < info.Duration.TotalSeconds; t += step)
        {
            samples.Add(new MotionSample(t, motionAt?.Invoke(t) ?? 1.0d));
        }

        return new MediaAnalysis
        {
            SourcePath = info.Path,
            SourceSizeBytes = info.FileSizeBytes,
            SourceModifiedUtc = info.LastModifiedUtc,
            Duration = info.Duration,
            SceneCuts = (cutsAtSeconds ?? []).Select(TimeSpan.FromSeconds).ToList(),
            BlackRanges = (black ?? []).ToList(),
            FreezeRanges = (frozen ?? []).ToList(),
            Motion = new MotionCurve(samples),
            Settings = new AnalysisSettings(),
        };
    }

    public static SelectionContext Context(
        IReadOnlyList<SourceMedia> sources,
        int segmentCount,
        double segmentSeconds = 5,
        SelectionOptions? options = null,
        int seed = 42) => new()
        {
            Sources = sources,
            Options = options ?? new SelectionOptions(),
            SegmentDurations = Enumerable.Repeat(TimeSpan.FromSeconds(segmentSeconds), segmentCount).ToList(),
            Random = new Random(seed),
        };

    public static TimeRange Range(double start, double end) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));

    /// <summary>Smallest gap between consecutive segment starts, in seconds.</summary>
    public static double MinimumGapSeconds(IReadOnlyList<Segment> segments)
    {
        if (segments.Count < 2)
        {
            return double.MaxValue;
        }

        var starts = segments.Select(s => s.Start.TotalSeconds).OrderBy(s => s).ToList();
        return Enumerable.Range(0, starts.Count - 1).Min(i => starts[i + 1] - starts[i]);
    }
}
