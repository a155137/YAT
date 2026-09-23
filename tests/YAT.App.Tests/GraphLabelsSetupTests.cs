using YAT.Application.Graphs;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The labels in the graph setup (Task #040): three fixed rows - graph title, X-axis title, Y-axis title - each Auto,
// Custom or Hidden, offered by every graph type, starting on Auto with nothing typed. The text is editable and read only
// for Custom, kept when the mode changes, and must have something in it once normalized.
public class GraphLabelsSetupTests
{
    private static readonly Worksheet Worksheet = new() { Id = Guid.NewGuid(), Name = "Sheet1" };
    private static readonly WorksheetColumn Lot = Column("Lot", WorksheetDataType.String, 0);
    private static readonly WorksheetColumn Reg1 = Column("Reg1", WorksheetDataType.Numeric, 1);
    private static readonly WorksheetColumn Reg2 = Column("Reg2", WorksheetDataType.Numeric, 2);
    private static readonly IReadOnlyList<WorksheetColumn> Columns = [Lot, Reg1, Reg2];

    private static WorksheetColumn Column(string name, WorksheetDataType dataType, int index) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = Worksheet.Id,
        Index = index,
        Name = name,
        DataType = dataType
    };

    public static TheoryData<GraphType> GraphTypes => [.. Enum.GetValues<GraphType>()];

    private static GraphSetupViewModel Setup(GraphType graphType, bool assigned = true)
    {
        var setup = new GraphSetupViewModel(GraphTypeDefinitions.For(graphType), Worksheet, Columns);
        if (!assigned)
        {
            return setup;
        }

        if (graphType == GraphType.ScatterPlot)
        {
            Assign(setup, GraphVariableRole.X, Reg1);
            Assign(setup, GraphVariableRole.Y, Reg2);
        }
        else if (graphType == GraphType.BoxPlot)
        {
            var role = Role(setup, GraphVariableRole.Variable);
            role.SelectedOptions.Add(Assert.Single(role.Options, option => option.WorksheetColumnId == Reg1.Id));
        }
        else
        {
            Assign(setup, GraphVariableRole.Variable, Reg1);
        }

        return setup;
    }

    private static GraphRoleViewModel Role(GraphSetupViewModel setup, GraphVariableRole role) =>
        Assert.Single(setup.Roles, candidate => candidate.Role == role);

    private static void Assign(GraphSetupViewModel setup, GraphVariableRole role, WorksheetColumn column)
    {
        var roleViewModel = Role(setup, role);
        roleViewModel.SelectedOption = Assert.Single(roleViewModel.Options, option => option.WorksheetColumnId == column.Id);
    }

    private static SetupChoice<GraphLabelMode> Mode(GraphSetupViewModel setup, GraphLabelMode mode) =>
        setup.LabelModeChoices.Single(choice => choice.Value == mode);

    // ---- What is offered ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void EveryGraphOffersTheLabelsOnAutoWithNothingTyped(GraphType graphType)
    {
        var setup = Setup(graphType, assigned: false);

        Assert.True(setup.SupportsLabels);
        Assert.Equal(["Auto", "Custom", "Hidden"], setup.LabelModeChoices.Select(choice => choice.Name));
        Assert.Equal([GraphLabelMode.Auto, GraphLabelMode.Custom, GraphLabelMode.Hidden], setup.LabelModeChoices.Select(choice => choice.Value));
        Assert.Equal(GraphLabelMode.Auto, setup.SelectedGraphTitleMode.Value);
        Assert.Equal(GraphLabelMode.Auto, setup.SelectedXAxisTitleMode.Value);
        Assert.Equal(GraphLabelMode.Auto, setup.SelectedYAxisTitleMode.Value);
        Assert.Equal(string.Empty, setup.GraphTitleText);
        Assert.Equal(string.Empty, setup.XAxisTitleText);
        Assert.Equal(string.Empty, setup.YAxisTitleText);
        Assert.False(setup.IsGraphTitleTextEnabled);
        Assert.False(setup.IsXAxisTitleTextEnabled);
        Assert.False(setup.IsYAxisTitleTextEnabled);
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void ByDefaultEveryGraphIsConfiguredWithTheDefaultLabels(GraphType graphType)
    {
        var configuration = Setup(graphType).Confirm();

        Assert.NotNull(configuration);
        Assert.Equal(GraphLabelOptions.Default, configuration.LabelOptions);
    }

    // ---- What reaches the configuration ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void TheChosenLabelsReachTheConfiguration(GraphType graphType)
    {
        var setup = Setup(graphType);
        setup.SelectedGraphTitleMode = Mode(setup, GraphLabelMode.Custom);
        setup.GraphTitleText = "  Wafer thickness ";
        setup.SelectedXAxisTitleMode = Mode(setup, GraphLabelMode.Hidden);
        setup.SelectedYAxisTitleMode = Mode(setup, GraphLabelMode.Custom);
        setup.YAxisTitleText = "Wafers";

        Assert.True(setup.CanConfirm);
        Assert.Equal(
            new GraphLabelOptions(GraphLabelOption.Custom("  Wafer thickness "), GraphLabelOption.Hidden, GraphLabelOption.Custom("Wafers")),
            setup.Confirm()!.LabelOptions);
    }

    [Fact]
    public void OnlyCustomEnablesAndReadsTheText()
    {
        var setup = Setup(GraphType.Histogram);
        setup.GraphTitleText = "typed";
        setup.XAxisTitleText = "   ";
        setup.YAxisTitleText = "typed";

        // Auto and Hidden: nothing typed matters, not even blank text.
        Assert.True(setup.CanConfirm);
        Assert.Equal(GraphLabelOptions.Default, setup.Confirm()!.LabelOptions);

        setup.SelectedXAxisTitleMode = Mode(setup, GraphLabelMode.Hidden);
        Assert.False(setup.IsXAxisTitleTextEnabled);
        Assert.Equal(GraphLabelOption.Hidden, setup.Confirm()!.LabelOptions.XAxisTitle);

        setup.SelectedGraphTitleMode = Mode(setup, GraphLabelMode.Custom);
        Assert.True(setup.IsGraphTitleTextEnabled);
        Assert.False(setup.IsXAxisTitleTextEnabled);
        Assert.False(setup.IsYAxisTitleTextEnabled);
        Assert.Equal(GraphLabelOption.Custom("typed"), setup.Confirm()!.LabelOptions.Title);
    }

    [Fact]
    public void SwitchingTheModeKeepsWhatWasTyped()
    {
        var setup = Setup(GraphType.ScatterPlot);
        setup.SelectedYAxisTitleMode = Mode(setup, GraphLabelMode.Custom);
        setup.YAxisTitleText = "Resistance";

        setup.SelectedYAxisTitleMode = Mode(setup, GraphLabelMode.Hidden);
        Assert.Equal("Resistance", setup.YAxisTitleText);
        Assert.Equal(GraphLabelOption.Hidden, setup.Confirm()!.LabelOptions.YAxisTitle);

        setup.SelectedYAxisTitleMode = Mode(setup, GraphLabelMode.Auto);
        Assert.Equal("Resistance", setup.YAxisTitleText);
        Assert.Equal(GraphLabelOptions.Default, setup.Confirm()!.LabelOptions);

        setup.SelectedYAxisTitleMode = Mode(setup, GraphLabelMode.Custom);
        Assert.Equal(GraphLabelOption.Custom("Resistance"), setup.Confirm()!.LabelOptions.YAxisTitle);
    }

    // ---- Validation ----

    [Theory]
    [InlineData(GraphLabelField.Title, "", "Enter a graph title, or choose Auto or Hidden.")]
    [InlineData(GraphLabelField.XAxisTitle, "   ", "Enter an X-axis title, or choose Auto or Hidden.")]
    [InlineData(GraphLabelField.YAxisTitle, "\r\n\t", "Enter a Y-axis title, or choose Auto or Hidden.")]
    public void ACustomLabelWithoutTextCannotBeConfirmedAndSaysWhy(GraphLabelField field, string typed, string message)
    {
        var setup = Setup(GraphType.Histogram);
        switch (field)
        {
            case GraphLabelField.Title:
                setup.SelectedGraphTitleMode = Mode(setup, GraphLabelMode.Custom);
                setup.GraphTitleText = typed;
                break;
            case GraphLabelField.XAxisTitle:
                setup.SelectedXAxisTitleMode = Mode(setup, GraphLabelMode.Custom);
                setup.XAxisTitleText = typed;
                break;
            default:
                setup.SelectedYAxisTitleMode = Mode(setup, GraphLabelMode.Custom);
                setup.YAxisTitleText = typed;
                break;
        }

        Assert.False(setup.CanConfirm);
        Assert.Null(setup.Confirm());
        Assert.Equal(message, setup.ValidationMessage);
    }

    [Fact]
    public void CanConfirmFollowsTheLabelTextAsItIsTyped()
    {
        var setup = Setup(GraphType.BoxPlot);
        Assert.True(setup.CanConfirm);

        setup.SelectedYAxisTitleMode = Mode(setup, GraphLabelMode.Custom);
        Assert.False(setup.CanConfirm);

        setup.YAxisTitleText = " ";
        Assert.False(setup.CanConfirm);

        setup.YAxisTitleText = "Thickness (um)";
        Assert.True(setup.CanConfirm);

        setup.YAxisTitleText = string.Empty;
        Assert.False(setup.CanConfirm);

        setup.SelectedYAxisTitleMode = Mode(setup, GraphLabelMode.Auto);
        Assert.True(setup.CanConfirm);
    }

    // The dialog shows the labels last, so their problems are reported after everything above them.
    [Fact]
    public void LabelProblemsAreReportedAfterTheOthers()
    {
        var setup = Setup(GraphType.Histogram, assigned: false);
        setup.SelectedGraphTitleMode = Mode(setup, GraphLabelMode.Custom);
        setup.LowerLimitText = "abc";

        Assert.Null(setup.Confirm());
        Assert.Equal("Please select a variable.", setup.ValidationMessage);

        Assign(setup, GraphVariableRole.Variable, Reg1);
        Assert.Null(setup.Confirm());
        Assert.Equal("LSL must be a number.", setup.ValidationMessage);

        setup.LowerLimitText = "20";
        setup.UpperLimitText = "10";
        Assert.Null(setup.Confirm());
        Assert.Equal("LSL must be below USL.", setup.ValidationMessage);

        setup.UpperLimitText = "30";
        Assert.Null(setup.Confirm());
        Assert.Equal("Enter a graph title, or choose Auto or Hidden.", setup.ValidationMessage);

        setup.GraphTitleText = "Wafer thickness";
        Assert.NotNull(setup.Confirm());
        Assert.Null(setup.ValidationMessage);
    }

    [Fact]
    public void LabelsAreIndependentOfTheOtherOptions()
    {
        var setup = Setup(GraphType.Histogram);
        setup.ShowStatistics = false;
        setup.SelectedYScale = setup.YScaleChoices.Single(choice => choice.Value == HistogramYScale.Percent);
        setup.LowerLimitText = "14.8";
        setup.SelectedXAxisTitleMode = Mode(setup, GraphLabelMode.Custom);
        setup.XAxisTitleText = "Thickness";

        var configuration = setup.Confirm()!;

        Assert.False(configuration.PresentationOptions.ShowStatistics);
        Assert.Equal(HistogramYScale.Percent, configuration.HistogramOptions.YScale);
        Assert.Equal(14.8, configuration.Specification.LowerLimit);
        Assert.Equal(GraphLabelOption.Custom("Thickness"), configuration.LabelOptions.XAxisTitle);
    }

    [Fact]
    public void ANewSetupStartsOnAutoWhateverTheLastOneHad()
    {
        var first = Setup(GraphType.Histogram);
        first.SelectedGraphTitleMode = Mode(first, GraphLabelMode.Hidden);
        first.XAxisTitleText = "typed";
        _ = first.Confirm();

        var second = Setup(GraphType.Histogram);

        Assert.Equal(GraphLabelMode.Auto, second.SelectedGraphTitleMode.Value);
        Assert.Equal(string.Empty, second.XAxisTitleText);
        Assert.Equal(GraphLabelOptions.Default, second.Confirm()!.LabelOptions);
    }

    [Fact]
    public void LabelChoicesAreObservable()
    {
        var setup = Setup(GraphType.EmpiricalCdf);
        var changed = new List<string?>();
        setup.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        setup.SelectedGraphTitleMode = Mode(setup, GraphLabelMode.Custom);
        setup.SelectedXAxisTitleMode = Mode(setup, GraphLabelMode.Custom);
        setup.SelectedYAxisTitleMode = Mode(setup, GraphLabelMode.Custom);
        setup.GraphTitleText = "T";
        setup.XAxisTitleText = "X";
        setup.YAxisTitleText = "Y";

        Assert.Contains(nameof(GraphSetupViewModel.SelectedGraphTitleMode), changed);
        Assert.Contains(nameof(GraphSetupViewModel.SelectedXAxisTitleMode), changed);
        Assert.Contains(nameof(GraphSetupViewModel.SelectedYAxisTitleMode), changed);
        Assert.Contains(nameof(GraphSetupViewModel.IsGraphTitleTextEnabled), changed);
        Assert.Contains(nameof(GraphSetupViewModel.IsXAxisTitleTextEnabled), changed);
        Assert.Contains(nameof(GraphSetupViewModel.IsYAxisTitleTextEnabled), changed);
        Assert.Contains(nameof(GraphSetupViewModel.GraphTitleText), changed);
        Assert.Contains(nameof(GraphSetupViewModel.XAxisTitleText), changed);
        Assert.Contains(nameof(GraphSetupViewModel.YAxisTitleText), changed);
        Assert.Contains(nameof(GraphSetupViewModel.CanConfirm), changed);
    }

    // ---- Messages ----

    [Theory]
    [InlineData(GraphLabelField.Title, "Please choose Auto, Custom or Hidden for the graph title.")]
    [InlineData(GraphLabelField.XAxisTitle, "Please choose Auto, Custom or Hidden for the X-axis title.")]
    [InlineData(GraphLabelField.YAxisTitle, "Please choose Auto, Custom or Hidden for the Y-axis title.")]
    public void AnUnknownModeIsExplainedForItsField(GraphLabelField field, string message) =>
        Assert.Equal(message, GraphValidationMessages.For(
            new GraphValidationError(GraphValidationReason.LabelModeInvalid, LabelField: field),
            GraphTypeDefinitions.For(GraphType.Histogram)));
}
