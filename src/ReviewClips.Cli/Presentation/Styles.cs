using Spectre.Console;

namespace ReviewClips.Cli.Presentation;

/// <summary>
/// The single place that decides how secondary console text looks.
/// <para>
/// Deliberately avoids Spectre's <c>[grey]</c>, which resolves to palette index 8 ("bright
/// black"). Most terminal themes render index 8 as a very dark grey, so on a dark background
/// it is close to invisible, and the tool has no way to know what the background is.
/// <c>dim</c> (SGR 2) instead reduces the intensity of whatever foreground the user has
/// configured, so it stays readable on light and dark themes alike, and terminals that do not
/// implement SGR 2 simply draw it at full intensity - unstyled, but never illegible.
/// </para>
/// <para>
/// Only genuinely secondary text should be muted. Anything the user is waiting to read -
/// progress counters, percentages, results - is left at the default foreground.
/// </para>
/// </summary>
internal static class Styles
{
    /// <summary>Markup tag used for de-emphasized text.</summary>
    public const string Muted = "dim";

    /// <summary>Style for table borders and rules.</summary>
    public static Style Border { get; } = new(decoration: Decoration.Dim);

    /// <summary>Wraps already-escaped markup so it renders de-emphasized.</summary>
    public static string Faint(string markup) => $"[{Muted}]{markup}[/]";

    /// <summary>Wraps a table column heading.</summary>
    public static string Header(string text) => Faint(text);
}
