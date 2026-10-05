using YAT.Application.Abstractions.Persistence;
using YAT.Application.Filtering;
using YAT.Application.Graphs;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// The Panel role (Task #058) in the configuration and the graph data query: an optional Numeric or String column of the
// scatter plot and the single-variable graphs - not the box plot - never the Group column too, read in the same aligned
// windows as the graph's values and groups, after the row filter, one panel value per observation the graph keeps.
public class GraphPanelQueryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Fixture
    {
        public Fixture()
        {
            Worksheets.Seed(new Worksheet { Id = WorksheetId, Name = "Sheet1" });
            Graphs = new GraphDataQueryService(Worksheets, Columns, RawData);
        }

        public Guid WorksheetId { get; } = Guid.NewGuid();

        public FakeWorksheetRepository Worksheets { get; } = new();

        public FakeWorksheetColumnRepository Columns { get; } = new();

        public FakeWorksheetRawDataStore RawData { get; } = new();

        public GraphDataQueryService Graphs { get; }

        public List<WorksheetColumn> All { get; } = [];

        public WorksheetColumn Numeric(string name, params double?[] values)
        {
            var column = Column(name, WorksheetDataType.Numeric);
            RawData.Seed(WorksheetId, new NumericRawDataColumn(column.Id, values));
            return column;
        }

        public WorksheetColumn Text(string name, params string?[] values)
        {
            var column = Column(name, WorksheetDataType.String);
            RawData.Seed(WorksheetId, new StringRawDataColumn(column.Id, values));
            return column;
        }

        public GraphConfiguration Configuration(GraphType type, params (GraphVariableRole Role, WorksheetColumn Column)[] roles) =>
            new(type, WorksheetId, [.. roles.Select(role => new GraphColumnAssignment(role.Role, role.Column.Id))]);

        public Task<GraphData> LoadAsync(GraphConfiguration configuration) => Graphs.LoadAsync(configuration, Token);

        private WorksheetColumn Column(string name, WorksheetDataType dataType)
        {
            var column = new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = WorksheetId, Index = All.Count, Name = name, DataType = dataType };
            Columns.Seed(column);
            All.Add(column);
            return column;
        }
    }

    private static string?[] Strings(GraphGroupData? data) => [.. Assert.IsType<StringGroupData>(data).Values.ToArray()];

    private static double?[] Numbers(GraphGroupData? data) => [.. Assert.IsType<NumericGroupData>(data).Values.ToArray()];

    // ---- The role ----

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void PanelIsAnOptionalCategoricalRoleOfTheGraphsThatOfferIt(GraphType type)
    {
        var panel = GraphTypeDefinitions.For(type).FindRole(GraphVariableRole.Panel);

        Assert.NotNull(panel);
        Assert.False(panel.IsRequired);
        Assert.False(panel.AllowsMultiple);
        Assert.Equal([WorksheetDataType.Numeric, WorksheetDataType.String], panel.AllowedDataTypes);
    }

    [Fact]
    public void ABoxPlotHasNoPanels()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg1", 1, 2);
        var site = fixture.Numeric("Site", 1, 2);

        var result = new GraphConfigurationValidator().Validate(
            fixture.Configuration(GraphType.BoxPlot, (GraphVariableRole.Variable, reg), (GraphVariableRole.Panel, site)), fixture.All);

        Assert.Equal(GraphValidationReason.UnsupportedRole, Assert.Single(result.Errors).Reason);
    }

    [Fact]
    public void PanelAndGroupMustBeDifferentColumns()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg1", 1, 2);
        var site = fixture.Text("Site", "A", "B");
        var lot = fixture.Text("Lot", "x", "y");
        var validator = new GraphConfigurationValidator();

        var same = validator.Validate(
            fixture.Configuration(GraphType.Histogram, (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, site), (GraphVariableRole.Panel, site)), fixture.All);
        var error = Assert.Single(same.Errors);
        Assert.Equal((GraphValidationReason.PanelSameAsGroup, (GraphVariableRole?)GraphVariableRole.Panel, (Guid?)site.Id), (error.Reason, error.Role, error.WorksheetColumnId));

        Assert.True(validator.Validate(
            fixture.Configuration(GraphType.Histogram, (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, lot), (GraphVariableRole.Panel, site)), fixture.All).IsValid);
    }

    [Fact]
    public void APanelColumnMustBeNumericOrString()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg1", 1, 2);
        var column = new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = fixture.WorksheetId, Index = 9, Name = "When", DataType = WorksheetDataType.DateTime };

        var result = new GraphConfigurationValidator().Validate(
            fixture.Configuration(GraphType.EmpiricalCdf, (GraphVariableRole.Variable, reg), (GraphVariableRole.Panel, column)), [.. fixture.All, column]);

        Assert.Equal(GraphValidationReason.IncompatibleDataType, Assert.Single(result.Errors).Reason);
    }

    // ---- The query ----

    [Fact]
    public async Task WithoutAPanelColumnTheDataHasNoPanels()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg1", 1, 2, 3);

        var data = await fixture.LoadAsync(fixture.Configuration(GraphType.Histogram, (GraphVariableRole.Variable, reg)));

        Assert.Null(Assert.Single(Assert.IsType<MultiVariableGraphData>(data).Variables).Panel);
    }

    [Fact]
    public async Task AScatterPlotKeepsEachRowsPanelValueWithItsPoint()
    {
        var fixture = new Fixture();
        var x = fixture.Numeric("X", 1, 2, null, 4, 5);
        var y = fixture.Numeric("Y", 10, 20, 30, 40, 50);
        var site = fixture.Text("Site", "B", "A", "A", null, "B");

        var data = Assert.IsType<ScatterGraphData>(await fixture.LoadAsync(
            fixture.Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, x), (GraphVariableRole.Y, y), (GraphVariableRole.Panel, site))));

        Assert.Equal([1d, 2, 4, 5], data.XValues.ToArray());
        Assert.Equal(["B", "A", null, "B"], Strings(data.Panel));
        Assert.Equal("Site", data.Panel!.Column.Name);
        Assert.Null(data.Group);
    }

    [Fact]
    public async Task EveryVariableKeepsTheRowsOwnPanelValuesBesideItsGroups()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Numeric("Reg1", 1, null, 3, 4);
        var reg2 = fixture.Numeric("Reg2", 5, 6, null, 8);
        var lot = fixture.Text("Lot", "x", "y", "x", "y");
        var site = fixture.Numeric("Site", 1, 2, 2, null);

        var data = Assert.IsType<MultiVariableGraphData>(await fixture.LoadAsync(fixture.Configuration(
            GraphType.ProbabilityPlot, (GraphVariableRole.Variable, reg1), (GraphVariableRole.Variable, reg2), (GraphVariableRole.Group, lot), (GraphVariableRole.Panel, site))));

        Assert.Equal([1d, 2, null], Numbers(data.Variables[0].Panel));
        Assert.Equal(["x", "x", "y"], Strings(data.Variables[0].Group));
        Assert.Equal([1d, 2, null], Numbers(data.Variables[1].Panel));
        Assert.Equal(["x", "y", "y"], Strings(data.Variables[1].Group));
    }

    [Fact]
    public async Task TheFilterDecidesBeforeTheRowsReachAPanel()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg1", 1, 2, 3, 4, 5, 6);
        var site = fixture.Text("Site", "A", "B", "C", "A", "B", "C");
        var bin = fixture.Numeric("Bin", 1, 1, 2, 2, 1, 2);
        var configuration = fixture.Configuration(GraphType.EmpiricalCdf, (GraphVariableRole.Variable, reg), (GraphVariableRole.Panel, site)) with
        {
            Filter = new RowFilter(new NumericValueSetCondition(bin.Id, [1]))
        };

        var data = Assert.Single(Assert.IsType<MultiVariableGraphData>(await fixture.LoadAsync(configuration)).Variables);

        Assert.Equal([1d, 2, 5], data.Values.ToArray());
        Assert.Equal(["A", "B", "B"], Strings(data.Panel));
    }

    [Fact]
    public async Task ThePanelColumnIsReadOnceInTheSameWindowsAsTheValues()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg1", 1, 2, 3);
        var site = fixture.Text("Site", "A", "B", "A");
        var lot = fixture.Text("Lot", "x", "x", "y");

        await fixture.LoadAsync(fixture.Configuration(GraphType.Histogram, (GraphVariableRole.Variable, reg), (GraphVariableRole.Group, lot), (GraphVariableRole.Panel, site)));

        var read = Assert.Single(fixture.RawData.Reads);
        Assert.Equal([reg.Id, lot.Id, site.Id], read.ColumnIds);
    }

    [Fact]
    public async Task APanelColumnWithoutStoredValuesIsMissingInEveryRow()
    {
        var fixture = new Fixture();
        var reg = fixture.Numeric("Reg1", 1, 2);
        var site = new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = fixture.WorksheetId, Index = 5, Name = "Site", DataType = WorksheetDataType.String };
        fixture.Columns.Seed(site);

        var data = Assert.Single(Assert.IsType<MultiVariableGraphData>(await fixture.LoadAsync(
            fixture.Configuration(GraphType.Histogram, (GraphVariableRole.Variable, reg), (GraphVariableRole.Panel, site)))).Variables);

        Assert.Equal([null, null], Strings(data.Panel));
    }
}
