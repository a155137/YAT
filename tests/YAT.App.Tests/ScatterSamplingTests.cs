using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The display cap: a scatter plot draws at most a fixed number of points, chosen the same way every time. The graph
// data behind it is never touched, and what is drawn is reported by the model.
public class ScatterSamplingTests
{
    private static readonly ScatterPlotLabels Labels = new("Reg1", "Reg2");

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    // X = row index, Y = row index * 2, so every point says which row it came from.
    private static ScatterGraphData Rows(int count, string?[]? groups = null)
    {
        var x = new double[count];
        var y = new double[count];
        for (var row = 0; row < count; row++)
        {
            x[row] = row;
            y[row] = row * 2;
        }

        return new ScatterGraphData(
            Guid.NewGuid(),
            Column("Reg1"),
            Column("Reg2"),
            x,
            y,
            groups is null ? null : new StringGroupData(Column("SITE", WorksheetDataType.String), groups));
    }

    private static ScatterRenderModel Build(ScatterGraphData data, int cap) =>
        new ScatterRenderModelBuilder(cap).Build(data, Labels, TestContext.Current.CancellationToken)!;

    // 1
    [Fact]
    public void BelowTheCapEveryPointIsDrawn()
    {
        var model = Build(Rows(99), 100);

        Assert.Equal(99, model.SourcePointCount);
        Assert.Equal(99, model.RenderedPointCount);
        Assert.False(model.WasSampled);
        Assert.Equal(Enumerable.Range(0, 99).Select(row => (double)row), Assert.Single(model.Series).Points.ToArray().Select(point => point.X));
    }

    // 2
    [Fact]
    public void ExactlyAtTheCapEveryPointIsDrawn()
    {
        var model = Build(Rows(100), 100);

        Assert.Equal(100, model.RenderedPointCount);
        Assert.False(model.WasSampled);
    }

    // 3
    [Fact]
    public void AboveTheCapTheCapIsTheHardLimit()
    {
        var model = Build(Rows(1_000), 100);

        Assert.Equal(1_000, model.SourcePointCount);
        Assert.Equal(100, model.RenderedPointCount);
        Assert.True(model.WasSampled);
    }

    // 4
    [Fact]
    public void ASampleKeepsTheFirstAndTheLastObservation()
    {
        var points = Assert.Single(Build(Rows(1_000), 100).Series).Points.ToArray();

        Assert.Equal(0, points[0].X);
        Assert.Equal(999, points[^1].X);
    }

    // 5
    [Fact]
    public void ASampleIsSpreadOverTheWholeSeries()
    {
        var points = Assert.Single(Build(Rows(1_000), 100).Series).Points.ToArray();

        // Evenly spread source indexes, so the shape of the data survives being thinned out.
        Assert.Equal(points.Select(point => point.X).Order(), points.Select(point => point.X));
        Assert.Distinct(points.Select(point => point.X));
        Assert.All(points, point => Assert.Equal(point.X * 2, point.Y));
    }

    // 6
    [Fact]
    public void TheSameDataAlwaysDrawsTheSamePoints()
    {
        var data = Rows(10_000, [.. Enumerable.Range(0, 10_000).Select(row => (string?)(row % 7).ToString())]);

        var first = Build(data, 500);
        var second = Build(data, 500);

        Assert.Equal(
            first.Series.Select(series => (series.Label, series.Points.ToArray())),
            second.Series.Select(series => (series.Label, series.Points.ToArray())));
    }

    // 7
    [Fact]
    public void ASmallGroupIsNotSampledAway()
    {
        // One thousand observations of "A" and a single "B": the one point of "B" must still be drawn.
        var groups = new string?[1_001];
        Array.Fill(groups, "A");
        groups[500] = "B";

        var model = Build(Rows(1_001, groups), 100);

        Assert.Equal(100, model.RenderedPointCount);
        Assert.Equal([new ScatterPoint(500, 1_000)], model.Series.Single(series => series.Label == "B").Points.ToArray());
    }

    // 8
    [Fact]
    public void EveryGroupIsRepresentedWhileTheBudgetAllows()
    {
        var groups = new string?[5_000];
        for (var row = 0; row < groups.Length; row++)
        {
            groups[row] = $"Lot{row % 50}";
        }

        var model = Build(Rows(5_000, groups), 200);

        Assert.Equal(50, model.Series.Count);
        Assert.All(model.Series, series => Assert.True(series.Points.Length >= 1));
        Assert.True(model.RenderedPointCount <= 200);
    }

    // 9
    [Fact]
    public void MoreGroupsThanTheCapStillRespectTheCap()
    {
        // A categorical column with more values than the cap allows points: the first groups in first-observed order
        // are drawn with one point each, and the cap is not broken.
        var groups = new string?[20];
        for (var row = 0; row < groups.Length; row++)
        {
            groups[row] = $"Lot{row}";
        }

        var model = Build(Rows(20, groups), 5);

        Assert.Equal(5, model.RenderedPointCount);
        Assert.Equal(5, model.Series.Count);
        Assert.Equal(["Lot0", "Lot1", "Lot2", "Lot3", "Lot4"], model.Series.Select(series => series.Label));
        Assert.All(model.Series, series => Assert.Equal(1, series.Points.Length));
    }

    // 10
    [Theory]
    [InlineData(1_000, 1, 7)]
    [InlineData(1_000, 3, 7)]
    [InlineData(1_000, 37, 100)]
    [InlineData(1_000, 250, 300)]
    [InlineData(1_000, 999, 1_000)]
    [InlineData(5_000, 1, 4_999)]
    public void TheCapHoldsForEveryShapeOfData(int rows, int groupCount, int cap)
    {
        var groups = new string?[rows];
        for (var row = 0; row < rows; row++)
        {
            groups[row] = $"Lot{row % groupCount}";
        }

        var model = Build(Rows(rows, groups), cap);

        Assert.True(model.RenderedPointCount <= cap, $"{model.RenderedPointCount} > {cap}");
        Assert.Equal(rows, model.SourcePointCount);
        Assert.Equal(model.RenderedPointCount, model.Series.Sum(series => series.Points.Length));
    }

    // 11
    [Fact]
    public void TheLegendOnlyNamesSeriesThatAreDrawn()
    {
        var groups = new string?[20];
        for (var row = 0; row < groups.Length; row++)
        {
            groups[row] = $"Lot{row}";
        }

        var model = Build(Rows(20, groups), 5);

        Assert.NotNull(model.Frame.Legend);
        Assert.Equal(model.Series.Select(series => series.Label), model.Frame.Legend.Entries.Select(entry => entry.Label));
    }

    // 12
    [Fact]
    public void TheAxesCoverEveryObservationAndNotJustTheSample()
    {
        var full = Build(Rows(1_000), 10_000);
        var sampled = Build(Rows(1_000), 100);

        Assert.True(sampled.WasSampled);
        Assert.Equal(full.Frame.XAxis.Range, sampled.Frame.XAxis.Range);
        Assert.Equal(full.Frame.YAxis.Range, sampled.Frame.YAxis.Range);
    }
}
