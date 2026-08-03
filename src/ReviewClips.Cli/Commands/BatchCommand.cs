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

/// <summary>
/// Renders several different variants in one run.
/// <para>
/// This is the workflow-shaped command: one podcast episode usually needs a handful of
/// interchangeable background tracks. Because the analysis cache is warm after the first
/// variant, the rest are much faster than running <c>generate</c> repeatedly.
/// </para>
/// </summary>
internal sealed class BatchCommand
{
    private readonly GenerateOptions _options = new();

    private readonly Option<int> _count = new("--count", "-n")
    {
        Description = "How many variants to render.",
        HelpName = "count",
        DefaultValueFactory = _ => 3,
    };

    private readonly Option<string?> _outputDirectory = new("--out-dir")
    {
        Description = "Directory to write the variants into.",
        HelpName = "path",
    };

    public Command Build(IServiceProvider services)
    {
        var command = new Command("batch", "Render several background variants from the same sources.");
        _options.AddTo(command);
        command.Add(_count);
        command.Add(_outputDirectory);

        command.SetAction((parse, ct) => ExecuteAsync(services, parse, ct));
        return command;
    }

    private async Task<int> ExecuteAsync(
        IServiceProvider services,
        ParseResult parse,
        CancellationToken cancellationToken)
    {
        var console = services.GetRequiredService<IAnsiConsole>();
        var count = Math.Max(1, parse.GetValue(_count));
        var directory = parse.GetValue(_outputDirectory) ?? Directory.GetCurrentDirectory();

        var builder = new ClipRequestBuilder(
            _options,
            services.GetRequiredService<ProfileLibrary>(),
            services.GetRequiredService<SourceResolver>());

        var pipeline = services.GetRequiredService<RenderPipeline>();

        // Derive a distinct seed per variant from one base, so the whole batch stays
        // reproducible from a single --seed.
        var template = builder.Build(parse);
        var baseSeed = template.Selection.Seed ?? Random.Shared.Next();
        var stem = Path.GetFileNameWithoutExtension(template.OutputPath);

        Directory.CreateDirectory(directory);

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var output = Path.Combine(directory, $"{stem}-{i + 1:D2}.mp4");
            var seed = unchecked(baseSeed + (i * 7919));

            var request = template with
            {
                OutputPath = Path.GetFullPath(output),
                Selection = template.Selection with { Seed = seed },
            };

            console.MarkupLine($"[bold]variant {i + 1}/{count}[/] [grey]seed {seed}[/]");

            var observer = new ConsoleRenderObserver(console);
            var plan = await pipeline.PlanAsync(request, observer, cancellationToken);

            if (request.DryRun)
            {
                PlanPrinter.PrintSummary(console, plan);
                continue;
            }

            var result = await pipeline.ExecuteAsync(plan, observer, cancellationToken);

            if (request.ManifestPath is not null)
            {
                var manifestPath = Path.ChangeExtension(output, ".manifest.json");
                await RenderManifest.From(plan, result).WriteAsync(manifestPath, cancellationToken);
            }

            console.MarkupLine(
                $"  [green]done[/] {Markup.Escape(Path.GetFileName(result.OutputPath))} "
                + $"[grey]({GenerateCommand.FormatSize(result.OutputSizeBytes)}, {result.Elapsed.TotalSeconds:0.#}s)[/]");
        }

        return ExitCodes.Success;
    }
}
