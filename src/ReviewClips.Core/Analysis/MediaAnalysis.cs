using ReviewClips.Core.Media;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Analysis;

/// <summary>Knobs for the analysis pass. Part of the cache key, so changing them invalidates cached results.</summary>
public sealed record AnalysisSettings
{
    /// <summary>Frames per second to sample. 4 fps gives ~0.25s cut precision at a fraction of the decode cost.</summary>
    public int SampleFrameRate { get; init; } = 4;

    /// <summary>Width to downscale to before analysis. Cheap and has little effect on cut detection.</summary>
    public int SampleWidth { get; init; } = 320;

    /// <summary>scdet sensitivity (0-100). Lower finds more cuts.</summary>
    public double SceneThreshold { get; init; } = 8d;

    /// <summary>Minimum duration for a stretch of black to be recorded.</summary>
    public TimeSpan BlackMinDuration { get; init; } = TimeSpan.FromSeconds(0.3);

    /// <summary>Luma threshold below which a pixel counts as black (0-1).</summary>
    public double BlackPixelThreshold { get; init; } = 0.10;

    /// <summary>Minimum duration for a frozen/static stretch to be recorded.</summary>
    public TimeSpan FreezeMinDuration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>freezedetect noise tolerance, as a dB string understood by FFmpeg.</summary>
    public string FreezeNoiseTolerance { get; init; } = "-60dB";

    /// <summary>Use NVDEC/CUDA for the analysis decode. Has fixed init overhead, so it only pays off on long inputs.</summary>
    public bool UseHardwareDecode { get; init; }

    public string CacheDiscriminator =>
        $"v{MediaAnalysis.SchemaVersion}"
        + $":fps={SampleFrameRate}:w={SampleWidth}:scd={SceneThreshold:0.###}"
        + $":bd={BlackMinDuration.TotalSeconds:0.###}/{BlackPixelThreshold:0.###}"
        + $":fd={FreezeMinDuration.TotalSeconds:0.###}/{FreezeNoiseTolerance}";
}

/// <summary>
/// The cached result of scanning a source file: where the cuts are, how much motion each
/// moment has, and which stretches are unusable (black or frozen).
/// </summary>
public sealed record MediaAnalysis
{
    /// <summary>Bumped whenever the on-disk cache shape changes, to invalidate stale sidecars.</summary>
    public const int SchemaVersion = 1;

    public required string SourcePath { get; init; }

    public required long SourceSizeBytes { get; init; }

    public required DateTimeOffset SourceModifiedUtc { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>Timestamps where a new shot begins.</summary>
    public required IReadOnlyList<TimeSpan> SceneCuts { get; init; }

    public required IReadOnlyList<TimeRange> BlackRanges { get; init; }

    public required IReadOnlyList<TimeRange> FreezeRanges { get; init; }

    public required MotionCurve Motion { get; init; }

    public required AnalysisSettings Settings { get; init; }

    public DateTimeOffset AnalyzedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Ranges that should never be sampled: fades to black and static shots.</summary>
    public IEnumerable<TimeRange> UnusableRanges => BlackRanges.Concat(FreezeRanges);

    /// <summary>
    /// Derives shot boundaries from the scene cut list, bookended by 0 and the runtime.
    /// A "shot" is the stretch between two consecutive cuts.
    /// </summary>
    public IReadOnlyList<TimeRange> Shots()
    {
        var boundaries = new List<TimeSpan> { TimeSpan.Zero };
        boundaries.AddRange(SceneCuts.Where(c => c > TimeSpan.Zero && c < Duration));
        boundaries.Add(Duration);

        var shots = new List<TimeRange>(boundaries.Count);
        for (var i = 0; i < boundaries.Count - 1; i++)
        {
            if (boundaries[i + 1] > boundaries[i])
            {
                shots.Add(new TimeRange(boundaries[i], boundaries[i + 1]));
            }
        }

        return shots;
    }

    /// <summary>True when this analysis still describes the file currently on disk.</summary>
    public bool MatchesSource(MediaInfo info) =>
        SourceSizeBytes == info.FileSizeBytes
        && Math.Abs((SourceModifiedUtc - info.LastModifiedUtc).TotalSeconds) < 1
        && Math.Abs((Duration - info.Duration).TotalSeconds) < 1;
}
