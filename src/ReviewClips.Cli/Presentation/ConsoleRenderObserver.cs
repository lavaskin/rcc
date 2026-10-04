using System.Globalization;
using ReviewClips.Core.Pipeline;
using Spectre.Console;

namespace ReviewClips.Cli.Presentation;

/// <summary>
/// Renders pipeline progress with Spectre: a line per phase and event, and a self-updating
/// <see cref="ProgressLine"/> under each long-running step - scanning, encoding clips, stitching.
/// <para>
/// At most one bar is live, and it is closed, frozen as it stood, before anything else is
/// printed, so the scrollback reads in order. Dispose closes any bar a failure or Ctrl+C left
/// open, and restores the cursor, so the error message does not land inside a live display.
/// </para>
/// </summary>
internal sealed class ConsoleRenderObserver : IRenderObserver, IDisposable
{
    private readonly IAnsiConsole _console;
    private readonly bool _quiet;

    // Encoders report from their own output-reader threads, concurrently during extraction, and
    // a phase change must not race a report into a bar that is being closed.
    private readonly object _gate = new();
    private ProgressLine? _bar;
    private bool _disposed;

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

        var label = phase switch
        {
            RenderPhase.Probing => "Probing",
            RenderPhase.Analyzing => "Analyzing",
            RenderPhase.Selecting => "Selecting",
            RenderPhase.Extracting => "Extracting",
            RenderPhase.Stitching => "Stitching",
            RenderPhase.Finalizing => "Finalizing",
            _ => phase.ToString(),
        };

        lock (_gate)
        {
            CloseBar();
            _console.MarkupLine($"[bold]> {label}[/] {Styles.Faint(Escape(detail))}");

            // Opened here rather than on the first report so the elapsed time is ticking from
            // the start: a stitch can sit for a while opening its inputs before FFmpeg reports.
            if (phase is RenderPhase.Extracting)
            {
                OpenBar("encoding");
            }
            else if (phase is RenderPhase.Stitching)
            {
                OpenBar("stitching");
            }
        }
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

        lock (_gate)
        {
            CloseBar();

            if (fromCache)
            {
                _console.MarkupLine($"  [green]cached[/] {name}");
                return;
            }

            _console.MarkupLine(
                $"  [yellow]scanning[/] {name} {Styles.Faint("(slow once, cached afterwards)")}");
            OpenBar("scanning");
        }
    }

    public void OnAnalysisProgress(string path, double fraction) => Advance(fraction, null, fraction >= 1d);

    public void OnSegmentsSelected(int count, int requested)
    {
        if (_quiet)
        {
            return;
        }

        var color = count >= requested ? "green" : "yellow";
        _console.MarkupLine($"  [{color}]{count}[/] of {requested} segments placed");
    }

    public void OnExtractionProgress(int completed, int total, double fraction)
    {
        // Padded to the width of the total so the bar does not shift right at 10/N. The padding
        // sits mid-label because Spectre trims leading whitespace from a column.
        var width = total.ToString(CultureInfo.InvariantCulture).Length;
        var count = completed.ToString(CultureInfo.InvariantCulture).PadLeft(width);

        Advance(fraction, $"encoded {count}/{total}", completed >= total);
    }

    public void OnStitchProgress(double fraction) => Advance(fraction, null, fraction >= 1d);

    public void OnWarning(string message)
    {
        lock (_gate)
        {
            CloseBar();
            _console.MarkupLine($"  [yellow]![/] {Escape(message)}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CloseBar();
            _disposed = true;
        }
    }

    private void Advance(double fraction, string? description, bool finished)
    {
        if (_quiet)
        {
            return;
        }

        lock (_gate)
        {
            // No bar: the step's bar was already closed by a warning or a failure. FFmpeg killed
            // on Ctrl+C can still flush a last report after that, and must not reopen one.
            if (_bar is null)
            {
                return;
            }

            if (description is null)
            {
                _bar.Report(fraction);
            }
            else
            {
                _bar.Report(fraction, description);
            }

            if (finished)
            {
                CloseBar();
            }
        }
    }

    private void OpenBar(string description)
    {
        if (!_disposed)
        {
            _bar = new ProgressLine(_console, description);
        }
    }

    private void CloseBar()
    {
        _bar?.Dispose();
        _bar = null;
    }

    private static string Escape(string value) => Markup.Escape(value);
}
