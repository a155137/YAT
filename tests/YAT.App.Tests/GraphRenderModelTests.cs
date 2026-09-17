using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// The render model and the axis tick policy: what a graph may describe, and what it must refuse to describe. No
// rendering and no layout here.
public class GraphRenderModelTests
{
    private static GraphAxisModel Axis(double minimum, double maximum, string? title = null) =>
        new(new GraphAxisRange(minimum, maximum), GraphAxisTicks.Evenly(new GraphAxisRange(minimum, maximum)), title);

    // 1
    [Fact]
    public void AxisRangeKeepsItsBoundsAndSpan()
    {
        var range = new GraphAxisRange(-10, 40);

        Assert.Equal(-10, range.Minimum);
        Assert.Equal(40, range.Maximum);
        Assert.Equal(50, range.Span);
        Assert.True(range.IsValid);
    }

    // 2
    [Theory]
    [InlineData(5d, 5d)]
    [InlineData(10d, 4d)]
    [InlineData(double.NaN, 10d)]
    [InlineData(0d, double.NaN)]
    [InlineData(double.NegativeInfinity, 0d)]
    [InlineData(0d, double.PositiveInfinity)]
    [InlineData(double.MinValue, double.MaxValue)]
    public void AxisRangeRejectsEmptyAndNonFiniteRanges(double minimum, double maximum)
    {
        Assert.Throws<ArgumentException>(() => new GraphAxisRange(minimum, maximum));
    }

    // 3
    [Fact]
    public void TheDefaultAxisRangeIsNotAValidRange()
    {
        var range = default(GraphAxisRange);

        Assert.False(range.IsValid);
        Assert.Throws<ArgumentException>(() => new GraphAxisModel(range, []));
    }

    // 4
    [Fact]
    public void EvenTicksCoverTheWholeRangeWithExactEnds()
    {
        var ticks = GraphAxisTicks.Evenly(new GraphAxisRange(0, 500), 6);

        Assert.Equal([0, 100, 200, 300, 400, 500], ticks.Select(tick => tick.Value));
        Assert.Equal(["0", "100", "200", "300", "400", "500"], ticks.Select(tick => tick.Label));
    }

    // 5
    [Fact]
    public void EvenTicksOfANonZeroRangeStayWithinTheRange()
    {
        var range = new GraphAxisRange(-2.5, 7.5);
        var ticks = GraphAxisTicks.Evenly(range, 5);

        Assert.Equal(range.Minimum, ticks[0].Value);
        Assert.Equal(range.Maximum, ticks[^1].Value);
        Assert.All(ticks, tick => Assert.InRange(tick.Value, range.Minimum, range.Maximum));
        Assert.Equal(["-2.5", "0", "2.5", "5", "7.5"], ticks.Select(tick => tick.Label));
    }

    // 6
    [Fact]
    public void TickLabelsAreInvariantAndDeterministic()
    {
        var range = new GraphAxisRange(0, 1);

        Assert.Equal(GraphAxisTicks.Evenly(range).Select(tick => tick.Label), GraphAxisTicks.Evenly(range).Select(tick => tick.Label));
        Assert.Equal("0.3333", GraphAxisTicks.Label(1d / 3d));
        Assert.Equal("1.50", GraphAxisTicks.Label(1.5, "0.00"));
    }

    // 7
    [Fact]
    public void FewerThanTwoTicksIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphAxisTicks.Evenly(new GraphAxisRange(0, 1), 1));
    }

    // 8
    [Fact]
    public void ATickNeedsAFiniteValueAndALabel()
    {
        Assert.Throws<ArgumentException>(() => new GraphAxisTick(double.NaN, "x"));
        Assert.Throws<ArgumentNullException>(() => new GraphAxisTick(0, null!));
    }

    // 9
    [Fact]
    public void AnAxisCopiesItsTicksAndKeepsItsOptionalTitle()
    {
        var ticks = new List<GraphAxisTick> { new(0, "0"), new(1, "1") };
        var axis = new GraphAxisModel(new GraphAxisRange(0, 1), ticks, "Reg2");

        ticks.Add(new GraphAxisTick(2, "2"));

        Assert.Equal(2, axis.Ticks.Count);
        Assert.Equal("Reg2", axis.Title);
        Assert.Null(new GraphAxisModel(new GraphAxisRange(0, 1), []).Title);
    }

    // 10
    [Fact]
    public void ALegendNeedsAtLeastOneEntry()
    {
        Assert.Throws<ArgumentException>(() => new GraphLegendModel([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphLegendEntry("Lot A", -1));
        Assert.Throws<ArgumentNullException>(() => new GraphLegendEntry(null!, 0));
    }

    // 11
    [Fact]
    public void AGraphWithoutSeriesHasNoLegendAtAll()
    {
        var model = new GraphRenderModel("Sample Graph", Axis(0, 100, "X Axis"), Axis(0, 500, "Y Axis"));

        Assert.Null(model.Legend);
    }

    // 12
    [Fact]
    public void ARenderModelNeedsBothAxes()
    {
        Assert.Throws<ArgumentNullException>(() => new GraphRenderModel("Sample Graph", null!, Axis(0, 1)));
        Assert.Throws<ArgumentNullException>(() => new GraphRenderModel("Sample Graph", Axis(0, 1), null!));
    }
}
