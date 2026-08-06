using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;

namespace ReviewClips.Core.Tests.Pipeline;

/// <summary>
/// Planning decisions that depend on the audio track: chiefly <c>--match-audio</c>, which has
/// to land before the cadence is planned or the render is sized against the wrong number.
/// </summary>
public class AudioPlanningTests
{
    private static (PipelineHarness Harness, string Source) Setup()
    {
        var harness = new PipelineHarness();
        return (harness, harness.AddSource("film.mkv", 3600));
    }

    [Fact]
    public async Task MatchAudioTakesTheTargetFromTheTrack()
    {
        var (harness, source) = Setup();
        var track = harness.AddAudio("bed.wav", 47);

        var request = PipelineHarness.Request(source, durationSeconds: 60) with
        {
            Audio = AudioOptions.FromFile(track) with { MatchDuration = true },
        };

        var plan = await harness.PlanAsync(request);

        // The 60s the request carried is replaced outright, not averaged with or added to.
        plan.Request.TargetDuration.ShouldBe(TimeSpan.FromSeconds(47));
        plan.EffectiveDuration.TotalSeconds.ShouldBe(47d, 0.5d);
    }

    [Fact]
    public async Task MatchAudioSizesTheCadenceNotJustTheReportedTarget()
    {
        var (harness, source) = Setup();
        var track = harness.AddAudio("bed.wav", 100);

        var request = PipelineHarness.Request(source, durationSeconds: 20, spliceSeconds: 5) with
        {
            Audio = AudioOptions.FromFile(track) with { MatchDuration = true },
        };

        var plan = await harness.PlanAsync(request);

        // 100s of 5s splices is 20-odd slots — a few more than 20, because the default
        // dip-to-black overlaps each join and the planner adds material to compensate.
        // Had the target been derived after cadence planning there would be about 4, and the
        // render would come out a fifth of the length of the track.
        plan.Segments.Count.ShouldBeGreaterThan(15);
        plan.EffectiveDuration.TotalSeconds.ShouldBe(100d, 1d);
    }

    [Fact]
    public async Task MatchAudioSubtractsTheOffset()
    {
        var (harness, source) = Setup();
        var track = harness.AddAudio("bed.wav", 60);

        var request = PipelineHarness.Request(source) with
        {
            Audio = AudioOptions.FromFile(track) with
            {
                MatchDuration = true,
                Offset = TimeSpan.FromSeconds(20),
            },
        };

        var plan = await harness.PlanAsync(request);

        // Starting 20s into a 60s track leaves 40s of audio to cover.
        plan.Request.TargetDuration.ShouldBe(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task AnOffsetPastTheEndOfTheTrackIsRefused()
    {
        var (harness, source) = Setup();
        var track = harness.AddAudio("bed.wav", 30);

        var request = PipelineHarness.Request(source) with
        {
            Audio = AudioOptions.FromFile(track) with
            {
                MatchDuration = true,
                Offset = TimeSpan.FromSeconds(45),
            },
        };

        var ex = await Should.ThrowAsync<RenderPlanningException>(() => harness.PlanAsync(request));

        ex.Message.ShouldContain("45");
        ex.Message.ShouldContain("30");
        ex.Message.ShouldContain("bed.wav");
    }

    [Fact]
    public async Task AnOffsetExactlyAtTheEndIsRefused()
    {
        var (harness, source) = Setup();
        var track = harness.AddAudio("bed.wav", 30);

        var request = PipelineHarness.Request(source) with
        {
            Audio = AudioOptions.FromFile(track) with
            {
                MatchDuration = true,
                Offset = TimeSpan.FromSeconds(30),
            },
        };

        // Zero remaining audio is as useless as negative, and would otherwise plan a 0s render.
        await Should.ThrowAsync<RenderPlanningException>(() => harness.PlanAsync(request));
    }

    [Fact]
    public async Task WithoutMatchAudioTheDurationIsLeftAlone()
    {
        var (harness, source) = Setup();
        var track = harness.AddAudio("bed.wav", 47);

        var request = PipelineHarness.Request(source, durationSeconds: 60) with
        {
            Audio = AudioOptions.FromFile(track),
        };

        var plan = await harness.PlanAsync(request);

        plan.Request.TargetDuration.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task MatchAudioWithoutATrackIsIgnoredRatherThanCrashing()
    {
        var (harness, source) = Setup();

        // The CLI rejects this combination up front; the pipeline must not fall over if some
        // other caller constructs it.
        var request = PipelineHarness.Request(source, durationSeconds: 60) with
        {
            Audio = new AudioOptions { MatchDuration = true },
        };

        var plan = await harness.PlanAsync(request);

        plan.Request.TargetDuration.ShouldBe(TimeSpan.FromSeconds(60));
    }

    // --- Mode plumbing -----------------------------------------------------

    [Theory]
    [InlineData(AudioMode.Mute, true)]
    [InlineData(AudioMode.Source, false)]
    [InlineData(AudioMode.External, false)]
    public void MuteIsAPassthroughOverTheMode(AudioMode mode, bool expected)
    {
        var request = PipelineHarness.Request("/movies/film.mkv") with
        {
            Audio = new AudioOptions { Mode = mode, ExternalPath = "/audio/bed.wav" },
        };

        request.Mute.ShouldBe(expected);
    }

    [Theory]
    [InlineData(AudioMode.Mute, false)]
    [InlineData(AudioMode.Source, true)]
    [InlineData(AudioMode.External, false)]
    public void OnlySourceModeExtractsSegmentAudio(AudioMode mode, bool expected)
    {
        // External audio replaces the per-segment audio wholesale, so extracting and encoding
        // the source audio for every clip would be work thrown away.
        new AudioOptions { Mode = mode, ExternalPath = "/audio/bed.wav" }
            .UsesSegmentAudio.ShouldBe(expected);
    }

    [Fact]
    public void AnExternalModeWithNoPathIsNotAnExternalTrack()
    {
        new AudioOptions { Mode = AudioMode.External }.HasExternalTrack.ShouldBeFalse();
        new AudioOptions { Mode = AudioMode.External, ExternalPath = "  " }
            .HasExternalTrack.ShouldBeFalse();
        AudioOptions.FromFile("/audio/bed.wav").HasExternalTrack.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnExternalModeWithNoPathCountsAsMuted(string? path)
    {
        // Otherwise the three questions disagree: not muted, no segment audio, no external
        // track. Every consumer would be told audio is wanted and none would have a stream to
        // take it from, producing a file that is silently silent while claiming otherwise.
        var audio = new AudioOptions { Mode = AudioMode.External, ExternalPath = path };

        audio.IsMuted.ShouldBeTrue();
        audio.UsesSegmentAudio.ShouldBeFalse();
        audio.HasExternalTrack.ShouldBeFalse();
    }

    [Fact]
    public void AnExternalTrackWithAPathIsNotMuted() =>
        AudioOptions.FromFile("/audio/bed.wav").IsMuted.ShouldBeFalse();

    [Fact]
    public void VolumeIsOnlyConsideredChangedWhenItActuallyIs()
    {
        AudioOptions.FromFile("/a.wav").AltersVolume.ShouldBeFalse();
        (AudioOptions.FromFile("/a.wav") with { Volume = 1.0001d }).AltersVolume.ShouldBeFalse();
        (AudioOptions.FromFile("/a.wav") with { Volume = 0.5d }).AltersVolume.ShouldBeTrue();
    }
}
