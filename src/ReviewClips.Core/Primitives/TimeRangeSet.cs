using System.Collections;

namespace ReviewClips.Core.Primitives;

/// <summary>
/// An immutable, normalised (sorted, non-overlapping, merged) set of <see cref="TimeRange"/>.
/// Used to model "where in the film are we allowed to take footage from".
/// </summary>
public sealed class TimeRangeSet : IReadOnlyList<TimeRange>
{
    private readonly List<TimeRange> _ranges;

    private TimeRangeSet(List<TimeRange> normalised) => _ranges = normalised;

    public static TimeRangeSet Empty { get; } = new([]);

    public TimeRange this[int index] => _ranges[index];

    public int Count => _ranges.Count;

    public TimeSpan TotalDuration
    {
        get
        {
            var total = TimeSpan.Zero;
            foreach (var r in _ranges)
            {
                total += r.Duration;
            }

            return total;
        }
    }

    public static TimeRangeSet Of(params TimeRange[] ranges) => From(ranges);

    public static TimeRangeSet From(IEnumerable<TimeRange> ranges)
    {
        var sorted = ranges.Where(r => !r.IsEmpty).OrderBy(r => r.Start).ToList();
        if (sorted.Count == 0)
        {
            return Empty;
        }

        var merged = new List<TimeRange>(sorted.Count);
        var current = sorted[0];
        for (var i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];
            if (next.Start <= current.End)
            {
                if (next.End > current.End)
                {
                    current = new TimeRange(current.Start, next.End);
                }
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return new TimeRangeSet(merged);
    }

    public TimeRangeSet Subtract(IEnumerable<TimeRange> holes)
    {
        var working = _ranges.ToList();
        foreach (var hole in holes)
        {
            if (hole.IsEmpty)
            {
                continue;
            }

            var next = new List<TimeRange>(working.Count + 1);
            foreach (var range in working)
            {
                next.AddRange(range.Subtract(hole));
            }

            working = next;
        }

        return From(working);
    }

    public TimeRangeSet Intersect(IEnumerable<TimeRange> others)
    {
        var otherSet = From(others);
        if (otherSet.Count == 0)
        {
            return Empty;
        }

        var result = new List<TimeRange>();
        foreach (var mine in _ranges)
        {
            foreach (var theirs in otherSet)
            {
                if (mine.Intersect(theirs) is { } hit)
                {
                    result.Add(hit);
                }
            }
        }

        return From(result);
    }

    /// <summary>Drops ranges shorter than <paramref name="minimum"/>.</summary>
    public TimeRangeSet WhereLongerThan(TimeSpan minimum) =>
        From(_ranges.Where(r => r.Duration >= minimum));

    public bool Contains(TimeSpan instant) => _ranges.Any(r => r.Contains(instant));

    /// <summary>
    /// Projects a real timestamp onto "eligible time": the amount of eligible material that
    /// precedes it, ignoring the excluded gaps. Lets segments be spread evenly across the
    /// <em>usable</em> footage rather than across the wall-clock runtime.
    /// </summary>
    /// <returns>The projected offset, or <c>null</c> when the instant is not eligible.</returns>
    public TimeSpan? Project(TimeSpan instant)
    {
        var accumulated = TimeSpan.Zero;

        foreach (var range in _ranges)
        {
            if (range.Contains(instant))
            {
                return accumulated + (instant - range.Start);
            }

            if (instant < range.Start)
            {
                return null;
            }

            accumulated += range.Duration;
        }

        return null;
    }

    /// <summary>True when a window of this length starting here fits inside a single range.</summary>
    public bool CanFit(TimeSpan start, TimeSpan length)
    {
        var end = start + length;
        return _ranges.Any(r => start >= r.Start && end <= r.End);
    }

    public IEnumerator<TimeRange> GetEnumerator() => _ranges.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
