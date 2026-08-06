using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ReviewClips.Core.Analysis;
using ReviewClips.Core.Media;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Ffmpeg.Analysis;

/// <summary>
/// Persists analysis to JSON under the user's cache directory.
/// <para>
/// This is what makes the tool pleasant to iterate with. Scanning a feature film is the
/// slowest part of a render by a wide margin, and you will usually generate many different
/// background tracks from the same title. Every run after the first reuses this.
/// </para>
/// <para>
/// The cache key covers the file path, size, modification time and every analysis setting, so
/// re-encoding a source or changing a threshold invalidates it automatically.
/// </para>
/// </summary>
public sealed class JsonAnalysisCache : IAnalysisCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly string _root;
    private readonly ILogger<JsonAnalysisCache> _logger;

    public JsonAnalysisCache(string? cacheDirectory, ILogger<JsonAnalysisCache> logger)
    {
        _root = cacheDirectory ?? DefaultCacheDirectory();
        _logger = logger;
    }

    public static string DefaultCacheDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // LocalApplicationData is already per-user on both platforms and is the right home for a
        // cache that should survive a reboot. It can come back empty in a container with no
        // HOME set, and the fallback then has to supply the per-user scoping itself rather than
        // dropping the cache into a directory shared with every other account on the machine.
        return string.IsNullOrEmpty(baseDir)
            ? Path.Combine(Core.Primitives.ScratchPaths.Root, "analysis")
            : Path.Combine(baseDir, "reviewclips", "analysis");
    }

    public string CacheDirectory => _root;

    public async Task<MediaAnalysis?> TryGetAsync(
        MediaInfo info,
        AnalysisSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        var path = ResolvePath(info, settings);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var record = await JsonSerializer.DeserializeAsync<CachedAnalysis>(
                stream, JsonOptions, cancellationToken);

            if (record is null || record.SchemaVersion != MediaAnalysis.SchemaVersion)
            {
                return null;
            }

            var analysis = record.ToAnalysis(settings);

            // Guard against a hash collision or a file swapped in place.
            return analysis.MatchesSource(info) ? analysis : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogDebug(ex, "Discarding unreadable cache entry {Path}", path);
            return null;
        }
    }

    public async Task SaveAsync(MediaAnalysis analysis, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var info = new MediaInfo
        {
            Path = analysis.SourcePath,
            FileSizeBytes = analysis.SourceSizeBytes,
            LastModifiedUtc = analysis.SourceModifiedUtc,
            Duration = analysis.Duration,
            Width = 0,
            Height = 0,
            FrameRate = 0,
            VideoCodec = string.Empty,
            PixelFormat = string.Empty,
            SampleAspectRatio = Ratio.One,
            HasAudio = false,
        };

        var path = ResolvePath(info, analysis.Settings);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write to a temporary file then move, so a cancelled run cannot leave a
            // half-written cache entry that would later fail to parse.
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(
                    stream, CachedAnalysis.From(analysis), JsonOptions, cancellationToken);
            }

            File.Move(temp, path, overwrite: true);
            _logger.LogDebug("Cached analysis at {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache write failure must never fail a render.
            _logger.LogWarning(ex, "Could not write analysis cache for {Source}", analysis.SourcePath);
        }
    }

    private string ResolvePath(MediaInfo info, AnalysisSettings settings)
    {
        var key = string.Join(
            '|',
            info.Path,
            info.FileSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            info.LastModifiedUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            settings.CacheDiscriminator);

        var hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..24];
        var name = SanitiseName(Path.GetFileNameWithoutExtension(info.Path));

        return Path.Combine(_root, $"{name}.{hash}.json");
    }

    private static string SanitiseName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 || c == '.' ? '_' : c);
        }

        var result = builder.ToString();
        return result.Length > 48 ? result[..48] : result;
    }

    /// <summary>
    /// On-disk shape. Kept separate from the domain model so the cache format can evolve
    /// independently, and so <see cref="MotionCurve"/> can be stored as flat arrays.
    /// </summary>
    private sealed record CachedAnalysis
    {
        [JsonPropertyName("schema")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("source")]
        public string SourcePath { get; init; } = string.Empty;

        [JsonPropertyName("size")]
        public long SourceSizeBytes { get; init; }

        [JsonPropertyName("mtime")]
        public long SourceModifiedUnix { get; init; }

        [JsonPropertyName("duration")]
        public double DurationSeconds { get; init; }

        [JsonPropertyName("analysedAt")]
        public long AnalysedAtUnix { get; init; }

        [JsonPropertyName("cuts")]
        public double[] SceneCuts { get; init; } = [];

        /// <summary>Flattened start/end pairs, halving the JSON overhead versus objects.</summary>
        [JsonPropertyName("black")]
        public double[] BlackRanges { get; init; } = [];

        [JsonPropertyName("freeze")]
        public double[] FreezeRanges { get; init; } = [];

        [JsonPropertyName("motionT")]
        public double[] MotionTimes { get; init; } = [];

        [JsonPropertyName("motionV")]
        public double[] MotionValues { get; init; } = [];

        public static CachedAnalysis From(MediaAnalysis analysis)
        {
            var samples = analysis.Motion.Samples;

            return new CachedAnalysis
            {
                SchemaVersion = MediaAnalysis.SchemaVersion,
                SourcePath = analysis.SourcePath,
                SourceSizeBytes = analysis.SourceSizeBytes,
                SourceModifiedUnix = analysis.SourceModifiedUtc.ToUnixTimeSeconds(),
                DurationSeconds = analysis.Duration.TotalSeconds,
                AnalysedAtUnix = analysis.AnalysedAtUtc.ToUnixTimeSeconds(),
                SceneCuts = [.. analysis.SceneCuts.Select(c => Round(c.TotalSeconds))],
                BlackRanges = Flatten(analysis.BlackRanges),
                FreezeRanges = Flatten(analysis.FreezeRanges),
                MotionTimes = [.. samples.Select(s => Round(s.AtSeconds))],
                MotionValues = [.. samples.Select(s => Math.Round(s.Mafd, 4))],
            };
        }

        public MediaAnalysis ToAnalysis(AnalysisSettings settings) => new()
        {
            SourcePath = SourcePath,
            SourceSizeBytes = SourceSizeBytes,
            SourceModifiedUtc = DateTimeOffset.FromUnixTimeSeconds(SourceModifiedUnix),
            Duration = TimeSpan.FromSeconds(DurationSeconds),
            SceneCuts = SceneCuts.Select(TimeSpan.FromSeconds).ToList(),
            BlackRanges = Unflatten(BlackRanges),
            FreezeRanges = Unflatten(FreezeRanges),
            Motion = new MotionCurve(
                MotionTimes.Zip(MotionValues, (t, v) => new MotionSample(t, v))),
            Settings = settings,
            AnalysedAtUtc = DateTimeOffset.FromUnixTimeSeconds(AnalysedAtUnix),
        };

        private static double Round(double value) => Math.Round(value, 3);

        private static double[] Flatten(IReadOnlyList<TimeRange> ranges)
        {
            var result = new double[ranges.Count * 2];
            for (var i = 0; i < ranges.Count; i++)
            {
                result[i * 2] = Round(ranges[i].Start.TotalSeconds);
                result[(i * 2) + 1] = Round(ranges[i].End.TotalSeconds);
            }

            return result;
        }

        private static List<TimeRange> Unflatten(double[] flat)
        {
            var result = new List<TimeRange>(flat.Length / 2);
            for (var i = 0; i + 1 < flat.Length; i += 2)
            {
                if (flat[i + 1] > flat[i])
                {
                    result.Add(new TimeRange(
                        TimeSpan.FromSeconds(flat[i]),
                        TimeSpan.FromSeconds(flat[i + 1])));
                }
            }

            return result;
        }
    }
}
