using Microsoft.Extensions.Logging.Abstractions;
using ReviewClips.Core.Options;
using ReviewClips.Core.Pipeline;
using ReviewClips.Core.Selection;
using ReviewClips.Ffmpeg.Encoding;
using ReviewClips.Ffmpeg.Extraction;
using ReviewClips.Ffmpeg.Filters;
using ReviewClips.Ffmpeg.Probe;
using ReviewClips.Ffmpeg.Stitching;

namespace ReviewClips.Ffmpeg.Tests.Integration;

/// <summary>
/// <c>--audio</c> in source mode against footage that has no audio stream.
/// <para>
/// Every segment must come out with the same stream layout, whatever its source had. The concat
/// demuxer needs that for a clean stream copy, and the filter graph references <c>[k:a]</c> for
/// every input and refuses to bind if one input lacks it. Writing a bare <c>-an</c> segment for a
/// silent source broke both, and did so only at the stitch — after every segment had been
/// encoded.
/// </para>
/// </summary>
[Collection(FfmpegTestGroup.Name)]
public class SilentSourceAudioTests
{
    private readonly FfmpegFixture _fixture;

    public SilentSourceAudioTests(FfmpegFixture fixture) => _fixture = fixture;

    private static EncoderProfile Encoder => new()
    {
        VideoEncoder = "libx264",
        IsHardware = false,
        QualityArguments = ["-crf", "30", "-preset", "ultrafast"],
        ExtraArguments = [],
    };

    private async Task<string> ExtractAsync(string sourcePath, double start, double duration, string name)
    {
        var probe = new FfprobeMediaProbe(_fixture.Runner, NullLogger<FfprobeMediaProbe>.Instance);
        var info = await probe.ProbeAsync(sourcePath, TestContext.Current.CancellationToken);

        var extractor = new FfmpegSegmentExtractor(
            _fixture.Runner,
            VideoFilterGraphBuilder.CreateDefault(),
            NullLogger<FfmpegSegmentExtractor>.Instance);

        var output = _fixture.PathFor(name);

        await extractor.ExtractAsync(
            new SegmentExtractionRequest
            {
                Segment = new Segment
                {
                    SourcePath = sourcePath,
                    Start = TimeSpan.FromSeconds(start),
                    Duration = TimeSpan.FromSeconds(duration),
                },
                Source = info,
                OutputPath = output,
                Format = OutputFormat.Youtube with { Width = 320, Height = 180 },
                Look = LookOptions.None,
                Encoder = Encoder,
                EncoderOptions = new EncoderOptions { Preference = EncoderPreference.X264 },

                // Source-audio mode: the pipeline sets this from AudioOptions.UsesSegmentAudio.
                Mute = false,
            },
            progress: null,
            TestContext.Current.CancellationToken);

        return output;
    }

    /// <summary>
    /// SimpleClip is synthesised from testsrc2 and carries no audio at all, which is the case
    /// that used to produce a segment with no audio stream.
    /// </summary>
    [Fact]
    public async Task ASilentSourceStillYieldsASegmentWithAnAudioStream()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var segment = await ExtractAsync(_fixture.SimpleClip, 1, 2, "silent_src_segment.mp4");

        (await _fixture.AudioStreamCountAsync(segment)).ShouldBe(1);

        // And it is the right length, so it cannot desynchronise the join.
        (await _fixture.AudioDurationOfAsync(segment)).ShouldBe(2d, 0.25d);
    }

    /// <summary>
    /// The failure as the user met it: extraction succeeded and the stitch then died on
    /// "Stream specifier ':a' ... matches no streams".
    /// </summary>
    [Fact]
    public async Task ASilentSourceCanBeStitchedInSourceAudioMode()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var segments = new List<string>
        {
            await ExtractAsync(_fixture.SimpleClip, 0, 1.5, "silent_stitch_0.mp4"),
            await ExtractAsync(_fixture.SimpleClip, 3, 1.5, "silent_stitch_1.mp4"),
        };

        var output = _fixture.PathFor("silent_stitched.mp4");

        var request = new StitchRequest
        {
            SegmentPaths = segments,
            SegmentDurations = [TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.5)],
            OutputPath = output,
            Transition = new TransitionOptions
            {
                Kind = TransitionKind.Fade,
                Duration = TimeSpan.FromSeconds(0.4),
            },
            Format = OutputFormat.Youtube with { Width = 320, Height = 180 },
            Encoder = Encoder,
            EncoderOptions = new EncoderOptions { Preference = EncoderPreference.X264 },
            Audio = AudioOptions.FromSource(),
            WorkingDirectory = _fixture.Directory,
        };

        await new FilterGraphStitcher(_fixture.Runner, NullLogger<FilterGraphStitcher>.Instance)
            .StitchAsync(request, progress: null, TestContext.Current.CancellationToken);

        (await _fixture.AudioStreamCountAsync(output)).ShouldBe(1);
    }

    /// <summary>
    /// The mixed set, which is the case a plan-time refusal could not have handled: one source has
    /// audio and the other does not, and the render has to take both.
    /// </summary>
    [Fact]
    public async Task ASetMixingSilentAndSoundedSourcesStitches()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        // A clip that really does carry audio, to sit alongside the silent one.
        var sounded = _fixture.PathFor("sounded_source.mp4");
        var built = await _fixture.RunAsync(
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30",
            "-f", "lavfi", "-i", "sine=frequency=440",
            "-t", "6",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            sounded,
        ]);

        built.Success.ShouldBeTrue(built.StandardError);

        var segments = new List<string>
        {
            await ExtractAsync(_fixture.SimpleClip, 0, 1.5, "mixed_silent.mp4"),
            await ExtractAsync(sounded, 0, 1.5, "mixed_sounded.mp4"),
        };

        // Both segments have to agree on layout, or neither stitcher can join them.
        foreach (var segment in segments)
        {
            (await _fixture.AudioStreamCountAsync(segment)).ShouldBe(1);
        }

        var output = _fixture.PathFor("mixed_stitched.mp4");

        var request = new StitchRequest
        {
            SegmentPaths = segments,
            SegmentDurations = [TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.5)],
            OutputPath = output,
            Transition = new TransitionOptions
            {
                Kind = TransitionKind.Fade,
                Duration = TimeSpan.FromSeconds(0.4),
            },
            Format = OutputFormat.Youtube with { Width = 320, Height = 180 },
            Encoder = Encoder,
            EncoderOptions = new EncoderOptions { Preference = EncoderPreference.X264 },
            Audio = AudioOptions.FromSource(),
            WorkingDirectory = _fixture.Directory,
        };

        await new FilterGraphStitcher(_fixture.Runner, NullLogger<FilterGraphStitcher>.Instance)
            .StitchAsync(request, progress: null, TestContext.Current.CancellationToken);

        (await _fixture.AudioStreamCountAsync(output)).ShouldBe(1);
    }
}
