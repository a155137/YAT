namespace YAT.Analytics.Statistics;

// Sample quantiles, in the R-7 convention (the default of R's quantile(), and the one Excel's PERCENTILE.INC uses):
//
//     h = (n - 1) * p
//     quantile = x[floor(h)] + (h - floor(h)) * (x[floor(h) + 1] - x[floor(h)])
//
// with x the sample in ascending order. The convention is fixed here on purpose: a histogram's bin count must not
// change because a different quantile definition was picked somewhere else.
public static class Quantiles
{
    // The p-quantile of an ascending sample. The caller sorts; this never reorders or copies the values.
    public static double Linear(ReadOnlySpan<double> sorted, double probability)
    {
        if (sorted.IsEmpty)
        {
            throw new ArgumentException("A quantile needs at least one value.", nameof(sorted));
        }

        if (!double.IsFinite(probability) || probability < 0 || probability > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(probability), probability, "A probability must be between 0 and 1.");
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var position = (sorted.Length - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Length - 1);

        var fraction = position - lower;
        var gap = sorted[upper] - sorted[lower];

        // Two finite neighbours whose difference overflows (Task #063.1: -MaxValue and MaxValue) are interpolated as a
        // weighted sum instead - the same point, every term finite. Every other sample keeps the formula above, to the
        // last bit.
        return double.IsFinite(gap)
            ? sorted[lower] + (fraction * gap)
            : ((1 - fraction) * sorted[lower]) + (fraction * sorted[upper]);
    }

    // Q3 - Q1 of an ascending sample: the width of the middle half of the data.
    public static double InterquartileRange(ReadOnlySpan<double> sorted) =>
        Linear(sorted, 0.75) - Linear(sorted, 0.25);
}
