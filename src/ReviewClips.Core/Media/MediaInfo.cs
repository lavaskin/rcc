using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Media;

/// <summary>Everything the pipeline needs to know about an input file.</summary>
public sealed record MediaInfo
{
    public required string Path { get; init; }

    public required long FileSizeBytes { get; init; }

    public required DateTimeOffset LastModifiedUtc { get; init; }

    public required TimeSpan Duration { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Average frame rate, used to normalise output timing.</summary>
    public required double FrameRate { get; init; }

    public required string VideoCodec { get; init; }

    public required string PixelFormat { get; init; }

    /// <summary>Storage aspect ratio. Anamorphic DVD sources are not 1:1 and must be corrected.</summary>
    public required Ratio SampleAspectRatio { get; init; }

    public required bool HasAudio { get; init; }

    /// <summary>
    /// Container chapter markers in file order; empty, never <c>null</c>, when the source
    /// carries none.
    /// </summary>
    public IReadOnlyList<Chapter> Chapters { get; init; } = [];

    public string? ColorTransfer { get; init; }

    public string? ColorPrimaries { get; init; }

    public string? ColorSpace { get; init; }

    /// <summary>
    /// True for PQ (HDR10) or HLG transfer functions. Such sources must be tone-mapped
    /// or the output renders washed-out and grey.
    /// </summary>
    public bool IsHdr =>
        ColorTransfer is "smpte2084" or "arib-std-b67";

    /// <summary>True when the stream is 10-bit or deeper.</summary>
    public bool IsHighBitDepth =>
        PixelFormat.Contains("p10", StringComparison.Ordinal)
        || PixelFormat.Contains("p12", StringComparison.Ordinal)
        || PixelFormat.Contains("p016", StringComparison.Ordinal);

    /// <summary>Display aspect ratio, i.e. pixel dimensions corrected by the sample aspect ratio.</summary>
    public double DisplayAspectRatio =>
        Height == 0 ? 0d : (double)Width / Height * (SampleAspectRatio.IsValid ? SampleAspectRatio.Value : 1d);

    public bool IsAnamorphic =>
        SampleAspectRatio.IsValid && SampleAspectRatio != Ratio.One;

    public bool HasChapters => Chapters.Count > 0;

    /// <summary>
    /// True when at least one chapter is named something other than its own number. Numbered
    /// titles cannot be matched against, so <see cref="HasChapters"/> alone says nothing about
    /// whether the chapters are usable.
    /// </summary>
    public bool HasNamedChapters => Chapters.Any(c => c.MeaningfulTitle is not null);

    public TimeRange FullRange => new(TimeSpan.Zero, Duration);
}
