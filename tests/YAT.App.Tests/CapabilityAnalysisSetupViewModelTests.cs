using YAT.Application.Analyses;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The capability setup: which columns it offers, what a specification has to look like before it can be confirmed,
// and which statistics a result shows to begin with.
public class CapabilityAnalysisSetupViewModelTests
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

    private static (CapabilityAnalysisSetupViewModel Setup, IReadOnlyList<WorksheetColumn> Columns) Create()
    {
        IReadOnlyList<WorksheetColumn> columns =
        [
            Column("SITE", WorksheetDataType.Numeric, 0),
            Column("Lot", WorksheetDataType.String, 1),
            Column("Reg1", WorksheetDataType.Numeric, 2),
            Column("Reg2", WorksheetDataType.Numeric, 3)
        ];

        return (new CapabilityAnalysisSetupViewModel("Capability Analysis", Sheet, columns), columns);
    }

    private static CapabilityVariableViewModel Variable(CapabilityAnalysisSetupViewModel setup, string name) =>
        setup.Variables.Single(variable => variable.Name == name);

    // 0
    [Fact]
    public void VariablesAreTheNumericColumnsAndGroupingAlsoOffersTextColumns()
    {
        var (setup, _) = Create();

        Assert.Equal("Capability Analysis", setup.Title);
        Assert.Equal(Sheet.Id, setup.WorksheetId);
        Assert.Equal("Sheet1", setup.WorksheetName);
        Assert.Equal(["SITE", "Reg1", "Reg2"], setup.Variables.Select(variable => variable.Name));
        Assert.Equal(["(None)", "SITE", "Lot", "Reg1", "Reg2"], setup.GroupOptions.Select(option => option.Name));
    }

    // 1
    [Fact]
    public void TheDefaultDisplaySelectionIsTheDataAndNotTheIndices()
    {
        var (setup, _) = Create();

        Assert.Equal(
            ["N", "Missing", "Mean", "Within StDev", "LSL", "USL", "Cp", "Cpl", "Cpu", "Cpk"],
            setup.Statistics.Select(statistic => statistic.DisplayName));

        Assert.Equal(
            ["N", "Mean", "Within StDev"],
            setup.Statistics.Where(statistic => statistic.IsSelected).Select(statistic => statistic.DisplayName));
    }

    // 2
    [Fact]
    public void NothingIsSelectedUntilTheUserSelectsIt()
    {
        var (setup, _) = Create();

        Assert.All(setup.Variables, variable => Assert.False(variable.IsSelected));
        Assert.All(setup.Variables, variable => Assert.Equal(string.Empty, variable.LowerSpecificationLimitText));
        Assert.True(setup.SelectedGroup!.IsNone);
        Assert.False(setup.CanConfirm);
        Assert.Null(setup.ValidationMessage);
    }

    // 3
    [Fact]
    public void AVariableWithoutASpecificationCannotBeConfirmed()
    {
        var (setup, _) = Create();
        Variable(setup, "Reg1").IsSelected = true;

        Assert.False(setup.CanConfirm);
        Assert.Null(setup.Confirm());
        Assert.Equal("Reg1: please enter a lower or an upper specification limit.", setup.ValidationMessage);
    }

    // 4
    [Fact]
    public void ATwoSidedSpecificationConfirmsIntoAConfigurationOfItsOwn()
    {
        var (setup, columns) = Create();
        var reg1 = Variable(setup, "Reg1");
        reg1.IsSelected = true;
        reg1.LowerSpecificationLimitText = "14500";
        reg1.UpperSpecificationLimitText = "15500";

        Assert.True(setup.CanConfirm);
        var configuration = setup.Confirm();

        Assert.NotNull(configuration);
        Assert.Equal(Sheet.Id, configuration.WorksheetId);
        var variable = Assert.Single(configuration.Variables);
        Assert.Equal(columns[2].Id, variable.WorksheetColumnId);
        Assert.Equal(14500, variable.LowerSpecificationLimit);
        Assert.Equal(15500, variable.UpperSpecificationLimit);
        Assert.Null(configuration.GroupColumnId);
        Assert.Equal(CapabilityAnalysisConfiguration.DefaultDisplayStatistics, configuration.DisplayStatistics);
    }

    // 5
    [Fact]
    public void EachVariableKeepsItsOwnLimitsAndBlankMeansThatSideIsNotSpecified()
    {
        var (setup, columns) = Create();
        var reg1 = Variable(setup, "Reg1");
        reg1.IsSelected = true;
        reg1.LowerSpecificationLimitText = "14500";
        reg1.UpperSpecificationLimitText = "15500";

        var reg2 = Variable(setup, "Reg2");
        reg2.IsSelected = true;
        reg2.UpperSpecificationLimitText = "500";

        var configuration = setup.Confirm();

        Assert.Equal([columns[2].Id, columns[3].Id], configuration!.Variables.Select(variable => variable.WorksheetColumnId));
        Assert.Equal([14500, null], configuration.Variables.Select(variable => variable.LowerSpecificationLimit));
        Assert.Equal([15500, 500], configuration.Variables.Select(variable => variable.UpperSpecificationLimit));
    }

    // 6
    [Fact]
    public void TheLowerLimitHasToBeBelowTheUpperOne()
    {
        var (setup, _) = Create();
        var reg1 = Variable(setup, "Reg1");
        reg1.IsSelected = true;
        reg1.LowerSpecificationLimitText = "15500";
        reg1.UpperSpecificationLimitText = "14500";

        Assert.False(setup.CanConfirm);
        Assert.Null(setup.Confirm());
        Assert.Equal("Reg1: the lower specification limit must be below the upper one.", setup.ValidationMessage);
    }

    // 7
    [Theory]
    [InlineData("abc", "Reg1: the lower specification limit must be a number.")]
    [InlineData("NaN", "Reg1: the lower specification limit must be a number.")]
    public void TextThatIsNotANumberIsReportedForThatVariableAndThatSide(string text, string message)
    {
        var (setup, _) = Create();
        var reg1 = Variable(setup, "Reg1");
        reg1.IsSelected = true;
        reg1.LowerSpecificationLimitText = text;
        reg1.UpperSpecificationLimitText = "15500";

        Assert.False(setup.CanConfirm);
        Assert.Null(setup.Confirm());
        Assert.Equal(message, setup.ValidationMessage);
    }

    // 8
    [Fact]
    public void AnUpperLimitThatIsNotANumberNamesTheUpperSide()
    {
        var (setup, _) = Create();
        var reg1 = Variable(setup, "Reg1");
        reg1.IsSelected = true;
        reg1.UpperSpecificationLimitText = "1.2.3";

        Assert.Null(setup.Confirm());
        Assert.Equal("Reg1: the upper specification limit must be a number.", setup.ValidationMessage);
    }

    // 9
    [Fact]
    public void AGroupingColumnIsRecordedByItsIdWhenOneIsChosen()
    {
        var (setup, columns) = Create();
        var reg1 = Variable(setup, "Reg1");
        reg1.IsSelected = true;
        reg1.UpperSpecificationLimitText = "500";

        setup.SelectedGroup = setup.GroupOptions.Single(option => option.Name == "Lot");

        Assert.Equal(columns[1].Id, setup.Confirm()!.GroupColumnId);
    }

    // 10
    [Fact]
    public void SwitchingAStatisticOnAddsItToWhatTheResultShows()
    {
        var (setup, _) = Create();
        var reg1 = Variable(setup, "Reg1");
        reg1.IsSelected = true;
        reg1.LowerSpecificationLimitText = "14500";
        reg1.UpperSpecificationLimitText = "15500";

        setup.Statistics.Single(statistic => statistic.Statistic == CapabilityStatistic.Cp).IsSelected = true;
        setup.Statistics.Single(statistic => statistic.Statistic == CapabilityStatistic.Cpk).IsSelected = true;

        Assert.Equal(
            [CapabilityStatistic.Count, CapabilityStatistic.Mean, CapabilityStatistic.WithinStandardDeviation, CapabilityStatistic.Cp, CapabilityStatistic.Cpk],
            setup.Confirm()!.DisplayStatistics);
    }

    // 11
    [Fact]
    public void AResultThatWouldShowNothingCannotBeConfirmed()
    {
        var (setup, _) = Create();
        var reg1 = Variable(setup, "Reg1");
        reg1.IsSelected = true;
        reg1.UpperSpecificationLimitText = "500";

        foreach (var statistic in setup.Statistics)
        {
            statistic.IsSelected = false;
        }

        Assert.False(setup.CanConfirm);
        Assert.Null(setup.Confirm());
        Assert.Equal("Please select at least one statistic to display.", setup.ValidationMessage);
    }

    // 12
    [Fact]
    public void DeselectingTheLastVariableDisablesConfirmationAgain()
    {
        var (setup, _) = Create();
        var reg1 = Variable(setup, "Reg1");
        reg1.IsSelected = true;
        reg1.UpperSpecificationLimitText = "500";
        Assert.True(setup.CanConfirm);

        reg1.IsSelected = false;

        Assert.False(setup.CanConfirm);
        Assert.Empty(setup.SelectedVariables);
        Assert.Null(setup.Confirm());
        Assert.Equal("Please select at least one variable.", setup.ValidationMessage);
    }

    // 13
    [Fact]
    public void LimitsTypedForAVariableThatIsNotSelectedAreIgnored()
    {
        var (setup, columns) = Create();
        var reg1 = Variable(setup, "Reg1");
        reg1.IsSelected = true;
        reg1.UpperSpecificationLimitText = "500";

        // Left over from an earlier attempt, on a row that is no longer ticked.
        Variable(setup, "Reg2").LowerSpecificationLimitText = "not a number";

        var configuration = setup.Confirm();

        Assert.NotNull(configuration);
        Assert.Equal([columns[2].Id], configuration.Variables.Select(variable => variable.WorksheetColumnId));
    }
}
