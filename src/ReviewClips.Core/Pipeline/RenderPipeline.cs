using Microsoft.Extensions.Logging;
using ReviewClips.Core.Options;
using ReviewClips.Core.Planning;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Pipeline;

/// <summary>
/// Orchestrates a render: probe, analyse, select, extract in parallel, stitch.
/// Owns no FFmpeg knowledge; everything concrete arrives through the abstractions.
/// </summary>
public sealed class RenderPipeline
{
    /// <summary>
    /// Consumer NVIDIA drivers cap simultaneous NVENC sessions. Exceeding the cap fails the
    /// encode outright, so hardware parallelism is clamped regardless of what was requested.
    /// </summary>
    private const int MaxHardwareSessions = 8;

    private readonly IMediaProbe _probe;
    private readonly IMediaAnalyzer _analyzer;
    private readonly IAnalysisCache _cache;
    private readonly IEncoderSelector _encoderSelector;
    private readonly ISegmentExtractor _extractor;
    private readonly IReadOnlyList<IStitcher> _stitchers;
    private readonly ISegmentSelectorFactory _selectorFactory;
    private readonly ILogger<RenderPipeline> _logger;

    public RenderPipeline(
        IMediaProbe probe,
        IMediaAnalyzer analyzer,
        IAnalysisCache cache,
        IEncoderSelector encoderSelector,
        ISegmentExtractor extractor,
        IEnumerable<IStitcher> stitchers,
        ISegmentSelectorFactory selectorFactory,
        ILogger<RenderPipeline> logger)
    {
        _probe = probe;
        _analyzer = analyzer;
        _cache = cache;
        _encoderSelector = encoderSelector;
        _extractor = extractor;
        _stitchers = stitchers.ToList();
        _selectorFactory = selectorFactory;
        _logger = logger;
    }

    /// <summary>Decides everything about the render without encoding anything.</summary>
    public async Task<RenderPlan> PlanAsync(
        ClipRequest request,
        IRenderObserver? observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        observer ??= NullRenderObserver.Instance;

        var warnings = new List<string>();
        var seed = request.Selection.Seed ?? Random.Shared.Next();
        var random = new Random(seed);

        // 1. Probe every source.
        observer.OnPhaseStarted(RenderPhase.Probing, $"{request.Sources.Count} source(s)");
        var infos = new List<Media.MediaInfo>(request.Sources.Count);
        for (var i = 0; i < request.Sources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = await _probe.ProbeAsync(request.Sources[i], cancellationToken);

            if (info.Duration <= TimeSpan.Zero)
            {
                warnings.Add($"Skipping '{Path.GetFileName(info.Path)}': reported zero duration.");
                continue;
            }

            infos.Add(info);
            observer.OnProbed(info.Path, i + 1, request.Sources.Count);
        }

        if (infos.Count == 0)
        {
            throw new RenderPlanningException("None of the supplied sources contained a usable video stream.");
        }

        // An explicit --skip-chapter that matches nothing is almost always a typo or a wrong
        // assumption about the titles, and silently doing nothing is the worst outcome.
        if (request.Selection.SkipChapterPatterns.Count > 0
            && !infos.Any(i => Media.ChapterFilter.Matching(i.Chapters, request.Selection.SkipChapterPatterns).Count > 0))
        {
            warnings.Add(infos.Any(i => i.HasChapters)
                ? "No chapter title matched --skip-chapter. Run 'rcc probe --chapters' to see the titles."
                : "--skip-chapter was given, but none of the sources carry chapter markers.");
        }

        // 2. Plan the cadence up front so both selection and stitching agree on lengths.
        // The target is the runtime of the finished file, so transition overlap is compensated
        // here rather than silently shortening the result.
        var durations = SplicePlanner.PlanForOutput(
            request.TargetDuration,
            request.SpliceLength,
            request.SpliceJitter,
            request.Transition.IsEnabled ? request.Transition.Duration : TimeSpan.Zero,
            seed);

        if (durations.Count == 0)
        {
            throw new RenderPlanningException(
                $"Cannot build a {request.TargetDuration.TotalSeconds:0.#}s render from {request.SpliceLength.TotalSeconds:0.#}s splices.");
        }

        var selector = _selectorFactory.Create(request.Selection.Strategy);

        // 3. Analyse only when the strategy actually needs it.
        var needsAnalysis = selector.RequiresAnalysis || request.Selection.RequiresAnalysis;
        var sources = new List<SourceMedia>(infos.Count);

        if (needsAnalysis)
        {
            observer.OnPhaseStarted(RenderPhase.Analyzing, $"{infos.Count} source(s)");
            foreach (var info in infos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var analysis = await GetOrCreateAnalysisAsync(
                    info, request.Analysis, request.IgnoreAnalysisCache, observer, warnings, cancellationToken);
                sources.Add(new SourceMedia(info, analysis));
            }
        }
        else
        {
            sources.AddRange(infos.Select(i => new SourceMedia(i, null)));
        }

        // 4. Select.
        observer.OnPhaseStarted(RenderPhase.Selecting, selector.Strategy.ToString());
        SelectionContext context = new()
        {
            Sources = sources,
            Options = request.Selection,
            SegmentDurations = durations,
            Random = random,
        };

        // When the distinct-clip pool is capped, select only that many and repeat them to fill
        // the runtime. Selection therefore runs against the smaller pool, which also makes the
        // spacing constraints easier to satisfy.
        var distinctWanted = request.MaxDistinctClips is { } cap && cap < durations.Count
            ? Math.Max(1, cap)
            : durations.Count;

        if (distinctWanted < durations.Count)
        {
            if (request.Selection.Strategy == SelectionStrategy.Cues)
            {
                // Cues already define the pool, so the cap only limits how many of them are
                // used. Each cue gets a full splice-length clip rather than a share of the
                // runtime, because the runtime is filled by repetition instead.
                var used = Math.Min(request.Selection.Cues.Count, distinctWanted);

                context = context with
                {
                    Options = request.Selection with
                    {
                        Cues = request.Selection.Cues.Take(used).ToList(),
                    },
                    SegmentDurations = Enumerable
                        .Repeat(request.SpliceLength, Math.Max(used, 1))
                        .ToList(),
                };
            }
            else
            {
                context = context with { SegmentDurations = durations.Take(distinctWanted).ToList() };
            }
        }

        var segments = selector.SelectSegments(context);

        if (segments.Count == 0)
        {
            throw new RenderPlanningException(BuildNoSegmentsMessage(request, sources));
        }

        // Cue-driven renders legitimately produce one segment per cue, so report against that
        // rather than against the splice-derived count.
        // For cues the expectation is one clip per cue, capped by --max-clips when set.
        var requestedCount = request.Selection.Strategy == SelectionStrategy.Cues
            ? Math.Min(request.Selection.Cues.Count, distinctWanted)
            : distinctWanted;

        observer.OnSegmentsSelected(segments.Count, requestedCount);

        if (segments.Count < requestedCount && request.Selection.Strategy != SelectionStrategy.Cues)
        {
            var achieved = segments.Aggregate(TimeSpan.Zero, (a, s) => a + s.Duration);
            warnings.Add(
                $"Only {segments.Count} of {requestedCount} segments could be placed "
                + $"({achieved.TotalSeconds:0.#}s of {request.TargetDuration.TotalSeconds:0.#}s). "
                + "Try lowering --min-gap, widening --skip-head/--skip-tail, or adding more sources.");
        }

        // 5. Repeat the pool to fill the runtime when the distinct count is capped.
        if (distinctWanted < durations.Count)
        {
            var pool = segments;

            segments = ClipSequencer.FillForOutput(
                pool,
                request.TargetDuration,
                request.Transition.IsEnabled ? request.Transition.Duration : TimeSpan.Zero,
                seed);

            if (segments.Count == 0)
            {
                throw new RenderPlanningException("Could not build a sequence from the selected clips.");
            }

            var factor = (double)segments.Count / pool.Count;
            if (factor >= 4d)
            {
                warnings.Add(
                    $"Each of the {pool.Count} clips appears about {factor:0.#} times to fill "
                    + $"{request.TargetDuration.TotalSeconds:0.#}s. Raise --max-clips or shorten "
                    + "--duration if the repetition becomes noticeable.");
            }
        }

        // 6. Measure how much of the source this consumes.
        // Deliberately after the repeat-fill: the fill is what decouples runtime from footage
        // consumed, so measuring before it would report the pool rather than the render.
        var usage = SourceUsageGuard.Evaluate(
            segments,
            sources.Select(s => s.Info.Duration),
            request.MaxSourceFraction);

        if (SourceUsageGuard.Describe(usage) is { } usageMessage)
        {
            if (request.EnforceMaxSourceFraction)
            {
                throw new SourceUsageLimitException(usageMessage, usage);
            }

            warnings.Add(usageMessage);
        }

        // 7. Resolve the encoder.
        var encoder = await _encoderSelector.SelectAsync(request.Encoder, cancellationToken);
        if (request.Encoder.Preference == EncoderPreference.Auto && !encoder.IsHardware)
        {
            warnings.Add("No hardware encoder available; falling back to software encoding (slower).");
        }

        return new RenderPlan
        {
            Request = request,
            Sources = sources,
            Segments = segments,
            Encoder = encoder,
            Seed = seed,
            Warnings = warnings,
        };
    }

    /// <summary>Executes a plan: extracts every segment, then stitches them together.</summary>
    public async Task<RenderResult> ExecuteAsync(
        RenderPlan plan,
        IRenderObserver? observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        observer ??= NullRenderObserver.Instance;

        var request = plan.Request;
        var started = DateTimeOffset.UtcNow;
        var workDir = CreateWorkingDirectory(request);

        try
        {
            var infoByPath = plan.Sources.ToDictionary(s => s.Path, s => s.Info, StringComparer.Ordinal);

            // Extract.
            var extension = Path.GetExtension(request.OutputPath) is { Length: > 1 } ext ? ext : ".mp4";

            // Identical clips are encoded once and referenced many times. This matters when
            // --max-clips is repeating a small pool: a 210-slot render built from 50 clips
            // performs 50 encodes, not 210, and every repeat is bit-identical.
            var uniqueClips = new Dictionary<ClipKey, string>();
            var extractionQueue = new List<(Segment Segment, string Path)>();

            foreach (var segment in plan.Segments)
            {
                var key = ClipKey.For(segment);
                if (uniqueClips.ContainsKey(key))
                {
                    continue;
                }

                var target = Path.Combine(workDir, $"seg_{uniqueClips.Count:D5}{extension}");
                uniqueClips[key] = target;
                extractionQueue.Add((segment, target));
            }

            observer.OnPhaseStarted(
                RenderPhase.Extracting,
                extractionQueue.Count == plan.Segments.Count
                    ? $"{plan.Segments.Count} clip(s)"
                    : $"{extractionQueue.Count} unique clip(s) filling {plan.Segments.Count} slot(s)");

            var completed = 0;

            var parallelism = Math.Max(1, request.Parallelism);
            if (plan.Encoder.IsHardware)
            {
                parallelism = Math.Min(parallelism, MaxHardwareSessions);
            }

            await Parallel.ForEachAsync(
                extractionQueue,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellationToken,
                },
                async (item, ct) =>
                {
                    await _extractor.ExtractAsync(
                        new SegmentExtractionRequest
                        {
                            Segment = item.Segment,
                            Source = infoByPath[item.Segment.SourcePath],
                            OutputPath = item.Path,
                            Format = request.Format,
                            Look = request.Look,
                            Encoder = plan.Encoder,
                            EncoderOptions = request.Encoder,
                            Mute = request.Mute,
                        },
                        progress: null,
                        ct);

                    observer.OnSegmentCompleted(
                        Interlocked.Increment(ref completed),
                        extractionQueue.Count);
                });

            // Expand back to one path per slot, repeating shared clips.
            var segmentPaths = plan.Segments.Select(s => uniqueClips[ClipKey.For(s)]).ToArray();

            // Stitch.
            observer.OnPhaseStarted(RenderPhase.Stitching, request.Transition.Kind.ToString());

            var stitchRequest = new StitchRequest
            {
                SegmentPaths = segmentPaths,
                SegmentDurations = plan.Segments.Select(s => s.Duration).ToList(),
                OutputPath = request.OutputPath,
                Transition = request.Transition,
                Format = request.Format,
                Encoder = plan.Encoder,
                EncoderOptions = request.Encoder,
                Mute = request.Mute,
                WorkingDirectory = workDir,
            };

            var stitcher = ResolveStitcher(stitchRequest);
            EnsureOutputDirectory(request.OutputPath);
            await stitcher.StitchAsync(stitchRequest, new InlineProgress<double>(observer.OnStitchProgress), cancellationToken);

            observer.OnPhaseStarted(RenderPhase.Finalising, Path.GetFileName(request.OutputPath));

            var size = File.Exists(request.OutputPath) ? new FileInfo(request.OutputPath).Length : 0L;

            return new RenderResult
            {
                Plan = plan,
                OutputPath = request.OutputPath,
                Elapsed = DateTimeOffset.UtcNow - started,
                OutputSizeBytes = size,
            };
        }
        finally
        {
            if (request.KeepTemporaryFiles)
            {
                _logger.LogInformation("Intermediate files kept at {Directory}", workDir);
            }
            else
            {
                TryDeleteDirectory(workDir);
            }
        }
    }

    /// <summary>Returns the stitcher and the extractor commands that a run would execute.</summary>
    public IReadOnlyList<string> DescribeCommands(RenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var request = plan.Request;
        var infoByPath = plan.Sources.ToDictionary(s => s.Path, s => s.Info, StringComparer.Ordinal);
        var workDir = Path.Combine(Path.GetTempPath(), "reviewclips", "dry-run");
        var extension = Path.GetExtension(request.OutputPath) is { Length: > 1 } ext ? ext : ".mp4";

        var lines = new List<string>();
        var segmentPaths = new List<string>();

        // Mirror the executed path: identical clips share one encode.
        var uniqueClips = new Dictionary<ClipKey, string>();

        foreach (var segment in plan.Segments)
        {
            var key = ClipKey.For(segment);

            if (!uniqueClips.TryGetValue(key, out var target))
            {
                target = Path.Combine(workDir, $"seg_{uniqueClips.Count:D5}{extension}");
                uniqueClips[key] = target;

                var args = _extractor.DescribeArguments(new SegmentExtractionRequest
                {
                    Segment = segment,
                    Source = infoByPath[segment.SourcePath],
                    OutputPath = target,
                    Format = request.Format,
                    Look = request.Look,
                    Encoder = plan.Encoder,
                    EncoderOptions = request.Encoder,
                    Mute = request.Mute,
                });

                lines.Add("ffmpeg " + Quote(args));
            }

            segmentPaths.Add(target);
        }

        var stitchRequest = new StitchRequest
        {
            SegmentPaths = segmentPaths,
            SegmentDurations = plan.Segments.Select(s => s.Duration).ToList(),
            OutputPath = request.OutputPath,
            Transition = request.Transition,
            Format = request.Format,
            Encoder = plan.Encoder,
            EncoderOptions = request.Encoder,
            Mute = request.Mute,
            WorkingDirectory = workDir,
        };

        lines.Add("ffmpeg " + Quote(ResolveStitcher(stitchRequest).DescribeArguments(stitchRequest)));
        return lines;
    }

    private async Task<Analysis.MediaAnalysis?> GetOrCreateAnalysisAsync(
        Media.MediaInfo info,
        Analysis.AnalysisSettings settings,
        bool ignoreCache,
        IRenderObserver observer,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var cached = ignoreCache
                ? null
                : await _cache.TryGetAsync(info, settings, cancellationToken);
            if (cached is not null)
            {
                observer.OnAnalysisStarted(info.Path, fromCache: true);
                return cached;
            }

            observer.OnAnalysisStarted(info.Path, fromCache: false);

            var analysis = await _analyzer.AnalyzeAsync(
                info,
                settings,
                new InlineProgress<double>(f => observer.OnAnalysisProgress(info.Path, f)),
                cancellationToken);

            await _cache.SaveAsync(analysis, cancellationToken);
            return analysis;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Analysis is an optimisation, not a requirement. Degrade to unanalysed rather
            // than failing the whole render.
            _logger.LogWarning(ex, "Analysis failed for {Path}", info.Path);
            var message = $"Analysis failed for '{Path.GetFileName(info.Path)}' ({ex.Message}). "
                + "Falling back to unanalysed selection for this file.";
            warnings.Add(message);
            observer.OnWarning(message);
            return null;
        }
    }

    private IStitcher ResolveStitcher(StitchRequest request) =>
        _stitchers.FirstOrDefault(s => s.CanHandle(request))
        ?? throw new RenderPlanningException("No stitcher can handle this request.");

    private static string BuildNoSegmentsMessage(ClipRequest request, IReadOnlyList<SourceMedia> sources)
    {
        var reasons = new List<string>();

        if (request.Selection.Strategy == SelectionStrategy.Cues && request.Selection.Cues.Count == 0)
        {
            reasons.Add("strategy is 'cues' but no cues were supplied (use --cues)");
        }

        var shortest = sources.Min(s => s.Info.Duration);
        if (shortest < request.SpliceLength)
        {
            reasons.Add($"the shortest source ({shortest.TotalSeconds:0.#}s) is shorter than the splice length");
        }

        if (request.Selection.RejectBlack || request.Selection.RejectFrozen)
        {
            reasons.Add("black/frozen rejection may have excluded everything (try --no-reject-black / --no-reject-frozen)");
        }

        var skippedChapters = sources
            .Sum(s => Media.ChapterFilter.Matching(s.Info.Chapters, request.Selection.EffectiveChapterPatterns).Count);
        if (skippedChapters > 0)
        {
            reasons.Add($"{skippedChapters} chapter(s) were skipped by title (try --chapters off)");
        }

        var detail = reasons.Count > 0
            ? " Likely cause: " + string.Join("; ", reasons) + "."
            : " Try relaxing --min-gap, --skip-head and --skip-tail.";

        return "No usable segments were found." + detail;
    }

    private static string CreateWorkingDirectory(ClipRequest request)
    {
        var root = request.WorkingDirectory
            ?? Path.Combine(Path.GetTempPath(), "reviewclips");

        var dir = Path.Combine(root, $"run_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void EnsureOutputDirectory(string outputPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove temporary directory {Directory}", directory);
        }
    }

    private static string Quote(IReadOnlyList<string> args) =>
        string.Join(' ', args.Select(a =>
            a.Length == 0 || a.Any(char.IsWhiteSpace) || a.Contains('\'', StringComparison.Ordinal)
                ? "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
                : a));
}

/// <summary>
/// Identity of a clip for de-duplication: the same source and timing yields the same encode.
/// </summary>
internal readonly record struct ClipKey(string SourcePath, long StartTicks, long DurationTicks)
{
    public static ClipKey For(Selection.Segment segment) =>
        new(segment.SourcePath, segment.Start.Ticks, segment.Duration.Ticks);
}

public sealed class RenderPlanningException : Exception
{
    public RenderPlanningException(string message) : base(message)
    {
    }

    public RenderPlanningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RenderPlanningException()
    {
    }
}

/// <summary>
/// Thrown when a render would consume more of the source than the configured limit allows and
/// <see cref="Options.ClipRequest.EnforceMaxSourceFraction"/> is set.
/// <para>
/// Deliberately not a <see cref="RenderPlanningException"/>. That one means "nothing usable
/// could be produced", which is a different situation with a different exit code: here the
/// planning worked perfectly and a policy declined the result.
/// </para>
/// </summary>
public sealed class SourceUsageLimitException : Exception
{
    public SourceUsageLimitException(string message, Planning.SourceUsageReport report)
        : base(message) => Report = report;

    public SourceUsageLimitException(string message) : base(message)
    {
    }

    public SourceUsageLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SourceUsageLimitException()
    {
    }

    public Planning.SourceUsageReport? Report { get; }
}
