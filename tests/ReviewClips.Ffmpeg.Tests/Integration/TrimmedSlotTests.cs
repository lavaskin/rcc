using Microsoft.Extensions.Logging.Abstractions;
using ReviewClips.Core.Media;
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
/// One encode filling slots of different lengths, as <c>--max-clips</c> produces when the final
/// slot is resized to land on the runtime. The shorter slots play the opening of the file, and
/// both stitchers have to cut it on the planned frame.
/// <para>
/// Encoded at <c>veryfast</c>, not the <c>ultrafast</c> the other round trips use: ultrafast
/// disables B-frames, and B-frames in the neighbouring encodes are the condition under which a
/// stream-copy cut by decode timestamp goes wrong.
/// </para>
/// </summary>
[Collection(FfmpegTestGroup.Name)]
public class TrimmedSlotTests
{
    private const double Fps = 30d;

    private readonly FfmpegFixture _fixture;

    public TrimmedSlotTests(FfmpegFixture fixture) => _fixture = fixture;

    private static OutputFormat SmallFormat => OutputFormat.Youtube with
    {
        Width = 320,
        Height = 180,
        FrameRate = Fps,
    };

    /// <summary>
    /// The trimmed final slot: clip A fills a whole slot, then the final slot plays only its
    /// opening 1.23s.
    /// </summary>
    [Fact]
    public async Task ConcatDemuxer_CutsATrimmedFinalSlotOnTheFrame()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (a, b, encoder) = await ExtractPairAsync(mute: true);
        double[] slots = [2, 2, 1.23];

        var output = await StitchAsync(
            new ConcatDemuxerStitcher(_fixture.Runner, NullLogger<ConcatDemuxerStitcher>.Instance),
            Request([a, b, a], slots, [2, 2, 2], TransitionKind.None, encoder, AudioOptions.Muted));

        (await _fixture.FrameCountOfAsync(output)).ShouldBe(ExpectedFrames(slots));
    }

    /// <summary>
    /// The lengthened final slot: the encode is the long one, and every earlier appearance is
    /// trimmed mid-render, where an overrun would overlap the next clip.
    /// </summary>
    [Fact]
    public async Task ConcatDemuxer_CutsTrimmedSlotsMidRenderOnTheFrame()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (a, b, encoder) = await ExtractPairAsync(mute: false);
        double[] slots = [1.23, 2, 1.23, 2, 2];

        var output = await StitchAsync(
            new ConcatDemuxerStitcher(_fixture.Runner, NullLogger<ConcatDemuxerStitcher>.Instance),
            Request([a, b, a, b, a], slots, [2, 2, 2, 2, 2], TransitionKind.None, encoder, AudioOptions.FromSource()));

        (await _fixture.FrameCountOfAsync(output)).ShouldBe(ExpectedFrames(slots));
        (await _fixture.DurationOfAsync(output)).ShouldBe(slots.Sum(), 0.1);
    }

    [Fact]
    public async Task FilterGraph_TrimsSlotsUnderTransitions()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (a, b, encoder) = await ExtractPairAsync(mute: false);
        double[] slots = [1.23, 2, 2];

        var output = await StitchAsync(
            new FilterGraphStitcher(_fixture.Runner, NullLogger<FilterGraphStitcher>.Instance),
            Request([a, b, a], slots, [2, 2, 2], TransitionKind.DipToBlack, encoder, AudioOptions.FromSource()));

        // 5.23s of material less two 0.4s transitions. Untrimmed, the first slot would run 2s and
        // the xfade offsets planned from 1.23s would cut into it early.
        (await _fixture.DurationOfAsync(output)).ShouldBe(4.43, 0.1);
    }

    [Fact]
    public async Task FilterGraph_TrimsSlotsOnHardCuts()
    {
        Assert.SkipUnless(_fixture.Available, "FFmpeg is not installed.");

        var (a, b, encoder) = await ExtractPairAsync(mute: true);
        double[] slots = [2, 1.23, 2, 1.23];

        // Fades force the filter graph with no transition overlap, so frames simply add up.
        var output = await StitchAsync(
            new FilterGraphStitcher(_fixture.Runner, NullLogger<FilterGraphStitcher>.Instance),
            Request([a, b, a, b], slots, [2, 2, 2, 2], TransitionKind.None, encoder, AudioOptions.Muted, fade: 0.3));

        (await _fixture.FrameCountOfAsync(output)).ShouldBe(ExpectedFrames(slots));
    }

    private static int ExpectedFrames(double[] slots) =>
        slots.Sum(s => FfmpegSegmentExtractor.FrameCount(TimeSpan.FromSeconds(s), Fps));

    /// <summary>
    /// Clip A is the shared, trimmed encode; clip B is an ordinary one with B-frames, so the
    /// stream copy joins encodes of both kinds as a real render does.
    /// </summary>
    private async Task<(string A, string B, EncoderProfile Encoder)> ExtractPairAsync(bool mute)
    {
        var probe = new FfprobeMediaProbe(_fixture.Runner, NullLogger<FfprobeMediaProbe>.Instance);
        var info = await probe.ProbeAsync(_fixture.SimpleClip, TestContext.Current.CancellationToken);

        var encoder = await new FfmpegEncoderSelector(_fixture.EncoderProbe, NullLogger<FfmpegEncoderSelector>.Instance)
            .SelectAsync(
                new EncoderOptions { Preference = EncoderPreference.X264, Preset = "veryfast" },
                TestContext.Current.CancellationToken);

        var a = await ExtractAsync(info, encoder, start: 1, trimmed: true, mute);
        var b = await ExtractAsync(info, encoder, start: 5, trimmed: false, mute);

        return (a, b, encoder);
    }

    private async Task<string> ExtractAsync(MediaInfo info, EncoderProfile encoder, double start, bool trimmed, bool mute)
    {
        var output = _fixture.PathFor($"trim_{Guid.NewGuid():N}.mp4");

        await new FfmpegSegmentExtractor(
                _fixture.Runner,
                VideoFilterGraphBuilder.CreateDefault(),
                NullLogger<FfmpegSegmentExtractor>.Instance)
            .ExtractAsync(
                new SegmentExtractionRequest
                {
                    Segment = new Segment
                    {
                        SourcePath = info.Path,
                        Start = TimeSpan.FromSeconds(start),
                        Duration = TimeSpan.FromSeconds(2),
                    },
                    Source = info,
                    OutputPath = output,
                    Format = SmallFormat,
                    Look = LookOptions.None,
                    Encoder = encoder,
                    EncoderOptions = new EncoderOptions { Preference = EncoderPreference.X264 },
                    Mute = mute,
                    Trimmed = trimmed,
                },
                progress: null,
                TestContext.Current.CancellationToken);

        return output;
    }

    private static async Task<string> StitchAsync(IStitcher stitcher, StitchRequest request)
    {
        stitcher.CanHandle(request).ShouldBeTrue();
        await stitcher.StitchAsync(request, null, TestContext.Current.CancellationToken);
        return request.OutputPath;
    }

    private StitchRequest Request(
        IReadOnlyList<string> paths,
        double[] slots,
        double[] encoded,
        TransitionKind kind,
        EncoderProfile encoder,
        AudioOptions audio,
        double fade = 0)
    {
        var output = _fixture.PathFor($"trim_out_{Guid.NewGuid():N}.mp4");

        return new StitchRequest
        {
            SegmentPaths = paths,
            SegmentDurations = [.. slots.Select(TimeSpan.FromSeconds)],
            EncodedDurations = [.. encoded.Select(TimeSpan.FromSeconds)],
            OutputPath = output,
            Transition = new TransitionOptions
            {
                Kind = kind,
                Duration = TimeSpan.FromSeconds(0.4),
                FadeIn = TimeSpan.FromSeconds(fade),
                FadeOut = TimeSpan.FromSeconds(fade),
            },
            Format = SmallFormat,
            Encoder = encoder,
            EncoderOptions = new EncoderOptions { Preference = EncoderPreference.X264 },
            Audio = audio,

            // Each test needs its own concat list; they would otherwise share segments.txt.
            WorkingDirectory = System.IO.Directory.CreateDirectory(
                Path.Combine(_fixture.Directory, $"trim_work_{Guid.NewGuid():N}")).FullName,
        };
    }
}
