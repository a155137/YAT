namespace YAT.Analytics.Statistics;

// The capability indices of a process, from its mean, its within standard deviation and the specification it is
// measured against:
//
//     Cp  = (USL - LSL) / (6 * sigma)
//     Cpu = (USL - mean) / (3 * sigma)
//     Cpl = (mean - LSL) / (3 * sigma)
//     Cpk = min(Cpu, Cpl), or whichever of the two the specification defines
//
// An index its inputs cannot support is null: no spread to divide by (a sequence without moving ranges, or a process
// that never moved), no mean, or no limit on that side. Nothing is clamped - a process centred outside its
// specification has a negative Cpk, and saying so is the point of measuring it.
public static class CapabilityIndices
{
    // (USL - LSL) / (6 * sigma): how much room the specification leaves for the spread, wherever the process sits.
    // A one-sided specification has no width, so it has no Cp.
    public static double? Cp(double? lowerLimit, double? upperLimit, double? standardDeviation) =>
        lowerLimit is { } lower && upperLimit is { } upper && Spread(standardDeviation) is { } sigma
            ? Finite((upper - lower) / (6 * sigma))
            : null;

    // (USL - mean) / (3 * sigma): the distance from the process to its upper limit, in spreads.
    public static double? Cpu(double? mean, double? upperLimit, double? standardDeviation) =>
        mean is { } centre && upperLimit is { } upper && Spread(standardDeviation) is { } sigma
            ? Finite((upper - centre) / (3 * sigma))
            : null;

    // (mean - LSL) / (3 * sigma): the distance from the process to its lower limit, in spreads.
    public static double? Cpl(double? mean, double? lowerLimit, double? standardDeviation) =>
        mean is { } centre && lowerLimit is { } lower && Spread(standardDeviation) is { } sigma
            ? Finite((centre - lower) / (3 * sigma))
            : null;

    // The worse of the two sides, or the only side a one-sided specification has.
    public static double? Cpk(double? lowerIndex, double? upperIndex) => (lowerIndex, upperIndex) switch
    {
        ({ } lower, { } upper) => Math.Min(lower, upper),
        ({ } lower, null) => lower,
        (null, { } upper) => upper,
        _ => null
    };

    // A spread an index can be divided by: finite and greater than zero. A process whose measurements never moved has
    // a within standard deviation of 0, which is a real result, but it divides no index.
    private static double? Spread(double? standardDeviation) =>
        standardDeviation is { } sigma && double.IsFinite(sigma) && sigma > 0 ? sigma : null;

    // Extreme limits and tiny spreads can overflow; a result that is not a finite number is no result.
    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
