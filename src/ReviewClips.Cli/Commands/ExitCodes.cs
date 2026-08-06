namespace ReviewClips.Cli.Commands;

internal static class ExitCodes
{
    public const int Success = 0;

    /// <summary>Bad arguments or an unusable configuration.</summary>
    public const int UsageError = 2;

    /// <summary>Planning succeeded but nothing usable could be produced.</summary>
    public const int NothingToRender = 3;

    /// <summary>FFmpeg or ffprobe failed, or could not be found.</summary>
    public const int ToolFailure = 4;

    /// <summary>Interrupted. Matches the shell convention of 128 + SIGINT.</summary>
    public const int Canceled = 130;

    public const int UnexpectedError = 70;
}
