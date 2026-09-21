namespace YAT.Analytics.Statistics;

// What one box of a box plot is made of: where the middle half of the sample sits, how far the data reaches on either
// side of it, where its mean is, and which observations lie beyond that reach.
//
//     IQR         = Q3 - Q1
//     lower fence = Q1 - 1.5 * IQR
//     upper fence = Q3 + 1.5 * IQR
//
// The whiskers are not drawn to the fences: they end at the smallest and largest observations that are still inside
// them, so a whisker always points at a measurement that was actually made. Everything beyond a whisker is an outlier,
// and outliers stay part of the sample - the quartiles and the mean are computed from all of the observations,
// including them.
//
// A sample without observations has no box at all; every statistic is null and there are no outliers.
public sealed record BoxPlotSummary(
    int Count,
    double? Mean,
    double? FirstQuartile,
    double? Median,
    double? ThirdQuartile,
    double? InterquartileRange,
    double? LowerWhisker,
    double? UpperWhisker,
    IReadOnlyList<double> Outliers)
{
    // The distance beyond the quartiles a whisker may reach, in interquartile ranges. Tukey's 1.5; fixed on purpose,
    // so two box plots of the same data never disagree about what an outlier is.
    public const double FenceMultiplier = 1.5;

    public static readonly BoxPlotSummary Empty = new(0, null, null, null, null, null, null, null, []);

    // The box of these observations, WHICH THIS SORTS IN PLACE: the caller's span comes back in ascending order, so
    // pass a buffer the caller owns (the quartiles need sorted values, and sorting a copy of a million observations
    // per box is worth avoiding).
    //
    // The values must be finite. Unlike the capability statistics, nothing here depends on the order they were
    // measured in: a box plot describes the distribution, not the sequence.
    public static BoxPlotSummary ComputeInPlaceSorting(Span<double> values)
    {
        if (values.IsEmpty)
        {
            return Empty;
        }

        values.Sort();

        var q1 = Quantiles.Linear(values, 0.25);
        var median = Quantiles.Linear(values, 0.5);
        var q3 = Quantiles.Linear(values, 0.75);
        var iqr = q3 - q1;
        var lowerFence = q1 - (FenceMultiplier * iqr);
        var upperFence = q3 + (FenceMultiplier * iqr);

        // The sample is sorted, so the observations inside the fences are one run: everything before it is a low
        // outlier and everything after it a high one. Q1 and Q3 themselves are always inside, so the run is never
        // empty and both whiskers always exist.
        var first = 0;
        while (first < values.Length && values[first] < lowerFence)
        {
            first++;
        }

        var last = values.Length - 1;
        while (last >= 0 && values[last] > upperFence)
        {
            last--;
        }

        var outliers = first + (values.Length - 1 - last) == 0 ? [] : Outside(values, first, last);

        return new BoxPlotSummary(
            values.Length,
            Descriptives.Mean(values),
            q1,
            median,
            q3,
            iqr,
            values[first],
            values[last],
            outliers);
    }

    // The observations beyond the whiskers, in ascending order: the low ones first, then the high ones.
    private static double[] Outside(ReadOnlySpan<double> sorted, int first, int last)
    {
        var outliers = new double[first + (sorted.Length - 1 - last)];
        var position = 0;
        for (var index = 0; index < first; index++)
        {
            outliers[position++] = sorted[index];
        }

        for (var index = last + 1; index < sorted.Length; index++)
        {
            outliers[position++] = sorted[index];
        }

        return outliers;
    }
}
