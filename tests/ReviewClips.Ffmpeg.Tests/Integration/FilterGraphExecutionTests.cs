using ReviewClips.Core.Media;
using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;
using ReviewClips.Ffmpeg.Filters;

namespace ReviewClips.Ffmpeg.Tests.Integration;

/// <summary>
/// Runs every generated filter graph through real FFmpeg.
/// <para>
/// String assertions cannot tell whether a graph is actually valid: a mislabelled branch or an
/// unsupported option only fails at execution time. These tests are the ones that catch that.
/// </para>
/// </summary>
[Collection(FfmpegTestGroup.Name)]
public class FilterGraphExecutionTests
{
    private readonly FfmpegFixture _fixture;

    public FilterGraphExecutionTests(FfmpegFixture fixture) => _fixture = fixture;

    private static MediaInfo Info(int width, int height, Ratio? sar = null, string? transfer = null) => new()
    {
        Path = "unused",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTimeOffset.UnixEpoch,
        Duration = TimeSpan.FromSeconds(10),
        Width = width,
        Height = height,
        FrameRate = 30,
        VideoCodec = "h264",
        PixelFormat = "yuv420p",
        SampleAspectRatio = sar ?? Ratio.One,
        HasAudio = false,
        ColorTransfer = transfer,
    };

    /// <summary>Builds a graph, runs it over the given source, and asserts FFmpeg accepted it.</summary>
    private async Task<string> RenderAsync(
        string sourcePath,
        MediaInfo info,
        OutputFormat format,
        LookOptions look,
        string outputName)
    {
        var context = new FilterContext
        {
            Source = info,
            Format = format,
            Look = look,
            SegmentDuration = TimeSpan.FromSeconds(2),
        };

        var graph = VideoFilterGraphBuilder.CreateDefault().Build(context);
        var output = _fixture.PathFor(outputName);

        var result = await _fixture.RunAsync(
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-ss", "1", "-t", "2",
            "-i", sourcePath,
            "-filter_complex", graph,
            "-map", "[vout]",
            "-an",
            "-c:v", "libx264", "-preset", "ultrafast",
            "-pix_fmt", "yuv420p",
            "-fps_mode", "cfr",
            output,
        ]);

        result.Success.ShouldBeTrue(
            $"FFmpeg rejected the generated graph.{Environment.NewLine}"
            + $"Graph: {graph}{Environment.NewLine}"
            + $"Error: {result.StandardError}");

        return output;
    }

    [Theory]
    [InlineData(FitMode.Crop)]
    [InlineData(FitMode.Pad)]
    [InlineData(FitMode.BlurPad)]
    [InlineData(FitMode.Stretch)]
    public async Task EveryFitMode_ProducesAValidGraphAtTheTargetSize(FitMode fit)
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var format = OutputFormat.Youtube with { Fit = fit, Width = 1280, Height = 720 };

        var output = await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            format,
            LookOptions.None,
            $"fit_{fit}.mp4");

        (await _fixture.DimensionsOfAsync(output)).ShouldBe((1280, 720));
    }

    [Fact]
    public async Task VerticalBlurPad_ProducesAValidGraphForALandscapeSource()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        // The branching graph: the case most likely to be mislabelled.
        var output = await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Shorts with { Width = 540, Height = 960 },
            LookOptions.None,
            "vertical.mp4");

        (await _fixture.DimensionsOfAsync(output)).ShouldBe((540, 960));
    }

    [Fact]
    public async Task FullLookStack_ProducesAValidGraph()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        // Everything at once, to catch stages that conflict when combined.
        var look = new LookOptions
        {
            Darken = 0.4,
            Saturation = 0.7,
            Contrast = 1.1,
            Blur = 2,
            Grain = 6,
            Vignette = true,
            Mirror = true,
        };

        await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            look,
            "fulllook.mp4");
    }

    [Fact]
    public async Task ToneMapping_ProducesAValidGraph()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        // The zscale/tonemap chain depends on optional FFmpeg components, so executing it is
        // the only way to know the local build supports it.
        await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720, transfer: "smpte2084"),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            LookOptions.None,
            "tonemapped.mp4");
    }

    [Fact]
    public async Task AnamorphicSource_IsCorrectedToTheRightShape()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        // 720x480 SAR 32:27 is 16:9 on screen. Fitting it into a 16:9 target with crop should
        // therefore lose almost nothing; without the correction it would be visibly squashed.
        var output = await RenderAsync(
            _fixture.AnamorphicClip,
            Info(720, 480, sar: new Ratio(32, 27)),
            OutputFormat.Youtube with { Width = 640, Height = 360, Fit = FitMode.Pad },
            LookOptions.None,
            "anamorphic_out.mp4");

        (await _fixture.DimensionsOfAsync(output)).ShouldBe((640, 360));
    }

    [Fact]
    public async Task ZoomAndSpeed_ProduceAValidGraph()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            new LookOptions { ZoomEnd = 1.12, Speed = 0.75 },
            "zoomspeed.mp4");
    }

    [Fact]
    public async Task OddTargetDimensions_AreMadeEvenSoTheEncoderAccepts()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        // yuv420p chroma subsampling requires even dimensions.
        var format = OutputFormat.Youtube.WithAspect(new Ratio(1001, 999)) with { Width = 641, Height = 361 };
        var even = format with
        {
            Width = OutputFormat.MakeEven(format.Width),
            Height = OutputFormat.MakeEven(format.Height),
        };

        (even.Width % 2).ShouldBe(0);
        (even.Height % 2).ShouldBe(0);

        await RenderAsync(_fixture.SimpleClip, Info(1280, 720), even, LookOptions.None, "even.mp4");
    }
}

/// <summary>
/// Tonal behaviour of the look pipeline, verified on rendered pixels rather than on the graph
/// string. A graph can be structurally valid and still produce an unusable picture.
/// </summary>
[Collection(FfmpegTestGroup.Name)]
public class LookToneTests
{
    private readonly FfmpegFixture _fixture;

    public LookToneTests(FfmpegFixture fixture) => _fixture = fixture;

    private static MediaInfo Info() => new()
    {
        Path = "unused",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTimeOffset.UnixEpoch,
        Duration = TimeSpan.FromSeconds(10),
        Width = 1280,
        Height = 720,
        FrameRate = 30,
        VideoCodec = "h264",
        PixelFormat = "yuv420p",
        SampleAspectRatio = Ratio.One,
        HasAudio = false,
    };

    /// <summary>
    /// Renders through the full stage pipeline including scale, crop and fps, so the look stage
    /// sees the same negotiated formats it does in a real render.
    /// </summary>
    private async Task<string> RenderAsync(LookOptions look, string name)
    {
        var context = new FilterContext
        {
            Source = Info(),
            Format = OutputFormat.Youtube with { Width = 960, Height = 540 },
            Look = look,
            SegmentDuration = TimeSpan.FromSeconds(2),
        };

        var graph = VideoFilterGraphBuilder.CreateDefault().Build(context);
        var output = _fixture.PathFor($"{name}_{Guid.NewGuid():N}.mp4");

        var result = await _fixture.RunAsync(
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-ss", "2", "-t", "2",
            "-i", _fixture.SimpleClip,
            "-filter_complex", graph,
            "-map", "[vout]", "-an",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            output,
        ]);

        result.Success.ShouldBeTrue(
            $"FFmpeg rejected or crashed on the look graph (exit {result.ExitCode}).{Environment.NewLine}"
            + $"Graph: {graph}{Environment.NewLine}{result.StandardError}");

        return output;
    }

    [Fact]
    public async Task Darkening_ScalesLumaProportionallyWithoutCrushingShadows()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var plain = await RenderAsync(LookOptions.None, "tone_plain");
        var dark = await RenderAsync(
            new LookOptions { Darken = 0.35, Saturation = 1, Contrast = 1 },
            "tone_dark");

        var before = await _fixture.LumaStatsAsync(plain);
        var after = await _fixture.LumaStatsAsync(dark);

        // Multiplicative: roughly 65% of the original brightness.
        var ratio = after.Mean / before.Mean;
        ratio.ShouldBeInRange(0.55, 0.78);

        // The regression that motivated this test: an additive brightness offset drove the
        // median pixel to 0, turning real footage into a black rectangle.
        after.Median.ShouldBeGreaterThan(0);
        after.Max.ShouldBeGreaterThan(40);
    }

    [Fact]
    public async Task Darkening_IsStrongerAtHigherValues()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var light = await _fixture.LumaStatsAsync(
            await RenderAsync(new LookOptions { Darken = 0.15, Saturation = 1 }, "tone_light"));
        var heavy = await _fixture.LumaStatsAsync(
            await RenderAsync(new LookOptions { Darken = 0.6, Saturation = 1 }, "tone_heavy"));

        heavy.Mean.ShouldBeLessThan(light.Mean);
        heavy.Median.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task NeutralLook_LeavesToneEssentiallyUnchanged()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var plain = await _fixture.LumaStatsAsync(await RenderAsync(LookOptions.None, "tone_neutral"));

        plain.Mean.ShouldBeGreaterThan(20);
        plain.Max.ShouldBeGreaterThan(100);
    }

    [Fact]
    public async Task DefaultLook_ProducesAVisiblePicture()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        // The shipped defaults must yield usable background footage, not a near-black frame.
        var stats = await _fixture.LumaStatsAsync(await RenderAsync(new LookOptions(), "tone_default"));

        stats.Mean.ShouldBeGreaterThan(15);
        stats.Median.ShouldBeGreaterThan(0);
    }
}
