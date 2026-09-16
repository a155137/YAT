using YAT.Application.Graphs;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// The V1 graph specifications: which roles each graph type has, which are required, and which column types they accept.
public class GraphTypeDefinitionsTests
{
    private static void AssertRole(GraphType graphType, GraphVariableRole role, bool required, params WorksheetDataType[] allowed)
    {
        var definition = GraphTypeDefinitions.For(graphType).FindRole(role);
        Assert.NotNull(definition);
        Assert.Equal(required, definition.IsRequired);
        Assert.Equal(allowed, definition.AllowedDataTypes);
    }

    [Fact]
    public void TheFourV1GraphTypesAreDefinedInMenuOrderWithTheirDisplayNames()
    {
        Assert.Equal(
            [GraphType.ScatterPlot, GraphType.Histogram, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf],
            GraphTypeDefinitions.All.Select(definition => definition.GraphType));
        Assert.Equal(
            ["Scatter Plot", "Histogram", "Probability Plot", "Empirical CDF"],
            GraphTypeDefinitions.All.Select(definition => definition.DisplayName));
    }

    [Fact]
    public void ScatterPlotTakesNumericXAndYWithAnOptionalGroup()
    {
        Assert.Equal(
            [GraphVariableRole.X, GraphVariableRole.Y, GraphVariableRole.Group],
            GraphTypeDefinitions.For(GraphType.ScatterPlot).Roles.Select(role => role.Role));
        AssertRole(GraphType.ScatterPlot, GraphVariableRole.X, required: true, WorksheetDataType.Numeric);
        AssertRole(GraphType.ScatterPlot, GraphVariableRole.Y, required: true, WorksheetDataType.Numeric);
        AssertRole(GraphType.ScatterPlot, GraphVariableRole.Group, required: false, WorksheetDataType.Numeric, WorksheetDataType.String);
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void SingleVariableGraphsTakeANumericVariableWithAnOptionalGroup(GraphType graphType)
    {
        Assert.Equal(
            [GraphVariableRole.Variable, GraphVariableRole.Group],
            GraphTypeDefinitions.For(graphType).Roles.Select(role => role.Role));
        AssertRole(graphType, GraphVariableRole.Variable, required: true, WorksheetDataType.Numeric);
        AssertRole(graphType, GraphVariableRole.Group, required: false, WorksheetDataType.Numeric, WorksheetDataType.String);
    }

    [Fact]
    public void RequiredAndOptionalRolesAreSeparated()
    {
        var scatter = GraphTypeDefinitions.For(GraphType.ScatterPlot);

        Assert.Equal([GraphVariableRole.X, GraphVariableRole.Y], scatter.RequiredRoles.Select(role => role.Role));
        Assert.Equal([GraphVariableRole.Group], scatter.OptionalRoles.Select(role => role.Role));
    }

    [Fact]
    public void NoV1GraphTypeHasRolesBeyondTheV1Set()
    {
        GraphVariableRole[] v1Roles = [GraphVariableRole.X, GraphVariableRole.Y, GraphVariableRole.Variable, GraphVariableRole.Group];

        Assert.All(GraphTypeDefinitions.All, definition => Assert.All(definition.Roles, role => Assert.Contains(role.Role, v1Roles)));
        Assert.All(GraphTypeDefinitions.All, definition => Assert.Equal(
            definition.Roles.Select(role => role.Role).Distinct().Count(), definition.Roles.Count));
    }

    [Fact]
    public void RolesAcceptOnlyTheirDataTypes()
    {
        var x = GraphTypeDefinitions.For(GraphType.ScatterPlot).FindRole(GraphVariableRole.X)!;
        var group = GraphTypeDefinitions.For(GraphType.ScatterPlot).FindRole(GraphVariableRole.Group)!;

        Assert.True(x.Allows(WorksheetDataType.Numeric));
        Assert.False(x.Allows(WorksheetDataType.String));
        Assert.True(group.Allows(WorksheetDataType.Numeric));
        Assert.True(group.Allows(WorksheetDataType.String));
    }

    [Fact]
    public void UnknownGraphTypesAreRejectedAndKnownOnesAreFound()
    {
        Assert.False(GraphTypeDefinitions.TryGet((GraphType)99, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphTypeDefinitions.For((GraphType)99));
        Assert.True(GraphTypeDefinitions.TryGet(GraphType.Histogram, out var histogram));
        Assert.Equal(GraphType.Histogram, histogram.GraphType);
        Assert.Null(histogram.FindRole(GraphVariableRole.X));
    }
}
