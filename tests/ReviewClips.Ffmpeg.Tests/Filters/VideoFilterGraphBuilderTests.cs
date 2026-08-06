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
        string? attributionTextPath = null,
        double segmentSeconds = 5) => new()
        {
            Source = source ?? Source(),
            Format = format ?? OutputFormat.Youtube,
            Look = look ?? LookOptions.None,
            SegmentDuration = TimeSpan.FromSeconds(segmentSeconds),
            AttributionTextPath = attributionTextPath,
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
    public void Graph_SetsSampleAspectRatioAfterScaling() =>
        // Without setsar the output inherits a non-square SAR and players squash it.
        Build(Context()).ShouldContain("setsar=1");

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
    public void ToneMap_StatesInputColorCharacteristicsExplicitly()
    {
        // Leaving these implicit makes zscale fail on files with partial color metadata.
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
    public void Look_AppliesDarkeningBeforeColorAdjustments()
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
    public void FrameRateNormalization_IsAlwaysApplied()
    {
        // Variable-frame-rate sources otherwise desynchronize when concatenated.
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

/// <summary>
/// The stages ported from pi, plus the options folded into existing stages.
/// </summary>
public class TreatmentStageTests
{
    private static FilterContext Context(LookOptions look, double segmentSeconds = 5) => new()
    {
        Source = new MediaInfo
        {
            Path = "/movies/film.mkv",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTimeOffset.UnixEpoch,
            Duration = TimeSpan.FromMinutes(90),
            Width = 1920,
            Height = 1080,
            FrameRate = 24,
            VideoCodec = "h264",
            PixelFormat = "yuv420p",
            SampleAspectRatio = Ratio.One,
            HasAudio = true,
        },
        Format = OutputFormat.Youtube,
        Look = look,
        SegmentDuration = TimeSpan.FromSeconds(segmentSeconds),
        AttributionTextPath = look.HasAttribution ? "/tmp/reviewclips/text/abc123.txt" : null,
    };

    private static string Build(LookOptions look, double segmentSeconds = 5) =>
        VideoFilterGraphBuilder.CreateDefault().Build(Context(look, segmentSeconds));

    private static IReadOnlyList<string> Stages(LookOptions look) =>
        VideoFilterGraphBuilder.CreateDefault().ActiveStageNames(Context(look));

    // --- Sharpen -----------------------------------------------------------

    [Fact]
    public void Sharpen_IsAbsentByDefault() =>
        Build(LookOptions.None).ShouldNotContain("unsharp");

    [Fact]
    public void Sharpen_EmitsALumaOnlyUnsharpMask()
    {
        var graph = Build(LookOptions.None with { Sharpen = 0.8 });

        // Chroma amount is 0: sharpening chroma on compressed footage amplifies blocking.
        graph.ShouldContain("unsharp=5:5:0.8:5:5:0");
    }

    [Fact]
    public void Sharpen_RunsBeforeBlurSoAnExplicitBlurStillWins()
    {
        var graph = Build(LookOptions.None with { Sharpen = 1, Blur = 4 });

        graph.IndexOf("unsharp", StringComparison.Ordinal)
            .ShouldBeLessThan(graph.IndexOf("gblur", StringComparison.Ordinal));
    }

    [Fact]
    public void Sharpen_IsClamped() =>
        Build(LookOptions.None with { Sharpen = 99 }).ShouldContain("unsharp=5:5:3:5:5:0");

    // --- Pixelate ----------------------------------------------------------

    [Fact]
    public void Pixelate_IsAbsentByDefault() =>
        Build(LookOptions.None).ShouldNotContain("neighbor");

    [Fact]
    public void Pixelate_DownscalesAndUpscalesWithNearestNeighbor()
    {
        // 1920x1080 at factor 4 is 480x270, then back to full size.
        var graph = Build(LookOptions.None with { Pixelate = 4 });

        graph.ShouldContain("scale=480:270:flags=neighbor");
        graph.ShouldContain("scale=1920:1080:flags=neighbor");
    }

    [Fact]
    public void Pixelate_AlwaysProducesEvenIntermediateDimensions()
    {
        // 1080/7 is 154.28..., which must not round to an odd number: an odd dimension makes
        // the yuv420p conversion at the end of the graph fail outright.
        var graph = Build(LookOptions.None with { Pixelate = 7 });

        var match = System.Text.RegularExpressions.Regex.Match(graph, @"scale=(\d+):(\d+):flags=neighbor");
        match.Success.ShouldBeTrue();

        (int.Parse(match.Groups[1].Value) % 2).ShouldBe(0);
        (int.Parse(match.Groups[2].Value) % 2).ShouldBe(0);
    }

    [Fact]
    public void Pixelate_IsSkippedAtOrBelowOne()
    {
        Build(LookOptions.None with { Pixelate = 1 }).ShouldNotContain("neighbor");
        Build(LookOptions.None with { Pixelate = 0 }).ShouldNotContain("neighbor");
    }

    [Fact]
    public void Pixelate_RunsAfterBlur()
    {
        var graph = Build(LookOptions.None with { Blur = 3, Pixelate = 4 });

        // Blurring after pixelating would soften away the blocks that are the point.
        graph.IndexOf("gblur", StringComparison.Ordinal)
            .ShouldBeLessThan(graph.IndexOf("neighbor", StringComparison.Ordinal));
    }

    // --- Fade edges --------------------------------------------------------

    [Fact]
    public void FadeEdges_IsAbsentByDefault() =>
        Build(LookOptions.None).ShouldNotContain("fade=t=");

    [Fact]
    public void FadeEdges_FadesInAtTheStartAndOutAtTheEnd()
    {
        var graph = Build(LookOptions.None with { FadeEdges = TimeSpan.FromSeconds(0.5) }, segmentSeconds: 6);

        graph.ShouldContain("fade=t=in:st=0:d=0.5");
        graph.ShouldContain("fade=t=out:st=5.5:d=0.5");
    }

    [Fact]
    public void FadeEdges_IsClampedToHalfTheClip()
    {
        // Two 3s fades cannot fit in a 4s clip; past halfway they would meet in the middle and
        // the clip would never reach full brightness.
        var graph = Build(LookOptions.None with { FadeEdges = TimeSpan.FromSeconds(3) }, segmentSeconds: 4);

        graph.ShouldContain("fade=t=in:st=0:d=2");
        graph.ShouldContain("fade=t=out:st=2:d=2");
    }

    // --- Attribution -------------------------------------------------------

    [Fact]
    public void Attribution_IsAbsentByDefault() =>
        Build(LookOptions.None).ShouldNotContain("drawtext");

    [Fact]
    public void Attribution_ReadsTheTextFromAFileRatherThanInlining()
    {
        var graph = Build(LookOptions.None with { Attribution = "Clip from Heat (1995)" });

        // No user string ever reaches the filter grammar.
        graph.ShouldContain("drawtext=textfile=");
        graph.ShouldNotContain("Heat");
    }

    [Fact]
    public void Attribution_DisablesTextExpansion()
    {
        // Without this a % or {} in a title is read as strftime and expression syntax.
        Build(LookOptions.None with { Attribution = "100% practical effects" })
            .ShouldContain("expansion=none");
    }

    [Fact]
    public void Attribution_EscapesTheTextFilePath()
    {
        var graph = VideoFilterGraphBuilder.CreateDefault().Build(new FilterContext
        {
            Source = Context(LookOptions.None).Source,
            Format = OutputFormat.Youtube,
            Look = LookOptions.None with { Attribution = "x" },
            SegmentDuration = TimeSpan.FromSeconds(5),
            AttributionTextPath = @"C:\temp\a b\text.txt",
        });

        // A colon is structural in filter syntax and would terminate the argument early.
        graph.ShouldContain(@"C\:");
    }

    [Theory]
    [InlineData(TextPosition.BottomRight, "x=w-tw-27:y=h-th-27")]
    [InlineData(TextPosition.BottomLeft, "x=27:y=h-th-27")]
    [InlineData(TextPosition.TopLeft, "x=27:y=27")]
    [InlineData(TextPosition.TopRight, "x=w-tw-27:y=27")]
    [InlineData(TextPosition.Top, "x=(w-tw)/2:y=27")]
    [InlineData(TextPosition.Bottom, "x=(w-tw)/2:y=h-th-27")]
    [InlineData(TextPosition.Center, "x=(w-tw)/2:y=(h-th)/2")]
    public void Attribution_AnchorsToTheRequestedCorner(TextPosition position, string expected) =>
        Build(LookOptions.None with { Attribution = "credit", AttributionPosition = position })
            .ShouldContain(expected);

    [Fact]
    public void Attribution_ShrinksTheTypeSoALongLineStillFits()
    {
        var shortLine = Build(LookOptions.None with { Attribution = "Heat" });
        var longLine = Build(LookOptions.None with
        {
            Attribution = "Clip from Heat (1995), directed by Michael Mann, "
                + "shown here for the purposes of criticism and review",
        });

        static double FontSize(string graph) => double.Parse(
            System.Text.RegularExpressions.Regex.Match(graph, @"fontsize=([\d.]+)").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);

        // drawtext cannot wrap, so a long line at the default size would run off the frame.
        FontSize(longLine).ShouldBeLessThan(FontSize(shortLine));
        FontSize(longLine).ShouldBeGreaterThanOrEqualTo(12);
    }

    [Fact]
    public void Attribution_IsSkippedWhenNoTextFileWasWritten()
    {
        var graph = VideoFilterGraphBuilder.CreateDefault().Build(new FilterContext
        {
            Source = Context(LookOptions.None).Source,
            Format = OutputFormat.Youtube,
            Look = LookOptions.None with { Attribution = "credit" },
            SegmentDuration = TimeSpan.FromSeconds(5),
            AttributionTextPath = null,
        });

        graph.ShouldNotContain("drawtext");
    }

    [Fact]
    public void Attribution_RunsAfterEverythingThatCouldObscureIt()
    {
        var look = LookOptions.None with
        {
            Attribution = "credit",
            Blur = 4,
            Grain = 20,
            Pixelate = 3,
            Vignette = true,
        };

        var stages = Stages(look);
        var attribution = stages.ToList().IndexOf("attribution");

        // Anything that obscures the frame must precede the credit line.
        foreach (var obscuring in new[] { "blur", "grain", "pixelate", "vignette" })
        {
            stages.ToList().IndexOf(obscuring).ShouldBeLessThan(attribution);
        }

        // But the pixel-format conversion still comes last.
        stages[^1].ShouldBe("output-format");
    }

    // --- Gamma and grayscale, folded into the look stage --------------------

    [Fact]
    public void Gamma_IsAbsentByDefault() =>
        Build(LookOptions.None).ShouldNotContain("gamma");

    [Fact]
    public void Gamma_UsesEqWhichIsAPowerCurveNotAnOffset() =>
        Build(LookOptions.None with { Gamma = 0.9 }).ShouldContain("gamma=0.9");

    [Fact]
    public void Grayscale_ZeroesSaturation() =>
        Build(LookOptions.None with { Grayscale = true }).ShouldContain("saturation=0");

    [Fact]
    public void Grayscale_WinsOverAnExplicitSaturation()
    {
        // The more specific request wins; the two coincide only when a profile set one and the
        // user then asked for the other.
        var graph = Build(LookOptions.None with { Grayscale = true, Saturation = 1.4 });

        graph.ShouldContain("saturation=0");
        graph.ShouldNotContain("saturation=1.4");
    }

    [Fact]
    public void Darken_IsStillMultiplicativeAndNotAnEqBrightnessOffset()
    {
        // Guards the reasoning in LookStages.cs: eq=brightness subtracts a constant and crushes
        // dark footage to black.
        var graph = Build(LookOptions.None with { Darken = 0.45 });

        graph.ShouldContain("lutyuv=y=minval+(val-minval)*0.55");
        graph.ShouldNotContain("brightness");
    }

    // --- Vertical flip, folded into the mirror stage ------------------------

    [Fact]
    public void Flip_IsAbsentByDefault()
    {
        Build(LookOptions.None).ShouldNotContain("vflip");
        Build(LookOptions.None).ShouldNotContain("hflip");
    }

    [Fact]
    public void Flip_EmitsVflipAlone()
    {
        var graph = Build(LookOptions.None with { FlipVertical = true });

        graph.ShouldContain("vflip");
        graph.ShouldNotContain("hflip");
    }

    [Fact]
    public void Flip_CombinesWithMirrorInOneStage()
    {
        var graph = Build(LookOptions.None with { Mirror = true, FlipVertical = true });

        graph.ShouldContain("hflip,vflip");
    }
}
