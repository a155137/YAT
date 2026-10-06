using YAT.Analytics.Statistics;

namespace YAT.Analytics.Tests;

// The quantile convention YAT uses: R-7, h = (n - 1) * p with linear interpolation. The numbers below are what that
// convention gives; a different convention would give different ones, which is exactly why this is pinned down.
public class QuantilesTests
{
    private const double Tolerance = 1e-12;

    // 1
    [Fact]
    public void AnEvenSampleInterpolatesBetweenItsValues()
    {
        double[] sorted = [1, 2, 3, 4];

        // h = 3 * 0.25 = 0.75 -> 1 + 0.75 * (2 - 1)
        Assert.Equal(1.75, Quantiles.Linear(sorted, 0.25), Tolerance);
        Assert.Equal(2.5, Quantiles.Linear(sorted, 0.5), Tolerance);
        Assert.Equal(3.25, Quantiles.Linear(sorted, 0.75), Tolerance);
        Assert.Equal(1.5, Quantiles.InterquartileRange(sorted), Tolerance);
    }

    // 2
    [Fact]
    public void AnOddSampleLandsOnItsOwnValuesWhereTheConventionSaysSo()
    {
        double[] sorted = [1, 2, 3, 4, 5];

        Assert.Equal(2, Quantiles.Linear(sorted, 0.25), Tolerance);
        Assert.Equal(3, Quantiles.Linear(sorted, 0.5), Tolerance);
        Assert.Equal(4, Quantiles.Linear(sorted, 0.75), Tolerance);
        Assert.Equal(2, Quantiles.InterquartileRange(sorted), Tolerance);
    }

    // 3
    [Fact]
    public void TheMedianOfThreeValuesIsTheMiddleOne()
    {
        Assert.Equal(2, Quantiles.Linear([1, 2, 3], 0.5), Tolerance);
    }

    // 4
    [Fact]
    public void TheEndsOfTheSampleAreItsSmallestAndLargestValue()
    {
        double[] sorted = [-7.5, 0, 0.25, 12];

        Assert.Equal(-7.5, Quantiles.Linear(sorted, 0));
        Assert.Equal(12, Quantiles.Linear(sorted, 1));
    }

    // 5
    [Fact]
    public void ASampleOfOneValueIsThatValueAtEveryProbability()
    {
        Assert.Equal(42, Quantiles.Linear([42], 0));
        Assert.Equal(42, Quantiles.Linear([42], 0.5));
        Assert.Equal(42, Quantiles.Linear([42], 1));
        Assert.Equal(0, Quantiles.InterquartileRange([42]));
    }

    // 6
    [Fact]
    public void RepeatedValuesHaveNoSpreadBetweenTheQuartiles()
    {
        Assert.Equal(0, Quantiles.InterquartileRange([5, 5, 5, 5, 5, 5]));
    }

    // 7
    [Fact]
    public void DuplicatesInTheMiddleStillInterpolate()
    {
        double[] sorted = [1, 2, 2, 9];

        Assert.Equal(1.75, Quantiles.Linear(sorted, 0.25), Tolerance);
        Assert.Equal(2, Quantiles.Linear(sorted, 0.5), Tolerance);
        Assert.Equal(3.75, Quantiles.Linear(sorted, 0.75), Tolerance);
        Assert.Equal(2, Quantiles.InterquartileRange(sorted), Tolerance);
    }

    // 8
    [Fact]
    public void TheSameSampleAlwaysGivesTheSameQuantile()
    {
        double[] sorted = [0.1, 0.2, 0.30000000000000004, 1, 7, 7, 91.5];

        Assert.Equal(Quantiles.Linear(sorted, 0.37), Quantiles.Linear(sorted, 0.37));
        Assert.Equal(Quantiles.InterquartileRange(sorted), Quantiles.InterquartileRange(sorted));
    }

    // 9
    [Fact]
    public void ASampleAndAProbabilityAreBothRequiredToMakeSense()
    {
        Assert.Throws<ArgumentException>(() => Quantiles.Linear([], 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantiles.Linear([1, 2], -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantiles.Linear([1, 2], 1.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantiles.Linear([1, 2], double.NaN));
    }

    // Task #063.1: neighbours whose difference overflows a double are still interpolated (R-7), to a finite quantile.
    [Fact]
    public void NeighboursNearTheLimitsOfDoubleGiveAFiniteQuantile()
    {
        Assert.Equal(double.MaxValue, Quantiles.Linear([double.MaxValue, double.MaxValue], 0.5));
        Assert.Equal(double.MaxValue, Quantiles.Linear([double.MaxValue, double.MaxValue], 0.25));
        Assert.Equal(0, Quantiles.Linear([-double.MaxValue, double.MaxValue], 0.5));
        Assert.Equal(-double.MaxValue / 2, Quantiles.Linear([-double.MaxValue, double.MaxValue], 0.25), double.MaxValue * 1e-15);
        Assert.Equal(double.MaxValue / 2, Quantiles.Linear([-double.MaxValue, double.MaxValue], 0.75), double.MaxValue * 1e-15);
    }
}
