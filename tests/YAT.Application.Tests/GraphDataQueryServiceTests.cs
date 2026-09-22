using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Graphs;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// Turning a graph configuration into observations: aligned reads, null rules, group preservation and row order.
public class GraphDataQueryServiceTests
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

        // A worksheet column with metadata; values are stored separately (or not at all).
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

        public GraphConfiguration Configuration(GraphType graphType, params (GraphVariableRole Role, WorksheetColumn Column)[] assignments) =>
            new(graphType, WorksheetId, [.. assignments.Select(item => new GraphColumnAssignment(item.Role, item.Column.Id))]);

        public Task<GraphData> LoadAsync(GraphConfiguration configuration) => Service.LoadAsync(configuration, Token);
    }

    private static (double X, double Y)[] Points(ScatterGraphData data) =>
        [.. Enumerable.Range(0, data.Count).Select(index => (data.XValues.Span[index], data.YValues.Span[index]))];

    private static string?[] StringGroups(GraphData data) =>
        [.. ((StringGroupData)data.Group!).Values.ToArray()];

    private static double?[] NumericGroups(GraphData data) =>
        [.. ((NumericGroupData)data.Group!).Values.ToArray()];

    // ---- Scatter ----

    [Fact]
    public async Task ScatterKeepsOnlyRowsWhereBothValuesArePresent()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 0, 1, null, 3, 4);
        var y = fixture.Numeric("Y", 1, 10, 20, null, 40);

        var data = Assert.IsType<ScatterGraphData>(
            await fixture.LoadAsync(fixture.Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, y))));

        Assert.Equal([(1, 10), (4, 40)], Points(data));
        Assert.Equal(2, data.Count);
        Assert.Null(data.Group);
        Assert.Equal(GraphType.ScatterPlot, data.GraphType);
        Assert.Equal(fixture.WorksheetId, data.WorksheetId);
    }

    [Fact]
    public async Task ScatterKeepsARowWhoseGroupIsMissing()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 0, 1, 2, 3);
        var y = fixture.Numeric("Y", 1, 10, 20, 30);
        var group = fixture.Text("Lot", 2, "A", null, "B");

        var data = Assert.IsType<ScatterGraphData>(await fixture.LoadAsync(fixture.Configuration(
            GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, y), (GraphVariableRole.Group, group))));

        Assert.Equal([(1, 10), (2, 20), (3, 30)], Points(data));
        Assert.Equal(["A", null, "B"], StringGroups(data));
        Assert.True(data.Group!.IsMissing(1));
        Assert.False(data.Group.IsMissing(0));
        Assert.Equal(data.Count, data.Group.Count);
    }

    [Fact]
    public async Task ANumericGroupStaysNumeric()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 0, 1, 2, 3);
        var y = fixture.Numeric("Y", 1, 10, 20, 30);
        var group = fixture.Numeric("SITE", 2, 1, null, 2.5);

        var data = await fixture.LoadAsync(fixture.Configuration(
            GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, y), (GraphVariableRole.Group, group)));

        var numeric = Assert.IsType<NumericGroupData>(data.Group);
        Assert.Equal([1, null, 2.5], NumericGroups(data));
        Assert.Equal(WorksheetDataType.Numeric, numeric.Column.DataType);
        Assert.Equal("SITE", numeric.Column.Name);
        Assert.Equal(group.Id, numeric.Column.ColumnId);
    }

    [Fact]
    public async Task GroupValuesFollowTheRowsThatSurviveFiltering()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 0, 1, null, 3, 4);
        var y = fixture.Numeric("Y", 1, 10, 20, null, 40);
        var group = fixture.Text("Lot", 2, "A", "B", "C", "D");

        var data = Assert.IsType<ScatterGraphData>(await fixture.LoadAsync(fixture.Configuration(
            GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, y), (GraphVariableRole.Group, group))));

        Assert.Equal([(1, 10), (4, 40)], Points(data));
        Assert.Equal(["A", "D"], StringGroups(data));
    }

    [Fact]
    public async Task ShorterColumnsAreNullPaddedByRow()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 0, 1, 2, 3, 4);
        var y = fixture.Numeric("Y", 1, 10, 20);
        var group = fixture.Text("Lot", 2, "A", "B", "C");

        var data = Assert.IsType<ScatterGraphData>(await fixture.LoadAsync(fixture.Configuration(
            GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, y), (GraphVariableRole.Group, group))));

        // Rows 2 and 3 have no Y, so they are dropped; row 2 would have had a group value.
        Assert.Equal([(1, 10), (2, 20)], Points(data));
        Assert.Equal(["A", "B"], StringGroups(data));
    }

    [Fact]
    public async Task RowsBeyondAShortGroupColumnKeepTheirValuesWithoutAGroup()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 0, 1, 2, 3);
        var y = fixture.Numeric("Y", 1, 10, 20, 30);
        var group = fixture.Text("Lot", 2, "A");

        var data = Assert.IsType<ScatterGraphData>(await fixture.LoadAsync(fixture.Configuration(
            GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, y), (GraphVariableRole.Group, group))));

        Assert.Equal(3, data.Count);
        Assert.Equal(["A", null, null], StringGroups(data));
    }

    [Fact]
    public async Task ScatterMayUseTheSameColumnForBothAxes()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 0, 1, null, 3);

        var data = Assert.IsType<ScatterGraphData>(
            await fixture.LoadAsync(fixture.Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, x))));

        Assert.Equal([(1, 1), (3, 3)], Points(data));
    }

    // ---- Univariate ----

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public async Task SingleVariableGraphsProduceTheirVariablesValuesInWorksheetOrder(GraphType graphType)
    {
        var fixture = new Fixture();
        var variable = fixture.Numeric("Reg1", 0, 3, null, 1, 2);

        var data = Assert.IsType<UnivariateGraphData>(
            await fixture.LoadAsync(fixture.Configuration(graphType, (GraphVariableRole.Variable, variable))));

        Assert.Equal([3, 1, 2], data.Values.ToArray());
        Assert.Equal(graphType, data.GraphType);
        Assert.Equal("Reg1", data.Variable.Name);
        Assert.Equal(variable.Id, data.Variable.ColumnId);
        Assert.Null(data.Group);
    }

    [Fact]
    public async Task AUnivariateRowWithoutAGroupValueIsKept()
    {
        var fixture = new Fixture();
        var variable = fixture.Numeric("Reg1", 0, 1, null, 3);
        var group = fixture.Text("Lot", 1, "A", "B", null);

        var data = Assert.IsType<UnivariateGraphData>(await fixture.LoadAsync(
            fixture.Configuration(GraphType.Histogram, (GraphVariableRole.Variable, variable), (GraphVariableRole.Group, group))));

        Assert.Equal([1, 3], data.Values.ToArray());
        Assert.Equal(["A", null], StringGroups(data));
        Assert.True(data.Group!.IsMissing(1));
    }

    [Fact]
    public async Task PresentationOptionsDoNotChangeTheDataThatIsRead()
    {
        var fixture = new Fixture();
        var variable = fixture.Numeric("Reg1", 0, 1, null, 3, 4);
        var group = fixture.Text("Lot", 1, "A", "B", null, "A");
        var configuration = fixture.Configuration(
            GraphType.Histogram, (GraphVariableRole.Variable, variable), (GraphVariableRole.Group, group));

        var shown = Assert.IsType<UnivariateGraphData>(await fixture.LoadAsync(configuration));
        var hidden = Assert.IsType<UnivariateGraphData>(await fixture.LoadAsync(
            configuration with { PresentationOptions = new GraphPresentationOptions(ShowStatistics: false) }));

        Assert.Equal(shown.Values.ToArray(), hidden.Values.ToArray());
        Assert.Equal(StringGroups(shown), StringGroups(hidden));
        Assert.Equal(shown.Variable, hidden.Variable);
    }

    // ---- Order, chunking and column state ----

    [Fact]
    public async Task ValuesKeepWorksheetRowOrderAcrossReadChunks()
    {
        var fixture = new Fixture();
        var rowCount = GraphDataQueryService.ReadChunkRowCount + 1_000;
        var values = new double?[rowCount];
        for (var row = 0; row < rowCount; row++)
        {
            // Every third row is empty, so filtering has to keep the surviving order.
            values[row] = row % 3 == 0 ? null : rowCount - row;
        }

        var variable = fixture.Numeric("Reg1", 0, values);

        var data = Assert.IsType<UnivariateGraphData>(
            await fixture.LoadAsync(fixture.Configuration(GraphType.EmpiricalCdf, (GraphVariableRole.Variable, variable))));

        var expected = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        Assert.Equal(expected, data.Values.ToArray());
        Assert.True(fixture.RawData.Reads.Count > 1, "the data was read in more than one chunk");
        Assert.All(fixture.RawData.Reads, read => Assert.Equal(GraphDataQueryService.ReadChunkRowCount, read.RowCount));
    }

    [Fact]
    public async Task TheConfiguredColumnsAreReadTogetherInOneAlignedRequest()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 0, 1, 2);
        var y = fixture.Numeric("Y", 1, 10, 20);
        var group = fixture.Text("Lot", 2, "A", "B");

        await fixture.LoadAsync(fixture.Configuration(
            GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, y), (GraphVariableRole.Group, group)));

        var read = Assert.Single(fixture.RawData.Reads);
        Assert.Equal([x.Id, y.Id, group.Id], read.ColumnIds);
        Assert.Equal(0, read.RowOffset);
    }

    [Fact]
    public async Task AValueColumnWithoutStoredValuesProducesNoObservations()
    {
        var fixture = new Fixture();
        var x = fixture.Column("X", WorksheetDataType.Numeric, 0);   // metadata only
        var y = fixture.Numeric("Y", 1, 10, 20);

        var data = Assert.IsType<ScatterGraphData>(
            await fixture.LoadAsync(fixture.Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, y))));

        Assert.Equal(0, data.Count);
        Assert.Empty(fixture.RawData.Reads);
        Assert.Equal(x.Id, data.X.ColumnId);
    }

    [Fact]
    public async Task AGroupColumnWithoutStoredValuesLeavesEveryObservationUnassigned()
    {
        var fixture = new Fixture();
        var variable = fixture.Numeric("Reg1", 0, 1, 2);
        var group = fixture.Column("Lot", WorksheetDataType.String, 1);

        var data = await fixture.LoadAsync(
            fixture.Configuration(GraphType.Histogram, (GraphVariableRole.Variable, variable), (GraphVariableRole.Group, group)));

        Assert.Equal(2, data.Count);
        Assert.Equal([null, null], StringGroups(data));
        Assert.Equal(group.Id, data.Group!.Column.ColumnId);
        Assert.Equal([variable.Id], Assert.Single(fixture.RawData.Reads).ColumnIds);
    }

    // ---- Identity and metadata ----

    [Fact]
    public async Task TheConfiguredColumnIdIsReadEvenAfterRenamingAndReindexing()
    {
        var fixture = new Fixture();
        var variable = fixture.Numeric("Reg1", 3, 1, 2, 3);
        var configuration = fixture.Configuration(GraphType.Histogram, (GraphVariableRole.Variable, variable));

        // The same column after a rename and a reindex.
        fixture.Columns.Seed(new WorksheetColumn
        {
            Id = variable.Id,
            WorksheetId = fixture.WorksheetId,
            Index = 0,
            Name = "Vth",
            DataType = WorksheetDataType.Numeric
        });

        var data = Assert.IsType<UnivariateGraphData>(await fixture.LoadAsync(configuration));

        Assert.Equal([1, 2, 3], data.Values.ToArray());
        Assert.Equal(variable.Id, data.Variable.ColumnId);
        Assert.Equal("Vth", data.Variable.Name);
    }

    // ---- Failures ----

    [Fact]
    public async Task AnInvalidConfigurationIsRejectedWithoutReadingValues()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 0, 1, 2);
        var lot = fixture.Text("Lot", 1, "A", "B");

        // Y is missing, and a String column cannot be an axis.
        var missingY = await Assert.ThrowsAsync<GraphDataException>(
            () => fixture.LoadAsync(fixture.Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, x))));
        var stringAxis = await Assert.ThrowsAsync<GraphDataException>(() => fixture.LoadAsync(
            fixture.Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, lot))));

        Assert.Equal(GraphDataError.InvalidConfiguration, missingY.Error);
        Assert.Equal(GraphDataError.InvalidConfiguration, stringAxis.Error);
        Assert.Empty(fixture.RawData.Reads);
    }

    [Fact]
    public async Task ADeletedColumnIsReportedAsUnavailable()
    {
        var fixture = new Fixture();
        var variable = fixture.Numeric("Reg1", 0, 1, 2);
        var configuration = fixture.Configuration(GraphType.Histogram, (GraphVariableRole.Variable, variable));
        fixture.Columns.Delete(variable.Id);

        var exception = await Assert.ThrowsAsync<GraphDataException>(() => fixture.LoadAsync(configuration));

        Assert.Equal(GraphDataError.ColumnUnavailable, exception.Error);
        Assert.Empty(fixture.RawData.Reads);
    }

    [Fact]
    public async Task AMissingWorksheetIsReported()
    {
        var fixture = new Fixture();
        var variable = fixture.Numeric("Reg1", 0, 1, 2);
        var configuration = new GraphConfiguration(
            GraphType.Histogram, Guid.NewGuid(), [new GraphColumnAssignment(GraphVariableRole.Variable, variable.Id)]);

        var exception = await Assert.ThrowsAsync<GraphDataException>(() => fixture.LoadAsync(configuration));

        Assert.Equal(GraphDataError.WorksheetUnavailable, exception.Error);
    }

    [Fact]
    public async Task AnUnknownGraphTypeIsReported()
    {
        var fixture = new Fixture();
        var configuration = new GraphConfiguration((GraphType)99, fixture.WorksheetId, []);

        var exception = await Assert.ThrowsAsync<GraphDataException>(() => fixture.LoadAsync(configuration));

        Assert.Equal(GraphDataError.UnsupportedGraphType, exception.Error);
    }

    [Fact]
    public async Task ARawStorageFailureIsReportedWithoutLeakingTheStorageException()
    {
        var fixture = new Fixture();
        var variable = fixture.Numeric("Reg1", 0, 1, 2);
        var configuration = fixture.Configuration(GraphType.Histogram, (GraphVariableRole.Variable, variable));
        fixture.RawData.ReadFailure = new RawDataStorageException("DuckDB said no.");

        var exception = await Assert.ThrowsAsync<GraphDataException>(() => fixture.LoadAsync(configuration));

        Assert.Equal(GraphDataError.DataReadFailed, exception.Error);
        Assert.IsType<RawDataStorageException>(exception.InnerException);
        Assert.DoesNotContain("DuckDB", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadingCanBeCancelled()
    {
        var fixture = new Fixture();
        var variable = fixture.Numeric("Reg1", 0, 1, 2);
        var configuration = fixture.Configuration(GraphType.Histogram, (GraphVariableRole.Variable, variable));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.LoadAsync(configuration, cancellation.Token));

        Assert.Empty(fixture.RawData.Reads);
    }
}
