using Microsoft.Extensions.Logging.Abstractions;
using ReviewClips.Ffmpeg.Encoding;
using ReviewClips.Ffmpeg.Process;

namespace ReviewClips.Ffmpeg.Tests.Integration;

/// <summary>
/// Shared FFmpeg-backed fixture. All test media is synthesised with <c>lavfi</c> sources, so the
/// suite needs no sample files and no copyrighted material to run anywhere, including CI.
/// </summary>
public sealed class FfmpegFixture : IAsyncLifetime
{
    private readonly List<string> _tempPaths = [];

    public FfmpegFixture() =>
        EncoderProbe = new FfmpegEncoderProbe(Runner, NullLogger<FfmpegEncoderProbe>.Instance);

    public FfmpegRunner Runner { get; } = new(
        new FfmpegToolset(),
        NullLogger<FfmpegRunner>.Instance);

    /// <summary>
    /// Shared across the whole collection so the probe encodes happen once, not once per test:
    /// the probe caches its verdict per encoder for the lifetime of the instance.
    /// </summary>
    public FfmpegEncoderProbe EncoderProbe { get; }

    public bool Available { get; private set; }

    public string Directory { get; private set; } = string.Empty;

    /// <summary>A 20s clip with three visually distinct shots and one 3s black stretch.</summary>
    public string MultiShotClip { get; private set; } = string.Empty;

    /// <summary>A 1280x720 SDR clip.</summary>
    public string SimpleClip { get; private set; } = string.Empty;

    /// <summary>A 720x480 clip with SAR 32:27, i.e. a 16:9 anamorphic DVD.</summary>
    public string AnamorphicClip { get; private set; } = string.Empty;

    /// <summary>A 20s MKV carrying four named chapters, as a disc rip marks titles and credits.</summary>
    public string ChapteredClip { get; private set; } = string.Empty;

    /// <summary>A 10s MKV whose chapters are named only by number, as most rips are.</summary>
    public string UnnamedChapterClip { get; private set; } = string.Empty;

    /// <summary>
    /// A 6s audio-only WAV. Audio-only on purpose: it is the case <see cref="MediaInfo"/>
    /// cannot describe, and therefore the one <c>ProbeDurationAsync</c> exists for.
    /// </summary>
    public string AudioTrack { get; private set; } = string.Empty;

    /// <summary>
    /// A 6s MKV carrying three audio streams and no video, so the stitchers' stream mapping can
    /// be shown to take one stream rather than all of them.
    /// </summary>
    public string MultiTrackAudio { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        Directory = Path.Combine(Path.GetTempPath(), "rcc_tests_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(Directory);

        try
        {
            await new FfmpegToolset().EnsureAvailableAsync(CancellationToken.None);
            Available = true;
        }
        catch (FfmpegNotFoundException) when (!Required)
        {
            Available = false;
            return;
        }

        SimpleClip = await SynthesiseAsync(
            "simple.mp4",
            ["-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30", "-t", "10"],
            []);

        AnamorphicClip = await SynthesiseAsync(
            "anamorphic.mp4",
            ["-f", "lavfi", "-i", "testsrc2=s=720x480:r=30", "-t", "8"],
            ["-vf", "setsar=32/27"]);

        MultiShotClip = await BuildMultiShotAsync();

        ChapteredClip = await BuildChapteredAsync(
            "chaptered.mkv",
            SimpleClip,
            [
                ("Opening Titles", 0, 2_000),
                ("Act One", 2_000, 5_000),
                ("Act Two", 5_000, 8_000),
                ("End Credits", 8_000, 10_000),
            ]);

        UnnamedChapterClip = await BuildChapteredAsync(
            "unnamed_chapters.mkv",
            SimpleClip,
            [
                ("Chapter 01", 0, 5_000),
                ("Chapter 02", 5_000, 10_000),
            ]);

        AudioTrack = await SynthesiseAudioAsync("track.wav", 6);
        MultiTrackAudio = await SynthesiseMultiTrackAudioAsync("multitrack.mkv", 6, tracks: 3);

        var filters = await Runner.RunFfmpegAsync(
            ["-hide_banner", "-filters"],
            CancellationToken.None);

        _filters = new HashSet<string>(
            ReviewClips.Ffmpeg.Diagnostics.EnvironmentInspector.ParseFilterNames(filters.StandardOutput),
            StringComparer.Ordinal);

        // drawtext existing is not the same as drawtext working: with no fontfile it asks
        // fontconfig for a default family, which fails on an image that has libfreetype and no
        // fonts. Established once here so the caption tests can skip rather than fail.
        FontAvailable = _filters.Contains("drawtext") && await CanDrawTextAsync();

        if (Required && !FontAvailable)
        {
            throw new InvalidOperationException(
                "RCC_REQUIRE_FFMPEG is set, but drawtext cannot resolve a font, so the attribution "
                + "tests would skip. Install a font package (fonts-dejavu-core).");
        }
    }

    /// <summary>
    /// Whether a missing FFmpeg should fail the run rather than skip it.
    /// <para>
    /// Skipping suits a contributor whose FFmpeg lacks an optional component. It is wrong in CI,
    /// where FFmpeg is installed as an explicit step: a skip there means that step stopped working
    /// and much of the suite is no longer running, while still reporting green.
    /// </para>
    /// </summary>
    private static bool Required =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RCC_REQUIRE_FFMPEG"));

    private HashSet<string> _filters = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether this FFmpeg was built with a given filter. Tests for optional features gate on
    /// this; asserting instead would make a build without libzimg indistinguishable from a
    /// genuine regression in the graph builder.
    /// </summary>
    public bool HasFilter(string name) => _filters.Contains(name);

    /// <summary>Whether <c>drawtext</c> can resolve a font, not merely whether it exists.</summary>
    public bool FontAvailable { get; private set; }

    private async Task<bool> CanDrawTextAsync()
    {
        var result = await Runner.RunFfmpegAsync(
            [
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=black:s=64x64:d=1",
                "-vf", "drawtext=text=rcc:fontcolor=white:fontsize=16",
                "-frames:v", "1", "-f", "null", "-",
            ],
            CancellationToken.None);

        return result.Success;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }

        return ValueTask.CompletedTask;
    }

    public string PathFor(string name)
    {
        var path = Path.Combine(Directory, name);
        _tempPaths.Add(path);
        return path;
    }

    /// <summary>Runs FFmpeg and returns stderr on failure, so a test can assert on the reason.</summary>
    public async Task<FfmpegRunResult> RunAsync(IEnumerable<string> arguments) =>
        await Runner.RunFfmpegAsync(arguments.ToList(), CancellationToken.None);

    public async Task<double> DurationOfAsync(string path)
    {
        var result = await Runner.RunFfprobeAsync(
            [
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "csv=p=0",
                path,
            ],
            CancellationToken.None);

        return double.TryParse(
            result.StandardOutput.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var seconds)
            ? seconds
            : 0d;
    }

    /// <summary>
    /// Counts video frames by decoding them, not by trusting the container's header. A segment
    /// one or two frames over sits below the tolerance any duration assertion can use, yet the
    /// bias is systematic and compounds across a render.
    /// </summary>
    public async Task<int> FrameCountOfAsync(string path)
    {
        var result = await Runner.RunFfprobeAsync(
            [
                "-v", "error",
                "-count_frames",
                "-select_streams", "v:0",
                "-show_entries", "stream=nb_read_frames",
                "-of", "csv=p=0",
                path,
            ],
            CancellationToken.None);

        return int.TryParse(result.StandardOutput.Trim(), out var frames) ? frames : -1;
    }

    /// <summary>
    /// Counts the chapter markers a file carries. Rendered output must report zero: chapters
    /// belong to the source's structure, and clips assembled from scattered moments have none.
    /// </summary>
    public async Task<int> ChapterCountOfAsync(string path)
    {
        var result = await Runner.RunFfprobeAsync(
            [
                "-v", "error",
                "-show_chapters",
                "-of", "csv=p=0",
                path,
            ],
            CancellationToken.None);

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }

    /// <summary>
    /// Decodes one frame to 8-bit greyscale and reports luma statistics. Failures in the look
    /// pipeline are tonal rather than structural: a valid graph can still produce a black frame.
    /// </summary>
    public async Task<(double Mean, int Median, int Max)> LumaStatsAsync(string path, double atSeconds = 1)
    {
        var raw = PathFor($"luma_{Guid.NewGuid():N}.gray");

        var result = await Runner.RunFfmpegAsync(
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", atSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-i", path,
                "-frames:v", "1",
                "-f", "rawvideo", "-pix_fmt", "gray",
                raw,
            ],
            CancellationToken.None);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Could not sample luma: {result.StandardError}");
        }

        var bytes = await File.ReadAllBytesAsync(raw);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("Sampled frame was empty.");
        }

        var sorted = bytes.ToArray();
        Array.Sort(sorted);

        double sum = 0;
        foreach (var b in bytes)
        {
            sum += b;
        }

        return (sum / bytes.Length, sorted[sorted.Length / 2], sorted[^1]);
    }

    public async Task<(int Width, int Height)> DimensionsOfAsync(string path)
    {
        var result = await Runner.RunFfprobeAsync(
            [
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=width,height",
                "-of", "csv=p=0",
                path,
            ],
            CancellationToken.None);

        var parts = result.StandardOutput.Trim().Split(',');
        return parts.Length >= 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
            ? (w, h)
            : (0, 0);
    }

    private async Task<string> SynthesiseAsync(
        string name,
        IReadOnlyList<string> input,
        IReadOnlyList<string> extra)
    {
        var path = Path.Combine(Directory, name);

        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        arguments.AddRange(input);
        arguments.AddRange(extra);
        arguments.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path]);

        var result = await Runner.RunFfmpegAsync(arguments, CancellationToken.None);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Could not synthesise {name}: {result.StandardError}");
        }

        return path;
    }

    /// <summary>Synthesises an audio-only file with no video stream whatsoever.</summary>
    private async Task<string> SynthesiseAudioAsync(string name, double seconds)
    {
        var path = Path.Combine(Directory, name);

        var result = await Runner.RunFfmpegAsync(
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi",
                "-i", "sine=frequency=440:sample_rate=44100",
                "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                path,
            ],
            CancellationToken.None);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Could not synthesise {name}: {result.StandardError}");
        }

        return path;
    }

    /// <summary>
    /// The duration of the first audio stream, as distinct from the container's: tells a padded
    /// track from a truncated one, which the container length cannot.
    /// </summary>
    public async Task<double> AudioDurationOfAsync(string path)
    {
        var result = await Runner.RunFfprobeAsync(
            [
                "-v", "error",
                "-select_streams", "a:0",
                "-show_entries", "stream=duration",
                "-of", "csv=p=0",
                path,
            ],
            CancellationToken.None);

        return double.TryParse(
            result.StandardOutput.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var seconds)
            ? seconds
            : 0d;
    }

    /// <summary>
    /// Synthesises an audio-only file carrying several distinct audio streams. Each track gets its
    /// own frequency so they are genuinely separate; Matroska rather than WAV, which holds only one.
    /// </summary>
    private async Task<string> SynthesiseMultiTrackAudioAsync(string name, double seconds, int tracks)
    {
        var path = Path.Combine(Directory, name);
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };

        for (var i = 0; i < tracks; i++)
        {
            arguments.AddRange(
            [
                "-f", "lavfi",
                "-i", $"sine=frequency={220 * (i + 1)}:sample_rate=44100",
            ]);
        }

        for (var i = 0; i < tracks; i++)
        {
            arguments.AddRange(["-map", i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":a"]);
        }

        arguments.AddRange(
        [
            "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a", "libopus",
            path,
        ]);

        var result = await Runner.RunFfmpegAsync(arguments, CancellationToken.None);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Could not synthesise {name}: {result.StandardError}");
        }

        return path;
    }

    /// <summary>
    /// How many audio streams a file carries. A count rather than a bool: "has audio" cannot tell
    /// <c>-map N:a:0</c> from <c>-map N:a</c>, and the latter's output plays correctly while
    /// quietly carrying every commentary and language track the source had.
    /// </summary>
    public async Task<int> AudioStreamCountAsync(string path)
    {
        var result = await Runner.RunFfprobeAsync(
            [
                "-v", "error",
                "-select_streams", "a",
                "-show_entries", "stream=codec_type",
                "-of", "csv=p=0",
                path,
            ],
            CancellationToken.None);

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(line => line.Equals("audio", StringComparison.Ordinal));
    }

    /// <summary>
    /// Remuxes a clip with chapter markers attached via an ffmetadata sidecar, which is the only
    /// way to author chapters with FFmpeg. Timings are in milliseconds.
    /// </summary>
    private async Task<string> BuildChapteredAsync(
        string name,
        string sourcePath,
        IReadOnlyList<(string Title, int StartMs, int EndMs)> chapters)
    {
        var lines = new List<string> { ";FFMETADATA1" };

        foreach (var (title, startMs, endMs) in chapters)
        {
            lines.AddRange(
            [
                "[CHAPTER]",
                "TIMEBASE=1/1000",
                $"START={startMs}",
                $"END={endMs}",
                $"title={title}",
            ]);
        }

        var metadataPath = Path.Combine(Directory, name + ".ffmeta");
        await File.WriteAllLinesAsync(metadataPath, lines);

        var output = Path.Combine(Directory, name);
        var result = await Runner.RunFfmpegAsync(
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-i", sourcePath,
                "-i", metadataPath,
                "-map_metadata", "1",
                "-map_chapters", "1",
                "-c", "copy",
                output,
            ],
            CancellationToken.None);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Could not build {name}: {result.StandardError}");
        }

        return output;
    }

    /// <summary>
    /// Three distinct shots plus a black stretch, concatenated. Gives the analyser real cuts,
    /// a black region and a frozen region to find.
    /// </summary>
    private async Task<string> BuildMultiShotAsync()
    {
        var parts = new List<string>
        {
            await SynthesiseAsync("shot1.mp4", ["-f", "lavfi", "-i", "testsrc2=s=640x360:r=30", "-t", "7"], []),
            await SynthesiseAsync("shot2.mp4", ["-f", "lavfi", "-i", "mandelbrot=s=640x360:r=30", "-t", "7"], []),
            await SynthesiseAsync("black.mp4", ["-f", "lavfi", "-i", "color=black:s=640x360:r=30", "-t", "3"], []),
            await SynthesiseAsync("shot3.mp4", ["-f", "lavfi", "-i", "rgbtestsrc=s=640x360:r=30", "-t", "3"], []),
        };

        var listPath = Path.Combine(Directory, "concat.txt");
        await File.WriteAllLinesAsync(listPath, parts.Select(p => $"file '{p}'"));

        var output = Path.Combine(Directory, "multishot.mp4");
        var result = await Runner.RunFfmpegAsync(
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "concat", "-safe", "0", "-i", listPath,
                "-c", "copy", output,
            ],
            CancellationToken.None);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Could not build multi-shot clip: {result.StandardError}");
        }

        return output;
    }
}

[CollectionDefinition(Name)]
public sealed class FfmpegTestGroup : ICollectionFixture<FfmpegFixture>
{
    public const string Name = "ffmpeg";
}
