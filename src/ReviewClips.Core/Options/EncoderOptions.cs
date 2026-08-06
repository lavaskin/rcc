namespace ReviewClips.Core.Options;

public enum EncoderPreference
{
    /// <summary>Probe for a working hardware encoder, fall back to software.</summary>
    Auto,

    /// <summary>Force NVIDIA NVENC. Fails loudly if unavailable.</summary>
    Nvenc,

    /// <summary>Force libx264.</summary>
    X264,

    /// <summary>Force libx265.</summary>
    X265,
}

public enum VideoCodecKind
{
    H264,
    Hevc,
}

public sealed record EncoderOptions
{
    public EncoderPreference Preference { get; init; } = EncoderPreference.Auto;

    public VideoCodecKind Codec { get; init; } = VideoCodecKind.H264;

    /// <summary>
    /// Perceptual quality target, 0 (best) to 51 (worst); maps to CRF for software encoders and
    /// CQ for NVENC.
    /// </summary>
    public int Quality { get; init; } = 20;

    /// <summary>Encoder speed preset. Interpreted per-encoder.</summary>
    public string? Preset { get; init; }

    /// <summary>
    /// Hardware decoding for extraction. Off by default: init overhead can exceed the gain on
    /// short reads.
    /// </summary>
    public bool HardwareDecode { get; init; }

    public int AudioBitrateKbps { get; init; } = 160;
}

/// <summary>A concrete, resolved encoder chosen after probing the local FFmpeg build.</summary>
public sealed record EncoderProfile
{
    public required string VideoEncoder { get; init; }

    public required bool IsHardware { get; init; }

    /// <summary>Quality control arguments, e.g. <c>-crf 20</c> or <c>-cq 20 -rc vbr</c>.</summary>
    public required IReadOnlyList<string> QualityArguments { get; init; }

    public required IReadOnlyList<string> ExtraArguments { get; init; }

    public string PixelFormat { get; init; } = "yuv420p";
}
