using System.Globalization;
using Microsoft.Extensions.Logging;
using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Ffmpeg.Process;

namespace ReviewClips.Ffmpeg.Stitching;

/// <summary>
/// Joins segments through a filter graph, applying cross-transitions and top-level fades.
/// <para>
/// This costs one re-encode pass, but the inputs are already small and normalised, so on a
/// hardware encoder it is quick. Handles anything the stream-copy path cannot.
/// </para>
/// </summary>
public sealed class FilterGraphStitcher : IStitcher
{
    private readonly FfmpegRunner _runner;
    private readonly ILogger<FilterGraphStitcher> _logger;

    public FilterGraphStitcher(FfmpegRunner runner, ILogger<FilterGraphStitcher> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    /// <summary>Handles every request; registered last so the stream-copy path wins when it applies.</summary>
    public bool CanHandle(StitchRequest request) => true;

    public async Task StitchAsync(
        StitchRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = StitchPlan.Create(request);

        _logger.LogDebug(
            "Stitching {Count} segments with {Transition} transitions, output {Duration:0.##}s",
            request.SegmentPaths.Count,
            request.Transition.Kind,
            plan.TotalDuration.TotalSeconds);

        await _runner.RunFfmpegCheckedAsync(
            BuildArguments(request, plan),
            plan.TotalDuration,
            progress,
            cancellationToken);
    }

    public IReadOnlyList<string> DescribeArguments(StitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return BuildArguments(request, StitchPlan.Create(request));
    }

    private static List<string> BuildArguments(StitchRequest request, StitchPlan plan)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };

        foreach (var path in request.SegmentPaths)
        {
            arguments.AddRange(["-i", path]);
        }

        var audio = request.Audio;

        // The external track goes in after every segment, so its index is the segment count.
        var externalAudioIndex = default(int?);

        if (audio.HasExternalTrack)
        {
            if (audio.Offset > TimeSpan.Zero)
            {
                arguments.AddRange(["-ss", Number(audio.Offset.TotalSeconds)]);
            }

            arguments.AddRange(["-i", audio.ExternalPath!]);
            externalAudioIndex = request.SegmentPaths.Count;
        }

        // Only per-segment audio needs graph work. An external track is one continuous stream
        // and is mapped straight through, so BuildAudioChains — which exists to acrossfade
        // neighbouring clips into each other — has nothing to say about it.
        var graph = plan.BuildGraph(request.UsesSegmentAudio);

        arguments.AddRange(["-filter_complex", graph]);
        arguments.AddRange(["-map", $"[{StitchPlan.VideoOutputLabel}]"]);

        // Without this FFmpeg copies chapters from the first input, so the finished file would
        // inherit whatever markers segment one happened to carry. The extractor already strips
        // them; this makes the guarantee hold for the output regardless of the inputs.
        arguments.AddRange(["-map_chapters", "-1"]);

        if (externalAudioIndex is { } audioIndex)
        {
            arguments.AddRange(["-map", $"{audioIndex}:a"]);
            arguments.AddRange(["-c:a", "aac"]);
            arguments.AddRange(["-b:a", $"{request.EncoderOptions.AudioBitrateKbps}k"]);
            arguments.AddRange(["-ac", "2"]);

            // A simple -filter:a rather than a filter_complex branch: the track is mapped
            // straight from an input and never meets the segment graph, so it needs no labels.
            var audioFilters = new List<string>(3);

            if (audio.AltersVolume)
            {
                audioFilters.Add($"volume={Number(audio.Volume)}");
            }

            // Matched to the video fades so the bed does not carry on at full volume over a
            // picture fading to black. Timed against the video, which is what the fades were
            // computed from; if -shortest ends the file early because the track ran out first,
            // the closing fade is truncated with it.
            audioFilters.AddRange(plan.AudioFadeFilters());

            if (audioFilters.Count > 0)
            {
                arguments.AddRange(["-filter:a", string.Join(',', audioFilters)]);
            }

            // Stops a track longer than the render from padding the output with still video.
            arguments.Add("-shortest");
        }
        else if (request.UsesSegmentAudio)
        {
            arguments.AddRange(["-map", $"[{StitchPlan.AudioOutputLabel}]"]);
            arguments.AddRange(["-c:a", "aac"]);
            arguments.AddRange(["-b:a", $"{request.EncoderOptions.AudioBitrateKbps}k"]);
        }
        else
        {
            arguments.Add("-an");
        }

        arguments.AddRange(["-c:v", request.Encoder.VideoEncoder]);
        arguments.AddRange(request.Encoder.QualityArguments);
        arguments.AddRange(request.Encoder.ExtraArguments);
        arguments.AddRange(["-pix_fmt", request.Encoder.PixelFormat]);
        arguments.AddRange(["-fps_mode", "cfr"]);
        arguments.AddRange(["-r", Number(request.Format.FrameRate)]);
        arguments.AddRange(["-movflags", "+faststart"]);
        arguments.Add(request.OutputPath);

        return arguments;
    }

    private static string Number(double value) =>
        value.ToString("0.#####", CultureInfo.InvariantCulture);
}

/// <summary>
/// Works out the graph and the resulting runtime for a stitch.
/// Separated from the stitcher so the offset arithmetic can be unit tested directly.
/// </summary>
public sealed record StitchPlan
{
    private readonly IReadOnlyList<TimeSpan> _durations;
    private readonly TransitionOptions _transition;
    private readonly TimeSpan _effectiveTransition;

    private StitchPlan(
        IReadOnlyList<TimeSpan> durations,
        TransitionOptions transition,
        TimeSpan effectiveTransition)
    {
        _durations = durations;
        _transition = transition;
        _effectiveTransition = effectiveTransition;
    }

    public static string VideoOutputLabel => "vout";

    public static string AudioOutputLabel => "aout";

    public int InputCount => _durations.Count;

    /// <summary>The transition length actually used, after clamping to fit the shortest clip.</summary>
    public TimeSpan EffectiveTransition => _effectiveTransition;

    public bool UsesTransitions => _effectiveTransition > TimeSpan.Zero && _durations.Count > 1;

    /// <summary>
    /// Runtime of the finished render. Each transition overlaps two clips, so every one of them
    /// shortens the total by its own duration.
    /// </summary>
    public TimeSpan TotalDuration
    {
        get
        {
            var sum = _durations.Aggregate(TimeSpan.Zero, (a, b) => a + b);
            return UsesTransitions
                ? sum - (_effectiveTransition * (_durations.Count - 1))
                : sum;
        }
    }

    /// <summary>The opening fade, after clamping. Zero when there is none.</summary>
    public TimeSpan FadeInDuration => ClampFade(_transition.FadeIn, TotalDuration);

    /// <summary>The closing fade, after clamping. Zero when there is none.</summary>
    public TimeSpan FadeOutDuration => ClampFade(_transition.FadeOut, TotalDuration);

    /// <summary>When the closing fade begins.</summary>
    public TimeSpan FadeOutStart
    {
        get
        {
            var start = TotalDuration - FadeOutDuration;
            return start > TimeSpan.Zero ? start : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// True when the render fades at either end.
    /// <para>
    /// Exposed so the audio can be faded on exactly the same schedule. A picture that fades to
    /// black under a soundtrack still playing at full volume, then stops dead, is the single
    /// most obvious way for a render to sound unfinished — and it is far more noticeable under
    /// a music bed than under a clip's own incidental audio.
    /// </para>
    /// </summary>
    public bool HasFades => FadeInDuration > TimeSpan.Zero || FadeOutDuration > TimeSpan.Zero;

    /// <summary>
    /// The <c>afade</c> filters matching the video fades, or an empty list when there are none.
    /// </summary>
    public IReadOnlyList<string> AudioFadeFilters()
    {
        var filters = new List<string>(2);

        if (FadeInDuration > TimeSpan.Zero)
        {
            filters.Add($"afade=t=in:st=0:d={Seconds(FadeInDuration)}");
        }

        if (FadeOutDuration > TimeSpan.Zero)
        {
            filters.Add($"afade=t=out:st={Seconds(FadeOutStart)}:d={Seconds(FadeOutDuration)}");
        }

        return filters;
    }

    public static StitchPlan Create(StitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var durations = request.SegmentDurations;
        var requested = request.Transition.IsEnabled ? request.Transition.Duration : TimeSpan.Zero;

        if (requested > TimeSpan.Zero && durations.Count > 1)
        {
            // xfade consumes the transition from both neighbours. If it is not comfortably
            // shorter than the shortest clip, the graph either errors or eats a clip whole,
            // so clamp to half the shortest segment.
            var shortest = durations.Min();
            var ceiling = TimeSpan.FromSeconds(shortest.TotalSeconds / 2);
            if (requested > ceiling)
            {
                requested = ceiling;
            }
        }
        else
        {
            requested = TimeSpan.Zero;
        }

        return new StitchPlan(durations, request.Transition, requested);
    }

    /// <summary>Offsets at which each successive xfade begins, in order.</summary>
    public IReadOnlyList<TimeSpan> TransitionOffsets()
    {
        if (!UsesTransitions)
        {
            return [];
        }

        var offsets = new List<TimeSpan>(_durations.Count - 1);
        var cumulative = TimeSpan.Zero;

        for (var k = 1; k < _durations.Count; k++)
        {
            cumulative += _durations[k - 1];

            // offset_k = sum(d[0..k-1]) - k * D
            var offset = cumulative - (_effectiveTransition * k);
            offsets.Add(offset > TimeSpan.Zero ? offset : TimeSpan.Zero);
        }

        return offsets;
    }

    public string BuildGraph(bool includeAudio)
    {
        var chains = new List<string>();

        BuildVideoChains(chains);

        if (includeAudio)
        {
            BuildAudioChains(chains);
        }

        return string.Join(';', chains);
    }

    private void BuildVideoChains(List<string> chains)
    {
        var needsFades = HasFades;

        string joined;

        if (_durations.Count == 1)
        {
            joined = "j0";
            chains.Add($"[0:v]null[{joined}]");
        }
        else if (UsesTransitions)
        {
            var offsets = TransitionOffsets();
            var current = "0:v";

            for (var k = 1; k < _durations.Count; k++)
            {
                var next = $"x{k}";
                chains.Add(
                    $"[{current}][{k}:v]xfade=transition={_transition.XfadeName}"
                    + $":duration={Seconds(_effectiveTransition)}"
                    + $":offset={Seconds(offsets[k - 1])}[{next}]");
                current = next;
            }

            joined = current;
        }
        else
        {
            var inputs = string.Concat(Enumerable.Range(0, _durations.Count).Select(i => $"[{i}:v]"));
            joined = "cv";
            chains.Add($"{inputs}concat=n={_durations.Count}:v=1:a=0[{joined}]");
        }

        if (!needsFades)
        {
            chains.Add($"[{joined}]null[{VideoOutputLabel}]");
            return;
        }

        var filters = new List<string>(2);

        if (FadeInDuration > TimeSpan.Zero)
        {
            filters.Add($"fade=t=in:st=0:d={Seconds(FadeInDuration)}");
        }

        if (FadeOutDuration > TimeSpan.Zero)
        {
            filters.Add($"fade=t=out:st={Seconds(FadeOutStart)}:d={Seconds(FadeOutDuration)}");
        }

        chains.Add($"[{joined}]{string.Join(',', filters)}[{VideoOutputLabel}]");
    }

    private void BuildAudioChains(List<string> chains)
    {
        // The tail filter is where the whole-render fades land, so the audio ends on the same
        // schedule as the picture. anull when there are none, keeping the chain shape identical
        // either way.
        var fades = AudioFadeFilters();
        var tail = fades.Count > 0 ? string.Join(',', fades) : "anull";

        if (_durations.Count == 1)
        {
            chains.Add($"[0:a]{tail}[{AudioOutputLabel}]");
            return;
        }

        if (UsesTransitions)
        {
            // acrossfade computes its own overlap, so no offsets are needed here.
            var current = "0:a";
            for (var k = 1; k < _durations.Count; k++)
            {
                var next = $"xa{k}";
                chains.Add(
                    $"[{current}][{k}:a]acrossfade=d={Seconds(_effectiveTransition)}:c1=tri:c2=tri[{next}]");
                current = next;
            }

            chains.Add($"[{current}]{tail}[{AudioOutputLabel}]");
            return;
        }

        var inputs = string.Concat(Enumerable.Range(0, _durations.Count).Select(i => $"[{i}:a]"));
        var joined = fades.Count > 0 ? "ca" : AudioOutputLabel;

        chains.Add($"{inputs}concat=n={_durations.Count}:v=0:a=1[{joined}]");

        if (fades.Count > 0)
        {
            chains.Add($"[{joined}]{tail}[{AudioOutputLabel}]");
        }
    }

    private static TimeSpan ClampFade(TimeSpan fade, TimeSpan total)
    {
        // A fade longer than a third of the render is almost certainly a mistake.
        var ceiling = TimeSpan.FromSeconds(total.TotalSeconds / 3);
        return fade > ceiling && ceiling > TimeSpan.Zero ? ceiling : fade;
    }

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.#####", CultureInfo.InvariantCulture);
}
