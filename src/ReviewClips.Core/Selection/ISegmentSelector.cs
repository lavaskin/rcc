using ReviewClips.Core.Options;

namespace ReviewClips.Core.Selection;

/// <summary>Chooses which slices of the source material become clips.</summary>
public interface ISegmentSelector
{
    SelectionStrategy Strategy { get; }

    /// <summary>
    /// True when this selector needs the analysis pass; false lets the pipeline skip an
    /// expensive full-file scan.
    /// </summary>
    bool RequiresAnalysis { get; }

    IReadOnlyList<Segment> SelectSegments(SelectionContext context);
}

/// <summary>Resolves the selector for a strategy.</summary>
public interface ISegmentSelectorFactory
{
    ISegmentSelector Create(SelectionStrategy strategy);
}
