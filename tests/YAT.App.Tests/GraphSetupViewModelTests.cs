using YAT.Application.Graphs;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The graph setup state: roles from the graph specification, compatible columns per role, and the configuration OK produces.
public class GraphSetupViewModelTests
{
    private static readonly Worksheet Worksheet = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

    private static readonly WorksheetColumn No = Column("No", WorksheetDataType.Numeric, 0);
    private static readonly WorksheetColumn Site = Column("SITE", WorksheetDataType.Numeric, 1);
    private static readonly WorksheetColumn Lot = Column("Lot", WorksheetDataType.String, 2);
    private static readonly WorksheetColumn Reg1 = Column("Reg1", WorksheetDataType.Numeric, 3);
    private static readonly WorksheetColumn Reg2 = Column("Reg2", WorksheetDataType.Numeric, 4);

    private static readonly IReadOnlyList<WorksheetColumn> Columns = [No, Site, Lot, Reg1, Reg2];

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

    private static void Assign(GraphSetupViewModel setup, GraphVariableRole role, WorksheetColumn column)
    {
        var roleViewModel = Role(setup, role);
        roleViewModel.SelectedOption = Assert.Single(roleViewModel.Options, option => option.WorksheetColumnId == column.Id);
    }

    [Fact]
    public void ScatterPlotShowsXYAndGroupWithTheWorksheetColumns()
    {
        var setup = Setup(GraphType.ScatterPlot);

        Assert.Equal("Scatter Plot", setup.Title);
        Assert.Equal("Sheet1", setup.WorksheetName);
        Assert.Equal(Worksheet.Id, setup.WorksheetId);
        Assert.Equal([GraphVariableRole.X, GraphVariableRole.Y, GraphVariableRole.Group], setup.Roles.Select(role => role.Role));
        Assert.Equal(["X-axis", "Y-axis", "Group"], setup.Roles.Select(role => role.DisplayName));
        Assert.Equal(["No", "SITE", "Lot", "Reg1", "Reg2"], setup.AvailableColumns.Select(option => option.Name));
        Assert.Equal(["Numeric", "Numeric", "String", "Numeric", "Numeric"], setup.AvailableColumns.Select(option => option.DataTypeName));
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void SingleVariableGraphsShowVariableAndGroup(GraphType graphType)
    {
        var setup = Setup(graphType);

        Assert.Equal([GraphVariableRole.Variable, GraphVariableRole.Group], setup.Roles.Select(role => role.Role));
        Assert.Equal(["Variable", "Group"], setup.Roles.Select(role => role.DisplayName));
    }

    [Fact]
    public void NumericRolesOfferOnlyNumericColumnsAndGroupOffersNoneAndEveryColumn()
    {
        var setup = Setup(GraphType.ScatterPlot);

        Assert.Equal(["No", "SITE", "Reg1", "Reg2"], Role(setup, GraphVariableRole.X).Options.Select(option => option.Name));
        Assert.Equal(["No", "SITE", "Reg1", "Reg2"], Role(setup, GraphVariableRole.Y).Options.Select(option => option.Name));
        Assert.Equal(["(None)", "No", "SITE", "Lot", "Reg1", "Reg2"], Role(setup, GraphVariableRole.Group).Options.Select(option => option.Name));
        Assert.True(Role(setup, GraphVariableRole.Group).Options[0].IsNone);
        Assert.DoesNotContain(Role(setup, GraphVariableRole.X).Options, option => option.IsNone);
    }

    [Fact]
    public void RequiredRolesStartUnassignedAndOptionalGroupStartsAtNone()
    {
        var setup = Setup(GraphType.ScatterPlot);

        Assert.Null(Role(setup, GraphVariableRole.X).SelectedOption);
        Assert.Null(Role(setup, GraphVariableRole.Y).SelectedOption);
        Assert.True(Role(setup, GraphVariableRole.Group).SelectedOption!.IsNone);
        Assert.False(setup.CanConfirm);
    }

    [Fact]
    public void ConfirmIsRefusedWhileARequiredRoleIsUnassigned()
    {
        var setup = Setup(GraphType.ScatterPlot);
        Assign(setup, GraphVariableRole.X, Reg1);

        Assert.False(setup.CanConfirm);
        Assert.Null(setup.Confirm());
        Assert.Equal("Please select a Numeric column for Y-axis.", setup.ValidationMessage);

        Assign(setup, GraphVariableRole.Y, Reg2);

        Assert.True(setup.CanConfirm);
    }

    [Fact]
    public void ConfirmReturnsTheConfigurationOfTheAssignments()
    {
        var setup = Setup(GraphType.ScatterPlot);
        Assign(setup, GraphVariableRole.X, Reg1);
        Assign(setup, GraphVariableRole.Y, Reg2);
        Assign(setup, GraphVariableRole.Group, Lot);

        var configuration = setup.Confirm();

        Assert.NotNull(configuration);
        Assert.Equal(GraphType.ScatterPlot, configuration.GraphType);
        Assert.Equal(Worksheet.Id, configuration.WorksheetId);
        Assert.Equal(
            [(GraphVariableRole.X, Reg1.Id), (GraphVariableRole.Y, Reg2.Id), (GraphVariableRole.Group, Lot.Id)],
            configuration.Assignments.Select(assignment => (assignment.Role, assignment.WorksheetColumnId)));
        Assert.Null(setup.ValidationMessage);
    }

    [Fact]
    public void AnUnassignedGroupIsLeftOutOfTheConfiguration()
    {
        var setup = Setup(GraphType.Histogram);
        Assign(setup, GraphVariableRole.Variable, Reg1);

        var configuration = setup.Confirm();

        Assert.NotNull(configuration);
        Assert.Equal([GraphVariableRole.Variable], configuration.Assignments.Select(assignment => assignment.Role));
        Assert.Null(configuration.FindColumnId(GraphVariableRole.Group));
    }

    [Fact]
    public void AGroupCanBeSetBackToNone()
    {
        var setup = Setup(GraphType.Histogram);
        Assign(setup, GraphVariableRole.Variable, Reg1);
        Assign(setup, GraphVariableRole.Group, Lot);

        Role(setup, GraphVariableRole.Group).SelectedOption = GraphColumnOption.None;

        Assert.True(setup.CanConfirm);
        Assert.Equal([GraphVariableRole.Variable], setup.Confirm()!.Assignments.Select(assignment => assignment.Role));
    }

    [Fact]
    public void AMissingVariableIsReportedInTheSetupsOwnWords()
    {
        var setup = Setup(GraphType.Histogram);

        Assert.Null(setup.Confirm());
        Assert.Equal("Please select a variable.", setup.ValidationMessage);
    }

    [Fact]
    public void ScatterPlotMayUseTheSameColumnTwice()
    {
        var setup = Setup(GraphType.ScatterPlot);
        Assign(setup, GraphVariableRole.X, Reg1);
        Assign(setup, GraphVariableRole.Y, Reg1);

        var configuration = setup.Confirm();

        Assert.NotNull(configuration);
        Assert.Equal(Reg1.Id, configuration.FindColumnId(GraphVariableRole.X));
        Assert.Equal(Reg1.Id, configuration.FindColumnId(GraphVariableRole.Y));
    }

    [Fact]
    public void AWorksheetWithoutNumericColumnsOffersNothingForRequiredRoles()
    {
        var setup = new GraphSetupViewModel(GraphTypeDefinitions.For(GraphType.Histogram), Worksheet, [Lot]);

        Assert.Empty(Role(setup, GraphVariableRole.Variable).Options);
        Assert.Equal(["(None)", "Lot"], Role(setup, GraphVariableRole.Group).Options.Select(option => option.Name));
        Assert.False(setup.CanConfirm);
    }
}
