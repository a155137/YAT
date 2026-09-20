namespace YAT.Analytics.Statistics;

// Where an observation sits on a probability plot: the share of the distribution it is expected to be below, given its
// place in the sorted sample.
public static class ProbabilityPlotPositions
{
    // Benard's median rank, the plotting position probability plots are normally drawn with:
    //
    //     p = (i - 0.3) / (n + 0.4)
    //
    // for rank i of n, counted from 1. It is always strictly between 0 and 1, so every observation - including the
    // smallest and the largest - has a normal score, and a sample of one sits at the middle of the distribution.
    public static double Benard(int rank, int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rank, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rank, sampleCount);

        return (rank - 0.3) / (sampleCount + 0.4);
    }
}
