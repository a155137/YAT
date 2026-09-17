using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// The range a graph is drawn over, given the values it has: padded so nothing sits on an axis, and never degenerate.
public class GraphAxisRangesTests
{
    private const double Tolerance = 1e-9;

    private static void AssertRange(double expectedMinimum, double expectedMaximum, GraphAxisRange range)
    {
        Assert.Equal(expectedMinimum, range.Minimum, Tolerance);
        Assert.Equal(expectedMaximum, range.Maximum, Tolerance);
    }

    // 1
    [Fact]
    public void APositiveRangeIsPaddedByFivePercentOnEachSide()
    {
        AssertRange(-5, 105, GraphAxisRanges.FromValues(0, 100));
    }

    // 2
    [Fact]
    public void ANegativeRangeIsPaddedTheSameWay()
    {
        AssertRange(-102.5, -47.5, GraphAxisRanges.FromValues(-100, -50));
    }

    // 3
    [Fact]
    public void ARangeCrossingZeroIsPaddedTheSameWay()
    {
        AssertRange(-11, 11, GraphAxisRanges.FromValues(-10, 10));
    }

    // 4
    [Fact]
    public void ASmallDecimalRangeKeepsItsScale()
    {
        var range = GraphAxisRanges.FromValues(0.001, 0.002);

        Assert.Equal(0.00095, range.Minimum, 1e-12);
        Assert.Equal(0.00205, range.Maximum, 1e-12);
    }

    // 5
    [Fact]
    public void ALargeRangeKeepsItsScale()
    {
        var range = GraphAxisRanges.FromValues(1e9, 2e9);

        Assert.Equal(0.95e9, range.Minimum, 1);
        Assert.Equal(2.05e9, range.Maximum, 1);
    }

    // 6
    [Fact]
    public void AConstantPositiveValueGetsAWindowAroundItsOwnMagnitude()
    {
        AssertRange(99.5, 100.5, GraphAxisRanges.FromValues(100, 100));
    }

    // 7
    [Fact]
    public void AConstantNegativeValueGetsTheSameWindow()
    {
        AssertRange(-100.5, -99.5, GraphAxisRanges.FromValues(-100, -100));
    }

    // 8
    [Fact]
    public void AConstantZeroGetsTheFixedFallbackWindow()
    {
        AssertRange(-GraphAxisRanges.ZeroHalfSpan, GraphAxisRanges.ZeroHalfSpan, GraphAxisRanges.FromValues(0, 0));
    }

    // 9
    [Fact]
    public void AConstantValueTooSmallForARelativeWindowFallsBackToTheFixedOne()
    {
        // The relative half span underflows to zero here, so the fixed window is used instead of a degenerate range.
        var range = GraphAxisRanges.FromValues(double.Epsilon, double.Epsilon);

        Assert.True(range.IsValid);
        AssertRange(-GraphAxisRanges.ZeroHalfSpan, GraphAxisRanges.ZeroHalfSpan, range);
    }

    // 10
    [Fact]
    public void AValueAtTheLimitOfDoubleStillProducesAUsableRange()
    {
        Assert.True(GraphAxisRanges.FromValues(double.MaxValue, double.MaxValue).IsValid);
        Assert.True(GraphAxisRanges.FromValues(-double.MaxValue, double.MaxValue).IsValid);
    }

    // 11
    [Fact]
    public void EveryRangeItProducesIsValid()
    {
        double[] values = [-1e12, -7.5, -1, -0.0004, 0, 0.0004, 1, 7.5, 1e12];

        foreach (var minimum in values)
        {
            foreach (var maximum in values.Where(value => value >= minimum))
            {
                Assert.True(GraphAxisRanges.FromValues(minimum, maximum).IsValid, $"{minimum}..{maximum}");
            }
        }
    }

    // 12
    [Fact]
    public void TheSameValuesAlwaysGiveTheSameRange()
    {
        Assert.Equal(GraphAxisRanges.FromValues(-3, 17), GraphAxisRanges.FromValues(-3, 17));
        Assert.Equal(GraphAxisRanges.FromValues(5, 5), GraphAxisRanges.FromValues(5, 5));
    }

    // 13
    [Theory]
    [InlineData(10d, 5d)]
    [InlineData(double.NaN, 1d)]
    [InlineData(0d, double.NaN)]
    [InlineData(double.NegativeInfinity, 0d)]
    [InlineData(0d, double.PositiveInfinity)]
    public void ValuesThatAreNotARangeAreRejected(double minimum, double maximum)
    {
        Assert.Throws<ArgumentException>(() => GraphAxisRanges.FromValues(minimum, maximum));
    }
}
