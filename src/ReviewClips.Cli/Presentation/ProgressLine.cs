using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ReviewClips.Cli.Presentation;

/// <summary>
/// One self-updating progress bar: label, bar, percentage, time elapsed and an estimate of the
/// time left.
/// <para>
/// Spectre scopes a live display to a callback, but the pipeline announces its phases through
/// observer events, so the display runs in the background from construction until
/// <see cref="Dispose"/>. Disposing waits for the final frame, leaving the bar in the scrollback
/// as it stood, so whatever is written next lands below it. Only one may be open at a time.
/// </para>
/// <para>
/// On a console that is not interactive (redirected to a file or CI log, or under --verbose)
/// Spectre draws nothing live and prints an occasional plain "label: NN%" line instead.
/// </para>
/// </summary>
internal sealed class ProgressLine : IDisposable
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ProgressTask _task;
    private readonly Task _display;

    public ProgressLine(IAnsiConsole console, string description)
    {
        ArgumentNullException.ThrowIfNull(console);

        var ready = new TaskCompletionSource<ProgressTask>(TaskCreationOptions.RunContinuationsAsynchronously);

        _display = console.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .ExcludeVerticalPadding()
            .UseRenderHook((renderable, _) => new Padder(renderable, new Padding(2, 0, 0, 0)))
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn { Width = 30, RemainingStyle = Styles.ProgressTrack },
                new PercentageColumn(),
                new TimingColumn())
            .StartAsync(async context =>
            {
                ready.SetResult(context.AddTask(description, autoStart: true, maxValue: 1d));
                await _closed.Task;
            });

        // The task is added before the display's first await, so this has normally settled
        // already. Waiting on both surfaces a display that failed to start instead of hanging.
        Task.WhenAny(ready.Task, _display).GetAwaiter().GetResult();
        if (!ready.Task.IsCompleted)
        {
            _display.GetAwaiter().GetResult();
        }

        _task = ready.Task.Result;
    }

    /// <summary>Moves the bar to <paramref name="fraction"/> of the way, 0 to 1.</summary>
    public void Report(double fraction) => _task.Value = Math.Clamp(fraction, 0d, 1d);

    /// <summary>Moves the bar and relabels it.</summary>
    /// <param name="description">Spectre markup, already escaped.</param>
    public void Report(double fraction, string description)
    {
        _task.Description = description;
        Report(fraction);
    }

    /// <summary>Freezes the bar where it stands and returns once its final frame is drawn.</summary>
    public void Dispose()
    {
        _closed.TrySetResult();

        try
        {
            _display.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Presentation only. Often runs while a pipeline failure is unwinding, and must not
            // replace that failure with one of its own.
        }
    }

    /// <summary>
    /// "1:02 elapsed, ~4:51 left" while running, the bare elapsed time once finished.
    /// <para>
    /// Spectre's own remaining-time column estimates from a short window of recent updates, and
    /// shows nothing when updates are further apart than that. The parsers report in whole
    /// percents, which on a long stitch can be minutes apart, so the estimate here extrapolates
    /// the average rate since the start instead.
    /// </para>
    /// </summary>
    private sealed class TimingColumn : ProgressColumn
    {
        private const double MinimumFractionForEstimate = 0.01d;
        private static readonly TimeSpan MinimumElapsedForEstimate = TimeSpan.FromSeconds(2);

        protected override bool NoWrap => true;

        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            var elapsed = task.ElapsedTime ?? TimeSpan.Zero;

            if (task.IsFinished)
            {
                return new Text(Format(elapsed));
            }

            var fraction = task.MaxValue > 0 ? task.Value / task.MaxValue : 0d;

            if (fraction < MinimumFractionForEstimate || elapsed < MinimumElapsedForEstimate)
            {
                return new Text($"{Format(elapsed)} elapsed");
            }

            var remaining = elapsed * ((1d - fraction) / fraction);
            return new Text($"{Format(elapsed)} elapsed, ~{Format(remaining)} left");
        }

        private static string Format(TimeSpan time) =>
            time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }
}
