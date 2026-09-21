namespace YAT.Analytics.Statistics;

// The descriptive summary of one sample: how many observations it has, how many were unavailable, and the statistics
// those observations support. Nothing here knows what the numbers describe or where the missing observations come from.
//
// A statistic that a sample cannot support is null, never a manufactured zero: an empty sample has no mean, minimum,
// quartiles or maximum, and a single observation has no spread to measure (the sample standard deviation divides by
// n - 1). Callers show null as blank rather than as 0.
public sealed record DescriptiveSummary(
    int Count,
    int MissingCount,
    double? Mean,
    double? StandardDeviation,
    double? Minimum,
    double? FirstQuartile,
    double? Median,
    double? ThirdQuartile,
    double? Maximum)
{
    // A sample without observations: every statistic is null, and MissingCount is what the caller counted.
    public static DescriptiveSummary Empty(int missingCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(missingCount);
        return new DescriptiveSummary(0, missingCount, null, null, null, null, null, null, null);
    }

    // The summary of the given observations, WHICH THIS SORTS IN PLACE: the caller's span comes back in ascending
    // order, so pass a buffer the caller owns (the quartiles need sorted values, and sorting a copy of a million
    // observations per group is worth avoiding).
    //
    // The values must be finite; a missing observation is not a value here, it is counted in missingCount. Mean and
    // sample standard deviation come from Descriptives, the quartiles from Quantiles (R-7), so a descriptive table and
    // a graph never disagree about what these numbers mean.
    public static DescriptiveSummary ComputeInPlaceSorting(Span<double> values, int missingCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(missingCount);

        if (values.IsEmpty)
        {
            return Empty(missingCount);
        }

        values.Sort();

        return new DescriptiveSummary(
            values.Length,
            missingCount,
            Descriptives.Mean(values),
            // Fewer than two observations leave the sample standard deviation undefined; Descriptives returns 0 there,
            // which is the right answer for a graph and the wrong one for a table.
            values.Length < 2 ? null : Descriptives.StandardDeviation(values),
            values[0],
            Quantiles.Linear(values, 0.25),
            Quantiles.Linear(values, 0.5),
            Quantiles.Linear(values, 0.75),
            values[^1]);
    }
}
