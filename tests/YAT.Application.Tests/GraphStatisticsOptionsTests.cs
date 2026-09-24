using YAT.Application.Graphs;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// How a graph's statistics panel is presented (Task #045, replacing the old ShowStatistics option): carried by the
// configuration, read by nobody who reads data, and checked by the configuration's validation - a defined mode, and at
// least one statistic unless the panel is hidden.
public class GraphStatisticsOptionsTests
{
    private static readonly GraphConfigurationValidator Validator = new();

    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly Guid Reg1 = Guid.NewGuid();

    private static readonly IReadOnlyList<WorksheetColumn> Columns =
    [
        new() { Id = Reg1, WorksheetId = WorksheetId, Name = "Reg1", Index = 0, DataType = WorksheetDataType.Numeric }
    ];

    private static GraphConfiguration Configuration(GraphType type = GraphType.Histogram) =>
        new(type, WorksheetId, [new GraphColumnAssignment(GraphVariableRole.Variable, Reg1)]);

    private static IReadOnlyList<GraphValidationReason> Reasons(GraphConfiguration configuration) =>
        [.. Validator.Validate(configuration, Columns).Errors.Select(error => error.Reason)];

    [Fact]
    public void TheDefaultIsThePanelAsItAlwaysWas()
    {
        var options = GraphStatisticsOptions.Default;

        Assert.Equal(GraphStatisticsMode.Auto, options.Mode);
        Assert.True(options.ShowMean);
        Assert.True(options.ShowStandardDeviation);
        Assert.True(options.ShowCount);
        Assert.Equal(new GraphStatisticsOptions(), options);
        Assert.True(options.IsValid);
        Assert.Equal(GraphStatisticsOptions.AllItems, options.Items);
    }

    [Fact]
    public void AConfigurationStartsWithTheDefaultOptions()
    {
        Assert.Same(GraphStatisticsOptions.Default, Configuration().StatisticsOptions);
    }

    [Fact]
    public void AConfigurationCarriesTheOptionsItWasGiven()
    {
        var hidden = new GraphStatisticsOptions(GraphStatisticsMode.Hide, ShowCount: false);
        var configuration = Configuration() with { StatisticsOptions = hidden };

        Assert.Same(hidden, configuration.StatisticsOptions);
    }

    [Fact]
    public void ChangingTheStatisticsLeavesTheAssignmentsAlone()
    {
        var configuration = Configuration();
        var hidden = configuration with { StatisticsOptions = new GraphStatisticsOptions(GraphStatisticsMode.Hide) };

        Assert.Equal(configuration.GraphType, hidden.GraphType);
        Assert.Equal(configuration.WorksheetId, hidden.WorksheetId);
        Assert.Equal(configuration.Assignments, hidden.Assignments);
    }

    [Fact]
    public void OptionsCompareByValue()
    {
        Assert.Equal(new GraphStatisticsOptions(GraphStatisticsMode.Show, false), new GraphStatisticsOptions(GraphStatisticsMode.Show, false));
        Assert.NotEqual(new GraphStatisticsOptions(GraphStatisticsMode.Show), GraphStatisticsOptions.Default);
        Assert.NotEqual(new GraphStatisticsOptions(ShowCount: false), GraphStatisticsOptions.Default);
        Assert.Equal(new GraphStatisticsOptions(GraphStatisticsMode.Auto, true, true, true), GraphStatisticsOptions.Default);
    }

    // The statistics a panel shows are always in the panel's order, Mean, StDev, N, whatever is chosen.
    [Theory]
    [InlineData(true, false, false, new[] { GraphStatisticsItem.Mean })]
    [InlineData(false, true, false, new[] { GraphStatisticsItem.StandardDeviation })]
    [InlineData(false, false, true, new[] { GraphStatisticsItem.Count })]
    [InlineData(true, false, true, new[] { GraphStatisticsItem.Mean, GraphStatisticsItem.Count })]
    [InlineData(false, true, true, new[] { GraphStatisticsItem.StandardDeviation, GraphStatisticsItem.Count })]
    [InlineData(true, true, false, new[] { GraphStatisticsItem.Mean, GraphStatisticsItem.StandardDeviation })]
    [InlineData(false, false, false, new GraphStatisticsItem[0])]
    public void TheChosenStatisticsAreInPanelOrder(bool mean, bool deviation, bool count, GraphStatisticsItem[] expected)
    {
        var options = new GraphStatisticsOptions(GraphStatisticsMode.Show, mean, deviation, count);

        Assert.Equal(expected, options.Items);
        Assert.Equal(expected.Length > 0, options.HasItems);
    }

    [Theory]
    [InlineData(GraphStatisticsMode.Auto)]
    [InlineData(GraphStatisticsMode.Show)]
    [InlineData(GraphStatisticsMode.Hide)]
    public void EveryModeWithAStatisticIsValid(GraphStatisticsMode mode)
    {
        var options = new GraphStatisticsOptions(mode, ShowMean: false, ShowStandardDeviation: false);

        Assert.True(options.IsValid);
        Assert.Empty(Reasons(Configuration() with { StatisticsOptions = options }));
    }

    [Theory]
    [InlineData(GraphStatisticsMode.Auto)]
    [InlineData(GraphStatisticsMode.Show)]
    public void AShownPanelWithoutAStatisticIsRefused(GraphStatisticsMode mode)
    {
        var none = new GraphStatisticsOptions(mode, false, false, false);

        Assert.False(none.IsValid);
        Assert.True(none.IsModeValid);
        Assert.Equal([GraphValidationReason.StatisticsItemsMissing], Reasons(Configuration() with { StatisticsOptions = none }));
        Assert.Equal([GraphValidationReason.StatisticsItemsMissing], GraphConfigurationValidator.StatisticsErrors(none).Select(error => error.Reason));
    }

    // A hidden panel keeps whatever was chosen for it, nothing included, so it comes back as it was.
    [Fact]
    public void AHiddenPanelMayKeepNoStatisticAtAll()
    {
        var hidden = new GraphStatisticsOptions(GraphStatisticsMode.Hide, false, false, false);

        Assert.True(hidden.IsValid);
        Assert.Empty(hidden.Items);
        Assert.Empty(Reasons(Configuration() with { StatisticsOptions = hidden }));
    }

    [Fact]
    public void AModeThatIsNotAChoiceIsRefused()
    {
        var options = new GraphStatisticsOptions((GraphStatisticsMode)7);

        Assert.False(options.IsModeValid);
        Assert.False(options.IsValid);
        Assert.Equal([GraphValidationReason.StatisticsOptionsInvalid], Reasons(Configuration() with { StatisticsOptions = options }));

        // Only the mode is reported, even with no statistic chosen either.
        Assert.Equal(
            [GraphValidationReason.StatisticsOptionsInvalid],
            GraphConfigurationValidator.StatisticsErrors(new GraphStatisticsOptions((GraphStatisticsMode)(-1), false, false, false)).Select(error => error.Reason));
    }

    // Graph types without a statistics panel never read the options, so nothing about them is refused there.
    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.BoxPlot)]
    public void GraphTypesWithoutAPanelIgnoreTheOptions(GraphType type)
    {
        var definition = GraphTypeDefinitions.For(type);
        var invalid = new GraphStatisticsOptions((GraphStatisticsMode)7, false, false, false);

        Assert.False(definition.Supports(GraphCapability.StatisticsPanel));
        var configuration = new GraphConfiguration(type, WorksheetId, []) { StatisticsOptions = invalid };
        Assert.DoesNotContain(GraphValidationReason.StatisticsOptionsInvalid, Validator.Validate(configuration, Columns).Errors.Select(error => error.Reason));
        Assert.DoesNotContain(GraphValidationReason.StatisticsItemsMissing, Validator.Validate(configuration, Columns).Errors.Select(error => error.Reason));
    }

    [Fact]
    public void OnlyTheDistributionGraphsHaveAPanel()
    {
        Assert.Equal(
            [GraphType.Histogram, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf],
            GraphTypeDefinitions.All.Where(definition => definition.Supports(GraphCapability.StatisticsPanel)).Select(definition => definition.GraphType));
    }
}
