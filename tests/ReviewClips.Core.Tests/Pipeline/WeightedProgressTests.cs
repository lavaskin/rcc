using ReviewClips.Core.Pipeline;

namespace ReviewClips.Core.Tests.Pipeline;

/// <summary>
/// Clip encodes run side by side and each reports its own 0..1 progress; this folds them into
/// the single figure the extraction bar shows.
/// </summary>
public class WeightedProgressTests
{
    private static (WeightedProgress Progress, List<(int Completed, double Fraction)> Reports) Create(
        params double[] seconds)
    {
        var reports = new List<(int, double)>();
        var progress = new WeightedProgress(
            [.. seconds.Select(TimeSpan.FromSeconds)],
            (completed, fraction) => reports.Add((completed, fraction)));
        return (progress, reports);
    }

    [Fact]
    public void A_job_counts_in_proportion_to_its_length()
    {
        var (progress, reports) = Create(30, 10);

        progress.For(0).Report(0.5);

        reports.ShouldHaveSingleItem().ShouldBe((0, 15d / 40d));
    }

    [Fact]
    public void Completing_a_job_counts_it_and_fills_its_share()
    {
        var (progress, reports) = Create(30, 10);

        progress.Complete(1);

        reports.ShouldHaveSingleItem().ShouldBe((1, 0.25));
    }

    [Fact]
    public void The_overall_fraction_never_goes_backwards()
    {
        var (progress, reports) = Create(10, 10);

        progress.For(0).Report(0.6);
        progress.For(0).Report(0.4);

        reports.ShouldHaveSingleItem().Fraction.ShouldBe(0.3);
    }

    [Fact]
    public void Completing_a_job_twice_counts_it_once()
    {
        var (progress, reports) = Create(10, 10);

        progress.Complete(0);
        progress.Complete(0);

        reports.ShouldHaveSingleItem().Completed.ShouldBe(1);
    }

    [Fact]
    public void Finishing_every_job_reports_exactly_one()
    {
        // Thirds of 0.1s do not sum back to the total exactly in floating point.
        var (progress, reports) = Create(0.1, 0.1, 0.1);

        for (var job = 0; job < 3; job++)
        {
            progress.For(job).Report(1d / 3d);
            progress.Complete(job);
        }

        reports[^1].ShouldBe((3, 1d));
    }

    [Fact]
    public void Zero_length_jobs_are_weighted_equally()
    {
        var (progress, reports) = Create(0, 0);

        progress.Complete(0);

        reports.ShouldHaveSingleItem().ShouldBe((1, 0.5));
    }

    [Fact]
    public async Task Concurrent_reports_reach_the_sink_one_at_a_time_and_in_order()
    {
        const int Jobs = 8;
        var inside = 0;
        var overlapped = false;
        var fractions = new List<double>();

        var progress = new WeightedProgress(
            [.. Enumerable.Repeat(TimeSpan.FromSeconds(1), Jobs)],
            (_, fraction) =>
            {
                if (Interlocked.Increment(ref inside) > 1)
                {
                    overlapped = true;
                }

                fractions.Add(fraction);
                Interlocked.Decrement(ref inside);
            });

        await Task.WhenAll(Enumerable.Range(0, Jobs).Select(job => Task.Run(
            () =>
            {
                var sink = progress.For(job);
                for (var step = 1; step <= 100; step++)
                {
                    sink.Report(step / 100d);
                }

                progress.Complete(job);
            },
            TestContext.Current.CancellationToken)));

        overlapped.ShouldBeFalse();
        fractions.ShouldBe([.. fractions.Order()]);
        fractions[^1].ShouldBe(1d);
    }
}
