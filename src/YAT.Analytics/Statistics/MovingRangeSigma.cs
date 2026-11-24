namespace YAT.Analytics.Statistics;

// The within-sample spread of a measured sequence, estimated from the moving ranges of consecutive observations:
//
//     MR(i)   = |x(i) - x(i-1)|
//     sigma_w = MRbar / d2,  d2 = 1.128 for a moving range of two
//
// This is a sequence statistic, not a sample statistic: it describes how much the process moves from one measurement
// to the next, so the observations must arrive in the order they were measured and must never be sorted. It answers a
// different question from the sample standard deviation of Descriptives, which describes the spread of the values
// whatever order they came in.
//
// Observations are fed in one at a time, because a sequence interrupted by a missing measurement is not a sequence:
// two observations either side of a gap were not measured one after the other, so they form no moving range. The
// caller reports a gap with Break().
public sealed class MovingRangeSigma
{
    // The unbiasing constant of a moving range of two consecutive observations.
    public const double D2 = 1.128;

    private double _sum;
    private double _halfMean;
    private int _count;
    private double _previous;
    private bool _hasPrevious;
    private bool _useScaled;

    // How many moving ranges the sequence produced. Fewer than one means the spread cannot be estimated.
    public int RangeCount => _count;

    // MRbar: the mean of the moving ranges, or null when the sequence produced none or the result is not finite.
    public double? MeanRange => _count == 0
        ? null
        : _useScaled
            ? Finite(_halfMean * 2d)
            : Finite(_sum / _count);

    // The within standard deviation, MRbar / d2, or null when there is no moving range to estimate it from or the
    // result is not finite. When MRbar overflows double.MaxValue but MRbar / d2 is still representable, the finite
    // standard deviation is preserved. It is 0 when every consecutive pair repeated the same value: a measured result,
    // not a missing one.
    public double? StandardDeviation => _count == 0
        ? null
        : _useScaled
            ? Finite((_halfMean / D2) * 2d)
            : MeanRange is { } mean ? Finite(mean / D2) : null;

    // The next observation of the sequence. It must be finite; a missing measurement is Break(), not a value.
    public void Add(double value)
    {
        if (_hasPrevious)
        {
            if (!_useScaled)
            {
                var range = Math.Abs(value - _previous);
                var nextSum = _sum + range;
                if (double.IsFinite(nextSum))
                {
                    _sum = nextSum;
                    _count++;
                }
                else if (double.IsFinite(value) && double.IsFinite(_previous))
                {
                    // Difference or sum overflowed on finite observations: switch once to a half-scaled incremental
                    // mean so every half-range lies in [0, double.MaxValue] and never overflows.
                    _useScaled = true;
                    _halfMean = _count == 0 ? 0d : (_sum / _count) * 0.5;
                    _count++;
                    var halfRange = Math.Abs((value * 0.5) - (_previous * 0.5));
                    _halfMean += (halfRange - _halfMean) / _count;
                }
                else
                {
                    _sum = nextSum;
                    _count++;
                }
            }
            else
            {
                _count++;
                var halfRange = Math.Abs((value * 0.5) - (_previous * 0.5));
                _halfMean += (halfRange - _halfMean) / _count;
            }
        }

        _previous = value;
        _hasPrevious = true;
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;

    // The sequence is interrupted here: the next observation does not follow the previous one, so they form no moving
    // range. Rows that belong to another sequence (another group) are simply not offered and do not interrupt this one.
    public void Break() => _hasPrevious = false;

    // The within standard deviation of one sequence in its measured order, where null is a missing measurement. The
    // sequence is read as given and never reordered.
    public static double? StandardDeviationOf(ReadOnlySpan<double?> sequence)
    {
        var estimator = new MovingRangeSigma();
        foreach (var observation in sequence)
        {
            if (observation is { } value && double.IsFinite(value))
            {
                estimator.Add(value);
            }
            else
            {
                estimator.Break();
            }
        }

        return estimator.StandardDeviation;
    }
}
