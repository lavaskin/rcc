using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Ffmpeg.Stitching;

namespace ReviewClips.Ffmpeg.Tests.Stitching;

public class StitchPlanTests
{
    private static StitchRequest Request(
        double[] durations,
        TransitionKind kind = TransitionKind.Fade,
        double transitionSeconds = 0.4,
        double fadeIn = 0,
        double fadeOut = 0,
        AudioOptions? audio = null) => new()
        {
            SegmentPaths = durations.Select((_, i) => $"/tmp/seg{i}.mp4").ToList(),
            SegmentDurations = durations.Select(TimeSpan.FromSeconds).ToList(),
            OutputPath = "/tmp/out.mp4",
            Transition = new TransitionOptions
            {
                Kind = kind,
                Duration = TimeSpan.FromSeconds(transitionSeconds),
                FadeIn = TimeSpan.FromSeconds(fadeIn),
                FadeOut = TimeSpan.FromSeconds(fadeOut),
            },
            Format = OutputFormat.Youtube,
            Encoder = new EncoderProfile
            {
                VideoEncoder = "libx264",
                IsHardware = false,
                QualityArguments = ["-crf", "20"],
                ExtraArguments = [],
            },
            EncoderOptions = new EncoderOptions(),
            Audio = audio ?? AudioOptions.Muted,
            WorkingDirectory = "/tmp/work",
        };

    [Fact]
    public void TotalDuration_SubtractsOneTransitionPerJoin()
    {
        // 3 clips of 5s with 0.4s transitions: 15 - 2*0.4 = 14.2s.
        var plan = StitchPlan.Create(Request([5, 5, 5], transitionSeconds: 0.4));

        plan.TotalDuration.TotalSeconds.ShouldBe(14.2, 0.0001);
    }

    [Fact]
    public void TotalDuration_IsTheSimpleSumWithoutTransitions()
    {
        var plan = StitchPlan.Create(Request([5, 5, 5], TransitionKind.None));

        plan.TotalDuration.TotalSeconds.ShouldBe(15d, 0.0001);
    }

    [Fact]
    public void TransitionOffsets_FollowTheAccumulatingFormula()
    {
        var plan = StitchPlan.Create(Request([5, 5, 5, 5], transitionSeconds: 0.4));
        var offsets = plan.TransitionOffsets().Select(o => Math.Round(o.TotalSeconds, 4)).ToList();

        // offset_k = sum(d[0..k-1]) - k*D
        // k=1: 5 - 0.4 = 4.6   k=2: 10 - 0.8 = 9.2   k=3: 15 - 1.2 = 13.8
        offsets.ShouldBe([4.6, 9.2, 13.8]);
    }

    [Fact]
    public void TransitionOffsets_HandleUnevenSegmentLengths()
    {
        var plan = StitchPlan.Create(Request([4, 6, 5], transitionSeconds: 0.5));
        var offsets = plan.TransitionOffsets().Select(o => Math.Round(o.TotalSeconds, 4)).ToList();

        // k=1: 4 - 0.5 = 3.5   k=2: 10 - 1.0 = 9.0
        offsets.ShouldBe([3.5, 9.0]);
    }

    [Fact]
    public void OffsetsAreStrictlyIncreasing_SoXfadeChainsCorrectly()
    {
        var plan = StitchPlan.Create(Request([3, 7, 4, 9, 5], transitionSeconds: 0.4));
        var offsets = plan.TransitionOffsets().ToList();

        for (var i = 0; i < offsets.Count - 1; i++)
        {
            offsets[i + 1].ShouldBeGreaterThan(offsets[i]);
        }
    }

    [Fact]
    public void TransitionDuration_IsClampedToHalfTheShortestSegment()
    {
        // A 3s transition between 2s clips would consume them entirely.
        var plan = StitchPlan.Create(Request([2, 2, 2], transitionSeconds: 3));

        plan.EffectiveTransition.TotalSeconds.ShouldBe(1d, 0.0001);
    }

    [Fact]
    public void SingleSegment_UsesNoTransitions()
    {
        var plan = StitchPlan.Create(Request([5]));

        plan.UsesTransitions.ShouldBeFalse();
        plan.TransitionOffsets().ShouldBeEmpty();
        plan.TotalDuration.TotalSeconds.ShouldBe(5d);
    }

    [Fact]
    public void Graph_ChainsXfadeAcrossEveryJoin()
    {
        var graph = StitchPlan.Create(Request([5, 5, 5], transitionSeconds: 0.4))
            .BuildGraph(includeAudio: false);

        graph.ShouldContain("[0:v][1:v]xfade=transition=fade:duration=0.4:offset=4.6[x1]");
        graph.ShouldContain("[x1][2:v]xfade=transition=fade:duration=0.4:offset=9.2[x2]");
        graph.ShouldEndWith("[vout]");
    }

    [Fact]
    public void Graph_UsesTheConcatFilterWhenTransitionsAreDisabledButFadesAreNot()
    {
        var graph = StitchPlan.Create(Request([5, 5], TransitionKind.None, fadeIn: 0.5))
            .BuildGraph(includeAudio: false);

        graph.ShouldContain("[0:v][1:v]concat=n=2:v=1:a=0");
        graph.ShouldContain("fade=t=in:st=0:d=0.5");
    }

    [Fact]
    public void Graph_PlacesTheFadeOutRelativeToTheFinalDuration()
    {
        // 3x5s with 0.4s transitions = 14.2s total, so a 0.5s fade-out starts at 13.7s.
        var graph = StitchPlan.Create(Request([5, 5, 5], transitionSeconds: 0.4, fadeOut: 0.5))
            .BuildGraph(includeAudio: false);

        graph.ShouldContain("fade=t=out:st=13.7:d=0.5");
    }

    [Fact]
    public void Graph_MapsDipToBlackOntoTheFadeblackTransition()
    {
        var graph = StitchPlan.Create(Request([5, 5], TransitionKind.DipToBlack))
            .BuildGraph(includeAudio: false);

        graph.ShouldContain("xfade=transition=fadeblack");
    }

    [Fact]
    public void Graph_CrossfadesAudioWhenItIsKept()
    {
        var graph = StitchPlan.Create(Request([5, 5, 5], audio: AudioOptions.FromSource()))
            .BuildGraph(includeAudio: true);

        graph.ShouldContain("[0:a][1:a]acrossfade=d=0.4");
        graph.ShouldContain("[aout]");
    }

    [Fact]
    public void Graph_ConcatenatesAudioWhenThereAreNoTransitions()
    {
        var graph = StitchPlan.Create(Request([5, 5], TransitionKind.None, fadeIn: 0.5, audio: AudioOptions.FromSource()))
            .BuildGraph(includeAudio: true);

        graph.ShouldContain("concat=n=2:v=0:a=1[aout]");
    }

    [Fact]
    public void Graph_OmitsAudioChainsWhenMuted()
    {
        var graph = StitchPlan.Create(Request([5, 5])).BuildGraph(includeAudio: false);

        graph.ShouldNotContain(":a]");
        graph.ShouldNotContain("aout");
    }

    [Fact]
    public void Graph_UsesInvariantNumberFormattingForOffsets()
    {
        var graph = StitchPlan.Create(Request([5.25, 5.5], transitionSeconds: 0.4))
            .BuildGraph(includeAudio: false);

        graph.ShouldContain("offset=4.85");
    }
}

public class StitcherSelectionTests
{
    private static StitchRequest Request(TransitionKind kind, double fadeIn, double fadeOut) => new()
    {
        SegmentPaths = ["/tmp/a.mp4", "/tmp/b.mp4"],
        SegmentDurations = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)],
        OutputPath = "/tmp/out.mp4",
        Transition = new TransitionOptions
        {
            Kind = kind,
            Duration = TimeSpan.FromSeconds(0.4),
            FadeIn = TimeSpan.FromSeconds(fadeIn),
            FadeOut = TimeSpan.FromSeconds(fadeOut),
        },
        Format = OutputFormat.Youtube,
        Encoder = new EncoderProfile
        {
            VideoEncoder = "libx264",
            IsHardware = false,
            QualityArguments = [],
            ExtraArguments = [],
        },
        EncoderOptions = new EncoderOptions(),
        Audio = AudioOptions.Muted,
        WorkingDirectory = "/tmp/work",
    };

    private static ConcatDemuxerStitcher Concat() =>
        new(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<ConcatDemuxerStitcher>.Instance);

    [Fact]
    public void ConcatDemuxer_HandlesHardCutsWithNoFades() =>
        Concat().CanHandle(Request(TransitionKind.None, 0, 0)).ShouldBeTrue();

    [Fact]
    public void ConcatDemuxer_DeclinesWhenATransitionIsRequested() =>
        Concat().CanHandle(Request(TransitionKind.Fade, 0, 0)).ShouldBeFalse();

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(0, 0.5)]
    public void ConcatDemuxer_DeclinesWhenFadesAreRequested(double fadeIn, double fadeOut) =>
        // Fades need pixels touched, which a stream copy cannot do.
        Concat().CanHandle(Request(TransitionKind.None, fadeIn, fadeOut)).ShouldBeFalse();
}
