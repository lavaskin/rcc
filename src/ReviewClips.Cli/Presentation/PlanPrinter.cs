using ReviewClips.Core.Media;
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
            .BorderStyle(Styles.Border)
            .AddColumn(Styles.Header("Setting"))
            .AddColumn(Styles.Header("Value"));

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

        var usage = plan.SourceUsage;

        table.AddRow(
            "Source used",
            $"{usage.Used.TotalSeconds:0.#}s of footage"
            + (usage.Available > TimeSpan.Zero ? $", {usage.Percent:0.#}% of the source" : string.Empty)
            + (distinct == slots ? string.Empty : $" (vs {plan.TotalDuration.TotalSeconds:0.#}s of screen time)"));
        table.AddRow("Strategy", $"{request.Selection.Strategy} (seed {plan.Seed})");

        // Only worth a row when the sources actually carry markers.
        if (DescribeChapters(plan) is { } chapters)
        {
            table.AddRow("Chapters", chapters);
        }

        table.AddRow("Format", $"{request.Format.Width}x{request.Format.Height} @ {request.Format.FrameRate:0.##}fps, fit={request.Format.Fit}");
        table.AddRow("Look", DescribeLook(request.Look));
        table.AddRow("Transition", DescribeTransition(request.Transition));
        table.AddRow(
            "Encoder",
            $"{plan.Encoder.VideoEncoder} ({(plan.Encoder.IsHardware ? "hardware" : "software")}), q={request.Encoder.Quality}");
        table.AddRow("Audio", DescribeAudio(request.Audio));

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
            .BorderStyle(Styles.Border)
            .AddColumn(Styles.Header("#"))
            .AddColumn(Styles.Header("Source"))
            .AddColumn(Styles.Header("Start"))
            .AddColumn(Styles.Header("Length"))
            .AddColumn(Styles.Header("Score"))
            .AddColumn(Styles.Header("Why"));

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
            console.MarkupLine(Styles.Faint($"... and {plan.Segments.Count - shown} more"));
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
            console.MarkupLine(Styles.Faint($"# segment 1 of {commands.Count - 1}"));
            console.WriteLine(commands[0]);
            console.MarkupLine(Styles.Faint(
                $"# ... {commands.Count - 2} further segment commands, identical but for -ss"));
            console.MarkupLine(Styles.Faint("# stitch"));
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

    /// <summary>
    /// Summarizes what chapter filtering did, or null when no source has chapters. Reports the
    /// excluded titles by name: a silent exclusion of ten minutes of a film is exactly the kind
    /// of thing that should not be invisible.
    /// </summary>
    private static string? DescribeChapters(RenderPlan plan)
    {
        var total = plan.Sources.Sum(s => s.Info.Chapters.Count);
        if (total == 0)
        {
            return null;
        }

        var patterns = plan.Request.Selection.EffectiveChapterPatterns;

        var skipped = plan.Sources
            .SelectMany(s => ChapterFilter.Matching(s.Info.Chapters, patterns))
            .ToList();

        if (skipped.Count == 0)
        {
            return plan.Sources.Any(s => s.Info.HasNamedChapters)
                ? $"{total} marker(s), none matched as intro or credits"
                : $"{total} unnamed marker(s), nothing to match against";
        }

        var excluded = skipped.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Duration);

        var titles = skipped
            .Select(c => c.Title)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        var named = string.Join(", ", titles);
        if (skipped.Count > titles.Count)
        {
            named += ", ...";
        }

        return Markup.Escape(
            $"{total} marker(s); skipped {skipped.Count} "
            + $"({excluded.TotalSeconds:0.#}s): {named}");
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

        if (Math.Abs(look.Gamma - 1) > 0.001)
        {
            parts.Add($"gamma {look.Gamma:0.##}");
        }

        if (look.Grayscale)
        {
            parts.Add("grayscale");
        }

        if (look.Blur > 0.01)
        {
            parts.Add($"blur {look.Blur:0.##}");
        }

        if (look.HasSharpen)
        {
            parts.Add($"sharpen {look.Sharpen:0.##}");
        }

        if (look.HasPixelate)
        {
            parts.Add($"pixelate {look.Pixelate:0.##}");
        }

        if (look.Grain > 0)
        {
            parts.Add($"grain {look.Grain}");
        }

        if (look.Vignette)
        {
            parts.Add("vignette");
        }

        if (look.HasFadeEdges)
        {
            parts.Add($"fade edges {look.FadeEdges.TotalSeconds:0.##}s");
        }

        if (look.Mirror)
        {
            parts.Add("mirror");
        }

        if (look.FlipVertical)
        {
            parts.Add("flip");
        }

        if (look.HasAttribution)
        {
            parts.Add($"attribution ({look.AttributionPosition})");
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

    private static string DescribeAudio(AudioOptions audio)
    {
        if (audio.IsMuted)
        {
            return "muted";
        }

        if (!audio.HasExternalTrack)
        {
            return "source audio kept";
        }

        var parts = new List<string> { Path.GetFileName(audio.ExternalPath!) };

        if (audio.Offset > TimeSpan.Zero)
        {
            parts.Add($"from {DurationSpec.Format(audio.Offset)}");
        }

        if (audio.AltersVolume)
        {
            parts.Add($"at {audio.Volume:0.##}x");
        }

        if (audio.MatchDuration)
        {
            parts.Add("length matched");
        }

        return Markup.Escape(string.Join(", ", parts));
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
