using Microsoft.Extensions.Logging.Abstractions;
using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Ffmpeg.Encoding;
using ReviewClips.Ffmpeg.Probe;
using ReviewClips.Ffmpeg.Stitching;

namespace ReviewClips.Ffmpeg.Tests.Integration;

/// <summary>
/// Muxing an external track, run for real through both stitchers.
/// <para>
/// Both paths are covered deliberately. They build entirely different command lines — one is a
/// stream copy, the other a filter graph — and the whole value of the feature is that the
/// result is the same either way.
/// </para>
/// </summary>
[Collection(FfmpegTestGroup.Name)]
public class ExternalAudioTests
{
    private readonly FfmpegFixture _fixture;

    public ExternalAudioTests(FfmpegFixture fixture) => _fixture = fixture;

    /// <summary>Four 1.5s segments, i.e. 6s of video against the fixture's 6s track.</summary>
    private async Task<IReadOnlyList<string>> SegmentsAsync(string prefix)
    {
        var paths = new List<string>();

        for (var i = 0; i < 4; i++)
        {
            var path = _fixture.PathFor($"{prefix}_{i}.mp4");

            var result = await _fixture.RunAsync(
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", (i * 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-t", "1.5",
                "-i", _fixture.SimpleClip,
                "-an",
                "-c:v", "libx264", "-preset", "ultrafast",
                "-pix_fmt", "yuv420p",
                "-vf", "scale=320:180,fps=30",
                "-video_track_timescale", "90000",
                path,
            ]);

            result.Success.ShouldBeTrue(result.StandardError);
            paths.Add(path);
        }

        return paths;
    }

    private StitchRequest Request(
        IReadOnlyList<string> segments,
        string output,
        TransitionKind transition,
        AudioOptions audio) => new()
        {
            SegmentPaths = segments,
            SegmentDurations = [.. segments.Select(_ => TimeSpan.FromSeconds(1.5))],
            OutputPath = output,
            Transition = new TransitionOptions
            {
                Kind = transition,
                Duration = TimeSpan.FromSeconds(0.4),
            },
            Format = OutputFormat.Youtube with { Width = 320, Height = 180 },
            Encoder = new EncoderProfile
            {
                VideoEncoder = "libx264",
                IsHardware = false,
                QualityArguments = ["-crf", "30", "-preset", "ultrafast"],
                ExtraArguments = [],
            },
            EncoderOptions = new EncoderOptions { Preference = EncoderPreference.X264 },
            Audio = audio,
            WorkingDirectory = _fixture.Directory,
        };

    private ConcatDemuxerStitcher Concat() =>
        new(_fixture.Runner, NullLogger<ConcatDemuxerStitcher>.Instance);

    private FilterGraphStitcher Graph() =>
        new(_fixture.Runner, NullLogger<FilterGraphStitcher>.Instance);

    // --- Probing -----------------------------------------------------------

    [Fact]
    public async Task ProbeDurationReadsAnAudioOnlyFile()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var probe = new FfprobeMediaProbe(_fixture.Runner, NullLogger<FfprobeMediaProbe>.Instance);

        var duration = await probe.ProbeDurationAsync(
            _fixture.AudioTrack,
            TestContext.Current.CancellationToken);

        duration.TotalSeconds.ShouldBe(6d, 0.1d);
    }

    [Fact]
    public async Task FullProbeStillRefusesAnAudioOnlyFile()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var probe = new FfprobeMediaProbe(_fixture.Runner, NullLogger<FfprobeMediaProbe>.Instance);

        // This is exactly why ProbeDurationAsync exists: MediaInfo requires a video stream, so
        // a .wav cannot be described by it and must not silently produce a degenerate one.
        await Should.ThrowAsync<Process.FfmpegExecutionException>(
            () => probe.ProbeAsync(_fixture.AudioTrack, TestContext.Current.CancellationToken));
    }

    // --- Concat demuxer path -----------------------------------------------

    [Fact]
    public async Task ConcatStitcherMuxesTheTrack()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var segments = await SegmentsAsync("concat_audio");
        var output = _fixture.PathFor("concat_with_audio.mp4");

        await Concat().StitchAsync(
            Request(segments, output, TransitionKind.None, AudioOptions.FromFile(_fixture.AudioTrack)),
            progress: null,
            TestContext.Current.CancellationToken);

        (await _fixture.HasAudioStreamAsync(output)).ShouldBeTrue();
        (await _fixture.DurationOfAsync(output)).ShouldBe(6d, 0.3d);
    }

    [Fact]
    public async Task ConcatStitcherStillStreamCopiesTheVideo()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var segments = await SegmentsAsync("concat_copy");
        var request = Request(
            segments,
            _fixture.PathFor("copy_check.mp4"),
            TransitionKind.None,
            AudioOptions.FromFile(_fixture.AudioTrack));

        var arguments = string.Join(' ', Concat().DescribeArguments(request));

        // The fast path is the reason this stitcher exists. Muxing audio must not cost a video
        // re-encode, or --transition none stops being free.
        arguments.ShouldContain("-c:v copy");
        arguments.ShouldNotContain("libx264");
        arguments.ShouldContain("-map 0:v");
        arguments.ShouldContain("-map 1:a");
        arguments.ShouldContain("-shortest");
    }

    [Fact]
    public async Task ConcatStitcherAppliesOffsetAndVolume()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var segments = await SegmentsAsync("concat_offset");
        var output = _fixture.PathFor("concat_offset.mp4");

        var audio = AudioOptions.FromFile(_fixture.AudioTrack) with
        {
            Offset = TimeSpan.FromSeconds(2),
            Volume = 0.5d,
        };

        var request = Request(segments, output, TransitionKind.None, audio);
        var arguments = string.Join(' ', Concat().DescribeArguments(request));

        // -ss must precede the audio input to seek it rather than the concat list.
        arguments.ShouldContain("-ss 2 -i");
        arguments.ShouldContain("volume=0.5");

        await Concat().StitchAsync(request, progress: null, TestContext.Current.CancellationToken);

        // 6s of video against 4s of remaining audio: -shortest ends it at 4s.
        (await _fixture.DurationOfAsync(output)).ShouldBe(4d, 0.3d);
    }

    [Fact]
    public async Task ConcatStitcherWritesNoAudioWhenMuted()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var segments = await SegmentsAsync("concat_mute");
        var output = _fixture.PathFor("concat_muted.mp4");

        await Concat().StitchAsync(
            Request(segments, output, TransitionKind.None, AudioOptions.Muted),
            progress: null,
            TestContext.Current.CancellationToken);

        (await _fixture.HasAudioStreamAsync(output)).ShouldBeFalse();
    }

    // --- Filter graph path -------------------------------------------------

    [Fact]
    public async Task FilterGraphStitcherMuxesTheTrack()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var segments = await SegmentsAsync("graph_audio");
        var output = _fixture.PathFor("graph_with_audio.mp4");

        await Graph().StitchAsync(
            Request(segments, output, TransitionKind.Fade, AudioOptions.FromFile(_fixture.AudioTrack)),
            progress: null,
            TestContext.Current.CancellationToken);

        (await _fixture.HasAudioStreamAsync(output)).ShouldBeTrue();

        // Four 1.5s clips joined by three 0.4s crossfades: 6 - 1.2 = 4.8s of video, which is
        // shorter than the 6s track, so -shortest lands the file on the video.
        (await _fixture.DurationOfAsync(output)).ShouldBe(4.8d, 0.4d);
    }

    [Fact]
    public async Task FilterGraphStitcherMapsTheTrackInsteadOfCrossfadingSegments()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var segments = await SegmentsAsync("graph_map");
        var request = Request(
            segments,
            _fixture.PathFor("graph_map.mp4"),
            TransitionKind.Fade,
            AudioOptions.FromFile(_fixture.AudioTrack));

        var arguments = string.Join(' ', Graph().DescribeArguments(request));

        // The external track is one continuous stream, so acrossfade — which exists to blend
        // neighbouring clips' own audio — must not appear.
        arguments.ShouldNotContain("acrossfade");
        arguments.ShouldNotContain("[aout]");

        // It is the last input, after all four segments.
        arguments.ShouldContain("-map 4:a");
    }

    [Fact]
    public async Task FilterGraphStitcherKeepsSegmentAudioInSourceMode()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var segments = await SegmentsAsync("graph_source");
        var request = Request(
            segments,
            _fixture.PathFor("graph_source.mp4"),
            TransitionKind.Fade,
            AudioOptions.FromSource());

        var arguments = string.Join(' ', Graph().DescribeArguments(request));

        // Source mode is the one case that does want the per-segment chain.
        arguments.ShouldContain("acrossfade");
        arguments.ShouldContain("[aout]");
    }

    // --- Fades --------------------------------------------------------------

    [Fact]
    public async Task FilterGraphStitcherFadesTheTrackWithThePicture()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var segments = await SegmentsAsync("graph_afade");
        var request = Request(
            segments,
            _fixture.PathFor("graph_afade.mp4"),
            TransitionKind.Fade,
            AudioOptions.FromFile(_fixture.AudioTrack)) with
        {
            Transition = new TransitionOptions
            {
                Kind = TransitionKind.Fade,
                Duration = TimeSpan.FromSeconds(0.4),
                FadeIn = TimeSpan.FromSeconds(0.5),
                FadeOut = TimeSpan.FromSeconds(0.5),
            },
        };

        var arguments = string.Join(' ', Graph().DescribeArguments(request));

        // A bed still at full volume over a picture fading to black is the most audible way for
        // a render to sound unfinished, and the fades have to line up with the video's.
        arguments.ShouldContain("afade=t=in:st=0:d=0.5");
        arguments.ShouldContain("fade=t=in:st=0:d=0.5");

        // Four 1.5s clips with three 0.4s crossfades: 4.8s, so the closing fade starts at 4.3s.
        arguments.ShouldContain("afade=t=out:st=4.3:d=0.5");
        arguments.ShouldContain("fade=t=out:st=4.3:d=0.5");

        await Graph().StitchAsync(request, progress: null, TestContext.Current.CancellationToken);

        (await _fixture.HasAudioStreamAsync(request.OutputPath)).ShouldBeTrue();
    }

    [Fact]
    public async Task VolumeAndFadeAreCombinedIntoOneAudioFilterChain()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var segments = await SegmentsAsync("graph_vol_fade");
        var request = Request(
            segments,
            _fixture.PathFor("graph_vol_fade.mp4"),
            TransitionKind.None,
            AudioOptions.FromFile(_fixture.AudioTrack) with { Volume = 0.5d }) with
        {
            Transition = new TransitionOptions
            {
                Kind = TransitionKind.None,
                FadeIn = TimeSpan.FromSeconds(0.5),
                FadeOut = TimeSpan.Zero,
            },
        };

        var arguments = string.Join(' ', Graph().DescribeArguments(request));

        // One -filter:a, not two. A second would silently replace the first.
        arguments.Split("-filter:a").Length.ShouldBe(2);
        arguments.ShouldContain("volume=0.5,afade=t=in:st=0:d=0.5");

        await Graph().StitchAsync(request, progress: null, TestContext.Current.CancellationToken);

        (await _fixture.HasAudioStreamAsync(request.OutputPath)).ShouldBeTrue();
    }

    [Fact]
    public async Task NoFadesMeansNoAfade()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var segments = await SegmentsAsync("graph_nofade");
        var request = Request(
            segments,
            _fixture.PathFor("graph_nofade.mp4"),
            TransitionKind.Fade,
            AudioOptions.FromFile(_fixture.AudioTrack)) with
        {
            // TransitionOptions defaults both fades to 0.5s, so they have to be turned off
            // explicitly rather than merely left unmentioned.
            Transition = new TransitionOptions
            {
                Kind = TransitionKind.Fade,
                Duration = TimeSpan.FromSeconds(0.4),
                FadeIn = TimeSpan.Zero,
                FadeOut = TimeSpan.Zero,
            },
        };

        string.Join(' ', Graph().DescribeArguments(request)).ShouldNotContain("afade");
    }

    [Fact]
    public async Task BothStitchersAgreeOnTheResult()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var segments = await SegmentsAsync("agree");
        var audio = AudioOptions.FromFile(_fixture.AudioTrack);

        var concatOutput = _fixture.PathFor("agree_concat.mp4");
        var graphOutput = _fixture.PathFor("agree_graph.mp4");

        await Concat().StitchAsync(
            Request(segments, concatOutput, TransitionKind.None, audio),
            progress: null,
            TestContext.Current.CancellationToken);

        // No transitions, so the graph path produces the same 6s timeline the copy path does.
        await Graph().StitchAsync(
            Request(segments, graphOutput, TransitionKind.None, audio),
            progress: null,
            TestContext.Current.CancellationToken);

        (await _fixture.HasAudioStreamAsync(concatOutput)).ShouldBeTrue();
        (await _fixture.HasAudioStreamAsync(graphOutput)).ShouldBeTrue();

        var concatDuration = await _fixture.DurationOfAsync(concatOutput);
        var graphDuration = await _fixture.DurationOfAsync(graphOutput);

        concatDuration.ShouldBe(graphDuration, 0.4d);
    }
}
