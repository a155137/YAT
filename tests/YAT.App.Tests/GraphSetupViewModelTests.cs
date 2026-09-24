using YAT.App.Tests.TestDoubles;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
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
        roleViewModel.Choose(Assert.Single(roleViewModel.Options, option => option.WorksheetColumnId == column.Id));
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

    // The single-variable graphs all name the same two roles the way the people who use them do; the roles themselves
    // are unchanged.
    [Theory]
    [InlineData(GraphType.Histogram, "Histogram")]
    [InlineData(GraphType.ProbabilityPlot, "Probability Plot")]
    [InlineData(GraphType.EmpiricalCdf, "Empirical CDF")]
    public void GraphsWithMinitabStyleRoleNamesShowThem(GraphType graphType, string title)
    {
        var setup = Setup(graphType);

        Assert.Equal(title, setup.Title);
        Assert.Equal([GraphVariableRole.Variable, GraphVariableRole.Group], setup.Roles.Select(role => role.Role));
        Assert.Equal(["Graph variables", "Categorical variable for grouping"], setup.Roles.Select(role => role.DisplayName));
        Assert.True(setup.Roles[0].IsRequired);
        Assert.False(setup.Roles[1].IsRequired);
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

    // ---- Options ----

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void GraphsWithTheStatisticsPanelOfferItCheckedToBeginWith(GraphType graphType)
    {
        var setup = Setup(graphType);

        Assert.True(setup.SupportsStatisticsPanel);
        Assert.True(setup.ShowStatistics);
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.BoxPlot)]
    public void GraphsWithoutTheStatisticsPanelDoNotOfferIt(GraphType graphType)
    {
        Assert.False(Setup(graphType).SupportsStatisticsPanel);
    }

    [Fact]
    public void TheConfigurationCarriesWhetherStatisticsAreShown()
    {
        var setup = Setup(GraphType.Histogram);
        Assign(setup, GraphVariableRole.Variable, Reg1);

        Assert.True(setup.Confirm()!.PresentationOptions.ShowStatistics);

        setup.ShowStatistics = false;
        var off = setup.Confirm()!;
        Assert.False(off.PresentationOptions.ShowStatistics);
        Assert.Equal([new GraphColumnAssignment(GraphVariableRole.Variable, Reg1.Id)], off.Assignments);
    }

    [Fact]
    public void ShowStatisticsIsObservable()
    {
        var setup = Setup(GraphType.Histogram);
        var changed = new List<string?>();
        setup.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        setup.ShowStatistics = false;

        Assert.Contains(nameof(GraphSetupViewModel.ShowStatistics), changed);
    }

    // ---- Specification (#036) ----

    private static GraphSetupViewModel Histogram()
    {
        var setup = Setup(GraphType.Histogram);
        Assign(setup, GraphVariableRole.Variable, Reg1);
        return setup;
    }

    private static GraphSetupViewModel WithSpecification(string lower, string target, string upper)
    {
        var setup = Histogram();
        setup.LowerLimitText = lower;
        setup.TargetText = target;
        setup.UpperLimitText = upper;
        return setup;
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void DistributionGraphsOfferABlankSpecification(GraphType graphType)
    {
        var setup = Setup(graphType);

        Assert.True(setup.SupportsSpecificationLines);
        Assert.Equal(string.Empty, setup.LowerLimitText);
        Assert.Equal(string.Empty, setup.TargetText);
        Assert.Equal(string.Empty, setup.UpperLimitText);
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.BoxPlot)]
    public void GraphsWithoutSpecificationLinesDoNotOfferThem(GraphType graphType) =>
        Assert.False(Setup(graphType).SupportsSpecificationLines);

    [Fact]
    public void BlankFieldsGiveNoSpecification()
    {
        var configuration = WithSpecification("", "  ", "\t")!.Confirm()!;

        Assert.True(configuration.Specification.IsEmpty);
    }

    [Theory]
    [InlineData("14.5", "15", "15.5", 14.5, 15.0, 15.5)]
    [InlineData(" 14.5 ", "", "", 14.5, null, null)]
    [InlineData("", "1e-3", "", null, 0.001, null)]
    [InlineData("-2.5E+2", "", "3E2", -250.0, null, 300.0)]
    [InlineData("", "", ".5", null, null, 0.5)]
    public void TypedValuesAreParsedInvariantly(string lower, string target, string upper, double? expectedLower, double? expectedTarget, double? expectedUpper)
    {
        var setup = WithSpecification(lower, target, upper);

        Assert.True(setup.CanConfirm);
        Assert.Equal(new Specification(expectedLower, expectedTarget, expectedUpper), setup.Confirm()!.Specification);
        Assert.Null(setup.ValidationMessage);
    }

    [Theory]
    [InlineData("abc", "", "", "LSL must be a number.")]
    [InlineData("", "NaN", "", "Target must be a number.")]
    [InlineData("", "", "Infinity", "USL must be a number.")]
    [InlineData("", "", "-Infinity", "USL must be a number.")]
    [InlineData("1,000", "", "", "LSL must be a number.")]
    [InlineData("14,5", "", "", "LSL must be a number.")]
    [InlineData("16", "", "15", "LSL must be below USL.")]
    [InlineData("15", "", "15", "LSL must be below USL.")]
    [InlineData("14.5", "16", "15.5", "Target must lie within the specification limits (LSL to USL).")]
    [InlineData("14.5", "14", "", "Target must lie within the specification limits (LSL to USL).")]
    public void AnInvalidSpecificationCannotBeConfirmedAndSaysWhy(string lower, string target, string upper, string message)
    {
        var setup = WithSpecification(lower, target, upper);

        Assert.False(setup.CanConfirm);
        Assert.Null(setup.ValidationMessage);

        Assert.Null(setup.Confirm());
        Assert.Equal(message, setup.ValidationMessage);
        Assert.False(setup.CanConfirm);
    }

    [Theory]
    [InlineData("14.5", "14.5", "15.5")]
    [InlineData("14.5", "15.5", "15.5")]
    [InlineData("", "15", "")]
    public void ATargetOnALimitOrOnItsOwnIsValid(string lower, string target, string upper) =>
        Assert.NotNull(WithSpecification(lower, target, upper).Confirm());

    [Fact]
    public void CanConfirmFollowsTheSpecificationAsItIsTyped()
    {
        var setup = Histogram();
        Assert.True(setup.CanConfirm);

        setup.LowerLimitText = "x";
        Assert.False(setup.CanConfirm);

        setup.LowerLimitText = "14.5";
        Assert.True(setup.CanConfirm);

        setup.UpperLimitText = "14";
        Assert.False(setup.CanConfirm);

        setup.UpperLimitText = string.Empty;
        Assert.True(setup.CanConfirm);
    }

    [Fact]
    public void AMissingVariableIsReportedBeforeTheSpecification()
    {
        var setup = Setup(GraphType.Histogram);
        setup.LowerLimitText = "abc";

        Assert.Null(setup.Confirm());
        Assert.Equal("Please select a variable.", setup.ValidationMessage);
    }

    [Fact]
    public void TextThatIsNotANumberIsReportedBeforeValuesThatDoNotFit()
    {
        var setup = WithSpecification("16", "abc", "15");

        Assert.Null(setup.Confirm());
        Assert.Equal("Target must be a number.", setup.ValidationMessage);
    }

    [Fact]
    public void TheSpecificationAndTheStatisticsOptionAreIndependent()
    {
        var setup = WithSpecification("14.5", "", "15.5");
        setup.ShowStatistics = false;

        var configuration = setup.Confirm()!;

        Assert.False(configuration.PresentationOptions.ShowStatistics);
        Assert.Equal(new Specification(14.5, null, 15.5), configuration.Specification);
    }

    [Fact]
    public void ANewSetupStartsBlankWhateverTheLastOneHad()
    {
        WithSpecification("1", "2", "3").Confirm();

        Assert.True(Histogram().Confirm()!.Specification.IsEmpty);
    }

    // ---- Histogram controls (#039) ----

    private static GraphSetupViewModel HistogramSetup()
    {
        var setup = Setup(GraphType.Histogram);
        Assign(setup, GraphVariableRole.Variable, Reg1);
        return setup;
    }

    private static void Choose(GraphSetupViewModel setup, HistogramBinningMode mode) =>
        setup.SelectedBinning = setup.BinningChoices.Single(choice => choice.Value == mode);

    [Fact]
    public void AHistogramOffersItsControlsStartingFromFrequencyAndAutomaticBins()
    {
        var setup = Setup(GraphType.Histogram);

        Assert.True(setup.SupportsHistogramControls);
        Assert.Equal(["Frequency", "Percent", "Density"], setup.YScaleChoices.Select(choice => choice.Name));
        Assert.Equal(["Auto", "Number of bins", "Bin width and start"], setup.BinningChoices.Select(choice => choice.Name));
        Assert.Equal(HistogramYScale.Frequency, setup.SelectedYScale.Value);
        Assert.Equal(HistogramBinningMode.Auto, setup.SelectedBinning.Value);
        Assert.Equal(string.Empty, setup.BinCountText);
        Assert.False(setup.IsBinCountEnabled);
        Assert.False(setup.IsBinWidthAndStartEnabled);
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    [InlineData(GraphType.BoxPlot)]
    public void OtherGraphsHaveNoHistogramControls(GraphType graphType) =>
        Assert.False(Setup(graphType).SupportsHistogramControls);

    [Fact]
    public void ByDefaultTheHistogramIsConfiguredWithTheDefaultOptions() =>
        Assert.Equal(HistogramOptions.Default, HistogramSetup().Confirm()!.HistogramOptions);

    [Fact]
    public void TheChosenScaleAndBinsReachTheConfiguration()
    {
        var setup = HistogramSetup();
        setup.SelectedYScale = setup.YScaleChoices.Single(choice => choice.Value == HistogramYScale.Density);
        Choose(setup, HistogramBinningMode.WidthAndStart);
        setup.BinWidthText = " 1e2 ";
        setup.BinStartText = "14000";

        Assert.Equal(
            new HistogramOptions(HistogramYScale.Density, HistogramBinningMode.WidthAndStart, BinWidth: 100, BinStart: 14000),
            setup.Confirm()!.HistogramOptions);
    }

    [Fact]
    public void OnlyTheChosenModesFieldsAreEnabledAndRead()
    {
        var setup = HistogramSetup();
        setup.BinCountText = "abc";
        setup.BinWidthText = "-5";
        setup.BinStartText = "x";

        // Auto: nothing typed matters.
        Assert.True(setup.CanConfirm);
        Assert.Equal(HistogramOptions.Default, setup.Confirm()!.HistogramOptions);

        Choose(setup, HistogramBinningMode.Count);
        Assert.True(setup.IsBinCountEnabled);
        Assert.False(setup.IsBinWidthAndStartEnabled);
        Assert.False(setup.CanConfirm);

        setup.BinCountText = "30";
        Assert.True(setup.CanConfirm);
        Assert.Equal(new HistogramOptions(BinningMode: HistogramBinningMode.Count, BinCount: 30), setup.Confirm()!.HistogramOptions);

        Choose(setup, HistogramBinningMode.WidthAndStart);
        Assert.False(setup.IsBinCountEnabled);
        Assert.True(setup.IsBinWidthAndStartEnabled);
        Assert.False(setup.CanConfirm);
    }

    [Theory]
    [InlineData("", "Number of bins must be a whole number from 1 to 200.")]
    [InlineData("0", "Number of bins must be a whole number from 1 to 200.")]
    [InlineData("201", "Number of bins must be a whole number from 1 to 200.")]
    [InlineData("12.5", "Number of bins must be a whole number from 1 to 200.")]
    [InlineData("abc", "Number of bins must be a whole number from 1 to 200.")]
    public void AnInvalidNumberOfBinsSaysWhy(string text, string message)
    {
        var setup = HistogramSetup();
        Choose(setup, HistogramBinningMode.Count);
        setup.BinCountText = text;

        Assert.False(setup.CanConfirm);
        Assert.Null(setup.Confirm());
        Assert.Equal(message, setup.ValidationMessage);
    }

    [Theory]
    [InlineData(" 1 ", 1)]
    [InlineData("200", 200)]
    public void ANumberOfBinsFromOneToTwoHundredIsAccepted(string text, int count)
    {
        var setup = HistogramSetup();
        Choose(setup, HistogramBinningMode.Count);
        setup.BinCountText = text;

        Assert.Equal(count, setup.Confirm()!.HistogramOptions.BinCount);
    }

    [Theory]
    [InlineData("", "0", "Bin width must be a positive number.")]
    [InlineData("0", "0", "Bin width must be a positive number.")]
    [InlineData("-1", "0", "Bin width must be a positive number.")]
    [InlineData("NaN", "0", "Bin width must be a positive number.")]
    [InlineData("Infinity", "0", "Bin width must be a positive number.")]
    [InlineData("1,000", "0", "Bin width must be a positive number.")]
    [InlineData("100", "", "Bin start must be a number.")]
    [InlineData("100", "abc", "Bin start must be a number.")]
    [InlineData("100", "-Infinity", "Bin start must be a number.")]
    public void AnInvalidWidthOrStartSaysWhy(string width, string start, string message)
    {
        var setup = HistogramSetup();
        Choose(setup, HistogramBinningMode.WidthAndStart);
        setup.BinWidthText = width;
        setup.BinStartText = start;

        Assert.False(setup.CanConfirm);
        Assert.Null(setup.Confirm());
        Assert.Equal(message, setup.ValidationMessage);
    }

    [Fact]
    public void HistogramControlsAreIndependentOfStatisticsAndSpecification()
    {
        var setup = HistogramSetup();
        setup.SelectedYScale = setup.YScaleChoices.Single(choice => choice.Value == HistogramYScale.Percent);
        setup.ShowStatistics = false;
        setup.LowerLimitText = "14.5";

        var configuration = setup.Confirm()!;

        Assert.Equal(HistogramYScale.Percent, configuration.HistogramOptions.YScale);
        Assert.False(configuration.PresentationOptions.ShowStatistics);
        Assert.Equal(new Specification(14.5, null, null), configuration.Specification);
    }

    [Fact]
    public void AnotherGraphTypeIsConfiguredWithTheDefaultHistogramOptions()
    {
        var setup = Setup(GraphType.EmpiricalCdf);
        Assign(setup, GraphVariableRole.Variable, Reg1);
        Choose(setup, HistogramBinningMode.Count);
        setup.BinCountText = "abc";

        Assert.True(setup.CanConfirm);
        Assert.Same(HistogramOptions.Default, setup.Confirm()!.HistogramOptions);
    }

    [Fact]
    public void ANewSetupStartsWithTheDefaultHistogramOptionsWhateverTheLastOneHad()
    {
        var first = HistogramSetup();
        Choose(first, HistogramBinningMode.Count);
        first.BinCountText = "10";
        first.Confirm();

        Assert.Equal(HistogramOptions.Default, HistogramSetup().Confirm()!.HistogramOptions);
    }

    [Fact]
    public void HistogramChoicesAreObservable()
    {
        var setup = Setup(GraphType.Histogram);
        var changed = new List<string?>();
        setup.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        setup.SelectedYScale = setup.YScaleChoices[1];
        Choose(setup, HistogramBinningMode.Count);
        setup.BinCountText = "5";
        setup.BinWidthText = "1";
        setup.BinStartText = "0";

        Assert.Contains(nameof(GraphSetupViewModel.SelectedYScale), changed);
        Assert.Contains(nameof(GraphSetupViewModel.SelectedBinning), changed);
        Assert.Contains(nameof(GraphSetupViewModel.IsBinCountEnabled), changed);
        Assert.Contains(nameof(GraphSetupViewModel.IsBinWidthAndStartEnabled), changed);
        Assert.Contains(nameof(GraphSetupViewModel.BinCountText), changed);
        Assert.Contains(nameof(GraphSetupViewModel.BinWidthText), changed);
        Assert.Contains(nameof(GraphSetupViewModel.BinStartText), changed);
    }

    // ---- Show fitted line (#037) ----

    [Fact]
    public void AProbabilityPlotOffersTheFittedLineCheckedByDefault()
    {
        var setup = Setup(GraphType.ProbabilityPlot);

        Assert.True(setup.SupportsFittedLine);
        Assert.True(setup.ShowFittedLine);
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.EmpiricalCdf)]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.BoxPlot)]
    public void OtherGraphsDoNotOfferTheFittedLine(GraphType graphType) =>
        Assert.False(Setup(graphType).SupportsFittedLine);

    [Fact]
    public void TheConfigurationCarriesWhetherTheFittedLineIsShown()
    {
        var setup = Setup(GraphType.ProbabilityPlot);
        Assign(setup, GraphVariableRole.Variable, Reg1);

        Assert.True(setup.Confirm()!.ProbabilityPlotOptions.ShowFittedLine);

        setup.ShowFittedLine = false;
        var off = setup.Confirm()!;
        Assert.False(off.ProbabilityPlotOptions.ShowFittedLine);
        Assert.Equal([new GraphColumnAssignment(GraphVariableRole.Variable, Reg1.Id)], off.Assignments);
    }

    [Theory]
    [InlineData(true, true, "", "")]
    [InlineData(false, false, "14.5", "15.5")]
    [InlineData(true, false, "14.5", "15.5")]
    public void StatisticsFittedLineAndSpecificationAreIndependent(bool statistics, bool fittedLine, string lower, string upper)
    {
        var setup = Setup(GraphType.ProbabilityPlot);
        Assign(setup, GraphVariableRole.Variable, Reg1);
        setup.ShowStatistics = statistics;
        setup.ShowFittedLine = fittedLine;
        setup.LowerLimitText = lower;
        setup.UpperLimitText = upper;

        var configuration = setup.Confirm()!;

        Assert.Equal(statistics, configuration.PresentationOptions.ShowStatistics);
        Assert.Equal(fittedLine, configuration.ProbabilityPlotOptions.ShowFittedLine);
        Assert.Equal(lower.Length == 0 ? Specification.None : new Specification(14.5, null, 15.5), configuration.Specification);
    }

    [Fact]
    public void TheFittedLineNeverStopsASetupFromBeingConfirmed()
    {
        var setup = Setup(GraphType.ProbabilityPlot);
        Assign(setup, GraphVariableRole.Variable, Reg1);

        setup.ShowFittedLine = false;
        Assert.True(setup.CanConfirm);
        setup.ShowFittedLine = true;
        Assert.True(setup.CanConfirm);
    }

    [Fact]
    public void ANewSetupStartsWithTheFittedLineWhateverTheLastOneHad()
    {
        var first = Setup(GraphType.ProbabilityPlot);
        first.ShowFittedLine = false;

        Assert.True(Setup(GraphType.ProbabilityPlot).ShowFittedLine);
    }

    [Fact]
    public void ShowFittedLineIsObservable()
    {
        var setup = Setup(GraphType.ProbabilityPlot);
        var changed = new List<string?>();
        setup.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        setup.ShowFittedLine = false;

        Assert.Contains(nameof(GraphSetupViewModel.ShowFittedLine), changed);
    }

    [Fact]
    public void SpecificationFieldsAreObservable()
    {
        var setup = Setup(GraphType.Histogram);
        var changed = new List<string?>();
        setup.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        setup.LowerLimitText = "1";
        setup.TargetText = "2";
        setup.UpperLimitText = "3";

        Assert.Contains(nameof(GraphSetupViewModel.LowerLimitText), changed);
        Assert.Contains(nameof(GraphSetupViewModel.TargetText), changed);
        Assert.Contains(nameof(GraphSetupViewModel.UpperLimitText), changed);
    }
}
