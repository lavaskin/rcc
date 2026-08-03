using Microsoft.Extensions.Logging.Abstractions;
using ReviewClips.Ffmpeg.Process;

namespace ReviewClips.Ffmpeg.Tests.Integration;

/// <summary>
/// Shared FFmpeg-backed fixture.
/// <para>
/// All test media is synthesised with <c>lavfi</c> sources, so the suite needs no sample files
/// and — importantly for a tool like this — no copyrighted material to run anywhere, including CI.
/// </para>
/// </summary>
public sealed class FfmpegFixture : IAsyncLifetime
{
    private readonly List<string> _tempPaths = [];

    public FfmpegRunner Runner { get; } = new(
        new FfmpegToolset(),
        NullLogger<FfmpegRunner>.Instance);

    public bool Available { get; private set; }

    public string Directory { get; private set; } = string.Empty;

    /// <summary>A 20s clip with three visually distinct shots and one 3s black stretch.</summary>
    public string MultiShotClip { get; private set; } = string.Empty;

    /// <summary>A 1280x720 SDR clip.</summary>
    public string SimpleClip { get; private set; } = string.Empty;

    /// <summary>A 720x480 clip with SAR 32:27, i.e. a 16:9 anamorphic DVD.</summary>
    public string AnamorphicClip { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        Directory = Path.Combine(Path.GetTempPath(), "rcc_tests_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(Directory);

        try
        {
            await new FfmpegToolset().EnsureAvailableAsync(CancellationToken.None);
            Available = true;
        }
        catch (FfmpegNotFoundException)
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
    /// Decodes one frame to 8-bit greyscale and reports luma statistics.
    /// <para>
    /// Needed because the interesting failures in the look pipeline are tonal, not structural:
    /// a graph can be perfectly valid and still produce a black rectangle.
    /// </para>
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
