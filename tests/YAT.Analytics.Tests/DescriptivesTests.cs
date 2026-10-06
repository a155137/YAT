using YAT.Analytics.Statistics;

namespace YAT.Analytics.Tests;

// Summary statistics, with the definitions written down: the standard deviation here is the SAMPLE one.
public class DescriptivesTests
{
    private const double Tolerance = 1e-12;

    // 0
    [Fact]
    public void TheMeanIsTheArithmeticOne()
    {
        Assert.Equal(5, Descriptives.Mean([2, 4, 4, 4, 5, 5, 7, 9]), Tolerance);
        Assert.Equal(-1.5, Descriptives.Mean([-3, 0]), Tolerance);
        Assert.Equal(7, Descriptives.Mean([7]), Tolerance);
        Assert.Equal(0, Descriptives.Mean([]));
    }

    // 1
    [Fact]
    public void TheStandardDeviationIsTheSampleOneNotThePopulationOne()
    {
        // mean 5, sum of squared deviations 32, n = 8.
        double[] values = [2, 4, 4, 4, 5, 5, 7, 9];

        // Sample: sqrt(32 / 7). The population standard deviation of the same values is exactly 2.
        Assert.Equal(Math.Sqrt(32d / 7d), Descriptives.StandardDeviation(values), Tolerance);
        Assert.NotEqual(2d, Descriptives.StandardDeviation(values), Tolerance);
    }

    // 2
    [Fact]
    public void ASampleOfTwoValuesHasTheSpreadBetweenThem()
    {
        // mean 5, deviations -5 and +5, divided by n - 1 = 1.
        Assert.Equal(Math.Sqrt(50), Descriptives.StandardDeviation([0, 10]), Tolerance);
    }

    // 3
    [Fact]
    public void RepeatedValuesHaveNoSpread()
    {
        Assert.Equal(0, Descriptives.StandardDeviation([7, 7, 7, 7]));
    }

    // 4
    [Fact]
    public void FewerThanTwoValuesHaveNoSpreadToMeasure()
    {
        Assert.Equal(0, Descriptives.StandardDeviation([]));
        Assert.Equal(0, Descriptives.StandardDeviation([3.5]));
    }

    // 5
    [Fact]
    public void LargeValuesCloseTogetherKeepTheirSpread()
    {
        // The shape of measured data: a big offset with a small variation on top of it.
        double[] values = [1_000_000.1, 1_000_000.2, 1_000_000.3, 1_000_000.4];

        Assert.Equal(Descriptives.StandardDeviation([0.1, 0.2, 0.3, 0.4]), Descriptives.StandardDeviation(values), 1e-9);
    }

    // ---- Hotfix #034.2B: constant data has no spread, exactly ----

    // Values like 0.1 are not exact in binary, so the computed mean of three of them is not quite 0.1, and the old
    // deviations from that mean were not quite zero. Identical observations have a standard deviation of exactly 0.
    [Theory]
    [InlineData(0.1, 3)]
    [InlineData(0.1, 10)]
    [InlineData(-0.001, 904)]
    [InlineData(100, 5)]
    public void IdenticalObservationsHaveAStandardDeviationOfExactlyZero(double value, int count)
    {
        var values = Enumerable.Repeat(value, count).ToArray();

        Assert.Equal(0d, Descriptives.StandardDeviation(values));
    }

    // Only identical values are constant: the smallest real variation still has a spread.
    [Fact]
    public void TheSmallestRealVariationStillHasASpread()
    {
        Assert.True(Descriptives.StandardDeviation([1.0, Math.BitIncrement(1.0)]) > 0);
        Assert.True(Descriptives.StandardDeviation([0.1, 0.1, Math.BitIncrement(0.1)]) > 0);
        Assert.True(Descriptives.StandardDeviation([1e-12, 2e-12, 1e-12]) > 0);
    }

    // 6
    [Fact]
    public void TheSameValuesAlwaysGiveTheSameStandardDeviation()
    {
        double[] values = [-3, 0.5, 17, 17, 92.25];

        Assert.Equal(Descriptives.StandardDeviation(values), Descriptives.StandardDeviation(values));
    }

    // Task #063.1: values the sum or the squares of which overflow a double still have a finite mean and standard
    // deviation; only a standard deviation that is itself beyond a double is infinite.
    [Fact]
    public void ValuesNearTheLimitsOfDoubleKeepAFiniteMeanAndStandardDeviation()
    {
        Assert.Equal(double.MaxValue, Descriptives.Mean([double.MaxValue, double.MaxValue]));
        Assert.Equal(double.MaxValue, Descriptives.Mean([.. Enumerable.Repeat(double.MaxValue, 200)]));
        Assert.Equal(0, Descriptives.Mean([double.MaxValue, -double.MaxValue]));
        Assert.Equal(5d / 3, Descriptives.Mean([1e300, -1e300, 5]), 12);

        Assert.Equal(1e300, Descriptives.StandardDeviation([1e300, -1e300, 5]), 1e288);
        Assert.Equal(0, Descriptives.StandardDeviation([double.MaxValue, double.MaxValue]));
        Assert.Equal(double.MaxValue / Math.Sqrt(2), Descriptives.StandardDeviation([double.MaxValue, 0]), double.MaxValue * 1e-12);
        Assert.Equal(double.PositiveInfinity, Descriptives.StandardDeviation([double.MaxValue, -double.MaxValue]));
    }
}
