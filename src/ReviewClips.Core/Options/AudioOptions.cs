namespace ReviewClips.Core.Options;

public enum AudioMode
{
    /// <summary>Default. RCC is primarily used for a visual complement to a narrative.</summary>
    Mute,

    /// <summary>Keep each clip's own audio from the source.</summary>
    Source,

    /// <summary>Mux in a single external track over the whole render.</summary>
    External,
}

/// <summary>What, if anything, the finished render should sound like.</summary>
public sealed record AudioOptions
{
    public static AudioOptions Muted { get; } = new();

    public AudioMode Mode { get; init; } = AudioMode.Mute;

    /// <summary>The track to mux, when <see cref="Mode"/> is <see cref="AudioMode.External"/>.</summary>
    public string? ExternalPath { get; init; }

    /// <summary>How far into the external track to start.</summary>
    public TimeSpan Offset { get; init; }

    /// <summary>Linear gain applied to the muxed track. 1 leaves it untouched.</summary>
    public double Volume { get; init; } = 1d;

    /// <summary>
    /// Take the render's target duration from the external track instead of from
    /// <c>--duration</c>, so the footage lands exactly on the length of the audio.
    /// </summary>
    public bool MatchDuration { get; init; }

    /// <summary>
    /// True when no audio stream should be written at all.
    /// <para>
    /// <see cref="AudioMode.External"/> without a path counts as muted. That combination is a
    /// contradiction rather than an instruction, and the alternative reading is worse than
    /// useless: every consumer would be told audio is wanted, none would have a stream to
    /// take it from, and the result is a file that is silently silent while declaring itself
    /// otherwise. Collapsing it here means the three questions below cannot disagree whatever
    /// the mode says.
    /// </para>
    /// </summary>
    public bool IsMuted => Mode == AudioMode.Mute
        || (Mode == AudioMode.External && !HasExternalTrack);

    /// <summary>
    /// True when each clip keeps its own audio. False for an external track: its single
    /// continuous stream replaces the per-segment audio rather than mixing with it, so
    /// extracting and encoding the source audio would be wasted work.
    /// </summary>
    public bool UsesSegmentAudio => Mode == AudioMode.Source;

    /// <summary>True when an external track is to be muxed and a path was actually supplied.</summary>
    public bool HasExternalTrack =>
        Mode == AudioMode.External && !string.IsNullOrWhiteSpace(ExternalPath);

    /// <summary>True when the muxed track needs a <c>volume</c> filter.</summary>
    public bool AltersVolume => Math.Abs(Volume - 1d) > 0.001d;

    public static AudioOptions FromSource() => new() { Mode = AudioMode.Source };

    public static AudioOptions FromFile(string path) => new()
    {
        Mode = AudioMode.External,
        ExternalPath = path,
    };
}
