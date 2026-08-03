using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ReviewClips.Ffmpeg.Process;

public sealed record FfmpegRunResult
{
    public required int ExitCode { get; init; }

    public required string StandardError { get; init; }

    public required string StandardOutput { get; init; }

    public bool Success => ExitCode == 0;
}

/// <summary>
/// Runs FFmpeg and ffprobe.
/// <para>
/// Arguments are always passed via <see cref="ProcessStartInfo.ArgumentList"/>, never as a
/// concatenated string. File paths from a media library routinely contain spaces, quotes and
/// brackets, and shell interpolation would be both a correctness and an injection problem.
/// </para>
/// </summary>
public sealed class FfmpegRunner
{
    /// <summary>Keeps only the tail of stderr; a failing FFmpeg run can emit megabytes of warnings.</summary>
    private const int MaxCapturedErrorLines = 200;

    private readonly FfmpegToolset _toolset;
    private readonly ILogger<FfmpegRunner> _logger;

    public FfmpegRunner(FfmpegToolset toolset, ILogger<FfmpegRunner> logger)
    {
        _toolset = toolset;
        _logger = logger;
    }

    public FfmpegToolset Toolset => _toolset;

    public Task<FfmpegRunResult> RunFfmpegAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        RunAsync(_toolset.FfmpegPath, arguments, null, null, cancellationToken);

    public Task<FfmpegRunResult> RunFfprobeAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        RunAsync(_toolset.FfprobePath, arguments, null, null, cancellationToken);

    /// <summary>
    /// Runs FFmpeg with <c>-progress pipe:1</c> parsing.
    /// </summary>
    /// <param name="arguments">Arguments, excluding the progress flags which are added here.</param>
    /// <param name="expectedDuration">Used to convert elapsed media time into a fraction.</param>
    public Task<FfmpegRunResult> RunFfmpegWithProgressAsync(
        IReadOnlyList<string> arguments,
        TimeSpan expectedDuration,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var parser = new FfmpegProgressParser(expectedDuration, progress);
        var withProgress = new List<string>(arguments.Count + 3) { "-nostats", "-progress", "pipe:1" };
        withProgress.AddRange(arguments);

        return RunAsync(_toolset.FfmpegPath, withProgress, parser.Feed, null, cancellationToken);
    }

    /// <summary>
    /// Core process driver.
    /// </summary>
    /// <param name="onStandardOutputLine">Called per stdout line. When null, stdout is buffered.</param>
    /// <param name="onStandardErrorLine">Called per stderr line, in addition to tail buffering.</param>
    public async Task<FfmpegRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string>? onStandardOutputLine,
        Action<string>? onStandardErrorLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _logger.LogDebug("Running {Executable} {Arguments}", executable, string.Join(' ', arguments));

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderrTail = new Queue<string>(MaxCapturedErrorLines);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            if (onStandardOutputLine is not null)
            {
                onStandardOutputLine(e.Data);
            }
            else
            {
                stdout.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            onStandardErrorLine?.Invoke(e.Data);

            lock (stderrTail)
            {
                if (stderrTail.Count >= MaxCapturedErrorLines)
                {
                    stderrTail.Dequeue();
                }

                stderrTail.Enqueue(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new FfmpegNotFoundException($"Failed to start '{executable}'.", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C must not leave a detached encoder pinning the GPU and writing a
            // half-finished file. Kill the whole tree and let cancellation propagate.
            KillQuietly(process);
            throw;
        }

        // Ensures the async output handlers have drained before the buffers are read.
        process.WaitForExit();

        string stderr;
        lock (stderrTail)
        {
            stderr = string.Join(Environment.NewLine, stderrTail);
        }

        return new FfmpegRunResult
        {
            ExitCode = process.ExitCode,
            StandardError = stderr,
            StandardOutput = stdout.ToString(),
        };
    }

    /// <summary>Runs FFmpeg and throws a descriptive exception on a non-zero exit.</summary>
    public async Task RunFfmpegCheckedAsync(
        IReadOnlyList<string> arguments,
        TimeSpan expectedDuration,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var result = await RunFfmpegWithProgressAsync(arguments, expectedDuration, progress, cancellationToken);
        if (!result.Success)
        {
            throw new FfmpegExecutionException(
                _toolset.FfmpegPath,
                arguments,
                result.ExitCode,
                result.StandardError);
        }
    }

    private void KillQuietly(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not terminate process {Id}", SafeId(process));
        }
    }

    private static string SafeId(System.Diagnostics.Process process)
    {
        try
        {
            return process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "<exited>";
        }
    }
}

public sealed class FfmpegExecutionException : Exception
{
    public FfmpegExecutionException(
        string executable,
        IReadOnlyList<string> arguments,
        int exitCode,
        string standardError)
        : base(BuildMessage(executable, arguments, exitCode, standardError))
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public FfmpegExecutionException(string message) : base(message)
    {
    }

    public FfmpegExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FfmpegExecutionException()
    {
    }

    public int ExitCode { get; }

    public string StandardError { get; } = string.Empty;

    private static string BuildMessage(
        string executable,
        IReadOnlyList<string> arguments,
        int exitCode,
        string standardError)
    {
        // Surface only the last few stderr lines: that is where FFmpeg puts the actual cause.
        var lines = standardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(6);

        return $"{Path.GetFileName(executable)} exited with code {exitCode}."
            + Environment.NewLine
            + "Command: " + string.Join(' ', arguments)
            + Environment.NewLine
            + string.Join(Environment.NewLine, lines);
    }
}
