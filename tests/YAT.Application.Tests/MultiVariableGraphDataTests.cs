using YAT.Application.Abstractions.Persistence;
using YAT.Application.Graphs;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// Reading several measured variables of one worksheet together: one aligned read, and a compaction that is each
// variable's own - an empty cell of one variable never takes another variable's observation away.
public class MultiVariableGraphDataTests
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

        public GraphConfiguration Configuration(params (GraphVariableRole Role, WorksheetColumn Column)[] assignments) =>
            new(GraphType.BoxPlot, WorksheetId, [.. assignments.Select(item => new GraphColumnAssignment(item.Role, item.Column.Id))]);

        public async Task<MultiVariableGraphData> LoadAsync(GraphConfiguration configuration) =>
            Assert.IsType<MultiVariableGraphData>(await Service.LoadAsync(configuration, Token));
    }

    private static (double Value, string? Group)[] Observations(UnivariateGraphData variable)
    {
        var groups = variable.Group is StringGroupData text ? text.Values.ToArray() : null;
        return [.. Enumerable.Range(0, variable.Count).Select(index => (variable.Values.Span[index], groups?[index]))];
    }

    // 0
    [Fact]
    public async Task EachVariableKeepsTheRowsItHasAValueIn()
    {
        // The case the box plot exists for: Reg1 is empty in row 2, Reg2 in row 3, and neither takes the other's data.
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, null, 3);
        var reg2 = fixture.Numeric("Reg2", 1, 10, 20, null);
        var site = fixture.Text("SITE", 2, "1", "2", "3");

        var data = await fixture.LoadAsync(fixture.Configuration(
            (GraphVariableRole.Variable, reg1), (GraphVariableRole.Variable, reg2), (GraphVariableRole.Group, site)));

        Assert.Equal(["Reg1", "Reg2"], data.Variables.Select(variable => variable.Variable.Name));
        Assert.Equal([(1d, "1"), (3d, "3")], Observations(data.Variables[0]));
        Assert.Equal([(10d, "1"), (20d, "2")], Observations(data.Variables[1]));
        Assert.Equal(4, data.Count);
    }

    // 1
    [Fact]
    public async Task TheVariablesAndTheGroupAreReadInTheSameAlignedWindows()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, 2);
        var reg2 = fixture.Numeric("Reg2", 1, 10, 20);
        var site = fixture.Text("SITE", 2, "A", "B");

        await fixture.LoadAsync(fixture.Configuration(
            (GraphVariableRole.Variable, reg1), (GraphVariableRole.Variable, reg2), (GraphVariableRole.Group, site)));

        // One request, all three columns in it: row i of the block is worksheet row i for every one of them.
        var read = Assert.Single(fixture.RawData.Reads);
        Assert.Equal([reg1.Id, reg2.Id, site.Id], read.ColumnIds);
    }

    // 2
    [Fact]
    public async Task AVariableWithoutAGroupColumnHasNoGroupData()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, 2);
        var reg2 = fixture.Numeric("Reg2", 1, 10, 20);

        var data = await fixture.LoadAsync(fixture.Configuration(
            (GraphVariableRole.Variable, reg1), (GraphVariableRole.Variable, reg2)));

        Assert.All(data.Variables, variable => Assert.Null(variable.Group));
        Assert.Equal([1, 2], data.Variables[0].Values.ToArray());
        Assert.Equal([10, 20], data.Variables[1].Values.ToArray());
    }

    // 3
    [Fact]
    public async Task AMissingGroupValueIsKeptWithItsObservation()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, 2, 3);
        var site = fixture.Text("SITE", 1, "A", null, "B");

        var data = await fixture.LoadAsync(fixture.Configuration(
            (GraphVariableRole.Variable, reg1), (GraphVariableRole.Group, site)));

        var variable = Assert.Single(data.Variables);
        Assert.Equal([(1d, "A"), (2d, null), (3d, "B")], Observations(variable));
        Assert.True(variable.Group!.IsMissing(1));
    }

    // 4
    [Fact]
    public async Task ANumericGroupColumnStaysNumeric()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, 2);
        var site = fixture.Numeric("SITE", 1, 3, null);

        var data = await fixture.LoadAsync(fixture.Configuration(
            (GraphVariableRole.Variable, reg1), (GraphVariableRole.Group, site)));

        Assert.Equal([3, null], ((NumericGroupData)data.Variables[0].Group!).Values.ToArray());
    }

    // 5
    [Fact]
    public async Task AVariableWithoutStoredValuesHasNoObservationsAndTheOthersStillDo()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, 2);
        var empty = fixture.Column("Reg2", WorksheetDataType.Numeric, 1);

        var data = await fixture.LoadAsync(fixture.Configuration(
            (GraphVariableRole.Variable, reg1), (GraphVariableRole.Variable, empty)));

        Assert.Equal([1, 2], data.Variables[0].Values.ToArray());
        Assert.Equal(0, data.Variables[1].Count);
        Assert.Equal("Reg2", data.Variables[1].Variable.Name);
    }

    // 6
    [Fact]
    public async Task LongDatasetsAreReadInBoundedWindowsAndStayAligned()
    {
        const int RowCount = (GraphDataQueryService.ReadChunkRowCount * 2) + 57;
        var first = new double?[RowCount];
        var second = new double?[RowCount];
        var groups = new string?[RowCount];
        for (var row = 0; row < RowCount; row++)
        {
            first[row] = row % 3 == 0 ? null : row;
            second[row] = row % 5 == 0 ? null : row * 2;
            groups[row] = $"G{row % 4}";
        }

        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, first);
        var reg2 = fixture.Numeric("Reg2", 1, second);
        var site = fixture.Text("SITE", 2, groups);

        var data = await fixture.LoadAsync(fixture.Configuration(
            (GraphVariableRole.Variable, reg1), (GraphVariableRole.Variable, reg2), (GraphVariableRole.Group, site)));

        Assert.Equal(3, fixture.RawData.Reads.Count);
        Assert.Equal(first.Count(value => value is not null), data.Variables[0].Count);
        Assert.Equal(second.Count(value => value is not null), data.Variables[1].Count);

        // Every observation still carries the group of the worksheet row it came from.
        var reg1Observations = Observations(data.Variables[0]);
        var expected = Enumerable.Range(0, RowCount).Where(row => row % 3 != 0).Select(row => ((double)row, groups[row])).ToArray();
        Assert.Equal(expected, reg1Observations);
    }

    // 7
    [Fact]
    public async Task ADeletedVariableIsReportedRatherThanRead()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, 2);
        var configuration = new GraphConfiguration(
            GraphType.BoxPlot,
            fixture.WorksheetId,
            [
                new GraphColumnAssignment(GraphVariableRole.Variable, reg1.Id),
                new GraphColumnAssignment(GraphVariableRole.Variable, Guid.NewGuid())
            ]);

        var exception = await Assert.ThrowsAsync<YAT.Application.Exceptions.GraphDataException>(
            () => fixture.Service.LoadAsync(configuration, Token));

        Assert.Equal(YAT.Application.Exceptions.GraphDataError.ColumnUnavailable, exception.Error);
    }

    // 8
    [Fact]
    public async Task SingleVariableGraphsStillReadTheirOwnShape()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 0, 1, null, 3);

        var histogram = await fixture.Service.LoadAsync(
            new GraphConfiguration(
                GraphType.Histogram,
                fixture.WorksheetId,
                [new GraphColumnAssignment(GraphVariableRole.Variable, reg1.Id)]),
            Token);

        var univariate = Assert.IsType<UnivariateGraphData>(histogram);
        Assert.Equal([1, 3], univariate.Values.ToArray());
    }
}
