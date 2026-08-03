using ReviewClips.Ffmpeg.Analysis;
using ReviewClips.Ffmpeg.Process;

namespace ReviewClips.Ffmpeg.Tests.Analysis;

/// <summary>
/// Exercised against the exact stderr shapes emitted by FFmpeg 8.1. These strings are a
/// contract with an external tool, so they are reproduced verbatim.
/// </summary>
public class AnalysisLogParserTests
{
    [Fact]
    public void ParsesSceneCutTimestamps()
    {
        var parser = new AnalysisLogParser();

        parser.Feed("[Parsed_scdet_2 @ 0x7f457c011a00] lavfi.scd.score: 36.217, lavfi.scd.time: 4");
        parser.Feed("[Parsed_scdet_2 @ 0x7f457c011a00] lavfi.scd.score: 18.900, lavfi.scd.time: 12.5");

        parser.SceneCuts.Select(c => c.TotalSeconds).ShouldBe([4d, 12.5d]);
    }

    [Fact]
    public void ParsesBlackRanges()
    {
        var parser = new AnalysisLogParser();

        parser.Feed("[Parsed_blackdetect_4 @ 0x7f457c011f80] black_start:4 black_end:6.75 black_duration:2.75");

        var range = parser.BlackRanges.ShouldHaveSingleItem();
        range.Start.TotalSeconds.ShouldBe(4d);
        range.End.TotalSeconds.ShouldBe(6.75d);
    }

    [Fact]
    public void ParsesFreezeRangesFromSeparateStartAndEndLines()
    {
        var parser = new AnalysisLogParser();

        parser.Feed("[Parsed_freezedetect_3 @ 0x1] lavfi.freezedetect.freeze_start: 10");
        parser.Feed("[Parsed_freezedetect_3 @ 0x1] lavfi.freezedetect.freeze_duration: 5");
        parser.Feed("[Parsed_freezedetect_3 @ 0x1] lavfi.freezedetect.freeze_end: 15");

        var range = parser.FreezeRanges.ShouldHaveSingleItem();
        range.Start.TotalSeconds.ShouldBe(10d);
        range.End.TotalSeconds.ShouldBe(15d);
    }

    [Fact]
    public void ClosesAFreezeThatRunsToEndOfStream()
    {
        // freezedetect reports freeze_start immediately but only reports freeze_end when
        // motion resumes, so a static final shot never gets one.
        var parser = new AnalysisLogParser();

        parser.Feed("[Parsed_freezedetect_3 @ 0x1] lavfi.freezedetect.freeze_start: 80");
        parser.FreezeRanges.ShouldBeEmpty();

        parser.Complete(TimeSpan.FromSeconds(98));

        var range = parser.FreezeRanges.ShouldHaveSingleItem();
        range.Start.TotalSeconds.ShouldBe(80d);
        range.End.TotalSeconds.ShouldBe(98d);
    }

    [Fact]
    public void CompleteIsIdempotentWhenNoFreezeIsPending()
    {
        var parser = new AnalysisLogParser();

        parser.Complete(TimeSpan.FromSeconds(60));
        parser.Complete(TimeSpan.FromSeconds(60));

        parser.FreezeRanges.ShouldBeEmpty();
    }

    [Fact]
    public void IgnoresUnrelatedOutput()
    {
        var parser = new AnalysisLogParser();

        parser.Feed("  Duration: 00:01:38.00, start: 0.000000, bitrate: 232 kb/s");
        parser.Feed("frame=   12 fps=0.0 q=-0.0 Lsize=N/A time=00:00:03.00");
        parser.Feed(string.Empty);

        parser.SceneCuts.ShouldBeEmpty();
        parser.BlackRanges.ShouldBeEmpty();
        parser.FreezeRanges.ShouldBeEmpty();
    }

    [Fact]
    public void IgnoresDegenerateBlackRanges()
    {
        var parser = new AnalysisLogParser();

        parser.Feed("[blackdetect] black_start:10 black_end:10 black_duration:0");

        parser.BlackRanges.ShouldBeEmpty();
    }
}

/// <summary>
/// The metadata stream is read from stdout rather than a file because FFmpeg truncates
/// <c>file=</c> targets whenever the filter graph reinitialises. These tests pin the behaviour
/// that made that bug visible: frame counters restart, so only <c>pts_time</c> can be trusted.
/// </summary>
public class MetadataStreamParserTests
{
    [Fact]
    public void ExtractsMafdSamplesAgainstFrameTimestamps()
    {
        var parser = new MetadataStreamParser();

        parser.Feed("frame:0    pts:0       pts_time:0");
        parser.Feed("lavfi.scd.mafd=0.000");
        parser.Feed("lavfi.scd.score=0.000");
        parser.Feed("frame:1    pts:512     pts_time:0.25");
        parser.Feed("lavfi.scd.mafd=0.899");
        parser.Feed("lavfi.scd.score=0.899");

        var samples = parser.ToCurve().Samples;

        samples.Count.ShouldBe(2);
        samples[0].AtSeconds.ShouldBe(0d);
        samples[1].AtSeconds.ShouldBe(0.25d);
        samples[1].Mafd.ShouldBe(0.899d, 0.0001);
    }

    [Fact]
    public void UsesPtsTimeEvenWhenFrameCountersRestart()
    {
        // This is exactly what a mid-stream filter reinitialisation looks like: the frame index
        // resets to 0 while pts_time continues from where it left off.
        var parser = new MetadataStreamParser();

        parser.Feed("frame:143  pts:391  pts_time:97.75");
        parser.Feed("lavfi.scd.mafd=1.5");
        parser.Feed("frame:0    pts:400  pts_time:100");
        parser.Feed("lavfi.scd.mafd=2.5");

        var samples = parser.ToCurve().Samples;

        samples.Select(s => s.AtSeconds).ShouldBe([97.75d, 100d]);
    }

    [Fact]
    public void TracksCurrentTimeForProgressReporting()
    {
        var parser = new MetadataStreamParser();

        parser.CurrentTimeSeconds.ShouldBeNull();

        parser.Feed("frame:20   pts:100  pts_time:42.5");

        parser.CurrentTimeSeconds.ShouldBe(42.5d);
    }

    [Fact]
    public void IgnoresMetadataArrivingBeforeAnyFrameHeader()
    {
        var parser = new MetadataStreamParser();

        parser.Feed("lavfi.scd.mafd=1.234");

        parser.SampleCount.ShouldBe(0);
    }

    [Fact]
    public void IgnoresOtherMetadataKeys()
    {
        var parser = new MetadataStreamParser();

        parser.Feed("frame:0 pts:0 pts_time:0");
        parser.Feed("lavfi.scd.score=12.5");
        parser.Feed("lavfi.freezedetect.freeze_start=0");

        parser.SampleCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("frame:0    pts:0       pts_time:0", 0d)]
    [InlineData("frame:12   pts:512     pts_time:3.25", 3.25d)]
    [InlineData("frame:1 pts:1 pts_time:1.5 extra:stuff", 1.5d)]
    public void ExtractPtsTime_ReadsTheTimestampField(string line, double expected) =>
        MetadataStreamParser.ExtractPtsTime(line).ShouldBe(expected);

    [Fact]
    public void ExtractPtsTime_ReturnsNullWhenAbsent() =>
        MetadataStreamParser.ExtractPtsTime("frame:0 pts:0").ShouldBeNull();
}

public class FfmpegProgressParserTests
{
    [Fact]
    public void ConvertsMicrosecondsIntoAFractionOfTheExpectedDuration()
    {
        var reported = new List<double>();
        var parser = new FfmpegProgressParser(
            TimeSpan.FromSeconds(10),
            new ReviewClips.Core.Pipeline.InlineProgress<double>(reported.Add));

        // Despite its name, FFmpeg reports out_time_ms in microseconds.
        parser.Feed("out_time_ms=5000000");

        parser.CurrentTime.TotalSeconds.ShouldBe(5d, 0.001);
        reported.ShouldContain(f => Math.Abs(f - 0.5) < 0.001);
    }

    [Fact]
    public void ReportsCompletionOnProgressEnd()
    {
        var reported = new List<double>();
        var parser = new FfmpegProgressParser(
            TimeSpan.FromSeconds(10),
            new ReviewClips.Core.Pipeline.InlineProgress<double>(reported.Add));

        parser.Feed("progress=end");

        parser.Completed.ShouldBeTrue();
        reported.ShouldContain(1d);
    }

    [Fact]
    public void ThrottlesToWholePercentages()
    {
        var reported = new List<double>();
        var parser = new FfmpegProgressParser(
            TimeSpan.FromSeconds(1000),
            new ReviewClips.Core.Pipeline.InlineProgress<double>(reported.Add));

        // 100 sub-percent steps should not produce 100 callbacks.
        for (var i = 0; i < 100; i++)
        {
            parser.Feed($"out_time_ms={i * 1000}");
        }

        reported.Count.ShouldBeLessThan(5);
    }

    [Fact]
    public void IgnoresMalformedAndUnrelatedLines()
    {
        var parser = new FfmpegProgressParser(TimeSpan.FromSeconds(10), null);

        parser.Feed("garbage");
        parser.Feed(string.Empty);
        parser.Feed("=novalue");
        parser.Feed("out_time_ms=notanumber");

        parser.CurrentTime.ShouldBe(TimeSpan.Zero);
        parser.Completed.ShouldBeFalse();
    }
}
