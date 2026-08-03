using ReviewClips.Core.Planning;
using ReviewClips.Core.Selection;

namespace ReviewClips.Core.Tests.Planning;

public class ClipSequencerTests
{
    private static List<Segment> Pool(int count, double seconds = 5) =>
        Enumerable.Range(0, count)
            .Select(i => new Segment
            {
                SourcePath = "/movies/film.mkv",
                Start = TimeSpan.FromSeconds(100 + (i * 60)),
                Duration = TimeSpan.FromSeconds(seconds),
            })
            .ToList();

    [Fact]
    public void Fill_ReachesTheRequestedMaterialTotalExactly()
    {
        var sequence = ClipSequencer.Fill(Pool(10), TimeSpan.FromSeconds(300), new Random(1));

        sequence.Sum(s => s.Duration.TotalSeconds).ShouldBe(300d, 0.0001);
    }

    [Fact]
    public void Fill_RepeatsThePoolWhenTheTargetExceedsIt()
    {
        var sequence = ClipSequencer.Fill(Pool(10), TimeSpan.FromSeconds(500), new Random(1));

        // 10 clips of 5s is 50s of material, so 500s needs about ten passes.
        sequence.Count.ShouldBeGreaterThan(90);
        ClipSequencer.CountDistinct(sequence).ShouldBe(10);
    }

    [Fact]
    public void Fill_NeverPlacesAClipImmediatelyAfterItself()
    {
        // The cycle boundary is where a naive loop would stutter.
        var sequence = ClipSequencer.Fill(Pool(6), TimeSpan.FromSeconds(400), new Random(9));

        for (var i = 0; i < sequence.Count - 1; i++)
        {
            (sequence[i].SourcePath == sequence[i + 1].SourcePath
             && sequence[i].Start == sequence[i + 1].Start)
                .ShouldBeFalse($"clip repeats back-to-back at index {i}");
        }
    }

    [Fact]
    public void Fill_VariesTheOrderBetweenPasses()
    {
        var pool = Pool(8);
        var sequence = ClipSequencer.Fill(pool, TimeSpan.FromSeconds(320), new Random(3));

        var firstPass = sequence.Take(8).Select(s => s.Start).ToList();
        var secondPass = sequence.Skip(8).Take(8).Select(s => s.Start).ToList();

        // Same clips, different order: repetition is far less noticeable than a plain loop.
        secondPass.OrderBy(s => s).ShouldBe(firstPass.OrderBy(s => s));
        secondPass.ShouldNotBe(firstPass);
    }

    [Fact]
    public void Fill_IsDeterministicForAGivenSeed()
    {
        var a = ClipSequencer.Fill(Pool(10), TimeSpan.FromSeconds(300), new Random(42));
        var b = ClipSequencer.Fill(Pool(10), TimeSpan.FromSeconds(300), new Random(42));

        a.Select(s => s.Start).ShouldBe(b.Select(s => s.Start));
    }

    [Fact]
    public void Fill_HandlesASinglePooledClip()
    {
        var sequence = ClipSequencer.Fill(Pool(1), TimeSpan.FromSeconds(30), new Random(1));

        sequence.Sum(s => s.Duration.TotalSeconds).ShouldBe(30d, 0.0001);
        ClipSequencer.CountDistinct(sequence).ShouldBe(1);
    }

    [Fact]
    public void Fill_ReturnsEmptyForADegeneratePool()
    {
        ClipSequencer.Fill([], TimeSpan.FromSeconds(60), new Random(1)).ShouldBeEmpty();
        ClipSequencer.Fill(Pool(4), TimeSpan.Zero, new Random(1)).ShouldBeEmpty();
    }

    [Fact]
    public void Fill_NeverEmitsASegmentBelowTheMinimum()
    {
        // 302s from 5s clips leaves a 2s tail; with awkward totals it could be far smaller.
        for (var target = 301; target <= 310; target++)
        {
            var sequence = ClipSequencer.Fill(Pool(7), TimeSpan.FromSeconds(target), new Random(target));

            sequence.ShouldAllBe(s => s.Duration >= SplicePlanner.MinimumSegment);
        }
    }

    [Theory]
    [InlineData(960, 0.4)]
    [InlineData(300, 0.5)]
    [InlineData(120, 0.8)]
    [InlineData(60, 0.4)]
    public void FillForOutput_LandsOnTheRequestedRuntimeAfterTransitions(
        double target,
        double transition)
    {
        var sequence = ClipSequencer.FillForOutput(
            Pool(50),
            TimeSpan.FromSeconds(target),
            TimeSpan.FromSeconds(transition),
            seed: 1);

        var effective = SplicePlanner.EffectiveTransition(
            sequence.Select(s => s.Duration).ToList(),
            TimeSpan.FromSeconds(transition));

        var output = sequence.Sum(s => s.Duration.TotalSeconds)
            - (effective.TotalSeconds * (sequence.Count - 1));

        output.ShouldBe(target, 0.3);
    }

    [Fact]
    public void FillForOutput_MatchesTheTargetWhenThereAreNoTransitions()
    {
        var sequence = ClipSequencer.FillForOutput(
            Pool(20), TimeSpan.FromSeconds(600), TimeSpan.Zero, seed: 5);

        sequence.Sum(s => s.Duration.TotalSeconds).ShouldBe(600d, 0.0001);
    }

    [Fact]
    public void CountDistinct_TreatsATrimmedRepeatAsTheSameClip()
    {
        var pool = Pool(3);
        var trimmed = pool[0] with { Duration = TimeSpan.FromSeconds(2) };

        // The pool size is what --max-clips controls; a shortened copy is not a new clip.
        ClipSequencer.CountDistinct([.. pool, trimmed]).ShouldBe(3);
    }

    [Fact]
    public void DistinctSourceDuration_CountsRepeatedFootageOnce()
    {
        var pool = Pool(10);
        var repeated = pool.Concat(pool).Concat(pool).ToList();

        // 10 clips of 5s regardless of how many times they appear.
        ClipSequencer.DistinctSourceDuration(repeated).TotalSeconds.ShouldBe(50d, 0.0001);
    }

    [Fact]
    public void DistinctSourceDuration_MergesOverlappingClips()
    {
        var a = new Segment
        {
            SourcePath = "/movies/film.mkv",
            Start = TimeSpan.FromSeconds(100),
            Duration = TimeSpan.FromSeconds(10),
        };

        var overlapping = a with { Start = TimeSpan.FromSeconds(105) };

        // 100-110 and 105-115 cover 15s of footage, not 20s.
        ClipSequencer.DistinctSourceDuration([a, overlapping])
            .TotalSeconds
            .ShouldBe(15d, 0.0001);
    }

    [Fact]
    public void DistinctSourceDuration_KeepsSourcesSeparate()
    {
        var a = new Segment
        {
            SourcePath = "/movies/a.mkv",
            Start = TimeSpan.FromSeconds(100),
            Duration = TimeSpan.FromSeconds(5),
        };

        var b = a with { SourcePath = "/movies/b.mkv" };

        // Identical timestamps in different files are different footage.
        ClipSequencer.DistinctSourceDuration([a, b]).TotalSeconds.ShouldBe(10d, 0.0001);
    }
}
