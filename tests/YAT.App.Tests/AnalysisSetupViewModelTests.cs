using YAT.Application.Analyses;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The analysis setup: which columns it offers for which role, when it may be confirmed, and what the confirmed
// configuration contains.
public class AnalysisSetupViewModelTests
{
    private static readonly Worksheet Sheet = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

    private static WorksheetColumn Column(string name, WorksheetDataType dataType, int index) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = Sheet.Id,
        Index = index,
        Name = name,
        DataType = dataType
    };

    private static (AnalysisSetupViewModel Setup, IReadOnlyList<WorksheetColumn> Columns) Create()
    {
        IReadOnlyList<WorksheetColumn> columns =
        [
            Column("No", WorksheetDataType.Numeric, 0),
            Column("SITE", WorksheetDataType.Numeric, 1),
            Column("Lot", WorksheetDataType.String, 2),
            Column("Reg1", WorksheetDataType.Numeric, 3),
            Column("Reg2", WorksheetDataType.Numeric, 4)
        ];

        return (new AnalysisSetupViewModel("Descriptive Statistics", Sheet, columns), columns);
    }

    // 0
    [Fact]
    public void VariablesAreTheNumericColumnsAndGroupingAlsoOffersTextColumns()
    {
        var (setup, _) = Create();

        Assert.Equal("Descriptive Statistics", setup.Title);
        Assert.Equal(Sheet.Id, setup.WorksheetId);
        Assert.Equal("Sheet1", setup.WorksheetName);
        Assert.Equal(["No", "SITE", "Reg1", "Reg2"], setup.Variables.Select(variable => variable.Name));
        Assert.Equal(["(None)", "No", "SITE", "Lot", "Reg1", "Reg2"], setup.GroupOptions.Select(option => option.Name));
    }

    // 1
    [Fact]
    public void NothingIsSelectedUntilTheUserSelectsIt()
    {
        var (setup, _) = Create();

        Assert.All(setup.Variables, variable => Assert.False(variable.IsSelected));
        Assert.True(setup.SelectedGroup!.IsNone);
        Assert.False(setup.CanConfirm);
        Assert.Null(setup.ValidationMessage);
    }

    // 2
    [Fact]
    public void ConfirmingWithoutAVariableExplainsWhatIsMissing()
    {
        var (setup, _) = Create();

        Assert.Null(setup.Confirm());
        Assert.Equal("Please select at least one variable.", setup.ValidationMessage);
        Assert.False(setup.CanConfirm);
    }

    // 3
    [Fact]
    public void OneSelectedVariableIsEnoughToConfirm()
    {
        var (setup, columns) = Create();

        setup.Variables.Single(variable => variable.Name == "Reg1").IsSelected = true;

        Assert.True(setup.CanConfirm);

        var configuration = setup.Confirm();
        Assert.NotNull(configuration);
        Assert.Equal(Sheet.Id, configuration.WorksheetId);
        Assert.Equal([columns[3].Id], configuration.VariableColumnIds);
        Assert.Null(configuration.GroupColumnId);
        Assert.Null(setup.ValidationMessage);
    }

    // 4
    [Fact]
    public void SeveralVariablesAreConfiguredInWorksheetOrderWhateverOrderTheyWerePicked()
    {
        var (setup, columns) = Create();

        setup.Variables.Single(variable => variable.Name == "Reg2").IsSelected = true;
        setup.Variables.Single(variable => variable.Name == "Reg1").IsSelected = true;

        var configuration = setup.Confirm();

        Assert.Equal([columns[3].Id, columns[4].Id], configuration!.VariableColumnIds);
    }

    // 5
    [Fact]
    public void AGroupingColumnIsRecordedByItsIdWhenOneIsChosen()
    {
        var (setup, columns) = Create();
        setup.Variables.Single(variable => variable.Name == "Reg1").IsSelected = true;

        setup.SelectedGroup = setup.GroupOptions.Single(option => option.Name == "Lot");
        var configuration = setup.Confirm();

        Assert.Equal(columns[2].Id, configuration!.GroupColumnId);
    }

    // 6
    [Fact]
    public void ClearingTheGroupingLeavesTheAnalysisUngrouped()
    {
        var (setup, _) = Create();
        setup.Variables.Single(variable => variable.Name == "Reg1").IsSelected = true;
        setup.SelectedGroup = setup.GroupOptions.Single(option => option.Name == "SITE");

        setup.SelectedGroup = AnalysisColumnOption.None;

        Assert.Null(setup.Confirm()!.GroupColumnId);
    }

    // 7
    [Fact]
    public void DeselectingTheLastVariableDisablesConfirmationAgain()
    {
        var (setup, _) = Create();
        var reg1 = setup.Variables.Single(variable => variable.Name == "Reg1");

        reg1.IsSelected = true;
        Assert.True(setup.CanConfirm);

        reg1.IsSelected = false;
        Assert.False(setup.CanConfirm);
        Assert.Empty(setup.SelectedVariableColumnIds);
    }

    // 8
    [Fact]
    public void AWorksheetWithoutTextColumnsStillOffersNumericGrouping()
    {
        IReadOnlyList<WorksheetColumn> columns = [Column("Reg1", WorksheetDataType.Numeric, 0)];
        var setup = new AnalysisSetupViewModel("Descriptive Statistics", Sheet, columns);

        Assert.Equal(["(None)", "Reg1"], setup.GroupOptions.Select(option => option.Name));
        Assert.Equal(["Reg1"], setup.Variables.Select(variable => variable.Name));
    }
}
