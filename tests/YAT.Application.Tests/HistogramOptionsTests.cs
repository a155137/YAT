using YAT.Application.Graphs;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// The histogram's own options (Task #039): frequency over automatic bins unless the user chooses otherwise, and rules
// that only look at the values the chosen binning mode uses.
public class HistogramOptionsTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly WorksheetColumn Reg1 = new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = WorksheetId,
        Index = 0,
        Name = "Reg1",
        DataType = WorksheetDataType.Numeric
    };

    private static GraphConfiguration Configuration(GraphType graphType, HistogramOptions options) =>
        new(graphType, WorksheetId, [new GraphColumnAssignment(GraphVariableRole.Variable, Reg1.Id)]) { HistogramOptions = options };

    // ---- Model ----

    [Fact]
    public void TheDefaultIsFrequencyOverAutomaticBins()
    {
        Assert.Equal(HistogramYScale.Frequency, HistogramOptions.Default.YScale);
        Assert.Equal(HistogramBinningMode.Auto, HistogramOptions.Default.BinningMode);
        Assert.Null(HistogramOptions.Default.BinCount);
        Assert.Null(HistogramOptions.Default.BinWidth);
        Assert.Null(HistogramOptions.Default.BinStart);
        Assert.False(HistogramOptions.Default.ShowNormalFit);
        Assert.Equal(HistogramOptions.Default, new HistogramOptions());
    }

    // #042: a normal fit is a choice with no invalid value, on any scale and bins.
    [Theory]
    [InlineData(HistogramYScale.Frequency, HistogramBinningMode.Auto)]
    [InlineData(HistogramYScale.Percent, HistogramBinningMode.Count)]
    [InlineData(HistogramYScale.Density, HistogramBinningMode.WidthAndStart)]
    public void ANormalFitIsValidWithAnyScaleAndBins(HistogramYScale scale, HistogramBinningMode mode)
    {
        var options = new HistogramOptions(scale, mode, BinCount: 10, BinWidth: 0.5, BinStart: 0, ShowNormalFit: true);

        Assert.Empty(HistogramOptionsRules.Check(options));
        Assert.NotEqual(options with { ShowNormalFit = false }, options);
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ScatterPlot)]
    public void EveryConfigurationStartsWithTheDefault(GraphType graphType) =>
        Assert.Same(HistogramOptions.Default, new GraphConfiguration(graphType, WorksheetId, []).HistogramOptions);

    [Fact]
    public void TheBinLimitsAreOneToTwoHundred()
    {
        Assert.Equal(1, HistogramOptions.MinimumBinCount);
        Assert.Equal(200, HistogramOptions.MaximumBinCount);
    }

    // ---- Rules ----

    public static TheoryData<HistogramOptions> ValidOptions => new()
    {
        HistogramOptions.Default,
        new HistogramOptions(HistogramYScale.Percent),
        new HistogramOptions(HistogramYScale.Density),
        new HistogramOptions(BinningMode: HistogramBinningMode.Count, BinCount: 1),
        new HistogramOptions(BinningMode: HistogramBinningMode.Count, BinCount: 200),
        new HistogramOptions(BinningMode: HistogramBinningMode.WidthAndStart, BinWidth: 100, BinStart: 14000),
        new HistogramOptions(BinningMode: HistogramBinningMode.WidthAndStart, BinWidth: 1e-9, BinStart: -3.5),
        // Values of other modes are not read.
        new HistogramOptions(BinningMode: HistogramBinningMode.Auto, BinCount: -5, BinWidth: double.NaN),
        new HistogramOptions(BinningMode: HistogramBinningMode.Count, BinCount: 30, BinWidth: -1, BinStart: double.PositiveInfinity),
        new HistogramOptions(BinningMode: HistogramBinningMode.WidthAndStart, BinCount: 0, BinWidth: 0.5, BinStart: 0)
    };

    [Theory]
    [MemberData(nameof(ValidOptions))]
    public void ValidOptionsHaveNoProblems(HistogramOptions options)
    {
        Assert.Empty(HistogramOptionsRules.Check(options));
        Assert.True(HistogramOptionsRules.IsValid(options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public void ACountOutsideOneToTwoHundredIsInvalid(int? count) =>
        Assert.Equal(
            [HistogramOptionsProblem.BinCountInvalid],
            HistogramOptionsRules.Check(new HistogramOptions(BinningMode: HistogramBinningMode.Count, BinCount: count)));

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AWidthThatIsNotPositiveAndFiniteIsInvalid(double? width) =>
        Assert.Equal(
            [HistogramOptionsProblem.BinWidthInvalid],
            HistogramOptionsRules.Check(new HistogramOptions(BinningMode: HistogramBinningMode.WidthAndStart, BinWidth: width, BinStart: 0)));

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void AStartThatIsNotFiniteIsInvalid(double? start) =>
        Assert.Equal(
            [HistogramOptionsProblem.BinStartInvalid],
            HistogramOptionsRules.Check(new HistogramOptions(BinningMode: HistogramBinningMode.WidthAndStart, BinWidth: 1, BinStart: start)));

    [Fact]
    public void BothMissingWidthAndStartAreReported() =>
        Assert.Equal(
            [HistogramOptionsProblem.BinWidthInvalid, HistogramOptionsProblem.BinStartInvalid],
            HistogramOptionsRules.Check(new HistogramOptions(BinningMode: HistogramBinningMode.WidthAndStart)));

    [Fact]
    public void UndefinedChoicesAreInvalid()
    {
        Assert.Equal([HistogramOptionsProblem.UnknownChoice], HistogramOptionsRules.Check(new HistogramOptions((HistogramYScale)99)));
        Assert.Equal([HistogramOptionsProblem.UnknownChoice], HistogramOptionsRules.Check(new HistogramOptions(BinningMode: (HistogramBinningMode)99)));
    }

    // ---- Validation of a configuration ----

    [Theory]
    [InlineData(HistogramBinningMode.Count, GraphValidationReason.HistogramBinCountInvalid)]
    [InlineData(HistogramBinningMode.WidthAndStart, GraphValidationReason.HistogramBinWidthInvalid)]
    public void AHistogramWithInvalidOptionsCannotBeConfigured(HistogramBinningMode mode, GraphValidationReason reason)
    {
        var result = new GraphConfigurationValidator().Validate(
            Configuration(GraphType.Histogram, new HistogramOptions(BinningMode: mode, BinStart: 0)), [Reg1]);

        Assert.Equal(reason, Assert.Single(result.Errors).Reason);
    }

    [Fact]
    public void AnUndefinedChoiceIsReportedForAHistogram()
    {
        var result = new GraphConfigurationValidator().Validate(Configuration(GraphType.Histogram, new HistogramOptions((HistogramYScale)7)), [Reg1]);

        Assert.Equal(GraphValidationReason.HistogramOptionsInvalid, Assert.Single(result.Errors).Reason);
    }

    [Theory]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    [InlineData(GraphType.BoxPlot)]
    public void OtherGraphsIgnoreHistogramOptions(GraphType graphType)
    {
        var options = new HistogramOptions(BinningMode: HistogramBinningMode.Count, BinCount: 0);

        Assert.True(new GraphConfigurationValidator().Validate(Configuration(graphType, options), [Reg1]).IsValid);
    }
}
