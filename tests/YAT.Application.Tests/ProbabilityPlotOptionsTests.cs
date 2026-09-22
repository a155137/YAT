using YAT.Application.Graphs;

namespace YAT.Application.Tests;

// The probability plot's own options (Task #037): carried by every configuration with the fitted line on, read only by
// a graph type that declares the fitted-line capability.
public class ProbabilityPlotOptionsTests
{
    private static GraphConfiguration Configuration(GraphType graphType = GraphType.ProbabilityPlot) =>
        new(graphType, Guid.NewGuid(), [new GraphColumnAssignment(GraphVariableRole.Variable, Guid.NewGuid())]);

    [Fact]
    public void TheFittedLineIsShownByDefault()
    {
        Assert.True(new ProbabilityPlotOptions().ShowFittedLine);
        Assert.True(ProbabilityPlotOptions.Default.ShowFittedLine);
    }

    [Fact]
    public void TheFittedLineCanBeTurnedOnOrOffExplicitly()
    {
        Assert.True(new ProbabilityPlotOptions(ShowFittedLine: true).ShowFittedLine);
        Assert.False(new ProbabilityPlotOptions(ShowFittedLine: false).ShowFittedLine);
    }

    [Fact]
    public void OptionsCompareByValue()
    {
        Assert.Equal(new ProbabilityPlotOptions(true), ProbabilityPlotOptions.Default);
        Assert.NotEqual(new ProbabilityPlotOptions(false), ProbabilityPlotOptions.Default);
    }

    [Theory]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ScatterPlot)]
    public void EveryConfigurationStartsWithTheDefaultOptions(GraphType graphType)
    {
        var configuration = Configuration(graphType);

        Assert.Same(ProbabilityPlotOptions.Default, configuration.ProbabilityPlotOptions);
    }

    [Fact]
    public void TurningTheLineOffLeavesEverythingElseAlone()
    {
        var configuration = Configuration();
        var off = configuration with { ProbabilityPlotOptions = new ProbabilityPlotOptions(false) };

        Assert.False(off.ProbabilityPlotOptions.ShowFittedLine);
        Assert.Equal(configuration.GraphType, off.GraphType);
        Assert.Equal(configuration.Assignments, off.Assignments);
        Assert.Same(configuration.PresentationOptions, off.PresentationOptions);
        Assert.Same(configuration.Specification, off.Specification);
    }

    [Fact]
    public void AnOptionOnAGraphTypeWithoutTheCapabilityIsNotAnError()
    {
        // Ignored rather than rejected, like every option on a graph type that does not declare it.
        var worksheetId = Guid.NewGuid();
        var column = new YAT.Domain.Entities.WorksheetColumn
        {
            Id = Guid.NewGuid(),
            WorksheetId = worksheetId,
            Index = 0,
            Name = "Reg1",
            DataType = YAT.Domain.Enums.WorksheetDataType.Numeric
        };
        var histogram = new GraphConfiguration(GraphType.Histogram, worksheetId, [new GraphColumnAssignment(GraphVariableRole.Variable, column.Id)])
        {
            ProbabilityPlotOptions = new ProbabilityPlotOptions(false)
        };

        Assert.True(new GraphConfigurationValidator().Validate(histogram, [column]).IsValid);
        Assert.False(GraphTypeDefinitions.For(GraphType.Histogram).Supports(GraphCapability.FittedLine));
    }
}
