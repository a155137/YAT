namespace YAT.Analytics.Statistics;

// The capability of one measured sequence against one specification: how many observations it had, where it sits, how
// much it moves, and what that means for the limits it must meet.
//
// WithinStandardDeviation is the moving range estimate (MRbar / d2) of how much the process moves between consecutive
// measurements - deliberately not the sample standard deviation of DescriptiveSummary, which describes the spread of
// the values themselves. The two are different statistics and are never interchangeable.
//
// A statistic the data cannot support is null, never a manufactured zero or an infinity: no observations, a single
// observation, a sequence without a moving range, or a process that never moved all leave the indices undefined.
public sealed record CapabilitySummary(
    int Count,
    int MissingCount,
    double? Mean,
    double? WithinStandardDeviation,
    double? LowerSpecificationLimit,
    double? UpperSpecificationLimit,
    double? Cp,
    double? Cpl,
    double? Cpu,
    double? Cpk)
{
    // The capability of a sequence that has already been summarised: its count, what was missing, its mean and its
    // within standard deviation. The specification limits are carried through, so a result can show what it was
    // measured against even when there was nothing to measure.
    public static CapabilitySummary Compute(
        int count,
        int missingCount,
        double? mean,
        double? withinStandardDeviation,
        double? lowerSpecificationLimit,
        double? upperSpecificationLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(missingCount);

        var cpl = CapabilityIndices.Cpl(mean, lowerSpecificationLimit, withinStandardDeviation);
        var cpu = CapabilityIndices.Cpu(mean, upperSpecificationLimit, withinStandardDeviation);

        return new CapabilitySummary(
            count,
            missingCount,
            mean,
            withinStandardDeviation,
            lowerSpecificationLimit,
            upperSpecificationLimit,
            CapabilityIndices.Cp(lowerSpecificationLimit, upperSpecificationLimit, withinStandardDeviation),
            cpl,
            cpu,
            CapabilityIndices.Cpk(cpl, cpu));
    }
}
