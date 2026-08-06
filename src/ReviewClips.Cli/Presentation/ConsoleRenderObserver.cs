using ReviewClips.Core.Pipeline;
using Spectre.Console;

namespace ReviewClips.Cli.Presentation;

/// <summary>
/// Renders pipeline progress with Spectre. Line-based rather than a live-updating display: the
/// pipeline runs several FFmpeg processes concurrently, and a live table competing with their
/// output garbles a redirected or non-interactive terminal, such as a file or a CI log.
/// </summary>
internal sealed class ConsoleRenderObserver : IRenderObserver
{
    private readonly IAnsiConsole _console;
    private readonly bool _quiet;

    // Extraction runs several FFmpeg processes concurrently, so completion callbacks arrive
    // from thread-pool threads. Without this the counters interleave and garble the line.
    private readonly object _gate = new();
    private int _lastPercent = -1;

    public ConsoleRenderObserver(IAnsiConsole console, bool quiet = false)
    {
        _console = console;
        _quiet = quiet;
    }

    public void OnPhaseStarted(RenderPhase phase, string detail)
    {
        if (_quiet)
        {
            return;
        }

        _lastPercent = -1;
        var label = phase switch
        {
            RenderPhase.Probing => "Probing",
            RenderPhase.Analyzing => "Analysing",
            RenderPhase.Selecting => "Selecting",
            RenderPhase.Extracting => "Extracting",
            RenderPhase.Stitching => "Stitching",
            RenderPhase.Finalising => "Finalising",
            _ => phase.ToString(),
        };

        _console.MarkupLine($"[bold]> {label}[/] {Styles.Faint(Escape(detail))}");
    }

    public void OnProbed(string path, int completed, int total)
    {
    }

    public void OnAnalysisStarted(string path, bool fromCache)
    {
        if (_quiet)
        {
            return;
        }

        var name = Escape(Path.GetFileName(path));

        if (fromCache)
        {
            _console.MarkupLine($"  [green]cached[/] {name}");
        }
        else
        {
            _console.MarkupLine(
                $"  [yellow]scanning[/] {name} {Styles.Faint("(slow once, cached afterwards)")}");
        }
    }

    public void OnAnalysisProgress(string path, double fraction)
    {
        if (_quiet)
        {
            return;
        }

        // Report only at 10% steps: this can fire thousands of times on a feature film.
        var percent = (int)(fraction * 100);
        var bucket = percent / 10 * 10;

        if (bucket <= _lastPercent || bucket == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (bucket <= _lastPercent)
            {
                return;
            }

            _lastPercent = bucket;

            // Left at the default foreground: this is the only feedback during a scan that can
            // run for minutes, so it must be readable whatever the terminal theme.
            _console.Write($"{bucket}% ");

            if (bucket >= 100)
            {
                _console.WriteLine();
            }
        }
    }

    public void OnSegmentsSelected(int count, int requested)
    {
        if (_quiet)
        {
            return;
        }

        var colour = count >= requested ? "green" : "yellow";
        _console.MarkupLine($"  [{colour}]{count}[/] of {requested} segments placed");
    }

    public void OnSegmentCompleted(int completed, int total)
    {
        if (_quiet)
        {
            return;
        }

        lock (_gate)
        {
            // Overwrite a single line rather than scrolling one line per segment. The counter
            // stays at the default foreground so it is legible on any terminal theme.
            _console.Write($"\r  {completed}/{total} encoded");

            if (completed == total)
            {
                _console.WriteLine();
            }
        }
    }

    public void OnStitchProgress(double fraction)
    {
    }

    public void OnWarning(string message) =>
        _console.MarkupLine($"  [yellow]![/] {Escape(message)}");

    private static string Escape(string value) => Markup.Escape(value);
}
