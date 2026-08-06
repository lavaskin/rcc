using System.CommandLine;
using System.CommandLine.Parsing;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Cli.Cli;

/// <summary>
/// Custom parsers for the domain primitives, so validation errors surface at parse time with a
/// helpful message instead of throwing halfway through a render.
/// </summary>
internal static class ValueParsers
{
    public static Option<TimeSpan?> Duration(string name, string description, params string[] aliases) =>
        new(name, aliases)
        {
            Description = description,
            HelpName = "duration",
            CustomParser = result =>
            {
                var text = SingleToken(result);
                if (text is null)
                {
                    return null;
                }

                if (!DurationSpec.TryParse(text, out var value, out var error))
                {
                    result.AddError(error);
                    return null;
                }

                return value;
            },
        };

    public static Option<Offset?> OffsetOption(string name, string description, params string[] aliases) =>
        new(name, aliases)
        {
            Description = description,
            HelpName = "time|percent",
            CustomParser = result =>
            {
                var text = SingleToken(result);
                if (text is null)
                {
                    return null;
                }

                if (!Offset.TryParse(text, out var value, out var error))
                {
                    result.AddError(error);
                    return null;
                }

                return value;
            },
        };

    public static Option<Ratio?> AspectOption(string name, string description, params string[] aliases) =>
        new(name, aliases)
        {
            Description = description,
            HelpName = "w:h",
            CustomParser = result =>
            {
                var text = SingleToken(result);
                if (text is null)
                {
                    return null;
                }

                if (!Ratio.TryParse(text, out var value, out var error))
                {
                    result.AddError(error);
                    return null;
                }

                return value;
            },
        };

    /// <summary>Parses <c>1920x1080</c> or a shorthand such as <c>1080p</c>.</summary>
    public static Option<(int Width, int Height)?> ResolutionOption(
        string name,
        string description,
        params string[] aliases) =>
        new(name, aliases)
        {
            Description = description,
            HelpName = "WxH|1080p",
            CustomParser = result =>
            {
                var text = SingleToken(result);
                if (text is null)
                {
                    return null;
                }

                if (!TryParseResolution(text, out var size, out var error))
                {
                    result.AddError(error!);
                    return null;
                }

                return size;
            },
        };

    /// <summary>Parses repeated <c>start-end</c> ranges.</summary>
    public static Option<List<TimeRange>> RangeListOption(
        string name,
        string description,
        params string[] aliases) =>
        new(name, aliases)
        {
            Description = description,
            HelpName = "start-end",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore,
            CustomParser = result =>
            {
                var ranges = new List<TimeRange>();

                foreach (var token in result.Tokens)
                {
                    if (!TryParseRange(token.Value, out var range, out var error))
                    {
                        result.AddError(error!);
                        continue;
                    }

                    ranges.Add(range);
                }

                return ranges;
            },
        };

    /// <summary>
    /// Collects repeated free-text values. Not comma-split, because the values are matched
    /// against chapter titles and a title may legitimately contain a comma.
    /// </summary>
    public static Option<List<string>> TextListOption(
        string name,
        string description,
        string helpName,
        params string[] aliases) =>
        new(name, aliases)
        {
            Description = description,
            HelpName = helpName,
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore,
            CustomParser = result =>
            [
                .. result.Tokens
                    .Select(t => t.Value.Trim())
                    .Where(v => v.Length > 0),
            ],
        };

    /// <summary>
    /// Parses cues from either a comma-separated list or a file, one per line. Blank lines,
    /// <c>#</c> comments and trailing labels are ignored, so a working notes file can be used
    /// directly.
    /// </summary>
    public static Option<List<TimeSpan>> CuesOption(
        string name,
        string description,
        params string[] aliases) =>
        new(name, aliases)
        {
            Description = description,
            HelpName = "file|t1,t2,...",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore,
            CustomParser = result =>
            {
                var cues = new List<TimeSpan>();

                foreach (var token in result.Tokens)
                {
                    if (File.Exists(token.Value))
                    {
                        ReadCueFile(token.Value, cues, result);
                    }
                    else
                    {
                        foreach (var part in token.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (DurationSpec.TryParse(part, out var cue, out var error))
                            {
                                cues.Add(cue);
                            }
                            else
                            {
                                result.AddError(error);
                            }
                        }
                    }
                }

                return cues;
            },
        };

    public static Option<T?> EnumOption<T>(string name, string description, params string[] aliases)
        where T : struct, Enum =>
        new(name, aliases)
        {
            Description = description + " (" + string.Join('|', Enum.GetNames<T>().Select(Kebab)) + ")",
            HelpName = "value",
            CustomParser = result =>
            {
                var text = SingleToken(result);
                if (text is null)
                {
                    return null;
                }

                // Accept kebab-case as well as the enum's own spelling.
                var normalised = text.Replace("-", string.Empty, StringComparison.Ordinal);
                if (Enum.TryParse<T>(normalised, ignoreCase: true, out var value))
                {
                    return value;
                }

                result.AddError(
                    $"'{text}' is not valid for {name}. Expected one of: "
                    + string.Join(", ", Enum.GetNames<T>().Select(Kebab)) + ".");
                return null;
            },
        };

    public static string Kebab(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    internal static bool TryParseResolution(string text, out (int Width, int Height) size, out string? error)
    {
        size = default;
        error = null;
        var input = text.Trim().ToLowerInvariant();

        // Common shorthands, all 16:9.
        var shorthand = input switch
        {
            "720p" => (1280, 720),
            "1080p" or "fhd" => (1920, 1080),
            "1440p" or "2k" => (2560, 1440),
            "2160p" or "4k" or "uhd" => (3840, 2160),
            _ => ((int, int)?)null,
        };

        if (shorthand is { } known)
        {
            size = known;
            return true;
        }

        var parts = input.Split('x', '×');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var w)
            || !int.TryParse(parts[1], out var h)
            || w <= 0
            || h <= 0)
        {
            error = $"'{text}' is not a valid resolution. Use 1920x1080 or a shorthand like 1080p.";
            return false;
        }

        size = (w, h);
        return true;
    }

    internal static bool TryParseRange(string text, out TimeRange range, out string? error)
    {
        range = default;
        error = null;

        var separator = text.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0)
        {
            error = $"'{text}' is not a valid range. Use start-end, e.g. 00:10:00-01:40:00.";
            return false;
        }

        var startText = text[..separator];
        var endText = text[(separator + 1)..];

        if (!DurationSpec.TryParse(startText, out var start, out error)
            || !DurationSpec.TryParse(endText, out var end, out error))
        {
            return false;
        }

        if (end <= start)
        {
            error = $"'{text}' ends before it starts.";
            return false;
        }

        range = new TimeRange(start, end);
        return true;
    }

    private static void ReadCueFile(string path, List<TimeSpan> cues, ArgumentResult result)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            // Allow a trailing label, as in "00:12:34  the twist reveal".
            var token = line.Split([' ', '\t', ',', ';'], 2, StringSplitOptions.RemoveEmptyEntries)[0];

            if (DurationSpec.TryParse(token, out var cue, out _))
            {
                cues.Add(cue);
            }
            else
            {
                result.AddError($"{Path.GetFileName(path)}: could not read a timestamp from '{line}'.");
            }
        }
    }

    private static string? SingleToken(ArgumentResult result) =>
        result.Tokens.Count == 0 ? null : result.Tokens[^1].Value;
}
