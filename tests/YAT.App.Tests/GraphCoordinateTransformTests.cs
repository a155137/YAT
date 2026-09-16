using SkiaSharp;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// Data values to screen coordinates: the one place where the Y axis is flipped, and the one place that decides what
// happens at and beyond the ends of an axis.
public class GraphCoordinateTransformTests
{
    // 400 x 400, so that a value and its coordinate are easy to read.
    private static readonly SKRect PlotArea = new(100, 50, 500, 450);

    private const double Tolerance = 1e-9;

    private static GraphCoordinateTransform Transform(
        double xMinimum = 0,
        double xMaximum = 100,
        double yMinimum = 0,
        double yMaximum = 500,
        SKRect? plotArea = null) =>
        new(new GraphAxisRange(xMinimum, xMaximum), new GraphAxisRange(yMinimum, yMaximum), plotArea ?? PlotArea);

    // 1
    [Fact]
    public void TheMinimumIsTheLeftAndTheBottomOfThePlotArea()
    {
        var transform = Transform();

        Assert.Equal(PlotArea.Left, transform.ToScreenX(0), Tolerance);
        Assert.Equal(PlotArea.Bottom, transform.ToScreenY(0), Tolerance);
    }

    // 2
    [Fact]
    public void TheMaximumIsTheRightAndTheTopOfThePlotArea()
    {
        var transform = Transform();

        Assert.Equal(PlotArea.Right, transform.ToScreenX(100), Tolerance);
        Assert.Equal(PlotArea.Top, transform.ToScreenY(500), Tolerance);
    }

    // 3
    [Fact]
    public void TheMidpointIsTheCentreOfThePlotArea()
    {
        var transform = Transform();

        Assert.Equal(PlotArea.MidX, transform.ToScreenX(50), Tolerance);
        Assert.Equal(PlotArea.MidY, transform.ToScreenY(250), Tolerance);
    }

    // 4
    [Fact]
    public void ScreenYGrowsDownwardsWhileDataYGrowsUpwards()
    {
        var transform = Transform();

        Assert.True(transform.ToScreenY(400) < transform.ToScreenY(100));
        Assert.True(transform.ToScreenX(40) < transform.ToScreenX(60));
    }

    // 5
    [Fact]
    public void RangesThatDoNotStartAtZeroMapTheSameWay()
    {
        var transform = Transform(xMinimum: 10, xMaximum: 20, yMinimum: 1000, yMaximum: 1100);

        Assert.Equal(PlotArea.Left, transform.ToScreenX(10), Tolerance);
        Assert.Equal(PlotArea.MidX, transform.ToScreenX(15), Tolerance);
        Assert.Equal(PlotArea.Right, transform.ToScreenX(20), Tolerance);
        Assert.Equal(PlotArea.Bottom, transform.ToScreenY(1000), Tolerance);
        Assert.Equal(PlotArea.MidY, transform.ToScreenY(1050), Tolerance);
        Assert.Equal(PlotArea.Top, transform.ToScreenY(1100), Tolerance);
    }

    // 6
    [Fact]
    public void NegativeRangesMapTheSameWay()
    {
        var transform = Transform(xMinimum: -50, xMaximum: -10, yMinimum: -8, yMaximum: -4);

        Assert.Equal(PlotArea.Left, transform.ToScreenX(-50), Tolerance);
        Assert.Equal(PlotArea.MidX, transform.ToScreenX(-30), Tolerance);
        Assert.Equal(PlotArea.Right, transform.ToScreenX(-10), Tolerance);
        Assert.Equal(PlotArea.Bottom, transform.ToScreenY(-8), Tolerance);
        Assert.Equal(PlotArea.MidY, transform.ToScreenY(-6), Tolerance);
        Assert.Equal(PlotArea.Top, transform.ToScreenY(-4), Tolerance);
    }

    // 7
    [Fact]
    public void ValuesOutsideTheRangeAreExtrapolatedAndNeverClamped()
    {
        var transform = Transform();

        Assert.Equal(PlotArea.Left - 400, transform.ToScreenX(-100), Tolerance);
        Assert.Equal(PlotArea.Right + 400, transform.ToScreenX(200), Tolerance);
        Assert.Equal(PlotArea.Bottom + 400, transform.ToScreenY(-500), Tolerance);
        Assert.Equal(PlotArea.Top - 400, transform.ToScreenY(1000), Tolerance);
    }

    // 8
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteValuesBecomeNaNAndNeverAUsableCoordinate(double value)
    {
        var transform = Transform();

        Assert.True(double.IsNaN(transform.ToScreenX(value)));
        Assert.True(double.IsNaN(transform.ToScreenY(value)));

        var point = transform.ToScreenPoint(value, value);
        Assert.True(float.IsNaN(point.X));
        Assert.True(float.IsNaN(point.Y));
    }

    // 9
    [Fact]
    public void APointIsTheTwoAxesTogether()
    {
        var transform = Transform();
        var point = transform.ToScreenPoint(50, 250);

        Assert.Equal(PlotArea.MidX, point.X, 1e-3f);
        Assert.Equal(PlotArea.MidY, point.Y, 1e-3f);
    }

    // 10
    [Fact]
    public void ADegenerateOrUnsetRangeIsRejectedWhenTheTransformIsBuilt()
    {
        Assert.Throws<ArgumentException>(() => new GraphCoordinateTransform(default, new GraphAxisRange(0, 1), PlotArea));
        Assert.Throws<ArgumentException>(() => new GraphCoordinateTransform(new GraphAxisRange(0, 1), default, PlotArea));
    }

    // 11
    [Theory]
    [InlineData(0f, 0f, 0f, 0f)]
    [InlineData(10f, 10f, 10f, 40f)]
    [InlineData(10f, 10f, 40f, 10f)]
    [InlineData(0f, 0f, float.NaN, 10f)]
    [InlineData(0f, 0f, float.PositiveInfinity, 10f)]
    public void APlotAreaWithoutAPositiveAreaIsRejected(float left, float top, float right, float bottom)
    {
        Assert.Throws<ArgumentException>(() =>
            new GraphCoordinateTransform(new GraphAxisRange(0, 1), new GraphAxisRange(0, 1), new SKRect(left, top, right, bottom)));
    }

    // 12
    [Fact]
    public void TheTransformRemembersWhatItWasBuiltFrom()
    {
        var transform = Transform();

        Assert.Equal(new GraphAxisRange(0, 100), transform.XRange);
        Assert.Equal(new GraphAxisRange(0, 500), transform.YRange);
        Assert.Equal(PlotArea, transform.PlotArea);
    }
}
