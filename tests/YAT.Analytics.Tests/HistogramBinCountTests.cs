using YAT.Analytics.Statistics;

namespace YAT.Analytics.Tests;

// The bin-count policy: which rule applies, what it gives, and what happens when a rule has nothing to say.
public class HistogramBinCountTests
{
    // The factor both width rules scale by: n^(-1/3).
    private static double Scale(int n) => Math.Pow(n, -1d / 3d);

    // 1
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(8, 4)]
    [InlineData(10, 5)]
    [InlineData(29, 6)]
    public void ASmallSampleUsesSturges(int observationCount, int expected)
    {
        // ceil(log2(n) + 1), whatever the spread of the values is.
        Assert.Equal(expected, HistogramBinCount.Suggest(observationCount, 100, interquartileRange: 20, standardDeviation: 30));
    }

    // 2
    [Fact]
    public void ASampleOfThirtyOrMoreUsesFreedmanDiaconis()
    {
        var width = 2 * 20 * Scale(100);
        var expected = (int)Math.Ceiling(100 / width);

        Assert.Equal(expected, HistogramBinCount.Suggest(100, 100, interquartileRange: 20, standardDeviation: 30));
        Assert.Equal(12, expected);
    }

    // 3
    [Fact]
    public void WithoutAnInterquartileRangeItFallsBackToScott()
    {
        var width = 3.5 * 30 * Scale(100);
        var expected = (int)Math.Ceiling(100 / width);

        Assert.Equal(expected, HistogramBinCount.Suggest(100, 100, interquartileRange: 0, standardDeviation: 30));
        Assert.Equal(5, expected);
    }

    // 4
    [Fact]
    public void WithoutASpreadAtAllItFallsBackToOneBin()
    {
        Assert.Equal(1, HistogramBinCount.Suggest(100, 100, interquartileRange: 0, standardDeviation: 0));
    }

    // 5
    [Fact]
    public void ConstantValuesAreOneBin()
    {
        Assert.Equal(1, HistogramBinCount.Suggest(1_000, range: 0, interquartileRange: 0, standardDeviation: 0));
        Assert.Equal(1, HistogramBinCount.Suggest(10, range: 0, interquartileRange: 0, standardDeviation: 0));
    }

    // 6
    [Fact]
    public void ANarrowMiddleHalfCannotAskForThousandsOfBins()
    {
        // An outlier far from a tight cluster: Freedman-Diaconis would want ten billion bins.
        Assert.Equal(
            HistogramBinCount.MaximumBinCount,
            HistogramBinCount.Suggest(1_000, range: 1e9, interquartileRange: 0.5, standardDeviation: 3e7));
    }

    // 7
    [Fact]
    public void AWidthWiderThanTheDataIsStillOneBin()
    {
        Assert.Equal(1, HistogramBinCount.Suggest(100, range: 1, interquartileRange: 1_000, standardDeviation: 1_000));
    }

    // 8
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ARuleThatAnswersNonsenseIsSkipped(double interquartileRange)
    {
        // Freedman-Diaconis cannot be used, so Scott is.
        var expected = (int)Math.Ceiling(100 / (3.5 * 30 * Scale(100)));

        Assert.Equal(expected, HistogramBinCount.Suggest(100, 100, interquartileRange, standardDeviation: 30));
    }

    // 9
    [Fact]
    public void EveryAnswerIsWithinTheBounds()
    {
        double[] ranges = [0.0001, 1, 100, 1e9];
        double[] spreads = [0, 1e-9, 0.5, 20, 1e8];

        foreach (var range in ranges)
        {
            foreach (var spread in spreads)
            {
                foreach (var count in (int[])[1, 5, 30, 1_000, 1_000_000])
                {
                    var bins = HistogramBinCount.Suggest(count, range, spread, spread);
                    Assert.InRange(bins, HistogramBinCount.MinimumBinCount, HistogramBinCount.MaximumBinCount);
                }
            }
        }
    }

    // 10
    [Fact]
    public void TheSameSampleAlwaysGivesTheSameBinCount()
    {
        Assert.Equal(
            HistogramBinCount.Suggest(517, 42.5, 6.25, 9.75),
            HistogramBinCount.Suggest(517, 42.5, 6.25, 9.75));
    }

    // 11
    [Fact]
    public void ASampleNeedsObservationsAndAUsableRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HistogramBinCount.Suggest(0, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => HistogramBinCount.Suggest(10, -1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => HistogramBinCount.Suggest(10, double.NaN, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => HistogramBinCount.Suggest(10, double.PositiveInfinity, 1, 1));
    }
}
