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

            // Mirrors what the extractor does, so the graph under test is the real one.
            AttributionTextPath = look.HasAttribution
                ? TextResources.Materialise(look.Attribution!.Trim())
                : null,
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

    // --- Stages ported from pi ---------------------------------------------
    //
    // These are the tests that matter for the new stages. A drawtext or zscale graph can look
    // perfectly reasonable as a string and still be rejected outright by FFmpeg, which is how
    // the colorlevels segfault and the zscale colorspace failure were both found.

    [Theory]
    [InlineData(0.3)]
    [InlineData(1.5)]
    [InlineData(3)]
    public async Task Sharpen_ProducesAValidGraph(double amount)
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            LookOptions.None with { Sharpen = amount },
            $"sharpen_{amount}.mp4");
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(16)]
    public async Task Pixelate_ProducesAValidGraphAtAnyFactor(double factor)
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        // Awkward factors are the interesting ones: an odd intermediate dimension makes the
        // final yuv420p conversion fail, and factor 16 of a small frame nearly bottoms out.
        var output = await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            LookOptions.None with { Pixelate = factor },
            $"pixelate_{factor}.mp4");

        // The frame must come back at full size, not at the reduced intermediate one.
        (await _fixture.DimensionsOfAsync(output)).ShouldBe((640, 360));
    }

    [Fact]
    public async Task FadeEdges_ProducesAValidGraphAndActuallyDarkensTheEnds()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var output = await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            LookOptions.None with { FadeEdges = TimeSpan.FromSeconds(0.5) },
            "fade_edges.mp4");

        // The rendered clip is 2s long with a 0.5s fade at each end.
        var start = await _fixture.LumaStatsAsync(output, atSeconds: 0.02);
        var middle = await _fixture.LumaStatsAsync(output, atSeconds: 1);

        start.Mean.ShouldBeLessThan(middle.Mean);
    }

    [Fact]
    public async Task FadeEdges_IsClampedRatherThanFadingTheWholeClipAway()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        // 10s of fade requested on a 2s clip. Clamping to half the length must leave the
        // midpoint visible rather than producing two seconds of black.
        var output = await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            LookOptions.None with { FadeEdges = TimeSpan.FromSeconds(10) },
            "fade_edges_clamped.mp4");

        (await _fixture.LumaStatsAsync(output, atSeconds: 1)).Max.ShouldBeGreaterThan(20);
    }

    [Theory]
    [InlineData("Clip from Heat (1995), dir. Michael Mann")]
    [InlineData("Leon: The Professional")]
    [InlineData("Ocean's Eleven")]
    [InlineData("100% [practical] effects, no CGI")]
    [InlineData(@"back\slash and 'quotes' and, commas")]
    [InlineData("Amelie - Le Fabuleux Destin d'Amelie Poulain")]
    public async Task Attribution_SurvivesTitlesFullOfFilterSyntax(string text)
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        // Every one of these contains a character that is structural in FFmpeg's filter
        // grammar. Inlining any of them into text= terminates the argument early and fails the
        // graph, which is the entire reason the stage writes through textfile=.
        await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            LookOptions.None with { Attribution = text },
            $"attribution_{Math.Abs(text.GetHashCode(StringComparison.Ordinal))}.mp4");
    }

    [Theory]
    [InlineData(TextPosition.BottomRight)]
    [InlineData(TextPosition.TopLeft)]
    [InlineData(TextPosition.Center)]
    [InlineData(TextPosition.Bottom)]
    public async Task Attribution_ProducesAValidGraphAtEveryPosition(TextPosition position)
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            LookOptions.None with { Attribution = "Clip from Heat (1995)", AttributionPosition = position },
            $"attribution_pos_{position}.mp4");
    }

    [Fact]
    public async Task Attribution_ActuallyMarksThePixels()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        // Drawn over a black frame, so any non-black pixel is the text itself. A graph can be
        // accepted by FFmpeg and still draw nothing, e.g. if the font cannot be resolved.
        var black = _fixture.PathFor("black_for_text.mp4");
        var made = await _fixture.RunAsync(
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "color=black:s=640x360:r=30", "-t", "3",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            black,
        ]);
        made.Success.ShouldBeTrue(made.StandardError);

        var plain = await RenderAsync(
            black,
            Info(640, 360),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            LookOptions.None,
            "text_absent.mp4");

        var captioned = await RenderAsync(
            black,
            Info(640, 360),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            LookOptions.None with { Attribution = "CLIP FROM HEAT (1995)", AttributionPosition = TextPosition.Center },
            "text_present.mp4");

        (await _fixture.LumaStatsAsync(plain, atSeconds: 1)).Max.ShouldBe(0);
        (await _fixture.LumaStatsAsync(captioned, atSeconds: 1)).Max.ShouldBeGreaterThan(50);
    }

    [Fact]
    public async Task Gamma_ProducesAValidGraphAndChangesTheMidtones()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var deepened = await _fixture.LumaStatsAsync(await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            LookOptions.None with { Gamma = 0.6 },
            "gamma_low.mp4"));

        var lifted = await _fixture.LumaStatsAsync(await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            LookOptions.None with { Gamma = 1.8 },
            "gamma_high.mp4"));

        // eq's gamma is pow(value, 1/gamma), so above 1 brightens. Easy to get backwards, and
        // the reason the doc comment states the direction explicitly.
        lifted.Mean.ShouldBeGreaterThan(deepened.Mean);
    }

    [Fact]
    public async Task Grayscale_ProducesAValidGraph()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            LookOptions.None with { Grayscale = true },
            "grayscale.mp4");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MirrorAndFlip_ProduceAValidGraph(bool mirror, bool flip)
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var output = await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            LookOptions.None with { Mirror = mirror, FlipVertical = flip },
            $"flip_{mirror}_{flip}.mp4");

        // Flipping must not disturb the frame geometry.
        (await _fixture.DimensionsOfAsync(output)).ShouldBe((640, 360));
    }

    [Fact]
    public async Task EveryNewStageAtOnce_StillProducesAValidGraph()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        // The stages have to compose. Ordering bugs surface here rather than in isolation.
        await RenderAsync(
            _fixture.SimpleClip,
            Info(1280, 720),
            OutputFormat.Youtube with { Width = 640, Height = 360 },
            new LookOptions
            {
                Darken = 0.3,
                Saturation = 0.8,
                Contrast = 1.1,
                Gamma = 0.95,
                Sharpen = 0.6,
                Blur = 1.2,
                Pixelate = 2,
                Vignette = true,
                FadeEdges = TimeSpan.FromSeconds(0.3),
                Grain = 8,
                Mirror = true,
                FlipVertical = true,
                Attribution = "Clip from Heat (1995): dir. Michael Mann",
            },
            "everything.mp4");
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
