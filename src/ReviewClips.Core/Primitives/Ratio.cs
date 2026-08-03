using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ReviewClips.Core.Primitives;

/// <summary>An exact rational, used for aspect ratios and sample aspect ratios.</summary>
public readonly record struct Ratio(int Numerator, int Denominator)
{
    public static Ratio One { get; } = new(1, 1);

    public static Ratio Widescreen { get; } = new(16, 9);

    public static Ratio Vertical { get; } = new(9, 16);

    public static Ratio Square { get; } = new(1, 1);

    public bool IsValid => Numerator > 0 && Denominator > 0;

    public double Value => Denominator == 0 ? 0d : (double)Numerator / Denominator;

    /// <summary>Parses <c>16:9</c> or <c>16/9</c>.</summary>
    public static bool TryParse(string? text, out Ratio value, [NotNullWhen(false)] out string? error)
    {
        value = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Ratio is empty.";
            return false;
        }

        var parts = text.Trim().Split(':', '/');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
        {
            error = $"'{text}' is not a valid ratio. Use forms like 16:9.";
            return false;
        }

        if (n <= 0 || d <= 0)
        {
            error = $"'{text}' must have positive components.";
            return false;
        }

        value = new Ratio(n, d);
        return true;
    }

    public static Ratio Parse(string text) =>
        TryParse(text, out var value, out var error) ? value : throw new FormatException(error);

    public override string ToString() => $"{Numerator}:{Denominator}";
}
