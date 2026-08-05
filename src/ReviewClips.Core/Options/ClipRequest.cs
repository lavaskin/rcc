using ReviewClips.Core.Analysis;

namespace ReviewClips.Core.Options;

/// <summary>A fully resolved render request. Everything the pipeline needs, with no CLI concepts left.</summary>
public sealed record ClipRequest
{
    /// <summary>Resolved absolute paths to source media, in the order they were discovered.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    public required string OutputPath { get; init; }

    /// <summary>Target runtime of the finished render.</summary>
    public required TimeSpan TargetDuration { get; init; }

    /// <summary>Nominal length of each spliced segment.</summary>
    public TimeSpan SpliceLength { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Random variation applied to each splice length, plus or minus. A perfectly regular
    /// cadence is noticeably robotic, so a little jitter reads as a human edit.
    /// </summary>
    public TimeSpan SpliceJitter { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Caps how many <em>distinct</em> clips are cut, repeating them to fill the runtime.
    /// <c>null</c> means every slot gets its own clip.
    /// <para>
    /// This decouples the length of the render from the amount of source footage consumed.
    /// A 16-minute render normally takes over 17 minutes of the original; capping at 50 clips
    /// takes about 4 minutes of it instead.
    /// </para>
    /// </summary>
    public int? MaxDistinctClips { get; init; }

    /// <summary>
    /// Share of the combined source runtime this render may consume before it is remarked upon,
    /// measured as the union of the referenced ranges rather than as clip count times clip
    /// length. <c>0</c> disables the check.
    /// </summary>
    public double MaxSourceFraction { get; init; } = Planning.SourceUsageGuard.DefaultLimit;

    /// <summary>
    /// Turn <see cref="MaxSourceFraction"/> from a warning into a refusal.
    /// <para>
    /// Off by default. A hard failure at 10% refuses ordinary short renders outright — a 30s
    /// background track cut from a three-minute source is 16.7% and would simply not run — so
    /// the default surfaces the exposure without breaking the common case.
    /// </para>
    /// </summary>
    public bool EnforceMaxSourceFraction { get; init; }

    public SelectionOptions Selection { get; init; } = new();

    public OutputFormat Format { get; init; } = OutputFormat.Youtube;

    public LookOptions Look { get; init; } = new();

    public TransitionOptions Transition { get; init; } = new();

    public EncoderOptions Encoder { get; init; } = new();

    public AnalysisSettings Analysis { get; init; } = new();

    /// <summary>
    /// What the finished render should sound like. Muted by default: this footage sits under
    /// podcast narration, and a second audio bed fighting your voice is worse than silence.
    /// </summary>
    public AudioOptions Audio { get; init; } = new();

    /// <summary>
    /// Whether the render carries no audio at all. A passthrough over <see cref="Audio"/> so
    /// the extractor, the stitchers and the manifest keep asking the one question they care
    /// about without knowing where the audio would have come from.
    /// </summary>
    public bool Mute => Audio.IsMuted;

    /// <summary>Concurrent FFmpeg extraction processes.</summary>
    public int Parallelism { get; init; } = 4;

    /// <summary>
    /// Re-analyse sources even when a cached result exists, and overwrite it. The cache key
    /// already covers file identity and every analysis setting, so this is only needed to
    /// recover from a stale entry or to measure scan cost.
    /// </summary>
    public bool IgnoreAnalysisCache { get; init; }

    /// <summary>Print the plan and the exact FFmpeg commands without rendering.</summary>
    public bool DryRun { get; init; }

    /// <summary>Leave the intermediate segment files on disk for inspection.</summary>
    public bool KeepTemporaryFiles { get; init; }

    /// <summary>Where to write the reproducibility manifest, if anywhere.</summary>
    public string? ManifestPath { get; init; }

    /// <summary>Overrides the temp directory used for intermediate segments.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Number of segments needed to fill <see cref="TargetDuration"/> at the nominal splice length.</summary>
    public int EstimatedSegmentCount =>
        SpliceLength <= TimeSpan.Zero
            ? 0
            : Math.Max(1, (int)Math.Ceiling(TargetDuration.TotalSeconds / SpliceLength.TotalSeconds));
}
