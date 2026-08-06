using System.Text.Json;
using System.Text.Json.Serialization;
using ReviewClips.Core.Pipeline;

namespace ReviewClips.Cli.Presentation;

/// <summary>
/// A record of exactly what a render used.
/// <para>
/// Two purposes. It makes a render reproducible, since the seed plus the settings regenerate
/// the identical output. And it is an audit trail: if a clip is ever challenged, you have a
/// precise record of which source, which timestamps, and how much of each title was used.
/// </para>
/// </summary>
internal sealed record RenderManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("tool")]
    public string Tool { get; init; } = "reviewclips";

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("output")]
    public string Output { get; init; } = string.Empty;

    [JsonPropertyName("seed")]
    public int Seed { get; init; }

    [JsonPropertyName("strategy")]
    public string Strategy { get; init; } = string.Empty;

    [JsonPropertyName("targetSeconds")]
    public double TargetSeconds { get; init; }

    [JsonPropertyName("renderedSeconds")]
    public double RenderedSeconds { get; init; }

    /// <summary>Clips actually cut from the source, ignoring repeats.</summary>
    [JsonPropertyName("distinctClips")]
    public int DistinctClips { get; init; }

    /// <summary>Slots in the finished render; exceeds distinctClips when clips repeat.</summary>
    [JsonPropertyName("slots")]
    public int Slots { get; init; }

    /// <summary>Seconds of source footage consumed, counting each distinct clip once.</summary>
    [JsonPropertyName("distinctSourceSeconds")]
    public double DistinctSourceSeconds { get; init; }

    /// <summary>Combined runtime of every source that fed the render.</summary>
    [JsonPropertyName("availableSourceSeconds")]
    public double AvailableSourceSeconds { get; init; }

    /// <summary>
    /// <c>distinctSourceSeconds / availableSourceSeconds</c>. The share of the supplied footage
    /// this render consumes, with repeats and overlaps counted once.
    /// </summary>
    [JsonPropertyName("sourceUsageFraction")]
    public double SourceUsageFraction { get; init; }

    /// <summary>
    /// The largest share taken from any single source, which is what the guardrail tests. This
    /// is the figure that matters for an audit: the aggregate above is diluted by every source
    /// the render barely touched.
    /// </summary>
    [JsonPropertyName("peakSourceUsageFraction")]
    public double PeakSourceUsageFraction { get; init; }

    [JsonPropertyName("encoder")]
    public string Encoder { get; init; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; init; } = string.Empty;

    [JsonPropertyName("muted")]
    public bool Muted { get; init; }

    /// <summary>
    /// What the render sounds like, when it makes a sound. Null for a muted render, which is the
    /// default and which <c>muted</c> already describes in full.
    /// <para>
    /// <c>muted: false</c> on its own does not distinguish "kept each clip's own audio" from
    /// "muxed a file", and says nothing about which file or at what offset and gain. Both are
    /// needed for the manifest's two purposes: reproducing the render, and explaining a
    /// <c>targetSeconds</c> that came from the audio rather than from <c>--duration</c>.
    /// </para>
    /// </summary>
    [JsonPropertyName("audio")]
    public ManifestAudio? Audio { get; init; }

    [JsonPropertyName("elapsedSeconds")]
    public double? ElapsedSeconds { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Per-source totals: how much of each title ended up in the render.</summary>
    [JsonPropertyName("sourceUsage")]
    public IReadOnlyList<SourceUsage> Usage { get; init; } = [];

    [JsonPropertyName("segments")]
    public IReadOnlyList<ManifestSegment> Segments { get; init; } = [];

    public static RenderManifest From(RenderPlan plan, RenderResult? result)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var request = plan.Request;

        var clipsBySource = plan.Segments
            .GroupBy(s => s.SourcePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (Clips: g.Count(), Seconds: g.Sum(s => s.Duration.TotalSeconds)), StringComparer.Ordinal);

        // Driven from the usage report rather than from the segments, so the per-source rows and
        // the guardrail are the same measurement. A source that contributed nothing still gets a
        // row: "we supplied this and took none of it" is part of the record.
        var usage = plan.SourceUsage.Sources
            .Select(entry =>
            {
                var counted = clipsBySource.GetValueOrDefault(entry.Path);

                return new SourceUsage
                {
                    Source = entry.Path,
                    Clips = counted.Clips,
                    TotalSeconds = Math.Round(counted.Seconds, 3),
                    DistinctSeconds = Math.Round(entry.Used.TotalSeconds, 3),
                    AvailableSeconds = Math.Round(entry.Available.TotalSeconds, 3),
                    Fraction = Math.Round(entry.Fraction, 5),
                };
            })
            .OrderByDescending(u => u.Fraction)
            .ThenByDescending(u => u.TotalSeconds)
            .ToList();

        return new RenderManifest
        {
            Output = request.OutputPath,
            Seed = plan.Seed,
            Strategy = request.Selection.Strategy.ToString(),
            DistinctClips = plan.DistinctClipCount,
            Slots = plan.Segments.Count,
            DistinctSourceSeconds = Math.Round(plan.DistinctSourceDuration.TotalSeconds, 3),
            AvailableSourceSeconds = Math.Round(plan.SourceUsage.Available.TotalSeconds, 3),
            SourceUsageFraction = Math.Round(plan.SourceUsage.Fraction, 5),
            PeakSourceUsageFraction = Math.Round(plan.SourceUsage.PeakFraction, 5),
            TargetSeconds = Math.Round(request.TargetDuration.TotalSeconds, 3),
            RenderedSeconds = Math.Round(plan.EffectiveDuration.TotalSeconds, 3),
            Encoder = plan.Encoder.VideoEncoder,
            Format = $"{request.Format.Width}x{request.Format.Height}@{request.Format.FrameRate:0.##}",
            Muted = request.Mute,
            Audio = ManifestAudio.For(request.Audio),
            ElapsedSeconds = result is null ? null : Math.Round(result.Elapsed.TotalSeconds, 2),
            Warnings = plan.Warnings,
            Usage = usage,
            Segments = plan.Segments.Select((s, i) => new ManifestSegment
            {
                Index = i,
                Source = s.SourcePath,
                StartSeconds = Math.Round(s.Start.TotalSeconds, 3),
                DurationSeconds = Math.Round(s.Duration.TotalSeconds, 3),
                Score = Math.Round(s.Score, 4),
                Reason = s.Reason,
            }).ToList(),
        };
    }

    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, JsonOptions, cancellationToken);
    }

    /// <summary>The audio settings a render actually used, as far as they apply.</summary>
    internal sealed record ManifestAudio
    {
        /// <summary><c>source</c> or <c>external</c>. Muted renders carry no audio block at all.</summary>
        [JsonPropertyName("mode")]
        public string Mode { get; init; } = string.Empty;

        /// <summary>The muxed track. Absent in <c>source</c> mode, where there is no one file.</summary>
        [JsonPropertyName("track")]
        public string? Track { get; init; }

        [JsonPropertyName("offsetSeconds")]
        public double? OffsetSeconds { get; init; }

        /// <summary>Linear gain. Absent when the track was left untouched.</summary>
        [JsonPropertyName("volume")]
        public double? Volume { get; init; }

        /// <summary>
        /// True when the render's length came from the track rather than from <c>--duration</c>.
        /// This is what accounts for a <c>targetSeconds</c> that appears in no command line.
        /// </summary>
        [JsonPropertyName("matchedDuration")]
        public bool? MatchedDuration { get; init; }

        /// <summary>
        /// Null for a muted render. Both this and <c>muted</c> read
        /// <see cref="Core.Options.AudioOptions.IsMuted"/>, so they cannot disagree — including on
        /// the <c>External</c>-without-a-path case, which that property counts as muted.
        /// </summary>
        public static ManifestAudio? For(Core.Options.AudioOptions audio)
        {
            if (audio.IsMuted)
            {
                return null;
            }

            return new ManifestAudio
            {
                Mode = audio.UsesSegmentAudio ? "source" : "external",
                Track = audio.ExternalPath,

                // Only recorded when they mean something: offset and volume have no bearing on
                // per-segment audio, and a default gain is noise in an audit trail.
                OffsetSeconds = audio.HasExternalTrack && audio.Offset > TimeSpan.Zero
                    ? Math.Round(audio.Offset.TotalSeconds, 3)
                    : null,
                Volume = audio.HasExternalTrack && audio.AltersVolume
                    ? Math.Round(audio.Volume, 4)
                    : null,
                MatchedDuration = audio.HasExternalTrack ? audio.MatchDuration : null,
            };
        }
    }

    internal sealed record SourceUsage
    {
        [JsonPropertyName("source")]
        public string Source { get; init; } = string.Empty;

        [JsonPropertyName("clips")]
        public int Clips { get; init; }

        /// <summary>Screen time drawn from this source, counting every repeat.</summary>
        [JsonPropertyName("totalSeconds")]
        public double TotalSeconds { get; init; }

        /// <summary>Union of the ranges referenced in this source, counting each moment once.</summary>
        [JsonPropertyName("distinctSeconds")]
        public double DistinctSeconds { get; init; }

        /// <summary>This source's full runtime.</summary>
        [JsonPropertyName("availableSeconds")]
        public double AvailableSeconds { get; init; }

        /// <summary><c>distinctSeconds / availableSeconds</c>: the share taken from this title.</summary>
        [JsonPropertyName("fraction")]
        public double Fraction { get; init; }
    }

    internal sealed record ManifestSegment
    {
        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("source")]
        public string Source { get; init; } = string.Empty;

        [JsonPropertyName("startSeconds")]
        public double StartSeconds { get; init; }

        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; init; }

        [JsonPropertyName("score")]
        public double Score { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }
}
