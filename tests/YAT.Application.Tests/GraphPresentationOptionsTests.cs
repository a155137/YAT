using YAT.Application.Graphs;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// How a graph is presented, as opposed to what it plots: carried by the configuration, read by nobody who reads data.
public class GraphPresentationOptionsTests
{
    private static GraphConfiguration Configuration() =>
        new(GraphType.Histogram, Guid.NewGuid(), [new GraphColumnAssignment(GraphVariableRole.Variable, Guid.NewGuid())]);

    [Fact]
    public void StatisticsAreShownByDefault()
    {
        Assert.True(new GraphPresentationOptions().ShowStatistics);
        Assert.True(GraphPresentationOptions.Default.ShowStatistics);
    }

    [Fact]
    public void AConfigurationStartsWithTheDefaultOptions()
    {
        var configuration = Configuration();

        Assert.Same(GraphPresentationOptions.Default, configuration.PresentationOptions);
        Assert.True(configuration.PresentationOptions.ShowStatistics);
    }

    [Fact]
    public void AConfigurationCarriesTheOptionsItWasGiven()
    {
        var configuration = Configuration() with { PresentationOptions = new GraphPresentationOptions(ShowStatistics: false) };

        Assert.False(configuration.PresentationOptions.ShowStatistics);
    }

    [Fact]
    public void OptionsCompareByValue()
    {
        Assert.Equal(new GraphPresentationOptions(false), new GraphPresentationOptions(false));
        Assert.NotEqual(new GraphPresentationOptions(false), GraphPresentationOptions.Default);
        Assert.Equal(new GraphPresentationOptions(true), GraphPresentationOptions.Default);
    }

    [Fact]
    public void TurningStatisticsOffLeavesTheAssignmentsAlone()
    {
        var configuration = Configuration();
        var off = configuration with { PresentationOptions = new GraphPresentationOptions(false) };

        Assert.Equal(configuration.GraphType, off.GraphType);
        Assert.Equal(configuration.WorksheetId, off.WorksheetId);
        Assert.Equal(configuration.Assignments, off.Assignments);
    }
}
