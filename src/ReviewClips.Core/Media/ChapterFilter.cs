using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ReviewClips.Core.Media;

/// <summary>
/// Matches chapter titles against patterns, and supplies the built-in set of titles that
/// denote material worth skipping.
/// </summary>
public static class ChapterFilter
{
    /// <summary>
    /// Titles that indicate an opening sequence, an end sequence, or recycled footage.
    /// <para>
    /// These are matched as case-insensitive substrings. They are deliberately phrases a
    /// distributor uses for structural chapters rather than words a chapter of actual content
    /// would be named after, so a false positive costs a few minutes of eligible footage at
    /// worst. Titles are only ever consulted, never positions, so an unnamed disc rip is
    /// unaffected.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> IntroOutroPatterns { get; } =
    [
        // Front matter.
        "logo",
        "distributor",
        "copyright",
        "disclaimer",
        "intro",
        "opening",
        "main titles",
        "main title sequence",
        "title sequence",

        // Back matter.
        "credits",
        "end titles",
        "closing",
        "ending",
        "outro",

        // Footage borrowed from elsewhere, which reads as filler in a review.
        "recap",
        "previously on",
        "next episode",
        "next time",
        "preview",
        "trailer",
        "bloopers",
        "outtakes",
        "gag reel",
    ];

    /// <summary>Compiled globs, cached because the same handful of patterns is reused per source.</summary>
    private static readonly ConcurrentDictionary<string, Regex> GlobCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Titles that a ripping tool generated from the chapter number and that therefore say
    /// nothing about content: <c>Chapter 12</c>, <c>12</c>, <c>00:14:32.100</c>.
    /// </summary>
    private static readonly Regex GenericTitle = new(
        @"^\s*(chapter|kapitel|chapitre|capitulo|cap[íi]tulo|part|track|segment)?\s*[-#:.]?\s*\d{1,3}\s*$"
        + @"|^\s*\d{1,2}:\d{2}(:\d{2})?([.,]\d+)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// True when the title carries no information beyond the chapter's position.
    /// </summary>
    public static bool IsGenericTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) || GenericTitle.IsMatch(title);

    /// <summary>
    /// Tests a chapter title against a pattern.
    /// <para>
    /// A pattern containing <c>*</c> or <c>?</c> is a glob matched against the whole title.
    /// Anything else is matched as a substring, because <c>--skip-chapter credits</c> meaning
    /// "only a chapter named exactly 'credits'" would surprise every user who typed it.
    /// </para>
    /// </summary>
    public static bool Matches(string? title, string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (string.IsNullOrWhiteSpace(title) || pattern.Length == 0)
        {
            return false;
        }

        var trimmed = pattern.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (!IsGlob(trimmed))
        {
            return title.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return GlobCache.GetOrAdd(trimmed, CompileGlob).IsMatch(title.Trim());
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological title is not a reason to fail a render.
            return false;
        }
    }

    /// <summary>True when the title matches any of the patterns.</summary>
    public static bool MatchesAny(string? title, IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (Matches(title, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The chapters whose titles match any pattern, in file order. Chapters with a generic
    /// title are never matched: <c>Chapter 12</c> would otherwise be caught by a pattern such
    /// as <c>*1*</c> for no useful reason.
    /// </summary>
    public static IReadOnlyList<Chapter> Matching(
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(patterns);

        if (chapters.Count == 0 || patterns.Count == 0)
        {
            return [];
        }

        List<Chapter>? matched = null;
        foreach (var chapter in chapters)
        {
            if (chapter.MeaningfulTitle is { } title && MatchesAny(title, patterns))
            {
                (matched ??= []).Add(chapter);
            }
        }

        return matched ?? (IReadOnlyList<Chapter>)[];
    }

    private static bool IsGlob(string pattern) =>
        pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal);

    private static Regex CompileGlob(string pattern)
    {
        var expression = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";

        return new Regex(
            expression,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
    }
}
