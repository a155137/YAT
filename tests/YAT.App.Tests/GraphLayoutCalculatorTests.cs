using SkiaSharp;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// How a graph divides the canvas it is given. Sizes stand in for measured text, so the arithmetic is checked without a
// font: the renderer measures for real and passes the same kind of numbers in.
public class GraphLayoutCalculatorTests
{
    private static readonly GraphLayoutMetrics Metrics = new()
    {
        TitleHeight = 20,
        AxisTitleHeight = 14,
        TickLabelHeight = 13,
        YTickLabelWidth = 30,
        XTickLabelOverflow = 10,
        LegendWidth = 80
    };

    private static readonly SKRect Canvas = new(0, 0, 800, 600);

    private static GraphAxisModel Axis(double minimum, double maximum, string? title) =>
        new(new GraphAxisRange(minimum, maximum), GraphAxisTicks.Evenly(new GraphAxisRange(minimum, maximum)), title);

    private static GraphRenderModel Model(
        string? title = "Sample Graph",
        string? xTitle = "X Axis",
        string? yTitle = "Y Axis",
        GraphLegendModel? legend = null) =>
        new(title, Axis(0, 100, xTitle), Axis(0, 500, yTitle), legend);

    private static GraphLegendModel Legend() => new([new GraphLegendEntry("Lot A", 0), new GraphLegendEntry("Lot B", 1)]);

    private static void AssertInside(SKRect outer, SKRect inner)
    {
        Assert.True(inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom,
            $"{inner} is not inside {outer}.");
    }

    // 1
    [Fact]
    public void EveryAreaLiesInsideTheCanvasAndNoTwoAreasOverlap()
    {
        var layout = GraphLayoutCalculator.Calculate(Canvas, Model(legend: Legend()), Metrics);

        var areas = new[] { layout.TitleArea, layout.PlotArea, layout.XAxisArea, layout.YAxisArea, layout.LegendArea };
        Assert.All(areas, area => AssertInside(Canvas, area));

        for (var first = 0; first < areas.Length; first++)
        {
            for (var second = first + 1; second < areas.Length; second++)
            {
                Assert.False(areas[first].IntersectsWith(areas[second]), $"{areas[first]} overlaps {areas[second]}.");
            }
        }
    }

    // 2
    [Fact]
    public void TheAxisAreasSitBelowAndLeftOfThePlotArea()
    {
        var layout = GraphLayoutCalculator.Calculate(Canvas, Model(), Metrics);

        Assert.Equal(layout.PlotArea.Bottom, layout.XAxisArea.Top);
        Assert.Equal(layout.PlotArea.Left, layout.XAxisArea.Left);
        Assert.Equal(layout.PlotArea.Right, layout.XAxisArea.Right);

        Assert.Equal(layout.PlotArea.Left, layout.YAxisArea.Right);
        Assert.Equal(layout.PlotArea.Top, layout.YAxisArea.Top);
        Assert.Equal(layout.PlotArea.Bottom, layout.YAxisArea.Bottom);

        Assert.True(layout.XAxisArea.Height >= Metrics.TickLength + Metrics.TickLabelHeight + Metrics.AxisTitleHeight);
        Assert.True(layout.YAxisArea.Width >= Metrics.TickLength + Metrics.YTickLabelWidth + Metrics.AxisTitleHeight);
    }

    // 3
    [Fact]
    public void ThePlotAreaFollowsTheCanvasWhenTheWindowIsResized()
    {
        var small = GraphLayoutCalculator.Calculate(new SKRect(0, 0, 400, 300), Model(), Metrics);
        var large = GraphLayoutCalculator.Calculate(new SKRect(0, 0, 1600, 1200), Model(), Metrics);

        Assert.True(large.PlotArea.Width > small.PlotArea.Width);
        Assert.True(large.PlotArea.Height > small.PlotArea.Height);

        // The frame around the plot keeps its measured size: growing the window grows the plot, not the labels.
        Assert.Equal(small.YAxisArea.Width, large.YAxisArea.Width, 1e-3f);
        Assert.Equal(small.XAxisArea.Height, large.XAxisArea.Height, 1e-3f);
    }

    // 4
    [Fact]
    public void ALayoutIsTheSameEveryTimeItIsCalculated()
    {
        Assert.Equal(
            GraphLayoutCalculator.Calculate(Canvas, Model(), Metrics),
            GraphLayoutCalculator.Calculate(Canvas, Model(), Metrics));
    }

    // 5
    [Fact]
    public void AGraphWithoutATitleGivesTheSpaceBackToThePlotArea()
    {
        var titled = GraphLayoutCalculator.Calculate(Canvas, Model(), Metrics);
        var untitled = GraphLayoutCalculator.Calculate(Canvas, Model(title: null), Metrics);

        Assert.True(untitled.TitleArea.IsEmpty);
        Assert.True(untitled.PlotArea.Height > titled.PlotArea.Height);
        Assert.False(titled.TitleArea.IsEmpty);
        Assert.True(titled.TitleArea.Bottom <= titled.PlotArea.Top);
    }

    // 6
    [Fact]
    public void AxisTitlesOnlyTakeSpaceWhenTheAxesHaveThem()
    {
        var titled = GraphLayoutCalculator.Calculate(Canvas, Model(), Metrics);
        var untitled = GraphLayoutCalculator.Calculate(Canvas, Model(xTitle: null, yTitle: "  "), Metrics);

        Assert.True(untitled.PlotArea.Width > titled.PlotArea.Width);
        Assert.True(untitled.PlotArea.Height > titled.PlotArea.Height);
    }

    // 7
    [Fact]
    public void TheLegendAreaExistsOnlyForAGraphWithSeries()
    {
        var without = GraphLayoutCalculator.Calculate(Canvas, Model(), Metrics);
        var with = GraphLayoutCalculator.Calculate(Canvas, Model(legend: Legend()), Metrics);

        Assert.True(without.LegendArea.IsEmpty);
        Assert.False(with.LegendArea.IsEmpty);
        Assert.Equal(Metrics.LegendWidth, with.LegendArea.Width, 1e-3f);
        Assert.True(with.LegendArea.Left >= with.PlotArea.Right);
        Assert.True(with.PlotArea.Width < without.PlotArea.Width);
    }

    // 8
    [Fact]
    public void ThePlotAreaKeepsRoomForTheLabelsThatOverhangIt()
    {
        var layout = GraphLayoutCalculator.Calculate(Canvas, Model(), Metrics);

        // Half of the widest X tick label reaches past the right edge of the plot, half of a tick label above its top.
        Assert.True(Canvas.Right - layout.PlotArea.Right >= Metrics.XTickLabelOverflow);
        Assert.True(layout.PlotArea.Top - layout.TitleArea.Bottom >= Metrics.TickLabelHeight / 2);
    }

    // 9
    [Theory]
    [InlineData(40f, 400f)]
    [InlineData(400f, 40f)]
    [InlineData(1f, 1f)]
    [InlineData(0f, 0f)]
    public void ACanvasTooSmallForAPlotIsLaidOutAsNothingButTheCanvas(float width, float height)
    {
        var layout = GraphLayoutCalculator.Calculate(new SKRect(0, 0, width, height), Model(), Metrics);

        Assert.False(layout.HasPlotArea);
        Assert.True(layout.PlotArea.IsEmpty);
        Assert.True(layout.TitleArea.IsEmpty);
        Assert.True(layout.XAxisArea.IsEmpty);
        Assert.True(layout.YAxisArea.IsEmpty);
        Assert.True(layout.LegendArea.IsEmpty);
        Assert.Equal(new SKRect(0, 0, width, height), layout.Canvas);
    }

    // 10
    [Fact]
    public void ThePlotAreaIsWhatIsLeftOfTheCanvas()
    {
        var layout = GraphLayoutCalculator.Calculate(Canvas, Model(), Metrics);

        Assert.True(layout.HasPlotArea);
        Assert.True(layout.PlotArea.Width > 0);
        Assert.True(layout.PlotArea.Height > 0);
        AssertInside(Canvas, layout.PlotArea);
    }

    // 11
    [Fact]
    public void ALayoutNeedsFiniteNonNegativeMetricsAndAFiniteCanvas()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GraphLayoutCalculator.Calculate(Canvas, Model(), Metrics with { TickLabelHeight = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GraphLayoutCalculator.Calculate(Canvas, Model(), Metrics with { YTickLabelWidth = float.NaN }));
        Assert.Throws<ArgumentException>(() =>
            GraphLayoutCalculator.Calculate(new SKRect(0, 0, float.NaN, 600), Model(), Metrics));
        Assert.Throws<ArgumentNullException>(() => GraphLayoutCalculator.Calculate(Canvas, null!, Metrics));
        Assert.Throws<ArgumentNullException>(() => GraphLayoutCalculator.Calculate(Canvas, Model(), null!));
    }

    // 12
    [Fact]
    public void TheLayoutStartsFromTheCanvasItWasGivenWhereverItIs()
    {
        var offset = GraphLayoutCalculator.Calculate(new SKRect(200, 100, 1000, 700), Model(), Metrics);
        var origin = GraphLayoutCalculator.Calculate(Canvas, Model(), Metrics);

        Assert.Equal(origin.PlotArea.Width, offset.PlotArea.Width, 1e-3f);
        Assert.Equal(origin.PlotArea.Height, offset.PlotArea.Height, 1e-3f);
        Assert.Equal(origin.PlotArea.Left + 200, offset.PlotArea.Left, 1e-3f);
        Assert.Equal(origin.PlotArea.Top + 100, offset.PlotArea.Top, 1e-3f);
    }

    // ---- Reference line label band (#036) ----

    [Fact]
    public void AZeroLabelBandLeavesTheLayoutAsItWas()
    {
        var model = Model(legend: Legend());

        Assert.Equal(
            GraphLayoutCalculator.Calculate(Canvas, model, Metrics),
            GraphLayoutCalculator.Calculate(Canvas, model, Metrics with { ReferenceLabelHeight = 0 }));
        Assert.True(GraphLayoutCalculator.Calculate(Canvas, model, Metrics).ReferenceLabelArea.IsEmpty);
    }

    [Fact]
    public void ALabelBandSitsDirectlyAboveThePlotAcrossItsWidth()
    {
        var model = Model();
        var plain = GraphLayoutCalculator.Calculate(Canvas, model, Metrics);
        var banded = GraphLayoutCalculator.Calculate(Canvas, model, Metrics with { ReferenceLabelHeight = 40 });
        var band = banded.ReferenceLabelArea;

        Assert.Equal(40, band.Height, 1e-3f);
        Assert.Equal(banded.PlotArea.Top, band.Bottom);
        Assert.Equal(banded.PlotArea.Left, band.Left);
        Assert.Equal(banded.PlotArea.Right, band.Right);
        Assert.True(band.Top >= banded.TitleArea.Bottom);

        // It replaces the half tick label of headroom the plot kept anyway, so the plot loses only the difference.
        Assert.Equal(plain.PlotArea.Top + 40 - (Metrics.TickLabelHeight / 2f), banded.PlotArea.Top, 1e-3f);
        Assert.Equal(plain.PlotArea.Left, banded.PlotArea.Left);
        Assert.Equal(plain.PlotArea.Right, banded.PlotArea.Right);
        Assert.Equal(plain.PlotArea.Bottom, banded.PlotArea.Bottom);
    }

    [Fact]
    public void ALabelBandLowerThanHalfATickLabelDoesNotMoveThePlot()
    {
        var model = Model();
        var plain = GraphLayoutCalculator.Calculate(Canvas, model, Metrics);
        var banded = GraphLayoutCalculator.Calculate(Canvas, model, Metrics with { ReferenceLabelHeight = Metrics.TickLabelHeight / 4f });

        Assert.Equal(plain.PlotArea, banded.PlotArea);
        Assert.False(banded.ReferenceLabelArea.IsEmpty);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void ANegativeOrNonFiniteLabelBandIsRejected(float height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphLayoutCalculator.Calculate(Canvas, Model(), Metrics with { ReferenceLabelHeight = height }));
}
