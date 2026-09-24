using YAT.Application.Graphs;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// Legend options (Task #044): what a configuration carries, which graph types have a legend, and the one rule the
// options obey - both choices are defined values.
public class GraphLegendOptionsTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly WorksheetColumn Reg1 = new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = WorksheetId,
        Name = "Reg1",
        Index = 0,
        DataType = WorksheetDataType.Numeric
    };

    [Fact]
    public void EveryConfigurationStartsWithTheGraphTypesLegendOnTheRight()
    {
        var configuration = new GraphConfiguration(GraphType.Histogram, WorksheetId, []);

        Assert.Same(GraphLegendOptions.Default, configuration.LegendOptions);
        Assert.Equal(new GraphLegendOptions(GraphLegendMode.Auto, GraphLegendPosition.Right), GraphLegendOptions.Default);
        Assert.Equal(GraphLegendOptions.Default, new GraphLegendOptions());
        Assert.True(GraphLegendOptions.Default.IsValid);
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    [InlineData(GraphType.BoxPlot)]
    public void EveryGraphTypeCanHaveALegend(GraphType type) =>
        Assert.True(GraphTypeDefinitions.For(type).Supports(GraphCapability.Legend));

    [Fact]
    public void EveryModeAndSideIsAValidChoice()
    {
        foreach (var mode in Enum.GetValues<GraphLegendMode>())
        {
            foreach (var position in Enum.GetValues<GraphLegendPosition>())
            {
                Assert.True(new GraphLegendOptions(mode, position).IsValid);
            }
        }
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(0, 4)]
    [InlineData(-1, -1)]
    public void ChoicesThatAreNotDefinedAreRefused(int mode, int position)
    {
        var configuration = new GraphConfiguration(GraphType.Histogram, WorksheetId, [new(GraphVariableRole.Variable, Reg1.Id)])
        {
            LegendOptions = new GraphLegendOptions((GraphLegendMode)mode, (GraphLegendPosition)position)
        };

        Assert.False(configuration.LegendOptions.IsValid);
        Assert.Equal(
            new GraphValidationError(GraphValidationReason.LegendOptionsInvalid),
            Assert.Single(new GraphConfigurationValidator().Validate(configuration, [Reg1]).Errors));
    }
}
