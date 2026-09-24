using YAT.Application.Graphs;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// How many columns a role takes, and that the rule is the role's own: the generic validator reads AllowsMultiple and
// never the graph type, so adding a graph that draws several variables did not loosen the graphs that draw one.
public class GraphRoleCardinalityTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly GraphConfigurationValidator Validator = new();

    private static WorksheetColumn Column(string name, int index, WorksheetDataType dataType = WorksheetDataType.Numeric) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = WorksheetId,
        Index = index,
        Name = name,
        DataType = dataType
    };

    private static readonly WorksheetColumn Reg1 = Column("Reg1", 0);
    private static readonly WorksheetColumn Reg2 = Column("Reg2", 1);
    private static readonly WorksheetColumn Site = Column("SITE", 2, WorksheetDataType.String);

    private static readonly WorksheetColumn[] Columns = [Reg1, Reg2, Site];

    private static GraphConfiguration Configuration(GraphType graphType, params (GraphVariableRole Role, WorksheetColumn Column)[] assignments) =>
        new(graphType, WorksheetId, [.. assignments.Select(item => new GraphColumnAssignment(item.Role, item.Column.Id))]);

    // 0: from Task #041 the variables of every graph but the scatter plot are picked several at a time; axes and groups
    // take one column.
    [Fact]
    public void EveryVariableRoleTakesSeveralColumns()
    {
        foreach (var definition in GraphTypeDefinitions.All)
        {
            foreach (var role in definition.Roles)
            {
                var expected = role.Role == GraphVariableRole.Variable;
                Assert.Equal(expected, role.AllowsMultiple);
            }
        }
    }

    // 1: a distribution graph takes one variable as it always did, or several - but one column only once.
    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void ADistributionGraphTakesOneOrSeveralVariables(GraphType graphType)
    {
        Assert.True(Validator.Validate(Configuration(graphType, (GraphVariableRole.Variable, Reg1)), Columns).IsValid);
        Assert.True(Validator.Validate(
            Configuration(graphType, (GraphVariableRole.Variable, Reg1), (GraphVariableRole.Variable, Reg2)), Columns).IsValid);

        var twice = Validator.Validate(
            Configuration(graphType, (GraphVariableRole.Variable, Reg1), (GraphVariableRole.Variable, Reg1)), Columns);

        var error = Assert.Single(twice.Errors);
        Assert.Equal(GraphValidationReason.DuplicateColumn, error.Reason);
        Assert.Equal(GraphVariableRole.Variable, error.Role);
    }

    // 2
    [Fact]
    public void ScatterKeepsItsOwnRoleSemantics()
    {
        Assert.True(Validator.Validate(
            Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, Reg1), (GraphVariableRole.Y, Reg2)), Columns).IsValid);

        // The same column in two different roles stays allowed; the same role twice does not.
        Assert.True(Validator.Validate(
            Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, Reg1), (GraphVariableRole.Y, Reg1)), Columns).IsValid);

        var twice = Validator.Validate(
            Configuration(GraphType.ScatterPlot, (GraphVariableRole.X, Reg1), (GraphVariableRole.X, Reg2), (GraphVariableRole.Y, Reg2)),
            Columns);

        Assert.Equal(GraphValidationReason.DuplicateRole, Assert.Single(twice.Errors).Reason);
    }

    // 3
    [Fact]
    public void ABoxPlotTakesOneOrMoreVariables()
    {
        Assert.True(Validator.Validate(Configuration(GraphType.BoxPlot, (GraphVariableRole.Variable, Reg1)), Columns).IsValid);

        Assert.True(Validator.Validate(
            Configuration(GraphType.BoxPlot, (GraphVariableRole.Variable, Reg1), (GraphVariableRole.Variable, Reg2)),
            Columns).IsValid);

        Assert.True(Validator.Validate(
            Configuration(
                GraphType.BoxPlot,
                (GraphVariableRole.Variable, Reg1),
                (GraphVariableRole.Variable, Reg2),
                (GraphVariableRole.Group, Site)),
            Columns).IsValid);
    }

    // 4
    [Fact]
    public void ABoxPlotWithoutAVariableIsInvalid()
    {
        var result = Validator.Validate(Configuration(GraphType.BoxPlot, (GraphVariableRole.Group, Site)), Columns);

        var error = Assert.Single(result.Errors);
        Assert.Equal(GraphValidationReason.MissingRequiredRole, error.Reason);
        Assert.Equal(GraphVariableRole.Variable, error.Role);
    }

    // 5
    [Fact]
    public void TheSameColumnTwiceInTheSameMultiValuedRoleIsInvalid()
    {
        var result = Validator.Validate(
            Configuration(GraphType.BoxPlot, (GraphVariableRole.Variable, Reg1), (GraphVariableRole.Variable, Reg1)), Columns);

        var error = Assert.Single(result.Errors);
        Assert.Equal(GraphValidationReason.DuplicateColumn, error.Reason);
        Assert.Equal(GraphVariableRole.Variable, error.Role);
        Assert.Equal(Reg1.Id, error.WorksheetColumnId);
    }

    // 6
    [Fact]
    public void TheBoxPlotsGroupingRoleStillTakesOneColumn()
    {
        var result = Validator.Validate(
            Configuration(
                GraphType.BoxPlot,
                (GraphVariableRole.Variable, Reg1),
                (GraphVariableRole.Group, Site),
                (GraphVariableRole.Group, Reg2)),
            Columns);

        Assert.Equal(GraphValidationReason.DuplicateRole, Assert.Single(result.Errors).Reason);
    }

    // 7
    [Fact]
    public void ABoxPlotVariableMustStillBeNumeric()
    {
        var result = Validator.Validate(
            Configuration(GraphType.BoxPlot, (GraphVariableRole.Variable, Reg1), (GraphVariableRole.Variable, Site)), Columns);

        var error = Assert.Single(result.Errors);
        Assert.Equal(GraphValidationReason.IncompatibleDataType, error.Reason);
        Assert.Equal(Site.Id, error.WorksheetColumnId);
    }

    // 8
    [Fact]
    public void FindColumnIdReadsTheFirstAssignmentAndFindColumnIdsReadsThemAll()
    {
        var configuration = Configuration(
            GraphType.BoxPlot,
            (GraphVariableRole.Variable, Reg1),
            (GraphVariableRole.Variable, Reg2),
            (GraphVariableRole.Group, Site));

        Assert.Equal(Reg1.Id, configuration.FindColumnId(GraphVariableRole.Variable));
        Assert.Equal([Reg1.Id, Reg2.Id], configuration.FindColumnIds(GraphVariableRole.Variable));
        Assert.Equal([Site.Id], configuration.FindColumnIds(GraphVariableRole.Group));
        Assert.Empty(configuration.FindColumnIds(GraphVariableRole.X));
        Assert.Null(configuration.FindColumnId(GraphVariableRole.X));
    }

    // 9
    [Fact]
    public void TheBoxPlotIsOfferedBetweenTheHistogramAndTheProbabilityPlot()
    {
        Assert.Equal(
            [GraphType.ScatterPlot, GraphType.Histogram, GraphType.BoxPlot, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf],
            GraphTypeDefinitions.All.Select(definition => definition.GraphType));

        var boxPlot = GraphTypeDefinitions.For(GraphType.BoxPlot);
        Assert.Equal("Box Plot", boxPlot.DisplayName);
        Assert.Equal("Graph variables", boxPlot.FindRole(GraphVariableRole.Variable)!.DisplayName);
        Assert.Equal("Categorical variable for grouping", boxPlot.FindRole(GraphVariableRole.Group)!.DisplayName);
        Assert.True(boxPlot.FindRole(GraphVariableRole.Group)!.Allows(WorksheetDataType.String));
        Assert.False(boxPlot.FindRole(GraphVariableRole.Group)!.IsRequired);
    }
}
