namespace ReviewClips.Core.Pipeline;

/// <summary>
/// Folds the progress of jobs running side by side into one overall fraction, each job weighted
/// by its length, alongside a count of jobs finished.
/// <para>
/// Weighted rather than counted so the figure moves with the work: with a few long clips in the
/// batch, "3 of 31 done" can be a fifth of the encoding. Reports are serialized under a lock, so
/// the sink sees one caller at a time and a fraction that never goes backwards, whichever worker
/// thread it came from.
/// </para>
/// </summary>
internal sealed class WeightedProgress
{
    private readonly double[] _weights;
    private readonly double[] _fractions;
    private readonly bool[] _finished;
    private readonly double _totalWeight;
    private readonly Action<int, double> _report;
    private readonly object _gate = new();
    private double _doneWeight;
    private int _completed;

    /// <param name="durations">One entry per job; its share of the total work.</param>
    /// <param name="report">Receives the number of jobs finished and the overall fraction.</param>
    public WeightedProgress(IReadOnlyList<TimeSpan> durations, Action<int, double> report)
    {
        ArgumentNullException.ThrowIfNull(durations);
        ArgumentNullException.ThrowIfNull(report);

        _weights = [.. durations.Select(d => Math.Max(d.TotalSeconds, 0d))];
        _totalWeight = _weights.Sum();

        // Zero-length jobs carry no signal about relative cost; count them equally instead.
        if (_totalWeight <= 0d)
        {
            Array.Fill(_weights, 1d);
            _totalWeight = _weights.Length;
        }

        _fractions = new double[_weights.Length];
        _finished = new bool[_weights.Length];
        _report = report;
    }

    /// <summary>A sink for one job's own 0..1 progress.</summary>
    public IProgress<double> For(int job) => new InlineProgress<double>(fraction => Advance(job, fraction));

    /// <summary>Marks a job finished, whether or not it reported reaching the end.</summary>
    public void Complete(int job)
    {
        lock (_gate)
        {
            if (_finished[job])
            {
                return;
            }

            _finished[job] = true;
            _completed++;
            Move(job, 1d);
            _report(_completed, Overall());
        }
    }

    private void Advance(int job, double fraction)
    {
        lock (_gate)
        {
            if (Move(job, fraction))
            {
                _report(_completed, Overall());
            }
        }
    }

    private bool Move(int job, double fraction)
    {
        fraction = Math.Clamp(fraction, 0d, 1d);
        if (fraction <= _fractions[job])
        {
            return false;
        }

        _doneWeight += (fraction - _fractions[job]) * _weights[job];
        _fractions[job] = fraction;
        return true;
    }

    // Exactly 1 once everything has finished: summed floating-point shares can land a hair short,
    // which would leave a bar at "100%" that never reads as complete.
    private double Overall() =>
        _completed == _weights.Length ? 1d : Math.Min(_doneWeight / _totalWeight, 1d);
}
