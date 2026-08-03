using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ReviewClips.Cli.Presentation;
using ReviewClips.Core.Analysis;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Primitives;
using ReviewClips.Core.Sources;
using Spectre.Console;

namespace ReviewClips.Cli.Commands;

/// <summary>
/// Runs the analysis pass and populates the cache, without rendering.
/// <para>
/// Useful as a deliberate warm-up: scan a title once while you do something else, and every
/// later <c>generate</c> against it starts instantly.
/// </para>
/// </summary>
internal sealed class ScanCommand
{
    private readonly Argument<List<string>> _inputs = new("input")
    {
        Description = "Files, directories or globs to analyse.",
        Arity = ArgumentArity.OneOrMore,
    };

    private readonly Option<bool> _force = new("--force")
    {
        Description = "Re-analyse even when a cached result exists.",
    };

    private readonly Option<int?> _fps = new("--analysis-fps")
    {
        Description = "Frames per second to sample.",
        HelpName = "fps",
    };

    private readonly Option<double?> _threshold = new("--scene-threshold")
    {
        Description = "Scene-cut sensitivity, 0 to 100.",
        HelpName = "0-100",
    };

    private readonly Option<bool> _hwDecode = new("--hwdecode")
    {
        Description = "Use CUDA for decoding during the scan.",
    };

    public Command Build(IServiceProvider services)
    {
        var command = new Command("scan", "Analyse sources and populate the cache.");
        command.Add(_inputs);
        command.Add(_force);
        command.Add(_fps);
        command.Add(_threshold);
        command.Add(_hwDecode);

        command.SetAction((parse, ct) => ExecuteAsync(services, parse, ct));
        return command;
    }

    private async Task<int> ExecuteAsync(
        IServiceProvider services,
        ParseResult parse,
        CancellationToken cancellationToken)
    {
        var console = services.GetRequiredService<IAnsiConsole>();
        var probe = services.GetRequiredService<IMediaProbe>();
        var analyzer = services.GetRequiredService<IMediaAnalyzer>();
        var cache = services.GetRequiredService<IAnalysisCache>();
        var resolver = services.GetRequiredService<SourceResolver>();

        var settings = new AnalysisSettings
        {
            SampleFrameRate = parse.GetValue(_fps) ?? 4,
            SceneThreshold = parse.GetValue(_threshold) ?? 8d,
            UseHardwareDecode = parse.GetValue(_hwDecode),
        };

        var force = parse.GetValue(_force);
        var sources = resolver.Resolve(parse.GetValue(_inputs) ?? []);
        var observer = new ConsoleRenderObserver(console);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]File[/]")
            .AddColumn("[grey]Cuts[/]")
            .AddColumn("[grey]Avg shot[/]")
            .AddColumn("[grey]Black[/]")
            .AddColumn("[grey]Frozen[/]")
            .AddColumn("[grey]Median motion[/]")
            .AddColumn("[grey]Source[/]");

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = await probe.ProbeAsync(source, cancellationToken);
            var cached = force ? null : await cache.TryGetAsync(info, settings, cancellationToken);
            var fromCache = cached is not null;

            observer.OnAnalysisStarted(info.Path, fromCache);

            var analysis = cached;
            if (analysis is null)
            {
                analysis = await analyzer.AnalyzeAsync(
                    info,
                    settings,
                    new InlineProgress<double>(f => observer.OnAnalysisProgress(info.Path, f)),
                    cancellationToken);

                await cache.SaveAsync(analysis, cancellationToken);
            }

            var shots = analysis.Shots();
            var averageShot = shots.Count > 0
                ? TimeSpan.FromSeconds(shots.Average(s => s.Duration.TotalSeconds))
                : TimeSpan.Zero;

            table.AddRow(
                Markup.Escape(Path.GetFileName(info.Path)),
                analysis.SceneCuts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{averageShot.TotalSeconds:0.0}s",
                Total(analysis.BlackRanges),
                Total(analysis.FreezeRanges),
                $"{analysis.Motion.Median():0.###}",
                fromCache ? "[green]cache[/]" : "[yellow]scanned[/]");
        }

        console.Write(table);
        console.MarkupLine(
            $"[grey]cache dir:[/] {Markup.Escape(Presentation.CachePaths.Describe(services))}");

        return ExitCodes.Success;
    }

    private static string Total(IReadOnlyList<TimeRange> ranges)
    {
        if (ranges.Count == 0)
        {
            return "[grey]none[/]";
        }

        var total = ranges.Aggregate(TimeSpan.Zero, (a, r) => a + r.Duration);
        return $"{ranges.Count} ({total.TotalSeconds:0.#}s)";
    }
}
