using ReviewClips.Core.Media;
using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;
using ReviewClips.Ffmpeg.Filters;

namespace ReviewClips.Ffmpeg.Tests.Filters;

public class VideoFilterGraphBuilderTests
{
    private static MediaInfo Source(
        int width = 1920,
        int height = 1080,
        string pixelFormat = "yuv420p",
        string? colorTransfer = null,
        Ratio? sar = null) => new()
        {
            Path = "/movies/film.mkv",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTimeOffset.UnixEpoch,
            Duration = TimeSpan.FromMinutes(90),
            Width = width,
            Height = height,
            FrameRate = 24,
            VideoCodec = "h264",
            PixelFormat = pixelFormat,
            SampleAspectRatio = sar ?? Ratio.One,
            HasAudio = true,
            ColorTransfer = colorTransfer,
        };

    private static FilterContext Context(
        MediaInfo? source = null,
        OutputFormat? format = null,
        LookOptions? look = null,
        int? overlayIndex = null) => new()
        {
            Source = source ?? Source(),
            Format = format ?? OutputFormat.Youtube,
            Look = look ?? LookOptions.None,
            SegmentDuration = TimeSpan.FromSeconds(5),
            OverlayInputIndex = overlayIndex,
        };

    private static string Build(FilterContext context) =>
        VideoFilterGraphBuilder.CreateDefault().Build(context);

    [Fact]
    public void Graph_AlwaysEndsAtTheRequestedOutputLabel() =>
        Build(Context()).ShouldEndWith("[vout]");

    [Fact]
    public void Graph_AlwaysStartsFromTheRequestedInputLabel() =>
        Build(Context()).ShouldStartWith("[0:v]");

    [Fact]
    public void Graph_ForcesACompatiblePixelFormatLast()
    {
        var graph = Build(Context());

        graph.ShouldContain("format=yuv420p[vout]");
    }

    [Fact]
    public void Graph_SetsSampleAspectRatioAfterScaling()
    {
        // Without setsar the output inherits a non-square SAR and players squash it.
        Build(Context()).ShouldContain("setsar=1");
    }

    // --- Tone mapping ------------------------------------------------------

    [Fact]
    public void ToneMap_IsSkippedForOrdinarySdrSources() =>
        Build(Context()).ShouldNotContain("tonemap");

    [Theory]
    [InlineData("smpte2084")]
    [InlineData("arib-std-b67")]
    public void ToneMap_IsAppliedForHdrTransferFunctions(string transfer)
    {
        var graph = Build(Context(Source(colorTransfer: transfer, pixelFormat: "yuv420p10le")));

        graph.ShouldContain("t=linear:npl=100");
        graph.ShouldContain("tonemap=tonemap=hable");
        graph.ShouldContain("zscale=t=bt709:m=bt709:r=tv");
    }

    [Fact]
    public void ToneMap_StatesInputColourCharacteristicsExplicitly()
    {
        // Leaving these implicit makes zscale fail on files with partial colour metadata.
        var graph = Build(Context(Source(colorTransfer: "smpte2084")));

        graph.ShouldContain("setparams=color_trc=smpte2084");
        graph.ShouldContain("colorspace=bt2020nc");
        graph.ShouldContain("color_primaries=bt2020");
    }

    [Fact]
    public void ToneMap_PrefersDetectedMetadataOverHdrDefaults()
    {
        var source = Source(colorTransfer: "arib-std-b67") with
        {
            ColorPrimaries = "bt709",
            ColorSpace = "bt709",
        };

        var graph = Build(Context(source));

        graph.ShouldContain("color_trc=arib-std-b67");
        graph.ShouldContain("color_primaries=bt709");
    }

    [Fact]
    public void ToneMap_TreatsUnknownMetadataAsMissing()
    {
        var source = Source(colorTransfer: "smpte2084") with
        {
            ColorPrimaries = "unknown",
            ColorSpace = "unknown",
        };

        var graph = Build(Context(source));

        graph.ShouldNotContain("unknown");
        graph.ShouldContain("colorspace=bt2020nc");
        graph.ShouldContain("color_primaries=bt2020");
    }

    [Fact]
    public void ToneMap_AssumesBt709InputWhenForcedOnAnSdrSource()
    {
        var context = Context(format: OutputFormat.Youtube with { ToneMap = ToneMapMode.Hable });

        Build(context).ShouldContain("setparams=color_trc=bt709");
    }

    [Fact]
    public void ToneMap_RunsBeforeScaling()
    {
        var graph = Build(Context(Source(colorTransfer: "smpte2084")));

        // Tone-mapping must happen while the data is still wide gamut.
        graph.IndexOf("tonemap", StringComparison.Ordinal)
            .ShouldBeLessThan(graph.IndexOf("scale=1920:1080", StringComparison.Ordinal));
    }

    [Fact]
    public void ToneMap_CanBeDisabledExplicitly()
    {
        var context = Context(
            Source(colorTransfer: "smpte2084"),
            OutputFormat.Youtube with { ToneMap = ToneMapMode.None });

        Build(context).ShouldNotContain("tonemap");
    }

    [Fact]
    public void ToneMap_AppliesToSdrWhenForced()
    {
        var context = Context(format: OutputFormat.Youtube with { ToneMap = ToneMapMode.Mobius });

        Build(context).ShouldContain("tonemap=tonemap=mobius");
    }

    // --- Anamorphic --------------------------------------------------------

    [Fact]
    public void AnamorphicSource_IsSquaredUpBeforeFitting()
    {
        // 720x480 with SAR 32:27 is a 16:9 DVD; scaling on stored dimensions would squash it.
        var context = Context(Source(720, 480, sar: new Ratio(32, 27)));
        var graph = Build(context);

        graph.ShouldContain("scale=trunc(iw*sar/2)*2:ih");

        graph.IndexOf("scale=trunc(iw*sar/2)*2:ih", StringComparison.Ordinal)
            .ShouldBeLessThan(graph.IndexOf("scale=1920:1080", StringComparison.Ordinal));
    }

    [Fact]
    public void SquarePixelSource_SkipsTheCorrection() =>
        Build(Context(Source(1920, 1080))).ShouldNotContain("iw*sar");

    // --- Fit modes ---------------------------------------------------------

    [Fact]
    public void CropFit_ScalesToCoverThenCrops()
    {
        var graph = Build(Context(format: OutputFormat.Youtube with { Fit = FitMode.Crop }));

        graph.ShouldContain("scale=1920:1080:force_original_aspect_ratio=increase");
        graph.ShouldContain("crop=1920:1080");
    }

    [Fact]
    public void PadFit_ScalesToFitThenLetterboxes()
    {
        var graph = Build(Context(format: OutputFormat.Youtube with { Fit = FitMode.Pad }));

        graph.ShouldContain("force_original_aspect_ratio=decrease");
        graph.ShouldContain("pad=1920:1080:(ow-iw)/2:(oh-ih)/2:black");
    }

    [Fact]
    public void StretchFit_ScalesWithoutPreservingAspect()
    {
        var graph = Build(Context(format: OutputFormat.Youtube with { Fit = FitMode.Stretch }));

        graph.ShouldContain("scale=1920:1080");
        graph.ShouldNotContain("force_original_aspect_ratio");
    }

    [Fact]
    public void BlurPadFit_SplitsScalesAndRecombines()
    {
        var graph = Build(Context(format: OutputFormat.Shorts));

        // Branch: a blurred cover copy behind the fitted original.
        graph.ShouldContain("split=2");
        graph.ShouldContain("gblur=sigma=24");
        graph.ShouldContain("force_original_aspect_ratio=increase");
        graph.ShouldContain("force_original_aspect_ratio=decrease");
        graph.ShouldContain("overlay=(W-w)/2:(H-h)/2");
    }

    [Fact]
    public void BlurPadFit_ProducesAConnectedGraph()
    {
        var graph = Build(Context(format: OutputFormat.Shorts));

        // Every intermediate label must be produced exactly once and consumed exactly once.
        var produced = System.Text.RegularExpressions.Regex
            .Matches(graph, @"\[(fx\d+)\](?=;|$)")
            .Select(m => m.Groups[1].Value)
            .ToList();

        produced.Distinct().Count().ShouldBe(produced.Count, "a label is produced more than once");

        foreach (var label in produced)
        {
            var consumed = System.Text.RegularExpressions.Regex
                .Count(graph, $@"\[{label}\]");

            consumed.ShouldBe(2, $"label {label} should be written once and read once");
        }
    }

    // --- Look --------------------------------------------------------------

    [Fact]
    public void Look_DarkensMultiplicativelyRatherThanByAdditiveOffset()
    {
        var graph = Build(Context(look: new LookOptions { Darken = 0.35, Saturation = 1, Contrast = 1 }));

        // Scaling luma to 0.65 about the black pedestal preserves shadow detail. An
        // additive brightness offset crushes everything below it to pure black.
        graph.ShouldContain("lutyuv=y=minval+(val-minval)*0.65");
        graph.ShouldNotContain("brightness");

        // colorlevels is the obvious alternative but segfaults FFmpeg 8.1 here.
        graph.ShouldNotContain("colorlevels");
    }

    [Fact]
    public void Look_CombinesSaturationAndContrastIntoASingleEqFilter()
    {
        var graph = Build(Context(look: new LookOptions { Darken = 0, Saturation = 0.7, Contrast = 1.2 }));

        graph.ShouldContain("eq=saturation=0.7:contrast=1.2");
    }

    [Fact]
    public void Look_AppliesDarkeningBeforeColourAdjustments()
    {
        var graph = Build(Context(look: new LookOptions { Darken = 0.4, Saturation = 0.7 }));

        graph.IndexOf("lutyuv", StringComparison.Ordinal)
            .ShouldBeLessThan(graph.IndexOf("eq=", StringComparison.Ordinal));
    }

    [Fact]
    public void Look_IsOmittedEntirelyWhenNeutral() =>
        Build(Context(look: LookOptions.None)).ShouldNotContain("eq=");

    [Fact]
    public void Look_UsesInvariantNumberFormatting()
    {
        // A comma decimal separator would silently split the filter argument list.
        var graph = Build(Context(look: new LookOptions { Darken = 0.35, Saturation = 0.85 }));

        graph.ShouldNotContain(",saturation");
        graph.ShouldContain("0.65");
        graph.ShouldContain("0.85");
    }

    [Fact]
    public void Grain_IsAppliedAfterBlurSoItIsNotSmoothedAway()
    {
        var graph = Build(Context(look: new LookOptions { Blur = 3, Grain = 8 }));

        graph.IndexOf("gblur", StringComparison.Ordinal)
            .ShouldBeLessThan(graph.IndexOf("noise=alls=8", StringComparison.Ordinal));
    }

    [Fact]
    public void Vignette_AndMirror_AreAppliedWhenRequested()
    {
        var graph = Build(Context(look: new LookOptions { Vignette = true, Mirror = true }));

        graph.ShouldContain("vignette");
        graph.ShouldContain("hflip");
    }

    [Fact]
    public void Speed_RetimesWithSetpts()
    {
        var graph = Build(Context(look: new LookOptions { Speed = 0.5 }));

        graph.ShouldContain("setpts=PTS/0.5");
    }

    [Fact]
    public void Zoom_PacesTheDriftToReachTheTargetByTheFinalFrame()
    {
        var context = Context(look: new LookOptions { ZoomEnd = 1.1 }) with
        {
            SegmentDuration = TimeSpan.FromSeconds(5),
        };

        var graph = Build(context);

        graph.ShouldContain("zoompan=");
        graph.ShouldContain("1.1");
        graph.ShouldContain("s=1920x1080");
    }

    [Fact]
    public void Zoom_IsOmittedAtUnityScale() =>
        Build(Context(look: new LookOptions { ZoomEnd = 1.0 })).ShouldNotContain("zoompan");

    [Fact]
    public void Overlay_IsOnlyEmittedWhenAnInputIndexIsAvailable()
    {
        var look = new LookOptions { OverlayPath = "/tmp/logo.png", OverlayOpacity = 0.5 };

        // No index means the caller did not add the second -i, so the stage must stay silent.
        Build(Context(look: look)).ShouldNotContain("overlay=0:0");

        var withInput = Build(Context(look: look, overlayIndex: 1));
        withInput.ShouldContain("[1:v]");
        withInput.ShouldContain("colorchannelmixer=aa=0.5");
        withInput.ShouldContain("overlay=0:0:shortest=1");
    }

    [Fact]
    public void Lut_EscapesPathSeparatorsThatWouldBreakFilterSyntax()
    {
        var graph = Build(Context(look: new LookOptions { LutPath = "/luts/my:grade,v2.cube" }));

        // Raw colons and commas would terminate the filter argument early.
        graph.ShouldContain(@"lut3d=file=/luts/my\:grade\,v2.cube");
    }

    // --- Stage ordering ----------------------------------------------------

    [Fact]
    public void ActiveStageNames_ReflectTheEnabledStagesInOrder()
    {
        var context = Context(
            Source(colorTransfer: "smpte2084", sar: new Ratio(32, 27)),
            OutputFormat.Shorts,
            new LookOptions { Darken = 0.3, Grain = 5, Vignette = true });

        var names = VideoFilterGraphBuilder.CreateDefault().ActiveStageNames(context);

        names.ShouldBe(
        [
            "tonemap",
            "square-pixels",
            "fit",
            "fps",
            "look",
            "vignette",
            "grain",
            "output-format",
        ]);
    }

    [Fact]
    public void FrameRateNormalisation_IsAlwaysApplied()
    {
        // Variable-frame-rate sources otherwise desynchronise when concatenated.
        Build(Context(format: OutputFormat.Youtube with { FrameRate = 30 }))
            .ShouldContain("fps=30");
    }
}

/// <summary>
/// The blur-pad foreground scale, which trades width for height when a very wide source is
/// placed in a very tall frame.
/// </summary>
public class BlurPadForegroundScaleTests
{
    private static FilterContext Context(double scale) => new()
    {
        Source = new ReviewClips.Core.Media.MediaInfo
        {
            Path = "/movies/scope.mkv",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTimeOffset.UnixEpoch,
            Duration = TimeSpan.FromMinutes(90),

            // 2.35:1 scope, the common cinema shape.
            Width = 1920,
            Height = 816,
            FrameRate = 24,
            VideoCodec = "h264",
            PixelFormat = "yuv420p",
            SampleAspectRatio = Ratio.One,
            HasAudio = false,
        },
        Format = OutputFormat.Shorts with
        {
            Width = 1080,
            Height = 1920,
            BlurPadForegroundScale = scale,
        },
        Look = LookOptions.None,
        SegmentDuration = TimeSpan.FromSeconds(5),
    };

    private static string Build(double scale) =>
        VideoFilterGraphBuilder.CreateDefault().Build(Context(scale));

    [Fact]
    public void ScaleOfOne_FitsTheWholeFrameWithNoCrop()
    {
        var graph = Build(1.0);

        graph.ShouldContain("scale=1080:1920:force_original_aspect_ratio=decrease");
        graph.ShouldNotContain("crop=min");
    }

    [Fact]
    public void ScaleAboveOne_EnlargesThenCropsBackToTheFrame()
    {
        var graph = Build(1.5);

        graph.ShouldContain("scale=1620:2880:force_original_aspect_ratio=decrease");

        // Cropping only where the enlarged image actually overflows.
        graph.ShouldContain(@"crop=min(iw\,1080):min(ih\,1920)");
    }

    [Fact]
    public void ScaleIsClampedToASaneRange()
    {
        // Absurd values must not produce a gigantic intermediate scale.
        Build(99).ShouldContain("scale=4320:7680");
        Build(0.1).ShouldNotContain("crop=min");
    }

    [Fact]
    public void BackdropIsUnaffectedByTheForegroundScale()
    {
        var graph = Build(1.5);

        // The blurred cover layer always fills the frame exactly.
        graph.ShouldContain("scale=1080:1920:force_original_aspect_ratio=increase");
        graph.ShouldContain("crop=1080:1920");
    }
}
