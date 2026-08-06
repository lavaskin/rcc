namespace ReviewClips.Core.Options;

public enum AudioMode
{
    /// <summary>Default.</summary>
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
    /// <see cref="AudioMode.External"/> without a path counts as muted: consumers would
    /// otherwise be told audio is wanted with no stream to take it from. Collapsing it here
    /// keeps the three questions below in agreement whatever the mode says.
    /// </para>
    /// </summary>
    public bool IsMuted => Mode == AudioMode.Mute
        || (Mode == AudioMode.External && !HasExternalTrack);

    /// <summary>
    /// True when each clip keeps its own audio. False for an external track: it replaces
    /// per-segment audio rather than mixing with it, so extracting that audio is wasted work.
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
