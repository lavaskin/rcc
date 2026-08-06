using Microsoft.Extensions.Logging.Abstractions;
using ReviewClips.Ffmpeg.Analysis;
using ReviewClips.Ffmpeg.Diagnostics;

namespace ReviewClips.Ffmpeg.Tests.Integration;

/// <summary>
/// Runs the doctor's inspection against the real FFmpeg install. The report has to agree with
/// reality, or it says the environment works while <c>generate</c> fails.
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

        // Only the required tier is asserted unconditionally: any build that can run this suite
        // at all has these.
        report.Filters.Missing.ShouldBeEmpty();
        report.Filters.Present.ShouldContain("scale");
        report.Filters.Present.ShouldContain("lutyuv");
    }

    [Fact]
    public async Task ReportsAnOptionalFilterAgainstTheFeatureItGates()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var report = await Inspector().InspectAsync(TestContext.Current.CancellationToken);

        // Whichever way this build went, the report has to place zscale in exactly one tier —
        // and never in the fatal one, because a build without libzimg still renders SDR.
        report.Filters.Missing.ShouldNotContain("zscale");

        if (_fixture.HasFilter("zscale"))
        {
            report.Filters.Present.ShouldContain("zscale");
            report.Filters.MissingOptional.ShouldNotContain(f => f.Filter == "zscale");
        }
        else
        {
            var gap = report.Filters.MissingOptional.SingleOrDefault(f => f.Filter == "zscale");

            gap.ShouldNotBeNull();
            gap.Feature.ShouldContain("--tone-map");
        }
    }

    [Fact]
    public async Task ReportsWhetherCaptionsCanActuallyBeDrawn()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var report = await Inspector().InspectAsync(TestContext.Current.CancellationToken);

        // The point of the check: drawtext being compiled in does not mean a font resolves.
        report.Text.Usable.ShouldBe(_fixture.FontAvailable);

        if (!report.Text.Usable)
        {
            report.Text.Error.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task ConsidersAWorkingInstallUsable()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var report = await Inspector().InspectAsync(TestContext.Current.CancellationToken);

        // Unconditional on purpose: any FFmpeg complete enough to have produced this fixture's
        // media is one rcc can render with, whatever optional pieces it was built without.
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
    public async Task ClearCacheRemovesTheEntriesAndReportsWhatWent()
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
        File.Exists(Path.Combine(directory, "entry.json")).ShouldBeFalse();

        // The directory itself stays: the next run needs it, and creating it again is pure
        // churn. Only the entries were promised.
        Directory.Exists(directory).ShouldBeTrue();

        // Clearing twice must not throw; an already-empty cache is a normal state.
        inspector.ClearCache().Entries.ShouldBe(0);
    }

    [Fact]
    public async Task ClearCacheLeavesFilesItDoesNotOwn()
    {
        // The directory comes from configuration, is used verbatim and nothing asks for
        // confirmation, so --clear-cache must not recursively delete what the user pointed at.
        var directory = _fixture.PathFor("cache_with_neighbors");
        Directory.CreateDirectory(directory);

        var entry = Path.Combine(directory, "film.abc123.json");
        var stranger = Path.Combine(directory, "important-notes.txt");
        var subdirectory = Path.Combine(directory, "holiday-photos");

        await File.WriteAllTextAsync(entry, "{}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(stranger, "do not delete", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(subdirectory);
        await File.WriteAllTextAsync(
            Path.Combine(subdirectory, "beach.jpg"),
            "jpeg",
            TestContext.Current.CancellationToken);

        var cleared = Inspector(directory).ClearCache();

        cleared.Entries.ShouldBe(1);
        File.Exists(entry).ShouldBeFalse();

        File.Exists(stranger).ShouldBeTrue();
        Directory.Exists(subdirectory).ShouldBeTrue();
        File.Exists(Path.Combine(subdirectory, "beach.jpg")).ShouldBeTrue();
    }

    [Fact]
    public async Task InspectAndClearAgreeOnWhatCountsAsAnEntry()
    {
        // "cleared 3 entries" has to mean the 3 that were just reported, so both sides read
        // the same definition.
        var directory = _fixture.PathFor("cache_agreement");
        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "a.json"), "{}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "b.json"), "{}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "stray.txt"), "x", TestContext.Current.CancellationToken);

        var inspector = Inspector(directory);
        var before = inspector.InspectCache();

        before.Entries.ShouldBe(2);
        inspector.ClearCache().Entries.ShouldBe(before.Entries);
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

    [Fact]
    public async Task ReportsABinaryThatLaunchesButFailsAsUnavailable()
    {
        // A build missing a shared library, or one for the wrong architecture, execs fine and
        // then exits non-zero. Doctor must not print a green row above a render that cannot start.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Needs a POSIX shell script.");

        var stub = _fixture.PathFor("failing_ffmpeg");
        await File.WriteAllTextAsync(
            stub,
            "#!/bin/sh\necho 'error while loading shared libraries: libzimg.so.2' >&2\nexit 127\n",
            TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                stub,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var inspector = new EnvironmentInspector(
            new Process.FfmpegRunner(
                new Process.FfmpegToolset { FfmpegPath = stub, FfprobePath = stub },
                NullLogger<Process.FfmpegRunner>.Instance),
            _fixture.EncoderProbe,
            new JsonAnalysisCache(_fixture.PathFor("broken"), NullLogger<JsonAnalysisCache>.Instance));

        var report = await inspector.InspectAsync(TestContext.Current.CancellationToken);

        report.Ffmpeg.Available.ShouldBeFalse();
        report.Ffmpeg.Version.ShouldBeNull();
        report.IsUsable.ShouldBeFalse();

        // The exit code alone says nothing actionable, so the reason the binary gave has to
        // travel with it.
        var error = report.Ffmpeg.Error;
        error.ShouldNotBeNull();
        error.ShouldContain("127");
        error.ShouldContain("libzimg.so.2");
    }
}
