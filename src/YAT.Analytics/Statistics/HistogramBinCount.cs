namespace YAT.Analytics.Statistics;

// How many equal-width bins a sample should be divided into. This is the bin-count policy alone: where the bins start,
// how wide they are and which value falls in which bin is decided by the caller.
//
// The rules, in order:
//
//     n < 30                  -> Sturges,            bins = ceil(log2(n) + 1)
//     otherwise               -> Freedman-Diaconis,  width = 2 * IQR * n^(-1/3)
//     Freedman-Diaconis invalid (IQR = 0, or a width that is not a positive finite number)
//                             -> Scott,              width = 3.5 * s * n^(-1/3)
//     Scott invalid (s = 0, i.e. every value is the same)
//                             -> a single bin
//
// and the result is always clamped to [MinimumBinCount, MaximumBinCount], so a sample whose middle half is far narrower
// than its full range cannot ask for thousands of bins.
public static class HistogramBinCount
{
    // Below this many observations the quartile-based rules have too little to work with, and Sturges is used instead.
    public const int SmallSampleThreshold = 30;

    public const int MinimumBinCount = 1;

    public const int MaximumBinCount = 200;

    // observationCount: how many values the sample has.
    // range: the largest value minus the smallest one; 0 for a sample with no spread.
    // interquartileRange and standardDeviation: of the same sample. Both may be 0, and both are ignored for a small
    // sample, which uses Sturges.
    public static int Suggest(int observationCount, double range, double interquartileRange, double standardDeviation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(observationCount, 1);

        if (!double.IsFinite(range) || range < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(range), range, "The range must be a finite, non-negative number.");
        }

        // No spread at all: one bin is the only honest answer.
        if (range == 0)
        {
            return MinimumBinCount;
        }

        if (observationCount < SmallSampleThreshold)
        {
            return Clamp(Math.Ceiling(Math.Log2(observationCount) + 1));
        }

        var scale = Math.Pow(observationCount, -1d / 3d);

        return FromWidth(range, 2 * interquartileRange * scale)
            ?? FromWidth(range, 3.5 * standardDeviation * scale)
            ?? MinimumBinCount;
    }

    // The bins a width of this size divides the range into, or null when the width says nothing usable.
    private static int? FromWidth(double range, double width) =>
        double.IsFinite(width) && width > 0 ? Clamp(Math.Ceiling(range / width)) : null;

    private static int Clamp(double count) =>
        !double.IsFinite(count) || count < MinimumBinCount
            ? MinimumBinCount
            : (int)Math.Min(count, MaximumBinCount);
}
