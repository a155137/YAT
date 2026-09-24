using YAT.Application.Graphs;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Task #046.2: the setup dialog no longer lists the worksheet's columns on their own; the roles' selectors - Graph
// variables above all - are where columns are picked. For every graph type, those selectors offer the worksheet's
// compatible columns, and picking one, several or a group gives the configuration it always did.
public class GraphSetupColumnPickerTests
{
    private static readonly Worksheet Worksheet = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

    private static readonly WorksheetColumn Lot = Column("Lot", WorksheetDataType.String, 0);

    // Eleven numeric columns: one more than a role may take.
    private static readonly IReadOnlyList<WorksheetColumn> Numeric =
        [.. Enumerable.Range(1, 11).Select(index => Column($"V{index}", WorksheetDataType.Numeric, index))];

    private static readonly IReadOnlyList<WorksheetColumn> Columns = [Lot, .. Numeric];

    public static TheoryData<GraphType> EveryGraphType => [.. GraphTypeDefinitions.All.Select(definition => definition.GraphType)];

    public static TheoryData<GraphType> SeveralVariableGraphTypes =>
        [GraphType.Histogram, GraphType.BoxPlot, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf];

    private static WorksheetColumn Column(string name, WorksheetDataType dataType, int index) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = Worksheet.Id,
        Index = index,
        Name = name,
        DataType = dataType
    };

    private static GraphSetupViewModel Setup(GraphType graphType) =>
        new(GraphTypeDefinitions.For(graphType), Worksheet, Columns);

    private static GraphRoleViewModel Role(GraphSetupViewModel setup, GraphVariableRole role) =>
        Assert.Single(setup.Roles, candidate => candidate.Role == role);

    private static GraphColumnOption Option(GraphRoleViewModel role, WorksheetColumn column) =>
        Assert.Single(role.Options, option => option.WorksheetColumnId == column.Id);

    private static void Pick(GraphRoleViewModel role, WorksheetColumn column)
    {
        if (role.AllowsMultiple)
        {
            role.SelectedOptions.Add(Option(role, column));
        }
        else
        {
            role.SelectedOption = Option(role, column);
        }
    }

    // Every role's selector offers the worksheet's columns its data type allows, in worksheet order - an optional
    // role (None) first - so there is nothing a separate list of columns would show that the selectors do not.
    [Theory]
    [MemberData(nameof(EveryGraphType))]
    public void EveryRoleSelectorOffersTheWorksheetsCompatibleColumns(GraphType graphType)
    {
        var setup = Setup(graphType);
        string[] numeric = [.. Numeric.Select(column => column.Name)];

        foreach (var role in setup.Roles)
        {
            if (role.IsRequired)
            {
                Assert.Equal(numeric, role.Options.Select(option => option.Name));
            }
            else
            {
                Assert.Equal(["(None)", "Lot", .. numeric], role.Options.Select(option => option.Name));
            }
        }
    }

    [Theory]
    [MemberData(nameof(SeveralVariableGraphTypes))]
    public void GraphVariablesIsTheSeveralColumnSelectorOfEveryGraphTypeThatDrawsSeveral(GraphType graphType)
    {
        var setup = Setup(graphType);
        var variables = Role(setup, GraphVariableRole.Variable);

        Assert.Equal("Graph variables", variables.DisplayName);
        Assert.True(variables.AllowsMultiple);
        Assert.True(variables.IsRequired);
        Assert.True(setup.SupportsVariableLayout);
        Assert.Equal(11, variables.Options.Count);
    }

    [Theory]
    [MemberData(nameof(SeveralVariableGraphTypes))]
    public void OneGraphVariableGivesItsConfiguration(GraphType graphType)
    {
        var setup = Setup(graphType);
        Pick(Role(setup, GraphVariableRole.Variable), Numeric[2]);

        var request = setup.ConfirmRequest();

        Assert.NotNull(request);
        Assert.Equal(GraphVariableLayout.Together, request.Layout);
        Assert.Equal(graphType, request.Configuration.GraphType);
        Assert.Equal(Worksheet.Id, request.Configuration.WorksheetId);
        Assert.Equal(
            [new GraphColumnAssignment(GraphVariableRole.Variable, Numeric[2].Id)],
            request.Configuration.Assignments);
        Assert.False(setup.IsLayoutEnabled);
    }

    // Several variables, picked in any order, are assigned in worksheet order; with a group, the group comes after them.
    [Theory]
    [MemberData(nameof(SeveralVariableGraphTypes))]
    public void SeveralGraphVariablesAndAGroupGiveTheirConfiguration(GraphType graphType)
    {
        var setup = Setup(graphType);
        var variables = Role(setup, GraphVariableRole.Variable);
        Pick(variables, Numeric[4]);
        Pick(variables, Numeric[0]);
        Pick(variables, Numeric[7]);
        Pick(Role(setup, GraphVariableRole.Group), Lot);

        var configuration = setup.Confirm();

        Assert.NotNull(configuration);
        Assert.Equal(
            [
                new GraphColumnAssignment(GraphVariableRole.Variable, Numeric[0].Id),
                new GraphColumnAssignment(GraphVariableRole.Variable, Numeric[4].Id),
                new GraphColumnAssignment(GraphVariableRole.Variable, Numeric[7].Id),
                new GraphColumnAssignment(GraphVariableRole.Group, Lot.Id)
            ],
            configuration.Assignments);
    }

    [Theory]
    [MemberData(nameof(SeveralVariableGraphTypes))]
    public void TogetherOrSeparateIsTheRequestsLayoutOnceSeveralVariablesArePicked(GraphType graphType)
    {
        var setup = Setup(graphType);
        var variables = Role(setup, GraphVariableRole.Variable);
        Pick(variables, Numeric[0]);
        Pick(variables, Numeric[1]);

        Assert.True(setup.IsLayoutEnabled);
        Assert.True(setup.IsTogether);
        var together = setup.ConfirmRequest();

        setup.IsSeparate = true;
        var separate = setup.ConfirmRequest();

        Assert.Equal(GraphVariableLayout.Together, together!.Layout);
        Assert.Equal(GraphVariableLayout.Separate, separate!.Layout);
        Assert.Equal(together.Configuration.Assignments, separate.Configuration.Assignments);
    }

    // At most ten variables, as before.
    [Theory]
    [MemberData(nameof(SeveralVariableGraphTypes))]
    public void TenGraphVariablesAreAllowedAndAnEleventhIsRefused(GraphType graphType)
    {
        var setup = Setup(graphType);
        var variables = Role(setup, GraphVariableRole.Variable);
        foreach (var column in Numeric.Take(GraphRoleDefinition.MaximumColumns))
        {
            Pick(variables, column);
        }

        Assert.NotNull(setup.Confirm());
        Assert.Equal(10, setup.Confirm()!.FindColumnIds(GraphVariableRole.Variable).Count);

        Pick(variables, Numeric[10]);

        Assert.Null(setup.Confirm());
        Assert.False(setup.CanConfirm);
        Assert.Equal("Select at most 10 variables.", setup.ValidationMessage);
    }

    [Theory]
    [MemberData(nameof(SeveralVariableGraphTypes))]
    public void NoGraphVariableIsRefusedAsBefore(GraphType graphType)
    {
        var setup = Setup(graphType);
        Pick(Role(setup, GraphVariableRole.Group), Lot);

        Assert.Null(setup.Confirm());
        Assert.Equal("Please select a variable.", setup.ValidationMessage);
    }

    // The scatter plot picks one column per axis, and a group, from its single-column selectors.
    [Fact]
    public void TheScatterPlotsSingleColumnSelectorsGiveItsConfiguration()
    {
        var setup = Setup(GraphType.ScatterPlot);
        Assert.All(setup.Roles, role => Assert.False(role.AllowsMultiple));
        Assert.False(setup.SupportsVariableLayout);

        Pick(Role(setup, GraphVariableRole.X), Numeric[1]);
        Pick(Role(setup, GraphVariableRole.Y), Numeric[3]);
        Pick(Role(setup, GraphVariableRole.Group), Lot);

        var request = setup.ConfirmRequest();

        Assert.NotNull(request);
        Assert.Equal(GraphVariableLayout.Together, request.Layout);
        Assert.Equal(
            [
                new GraphColumnAssignment(GraphVariableRole.X, Numeric[1].Id),
                new GraphColumnAssignment(GraphVariableRole.Y, Numeric[3].Id),
                new GraphColumnAssignment(GraphVariableRole.Group, Lot.Id)
            ],
            request.Configuration.Assignments);
    }

    // The same picks in two setups give equal configurations: nothing about the dialog enters it.
    [Theory]
    [MemberData(nameof(EveryGraphType))]
    public void TheSamePicksGiveTheSameConfiguration(GraphType graphType)
    {
        GraphConfiguration? Configure()
        {
            var setup = Setup(graphType);
            foreach (var role in setup.Roles.Where(role => role.IsRequired))
            {
                Pick(role, role.Role == GraphVariableRole.Y ? Numeric[1] : Numeric[0]);
            }

            Pick(Role(setup, GraphVariableRole.Group), Lot);
            return setup.Confirm();
        }

        var first = Configure();
        var second = Configure();

        Assert.NotNull(first);
        Assert.Equal(first.Assignments, second!.Assignments);
        Assert.Equal(first with { Assignments = second.Assignments }, second);
    }
}
