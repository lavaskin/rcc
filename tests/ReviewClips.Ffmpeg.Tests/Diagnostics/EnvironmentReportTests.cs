using ReviewClips.Ffmpeg.Diagnostics;

namespace ReviewClips.Ffmpeg.Tests.Diagnostics;

/// <summary>
/// Parsers for <c>ffmpeg -filters</c> and <c>-version</c> output, tested against the real
/// shape of that output rather than against a tidied-up version of it.
/// </summary>
public class FilterListParserTests
{
    /// <summary>
    /// Verbatim FFmpeg 8.1 output, legend and all. The legend rows matter: they have the same
    /// token layout as a filter row and are the reason a naive parser invents filters called
    /// "=" or "Timeline".
    /// </summary>
    private const string RealOutput = """
        Filters:
          T.. = Timeline support
          .S. = Slice threading
          A = Audio input/output
          V = Video input/output
          N = Dynamic number and/or type of input/output
          | = Source or sink filter
          ------
         TS gblur             V->V       Apply Gaussian Blur filter.
         TS lutyuv            V->V       Compute and apply a lookup table to the YUV input video.
         .. scdet             V->V       Detect video scene change
         TS unsharp           V->V       Sharpen or blur the input video.
         .. unsharp_opencl    V->V       Apply unsharp mask to input video
         .S zscale            V->V       Apply resizing, colorspace and bit depth conversion.
         ... concat           N->N       Concatenate audio and video streams.
         ..C xfade            VV->V      Cross fade one video with another video.
         .S. acrossfade       AA->A      Cross fade two input audio streams.
        """;

    [Fact]
    public void ParsesEveryFilterName()
    {
        var names = EnvironmentInspector.ParseFilterNames(RealOutput);

        names.ShouldContain("gblur");
        names.ShouldContain("lutyuv");
        names.ShouldContain("scdet");
        names.ShouldContain("unsharp");
        names.ShouldContain("zscale");
        names.ShouldContain("concat");
        names.ShouldContain("xfade");
        names.ShouldContain("acrossfade");
    }

    [Fact]
    public void SkipsTheLegendAboveTheList()
    {
        var names = EnvironmentInspector.ParseFilterNames(RealOutput);

        names.ShouldNotContain("=");
        names.ShouldNotContain("Timeline");
        names.ShouldNotContain("Audio");
        names.ShouldNotContain("------");
    }

    [Fact]
    public void FindsOnlyTheExactName()
    {
        var names = EnvironmentInspector.ParseFilterNames(RealOutput);

        // A substring search would let unsharp_opencl satisfy a requirement for unsharp, which
        // is a different filter with different arguments.
        names.ShouldContain("unsharp_opencl");
        names.Count.ShouldBe(9);
    }

    [Fact]
    public void HandlesMultipleInputAndOutputArrows()
    {
        var names = EnvironmentInspector.ParseFilterNames(" ..C xfade            VV->V      Cross fade.");

        names.ShouldBe(["xfade"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Filters:")]
    public void ReturnsNothingForUnusableOutput(string output) =>
        EnvironmentInspector.ParseFilterNames(output).ShouldBeEmpty();
}

public class VersionParserTests
{
    [Theory]
    [InlineData("ffmpeg version n8.1.2 Copyright (c) 2000-2026 the FFmpeg developers", "n8.1.2")]
    [InlineData("ffprobe version n8.1.2 Copyright (c) 2007-2026 the FFmpeg developers", "n8.1.2")]
    [InlineData("ffmpeg version 4.4.2-0ubuntu0.22.04.1 Copyright (c) 2000-2021", "4.4.2-0ubuntu0.22.04.1")]
    [InlineData("ffmpeg version 7.1 Copyright (c) 2000-2024\nbuilt with gcc 14", "7.1")]
    public void ExtractsTheVersionToken(string output, string expected) =>
        EnvironmentInspector.ParseVersion(output).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("something else entirely")]
    [InlineData("ffmpeg version")]
    public void ReturnsNullRatherThanGuessing(string output) =>
        EnvironmentInspector.ParseVersion(output).ShouldBeNull();

    [Fact]
    public void ReadsOnlyTheFirstLine() =>
        EnvironmentInspector.ParseVersion("ffmpeg version 7.1 Copyright\nconfiguration: --version 9.9")
            .ShouldBe("7.1");
}

/// <summary>
/// What <c>rcc doctor</c> concludes from a given set of findings.
/// <para>
/// Built from synthetic records rather than a real inspection, because the interesting cases
/// are the ones a working machine cannot produce: an FFmpeg without libzimg, or with no font
/// installed. Both are common in containers.
/// </para>
/// </summary>
public class EnvironmentVerdictTests
{
    private static ToolStatus Working(string name) => new()
    {
        Name = name,
        Path = $"/usr/bin/{name}",
        Available = true,
        Version = "n8.1.2",
    };

    private static EnvironmentReport Report(
        bool ffmpeg = true,
        bool x264 = true,
        IReadOnlyList<string>? missingRequired = null,
        IReadOnlyList<MissingFeature>? missingOptional = null,
        bool textUsable = true) => new()
        {
            Ffmpeg = ffmpeg
                ? Working("ffmpeg")
                : new ToolStatus
                {
                    Name = "ffmpeg",
                    Path = "ffmpeg",
                    Available = false,
                    Error = "not found",
                },
            Ffprobe = Working("ffprobe"),
            Encoders =
            [
                new EncoderStatus
                {
                    Name = "libx264",
                    Usable = x264,
                    SelectedBy = "--encoder x264",
                    Required = true,
                },
                new EncoderStatus
                {
                    Name = "h264_nvenc",
                    Usable = false,
                    SelectedBy = "--encoder nvenc",
                    Required = false,
                },
            ],
            Filters = new FilterStatus
            {
                Present = ["scale"],
                Missing = missingRequired ?? [],
                MissingOptional = missingOptional ?? [],
            },
            Text = new TextRenderStatus
            {
                FilterPresent = true,
                Usable = textUsable,
                Error = textUsable ? null : "Cannot find a valid font",
            },
            Cache = new CacheStatus
            {
                Directory = "/tmp/cache",
                Exists = false,
                Entries = 0,
                SizeBytes = 0,
            },
        };

    [Fact]
    public void AHealthyMachineIsUsableWithNoLimitations()
    {
        var report = Report();

        report.IsUsable.ShouldBeTrue();
        report.HasLimitations.ShouldBeFalse();
        report.Filters.AllPresent.ShouldBeTrue();
    }

    [Fact]
    public void AMissingOptionalFilterIsALimitationNotAFailure()
    {
        // The case that matters: libzimg is absent on Alpine and on plenty of static builds.
        // Every SDR render still works, so the verdict must not be "broken".
        var report = Report(missingOptional:
        [
            new MissingFeature { Filter = "zscale", Feature = "HDR tone mapping (--tone-map)" },
        ]);

        report.IsUsable.ShouldBeTrue();
        report.HasLimitations.ShouldBeTrue();
        report.Filters.RequiredPresent.ShouldBeTrue();
        report.Filters.AllPresent.ShouldBeFalse();
    }

    [Fact]
    public void AMissingRequiredFilterIsAFailure()
    {
        var report = Report(missingRequired: ["scale"]);

        report.IsUsable.ShouldBeFalse();
        report.Filters.RequiredPresent.ShouldBeFalse();
    }

    [Fact]
    public void AnUnusableFontIsALimitationNotAFailure()
    {
        // drawtext compiled in, no font installed. Only --attribution stops working.
        var report = Report(textUsable: false);

        report.IsUsable.ShouldBeTrue();
        report.HasLimitations.ShouldBeTrue();
    }

    [Fact]
    public void AMissingRequiredEncoderIsAFailure()
    {
        var report = Report(x264: false);

        report.IsUsable.ShouldBeFalse();
    }

    /// <summary>
    /// h264_nvenc is unusable in every report built here; a machine without a GPU is not a
    /// broken machine.
    /// </summary>
    [Fact]
    public void AnUnusableOptionalEncoderIsNotAFailure() =>
        Report().IsUsable.ShouldBeTrue();

    [Fact]
    public void AMissingBinaryIsAFailure() =>
        Report(ffmpeg: false).IsUsable.ShouldBeFalse();

    [Fact]
    public void LimitationsAreOnlyReportedForAnOtherwiseUsableMachine()
    {
        // Otherwise doctor would print "usable, with limitations" over the top of a fatal row.
        var report = Report(ffmpeg: false, missingOptional:
        [
            new MissingFeature { Filter = "zscale", Feature = "HDR tone mapping (--tone-map)" },
        ]);

        report.IsUsable.ShouldBeFalse();
        report.HasLimitations.ShouldBeFalse();
    }
}
