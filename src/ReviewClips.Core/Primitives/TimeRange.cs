namespace ReviewClips.Core.Primitives;

/// <summary>A half-open time interval <c>[Start, End)</c>.</summary>
public readonly record struct TimeRange
{
    public TimeRange(TimeSpan start, TimeSpan end)
    {
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), $"End ({end}) must be >= start ({start}).");
        }

        Start = start;
        End = end;
    }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public TimeSpan Duration => End - Start;

    public bool IsEmpty => End <= Start;

    public static TimeRange FromDuration(TimeSpan start, TimeSpan duration) => new(start, start + duration);

    public bool Contains(TimeSpan instant) => instant >= Start && instant < End;

    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;

    public TimeRange? Intersect(TimeRange other)
    {
        var start = Start > other.Start ? Start : other.Start;
        var end = End < other.End ? End : other.End;
        return end > start ? new TimeRange(start, end) : null;
    }

    /// <summary>Removes <paramref name="hole"/> from this range, yielding 0, 1 or 2 remaining pieces.</summary>
    public IEnumerable<TimeRange> Subtract(TimeRange hole)
    {
        if (!Overlaps(hole))
        {
            yield return this;
            yield break;
        }

        if (hole.Start > Start)
        {
            yield return new TimeRange(Start, hole.Start);
        }

        if (hole.End < End)
        {
            yield return new TimeRange(hole.End, End);
        }
    }

    /// <summary>Expands the range by <paramref name="pad"/> on both sides, clamped at zero.</summary>
    public TimeRange Pad(TimeSpan pad)
    {
        var start = Start - pad;
        return new TimeRange(start < TimeSpan.Zero ? TimeSpan.Zero : start, End + pad);
    }

    public override string ToString() =>
        $"{Start.TotalSeconds:0.###}s-{End.TotalSeconds:0.###}s";
}
