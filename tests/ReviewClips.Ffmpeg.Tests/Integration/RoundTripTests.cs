using Microsoft.Extensions.Logging.Abstractions;
using ReviewClips.Core.Media;
using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Primitives;
using ReviewClips.Core.Selection;
using ReviewClips.Ffmpeg.Encoding;
using ReviewClips.Ffmpeg.Extraction;
using ReviewClips.Ffmpeg.Filters;
using ReviewClips.Ffmpeg.Probe;
using ReviewClips.Ffmpeg.Stitching;

namespace ReviewClips.Ffmpeg.Tests.Integration;

/// <summary>
/// Extracts real segments and stitches them, then verifies the resulting runtime.
/// These cover the arithmetic that string assertions cannot: whether the transition offsets
/// actually produce the intended output length.
/// </summary>
[Collection(FfmpegTestGroup.Name)]
public class RoundTripTests
{
    private readonly FfmpegFixture _fixture;

    public RoundTripTests(FfmpegFixture fixture) => _fixture = fixture;

    private async Task<(MediaInfo Info, EncoderProfile Encoder)> SetUpAsync()
    {
        var probe = new FfprobeMediaProbe(_fixture.Runner, NullLogger<FfprobeMediaProbe>.Instance);
        var info = await probe.ProbeAsync(_fixture.SimpleClip, TestContext.Current.CancellationToken);

        var selector = new FfmpegEncoderSelector(_fixture.EncoderProbe, NullLogger<FfmpegEncoderSelector>.Instance);

        // Force software encoding so the test result does not depend on the GPU present.
        var encoder = await selector.SelectAsync(
            new EncoderOptions { Preference = EncoderPreference.X264, Preset = "ultrafast" },
            TestContext.Current.CancellationToken);

        return (info, encoder);
    }

    private async Task<List<string>> ExtractAsync(
        MediaInfo info,
        EncoderProfile encoder,
        IReadOnlyList<(double Start, double Duration)> segments,
        OutputFormat? format = null,
        LookOptions? look = null)
    {
        var extractor = new FfmpegSegmentExtractor(
            _fixture.Runner,
            VideoFilterGraphBuilder.CreateDefault(),
            NullLogger<FfmpegSegmentExtractor>.Instance);

        var paths = new List<string>();

        for (var i = 0; i < segments.Count; i++)
        {
            var (start, duration) = segments[i];
            var output = _fixture.PathFor($"rt_{Guid.NewGuid():N}_{i}.mp4");

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
                    Format = format ?? (OutputFormat.Youtube with { Width = 320, Height = 180 }),
                    Look = look ?? LookOptions.None,
                    Encoder = encoder,
                    EncoderOptions = new EncoderOptions { Preference = EncoderPreference.X264 },
                    Mute = true,
                },
                progress: null,
                TestContext.Current.CancellationToken);

            paths.Add(output);
        }

        return paths;
    }

    [Fact]
    public async Task ExtractedSegmentHasTheRequestedDuration()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (info, encoder) = await SetUpAsync();
        var paths = await ExtractAsync(info, encoder, [(3.0, 2.0)]);

        // Input-side seeking with re-encoding is frame accurate, so this should be exact.
        (await _fixture.DurationOfAsync(paths[0])).ShouldBe(2.0, 0.1);
    }

    [Fact]
    public async Task ExtractedSegmentsAreNormalisedToIdenticalGeometry()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (info, encoder) = await SetUpAsync();
        var paths = await ExtractAsync(info, encoder, [(1.0, 1.0), (5.0, 1.5)]);

        // Uniformity is what lets the stitcher use a stream copy.
        var first = await _fixture.DimensionsOfAsync(paths[0]);
        var second = await _fixture.DimensionsOfAsync(paths[1]);

        first.ShouldBe(second);
        first.ShouldBe((320, 180));
    }

    [Fact]
    public async Task ConcatDemuxerStitch_ProducesTheSumOfSegmentDurations()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (info, encoder) = await SetUpAsync();
        var paths = await ExtractAsync(info, encoder, [(1.0, 2.0), (4.0, 2.0), (7.0, 2.0)]);

        var output = _fixture.PathFor($"concat_{Guid.NewGuid():N}.mp4");
        var stitcher = new ConcatDemuxerStitcher(_fixture.Runner, NullLogger<ConcatDemuxerStitcher>.Instance);

        var request = Request(paths, output, [2, 2, 2], TransitionKind.None, encoder, fade: 0);

        stitcher.CanHandle(request).ShouldBeTrue();
        await stitcher.StitchAsync(request, null, TestContext.Current.CancellationToken);

        // Hard cuts: no overlap, so the runtime is the plain sum.
        (await _fixture.DurationOfAsync(output)).ShouldBe(6.0, 0.25);
    }

    [Fact]
    public async Task XfadeStitch_ShortensTheOutputByOneTransitionPerJoin()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (info, encoder) = await SetUpAsync();
        var paths = await ExtractAsync(info, encoder, [(1.0, 2.0), (4.0, 2.0), (7.0, 2.0)]);

        var output = _fixture.PathFor($"xfade_{Guid.NewGuid():N}.mp4");
        var stitcher = new FilterGraphStitcher(_fixture.Runner, NullLogger<FilterGraphStitcher>.Instance);

        var request = Request(paths, output, [2, 2, 2], TransitionKind.DipToBlack, encoder, fade: 0);

        await stitcher.StitchAsync(request, null, TestContext.Current.CancellationToken);

        // 6s of material less two 0.4s transitions.
        (await _fixture.DurationOfAsync(output)).ShouldBe(5.2, 0.25);
    }

    [Fact]
    public async Task XfadeStitch_AppliesTopLevelFades()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (info, encoder) = await SetUpAsync();
        var paths = await ExtractAsync(info, encoder, [(1.0, 2.0), (4.0, 2.0)]);

        var output = _fixture.PathFor($"fades_{Guid.NewGuid():N}.mp4");
        var stitcher = new FilterGraphStitcher(_fixture.Runner, NullLogger<FilterGraphStitcher>.Instance);

        var request = Request(paths, output, [2, 2], TransitionKind.Fade, encoder, fade: 0.5);

        await stitcher.StitchAsync(request, null, TestContext.Current.CancellationToken);

        // 4s less one 0.4s transition; the fades do not change the length.
        (await _fixture.DurationOfAsync(output)).ShouldBe(3.6, 0.25);
    }

    [Fact]
    public async Task SingleSegmentStitch_Succeeds()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (info, encoder) = await SetUpAsync();
        var paths = await ExtractAsync(info, encoder, [(2.0, 2.0)]);

        var output = _fixture.PathFor($"single_{Guid.NewGuid():N}.mp4");
        var stitcher = new FilterGraphStitcher(_fixture.Runner, NullLogger<FilterGraphStitcher>.Instance);

        // A one-clip render has no joins; the graph must degenerate gracefully.
        var request = Request(paths, output, [2], TransitionKind.DipToBlack, encoder, fade: 0.3);

        await stitcher.StitchAsync(request, null, TestContext.Current.CancellationToken);

        (await _fixture.DurationOfAsync(output)).ShouldBe(2.0, 0.25);
    }

    [Fact]
    public async Task VerticalRender_RoundTripsThroughTheBlurPadBranch()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (info, encoder) = await SetUpAsync();
        var format = OutputFormat.Shorts with { Width = 180, Height = 320 };

        var paths = await ExtractAsync(info, encoder, [(1.0, 1.5), (4.0, 1.5)], format);

        var output = _fixture.PathFor($"vertical_{Guid.NewGuid():N}.mp4");
        var stitcher = new FilterGraphStitcher(_fixture.Runner, NullLogger<FilterGraphStitcher>.Instance);

        await stitcher.StitchAsync(
            Request(paths, output, [1.5, 1.5], TransitionKind.Fade, encoder, fade: 0, format: format),
            null,
            TestContext.Current.CancellationToken);

        (await _fixture.DimensionsOfAsync(output)).ShouldBe((180, 320));
    }

    [Fact]
    public async Task SpeedChange_StillProducesTheRequestedOutputDuration()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (info, encoder) = await SetUpAsync();

        // Half speed: the extractor must read 1s of source to yield a 2s clip.
        var paths = await ExtractAsync(
            info,
            encoder,
            [(3.0, 2.0)],
            look: new LookOptions { Darken = 0, Saturation = 1, Speed = 0.5 });

        (await _fixture.DurationOfAsync(paths[0])).ShouldBe(2.0, 0.2);
    }

    [Fact]
    public async Task ChapteredSource_LeavesNoChaptersInTheSegmentsOrTheOutput()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var probe = new FfprobeMediaProbe(_fixture.Runner, NullLogger<FfprobeMediaProbe>.Instance);
        var info = await probe.ProbeAsync(_fixture.ChapteredClip, TestContext.Current.CancellationToken);
        info.HasChapters.ShouldBeTrue("the fixture must actually carry chapters to be a regression test");

        var (_, encoder) = await SetUpAsync();
        var paths = await ExtractAsync(info, encoder, [(2.5, 1.5), (6.0, 1.5)]);

        // FFmpeg copies chapters from the input by default and merely rebases them onto the cut,
        // so an unfixed extractor writes the source's whole chapter list into a 1.5s segment.
        foreach (var path in paths)
        {
            (await _fixture.ChapterCountOfAsync(path)).ShouldBe(0);
        }

        // Both stitchers must hold the guarantee: the filter graph copies chapters from its
        // first input, and the concat demuxer is only clean by happenstance.
        var graphOutput = _fixture.PathFor($"chap_graph_{Guid.NewGuid():N}.mp4");
        await new FilterGraphStitcher(_fixture.Runner, NullLogger<FilterGraphStitcher>.Instance)
            .StitchAsync(
                Request(paths, graphOutput, [1.5, 1.5], TransitionKind.Fade, encoder, fade: 0),
                null,
                TestContext.Current.CancellationToken);

        var concatOutput = _fixture.PathFor($"chap_concat_{Guid.NewGuid():N}.mp4");
        await new ConcatDemuxerStitcher(_fixture.Runner, NullLogger<ConcatDemuxerStitcher>.Instance)
            .StitchAsync(
                Request(paths, concatOutput, [1.5, 1.5], TransitionKind.None, encoder, fade: 0),
                null,
                TestContext.Current.CancellationToken);

        (await _fixture.ChapterCountOfAsync(graphOutput)).ShouldBe(0);
        (await _fixture.ChapterCountOfAsync(concatOutput)).ShouldBe(0);
    }

    private static StitchRequest Request(
        IReadOnlyList<string> paths,
        string output,
        double[] durations,
        TransitionKind kind,
        EncoderProfile encoder,
        double fade,
        OutputFormat? format = null) => new()
        {
            SegmentPaths = paths,
            SegmentDurations = durations.Select(TimeSpan.FromSeconds).ToList(),
            OutputPath = output,
            Transition = new TransitionOptions
            {
                Kind = kind,
                Duration = TimeSpan.FromSeconds(0.4),
                FadeIn = TimeSpan.FromSeconds(fade),
                FadeOut = TimeSpan.FromSeconds(fade),
            },
            Format = format ?? (OutputFormat.Youtube with { Width = 320, Height = 180 }),
            Encoder = encoder,
            EncoderOptions = new EncoderOptions { Preference = EncoderPreference.X264 },
            Mute = true,
            WorkingDirectory = Path.GetDirectoryName(output)!,
        };
}

[Collection(FfmpegTestGroup.Name)]
public class EncoderSelectorTests
{
    private readonly FfmpegFixture _fixture;

    public EncoderSelectorTests(FfmpegFixture fixture) => _fixture = fixture;

    private FfmpegEncoderSelector Selector() =>
        new(_fixture.EncoderProbe, NullLogger<FfmpegEncoderSelector>.Instance);

    [Fact]
    public async Task Auto_AlwaysResolvesToSomethingUsable()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var profile = await Selector().SelectAsync(
            new EncoderOptions { Preference = EncoderPreference.Auto },
            TestContext.Current.CancellationToken);

        profile.VideoEncoder.ShouldNotBeNullOrWhiteSpace();
        profile.QualityArguments.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Software_UsesCrfRateControl()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var profile = await Selector().SelectAsync(
            new EncoderOptions { Preference = EncoderPreference.X264, Quality = 18 },
            TestContext.Current.CancellationToken);

        profile.VideoEncoder.ShouldBe("libx264");
        profile.IsHardware.ShouldBeFalse();
        profile.QualityArguments.ShouldContain("-crf");
        profile.QualityArguments.ShouldContain("18");
    }

    [Fact]
    public async Task Hardware_UsesConstantQualityWithNoBitrateCap()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        EncoderProfile profile;
        try
        {
            profile = await Selector().SelectAsync(
                new EncoderOptions { Preference = EncoderPreference.Nvenc, Quality = 22 },
                TestContext.Current.CancellationToken);
        }
        catch (ReviewClips.Ffmpeg.Process.FfmpegExecutionException)
        {
            Assert.Skip("No usable NVENC encoder on this machine.");
            return;
        }

        profile.IsHardware.ShouldBeTrue();
        profile.QualityArguments.ShouldContain("-cq");

        // Without '-b:v 0' NVENC ignores the CQ target and uses a default bitrate instead.
        profile.QualityArguments.ShouldContain("-b:v");
        profile.QualityArguments.ShouldContain("0");
    }
}

public class AtempoChainTests
{
    [Theory]
    [InlineData(1.0, "anull")]
    [InlineData(1.5, "atempo=1.5")]
    [InlineData(0.5, "atempo=0.5")]
    public void SimpleRatesUseASingleStage(double speed, string expected) =>
        FfmpegSegmentExtractor.BuildAtempoChain(speed).ShouldBe(expected);

    [Fact]
    public void RatesAboveTwoAreDecomposed()
    {
        // A single atempo instance only accepts 0.5 to 2.0.
        var chain = FfmpegSegmentExtractor.BuildAtempoChain(4.0);

        chain.Split(',').ShouldAllBe(s => s.StartsWith("atempo=", StringComparison.Ordinal));
        chain.ShouldContain("atempo=2.0");
    }

    [Fact]
    public void RatesBelowHalfAreDecomposed()
    {
        var chain = FfmpegSegmentExtractor.BuildAtempoChain(0.25);

        chain.ShouldContain("atempo=0.5");
        chain.Split(',').Length.ShouldBeGreaterThan(1);
    }
}
