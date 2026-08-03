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

    [JsonPropertyName("encoder")]
    public string Encoder { get; init; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; init; } = string.Empty;

    [JsonPropertyName("muted")]
    public bool Muted { get; init; }

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

        var usage = plan.Segments
            .GroupBy(s => s.SourcePath, StringComparer.Ordinal)
            .Select(g => new SourceUsage
            {
                Source = g.Key,
                Clips = g.Count(),
                TotalSeconds = Math.Round(g.Sum(s => s.Duration.TotalSeconds), 3),
            })
            .OrderByDescending(u => u.TotalSeconds)
            .ToList();

        return new RenderManifest
        {
            Output = request.OutputPath,
            Seed = plan.Seed,
            Strategy = request.Selection.Strategy.ToString(),
            DistinctClips = plan.DistinctClipCount,
            Slots = plan.Segments.Count,
            DistinctSourceSeconds = Math.Round(plan.DistinctSourceDuration.TotalSeconds, 3),
            TargetSeconds = Math.Round(request.TargetDuration.TotalSeconds, 3),
            RenderedSeconds = Math.Round(plan.EffectiveDuration.TotalSeconds, 3),
            Encoder = plan.Encoder.VideoEncoder,
            Format = $"{request.Format.Width}x{request.Format.Height}@{request.Format.FrameRate:0.##}",
            Muted = request.Mute,
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

    internal sealed record SourceUsage
    {
        [JsonPropertyName("source")]
        public string Source { get; init; } = string.Empty;

        [JsonPropertyName("clips")]
        public int Clips { get; init; }

        [JsonPropertyName("totalSeconds")]
        public double TotalSeconds { get; init; }
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
