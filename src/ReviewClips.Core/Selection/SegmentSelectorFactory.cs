using ReviewClips.Core.Options;

namespace ReviewClips.Core.Selection;

public sealed class SegmentSelectorFactory : ISegmentSelectorFactory
{
    private readonly Dictionary<SelectionStrategy, ISegmentSelector> _selectors;

    public SegmentSelectorFactory(IEnumerable<ISegmentSelector> selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);
        _selectors = selectors.ToDictionary(s => s.Strategy);
    }

    public static SegmentSelectorFactory CreateDefault() =>
        new(
        [
            new UniformSegmentSelector(),
            new RandomSegmentSelector(),
            new SceneSegmentSelector(),
            new ScoredSegmentSelector(),
            new CueDrivenSegmentSelector(),
        ]);

    public ISegmentSelector Create(SelectionStrategy strategy) =>
        _selectors.TryGetValue(strategy, out var selector)
            ? selector
            : throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "No selector registered for this strategy.");
}
