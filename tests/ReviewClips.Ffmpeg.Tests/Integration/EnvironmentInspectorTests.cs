using Microsoft.Extensions.Logging.Abstractions;
using ReviewClips.Ffmpeg.Analysis;
using ReviewClips.Ffmpeg.Diagnostics;

namespace ReviewClips.Ffmpeg.Tests.Integration;

/// <summary>
/// Runs the doctor's inspection against the real FFmpeg install.
/// <para>
/// The point of these is that the report has to agree with reality. A doctor that cheerfully
/// reports a working environment while <c>generate</c> fails is worse than no doctor at all.
/// </para>
/// </summary>
[Collection(FfmpegTestGroup.Name)]
public class EnvironmentInspectorTests
{
    private readonly FfmpegFixture _fixture;

    public EnvironmentInspectorTests(FfmpegFixture fixture) => _fixture = fixture;

    private EnvironmentInspector Inspector(string? cacheDirectory = null) =>
        new(
            _fixture.Runner,
            _fixture.EncoderProbe,
            new JsonAnalysisCache(
                cacheDirectory ?? _fixture.PathFor("doctor_cache"),
                NullLogger<JsonAnalysisCache>.Instance));

    [Fact]
    public async Task ReportsBothBinariesWithAVersion()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var report = await Inspector().InspectAsync(TestContext.Current.CancellationToken);

        report.Ffmpeg.Available.ShouldBeTrue();
        report.Ffmpeg.Version.ShouldNotBeNullOrWhiteSpace();
        report.Ffprobe.Available.ShouldBeTrue();
        report.Ffprobe.Version.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ReportsLibx264AsUsable()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var report = await Inspector().InspectAsync(TestContext.Current.CancellationToken);

        // The whole suite already encodes with libx264, so anything other than "usable" here
        // means the probe is reporting on something the renderer does not actually do.
        var x264 = report.Encoders.Single(e => e.Name == "libx264");
        x264.Usable.ShouldBeTrue();
        x264.Required.ShouldBeTrue();
    }

    [Fact]
    public async Task ReportsEveryFilterTheRenderGraphsUse()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var report = await Inspector().InspectAsync(TestContext.Current.CancellationToken);

        // FilterGraphExecutionTests runs these graphs for real against the same binary, so a
        // filter reported missing here would have to have failed there first.
        report.Filters.Missing.ShouldBeEmpty();
        report.Filters.Present.ShouldContain("lutyuv");
        report.Filters.Present.ShouldContain("zscale");
    }

    [Fact]
    public async Task ConsidersAWorkingInstallUsable()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var report = await Inspector().InspectAsync(TestContext.Current.CancellationToken);

        report.IsUsable.ShouldBeTrue();
    }

    [Fact]
    public void ReportsAnAbsentCacheAsEmptyRatherThanFailing()
    {
        var status = Inspector(_fixture.PathFor("never_created")).InspectCache();

        status.Exists.ShouldBeFalse();
        status.Entries.ShouldBe(0);
        status.SizeBytes.ShouldBe(0);
    }

    [Fact]
    public async Task CountsAndSizesCacheEntries()
    {
        var directory = _fixture.PathFor("populated_cache");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "a.json"),
            new string('x', 100),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "b.json"),
            new string('x', 50),
            TestContext.Current.CancellationToken);

        var status = Inspector(directory).InspectCache();

        status.Exists.ShouldBeTrue();
        status.Entries.ShouldBe(2);
        status.SizeBytes.ShouldBe(150);
    }

    [Fact]
    public async Task ClearCacheRemovesTheDirectoryAndReportsWhatWent()
    {
        var directory = _fixture.PathFor("cache_to_clear");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "entry.json"),
            "{}",
            TestContext.Current.CancellationToken);

        var inspector = Inspector(directory);
        var cleared = inspector.ClearCache();

        cleared.Exists.ShouldBeTrue();
        cleared.Entries.ShouldBe(1);
        Directory.Exists(directory).ShouldBeFalse();

        // Clearing twice must not throw; an already-empty cache is a normal state.
        inspector.ClearCache().Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task ReportsAMissingBinaryWithoutThrowing()
    {
        var inspector = new EnvironmentInspector(
            new Process.FfmpegRunner(
                new Process.FfmpegToolset
                {
                    FfmpegPath = "rcc-no-such-ffmpeg",
                    FfprobePath = "rcc-no-such-ffprobe",
                },
                NullLogger<Process.FfmpegRunner>.Instance),
            _fixture.EncoderProbe,
            new JsonAnalysisCache(_fixture.PathFor("absent"), NullLogger<JsonAnalysisCache>.Instance));

        var report = await inspector.InspectAsync(TestContext.Current.CancellationToken);

        report.Ffmpeg.Available.ShouldBeFalse();
        report.Ffmpeg.Error.ShouldNotBeNullOrWhiteSpace();
        report.IsUsable.ShouldBeFalse();

        // With no binary the encoder rows must be reported as unusable rather than probed, and
        // the filter list as entirely missing rather than as satisfied.
        report.Encoders.ShouldAllBe(e => !e.Usable);
        report.Filters.Missing.ShouldNotBeEmpty();
    }
}
