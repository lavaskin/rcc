using Microsoft.Extensions.Logging.Abstractions;
using ReviewClips.Core.Media;
using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Selection;
using ReviewClips.Ffmpeg.Encoding;
using ReviewClips.Ffmpeg.Extraction;
using ReviewClips.Ffmpeg.Filters;
using ReviewClips.Ffmpeg.Probe;
using ReviewClips.Ffmpeg.Process;
using ReviewClips.Ffmpeg.Stitching;

namespace ReviewClips.Ffmpeg.Tests.Integration;

/// <summary>
/// A segment must contain exactly the frames it was planned to contain.
/// <para>
/// <c>-ss</c>/<c>-t</c> bound which source timestamps are read; the <c>fps</c> filter then
/// resamples that window onto the output rate. Both round outwards, so a segment reliably comes
/// out one to two frames long. Individually that is invisible. The concat stitcher sums actual
/// segment lengths, so the same bias in the same direction across a hundred and twenty clips
/// compounds into seconds — and the filter-graph stitcher schedules its transitions from the
/// <em>planned</em> lengths, so segments that disagree with the plan push the joins out of step.
/// </para>
/// </summary>
[Collection(FfmpegTestGroup.Name)]
public class SegmentLengthTests
{
    private const double Fps = 30d;

    private readonly FfmpegFixture _fixture;

    public SegmentLengthTests(FfmpegFixture fixture) => _fixture = fixture;

    private static OutputFormat SmallFormat => OutputFormat.Youtube with
    {
        Width = 320,
        Height = 180,
        FrameRate = Fps,
    };

    private async Task<(MediaInfo Info, EncoderProfile Encoder)> SetUpAsync()
    {
        var probe = new FfprobeMediaProbe(_fixture.Runner, NullLogger<FfprobeMediaProbe>.Instance);
        var info = await probe.ProbeAsync(_fixture.SimpleClip, TestContext.Current.CancellationToken);

        var selector = new FfmpegEncoderSelector(
            _fixture.EncoderProbe,
            NullLogger<FfmpegEncoderSelector>.Instance);

        var encoder = await selector.SelectAsync(
            new EncoderOptions { Preference = EncoderPreference.X264, Preset = "ultrafast" },
            TestContext.Current.CancellationToken);

        return (info, encoder);
    }

    private async Task<string> ExtractAsync(
        MediaInfo info,
        EncoderProfile encoder,
        double start,
        double duration,
        bool mute = true)
    {
        var extractor = new FfmpegSegmentExtractor(
            _fixture.Runner,
            VideoFilterGraphBuilder.CreateDefault(),
            NullLogger<FfmpegSegmentExtractor>.Instance);

        var output = _fixture.PathFor($"len_{Guid.NewGuid():N}.mp4");

        await extractor.ExtractAsync(
            new SegmentExtractionRequest
            {
                Segment = new Segment
                {
                    SourcePath = info.Path,
                    Start = TimeSpan.FromSeconds(start),
                    Duration = TimeSpan.FromSeconds(duration),
                },
                Source = info,
                OutputPath = output,
                Format = SmallFormat,
                Look = LookOptions.None,
                Encoder = encoder,
                EncoderOptions = new EncoderOptions { Preference = EncoderPreference.X264 },
                Mute = mute,
            },
            progress: null,
            TestContext.Current.CancellationToken);

        return output;
    }

    /// <summary>
    /// Awkward lengths on purpose: whole seconds would land on a frame boundary by luck and hide
    /// the rounding entirely.
    /// </summary>
    [Theory]
    [InlineData(1.0, 2.0)]
    [InlineData(2.0, 4.28234)]
    [InlineData(1.5, 5.33621)]
    [InlineData(3.0, 2.07312)]
    [InlineData(0.5, 0.8)]
    public async Task AnExtractedSegmentHasExactlyThePlannedFrameCount(double start, double duration)
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (info, encoder) = await SetUpAsync();
        var path = await ExtractAsync(info, encoder, start, duration);

        var expected = FfmpegSegmentExtractor.FrameCount(TimeSpan.FromSeconds(duration), Fps);

        (await _fixture.FrameCountOfAsync(path)).ShouldBe(expected);
    }

    /// <summary>
    /// The compounding case, at a scale small enough to run in CI. Eight awkward clips are enough
    /// for the old per-clip bias to show: it produced eight to twelve frames of overshoot here,
    /// which is the same proportional error that reached five seconds on a ten-minute render.
    /// </summary>
    [Fact]
    public async Task ConcatenatedSegmentsSumToThePlannedRuntime()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (info, encoder) = await SetUpAsync();

        double[] durations = [0.93, 1.17, 0.81, 1.34, 1.06, 0.77, 1.21, 0.89];
        var paths = new List<string>();

        for (var i = 0; i < durations.Length; i++)
        {
            paths.Add(await ExtractAsync(info, encoder, 0.5 + i, durations[i]));
        }

        var output = _fixture.PathFor($"sum_{Guid.NewGuid():N}.mp4");

        await new ConcatDemuxerStitcher(_fixture.Runner, NullLogger<ConcatDemuxerStitcher>.Instance)
            .StitchAsync(
                new StitchRequest
                {
                    SegmentPaths = paths,
                    SegmentDurations = [.. durations.Select(TimeSpan.FromSeconds)],
                    OutputPath = output,
                    Transition = TransitionOptions.None with { FadeIn = TimeSpan.Zero, FadeOut = TimeSpan.Zero },
                    Format = SmallFormat,
                    Encoder = encoder,
                    EncoderOptions = new EncoderOptions { Preference = EncoderPreference.X264 },
                    Audio = AudioOptions.Muted,
                    WorkingDirectory = _fixture.Directory,
                },
                null,
                TestContext.Current.CancellationToken);

        var expected = durations
            .Sum(d => FfmpegSegmentExtractor.FrameCount(TimeSpan.FromSeconds(d), Fps));

        (await _fixture.FrameCountOfAsync(output)).ShouldBe(expected);
    }

    [Fact]
    public async Task ASpeedChangedSegmentIsStillCutToTheOutputLength()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (info, encoder) = await SetUpAsync();

        var extractor = new FfmpegSegmentExtractor(
            _fixture.Runner,
            VideoFilterGraphBuilder.CreateDefault(),
            NullLogger<FfmpegSegmentExtractor>.Instance);

        var output = _fixture.PathFor($"speed_{Guid.NewGuid():N}.mp4");

        // The frame cap is on the output, so it must be derived from the rendered length rather
        // than from the longer or shorter stretch of source that was read to produce it.
        await extractor.ExtractAsync(
            new SegmentExtractionRequest
            {
                Segment = new Segment
                {
                    SourcePath = info.Path,
                    Start = TimeSpan.FromSeconds(3),
                    Duration = TimeSpan.FromSeconds(2),
                },
                Source = info,
                OutputPath = output,
                Format = SmallFormat,
                Look = new LookOptions { Darken = 0, Saturation = 1, Speed = 0.5 },
                Encoder = encoder,
                EncoderOptions = new EncoderOptions { Preference = EncoderPreference.X264 },
                Mute = true,
            },
            progress: null,
            TestContext.Current.CancellationToken);

        (await _fixture.FrameCountOfAsync(output)).ShouldBe(60);
    }

    [Fact]
    public async Task KeepingTheSourceAudioDoesNotChangeTheFrameCount()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var probe = new FfprobeMediaProbe(_fixture.Runner, NullLogger<FfprobeMediaProbe>.Instance);
        var info = await probe.ProbeAsync(_fixture.MultiShotClip, TestContext.Current.CancellationToken);
        var (_, encoder) = await SetUpAsync();

        var path = await ExtractAsync(info, encoder, 5.0, 1.73, mute: false);

        (await _fixture.FrameCountOfAsync(path))
            .ShouldBe(FfmpegSegmentExtractor.FrameCount(TimeSpan.FromSeconds(1.73), Fps));
    }

    [Theory]
    [InlineData(2.0, 30, 60)]
    [InlineData(1.73, 30, 52)]
    [InlineData(0.0, 30, 0)]
    [InlineData(2.0, 0, 0)]
    [InlineData(0.001, 30, 1)]
    public void FrameCountRoundsToTheNearestWholeFrame(double seconds, double fps, int expected) =>
        FfmpegSegmentExtractor.FrameCount(TimeSpan.FromSeconds(seconds), fps).ShouldBe(expected);

    /// <summary>
    /// FFmpeg exits 0 after writing a stream-less file when its graph yields nothing, so a
    /// successful exit code is not on its own evidence that anything was encoded. Without this
    /// check the empty file travels downstream and fails somewhere that cannot explain itself.
    /// </summary>
    [Fact]
    public async Task ARunThatEncodesNothingIsTreatedAsAFailure()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var output = _fixture.PathFor($"empty_{Guid.NewGuid():N}.mp4");

        // Trim a window that lies past the end of a one-second source. The source length is set
        // on the lavfi input rather than with -t so the trim really does run out of frames
        // instead of merely being truncated afterwards. Valid graph, zero frames, exit code 0.
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=s=64x64:r=30:d=1",
            "-vf", "trim=start=99:end=100,setpts=PTS-STARTPTS",
            "-c:v", "libx264", "-preset", "ultrafast",
            output,
        ];

        // Establish the premise: FFmpeg itself is perfectly happy.
        var raw = await _fixture.RunAsync(arguments);
        raw.Success.ShouldBeTrue("the point of this test is a zero exit code with no output");
        raw.FramesWritten.ShouldBeNull("plain runs are not progress-parsed");

        var checkedRun = await Should.ThrowAsync<FfmpegExecutionException>(async () =>
            await _fixture.Runner.RunFfmpegCheckedAsync(
                arguments,
                TimeSpan.FromSeconds(1),
                progress: null,
                TestContext.Current.CancellationToken,
                requireFrames: true));

        checkedRun.Message.ShouldContain("produced no frames");
    }

    [Fact]
    public async Task ARunThatEncodesSomethingIsNotTreatedAsAFailure()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var output = _fixture.PathFor($"nonempty_{Guid.NewGuid():N}.mp4");

        await _fixture.Runner.RunFfmpegCheckedAsync(
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=64x64:r=30",
                "-t", "1",
                "-c:v", "libx264", "-preset", "ultrafast",
                output,
            ],
            TimeSpan.FromSeconds(1),
            progress: null,
            TestContext.Current.CancellationToken,
            requireFrames: true);

        (await _fixture.FrameCountOfAsync(output)).ShouldBe(30);
    }
}
