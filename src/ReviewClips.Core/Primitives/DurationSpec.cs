using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ReviewClips.Core.Primitives;

/// <summary>
/// Parses human-friendly duration text: <c>90</c>, <c>90s</c>, <c>2m</c>, <c>1m30s</c>,
/// <c>1.5m</c>, <c>1h2m3s</c>, <c>00:01:30</c>, <c>1:30</c>, <c>00:01:30.500</c>.
/// </summary>
public static partial class DurationSpec
{
    [GeneratedRegex(
        @"^(?:(?<h>\d+(?:\.\d+)?)h)?(?:(?<m>\d+(?:\.\d+)?)m)?(?:(?<s>\d+(?:\.\d+)?)s)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitPattern { get; }

    public static TimeSpan Parse(string text)
    {
        if (!TryParse(text, out var value, out var error))
        {
            throw new FormatException(error);
        }

        return value;
    }

    public static bool TryParse(
        string? text,
        out TimeSpan value,
        [NotNullWhen(false)] out string? error)
    {
        value = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Duration is empty.";
            return false;
        }

        var input = text.Trim();

        // Clock form: [hh:]mm:ss[.fff]
        if (input.Contains(':', StringComparison.Ordinal))
        {
            var parts = input.Split(':');
            if (parts.Length is < 2 or > 3)
            {
                error = $"'{text}' is not a valid clock duration (expected mm:ss or hh:mm:ss).";
                return false;
            }

            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var component)
                    || component < 0)
                {
                    error = $"'{text}' contains a non-numeric or negative component.";
                    return false;
                }

                total = (total * 60) + component;
            }

            value = TimeSpan.FromSeconds(total);
            return true;
        }

        // Bare number means seconds.
        if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            if (seconds < 0)
            {
                error = $"'{text}' must not be negative.";
                return false;
            }

            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        var match = UnitPattern.Match(input);
        if (!match.Success || (!match.Groups["h"].Success && !match.Groups["m"].Success && !match.Groups["s"].Success))
        {
            error = $"'{text}' is not a recognized duration. Try 90s, 2m, 1m30s or 00:01:30.";
            return false;
        }

        var totalSeconds =
            Component(match, "h") * 3600 +
            Component(match, "m") * 60 +
            Component(match, "s");

        value = TimeSpan.FromSeconds(totalSeconds);
        return true;

        static double Component(Match match, string name) =>
            match.Groups[name].Success
                ? double.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture)
                : 0d;
    }

    public static string Format(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
}
