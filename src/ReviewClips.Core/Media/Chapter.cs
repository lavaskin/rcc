using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Media;

/// <summary>
/// A chapter marker read from the container.
/// <para>
/// Chapters are the only structural information a source can state about itself. Where they are
/// present and named, they locate the opening titles and the end credits exactly, which is
/// strictly better than inferring those positions from a percentage of the runtime.
/// </para>
/// </summary>
public sealed record Chapter
{
    /// <summary>Position in the file, counted from zero after unusable entries are dropped.</summary>
    public required int Index { get; init; }

    public required TimeRange Range { get; init; }

    /// <summary>Null when the container carries no title tag for this chapter.</summary>
    public string? Title { get; init; }

    public TimeSpan Start => Range.Start;

    public TimeSpan End => Range.End;

    public TimeSpan Duration => Range.Duration;

    /// <summary>
    /// True when the title carries no information beyond the position, as with the
    /// <c>Chapter 07</c> names that ripping tools generate for unmarked discs. Such a title
    /// cannot be matched against, so it is worth reporting separately from having no title.
    /// </summary>
    public bool HasGenericTitle => ChapterFilter.IsGenericTitle(Title);

    /// <summary>A title worth matching against, or null.</summary>
    public string? MeaningfulTitle => HasGenericTitle ? null : Title;

    public override string ToString() =>
        $"{Index + 1}. {Title ?? "(untitled)"} [{Range}]";
}
