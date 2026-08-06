using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ReviewClips.Core.Primitives;

/// <summary>
/// An offset into a piece of media, expressed either absolutely (<c>90s</c>) or as a fraction of
/// its runtime (<c>5%</c>), so one <c>--skip-head</c> / <c>--skip-tail</c> default suits any
/// runtime.
/// </summary>
public readonly record struct Offset
{
    private Offset(TimeSpan? absolute, double? fraction)
    {
        Absolute = absolute;
        Fraction = fraction;
    }

    public TimeSpan? Absolute { get; }

    public double? Fraction { get; }

    public static Offset Zero { get; } = FromTime(TimeSpan.Zero);

    public bool IsRelative => Fraction.HasValue;

    /// <summary>
    /// True when this offset trims nothing, whichever form it was given in: <c>0s</c> and
    /// <c>0%</c> are distinct values but the same instruction.
    /// <para>
    /// A default-constructed <see cref="Offset"/> holds neither form and counts too. Parsing
    /// always produces one or the other, but <c>SelectionOptions</c> can be built directly, and
    /// <c>EligibilityDiagnostics</c> reads this when apportioning blame for an empty selection.
    /// </para>
    /// </summary>
    public bool IsZero => Absolute is { Ticks: <= 0 }
        || Fraction <= 0d
        || (Absolute is null && Fraction is null);

    public static Offset FromTime(TimeSpan value) => new(value, null);

    public static Offset FromPercent(double percent) => new(null, percent / 100d);

    public TimeSpan Resolve(TimeSpan total) =>
        Absolute ?? TimeSpan.FromSeconds(total.TotalSeconds * Fraction!.Value);

    public static Offset Parse(string text)
    {
        if (!TryParse(text, out var value, out var error))
        {
            throw new FormatException(error);
        }

        return value;
    }

    public static bool TryParse(
        string? text,
        out Offset value,
        [NotNullWhen(false)] out string? error)
    {
        value = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Offset is empty.";
            return false;
        }

        var input = text.Trim();
        if (input.EndsWith('%'))
        {
            var numeric = input[..^1];
            if (!double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                error = $"'{text}' is not a valid percentage.";
                return false;
            }

            if (percent is < 0 or > 100)
            {
                error = $"'{text}' must be between 0% and 100%.";
                return false;
            }

            value = FromPercent(percent);
            return true;
        }

        if (!DurationSpec.TryParse(input, out var absolute, out error))
        {
            return false;
        }

        value = FromTime(absolute);
        return true;
    }

    public override string ToString() =>
        IsRelative
            ? $"{Fraction!.Value * 100:0.##}%"
            : $"{Absolute!.Value.TotalSeconds:0.##}s";
}
