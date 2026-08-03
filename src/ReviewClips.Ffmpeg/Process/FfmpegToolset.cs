using System.Diagnostics;

namespace ReviewClips.Ffmpeg.Process;

/// <summary>Locations of the external binaries this tool drives.</summary>
public sealed record FfmpegToolset
{
    public string FfmpegPath { get; init; } = "ffmpeg";

    public string FfprobePath { get; init; } = "ffprobe";

    /// <summary>Verifies both binaries can actually be launched, with a clear message if not.</summary>
    public async Task EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        await EnsureOneAsync(FfmpegPath, cancellationToken);
        await EnsureOneAsync(FfprobePath, cancellationToken);
    }

    private static async Task EnsureOneAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };

            process.StartInfo.ArgumentList.Add("-version");
            process.Start();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FfmpegNotFoundException(
                $"Could not run '{executable}'. Install FFmpeg and ensure it is on PATH, "
                + "or set Ffmpeg:FfmpegPath / Ffmpeg:FfprobePath in appsettings.json.",
                ex);
        }
    }
}

public sealed class FfmpegNotFoundException : Exception
{
    public FfmpegNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FfmpegNotFoundException(string message) : base(message)
    {
    }

    public FfmpegNotFoundException()
    {
    }
}
