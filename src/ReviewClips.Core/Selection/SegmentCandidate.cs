namespace ReviewClips.Core.Selection;

/// <summary>A possible segment start, before ranking and spacing rules are applied.</summary>
public readonly record struct SegmentCandidate(
    SourceMedia Source,
    TimeSpan Start,
    double Score,
    string Reason)
{
    public string SourcePath => Source.Path;
}
