using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ReviewClips.Cli.Presentation;
using ReviewClips.Cli.Profiles;
using Spectre.Console;

namespace ReviewClips.Cli.Commands;

internal sealed class ProfilesCommand
{
    public static Command Build(IServiceProvider services)
    {
        var command = new Command("profiles", "List the available presets.");

        command.SetAction(parse =>
        {
            var console = services.GetRequiredService<IAnsiConsole>();
            var library = services.GetRequiredService<ProfileLibrary>();

            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderStyle(Styles.Border)
                .AddColumn(Styles.Header("Profile"))
                .AddColumn(Styles.Header("Description"))
                .AddColumn(Styles.Header("Sets"));

            foreach (var profile in library.All)
            {
                table.AddRow(
                    $"[bold]{Markup.Escape(profile.Name)}[/]",
                    Markup.Escape(profile.Description),
                    Markup.Escape(Describe(profile)));
            }

            console.Write(table);
            console.MarkupLine(
                $"{Styles.Faint("Use with:")} rcc generate -i movie.mkv -d 90s --profile shorts");
            return ExitCodes.Success;
        });

        return command;
    }

    private static string Describe(RenderProfile profile)
    {
        var parts = new List<string>();

        if (profile.Width.HasValue && profile.Height.HasValue)
        {
            parts.Add($"{profile.Width}x{profile.Height}");
        }

        if (profile.Fit.HasValue)
        {
            parts.Add($"fit={profile.Fit}");
        }

        if (profile.Darken.HasValue)
        {
            parts.Add($"darken={profile.Darken:0.##}");
        }

        if (profile.Saturation.HasValue)
        {
            parts.Add($"sat={profile.Saturation:0.##}");
        }

        if (profile.Blur.HasValue && profile.Blur > 0)
        {
            parts.Add($"blur={profile.Blur:0.##}");
        }

        if (profile.Grain is > 0)
        {
            parts.Add($"grain={profile.Grain}");
        }

        if (profile.Vignette == true)
        {
            parts.Add("vignette");
        }

        if (profile.Zoom.HasValue)
        {
            parts.Add($"zoom={profile.Zoom:0.##}");
        }

        if (profile.Transition.HasValue)
        {
            parts.Add($"transition={profile.Transition}");
        }

        if (profile.SpliceSeconds.HasValue)
        {
            parts.Add($"splice={profile.SpliceSeconds:0.#}s");
        }

        return string.Join(", ", parts);
    }
}
