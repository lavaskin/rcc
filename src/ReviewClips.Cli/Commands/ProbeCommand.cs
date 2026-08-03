using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ReviewClips.Cli.Presentation;
using ReviewClips.Core.Media;
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

    private readonly Option<bool> _chapters = new("--chapters")
    {
        Description = "List chapter markers, flagging the ones selection would skip by default.",
    };

    public Command Build(IServiceProvider services)
    {
        var command = new Command("probe", "Show technical details of one or more sources.");
        command.Add(_inputs);
        command.Add(_chapters);
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
        var listChapters = parse.GetValue(_chapters);
        var probed = new List<MediaInfo>(sources.Count);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Styles.Border)
            .AddColumn(Styles.Header("File"))
            .AddColumn(Styles.Header("Duration"))
            .AddColumn(Styles.Header("Resolution"))
            .AddColumn(Styles.Header("FPS"))
            .AddColumn(Styles.Header("Codec"))
            .AddColumn(Styles.Header("Pixel fmt"))
            .AddColumn(Styles.Header("Notes"));

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = await probe.ProbeAsync(source, cancellationToken);
            probed.Add(info);

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

            notes.Add(info.HasAudio ? "audio" : Styles.Faint("no audio"));

            if (info.HasChapters)
            {
                notes.Add(info.HasNamedChapters
                    ? $"{info.Chapters.Count} chapters"
                    : Styles.Faint($"{info.Chapters.Count} unnamed chapters"));
            }

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

        if (listChapters)
        {
            foreach (var info in probed)
            {
                PrintChapters(console, info);
            }
        }
        else if (probed.Exists(i => i.HasNamedChapters))
        {
            console.MarkupLine(Styles.Faint(
                "Named chapters found. Use 'probe --chapters' to see which ones selection skips."));
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// Lists a source's chapters and marks the ones the default title patterns exclude, so a
    /// user can see what will be skipped before rendering and write their own patterns.
    /// </summary>
    private static void PrintChapters(IAnsiConsole console, MediaInfo info)
    {
        console.MarkupLine($"[bold]{Markup.Escape(Path.GetFileName(info.Path))}[/] chapters");

        if (!info.HasChapters)
        {
            console.MarkupLine(Styles.Faint("  none"));
            return;
        }

        var skipped = ChapterFilter
            .Matching(info.Chapters, ChapterFilter.IntroOutroPatterns)
            .Select(c => c.Index)
            .ToHashSet();

        var table = new Table()
            .Border(TableBorder.Minimal)
            .BorderStyle(Styles.Border)
            .AddColumn(Styles.Header("#"))
            .AddColumn(Styles.Header("Start"))
            .AddColumn(Styles.Header("Length"))
            .AddColumn(Styles.Header("Title"))
            .AddColumn(Styles.Header("Default"));

        foreach (var chapter in info.Chapters)
        {
            var isSkipped = skipped.Contains(chapter.Index);

            table.AddRow(
                (chapter.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                DurationSpec.Format(chapter.Start),
                $"{chapter.Duration.TotalSeconds:0.#}s",
                chapter.Title is null
                    ? Styles.Faint("(untitled)")
                    : Markup.Escape(chapter.Title),
                isSkipped ? "[yellow]skipped[/]" : Styles.Faint("eligible"));
        }

        console.Write(table);

        if (skipped.Count == 0 && !info.HasNamedChapters)
        {
            console.MarkupLine(Styles.Faint(
                "  Titles are auto-generated, so chapter filtering cannot help here; "
                + "rely on --skip-head/--skip-tail or --exclude."));
        }
    }
}
