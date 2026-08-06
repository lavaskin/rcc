using System.Globalization;

namespace ReviewClips.Ffmpeg.Process;

/// <summary>
/// Interprets the key/value stream produced by <c>-progress pipe:1 -nostats</c>.
/// FFmpeg emits one <c>key=value</c> per line, terminated by <c>progress=continue</c> or
/// <c>progress=end</c>.
/// </summary>
public sealed class FfmpegProgressParser
{
    private readonly TimeSpan _expectedDuration;
    private readonly IProgress<double>? _progress;
    private double _lastReported = -1d;

    public FfmpegProgressParser(TimeSpan expectedDuration, IProgress<double>? progress)
    {
        _expectedDuration = expectedDuration;
        _progress = progress;
    }

    public TimeSpan CurrentTime { get; private set; }

    public bool Completed { get; private set; }

    /// <summary>
    /// Frames written to the output so far, as last reported by FFmpeg, or null if it never said.
    /// <para>
    /// Worth capturing because FFmpeg exits 0 after writing an empty file. A filter graph that
    /// yields nothing — a mistimed trim, an overlay whose second input runs out immediately —
    /// produces "Output file is empty, nothing was encoded" on stderr, a valid 262-byte MP4 with
    /// no streams, and a successful exit code. Without this the failure surfaces several stages
    /// later as an unrelated complaint from whatever tried to read the file.
    /// </para>
    /// </summary>
    public long? FramesWritten { get; private set; }

    public void Feed(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        var separator = line.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return;
        }

        var key = line.AsSpan(0, separator).Trim();
        var value = line.AsSpan(separator + 1).Trim();

        if (key.Equals("out_time_ms", StringComparison.Ordinal)
            || key.Equals("out_time_us", StringComparison.Ordinal))
        {
            // Despite the name, FFmpeg reports out_time_ms in microseconds.
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros)
                && micros >= 0)
            {
                CurrentTime = TimeSpan.FromMicroseconds(micros);
                Report();
            }
        }
        else if (key.Equals("frame", StringComparison.Ordinal))
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames)
                && frames >= 0)
            {
                FramesWritten = frames;
            }
        }
        else if (key.Equals("progress", StringComparison.Ordinal))
        {
            if (value.Equals("end", StringComparison.Ordinal))
            {
                Completed = true;
                _progress?.Report(1d);
            }
        }
    }

    private void Report()
    {
        if (_progress is null || _expectedDuration <= TimeSpan.Zero)
        {
            return;
        }

        var fraction = Math.Clamp(CurrentTime.TotalSeconds / _expectedDuration.TotalSeconds, 0d, 1d);

        // Throttle to whole percents; a 2-hour analysis pass otherwise floods the console.
        if (fraction - _lastReported < 0.01d && fraction < 1d)
        {
            return;
        }

        _lastReported = fraction;
        _progress.Report(fraction);
    }
}
