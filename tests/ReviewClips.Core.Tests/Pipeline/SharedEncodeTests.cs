using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Tests.Pipeline;

/// <summary>
/// One encode per distinct clip. The sequencer lands on the runtime by changing the final slot's
/// length, and the encodes have to count clips the way the rest of the program does, by where
/// they start, or <c>--max-clips N</c> encodes N + 1.
/// </summary>
public class SharedEncodeTests
{
    // At three 5s clips, 62s ends on a 2.2s slot trimmed from a full one, and 300s ends on a
    // 5.6s slot, the previous entry lengthened because the remainder was too short to cut.
    private const double TrimmedFinalSlot = 62;
    private const double LengthenedFinalSlot = 300;

    [Theory]
    [InlineData(TrimmedFinalSlot)]
    [InlineData(LengthenedFinalSlot)]
    public async Task MaxClipsEncodesExactlyThatManyClips(double durationSeconds)
    {
        var (harness, request) = Render(durationSeconds, maxClips: 3);
        var plan = await harness.PlanAsync(request);

        // Guards the premise: the final slot really was resized, which is the case at issue.
        plan.Segments.Select(s => (s.Start, s.Duration)).Distinct().Count().ShouldBe(4);

        var observer = new RecordingObserver();
        await harness.Build().ExecuteAsync(plan, observer, TestContext.Current.CancellationToken);

        harness.Extracted.Count.ShouldBe(3);
        plan.DistinctClipCount.ShouldBe(3);
        observer.ExtractionTotals.ShouldAllBe(total => total == 3);
        observer.ExtractingDetail.ShouldBe($"3 unique clip(s) filling {plan.Segments.Count} slot(s)");
    }

    [Fact]
    public async Task ATrimmedFinalSlotReusesItsFullLengthEncode()
    {
        var (harness, request) = Render(TrimmedFinalSlot, maxClips: 3);
        var plan = await harness.PlanAsync(request);
        await harness.Build().ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        var last = plan.Segments[^1];
        var encode = harness.Extracted.Single(e => e.Segment.Start == last.Start);

        encode.Segment.Duration.ShouldBe(TimeSpan.FromSeconds(5));
        encode.Trimmed.ShouldBeTrue();
        harness.Extracted.Where(e => e != encode).ShouldAllBe(e => !e.Trimmed);

        var stitch = harness.Stitched.ShouldNotBeNull();
        stitch.SegmentDurations[^1].ShouldBe(last.Duration);
        stitch.EncodedDurations.ShouldNotBeNull()[^1].ShouldBe(TimeSpan.FromSeconds(5));
        stitch.SegmentPaths[^1].ShouldBe(encode.OutputPath);

        Enumerable.Range(0, plan.Segments.Count)
            .Where(stitch.IsTrimmed)
            .ShouldBe([plan.Segments.Count - 1]);
    }

    [Fact]
    public async Task ALengthenedFinalSlotIsEncodedLongAndItsOtherUsesTrimmed()
    {
        var (harness, request) = Render(LengthenedFinalSlot, maxClips: 3);
        var plan = await harness.PlanAsync(request);
        await harness.Build().ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        var last = plan.Segments[^1];
        last.Duration.ShouldBeGreaterThan(TimeSpan.FromSeconds(5));

        var encode = harness.Extracted.Single(e => e.Segment.Start == last.Start);
        encode.Segment.Duration.ShouldBe(last.Duration);
        encode.Trimmed.ShouldBeTrue();

        // Every earlier appearance of that clip plays the opening of the longer encode.
        var stitch = harness.Stitched.ShouldNotBeNull();
        var earlier = Enumerable.Range(0, plan.Segments.Count - 1)
            .Where(i => plan.Segments[i].Start == last.Start)
            .ToList();

        earlier.ShouldNotBeEmpty();
        earlier.ShouldAllBe(i => stitch.IsTrimmed(i) && stitch.SegmentPaths[i] == encode.OutputPath);
        stitch.IsTrimmed(plan.Segments.Count - 1).ShouldBeFalse();
    }

    [Fact]
    public async Task TheSlotDurationsAreStillThePlannedOnes()
    {
        var (harness, request) = Render(TrimmedFinalSlot, maxClips: 3);
        var plan = await harness.PlanAsync(request);
        await harness.Build().ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        // Fades, xfade offsets and the duration-miss check all read these.
        harness.Stitched.ShouldNotBeNull().SegmentDurations.ShouldBe(plan.Segments.Select(s => s.Duration));
    }

    [Theory]
    [InlineData(TrimmedFinalSlot)]
    [InlineData(LengthenedFinalSlot)]
    public async Task DryRunDescribesTheEncodesTheRunPerforms(double durationSeconds)
    {
        var (harness, request) = Render(durationSeconds, maxClips: 3);
        var plan = await harness.PlanAsync(request);
        var pipeline = harness.Build();

        var described = pipeline.DescribeCommands(plan).SkipLast(1).ToList();

        await pipeline.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        var executed = harness.Extracted
            .OrderBy(e => Path.GetFileName(e.OutputPath), StringComparer.Ordinal)
            .Select(e => "ffmpeg " + string.Join(' ', PipelineHarness.DescribeExtraction(e)))
            .ToList();

        described.ShouldBe(executed);
    }

    [Fact]
    public async Task UncappedRendersEncodeEverySlot()
    {
        var (harness, request) = Render(TrimmedFinalSlot, maxClips: null);
        var plan = await harness.PlanAsync(request);

        var observer = new RecordingObserver();
        await harness.Build().ExecuteAsync(plan, observer, TestContext.Current.CancellationToken);

        harness.Extracted.Count.ShouldBe(plan.Segments.Count);
        harness.Extracted.ShouldAllBe(e => !e.Trimmed);
        observer.ExtractingDetail.ShouldBe($"{plan.Segments.Count} clip(s)");
    }

    [Fact]
    public void ALookThatVariesWithLengthEncodesEachLengthSeparately()
    {
        Segment[] slots = [Clip(100, 5), Clip(200, 5), Clip(100, 2)];

        // A fade-out at the clip's end is not in the opening of a longer clip, so the trimmed
        // slot needs its own encode to keep its fade.
        var plan = ExtractionPlan.Create(
            slots,
            LookOptions.None with { FadeEdges = TimeSpan.FromSeconds(0.5) },
            "/work",
            ".mp4");

        plan.Jobs.Count.ShouldBe(3);
        plan.Jobs.ShouldAllBe(j => !j.Trimmed);
        plan.EncodedDurations.ShouldBe(slots.Select(s => s.Duration));
    }

    [Fact]
    public async Task ALookThatVariesWithLengthStillReportsTheClipCount()
    {
        var (harness, request) = Render(TrimmedFinalSlot, maxClips: 3);
        request = request with { Look = LookOptions.None with { ZoomEnd = 1.08d } };
        var plan = await harness.PlanAsync(request);

        var observer = new RecordingObserver();
        await harness.Build().ExecuteAsync(plan, observer, TestContext.Current.CancellationToken);

        harness.Extracted.Count.ShouldBe(4);
        observer.ExtractingDetail.ShouldBe($"3 unique clip(s) in 4 encode(s) filling {plan.Segments.Count} slot(s)");
    }

    [Fact]
    public async Task OneEncodePerSlotStillReportsTheClipCountWhenClipsRepeat()
    {
        var (harness, request) = Render(18, maxClips: 3);
        request = request with { Look = LookOptions.None with { ZoomEnd = 1.08d } };
        var plan = await harness.PlanAsync(request);

        // Guards the premise: a repeat whose length differs, so every slot is its own encode.
        plan.Segments.Count.ShouldBe(4);
        plan.DistinctClipCount.ShouldBe(3);

        var observer = new RecordingObserver();
        await harness.Build().ExecuteAsync(plan, observer, TestContext.Current.CancellationToken);

        harness.Extracted.Count.ShouldBe(4);
        observer.ExtractingDetail.ShouldBe("3 unique clip(s) in 4 encode(s) filling 4 slot(s)");
    }

    [Fact]
    public void SharedEncodesAreNamedInOrderOfFirstUse()
    {
        var plan = ExtractionPlan.Create(
            [Clip(300, 5), Clip(100, 5), Clip(300, 3)],
            LookOptions.None,
            "/work",
            ".mkv");

        plan.SlotPaths.ShouldBe(["/work/seg_00000.mkv", "/work/seg_00001.mkv", "/work/seg_00000.mkv"]);
        plan.Jobs.Select(j => j.Trimmed).ShouldBe([true, false]);
    }

    private static Segment Clip(double start, double duration) => new()
    {
        SourcePath = "/movies/film.mkv",
        Start = TimeSpan.FromSeconds(start),
        Duration = TimeSpan.FromSeconds(duration),
    };

    private static (PipelineHarness Harness, ClipRequest Request) Render(double durationSeconds, int? maxClips)
    {
        var harness = new PipelineHarness();
        var source = harness.AddSource("film.mkv", 600);

        // ExecuteAsync creates both directories, though the stubs write nothing into them.
        var scratch = Path.Combine(Path.GetTempPath(), "rcc_shared_encode_tests");

        var request = PipelineHarness.Request(source, durationSeconds, maxClips: maxClips) with
        {
            OutputPath = Path.Combine(scratch, "render.mp4"),
            WorkingDirectory = scratch,
        };

        return (harness, request);
    }

    private sealed class RecordingObserver : IRenderObserver
    {
        public string? ExtractingDetail { get; private set; }

        public List<int> ExtractionTotals { get; } = [];

        public void OnPhaseStarted(RenderPhase phase, string detail)
        {
            if (phase == RenderPhase.Extracting)
            {
                ExtractingDetail = detail;
            }
        }

        public void OnExtractionProgress(int completed, int total, double fraction)
        {
            lock (ExtractionTotals)
            {
                ExtractionTotals.Add(total);
            }
        }

        public void OnProbed(string path, int completed, int total)
        {
        }

        public void OnAnalysisStarted(string path, bool fromCache)
        {
        }

        public void OnAnalysisProgress(string path, double fraction)
        {
        }

        public void OnSegmentsSelected(int count, int requested)
        {
        }

        public void OnStitchProgress(double fraction)
        {
        }

        public void OnWarning(string message)
        {
        }
    }
}
