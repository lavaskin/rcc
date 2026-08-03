using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Primitives;
using Spectre.Console;

namespace ReviewClips.Cli.Presentation;

/// <summary>Renders a plan, its warnings, and the resulting FFmpeg commands.</summary>
internal static class PlanPrinter
{
    public static void PrintSummary(IAnsiConsole console, RenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(plan);

        var request = plan.Request;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]Setting[/]")
            .AddColumn("[grey]Value[/]");

        table.AddRow("Sources", $"{plan.Sources.Count} file(s)");
        table.AddRow("Output", Markup.Escape(request.OutputPath));
        table.AddRow(
            "Target",
            $"{request.TargetDuration.TotalSeconds:0.#}s -> {plan.EffectiveDuration.TotalSeconds:0.#}s after transitions");
        var distinct = plan.DistinctClipCount;
        var slots = plan.Segments.Count;

        // Report the actual average clip length rather than the configured splice length:
        // cue-driven renders share the runtime between cues, so the two can differ a lot.
        var averageClip = plan.Segments.Count == 0
            ? 0d
            : plan.Segments.Average(seg => seg.Duration.TotalSeconds);

        table.AddRow(
            "Clips",
            distinct == slots
                ? $"{slots} x ~{averageClip:0.#}s"
                : $"{distinct} distinct x ~{averageClip:0.#}s, "
                  + $"repeated to fill {slots} slots ({plan.RepeatFactor:0.#}x)");

        table.AddRow(
            "Source used",
            $"{plan.DistinctSourceDuration.TotalSeconds:0.#}s of footage"
            + (distinct == slots ? string.Empty : $" (vs {plan.TotalDuration.TotalSeconds:0.#}s of screen time)"));
        table.AddRow("Strategy", $"{request.Selection.Strategy} (seed {plan.Seed})");
        table.AddRow("Format", $"{request.Format.Width}x{request.Format.Height} @ {request.Format.FrameRate:0.##}fps, fit={request.Format.Fit}");
        table.AddRow("Look", DescribeLook(request.Look));
        table.AddRow("Transition", DescribeTransition(request.Transition));
        table.AddRow(
            "Encoder",
            $"{plan.Encoder.VideoEncoder} ({(plan.Encoder.IsHardware ? "hardware" : "software")}), q={request.Encoder.Quality}");
        table.AddRow("Audio", request.Mute ? "muted" : "kept");

        console.Write(table);

        foreach (var warning in plan.Warnings)
        {
            console.MarkupLine($"[yellow]warning:[/] {Markup.Escape(warning)}");
        }
    }

    public static void PrintSegments(IAnsiConsole console, RenderPlan plan, int limit = 40)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(plan);

        var table = new Table()
            .Border(TableBorder.Minimal)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]#[/]")
            .AddColumn("[grey]Source[/]")
            .AddColumn("[grey]Start[/]")
            .AddColumn("[grey]Length[/]")
            .AddColumn("[grey]Score[/]")
            .AddColumn("[grey]Why[/]");

        var shown = Math.Min(plan.Segments.Count, limit);
        for (var i = 0; i < shown; i++)
        {
            var segment = plan.Segments[i];
            table.AddRow(
                (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Markup.Escape(Truncate(Path.GetFileName(segment.SourcePath), 28)),
                DurationSpec.Format(segment.Start),
                $"{segment.Duration.TotalSeconds:0.00}s",
                $"{segment.Score:0.00}",
                Markup.Escape(segment.Reason ?? string.Empty));
        }

        console.Write(table);

        if (plan.Segments.Count > shown)
        {
            console.MarkupLine($"[grey]... and {plan.Segments.Count - shown} more[/]");
        }
    }

    public static void PrintCommands(IAnsiConsole console, IReadOnlyList<string> commands)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(commands);

        console.MarkupLine("[bold]FFmpeg commands[/]");

        // Show the first extraction command in full (they differ only by timestamp) plus the
        // stitch command, which is the interesting one.
        if (commands.Count > 2)
        {
            console.MarkupLine($"[grey]# segment 1 of {commands.Count - 1}[/]");
            console.WriteLine(commands[0]);
            console.MarkupLine($"[grey]# ... {commands.Count - 2} further segment commands, identical but for -ss[/]");
            console.MarkupLine("[grey]# stitch[/]");
            console.WriteLine(commands[^1]);
        }
        else
        {
            foreach (var command in commands)
            {
                console.WriteLine(command);
            }
        }
    }

    private static string DescribeLook(LookOptions look)
    {
        var parts = new List<string>();

        if (look.Darken > 0.001)
        {
            parts.Add($"darken {look.Darken:0.##}");
        }

        if (Math.Abs(look.Saturation - 1) > 0.001)
        {
            parts.Add($"saturation {look.Saturation:0.##}");
        }

        if (Math.Abs(look.Contrast - 1) > 0.001)
        {
            parts.Add($"contrast {look.Contrast:0.##}");
        }

        if (look.Blur > 0.01)
        {
            parts.Add($"blur {look.Blur:0.##}");
        }

        if (look.Grain > 0)
        {
            parts.Add($"grain {look.Grain}");
        }

        if (look.Vignette)
        {
            parts.Add("vignette");
        }

        if (look.Mirror)
        {
            parts.Add("mirror");
        }

        if (look.HasZoom)
        {
            parts.Add($"zoom {look.ZoomEnd:0.###}");
        }

        if (look.HasSpeedChange)
        {
            parts.Add($"speed {look.Speed:0.##}x");
        }

        if (look.LutPath is not null)
        {
            parts.Add("lut");
        }

        if (look.OverlayPath is not null)
        {
            parts.Add("overlay");
        }

        return parts.Count == 0 ? "untouched" : Markup.Escape(string.Join(", ", parts));
    }

    private static string DescribeTransition(TransitionOptions transition)
    {
        if (!transition.IsEnabled)
        {
            var fades = transition.FadeIn > TimeSpan.Zero || transition.FadeOut > TimeSpan.Zero;
            return fades
                ? "hard cuts, with top-level fades"
                : "hard cuts (stream copy, no re-encode)";
        }

        return $"{transition.Kind} {transition.Duration.TotalSeconds:0.##}s";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "\u2026";
}
