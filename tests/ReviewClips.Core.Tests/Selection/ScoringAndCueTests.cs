using ReviewClips.Core.Analysis;
using ReviewClips.Core.Options;
using ReviewClips.Core.Primitives;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Tests.Selection;

public class MotionCurveTests
{
    private static MotionCurve Curve(params double[] values) =>
        new(values.Select((v, i) => new MotionSample(i * 0.25, v)));

    [Fact]
    public void MeanOver_AveragesOnlySamplesInsideTheRange()
    {
        var curve = Curve(1, 1, 1, 1, 9, 9, 9, 9);

        // First second covers the four 1.0 samples.
        curve.MeanOver(new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(1)))
            .ShouldNotBeNull()
            .ShouldBe(1d, 0.001);

        curve.MeanOver(new TimeRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)))
            .ShouldNotBeNull()
            .ShouldBe(9d, 0.001);
    }

    [Fact]
    public void MeanOver_ReturnsNullWhenNoSamplesFallInTheRange() =>
        Curve(1, 2, 3)
            .MeanOver(new TimeRange(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60)))
            .ShouldBeNull();

    [Fact]
    public void Median_IsRobustToOutliers() =>
        Curve(1, 1, 1, 1, 1000).Median().ShouldBe(1d);

    [Fact]
    public void StdDevOver_IsZeroForAConstantCurve() =>
        Curve(5, 5, 5, 5)
            .StdDevOver(new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(1)))
            .ShouldNotBeNull()
            .ShouldBe(0d, 0.0001);

    [Fact]
    public void EmptyCurve_ReportsZeroMedianAndNoMean()
    {
        MotionCurve.Empty.Median().ShouldBe(0d);
        MotionCurve.Empty.MeanOver(new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(5))).ShouldBeNull();
    }
}

public class ScoredSelectorTests
{
    private static readonly ScoringOptions Default = new();

    private static MotionCurve Flat(double value, double seconds = 60) =>
        new(Enumerable.Range(0, (int)(seconds * 4)).Select(i => new MotionSample(i * 0.25, value)));

    private static TimeRange Window(double start = 0, double length = 5) =>
        TimeRange.FromDuration(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(length));

    [Fact]
    public void ScoreWindow_RejectsNearStaticFootage() =>
        ScoredSegmentSelector.ScoreWindow(Flat(0.1), Window(), median: 1.0, Default)
            .ShouldBeNull();

    [Fact]
    public void ScoreWindow_RejectsFranticFootage() =>
        ScoredSegmentSelector.ScoreWindow(Flat(10.0), Window(), median: 1.0, Default)
            .ShouldBeNull();

    [Fact]
    public void ScoreWindow_PeaksAtTheTargetMotionLevel()
    {
        var atTarget = ScoredSegmentSelector.ScoreWindow(
            Flat(Default.TargetMotionMultiple), Window(), median: 1.0, Default);

        var offTarget = ScoredSegmentSelector.ScoreWindow(
            Flat(Default.TargetMotionMultiple * 2.5), Window(), median: 1.0, Default);

        atTarget.ShouldNotBeNull();
        offTarget.ShouldNotBeNull();
        atTarget!.Value.ShouldBeGreaterThan(offTarget!.Value);
    }

    [Fact]
    public void ScoreWindow_ThresholdsAreRelativeToTheTitleMedian()
    {
        // The same absolute motion is acceptable in a calm film and excessive in a busy one.
        ScoredSegmentSelector.ScoreWindow(Flat(2.0), Window(), median: 1.6, Default).ShouldNotBeNull();
        ScoredSegmentSelector.ScoreWindow(Flat(2.0), Window(), median: 0.2, Default).ShouldBeNull();
    }

    [Fact]
    public void ScoreWindow_ReturnsNeutralWhenTheTitleHasNoMotionBaseline()
    {
        // A fully static source has a zero median; dividing by it must not reject everything.
        ScoredSegmentSelector.ScoreWindow(Flat(0), Window(), median: 0d, Default)
            .ShouldNotBeNull()
            .ShouldBe(0.5d);
    }

    [Fact]
    public void ScoreWindow_PenalisesUnevenWindows()
    {
        var steady = Flat(1.25);

        // Same mean, but wildly varying: typically a window straddling a cut or a flash.
        var spiky = new MotionCurve(
            Enumerable.Range(0, 240).Select(i => new MotionSample(i * 0.25, i % 2 == 0 ? 0.35 : 2.15)));

        var steadyScore = ScoredSegmentSelector.ScoreWindow(steady, Window(), 1.0, Default);
        var spikyScore = ScoredSegmentSelector.ScoreWindow(spiky, Window(), 1.0, Default);

        steadyScore.ShouldNotBeNull();
        spikyScore.ShouldNotBeNull();
        steadyScore!.Value.ShouldBeGreaterThan(spikyScore!.Value);
    }

    [Fact]
    public void ScoredSelector_PrefersTheMoreInterestingHalfOfAFilm()
    {
        var info = SelectionTestData.Info(600);

        // Second half sits at the ideal motion level; first half is nearly static.
        var analysis = SelectionTestData.Analysis(
            info,
            motionAt: t => t < 300 ? 0.05 : 1.25);

        var context = SelectionTestData.Context([new SourceMedia(info, analysis)], segmentCount: 6);
        var segments = SegmentSelectorFactory.CreateDefault()
            .Create(SelectionStrategy.Scored)
            .SelectSegments(context);

        segments.ShouldNotBeEmpty();
        segments.Count(s => s.Start.TotalSeconds >= 295)
            .ShouldBeGreaterThan(segments.Count(s => s.Start.TotalSeconds < 295));
    }
}

public class SceneSelectorTests
{
    [Fact]
    public void SceneSelector_StartsClipsJustAfterACut()
    {
        var info = SelectionTestData.Info(600);
        var cuts = new[] { 60d, 150d, 240d, 330d, 420d, 510d };
        var analysis = SelectionTestData.Analysis(info, cutsAtSeconds: cuts);

        var options = new SelectionOptions
        {
            Strategy = SelectionStrategy.Scene,
            SceneLeadIn = TimeSpan.FromSeconds(0.35),
        };

        var context = SelectionTestData.Context(
            [new SourceMedia(info, analysis)], segmentCount: 4, options: options);

        var segments = SegmentSelectorFactory.CreateDefault()
            .Create(SelectionStrategy.Scene)
            .SelectSegments(context);

        segments.ShouldNotBeEmpty();

        foreach (var segment in segments)
        {
            var boundaries = cuts.Append(0d);
            boundaries.ShouldContain(
                b => Math.Abs(segment.Start.TotalSeconds - (b + 0.35)) < 0.01
                     || Math.Abs(segment.Start.TotalSeconds - b) < 0.01,
                $"{segment} does not begin at a shot boundary");
        }
    }

    [Fact]
    public void Shots_AreDerivedFromCutsAndBookendedByTheRuntime()
    {
        var info = SelectionTestData.Info(100);
        var analysis = SelectionTestData.Analysis(info, cutsAtSeconds: [30, 70]);

        var shots = analysis.Shots();

        shots.Count.ShouldBe(3);
        shots[0].ShouldBe(SelectionTestData.Range(0, 30));
        shots[1].ShouldBe(SelectionTestData.Range(30, 70));
        shots[2].ShouldBe(SelectionTestData.Range(70, 100));
    }
}

public class CueDrivenSelectorTests
{
    private static ISegmentSelector Selector() =>
        SegmentSelectorFactory.CreateDefault().Create(SelectionStrategy.Cues);

    [Fact]
    public void Cues_ProduceOneSegmentEachInTheOrderGiven()
    {
        var info = SelectionTestData.Info(600);
        var cues = new[] { 500d, 100d, 300d }.Select(TimeSpan.FromSeconds).ToList();

        var context = SelectionTestData.Context(
            [new SourceMedia(info, SelectionTestData.Analysis(info))],
            segmentCount: 6,
            options: new SelectionOptions
            {
                Strategy = SelectionStrategy.Cues,
                Cues = cues,
                SnapCuesToScene = false,
            });

        var segments = Selector().SelectSegments(context);

        segments.Count.ShouldBe(3);

        // Order must follow the cue list, not the timeline: it matches the running commentary.
        segments.Select(s => s.Start.TotalSeconds).ShouldBe([500d, 100d, 300d]);
    }

    [Fact]
    public void Cues_ShareTheTargetDurationEqually()
    {
        var info = SelectionTestData.Info(600);

        var context = SelectionTestData.Context(
            [new SourceMedia(info, SelectionTestData.Analysis(info))],
            segmentCount: 6,
            segmentSeconds: 5,
            options: new SelectionOptions
            {
                Strategy = SelectionStrategy.Cues,
                Cues = [TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(300)],
                SnapCuesToScene = false,
            });

        var segments = Selector().SelectSegments(context);

        // 6 x 5s = 30s target, shared between 3 cues.
        segments.ShouldAllBe(s => Math.Abs(s.Duration.TotalSeconds - 10d) < 0.001);
        segments.Sum(s => s.Duration.TotalSeconds).ShouldBe(30d, 0.001);
    }

    [Fact]
    public void Cues_SnapBackToTheStartOfTheContainingShot()
    {
        var info = SelectionTestData.Info(600);
        var analysis = SelectionTestData.Analysis(info, cutsAtSeconds: [100, 200]);

        var context = SelectionTestData.Context(
            [new SourceMedia(info, analysis)],
            segmentCount: 4,
            options: new SelectionOptions
            {
                Strategy = SelectionStrategy.Cues,

                // 1.5s into the shot that starts at 100s.
                Cues = [TimeSpan.FromSeconds(101.5)],
                SnapCuesToScene = true,
            });

        var segment = Selector().SelectSegments(context).ShouldHaveSingleItem();

        segment.Start.TotalSeconds.ShouldBe(100d, 0.001);
        segment.Reason.ShouldBe("cue/snapped-to-shot");
    }

    [Fact]
    public void Cues_DoNotSnapWhenTheShotStartIsFarBehind()
    {
        var info = SelectionTestData.Info(600);
        var analysis = SelectionTestData.Analysis(info, cutsAtSeconds: [100, 200]);

        var context = SelectionTestData.Context(
            [new SourceMedia(info, analysis)],
            segmentCount: 4,
            options: new SelectionOptions
            {
                Strategy = SelectionStrategy.Cues,

                // 80s into the shot: snapping back that far would show the wrong moment.
                Cues = [TimeSpan.FromSeconds(180)],
                SnapCuesToScene = true,
            });

        var segment = Selector().SelectSegments(context).ShouldHaveSingleItem();

        segment.Start.TotalSeconds.ShouldBe(180d, 0.001);
        segment.Reason.ShouldBe("cue");
    }

    [Fact]
    public void Cues_BeyondTheSourceRuntimeAreDropped()
    {
        var info = SelectionTestData.Info(100);

        var context = SelectionTestData.Context(
            [new SourceMedia(info, SelectionTestData.Analysis(info))],
            segmentCount: 4,
            options: new SelectionOptions
            {
                Strategy = SelectionStrategy.Cues,
                Cues = [TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(9999)],
                SnapCuesToScene = false,
            });

        Selector().SelectSegments(context).ShouldHaveSingleItem();
    }

    [Fact]
    public void Cues_AreClampedSoAClipCannotRunPastTheEnd()
    {
        var info = SelectionTestData.Info(100);

        var context = SelectionTestData.Context(
            [new SourceMedia(info, SelectionTestData.Analysis(info))],
            segmentCount: 4,
            segmentSeconds: 10,
            options: new SelectionOptions
            {
                Strategy = SelectionStrategy.Cues,
                Cues = [TimeSpan.FromSeconds(98)],
                SnapCuesToScene = false,
            });

        var segment = Selector().SelectSegments(context).ShouldHaveSingleItem();

        segment.End.ShouldBeLessThanOrEqualTo(info.Duration);
    }

    [Fact]
    public void NoCues_YieldsNoSegments()
    {
        var info = SelectionTestData.Info(600);

        var context = SelectionTestData.Context(
            [new SourceMedia(info, SelectionTestData.Analysis(info))],
            segmentCount: 4,
            options: new SelectionOptions { Strategy = SelectionStrategy.Cues, Cues = [] });

        Selector().SelectSegments(context).ShouldBeEmpty();
    }
}

public class DistributionTests
{
    [Theory]
    [InlineData(10, new[] { 1d, 1d }, new[] { 5, 5 })]
    [InlineData(10, new[] { 3d, 1d }, new[] { 8, 2 })]
    [InlineData(7, new[] { 1d, 1d, 1d }, new[] { 3, 2, 2 })]
    [InlineData(5, new[] { 1d }, new[] { 5 })]
    public void DistributeByWeight_ApportionsExactly(int total, double[] weights, int[] expected)
    {
        var result = TestableSelector.Distribute(total, weights);

        result.ShouldBe(expected);
        result.Sum().ShouldBe(total);
    }

    [Fact]
    public void DistributeByWeight_SpreadsEvenlyWhenAllWeightsAreZero()
    {
        var result = TestableSelector.Distribute(5, [0d, 0d]);

        result.Sum().ShouldBe(5);
    }

    /// <summary>Exposes the internal apportionment helper for direct testing.</summary>
    private sealed class TestableSelector : SegmentSelectorBase
    {
        public override SelectionStrategy Strategy => SelectionStrategy.Uniform;

        public override bool RequiresAnalysis => false;

        public static int[] Distribute(int total, IReadOnlyList<double> weights) =>
            DistributeByWeight(total, weights);

        protected override IEnumerable<SegmentCandidate> GenerateCandidates(
            SelectionContext context,
            SourceMedia source,
            TimeRangeSet eligible,
            TimeSpan window) => [];
    }
}
