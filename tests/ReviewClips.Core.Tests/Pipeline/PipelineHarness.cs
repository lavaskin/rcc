using Microsoft.Extensions.Logging.Abstractions;
using ReviewClips.Core.Analysis;
using ReviewClips.Core.Media;
using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Primitives;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Tests.Pipeline;

/// <summary>
/// Drives <see cref="RenderPipeline"/> against stub collaborators. Planning is where every policy
/// decision is made and none of them need FFmpeg to be wrong, so real selection strategies and
/// real planners are used throughout; only the collaborators that would shell out are replaced.
/// </summary>
internal sealed class PipelineHarness
{
    private readonly Dictionary<string, MediaInfo> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _audioDurations = new(StringComparer.Ordinal);

    /// <summary>Registers a source of the given length and returns its path.</summary>
    public string AddSource(string name, TimeSpan duration)
    {
        var path = "/movies/" + name;

        _sources[path] = new MediaInfo
        {
            Path = path,
            FileSizeBytes = 1_000_000,
            LastModifiedUtc = DateTimeOffset.UnixEpoch,
            Duration = duration,
            Width = 1920,
            Height = 1080,
            FrameRate = 24d,
            VideoCodec = "h264",
            PixelFormat = "yuv420p",
            SampleAspectRatio = Ratio.One,
            HasAudio = true,
        };

        return path;
    }

    public string AddSource(string name, double seconds) =>
        AddSource(name, TimeSpan.FromSeconds(seconds));

    /// <summary>Registers an external audio track of the given length and returns its path.</summary>
    public string AddAudio(string name, double seconds)
    {
        var path = "/audio/" + name;
        _audioDurations[path] = TimeSpan.FromSeconds(seconds);
        return path;
    }

    public RenderPipeline Build() => new(
        new StubProbe(_sources, _audioDurations),
        new StubAnalyzer(),
        new StubCache(),
        new StubEncoderSelector(),
        new StubExtractor(),
        [new StubStitcher()],
        SegmentSelectorFactory.CreateDefault(),
        NullLogger<RenderPipeline>.Instance);

    public Task<RenderPlan> PlanAsync(ClipRequest request) =>
        Build().PlanAsync(request, observer: null, TestContext.Current.CancellationToken);

    /// <summary>A request over one source, with the settings a test is most likely to vary.</summary>
    public static ClipRequest Request(
        string source,
        double durationSeconds = 60,
        double spliceSeconds = 5,
        int? maxClips = null)
    {
        return new ClipRequest
        {
            Sources = [source],
            OutputPath = "/out/render.mp4",
            TargetDuration = TimeSpan.FromSeconds(durationSeconds),
            SpliceLength = TimeSpan.FromSeconds(spliceSeconds),

            // Removed so a test's arithmetic is exact rather than approximately right.
            SpliceJitter = TimeSpan.Zero,
            MaxDistinctClips = maxClips,
            Selection = new SelectionOptions
            {
                Strategy = SelectionStrategy.Uniform,
                Seed = 1234,
                MinGap = TimeSpan.Zero,
                SkipHead = Offset.Zero,
                SkipTail = Offset.Zero,
                ChapterSkip = ChapterSkipMode.Off,
            },
        };
    }

    private sealed class StubProbe : IMediaProbe
    {
        private readonly Dictionary<string, MediaInfo> _sources;
        private readonly Dictionary<string, TimeSpan> _audio;

        public StubProbe(Dictionary<string, MediaInfo> sources, Dictionary<string, TimeSpan> audio)
        {
            _sources = sources;
            _audio = audio;
        }

        public Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken) =>
            _sources.TryGetValue(path, out var info)
                ? Task.FromResult(info)
                : throw new FileNotFoundException($"Test source not registered: {path}");

        public Task<TimeSpan> ProbeDurationAsync(string path, CancellationToken cancellationToken)
        {
            if (_audio.TryGetValue(path, out var duration))
            {
                return Task.FromResult(duration);
            }

            return _sources.TryGetValue(path, out var info)
                ? Task.FromResult(info.Duration)
                : throw new FileNotFoundException($"Test media not registered: {path}");
        }
    }

    private sealed class StubAnalyzer : IMediaAnalyzer
    {
        public Task<MediaAnalysis> AnalyzeAsync(
            MediaInfo info,
            AnalysisSettings settings,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            Task.FromResult(new MediaAnalysis
            {
                SourcePath = info.Path,
                SourceSizeBytes = info.FileSizeBytes,
                SourceModifiedUtc = info.LastModifiedUtc,
                Duration = info.Duration,
                AnalysedAtUtc = DateTimeOffset.UnixEpoch,
                Settings = settings,
                SceneCuts = [],
                BlackRanges = [],
                FreezeRanges = [],
                Motion = MotionCurve.Empty,
            });
    }

    private sealed class StubCache : IAnalysisCache
    {
        public Task<MediaAnalysis?> TryGetAsync(
            MediaInfo info,
            AnalysisSettings settings,
            CancellationToken cancellationToken) =>
            Task.FromResult<MediaAnalysis?>(null);

        public Task SaveAsync(MediaAnalysis analysis, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubEncoderSelector : IEncoderSelector
    {
        public Task<EncoderProfile> SelectAsync(EncoderOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new EncoderProfile
            {
                VideoEncoder = "libx264",
                IsHardware = false,
                QualityArguments = [],
                ExtraArguments = [],
            });
    }

    private sealed class StubExtractor : ISegmentExtractor
    {
        public Task ExtractAsync(
            SegmentExtractionRequest request,
            IProgress<double>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public IReadOnlyList<string> DescribeArguments(SegmentExtractionRequest request) =>
            ["-i", request.Segment.SourcePath, request.OutputPath];
    }

    private sealed class StubStitcher : IStitcher
    {
        public bool CanHandle(StitchRequest request) => true;

        public Task StitchAsync(
            StitchRequest request,
            IProgress<double>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public IReadOnlyList<string> DescribeArguments(StitchRequest request) => ["-i", request.OutputPath];
    }
}
