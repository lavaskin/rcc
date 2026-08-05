using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ReviewClips.Cli.Presentation;
using ReviewClips.Ffmpeg.Diagnostics;
using Spectre.Console;

namespace ReviewClips.Cli.Commands;

/// <summary>
/// Reports what this machine can actually do: binaries, encoders, filters, cache.
/// <para>
/// The encoder rows come from the same probe-encode the renderer uses to pick an encoder, not
/// from <c>ffmpeg -encoders</c>. That distinction is the point of the command: NVENC is compiled
/// into most FFmpeg builds and still fails at runtime without a suitable driver, so a list of
/// compiled-in encoders answers a question nobody asked.
/// </para>
/// </summary>
internal sealed class DoctorCommand
{
    private readonly Option<bool> _cacheOnly = new("--cache")
    {
        Description = "Report only the analysis cache, skipping the encoder probes.",
    };

    private readonly Option<bool> _clearCache = new("--clear-cache")
    {
        Description = "Delete the cached analysis results.",
    };

    public Command Build(IServiceProvider services)
    {
        var command = new Command("doctor", "Check FFmpeg, encoders, filters and the analysis cache.");
        command.Add(_cacheOnly);
        command.Add(_clearCache);

        command.SetAction((parse, ct) => ExecuteAsync(services, parse, ct));
        return command;
    }

    private async Task<int> ExecuteAsync(
        IServiceProvider services,
        ParseResult parse,
        CancellationToken cancellationToken)
    {
        var console = services.GetRequiredService<IAnsiConsole>();
        var inspector = services.GetRequiredService<EnvironmentInspector>();

        if (parse.GetValue(_clearCache))
        {
            var cleared = inspector.ClearCache();

            console.MarkupLine(cleared.Exists
                ? $"[green]cleared[/] {cleared.Entries} cache entr{(cleared.Entries == 1 ? "y" : "ies")} "
                  + Styles.Faint($"({GenerateCommand.FormatSize(cleared.SizeBytes)}) from {Markup.Escape(cleared.Directory)}")
                : $"{Styles.Faint("cache was already empty:")} {Markup.Escape(cleared.Directory)}");
        }

        if (parse.GetValue(_cacheOnly))
        {
            PrintCache(console, inspector.InspectCache());
            return ExitCodes.Success;
        }

        var report = await inspector.InspectAsync(cancellationToken);

        PrintTools(console, report);

        if (!report.Ffmpeg.Available || !report.Ffprobe.Available)
        {
            // Nothing below this point can be measured, and pretending otherwise would print a
            // wall of red rows whose real cause is the one line above.
            console.MarkupLine(
                "[red]FFmpeg is not usable, so nothing else could be checked.[/] "
                + Styles.Faint("Install FFmpeg and put it on PATH, or set Ffmpeg:FfmpegPath in appsettings.json."));
            return ExitCodes.ToolFailure;
        }

        PrintEncoders(console, report);
        PrintFilters(console, report);
        PrintCache(console, report.Cache);

        if (report.IsUsable)
        {
            console.MarkupLine("[green]Environment looks good.[/]");
            return ExitCodes.Success;
        }

        console.MarkupLine(
            "[yellow]Environment is incomplete[/] "
            + Styles.Faint("see the rows marked no above."));
        return ExitCodes.ToolFailure;
    }

    private static void PrintTools(IAnsiConsole console, EnvironmentReport report)
    {
        foreach (var tool in new[] { report.Ffmpeg, report.Ffprobe })
        {
            if (tool.Available)
            {
                console.MarkupLine(
                    $"[green]ok[/] {tool.Name,-8} "
                    + Styles.Faint(Markup.Escape(tool.Version ?? "version not reported")));
            }
            else
            {
                console.MarkupLine($"[red]no[/] {tool.Name,-8} {Markup.Escape(tool.Error ?? "not found")}");
            }
        }
    }

    private static void PrintEncoders(IAnsiConsole console, EnvironmentReport report)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Styles.Border)
            .Title("[bold]Encoders[/]")
            .AddColumn(Styles.Header("Encoder"))
            .AddColumn(Styles.Header("Usable"))
            .AddColumn(Styles.Header("Select with"));

        foreach (var encoder in report.Encoders)
        {
            var verdict = encoder.Usable
                ? "[green]yes[/]"
                : encoder.Required ? "[red]no[/]" : Styles.Faint("no");

            table.AddRow(
                Markup.Escape(encoder.Name) + (encoder.Required ? Styles.Faint(" (required)") : string.Empty),
                verdict,
                Styles.Faint(Markup.Escape(encoder.SelectedBy)));
        }

        console.Write(table);
        console.MarkupLine(Styles.Faint("each row was measured by encoding two frames, not by reading -encoders"));
    }

    private static void PrintFilters(IAnsiConsole console, EnvironmentReport report)
    {
        var filters = report.Filters;

        if (filters.AllPresent)
        {
            console.MarkupLine($"[green]ok[/] all {filters.Present.Count} required filters present");
            return;
        }

        console.MarkupLine(
            $"[red]no[/] {filters.Missing.Count} required filter(s) missing: "
            + Markup.Escape(string.Join(", ", filters.Missing)));
    }

    private static void PrintCache(IAnsiConsole console, CacheStatus cache)
    {
        console.MarkupLine(cache.Exists
            ? $"{Styles.Faint("analysis cache:")} {cache.Entries} entr{(cache.Entries == 1 ? "y" : "ies")}, "
              + $"{GenerateCommand.FormatSize(cache.SizeBytes)} "
              + Styles.Faint($"in {Markup.Escape(cache.Directory)}")
            : $"{Styles.Faint("analysis cache:")} empty "
              + Styles.Faint($"({Markup.Escape(cache.Directory)})"));
    }
}
