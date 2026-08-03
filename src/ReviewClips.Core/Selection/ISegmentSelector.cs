using ReviewClips.Core.Options;

namespace ReviewClips.Core.Selection;

/// <summary>Chooses which slices of the source material become clips.</summary>
public interface ISegmentSelector
{
    SelectionStrategy Strategy { get; }

    /// <summary>
    /// True when this selector needs the analysis pass. Lets the pipeline skip an expensive
    /// full-file scan for strategies that don't benefit from one.
    /// </summary>
    bool RequiresAnalysis { get; }

    IReadOnlyList<Segment> SelectSegments(SelectionContext context);
}

/// <summary>Resolves the selector for a strategy.</summary>
public interface ISegmentSelectorFactory
{
    ISegmentSelector Create(SelectionStrategy strategy);
}
