using System.CommandLine;
using System.Runtime.InteropServices;
using ReviewClips.Cli;
using ReviewClips.Cli.Cli;
using ReviewClips.Cli.Commands;
using ReviewClips.Cli.Presentation;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Sources;
using ReviewClips.Ffmpeg.Process;
using Spectre.Console;

// Verbosity has to be known before the container is built, since it configures logging.
var verbose = args.Contains("--verbose", StringComparer.Ordinal) || args.Contains("-v", StringComparer.Ordinal);

using var services = CompositionRoot.Build(verbose);

var root = new RootCommand(
    "Generate background footage for video essays and podcast clips by splicing short "
    + "segments out of one or more sources.")
{
    new GenerateCommand().Build(services),
    new BatchCommand().Build(services),
    new ScanCommand().Build(services),
    new ProbeCommand().Build(services),
    new DoctorCommand().Build(services),
    ProfilesCommand.Build(services),
};

using var cancellation = new CancellationTokenSource();

// Interruption must unwind the pipeline so child FFmpeg processes are killed and the partial
// output and temp files are cleaned up. A second signal gives up and lets the runtime terminate.
//
// PosixSignalRegistration is used rather than Console.CancelKeyPress because the latter only
// covers SIGINT. It never sees SIGTERM, which is what `kill`, systemd, Docker and CI runners
// actually send, so a CancelKeyPress-only program leaks orphaned encoders when shut down by
// anything other than an interactive Ctrl+C.
//
// The handlers deliberately do almost nothing. They run on the runtime's signal thread, and
// CancellationTokenSource.Cancel() invokes every registered callback synchronously on the
// calling thread; doing that here would run the pipeline's cancellation callbacks on the signal
// thread, contending with locks the worker threads hold. Dispatching to the thread pool keeps
// the signal thread free.
var interrupted = 0;

void HandleSignal(PosixSignalContext context)
{
    if (Interlocked.Exchange(ref interrupted, 1) != 0)
    {
        // Second signal: fall through to the default disposition and die.
        return;
    }

    context.Cancel = true;
    ThreadPool.UnsafeQueueUserWorkItem(
        static state => ((CancellationTokenSource)state!).Cancel(),
        cancellation);
}

using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, HandleSignal);
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, HandleSignal);

var configuration = new InvocationConfiguration
{
    // Exceptions are translated to exit codes below, so the built-in handler is not wanted.
    EnableDefaultExceptionHandler = false,
};

try
{
    return await root.Parse(args).InvokeAsync(configuration, cancellation.Token);
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("[yellow]cancelled.[/]");
    return ExitCodes.Cancelled;
}
catch (CliUsageException ex)
{
    AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
    return ExitCodes.UsageError;
}
catch (SourceResolutionException ex)
{
    AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
    return ExitCodes.UsageError;
}
catch (SourceUsageLimitException ex)
{
    // A policy refusal, not "nothing usable was found": planning succeeded and the settings
    // were declined, which is the same class of problem as a bad argument.
    AnsiConsole.MarkupLine($"[red]refused:[/] {Markup.Escape(ex.Message)}");
    AnsiConsole.MarkupLine(Styles.Faint("drop --strict-source-limit to make this a warning instead"));
    return ExitCodes.UsageError;
}
catch (RenderPlanningException ex)
{
    AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
    return ExitCodes.NothingToRender;
}
catch (FfmpegNotFoundException ex)
{
    AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
    return ExitCodes.ToolFailure;
}
catch (FfmpegExecutionException ex)
{
    AnsiConsole.MarkupLine($"[red]ffmpeg failed:[/] {Markup.Escape(ex.Message)}");
    return ExitCodes.ToolFailure;
}
catch (FileNotFoundException ex)
{
    AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
    return ExitCodes.UsageError;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]unexpected error:[/] {Markup.Escape(ex.Message)}");

    if (verbose)
    {
        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);
    }
    else
    {
        AnsiConsole.MarkupLine(Styles.Faint("run again with --verbose for a stack trace"));
    }

    return ExitCodes.UnexpectedError;
}
