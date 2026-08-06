using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Options;

public enum SelectionStrategy
{
    /// <summary>Evenly spaced across the eligible runtime. Predictable and cheap.</summary>
    Uniform,

    /// <summary>Random positions, reproducible via <see cref="SelectionOptions.Seed"/>.</summary>
    Random,

    /// <summary>Starts each clip just after a detected cut, so segments begin on a fresh shot.</summary>
    Scene,

    /// <summary>Scene-aligned, then ranked by a motion-suitability score. The default.</summary>
    Scored,

    /// <summary>Driven by an explicit list of timestamps you supply.</summary>
    Cues,
}

public enum ChapterSkipMode
{
    /// <summary>
    /// Skip chapters whose title reads as an opening sequence, end credits, or recycled
    /// footage. See <see cref="Media.ChapterFilter.IntroOutroPatterns"/>.
    /// </summary>
    Auto,

    /// <summary>Consult no built-in titles. Explicit patterns still apply.</summary>
    Off,
}

public enum SegmentOrder
{
    /// <summary>Segments appear in the order they occur in the source.</summary>
    Chronological,

    /// <summary>Segments are shuffled using the seeded RNG.</summary>
    Shuffled,
}

/// <summary>
/// Tuning for <see cref="SelectionStrategy.Scored"/>.
/// <para>
/// Motion thresholds are multiples of the title's own median motion, not absolute
/// frame-difference values, so one set of defaults works across titles of any pace.
/// </para>
/// </summary>
public sealed record ScoringOptions
{
    /// <summary>Ideal motion level as a multiple of the title's median.</summary>
    public double TargetMotionMultiple { get; init; } = 1.25d;

    /// <summary>Reject windows quieter than this multiple of the median (near-static shots).</summary>
    public double MotionFloorMultiple { get; init; } = 0.35d;

    /// <summary>Reject windows busier than this multiple of the median (frantic action, strobing).</summary>
    public double MotionCeilingMultiple { get; init; } = 4.0d;

    /// <summary>
    /// How strongly to penalise uneven windows. High variance usually means the window
    /// straddles a cut or contains a flash.
    /// </summary>
    public double StabilityWeight { get; init; } = 0.35d;
}

public sealed record SelectionOptions
{
    public SelectionStrategy Strategy { get; init; } = SelectionStrategy.Scored;

    /// <summary>Seed for reproducible renders. Null generates one and records it in the manifest.</summary>
    public int? Seed { get; init; }

    /// <summary>Skipped from the start of every source, to clear logos and opening titles.</summary>
    public Offset SkipHead { get; init; } = Offset.FromPercent(5);

    /// <summary>Skipped from the end of every source, to clear end credits.</summary>
    public Offset SkipTail { get; init; } = Offset.FromPercent(8);

    /// <summary>When non-empty, only these ranges are eligible.</summary>
    public IReadOnlyList<TimeRange> IncludeRanges { get; init; } = [];

    /// <summary>Always excluded.</summary>
    public IReadOnlyList<TimeRange> ExcludeRanges { get; init; } = [];

    /// <summary>
    /// Whether the built-in intro and credits chapter titles are skipped. On by default: named
    /// chapters locate that material exactly, where <see cref="SkipHead"/> and
    /// <see cref="SkipTail"/> can only approximate it.
    /// </summary>
    public ChapterSkipMode ChapterSkip { get; init; } = ChapterSkipMode.Auto;

    /// <summary>
    /// Extra chapter title patterns to skip, always honoured regardless of
    /// <see cref="ChapterSkip"/>. Matched as case-insensitive substrings, or as globs when the
    /// pattern contains <c>*</c> or <c>?</c>.
    /// </summary>
    public IReadOnlyList<string> SkipChapterPatterns { get; init; } = [];

    /// <summary>Minimum spacing between the starts of two chosen segments.</summary>
    public TimeSpan MinGap { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Skip stretches detected as fades to black.</summary>
    public bool RejectBlack { get; init; } = true;

    /// <summary>Skip stretches detected as frozen or static.</summary>
    public bool RejectFrozen { get; init; } = true;

    /// <summary>How far into a shot to start, so a clip doesn't open on the cut frame itself.</summary>
    public TimeSpan SceneLeadIn { get; init; } = TimeSpan.FromSeconds(0.35);

    /// <summary>Shots shorter than this are ignored by scene-aware selection.</summary>
    public TimeSpan MinShotDuration { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Explicit timestamps for <see cref="SelectionStrategy.Cues"/>.</summary>
    public IReadOnlyList<TimeSpan> Cues { get; init; } = [];

    /// <summary>Snap each cue back to the start of the shot containing it.</summary>
    public bool SnapCuesToScene { get; init; } = true;

    public ScoringOptions Scoring { get; init; } = new();

    /// <summary>Candidate windows evaluated per source for grid-based strategies.</summary>
    public int CandidateDensity { get; init; } = 400;

    /// <summary>
    /// Order of segments in the finished render. Shuffled by default: original sequence reads as
    /// a condensed copy of the work rather than as background footage.
    /// </summary>
    public SegmentOrder Order { get; init; } = SegmentOrder.Shuffled;

    /// <summary>
    /// Every chapter title pattern in force: the built-in set unless disabled, plus whatever the
    /// user supplied. Computed here so selection and the plan report cannot disagree.
    /// </summary>
    public IReadOnlyList<string> EffectiveChapterPatterns =>
        ChapterSkip == ChapterSkipMode.Auto
            ? [.. Media.ChapterFilter.IntroOutroPatterns, .. SkipChapterPatterns]
            : SkipChapterPatterns;

    /// <summary>True when the chosen strategy needs a (potentially slow) analysis pass.</summary>
    public bool RequiresAnalysis =>
        Strategy is SelectionStrategy.Scene or SelectionStrategy.Scored
        || RejectBlack
        || RejectFrozen
        || (Strategy is SelectionStrategy.Cues && SnapCuesToScene);
}
