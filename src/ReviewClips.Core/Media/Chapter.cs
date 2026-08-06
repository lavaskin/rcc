using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Media;

/// <summary>
/// A chapter marker read from the container.
/// <para>
/// The only structural information a source states about itself: where present and named,
/// chapters locate the opening titles and end credits exactly rather than by inferring them
/// from a percentage of the runtime.
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
    /// <c>Chapter 07</c> names ripping tools generate. Reported separately from having no
    /// title at all. See <see cref="ChapterFilter.IsGenericTitle"/>.
    /// </summary>
    public bool HasGenericTitle => ChapterFilter.IsGenericTitle(Title);

    /// <summary>A title worth matching against, or null.</summary>
    public string? MeaningfulTitle => HasGenericTitle ? null : Title;

    public override string ToString() =>
        $"{Index + 1}. {Title ?? "(untitled)"} [{Range}]";
}
