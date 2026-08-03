using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Primitives;
using ReviewClips.Core.Sources;
using Spectre.Console;

namespace ReviewClips.Cli.Commands;

/// <summary>Reports what the tool sees in a source file.</summary>
internal sealed class ProbeCommand
{
    private readonly Argument<List<string>> _inputs = new("input")
    {
        Description = "Files, directories or globs to inspect.",
        Arity = ArgumentArity.OneOrMore,
    };

    public Command Build(IServiceProvider services)
    {
        var command = new Command("probe", "Show technical details of one or more sources.");
        command.Add(_inputs);
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
        var resolver = services.GetRequiredService<SourceResolver>();

        var sources = resolver.Resolve(parse.GetValue(_inputs) ?? []);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]File[/]")
            .AddColumn("[grey]Duration[/]")
            .AddColumn("[grey]Resolution[/]")
            .AddColumn("[grey]FPS[/]")
            .AddColumn("[grey]Codec[/]")
            .AddColumn("[grey]Pixel fmt[/]")
            .AddColumn("[grey]Notes[/]");

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = await probe.ProbeAsync(source, cancellationToken);

            var notes = new List<string>();
            if (info.IsHdr)
            {
                notes.Add($"[yellow]HDR ({info.ColorTransfer})[/]");
            }

            if (info.IsAnamorphic)
            {
                notes.Add($"[yellow]anamorphic SAR {info.SampleAspectRatio}[/]");
            }

            if (info.IsHighBitDepth)
            {
                notes.Add("10-bit+");
            }

            notes.Add(info.HasAudio ? "audio" : "[grey]no audio[/]");

            table.AddRow(
                Markup.Escape(Path.GetFileName(info.Path)),
                DurationSpec.Format(info.Duration),
                $"{info.Width}x{info.Height}",
                $"{info.FrameRate:0.###}",
                Markup.Escape(info.VideoCodec),
                Markup.Escape(info.PixelFormat),
                string.Join(", ", notes));
        }

        console.Write(table);
        return ExitCodes.Success;
    }
}
