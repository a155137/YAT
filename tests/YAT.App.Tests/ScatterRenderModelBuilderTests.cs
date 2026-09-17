using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Turning the observations of a scatter plot into what is drawn: series, axis ranges, ticks and legend. No worksheet
// and no storage here - the graph data is the input.
public class ScatterRenderModelBuilderTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly ScatterPlotLabels Labels = new("Reg1", "Reg2", "SITE");

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static ScatterGraphData Data(double[] x, double[] y, GraphGroupData? group = null) =>
        new(WorksheetId, Column("Reg1"), Column("Reg2"), x, y, group);

    private static StringGroupData Text(params string?[] values) =>
        new(Column("SITE", WorksheetDataType.String), values);

    private static NumericGroupData Numbers(params double?[] values) =>
        new(Column("SITE"), values);

    private static ScatterRenderModel Build(ScatterGraphData data, int maximumRenderedPoints = ScatterRenderModelBuilder.DefaultMaximumRenderedPoints) =>
        new ScatterRenderModelBuilder(maximumRenderedPoints).Build(data, Labels, TestContext.Current.CancellationToken)
        ?? throw new InvalidOperationException("The scatter plot has no data.");

    private static IReadOnlyList<ScatterPoint> PointsOf(ScatterRenderModel model, string label) =>
        model.Series.Single(series => series.Label == label).Points.ToArray();

    // 1
    [Fact]
    public void WithoutAGroupColumnThereIsOneSeriesAndNoLegend()
    {
        var model = Build(Data([1, 2, 3], [10, 20, 30]));

        var series = Assert.Single(model.Series);
        Assert.Equal(string.Empty, series.Label);
        Assert.Equal(0, series.SeriesIndex);
        Assert.Equal([new ScatterPoint(1, 10), new ScatterPoint(2, 20), new ScatterPoint(3, 30)], series.Points.ToArray());
        Assert.Null(model.Frame.Legend);
    }

    // 2
    [Fact]
    public void AStringGroupBecomesOneSeriesPerValueInFirstObservedOrder()
    {
        var model = Build(Data([1, 2, 3, 4], [10, 20, 30, 40], Text("B", "A", "B", "A")));

        Assert.Equal(["B", "A"], model.Series.Select(series => series.Label));
        Assert.Equal([0, 1], model.Series.Select(series => series.SeriesIndex));
        Assert.Equal([new ScatterPoint(1, 10), new ScatterPoint(3, 30)], PointsOf(model, "B"));
        Assert.Equal([new ScatterPoint(2, 20), new ScatterPoint(4, 40)], PointsOf(model, "A"));
    }

    // 3
    [Fact]
    public void ANumericGroupBecomesOneSeriesPerValue()
    {
        var model = Build(Data([1, 2, 3], [10, 20, 30], Numbers(2, 1.5, 2)));

        Assert.Equal(["2", "1.5"], model.Series.Select(series => series.Label));
        Assert.Equal([new ScatterPoint(1, 10), new ScatterPoint(3, 30)], PointsOf(model, "2"));
        Assert.Equal([new ScatterPoint(2, 20)], PointsOf(model, "1.5"));
    }

    // 4
    [Fact]
    public void ObservationsWithoutAGroupValueAreKeptInTheirOwnSeries()
    {
        var model = Build(Data([1, 2, 3], [10, 20, 30], Text("A", null, "A")));

        Assert.Equal(["A", ScatterRenderModelBuilder.MissingGroupLabel], model.Series.Select(series => series.Label));
        Assert.Equal([new ScatterPoint(2, 20)], PointsOf(model, ScatterRenderModelBuilder.MissingGroupLabel));
        Assert.Equal(3, model.RenderedPointCount);
    }

    // 5
    [Fact]
    public void AMissingNumericGroupValueIsTreatedTheSameWay()
    {
        var model = Build(Data([1, 2], [10, 20], Numbers(null, 7)));

        Assert.Equal([ScatterRenderModelBuilder.MissingGroupLabel, "7"], model.Series.Select(series => series.Label));
    }

    // 6
    [Fact]
    public void XYAndGroupStayOnTheSameRow()
    {
        // Row 2 is the only one of group "A"; its X and Y must travel together into that series.
        var model = Build(Data([1, 2, 3], [-10, -20, -30], Text("B", "A", "B")));

        Assert.Equal([new ScatterPoint(2, -20)], PointsOf(model, "A"));
        Assert.Equal([new ScatterPoint(1, -10), new ScatterPoint(3, -30)], PointsOf(model, "B"));
    }

    // 7
    [Fact]
    public void TheLegendMirrorsTheSeriesAndCarriesTheGroupColumnName()
    {
        var model = Build(Data([1, 2, 3], [10, 20, 30], Text("B", "A", null)));

        Assert.NotNull(model.Frame.Legend);
        Assert.Equal("SITE", model.Frame.Legend.Title);
        Assert.Equal(
            model.Series.Select(series => (series.Label, series.SeriesIndex)),
            model.Frame.Legend.Entries.Select(entry => (entry.Label, entry.SeriesIndex)));
    }

    // 8
    [Fact]
    public void TheFrameIsTitledAfterTheColumnsItPlots()
    {
        var model = Build(Data([1, 2], [10, 20]));

        Assert.Equal("Scatterplot of Reg2 vs Reg1", model.Frame.Title);
        Assert.Equal("Reg1", model.Frame.XAxis.Title);
        Assert.Equal("Reg2", model.Frame.YAxis.Title);
    }

    // 9
    [Fact]
    public void TheAxesCoverTheDataWithTheRangePolicyAndGetNiceTicks()
    {
        var model = Build(Data([0, 100], [0, 500]));

        Assert.Equal(GraphAxisRanges.FromValues(0, 100), model.Frame.XAxis.Range);
        Assert.Equal(GraphAxisRanges.FromValues(0, 500), model.Frame.YAxis.Range);
        Assert.Equal([0, 20, 40, 60, 80, 100], model.Frame.XAxis.Ticks.Select(tick => tick.Value));
        Assert.All(model.Frame.YAxis.Ticks, tick => Assert.InRange(tick.Value, model.Frame.YAxis.Range.Minimum, model.Frame.YAxis.Range.Maximum));
    }

    // 10
    [Fact]
    public void ConstantDataStillGetsAUsableAxis()
    {
        var model = Build(Data([100, 100, 100], [0, 0, 0]));

        Assert.True(model.Frame.XAxis.Range.IsValid);
        Assert.True(model.Frame.YAxis.Range.IsValid);
        Assert.Equal(GraphAxisRanges.FromValues(100, 100), model.Frame.XAxis.Range);
        Assert.Equal(GraphAxisRanges.FromValues(0, 0), model.Frame.YAxis.Range);
        Assert.Equal(3, model.RenderedPointCount);
    }

    // 11
    [Fact]
    public void NoObservationsMeansNoScatterPlotAtAll()
    {
        Assert.Null(new ScatterRenderModelBuilder().Build(Data([], []), Labels, TestContext.Current.CancellationToken));
    }

    // 12
    [Fact]
    public void ObservationsThatCannotBePlacedAreLeftOut()
    {
        // The raw store rejects non-finite numbers; the builder still refuses to turn one into geometry.
        var model = Build(Data([1, double.NaN, 3, 5], [10, 20, double.PositiveInfinity, 50]));

        Assert.Equal([new ScatterPoint(1, 10), new ScatterPoint(5, 50)], Assert.Single(model.Series).Points.ToArray());
        Assert.Equal(2, model.SourcePointCount);
    }

    // 13
    [Fact]
    public void AGraphOfNothingButUnplottableObservationsIsNoGraph()
    {
        Assert.Null(new ScatterRenderModelBuilder().Build(
            Data([double.NaN], [double.NaN]), Labels, TestContext.Current.CancellationToken));
    }

    // 14
    [Fact]
    public void PreparationCanBeCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new ScatterRenderModelBuilder().Build(Data([1, 2], [3, 4]), Labels, cancellation.Token));
    }

    // 15
    [Fact]
    public void TheBuilderNeedsDataAndLabels()
    {
        var builder = new ScatterRenderModelBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.Build(null!, Labels, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => builder.Build(Data([1], [1]), null!, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScatterRenderModelBuilder(0));
    }

    // 16
    [Fact]
    public void ThePreparedModelIsTheSameEveryTime()
    {
        var data = Data([1, 2, 3, 4], [10, 20, 30, 40], Text("A", "B", null, "A"));

        var first = Build(data);
        var second = Build(data);

        Assert.Equal(
            first.Series.Select(series => (series.Label, series.SeriesIndex, series.Points.ToArray())),
            second.Series.Select(series => (series.Label, series.SeriesIndex, series.Points.ToArray())));
        Assert.Equal(first.Frame.XAxis.Range, second.Frame.XAxis.Range);
    }
}
