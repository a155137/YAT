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
    public void TheGraphTypesAreDefinedInMenuOrderWithTheirDisplayNames()
    {
        Assert.Equal(
            [GraphType.ScatterPlot, GraphType.Histogram, GraphType.BoxPlot, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf],
            GraphTypeDefinitions.All.Select(definition => definition.GraphType));
        Assert.Equal(
            ["Scatter Plot", "Histogram", "Box Plot", "Probability Plot", "Empirical CDF"],
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

    // ---- Capabilities ----

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void SingleVariableGraphsOfferTheStatisticsPanelAndSpecificationLines(GraphType graphType)
    {
        var definition = GraphTypeDefinitions.For(graphType);

        Assert.True(definition.Supports(GraphCapability.StatisticsPanel));
        Assert.True(definition.Supports(GraphCapability.SpecificationLines));
        Assert.Equal(
            graphType switch
            {
                GraphType.ProbabilityPlot =>
                    [GraphCapability.StatisticsPanel, GraphCapability.SpecificationLines, GraphCapability.FittedLine, GraphCapability.Labels, GraphCapability.AxisRange, GraphCapability.Legend, GraphCapability.Appearance, GraphCapability.VariableLayout],
                GraphType.Histogram =>
                    [GraphCapability.StatisticsPanel, GraphCapability.SpecificationLines, GraphCapability.HistogramControls, GraphCapability.Labels, GraphCapability.AxisRange, GraphCapability.Legend, GraphCapability.Appearance, GraphCapability.VariableLayout],
                _ => (GraphCapability[])[GraphCapability.StatisticsPanel, GraphCapability.SpecificationLines, GraphCapability.Labels, GraphCapability.AxisRange, GraphCapability.Legend, GraphCapability.Appearance, GraphCapability.VariableLayout]
            },
            definition.Capabilities);
    }

    // Only the probability plot draws a fitted line the user can hide (Task #037).
    [Theory]
    [InlineData(GraphType.ProbabilityPlot, true)]
    [InlineData(GraphType.Histogram, false)]
    [InlineData(GraphType.EmpiricalCdf, false)]
    [InlineData(GraphType.ScatterPlot, false)]
    [InlineData(GraphType.BoxPlot, false)]
    public void OnlyTheProbabilityPlotOffersTheFittedLine(GraphType graphType, bool supported) =>
        Assert.Equal(supported, GraphTypeDefinitions.For(graphType).Supports(GraphCapability.FittedLine));

    // Only the histogram has its own Y scale and bins (Task #039).
    [Theory]
    [InlineData(GraphType.Histogram, true)]
    [InlineData(GraphType.ProbabilityPlot, false)]
    [InlineData(GraphType.EmpiricalCdf, false)]
    [InlineData(GraphType.ScatterPlot, false)]
    [InlineData(GraphType.BoxPlot, false)]
    public void OnlyTheHistogramOffersHistogramControls(GraphType graphType, bool supported) =>
        Assert.Equal(supported, GraphTypeDefinitions.For(graphType).Supports(GraphCapability.HistogramControls));

    // Every graph has a title and axis titles the user may keep, replace or hide (Task #040).
    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.BoxPlot)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void EveryGraphOffersLabels(GraphType graphType) =>
        Assert.True(GraphTypeDefinitions.For(graphType).Supports(GraphCapability.Labels));

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.BoxPlot)]
    public void ScatterAndBoxPlotsHaveNoStatisticsPanelAndNoSpecificationLines(GraphType graphType)
    {
        var definition = GraphTypeDefinitions.For(graphType);

        Assert.False(definition.Supports(GraphCapability.StatisticsPanel));
        Assert.False(definition.Supports(GraphCapability.SpecificationLines));
        Assert.Equal(
            graphType == GraphType.BoxPlot
                ? [GraphCapability.Labels, GraphCapability.AxisRange, GraphCapability.Legend, GraphCapability.Appearance, GraphCapability.VariableLayout]
                : (GraphCapability[])[GraphCapability.Labels, GraphCapability.AxisRange, GraphCapability.Legend, GraphCapability.Appearance],
            definition.Capabilities);
    }

    [Fact]
    public void ADefinitionHasNoCapabilitiesUnlessItDeclaresThem()
    {
        var definition = new GraphTypeDefinition(GraphType.Histogram, "Test", GraphTypeDefinitions.For(GraphType.Histogram).Roles);

        Assert.Empty(definition.Capabilities);
        Assert.False(definition.Supports(GraphCapability.StatisticsPanel));
        Assert.False(definition.Supports(GraphCapability.SpecificationLines));
        Assert.False(definition.Supports(GraphCapability.FittedLine));
        Assert.False(definition.Supports(GraphCapability.Labels));
    }
}
