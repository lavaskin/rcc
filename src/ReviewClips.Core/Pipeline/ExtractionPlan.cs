using ReviewClips.Core.Options;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Pipeline;

/// <summary>
/// Decides which encodes a render needs and which file each slot plays. Shared by the executed
/// path and <c>--dry-run</c>, so the two cannot disagree about what is encoded.
/// <para>
/// A clip is a moment of a source, identified by where it starts, which is how
/// <see cref="Planning.ClipSequencer.CountDistinct"/> and everything reporting on the plan count
/// them. The sequencer lands on the exact runtime by shortening or lengthening the final slot, so
/// one moment can fill slots of different lengths: it is encoded once at the longest of them and
/// the shorter slots play its opening. The exception is a look that varies with clip length,
/// where a shorter clip is not the opening of a longer one and each length is its own encode.
/// </para>
/// </summary>
internal sealed class ExtractionPlan
{
    private ExtractionPlan(
        IReadOnlyList<ExtractionJob> jobs,
        IReadOnlyList<string> slotPaths,
        IReadOnlyList<TimeSpan> encodedDurations)
    {
        Jobs = jobs;
        SlotPaths = slotPaths;
        EncodedDurations = encodedDurations;
    }

    /// <summary>One entry per encode, in the order the slots first need them.</summary>
    public IReadOnlyList<ExtractionJob> Jobs { get; }

    /// <summary>The file each slot plays, repeating shared encodes.</summary>
    public IReadOnlyList<string> SlotPaths { get; }

    /// <summary>The length of the file each slot plays, which can exceed the slot.</summary>
    public IReadOnlyList<TimeSpan> EncodedDurations { get; }

    public static ExtractionPlan Create(
        IReadOnlyList<Segment> slots,
        LookOptions look,
        string workDir,
        string extension)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(look);

        var shareAcrossLengths = !look.VariesWithClipLength;

        var order = new List<ClipKey>();
        var longest = new Dictionary<ClipKey, Segment>();

        foreach (var slot in slots)
        {
            var key = ClipKey.For(slot, shareAcrossLengths);

            if (!longest.TryGetValue(key, out var current))
            {
                order.Add(key);
                longest[key] = slot;
            }
            else if (slot.Duration > current.Duration)
            {
                longest[key] = slot;
            }
        }

        var paths = new Dictionary<ClipKey, string>(order.Count);
        for (var i = 0; i < order.Count; i++)
        {
            paths[order[i]] = Path.Combine(workDir, $"seg_{i:D5}{extension}");
        }

        var slotPaths = new List<string>(slots.Count);
        var encodedDurations = new List<TimeSpan>(slots.Count);
        var trimmed = new HashSet<ClipKey>();

        foreach (var slot in slots)
        {
            var key = ClipKey.For(slot, shareAcrossLengths);
            var encoded = longest[key].Duration;

            slotPaths.Add(paths[key]);
            encodedDurations.Add(encoded);

            if (slot.Duration < encoded)
            {
                trimmed.Add(key);
            }
        }

        var jobs = order
            .Select(key => new ExtractionJob(longest[key], paths[key], trimmed.Contains(key)))
            .ToList();

        return new ExtractionPlan(jobs, slotPaths, encodedDurations);
    }
}

/// <summary>One encode: the segment to cut, where it goes, and whether any slot trims it.</summary>
internal sealed record ExtractionJob(Segment Segment, string OutputPath, bool Trimmed);

/// <summary>
/// Identity of an encode. Slots at the same start share one regardless of length, unless the
/// look makes their pictures differ, in which case the length is part of the identity too.
/// </summary>
internal readonly record struct ClipKey(string SourcePath, long StartTicks, long? DurationTicks)
{
    public static ClipKey For(Segment segment, bool shareAcrossLengths) =>
        new(segment.SourcePath, segment.Start.Ticks, shareAcrossLengths ? null : segment.Duration.Ticks);
}
