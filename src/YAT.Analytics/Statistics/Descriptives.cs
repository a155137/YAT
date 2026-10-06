namespace YAT.Analytics.Statistics;

// Summary statistics of a sample. Nothing here knows what the numbers describe.
public static class Descriptives
{
    // The arithmetic mean. An empty sample has no mean and returns 0.
    public static double Mean(ReadOnlySpan<double> values)
    {
        if (values.IsEmpty)
        {
            return 0;
        }

        var sum = 0d;
        foreach (var value in values)
        {
            sum += value;
        }

        // Finite values whose sum overflows (Task #063.1: 1e300 and more) still have a finite mean - it lies between the
        // smallest and the largest of them. Only then is it worked out from the values divided first; every other
        // sample keeps the sum above, to the last bit.
        return double.IsFinite(sum) ? sum / values.Length : ScaledMean(values);
    }

    // The divided values can round to a sum just past the largest or smallest value (even past MaxValue, for a sample
    // of MaxValue only), so the result is kept between them, where a mean always lies.
    private static double ScaledMean(ReadOnlySpan<double> values)
    {
        var mean = 0d;
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        foreach (var value in values)
        {
            mean += value / values.Length;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }

        return Math.Clamp(mean, minimum, maximum);
    }

    // The SAMPLE standard deviation: the sum of squared deviations divided by n - 1 (Bessel's correction), not by n.
    // A sample of fewer than two values has no spread to measure and returns 0.
    //
    // Computed in two passes (mean, then deviations) rather than from the sum of squares, which loses precision when
    // the values are large and close together - exactly the shape of measured semiconductor data.
    public static double StandardDeviation(ReadOnlySpan<double> values)
    {
        if (values.Length < 2)
        {
            return 0;
        }

        // Identical observations have no spread, exactly. The computation below would not always say so: the mean of
        // a value that binary cannot represent exactly (0.1, -0.001) is rounded, so identical observations differ from
        // it by a residue and the result came out near 1e-17 instead of 0. Only exact equality counts here - values
        // that differ at all, however little, keep their spread.
        if (AllEqual(values))
        {
            return 0;
        }

        var sum = 0d;
        foreach (var value in values)
        {
            sum += value;
        }

        var mean = sum / values.Length;

        var squared = 0d;
        foreach (var value in values)
        {
            var deviation = value - mean;
            squared += deviation * deviation;
        }

        // Finite values whose sum, deviations or squares overflow (Task #063.1) are worked out again over the values
        // scaled by the largest of them, so only a standard deviation that is itself beyond a double is infinite.
        // Every other sample keeps the result above, to the last bit.
        if (!double.IsFinite(squared))
        {
            return ScaledStandardDeviation(values);
        }

        var variance = squared / (values.Length - 1);
        return variance > 0 ? Math.Sqrt(variance) : 0;
    }

    private static double ScaledStandardDeviation(ReadOnlySpan<double> values)
    {
        var scale = 0d;
        foreach (var value in values)
        {
            scale = Math.Max(scale, Math.Abs(value));
        }

        var mean = 0d;
        foreach (var value in values)
        {
            mean += value / scale / values.Length;
        }

        var squared = 0d;
        foreach (var value in values)
        {
            var deviation = (value / scale) - mean;
            squared += deviation * deviation;
        }

        var variance = squared / (values.Length - 1);
        return variance > 0 ? scale * Math.Sqrt(variance) : 0;
    }

    private static bool AllEqual(ReadOnlySpan<double> values)
    {
        var first = values[0];
        foreach (var value in values[1..])
        {
            if (value != first)
            {
                return false;
            }
        }

        return true;
    }
}
