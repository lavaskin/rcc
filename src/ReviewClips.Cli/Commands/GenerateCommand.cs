using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ReviewClips.Cli.Cli;
using ReviewClips.Cli.Presentation;
using ReviewClips.Cli.Profiles;
using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Sources;
using Spectre.Console;

namespace ReviewClips.Cli.Commands;

/// <summary>The main command: build one background render.</summary>
internal sealed class GenerateCommand
{
    private readonly GenerateOptions _options = new();

    public Command Build(IServiceProvider services)
    {
        var command = new Command("generate", "Generate a background clip from one or more sources.");
        _options.AddTo(command);

        command.SetAction((parse, ct) => ExecuteAsync(services, parse, ct));
        return command;
    }

    private async Task<int> ExecuteAsync(
        IServiceProvider services,
        ParseResult parse,
        CancellationToken cancellationToken)
    {
        var console = services.GetRequiredService<IAnsiConsole>();

        var builder = new ClipRequestBuilder(
            _options,
            services.GetRequiredService<ProfileLibrary>(),
            services.GetRequiredService<SourceResolver>());

        var request = builder.Build(parse);
        var pipeline = services.GetRequiredService<RenderPipeline>();
        var observer = new ConsoleRenderObserver(console);

        var plan = await pipeline.PlanAsync(request, observer, cancellationToken);

        PlanPrinter.PrintSummary(console, plan);

        if (request.DryRun)
        {
            console.WriteLine();
            PlanPrinter.PrintSegments(console, plan);
            console.WriteLine();
            PlanPrinter.PrintCommands(console, pipeline.DescribeCommands(plan));

            if (request.ManifestPath is { } manifestPath)
            {
                await RenderManifest.From(plan, null).WriteAsync(manifestPath, cancellationToken);
                console.MarkupLine($"[grey]manifest ->[/] {Markup.Escape(manifestPath)}");
            }

            console.MarkupLine("[yellow]dry run: nothing was rendered.[/]");
            return ExitCodes.Success;
        }

        var result = await pipeline.ExecuteAsync(plan, observer, cancellationToken);

        if (request.ManifestPath is { } path)
        {
            await RenderManifest.From(plan, result).WriteAsync(path, cancellationToken);
            console.MarkupLine($"[grey]manifest ->[/] {Markup.Escape(path)}");
        }

        console.MarkupLine(
            $"[green]done[/] {Markup.Escape(result.OutputPath)} "
            + $"[grey]({FormatSize(result.OutputSizeBytes)}, {result.Elapsed.TotalSeconds:0.#}s)[/]");

        return ExitCodes.Success;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B",
    };
}
