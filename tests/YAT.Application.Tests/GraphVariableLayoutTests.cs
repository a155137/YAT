using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// Several variables in one graph setup (Task #041): the variables of every graph but the scatter plot are picked
// several at a time, at most ten; a request says whether they are drawn together or each in a graph of its own, and a
// variable's own graph is configured exactly as setting up that one variable would configure it.
public class GraphVariableLayoutTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly IReadOnlyList<WorksheetColumn> Columns =
    [
        .. Enumerable.Range(1, 12).Select(index => Column($"Reg{index}", WorksheetDataType.Numeric, index - 1)),
        Column("Lot", WorksheetDataType.String, 12)
    ];

    private static WorksheetColumn Column(string name, WorksheetDataType type, int index) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = WorksheetId,
        Index = index,
        Name = name,
        DataType = type
    };

    private static WorksheetColumn Named(string name) => Columns.Single(column => column.Name == name);

    private static GraphConfiguration Configuration(GraphType type, int variables, bool grouped = false) =>
        new(type, WorksheetId,
        [
            .. Columns.Take(variables).Select(column => new GraphColumnAssignment(GraphVariableRole.Variable, column.Id)),
            .. grouped ? [new GraphColumnAssignment(GraphVariableRole.Group, Named("Lot").Id)] : Array.Empty<GraphColumnAssignment>()
        ]);

    public static TheoryData<GraphType> GraphsOfVariables =>
        [GraphType.Histogram, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf, GraphType.BoxPlot];

    [Theory]
    [MemberData(nameof(GraphsOfVariables))]
    public void UpToTenVariablesCanBeGraphed(GraphType type)
    {
        var validator = new GraphConfigurationValidator();

        Assert.Equal(10, GraphRoleDefinition.MaximumColumns);
        foreach (var count in new[] { 1, 2, 10 })
        {
            Assert.True(validator.Validate(Configuration(type, count, grouped: true), Columns).IsValid, $"{count} variables");
        }

        var eleven = validator.Validate(Configuration(type, 11), Columns);
        var error = Assert.Single(eleven.Errors);
        Assert.Equal(GraphValidationReason.TooManyColumns, error.Reason);
        Assert.Equal(GraphVariableRole.Variable, error.Role);
    }

    [Theory]
    [MemberData(nameof(GraphsOfVariables))]
    public void EveryGraphOfVariablesOffersTheLayout(GraphType type) =>
        Assert.True(GraphTypeDefinitions.For(type).Supports(GraphCapability.VariableLayout));

    [Fact]
    public void TheScatterPlotHasNoLayout() =>
        Assert.False(GraphTypeDefinitions.For(GraphType.ScatterPlot).Supports(GraphCapability.VariableLayout));

    [Fact]
    public void AVariablesOwnGraphKeepsEverythingButTheOtherVariables()
    {
        var labels = new GraphLabelOptions(GraphLabelOption.Custom("Wafer"), GraphLabelOption.Hidden, GraphLabelOption.Auto);
        var configuration = Configuration(GraphType.Histogram, 3, grouped: true) with
        {
            StatisticsOptions = new GraphStatisticsOptions(GraphStatisticsMode.Hide),
            Specification = new Specification(1, 2, 3),
            HistogramOptions = new HistogramOptions(HistogramYScale.Density),
            ProbabilityPlotOptions = new ProbabilityPlotOptions(ShowFittedLine: false),
            LabelOptions = labels
        };

        var reg2 = GraphSetupRequest.ForVariable(configuration, Named("Reg2").Id);

        Assert.Equal([Named("Reg2").Id], reg2.FindColumnIds(GraphVariableRole.Variable));
        Assert.Equal(Named("Lot").Id, reg2.FindColumnId(GraphVariableRole.Group));
        Assert.Equal(
            [GraphVariableRole.Variable, GraphVariableRole.Group],
            reg2.Assignments.Select(assignment => assignment.Role));
        Assert.Equal(configuration.GraphType, reg2.GraphType);
        Assert.Equal(configuration.WorksheetId, reg2.WorksheetId);
        Assert.Same(configuration.StatisticsOptions, reg2.StatisticsOptions);
        Assert.Same(configuration.Specification, reg2.Specification);
        Assert.Same(configuration.HistogramOptions, reg2.HistogramOptions);
        Assert.Same(configuration.ProbabilityPlotOptions, reg2.ProbabilityPlotOptions);
        Assert.Same(labels, reg2.LabelOptions);
        Assert.True(new GraphConfigurationValidator().Validate(reg2, Columns).IsValid);
    }

    [Fact]
    public void ARequestDrawsItsVariablesTogetherUnlessItSaysOtherwise()
    {
        var configuration = Configuration(GraphType.Histogram, 2);

        Assert.Equal(GraphVariableLayout.Together, new GraphSetupRequest(configuration).Layout);
        Assert.Equal(GraphVariableLayout.Separate, new GraphSetupRequest(configuration, GraphVariableLayout.Separate).Layout);
        Assert.Same(configuration, new GraphSetupRequest(configuration).Configuration);
    }
}
