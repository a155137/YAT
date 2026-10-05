using YAT.Application.Graphs;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Task #048: a graph setup says why it cannot be confirmed while it is being edited - OK is unavailable then, so this is
// the only place the user learns why. The reason is the one Confirm gives, from the same rules in the same words;
// once the setup is valid it says nothing, and Confirm still checks once more.
public class GraphSetupLiveValidationTests
{
    private static readonly Worksheet Worksheet = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

    private static readonly IReadOnlyList<WorksheetColumn> Columns =
    [
        Column("Lot", WorksheetDataType.String, 0),
        .. Enumerable.Range(1, 55).Select(index => Column($"V{index}", WorksheetDataType.Numeric, index))
    ];

    private static WorksheetColumn Column(string name, WorksheetDataType dataType, int index) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = Worksheet.Id,
        Index = index,
        Name = name,
        DataType = dataType
    };

    private static GraphSetupViewModel Setup(GraphType graphType) => new(GraphTypeDefinitions.For(graphType), Worksheet, Columns);

    private static GraphRoleViewModel Variables(GraphSetupViewModel setup) =>
        Assert.Single(setup.Roles, role => role.Role == GraphVariableRole.Variable);

    private static void Pick(GraphSetupViewModel setup, int count)
    {
        var variables = Variables(setup);
        foreach (var option in variables.Options.Take(count))
        {
            variables.SelectedOptions.Add(option);
        }
    }

    // The reason shown while editing is the reason a confirm would give, and OK is unavailable.
    private static void AssertSays(GraphSetupViewModel setup, string message)
    {
        Assert.False(setup.CanConfirm);
        Assert.Equal(message, setup.ValidationMessage);

        Assert.Null(setup.Confirm());
        Assert.Equal(message, setup.ValidationMessage);
    }

    private static void AssertValid(GraphSetupViewModel setup)
    {
        Assert.True(setup.CanConfirm);
        Assert.Null(setup.ValidationMessage);
    }

    // As it opens, a setup without its columns already says what it needs.
    [Theory]
    [InlineData(GraphType.Histogram, "Please select a variable.")]
    [InlineData(GraphType.ProbabilityPlot, "Please select a variable.")]
    [InlineData(GraphType.EmpiricalCdf, "Please select a variable.")]
    [InlineData(GraphType.BoxPlot, "Please select a variable.")]
    [InlineData(GraphType.ScatterPlot, "Please select a Numeric column for X-axis.")]
    public void ASetupWithoutItsColumnsSaysWhatItNeeds(GraphType graphType, string message) =>
        AssertSays(Setup(graphType), message);

    [Fact]
    public void PickingAVariableClearsTheReason()
    {
        var setup = Setup(GraphType.Histogram);
        Pick(setup, 1);

        AssertValid(setup);
    }

    [Fact]
    public void MoreThanFiftyVariablesAreRefusedAsTheyArePicked()
    {
        var setup = Setup(GraphType.BoxPlot);
        Pick(setup, 50);
        AssertValid(setup);

        Variables(setup).SelectedOptions.Add(Variables(setup).Options[50]);
        Assert.False(setup.CanConfirm);
        Assert.Equal("Select at most 50 variables.", setup.ValidationMessage);

        Variables(setup).SelectedOptions.RemoveAt(50);
        AssertValid(setup);
    }

    [Theory]
    [InlineData("abc", "", "LSL must be a number.")]
    [InlineData("20", "10", "LSL must be below USL.")]
    public void AnInvalidSpecificationIsSaidAsItIsTyped(string lower, string upper, string message)
    {
        var setup = Setup(GraphType.Histogram);
        Pick(setup, 1);

        setup.LowerLimitText = lower;
        setup.UpperLimitText = upper;
        Assert.False(setup.CanConfirm);
        Assert.Equal(message, setup.ValidationMessage);

        setup.LowerLimitText = string.Empty;
        setup.UpperLimitText = string.Empty;
        AssertValid(setup);

        setup.LowerLimitText = lower;
        setup.UpperLimitText = upper;
        AssertSays(setup, message);
    }

    [Theory]
    [InlineData("16", "15", "X-axis minimum must be below the X-axis maximum.")]
    [InlineData("x", "", "X-axis minimum must be a number.")]
    public void AnInvalidAxisRangeIsSaidAsItIsTyped(string minimum, string maximum, string message)
    {
        var setup = Setup(GraphType.Histogram);
        Pick(setup, 1);

        setup.Axes.XMinimumText = minimum;
        setup.Axes.XMaximumText = maximum;
        AssertSays(setup, message);

        setup.Axes.XMinimumText = string.Empty;
        setup.Axes.XMaximumText = string.Empty;
        AssertValid(setup);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("201")]
    [InlineData("12.5")]
    [InlineData("")]
    public void AnInvalidBinCountIsSaidAsItIsTyped(string count)
    {
        var setup = Setup(GraphType.Histogram);
        Pick(setup, 1);
        setup.SelectedBinning = setup.BinningChoices.Single(choice => choice.Value == HistogramBinningMode.Count);
        setup.BinCountText = count;

        Assert.False(setup.CanConfirm);
        var message = setup.ValidationMessage;
        Assert.False(string.IsNullOrEmpty(message));
        AssertSays(setup, message);

        setup.BinCountText = "30";
        AssertValid(setup);
    }

    // Several problems at once: the first one Confirm would report, in its order.
    [Fact]
    public void WithSeveralProblemsTheFirstIsSaid()
    {
        var setup = Setup(GraphType.Histogram);
        setup.LowerLimitText = "abc";

        AssertSays(setup, "Please select a variable.");

        Pick(setup, 1);
        AssertSays(setup, "LSL must be a number.");
    }
}
