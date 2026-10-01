using YAT.Application.Abstractions.Persistence;
using YAT.Application.Graphs;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.Infrastructure.Tests;

// A graph's filter (Task #049) over real DuckDB raw storage: the rows it keeps across read windows, columns of different
// stored lengths, the filter column beside or as the group column, and the distinct values a filter is chosen from.
public class GraphDataFilterIntegrationTests
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
            new(new DuckDbRawDataStoreSettings(":memory:", memoryLimitBytes: 512L * 1024 * 1024, threads: 2));

        public Worksheet Worksheet { get; } = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

        public GraphDataQueryService Service => new(Worksheets, Columns, RawStore);

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

        public Task<GraphData> LoadAsync(GraphConfiguration configuration) => Service.LoadAsync(configuration, Token);

        public void Dispose() => RawStore.Dispose();
    }

    private static UnivariateGraphData OneVariable(GraphData data) =>
        Assert.Single(Assert.IsType<MultiVariableGraphData>(data).Variables);

    private static double?[] NumericGroups(GraphData data) => [.. ((NumericGroupData)data.Group!).Values.ToArray()];

    private static string?[] StringGroups(GraphData data) => [.. ((StringGroupData)data.Group!).Values.ToArray()];

    [Fact]
    public async Task TheGroupColumnFiltersItsOwnGraphOverStoredRows()
    {
        using var composition = new Composition();
        var reg = await composition.AddNumericAsync("PS_RAW", 0, [10, 20, 30, 40, 50, 60, null, 80]);
        var site = await composition.AddNumericAsync("Site", 1, [1, 2, 3, 4, 2, 6, 2, 2]);

        var data = OneVariable(await composition.LoadAsync(composition.Configuration(
            GraphType.Histogram, new NumericValueFilter(site.Id, [2]),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, site))));

        Assert.Equal([20, 50, 80], data.Values.ToArray());
        Assert.Equal([2, 2, 2], NumericGroups(data));
    }

    [Fact]
    public async Task AnotherColumnFiltersAGroupedGraph()
    {
        using var composition = new Composition();
        var reg = await composition.AddNumericAsync("PS_RAW", 0, [10, 20, 30, 40, 50, 60]);
        var tester = await composition.AddStringAsync("Tester", 1, ["T01", "T02", "T01", "T03", "T02", "T01"]);
        var site = await composition.AddNumericAsync("Site", 2, [1, 2, 3, 5, 7, 8]);

        var data = OneVariable(await composition.LoadAsync(composition.Configuration(
            GraphType.ProbabilityPlot, new NumericValueFilter(site.Id, [1, 3, 5, 7]),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, tester))));

        Assert.Equal([10, 30, 40, 50], data.Values.ToArray());
        Assert.Equal(["T01", "T01", "T03", "T02"], StringGroups(data));
    }

    [Fact]
    public async Task RowsBeyondAShorterFilterColumnAreMissing()
    {
        using var composition = new Composition();
        var x = await composition.AddNumericAsync("X", 0, [1, 2, 3, 4, 5]);
        var y = await composition.AddNumericAsync("Y", 1, [10, 20, 30, 40, 50]);
        var lot = await composition.AddStringAsync("Lot", 2, ["A", null, "B"]);

        var data = Assert.IsType<ScatterGraphData>(await composition.LoadAsync(composition.Configuration(
            GraphType.ScatterPlot, new TextValueFilter(lot.Id, ["A"], includeMissing: true),
            (GraphVariableRole.X, x), (GraphVariableRole.Y, y))));

        Assert.Equal([1, 2, 4, 5], data.XValues.ToArray());
        Assert.Equal([10, 20, 40, 50], data.YValues.ToArray());
    }

    [Fact]
    public async Task SeveralVariablesKeepTheirOwnRowsUnderTheFilter()
    {
        using var composition = new Composition();
        var a = await composition.AddNumericAsync("A", 0, [1, null, 3, 4, 5]);
        var b = await composition.AddNumericAsync("B", 1, [10, 20, 30]);
        var site = await composition.AddNumericAsync("Site", 2, [2, 2, 1, 2, 2]);

        var data = Assert.IsType<MultiVariableGraphData>(await composition.LoadAsync(composition.Configuration(
            GraphType.BoxPlot, new NumericValueFilter(site.Id, [2]),
            (GraphVariableRole.Variable, a), (GraphVariableRole.Variable, b), (GraphVariableRole.Group, site))));

        Assert.Equal([1, 4, 5], data.Variables[0].Values.ToArray());
        Assert.Equal([2, 2, 2], NumericGroups(data.Variables[0]));
        Assert.Equal([10, 20], data.Variables[1].Values.ToArray());
        Assert.Equal([2, 2], NumericGroups(data.Variables[1]));
    }

    [Fact]
    public async Task ALargeWorksheetIsFilteredAcrossItsReadWindows()
    {
        using var composition = new Composition();
        const int rowCount = 2 * GraphDataQueryService.ReadChunkRowCount + 11;
        var values = new double?[rowCount];
        var sites = new string?[rowCount];
        for (var row = 0; row < rowCount; row++)
        {
            values[row] = row;
            sites[row] = row % 10 == 9 ? null : $"S{row % 8 + 1}";
        }

        var reg = await composition.AddNumericAsync("Reg", 0, values);
        var site = await composition.AddStringAsync("Site", 1, sites);

        var data = OneVariable(await composition.LoadAsync(composition.Configuration(
            GraphType.EmpiricalCdf, new TextValueFilter(site.Id, ["S2", "S5"], includeMissing: true),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, site))));

        var expected = Enumerable.Range(0, rowCount)
            .Where(row => sites[row] is null or "S2" or "S5")
            .ToArray();
        Assert.Equal(expected.Select(row => (double)row), data.Values.ToArray());
        Assert.Equal(expected.Select(row => sites[row]), StringGroups(data));
    }

    [Fact]
    public async Task WithoutAFilterTheGraphDataIsUnchanged()
    {
        using var composition = new Composition();
        var reg = await composition.AddNumericAsync("Reg", 0, [1, null, 3, 4]);
        var lot = await composition.AddStringAsync("Lot", 1, ["A", "B", null]);

        var unfiltered = OneVariable(await composition.LoadAsync(composition.Configuration(
            GraphType.Histogram, null, (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, lot))));

        Assert.Equal([1, 3, 4], unfiltered.Values.ToArray());
        Assert.Equal(["A", null, null], StringGroups(unfiltered));
    }

    [Fact]
    public async Task EveryListedValueSelectedIsCanonicallyNoFilterAndKeepsEveryRow()
    {
        using var composition = new Composition();
        var reg = await composition.AddNumericAsync("Reg", 0, [1, 2, 3, 4, 5]);
        var site = await composition.AddNumericAsync("Site", 1, [3, null, 1, 3, 2]);

        var available = Assert.IsType<NumericRawDistinctValues>(await composition.RawStore.GetDistinctValuesAsync(
            composition.Worksheet.Id, site.Id, GraphValueFilter.MaximumDistinctValues, Token));
        var everything = new NumericValueFilter(site.Id, available.Values, includeMissing: available.HasMissing);

        Assert.Equal([3, 1, 2], available.Values);
        Assert.Null(GraphValueFilter.Canonicalize(everything, available));

        var filtered = OneVariable(await composition.LoadAsync(composition.Configuration(
            GraphType.Histogram, everything, (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, site))));
        var unfiltered = OneVariable(await composition.LoadAsync(composition.Configuration(
            GraphType.Histogram, null, (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, site))));

        Assert.Equal(unfiltered.Values.ToArray(), filtered.Values.ToArray());
        Assert.Equal(NumericGroups(unfiltered), NumericGroups(filtered));
    }
}
