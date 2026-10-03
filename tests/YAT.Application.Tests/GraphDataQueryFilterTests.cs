using YAT.Application.Filtering;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Graphs;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// The rows a graph's filter keeps (Task #049), as the data query reads them: decided per worksheet row before the graph's
// null rules, typed exact matching, Missing on its own, the filter column read once in the same aligned windows, and no
// change at all without a filter.
public class GraphDataQueryFilterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Fixture
    {
        public Fixture()
        {
            Worksheets.Seed(new Worksheet { Id = WorksheetId, Name = "Sheet1" });
            Service = new GraphDataQueryService(Worksheets, Columns, RawData);
        }

        public Guid WorksheetId { get; } = Guid.NewGuid();

        public FakeWorksheetRepository Worksheets { get; } = new();

        public FakeWorksheetColumnRepository Columns { get; } = new();

        public FakeWorksheetRawDataStore RawData { get; } = new();

        public GraphDataQueryService Service { get; }

        public WorksheetColumn Column(string name, WorksheetDataType dataType, int index)
        {
            var column = new WorksheetColumn
            {
                Id = Guid.NewGuid(),
                WorksheetId = WorksheetId,
                Index = index,
                Name = name,
                DataType = dataType
            };
            Columns.Seed(column);
            return column;
        }

        public WorksheetColumn Numeric(string name, int index, params double?[] values)
        {
            var column = Column(name, WorksheetDataType.Numeric, index);
            RawData.Seed(WorksheetId, new NumericRawDataColumn(column.Id, values));
            return column;
        }

        public WorksheetColumn Text(string name, int index, params string?[] values)
        {
            var column = Column(name, WorksheetDataType.String, index);
            RawData.Seed(WorksheetId, new StringRawDataColumn(column.Id, values));
            return column;
        }

        public GraphConfiguration Configuration(
            GraphType graphType,
            ValueSetCondition? filter,
            params (GraphVariableRole Role, WorksheetColumn Column)[] assignments) =>
            new(graphType, WorksheetId, [.. assignments.Select(item => new GraphColumnAssignment(item.Role, item.Column.Id))])
            {
                Filter = filter is null ? null : new RowFilter(filter)
            };

        public Task<GraphData> LoadAsync(GraphConfiguration configuration) => Service.LoadAsync(configuration, Token);
    }

    private static UnivariateGraphData OneVariable(GraphData data) =>
        Assert.Single(Assert.IsType<MultiVariableGraphData>(data).Variables);

    private static (double X, double Y)[] Points(ScatterGraphData data) =>
        [.. Enumerable.Range(0, data.Count).Select(index => (data.XValues.Span[index], data.YValues.Span[index]))];

    private static string?[] StringGroups(GraphData data) => [.. ((StringGroupData)data.Group!).Values.ToArray()];

    private static double?[] NumericGroups(GraphData data) => [.. ((NumericGroupData)data.Group!).Values.ToArray()];

    // ---- No filter ----

    [Fact]
    public async Task WithoutAFilterTheSameColumnsAreReadAndEveryRowIsKept()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 0, 1, 2, null, 4);
        var y = fixture.Numeric("Y", 1, 10, 20, 30, 40);
        var group = fixture.Text("Lot", 2, "A", null, "B", "A");
        fixture.Numeric("Site", 3, 1, 2, 3, 4);

        var data = Assert.IsType<ScatterGraphData>(await fixture.LoadAsync(fixture.Configuration(
            GraphType.ScatterPlot, null, (GraphVariableRole.X, x), (GraphVariableRole.Y, y), (GraphVariableRole.Group, group))));

        Assert.Equal([(1, 10), (2, 20), (4, 40)], Points(data));
        Assert.Equal(["A", null, "A"], StringGroups(data));
        var read = Assert.Single(fixture.RawData.Reads);
        Assert.Equal([x.Id, y.Id, group.Id], read.ColumnIds);
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    [InlineData(GraphType.BoxPlot)]
    public async Task WithoutAFilterAVariableGraphIsReadExactlyAsBefore(GraphType graphType)
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg", 0, 5, null, 7, 8);
        var site = fixture.Numeric("Site", 1, 1, 2, null, 1);

        var data = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            graphType, null, (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, site))));

        Assert.Equal([5, 7, 8], data.Values.ToArray());
        Assert.Equal([1, null, 1], NumericGroups(data));
        Assert.Equal([reg.Id, site.Id], Assert.Single(fixture.RawData.Reads).ColumnIds);
    }

    // ---- Values ----

    [Fact]
    public async Task OneSelectedNumberKeepsOnlyItsRows()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("PS_RAW", 0, 10, 20, 30, 40, 50, 60);
        var site = fixture.Numeric("Site", 1, 1, 2, 3, 2, 1, 2);

        var data = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new NumericValueSetCondition(site.Id, [2]),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, site))));

        Assert.Equal([20, 40, 60], data.Values.ToArray());
        Assert.Equal([2, 2, 2], NumericGroups(data));
    }

    [Fact]
    public async Task SeveralSelectedValuesKeepTheirRowsInWorksheetOrder()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("PS_RAW", 0, 10, 20, 30, 40, 50, 60, 70, 80);
        var site = fixture.Numeric("Site", 1, 1, 2, 3, 4, 5, 6, 7, 8);

        var data = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new NumericValueSetCondition(site.Id, [7, 1, 5, 3]),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, site))));

        Assert.Equal([10, 30, 50, 70], data.Values.ToArray());
        Assert.Equal([1, 3, 5, 7], NumericGroups(data));
    }

    [Fact]
    public async Task EveryValueSelectedKeepsWhatNoFilterKeeps()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("PS_RAW", 0, 10, 20, null, 40);
        var tester = fixture.Text("Tester", 1, "T01", "T02", "T01", "T03");

        var all = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.EmpiricalCdf, new TextValueSetCondition(tester.Id, ["T01", "T02", "T03"]),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, tester))));
        var none = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.EmpiricalCdf, null, (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, tester))));

        Assert.Equal(none.Values.ToArray(), all.Values.ToArray());
        Assert.Equal(StringGroups(none), StringGroups(all));
    }

    [Fact]
    public async Task NumbersMatchExactly()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg", 0, 1, 2, 3, 4, 5);
        var site = fixture.Numeric("Site", 1, 1.0, 1.0000000000000002, 0.30000000000000004, 0.3, -0.0);

        var exact = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new NumericValueSetCondition(site.Id, [1.0, 0.1 + 0.2]), (GraphVariableRole.Variable, reg))));
        var zero = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new NumericValueSetCondition(site.Id, [0.0]), (GraphVariableRole.Variable, reg))));

        Assert.Equal([1, 3], exact.Values.ToArray());
        Assert.Equal([5], zero.Values.ToArray());
    }

    [Fact]
    public async Task TextMatchesOrdinally()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg", 0, 1, 2, 3, 4, 5, 6);
        var lot = fixture.Text("Lot", 1, "a", "A", "1", "01", "a ", "");

        var data = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new TextValueSetCondition(lot.Id, ["a", "01", ""]), (GraphVariableRole.Variable, reg))));

        Assert.Equal([1, 4, 6], data.Values.ToArray());
    }

    [Fact]
    public async Task NumericLookingTextIsText()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg", 0, 1, 2, 3);
        var site = fixture.Text("Site", 1, "1", "1.0", "2");

        var data = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new TextValueSetCondition(site.Id, ["1"]),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, site))));

        Assert.Equal([1], data.Values.ToArray());
        Assert.Equal(["1"], StringGroups(data));
    }

    // ---- Missing ----

    [Fact]
    public async Task MissingSelectedKeepsRowsWithoutAValue()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg", 0, 1, 2, 3, 4, 5);
        var lot = fixture.Text("Lot", 1, "A", null, "B", null);

        var onlyMissing = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new TextValueSetCondition(lot.Id, [], includeMissing: true),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, lot))));
        var missingAndA = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new TextValueSetCondition(lot.Id, ["A"], includeMissing: true),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, lot))));

        // Row 5 is beyond the Lot column: it has no Lot value either.
        Assert.Equal([2, 4, 5], onlyMissing.Values.ToArray());
        Assert.Equal([null, null, null], StringGroups(onlyMissing));
        Assert.Equal([1, 2, 4, 5], missingAndA.Values.ToArray());
    }

    [Fact]
    public async Task MissingNotSelectedDropsRowsWithoutAValue()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg", 0, 1, 2, 3, 4, 5);
        var site = fixture.Numeric("Site", 1, 1, null, 2, null);

        var data = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new NumericValueSetCondition(site.Id, [1, 2]), (GraphVariableRole.Variable, reg))));

        Assert.Equal([1, 3], data.Values.ToArray());
    }

    [Fact]
    public async Task AFilterColumnWithoutStoredValuesIsMissingInEveryRow()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg", 0, 1, 2, 3);
        var site = fixture.Column("Site", WorksheetDataType.Numeric, 1);

        var missing = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new NumericValueSetCondition(site.Id, [], includeMissing: true), (GraphVariableRole.Variable, reg))));
        var values = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new NumericValueSetCondition(site.Id, [1]), (GraphVariableRole.Variable, reg))));

        Assert.Equal([1, 2, 3], missing.Values.ToArray());
        Assert.Empty(values.Values.ToArray());
        Assert.All(fixture.RawData.Reads, read => Assert.DoesNotContain(site.Id, read.ColumnIds));
    }

    // ---- The filter column ----

    [Fact]
    public async Task TheGroupColumnIsReadOnceWhenItIsAlsoTheFilterColumn()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg", 0, 1, 2, 3);
        var site = fixture.Numeric("Site", 1, 1, 2, 1);

        await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new NumericValueSetCondition(site.Id, [1]),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, site)));

        Assert.Equal([reg.Id, site.Id], Assert.Single(fixture.RawData.Reads).ColumnIds);
    }

    [Fact]
    public async Task AFilterColumnThatIsNotTheGroupIsReadAlongside()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("PS_RAW", 0, 10, 20, 30, 40, 50, 60);
        var tester = fixture.Text("Tester", 1, "T1", "T2", "T1", "T2", "T1", "T2");
        var site = fixture.Numeric("Site", 2, 1, 1, 3, 3, 2, 5);

        var data = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new NumericValueSetCondition(site.Id, [1, 3, 5, 7]),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, tester))));

        Assert.Equal([10, 20, 30, 40, 60], data.Values.ToArray());
        Assert.Equal(["T1", "T2", "T1", "T2", "T2"], StringGroups(data));
        Assert.Equal([reg.Id, tester.Id, site.Id], Assert.Single(fixture.RawData.Reads).ColumnIds);
    }

    [Fact]
    public async Task AFilterOnAMeasuredColumnSharesItsRead()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 0, 1, 2, 1, 3);
        var y = fixture.Numeric("Y", 1, 10, 20, 30, 40);

        var data = Assert.IsType<ScatterGraphData>(await fixture.LoadAsync(fixture.Configuration(
            GraphType.ScatterPlot, new NumericValueSetCondition(x.Id, [1]), (GraphVariableRole.X, x), (GraphVariableRole.Y, y))));

        Assert.Equal([(1, 10), (1, 30)], Points(data));
        Assert.Equal([x.Id, y.Id], Assert.Single(fixture.RawData.Reads).ColumnIds);
    }

    [Fact]
    public async Task ScatterKeepsOnlyFilteredRowsWithBothValues()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 0, 1, 2, 3, null, 5);
        var y = fixture.Numeric("Y", 1, 10, null, 30, 40, 50);
        var lot = fixture.Text("Lot", 2, "A", "A", "B", "A", "A");

        var data = Assert.IsType<ScatterGraphData>(await fixture.LoadAsync(fixture.Configuration(
            GraphType.ScatterPlot, new TextValueSetCondition(lot.Id, ["A"]),
            (GraphVariableRole.X, x), (GraphVariableRole.Y, y), (GraphVariableRole.Group, lot))));

        Assert.Equal([(1, 10), (5, 50)], Points(data));
        Assert.Equal(["A", "A"], StringGroups(data));
    }

    // ---- Several variables ----

    [Fact]
    public async Task EveryVariableKeepsTheGroupOfItsOwnRowAfterTheFilter()
    {
        var fixture = new Fixture();
        var a = fixture.Numeric("A", 0, 1, null, 3, 4, 5, 6);
        var b = fixture.Numeric("B", 1, null, 20, 30, null, 50);
        var c = fixture.Numeric("C", 2, 100, 200, 300, 400, 500, 600);
        var lot = fixture.Text("Lot", 3, "L1", "L2", "L1", "L2", "L1", "L2");
        var site = fixture.Numeric("Site", 4, 2, 2, 1, 2, 2, 1);

        var data = Assert.IsType<MultiVariableGraphData>(await fixture.LoadAsync(fixture.Configuration(
            GraphType.BoxPlot, new NumericValueSetCondition(site.Id, [2]),
            (GraphVariableRole.Variable, a), (GraphVariableRole.Variable, b), (GraphVariableRole.Variable, c),
            (GraphVariableRole.Group, lot))));

        // Site 2 is rows 1, 2, 4 and 5.
        Assert.Equal([1, 4, 5], data.Variables[0].Values.ToArray());
        Assert.Equal(["L1", "L2", "L1"], StringGroups(data.Variables[0]));
        Assert.Equal([20, 50], data.Variables[1].Values.ToArray());
        Assert.Equal(["L2", "L1"], StringGroups(data.Variables[1]));
        Assert.Equal([100, 200, 400, 500], data.Variables[2].Values.ToArray());
        Assert.Equal(["L1", "L2", "L2", "L1"], StringGroups(data.Variables[2]));
        Assert.Equal(9, data.Count);
        Assert.Equal([a.Id, b.Id, c.Id, lot.Id, site.Id], Assert.Single(fixture.RawData.Reads).ColumnIds);
    }

    [Fact]
    public async Task AVariableTheFilterLeavesWithoutRowsIsStillThere()
    {
        var fixture = new Fixture();
        var a = fixture.Numeric("A", 0, 1, 2, 3);
        var b = fixture.Numeric("B", 1, null, 20, null);
        var site = fixture.Numeric("Site", 2, 1, 2, 1);

        var data = Assert.IsType<MultiVariableGraphData>(await fixture.LoadAsync(fixture.Configuration(
            GraphType.BoxPlot, new NumericValueSetCondition(site.Id, [1]), (GraphVariableRole.Variable, a), (GraphVariableRole.Variable, b))));

        Assert.Equal([1, 3], data.Variables[0].Values.ToArray());
        Assert.Empty(data.Variables[1].Values.ToArray());
        Assert.Equal(2, data.Variables.Count);
    }

    // ---- Large data ----

    [Fact]
    public async Task TheFilterHoldsAcrossReadWindows()
    {
        const int rowCount = 2 * GraphDataQueryService.ReadChunkRowCount + 7;
        var fixture = new Fixture();
        var values = new double?[rowCount];
        var sites = new double?[rowCount];
        for (var row = 0; row < rowCount; row++)
        {
            values[row] = row;
            sites[row] = row % 8 + 1;
        }

        // Rows at the window edges belong to Site 2 whatever the pattern says.
        foreach (var edge in new[] { GraphDataQueryService.ReadChunkRowCount - 1, GraphDataQueryService.ReadChunkRowCount, 2 * GraphDataQueryService.ReadChunkRowCount })
        {
            sites[edge] = 2;
        }

        var reg = fixture.Numeric("Reg", 0, values);
        var site = fixture.Numeric("Site", 1, sites);

        var data = OneVariable(await fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new NumericValueSetCondition(site.Id, [2]),
            (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, site))));

        var expected = Enumerable.Range(0, rowCount).Where(row => sites[row] == 2).Select(row => (double)row).ToArray();
        Assert.Equal(expected, data.Values.ToArray());
        Assert.All(NumericGroups(data), group => Assert.Equal(2, group));
        Assert.Contains(GraphDataQueryService.ReadChunkRowCount - 1.0, expected);
        Assert.Contains((double)GraphDataQueryService.ReadChunkRowCount, expected);
        Assert.Contains(2.0 * GraphDataQueryService.ReadChunkRowCount, expected);
        Assert.Equal(3, fixture.RawData.Reads.Count);
    }

    // ---- Nothing kept ----

    [Fact]
    public async Task AFilterThatKeepsNoRowGivesEmptyGraphData()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 0, 1, 2);
        var y = fixture.Numeric("Y", 1, 10, 20);
        var site = fixture.Numeric("Site", 2, 1, 2);

        var data = Assert.IsType<ScatterGraphData>(await fixture.LoadAsync(fixture.Configuration(
            GraphType.ScatterPlot, new NumericValueSetCondition(site.Id, [9]), (GraphVariableRole.X, x), (GraphVariableRole.Y, y))));

        Assert.Equal(0, data.Count);
        Assert.Null(data.Group);
    }

    // ---- Configurations that cannot be used ----

    [Fact]
    public async Task AFilterColumnThatIsGoneIsReportedAsAnUnavailableColumn()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg", 0, 1);

        var exception = await Assert.ThrowsAsync<GraphDataException>(() => fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new NumericValueSetCondition(Guid.NewGuid(), [1]), (GraphVariableRole.Variable, reg))));

        Assert.Equal(GraphDataError.ColumnUnavailable, exception.Error);
        Assert.Empty(fixture.RawData.Reads);
    }

    [Fact]
    public async Task AFilterThatSelectsNothingIsAnInvalidConfiguration()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg", 0, 1);
        var site = fixture.Numeric("Site", 1, 1);

        var exception = await Assert.ThrowsAsync<GraphDataException>(() => fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new NumericValueSetCondition(site.Id, []), (GraphVariableRole.Variable, reg))));

        Assert.Equal(GraphDataError.InvalidConfiguration, exception.Error);
        Assert.Empty(fixture.RawData.Reads);
    }

    [Fact]
    public async Task AFilterOfTheWrongTypeIsAnInvalidConfiguration()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg", 0, 1);
        var lot = fixture.Text("Lot", 1, "1");

        var exception = await Assert.ThrowsAsync<GraphDataException>(() => fixture.LoadAsync(fixture.Configuration(
            GraphType.Histogram, new NumericValueSetCondition(lot.Id, [1]), (GraphVariableRole.Variable, reg))));

        Assert.Equal(GraphDataError.InvalidConfiguration, exception.Error);
    }
}
