using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Selection;

/// <summary>
/// Works out <em>why</em> a selection came back empty.
/// <para>
/// The cause is measured, not guessed: each filter is relaxed in turn and the eligible footage
/// recomputed; whichever relaxation restores footage is the culprit. Simply listing the enabled
/// filters would blame the on-by-default detectors for a failure caused by an explicit option.
/// Recomputing via <see cref="SelectionContext.EligibleRanges"/> keeps this from drifting.
/// </para>
/// </summary>
public static class EligibilityDiagnostics
{
    /// <summary>
    /// Describes the filter responsible for there being no eligible footage, or null when
    /// footage <em>was</em> eligible and the failure lies in placement instead.
    /// </summary>
    public static string? Explain(SelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var window = context.LongestSegment;
        if (window <= TimeSpan.Zero || context.Sources.Count == 0)
        {
            return null;
        }

        if (EligibleSeconds(context, context.Options, window) > TimeSpan.Zero)
        {
            // Footage was available; something downstream of eligibility rejected it.
            return null;
        }

        foreach (var (relaxed, description) in Relaxations(context.Options))
        {
            if (EligibleSeconds(context, relaxed, window) > TimeSpan.Zero)
            {
                return description;
            }
        }

        // Nothing relaxable accounts for it, so the sources are simply too short for a clip of
        // this length.
        var longest = context.Sources.Max(s => s.Info.Duration);
        return $"no source is long enough for a {window.TotalSeconds:0.#}s clip "
            + $"(the longest is {longest.TotalSeconds:0.#}s); try a shorter --splice";
    }

    /// <summary>
    /// One relaxation per filter that is actually in force, in the order a reader would want to
    /// hear about them: the settings they typed before the ones that were on by default.
    /// </summary>
    private static IEnumerable<(SelectionOptions Options, string Description)> Relaxations(
        SelectionOptions options)
    {
        if (options.IncludeRanges.Count > 0)
        {
            yield return (
                options with { IncludeRanges = [] },
                "--range leaves nothing long enough to cut from; widen or drop it");
        }

        if (options.ExcludeRanges.Count > 0)
        {
            yield return (
                options with { ExcludeRanges = [] },
                "--exclude removes everything that was left; narrow or drop it");
        }

        if (!options.SkipHead.IsZero || !options.SkipTail.IsZero)
        {
            yield return (
                options with { SkipHead = Offset.Zero, SkipTail = Offset.Zero },
                "--skip-head and --skip-tail together remove the whole source; try --skip-head 0 --skip-tail 0");
        }

        if (options.EffectiveChapterPatterns.Count > 0)
        {
            yield return (
                options with { ChapterSkip = ChapterSkipMode.Off, SkipChapterPatterns = [] },
                "every chapter was skipped by title; try --chapters off");
        }

        // Singly before jointly, so a source rejected purely for being static is not also
        // blamed on black. The combined form is the fallback when neither accounts for it alone.
        if (options.RejectBlack)
        {
            yield return (
                options with { RejectBlack = false },
                "the footage was detected as black throughout; try --no-reject-black");
        }

        if (options.RejectFrozen)
        {
            yield return (
                options with { RejectFrozen = false },
                "the footage was detected as frozen or static throughout; try --no-reject-frozen");
        }

        if (options.RejectBlack && options.RejectFrozen)
        {
            yield return (
                options with { RejectBlack = false, RejectFrozen = false },
                "the footage was detected as black or frozen throughout; "
                + "try --no-reject-black / --no-reject-frozen");
        }
    }

    private static TimeSpan EligibleSeconds(
        SelectionContext context,
        SelectionOptions options,
        TimeSpan window)
    {
        var probe = context with { Options = options };

        return context.Sources.Aggregate(
            TimeSpan.Zero,
            (total, source) => total + probe.EligibleRanges(source, window).TotalDuration);
    }
}
