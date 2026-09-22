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

    // ---- Including: the range reached out to values that must be seen on it (#036) ----

    private static readonly GraphAxisRange Data = new(10, 20);

    [Fact]
    public void ValuesStrictlyInsideLeaveTheRangeAsItIs()
    {
        Assert.Equal(Data, GraphAxisRanges.Including(Data, [10.001, 15, 19.999]));
        Assert.Equal(Data, GraphAxisRanges.Including(Data, []));
    }

    [Fact]
    public void AValueBelowMovesOnlyTheLowerEdgeAndPadsIt()
    {
        // New span 8..20 = 12; padding 5% of that = 0.6 below the value. The upper edge keeps the data's boundary.
        AssertRange(7.4, 20, GraphAxisRanges.Including(Data, [8]));
    }

    [Fact]
    public void AValueAboveMovesOnlyTheUpperEdgeAndPadsIt()
    {
        AssertRange(10, 25.75, GraphAxisRanges.Including(Data, [25]));
    }

    [Fact]
    public void ValuesOnBothSidesMoveBothEdges()
    {
        // Span 0..30 = 30, padding 1.5 on each moved side.
        AssertRange(-1.5, 31.5, GraphAxisRanges.Including(Data, [0, 15, 30]));
    }

    [Fact]
    public void AValueExactlyOnAnEdgeMovesThatEdgeOffIt()
    {
        var atMinimum = GraphAxisRanges.Including(Data, [10]);
        var atMaximum = GraphAxisRanges.Including(Data, [20]);

        AssertRange(9.5, 20, atMinimum);
        AssertRange(10, 20.5, atMaximum);
        Assert.True(atMinimum.Minimum < 10);
        Assert.True(atMaximum.Maximum > 20);
    }

    [Fact]
    public void IncludingTheSameValuesAgainChangesNothing()
    {
        double[] values = [0, 10, 20, 35];
        var once = GraphAxisRanges.Including(Data, values);
        var twice = GraphAxisRanges.Including(once, values);

        Assert.Equal(once, twice);
        Assert.Equal(twice, GraphAxisRanges.Including(twice, values));
    }

    [Fact]
    public void EveryIncludedValueEndsUpStrictlyInside()
    {
        double[] values = [-1e-9, 10, 20, 1e6];
        var range = GraphAxisRanges.Including(Data, values);

        Assert.All(values, value => Assert.True(value > range.Minimum && value < range.Maximum, $"{value:R} is not inside {range}."));
    }

    [Theory]
    [InlineData(1.7976931348623157e308)]
    [InlineData(-1.7976931348623157e308)]
    public void AValueTheRangeCannotReachKeepsTheRangeAsItIs(double extreme)
    {
        // 1e308 padded is past double.MaxValue: rather than an infinite axis, the graph keeps its own.
        Assert.Equal(Data, GraphAxisRanges.Including(Data, [extreme]));
        Assert.Equal(Data, GraphAxisRanges.Including(Data, [-extreme, extreme]));
    }

    [Fact]
    public void PaddingThatRoundsAwayKeepsTheRangeAsItIs()
    {
        // At 1e16 a double steps by 2, so padding a span of 1 by 5% cannot move the edge off the value.
        var huge = new GraphAxisRange(1e16, 1e16 + 4);

        Assert.Equal(huge, GraphAxisRanges.Including(huge, [1e16]));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonFiniteValuesAreRejected(double value) =>
        Assert.Throws<ArgumentException>(() => GraphAxisRanges.Including(Data, [value]));
}
