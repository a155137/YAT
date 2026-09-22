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

        return sum / values.Length;
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

        var variance = squared / (values.Length - 1);
        return variance > 0 ? Math.Sqrt(variance) : 0;
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
