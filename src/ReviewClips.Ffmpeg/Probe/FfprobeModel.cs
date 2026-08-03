using System.Text.Json.Serialization;

namespace ReviewClips.Ffmpeg.Probe;

/// <summary>Shape of <c>ffprobe -print_format json</c> output. Only the fields we need.</summary>
internal sealed class FfprobeOutput
{
    [JsonPropertyName("streams")]
    public List<FfprobeStream> Streams { get; init; } = [];

    [JsonPropertyName("format")]
    public FfprobeFormat? Format { get; init; }
}

internal sealed class FfprobeStream
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("codec_name")]
    public string? CodecName { get; init; }

    [JsonPropertyName("codec_type")]
    public string? CodecType { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("pix_fmt")]
    public string? PixelFormat { get; init; }

    /// <summary>Nominal frame rate, e.g. <c>24000/1001</c>. Can be <c>0/0</c> for some containers.</summary>
    [JsonPropertyName("r_frame_rate")]
    public string? RFrameRate { get; init; }

    /// <summary>Actual average frame rate. Preferred, as it is correct for variable-frame-rate sources.</summary>
    [JsonPropertyName("avg_frame_rate")]
    public string? AvgFrameRate { get; init; }

    [JsonPropertyName("sample_aspect_ratio")]
    public string? SampleAspectRatio { get; init; }

    [JsonPropertyName("display_aspect_ratio")]
    public string? DisplayAspectRatio { get; init; }

    [JsonPropertyName("color_transfer")]
    public string? ColorTransfer { get; init; }

    [JsonPropertyName("color_primaries")]
    public string? ColorPrimaries { get; init; }

    [JsonPropertyName("color_space")]
    public string? ColorSpace { get; init; }

    [JsonPropertyName("duration")]
    public string? Duration { get; init; }

    [JsonPropertyName("nb_frames")]
    public string? FrameCount { get; init; }

    [JsonPropertyName("disposition")]
    public Dictionary<string, int>? Disposition { get; init; }

    public bool IsVideo => string.Equals(CodecType, "video", StringComparison.OrdinalIgnoreCase);

    public bool IsAudio => string.Equals(CodecType, "audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for cover art and thumbnails, which ffprobe reports as video streams. Treating one
    /// as the main video stream would yield a still image and a nonsense duration.
    /// </summary>
    public bool IsAttachedPicture =>
        Disposition is not null
        && Disposition.TryGetValue("attached_pic", out var flag)
        && flag == 1;
}

internal sealed class FfprobeFormat
{
    [JsonPropertyName("duration")]
    public string? Duration { get; init; }

    [JsonPropertyName("format_name")]
    public string? FormatName { get; init; }

    [JsonPropertyName("bit_rate")]
    public string? BitRate { get; init; }
}
