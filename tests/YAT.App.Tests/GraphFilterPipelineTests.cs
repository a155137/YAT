using YAT.Application.Abstractions.Persistence;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.App.Tests;

// A graph's filter (Task #049) through the graph pipeline, from real DuckDB storage to the graph types' own builders:
// the order is Filter -> GraphData -> builders and statistics -> display sampling, so the statistics describe the kept
// rows and a graph's display sample is drawn from them alone. No builder knows there was a filter.
public class GraphFilterPipelineTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Composition : IDisposable
    {
        public Composition()
        {
            Worksheets.AddAsync(Worksheet, CancellationToken.None).GetAwaiter().GetResult();
        }

        public InMemoryWorksheetRepository Worksheets { get; } = new();

        public InMemoryWorksheetColumnRepository Columns { get; } = new();

        public DuckDbWorksheetRawDataStore RawStore { get; } =
            new(new DuckDbRawDataStoreSettings(":memory:", memoryLimitBytes: 256L * 1024 * 1024, threads: 1));

        public Worksheet Worksheet { get; } = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

        public async Task<WorksheetColumn> AddNumericAsync(string name, int index, IReadOnlyList<double?> values)
        {
            var column = await AddColumnAsync(name, index, WorksheetDataType.Numeric);
            await RawStore.WriteColumnsAsync(Worksheet.Id, new RawDataBlock([new NumericRawDataColumn(column.Id, values)]), Token);
            return column;
        }

        public async Task<WorksheetColumn> AddStringAsync(string name, int index, IReadOnlyList<string?> values)
        {
            var column = await AddColumnAsync(name, index, WorksheetDataType.String);
            await RawStore.WriteColumnsAsync(Worksheet.Id, new RawDataBlock([new StringRawDataColumn(column.Id, values)]), Token);
            return column;
        }

        private async Task<WorksheetColumn> AddColumnAsync(string name, int index, WorksheetDataType dataType)
        {
            var column = new WorksheetColumn
            {
                Id = Guid.NewGuid(),
                WorksheetId = Worksheet.Id,
                Index = index,
                Name = name,
                DataType = dataType
            };
            await Columns.AddAsync(column, Token);
            return column;
        }

        public GraphConfiguration Configuration(
            GraphType graphType,
            GraphValueFilter? filter,
            params (GraphVariableRole Role, WorksheetColumn Column)[] assignments) =>
            new(graphType, Worksheet.Id, [.. assignments.Select(item => new GraphColumnAssignment(item.Role, item.Column.Id))])
            {
                Filter = filter
            };

        public Task<GraphData> LoadAsync(GraphConfiguration configuration) =>
            new GraphDataQueryService(Worksheets, Columns, RawStore).LoadAsync(configuration, Token);

        public void Dispose() => RawStore.Dispose();
    }

    private static UnivariateGraphData OneVariable(GraphData data) =>
        Assert.Single(Assert.IsType<MultiVariableGraphData>(data).Variables);

    // 1,000 rows over eight sites; Site 2 has only the seven rows listed, spread over the worksheet.
    private static readonly int[] SiteTwoRows = [3, 150, 151, 480, 702, 903, 999];

    private static (double?[] Values, double?[] Sites) Worksheet1000()
    {
        var values = new double?[1_000];
        var sites = new double?[1_000];
        for (var row = 0; row < values.Length; row++)
        {
            values[row] = row * 0.5;
            sites[row] = row % 7 == 0 ? 1 : row % 7 + 2;
        }

        foreach (var row in SiteTwoRows)
        {
            sites[row] = 2;
        }

        return (values, sites);
    }

    // ---- Statistics ----

    [Fact]
    public async Task TheStatisticsPanelDescribesOnlyTheKeptRows()
    {
        using var composition = new Composition();
        var (values, sites) = Worksheet1000();
        var reg = await composition.AddNumericAsync("PS_RAW", 0, values);
        var site = await composition.AddNumericAsync("Site", 1, sites);
        var configuration = composition.Configuration(
            GraphType.Histogram, new NumericValueFilter(site.Id, [2]),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, site));

        var data = OneVariable(await composition.LoadAsync(configuration));
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("PS_RAW", "Site"), Token)!;
        var panel = GraphPresentation.Present(model.Frame, data, configuration, Token).BaseFrame.StatisticsPanel!;

        var row = Assert.Single(panel.Rows);
        Assert.Equal("2", row.Label);
        Assert.Equal(SiteTwoRows.Length, row.Count);
        Assert.Equal(SiteTwoRows.Average(index => index * 0.5), row.Mean, 12);
        Assert.Equal(SiteTwoRows.Length, model.Series.Sum(series => series.ObservationCount));
    }

    [Fact]
    public async Task AFilterOnAnotherColumnLeavesEachGroupItsKeptRows()
    {
        using var composition = new Composition();
        var reg = await composition.AddNumericAsync("PS_RAW", 0, [1, 2, 3, 4, 5, 6, 7, 8]);
        var tester = await composition.AddStringAsync("Tester", 1, ["T1", "T2", "T1", "T2", "T1", "T2", "T1", "T2"]);
        var site = await composition.AddNumericAsync("Site", 2, [1, 1, 2, 3, 3, 5, 7, 8]);

        var data = OneVariable(await composition.LoadAsync(composition.Configuration(
            GraphType.EmpiricalCdf, new NumericValueFilter(site.Id, [1, 3, 5, 7]),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, tester))));
        var panel = GraphStatisticsPanelBuilder.Build(data, Token)!;

        // Kept: rows 1, 2, 4, 5, 6, 7 -> T1: 1, 5, 7; T2: 2, 4, 6.
        Assert.Equal(["T1", "T2"], panel.Rows.Select(row => row.Label));
        Assert.Equal([3, 3], panel.Rows.Select(row => row.Count));
        Assert.Equal([13.0 / 3, 4.0], panel.Rows.Select(row => row.Mean));
    }

    // ---- Display sampling ----

    [Fact]
    public async Task AScatterPlotSamplesOnlyTheKeptRows()
    {
        using var composition = new Composition();
        var (values, sites) = Worksheet1000();
        var x = await composition.AddNumericAsync("X", 0, values);
        var y = await composition.AddNumericAsync("Y", 1, values.Select(value => value * 2).ToArray());
        var site = await composition.AddNumericAsync("Site", 2, sites);
        var builder = new ScatterRenderModelBuilder(maximumRenderedPoints: 10);

        var filtered = builder.Build(
            Assert.IsType<ScatterGraphData>(await composition.LoadAsync(composition.Configuration(
                GraphType.ScatterPlot, new NumericValueFilter(site.Id, [2]),
                (GraphVariableRole.X, x), (GraphVariableRole.Y, y), (GraphVariableRole.Group, site)))),
            new ScatterPlotLabels("X", "Y", "Site"),
            Token)!;
        var unfiltered = builder.Build(
            Assert.IsType<ScatterGraphData>(await composition.LoadAsync(composition.Configuration(
                GraphType.ScatterPlot, null,
                (GraphVariableRole.X, x), (GraphVariableRole.Y, y), (GraphVariableRole.Group, site)))),
            new ScatterPlotLabels("X", "Y", "Site"),
            Token)!;

        // Filtered first, Site 2 is the whole graph: every one of its points fits the budget and is drawn.
        var series = Assert.Single(filtered.Series);
        Assert.Equal(SiteTwoRows.Length, filtered.SourcePointCount);
        Assert.Equal(SiteTwoRows.Select(row => new ScatterPoint(row * 0.5, row * 1.0)), series.Points.ToArray());

        // Sampled from the whole worksheet, the same budget would have drawn only some of them.
        var siteTwo = unfiltered.Series.Single(candidate => candidate.Label == "2");
        Assert.True(siteTwo.Points.Length < SiteTwoRows.Length);
    }

    [Fact]
    public async Task AnEmpiricalCdfSamplesOnlyTheKeptRows()
    {
        using var composition = new Composition();
        var (values, sites) = Worksheet1000();
        var reg = await composition.AddNumericAsync("PS_RAW", 0, values);
        var site = await composition.AddNumericAsync("Site", 1, sites);

        var data = OneVariable(await composition.LoadAsync(composition.Configuration(
            GraphType.EmpiricalCdf, new NumericValueFilter(site.Id, [2]), (GraphVariableRole.Variable, reg))));
        var model = new EmpiricalCdfRenderModelBuilder(maximumRenderedPoints: 10).Build(data, new EmpiricalCdfLabels("PS_RAW"), Token)!;

        var series = Assert.Single(model.Series);
        Assert.Equal(SiteTwoRows.Length, model.SourceObservationCount);
        Assert.Equal(SiteTwoRows.Length, series.ObservationCount);
        Assert.Equal(SiteTwoRows.Select(row => row * 0.5), series.Points.ToArray().Select(point => point.Value).Distinct());
    }

    // ---- Box plot ----

    [Fact]
    public async Task ABoxPlotOfSeveralVariablesFilteredToOneGroupHasOneBoxPerVariable()
    {
        using var composition = new Composition();
        var a = await composition.AddNumericAsync("A", 0, [1, 2, 3, 4, 5, 6]);
        var b = await composition.AddNumericAsync("B", 1, [10, 20, 30, 40, 50, 60]);
        var c = await composition.AddNumericAsync("C", 2, [100, 200, null, 400, 500, 600]);
        var site = await composition.AddNumericAsync("Site", 3, [1, 2, 2, 3, 2, 1]);

        var data = Assert.IsType<MultiVariableGraphData>(await composition.LoadAsync(composition.Configuration(
            GraphType.BoxPlot, new NumericValueFilter(site.Id, [2]),
            (GraphVariableRole.Variable, a), (GraphVariableRole.Variable, b), (GraphVariableRole.Variable, c),
            (GraphVariableRole.Group, site))));
        var model = new BoxPlotRenderModelBuilder().Build(data, new BoxPlotLabels(["A", "B", "C"], "Site"), Token)!;

        Assert.Equal(["A / 2", "B / 2", "C / 2"], model.Categories);
        Assert.Equal([3, 3, 2], model.Boxes.Select(box => box.ObservationCount));
        Assert.All(model.Boxes, box => Assert.Equal(0, box.SeriesIndex));
        Assert.Equal(["2"], model.Frame.Legend!.Entries.Select(entry => entry.Label));
    }

    // ---- Nothing kept ----

    [Fact]
    public async Task AFilterThatKeepsNoRowLeavesEveryGraphTypeNothingToDraw()
    {
        using var composition = new Composition();
        var reg = await composition.AddNumericAsync("Reg", 0, [1, 2, 3]);
        var other = await composition.AddNumericAsync("Other", 1, [4, 5, 6]);
        var site = await composition.AddNumericAsync("Site", 2, [1, 1, 1]);
        var nothing = new NumericValueFilter(site.Id, [2]);

        var scatter = Assert.IsType<ScatterGraphData>(await composition.LoadAsync(composition.Configuration(
            GraphType.ScatterPlot, nothing, (GraphVariableRole.X, reg), (GraphVariableRole.Y, other))));
        var histogram = OneVariable(await composition.LoadAsync(composition.Configuration(
            GraphType.Histogram, nothing, (GraphVariableRole.Variable, reg))));
        var probability = OneVariable(await composition.LoadAsync(composition.Configuration(
            GraphType.ProbabilityPlot, nothing, (GraphVariableRole.Variable, reg))));
        var empiricalCdf = OneVariable(await composition.LoadAsync(composition.Configuration(
            GraphType.EmpiricalCdf, nothing, (GraphVariableRole.Variable, reg))));
        var boxPlot = Assert.IsType<MultiVariableGraphData>(await composition.LoadAsync(composition.Configuration(
            GraphType.BoxPlot, nothing, (GraphVariableRole.Variable, reg))));

        Assert.Null(new ScatterRenderModelBuilder().Build(scatter, new ScatterPlotLabels("Reg", "Other"), Token));
        Assert.Null(new HistogramRenderModelBuilder().Build(histogram, new HistogramPlotLabels("Reg"), Token));
        Assert.Null(new ProbabilityPlotRenderModelBuilder().Build(probability, new ProbabilityPlotLabels("Reg"), Token));
        Assert.Null(new EmpiricalCdfRenderModelBuilder().Build(empiricalCdf, new EmpiricalCdfLabels("Reg"), Token));
        Assert.Null(new BoxPlotRenderModelBuilder().Build(boxPlot, new BoxPlotLabels(["Reg"]), Token));
        Assert.Null(GraphStatisticsPanelBuilder.Build(histogram, Token));
    }
}
