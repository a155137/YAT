using YAT.Application.Graphs;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// Axis ranges (Task #043): what a configuration carries, which axes each graph type lets the user choose and what may be
// typed for them, and the rules a range obeys before any data is seen. Whether a range with one end chosen fits the
// graph's automatic other end is the graph's to say (App tests).
public class GraphAxisRangeOptionsTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly WorksheetColumn Reg1 = Column("Reg1", 0);
    private static readonly WorksheetColumn Reg2 = Column("Reg2", 1);

    private static WorksheetColumn Column(string name, int index) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = WorksheetId,
        Name = name,
        Index = index,
        DataType = WorksheetDataType.Numeric
    };

    private static GraphConfiguration Configuration(GraphType type, GraphAxisRangeOptions ranges) =>
        new(type, WorksheetId, type == GraphType.ScatterPlot
            ? [new(GraphVariableRole.X, Reg1.Id), new(GraphVariableRole.Y, Reg2.Id)]
            : [new(GraphVariableRole.Variable, Reg1.Id)])
        {
            AxisRangeOptions = ranges
        };

    private static IReadOnlyList<GraphValidationError> Errors(GraphType type, GraphAxisRangeOptions ranges) =>
        new GraphConfigurationValidator().Validate(Configuration(type, ranges), [Reg1, Reg2]).Errors;

    private static GraphAxisRangeOptions Y(double? minimum, double? maximum) =>
        new(GraphAxisRangeOption.Auto, new GraphAxisRangeOption(minimum, maximum));

    private static GraphAxisRangeOptions X(double? minimum, double? maximum) =>
        new(new GraphAxisRangeOption(minimum, maximum), GraphAxisRangeOption.Auto);

    [Fact]
    public void EveryConfigurationStartsWithBothAxesAuto()
    {
        var configuration = new GraphConfiguration(GraphType.Histogram, WorksheetId, []);

        Assert.Same(GraphAxisRangeOptions.Default, configuration.AxisRangeOptions);
        Assert.True(GraphAxisRangeOptions.Default.X.IsAuto && GraphAxisRangeOptions.Default.Y.IsAuto);
        Assert.Equal(GraphAxisRangeOptions.Default, new GraphAxisRangeOptions(new GraphAxisRangeOption(), new GraphAxisRangeOption()));
        Assert.False(new GraphAxisRangeOption(Minimum: 1).IsAuto);
        Assert.False(new GraphAxisRangeOption(Maximum: 1).IsAuto);
    }

    // Box plot X is its categories; every numeric axis of every graph type has a range to choose.
    [Theory]
    [InlineData(GraphType.ScatterPlot, GraphAxisKind.Numeric, GraphAxisKind.Numeric)]
    [InlineData(GraphType.Histogram, GraphAxisKind.Numeric, GraphAxisKind.NonNegative)]
    [InlineData(GraphType.ProbabilityPlot, GraphAxisKind.Numeric, GraphAxisKind.ProbabilityPercent)]
    [InlineData(GraphType.EmpiricalCdf, GraphAxisKind.Numeric, GraphAxisKind.Percent)]
    [InlineData(GraphType.BoxPlot, GraphAxisKind.None, GraphAxisKind.Numeric)]
    public void EachGraphTypeDeclaresWhatItsAxesRead(GraphType type, GraphAxisKind x, GraphAxisKind y)
    {
        var definition = GraphTypeDefinitions.For(type);

        Assert.True(definition.Supports(GraphCapability.AxisRange));
        Assert.Equal(x, definition.XAxisKind);
        Assert.Equal(y, definition.YAxisKind);
        Assert.Equal(x != GraphAxisKind.None, definition.SupportsAxisRange(GraphAxisField.X));
        Assert.True(definition.SupportsAxisRange(GraphAxisField.Y));
    }

    [Fact]
    public void AGraphTypeWithoutTheCapabilityHasNoAxisRange()
    {
        var definition = new GraphTypeDefinition(GraphType.Histogram, "Test", GraphTypeDefinitions.For(GraphType.Histogram).Roles)
        {
            XAxisKind = GraphAxisKind.Numeric
        };

        Assert.False(definition.SupportsAxisRange(GraphAxisField.X));
        Assert.Equal(GraphAxisKind.None, definition.YAxisKind);
    }

    // Min only, max only and both are all usable ranges; so is Auto.
    [Theory]
    [InlineData(null, null)]
    [InlineData(10d, null)]
    [InlineData(null, 20d)]
    [InlineData(10d, 20d)]
    [InlineData(-1e300, 1e300)]
    [InlineData(-5d, -4.999999)]
    public void OneEndBothEndsOrNeitherAreValid(double? minimum, double? maximum)
    {
        foreach (var type in Enum.GetValues<GraphType>())
        {
            Assert.DoesNotContain(Errors(type, X(minimum, maximum)), error => error.Axis == GraphAxisField.X);
        }
    }

    [Theory]
    [InlineData(20d, 10d)]
    [InlineData(10d, 10d)]
    public void AMinimumNotBelowTheMaximumIsRefused(double minimum, double maximum)
    {
        var error = Assert.Single(Errors(GraphType.ScatterPlot, X(minimum, maximum)));

        Assert.Equal(new GraphValidationError(GraphValidationReason.AxisRangeNotIncreasing, Axis: GraphAxisField.X), error);
    }

    [Theory]
    [InlineData(double.NaN, GraphAxisBound.Minimum)]
    [InlineData(double.PositiveInfinity, GraphAxisBound.Maximum)]
    [InlineData(double.NegativeInfinity, GraphAxisBound.Minimum)]
    public void AValueThatIsNotFiniteIsRefused(double value, GraphAxisBound bound)
    {
        var ranges = Y(bound == GraphAxisBound.Minimum ? value : null, bound == GraphAxisBound.Maximum ? value : null);

        Assert.Equal(
            new GraphValidationError(GraphValidationReason.AxisRangeValueNotNumeric, Axis: GraphAxisField.Y, Bound: bound),
            Assert.Single(Errors(GraphType.ScatterPlot, ranges)));
    }

    [Theory]
    [InlineData(1e15, 1e15 + 0.25)]
    [InlineData(-1e-300, -1e-300 + 1e-315)]
    public void ARangeTooNarrowToTellItsEndsApartIsRefused(double minimum, double maximum)
    {
        Assert.Equal(
            new GraphValidationError(GraphValidationReason.AxisRangeTooNarrow, Axis: GraphAxisField.X),
            Assert.Single(Errors(GraphType.ScatterPlot, X(minimum, maximum))));
        Assert.False(GraphAxisRangeRules.IsUsable(minimum, maximum));
    }

    [Fact]
    public void AWidthADoubleCannotHoldIsRefused()
    {
        Assert.False(GraphAxisRangeRules.IsUsable(-1.7e308, 1.7e308));
        Assert.Equal(GraphValidationReason.AxisRangeTooNarrow, Assert.Single(Errors(GraphType.ScatterPlot, X(-1.7e308, 1.7e308))).Reason);
        Assert.True(GraphAxisRangeRules.IsUsable(0, 1e-300));
        Assert.True(GraphAxisRangeRules.IsUsable(14.9, 15.1));
    }

    // A histogram's bars measure nothing below zero.
    [Fact]
    public void AHistogramsYCannotGoBelowZero()
    {
        Assert.Equal(
            new GraphValidationError(GraphValidationReason.AxisRangeValueOutsideAxis, Axis: GraphAxisField.Y, Bound: GraphAxisBound.Minimum),
            Assert.Single(Errors(GraphType.Histogram, Y(-1, 50))));
        Assert.Empty(Errors(GraphType.Histogram, Y(0, 50)));
        Assert.Empty(Errors(GraphType.Histogram, Y(null, 0.5)));

        // Its X axis is the measurement: any number.
        Assert.Empty(Errors(GraphType.Histogram, X(-100, -50)));
    }

    // An empirical CDF's percent means nothing outside 0 to 100, at either end, typed alone or together.
    [Theory]
    [InlineData(-0.1, null, GraphAxisBound.Minimum)]
    [InlineData(null, 100.1, GraphAxisBound.Maximum)]
    [InlineData(101d, null, GraphAxisBound.Minimum)]
    [InlineData(null, -5d, GraphAxisBound.Maximum)]
    public void AnEmpiricalCdfsPercentStaysWithinZeroToAHundred(double? minimum, double? maximum, GraphAxisBound bound)
    {
        Assert.Equal(
            new GraphValidationError(GraphValidationReason.AxisRangeValueOutsideAxis, Axis: GraphAxisField.Y, Bound: bound),
            Assert.Single(Errors(GraphType.EmpiricalCdf, Y(minimum, maximum))));
    }

    [Fact]
    public void AnEmpiricalCdfTakesAnyRangeFromZeroToAHundred()
    {
        Assert.Empty(Errors(GraphType.EmpiricalCdf, Y(0, 100)));
        Assert.Empty(Errors(GraphType.EmpiricalCdf, Y(90, null)));
        Assert.Empty(Errors(GraphType.EmpiricalCdf, Y(null, 50)));

        // 100 is a percent the axis shows; a minimum at the automatic maximum is the graph's to refuse.
        Assert.Empty(Errors(GraphType.EmpiricalCdf, Y(100, null)));
    }

    // A probability axis is typed in the percent it shows, within its farthest labelled tails.
    [Theory]
    [InlineData(0.0001, 99.9999, true)]
    [InlineData(1d, 99d, true)]
    [InlineData(0.00005, null, false)]
    [InlineData(null, 99.99995, false)]
    [InlineData(0d, 50d, false)]
    [InlineData(50d, 100d, false)]
    public void AProbabilityAxisIsTypedInPercentWithinItsTails(double? minimum, double? maximum, bool valid)
    {
        var errors = Errors(GraphType.ProbabilityPlot, Y(minimum, maximum));

        Assert.Equal(valid, errors.Count == 0);
        Assert.All(errors, error => Assert.Equal(GraphValidationReason.AxisRangeValueOutsideAxis, error.Reason));
    }

    [Fact]
    public void ABoxPlotsCategoriesHaveNoRangeAndAnythingTypedForThemIsIgnored()
    {
        Assert.Empty(Errors(GraphType.BoxPlot, X(20, 10)));
        Assert.Single(Errors(GraphType.BoxPlot, Y(20, 10)));
        Assert.Empty(GraphAxisRangeRules.Check(X(double.NaN, 1), GraphTypeDefinitions.For(GraphType.BoxPlot)));
    }

    // Every problem is reported, X before Y, each minimum before its maximum.
    [Fact]
    public void EveryProblemIsReportedInAxisAndBoundOrder()
    {
        var ranges = new GraphAxisRangeOptions(new GraphAxisRangeOption(double.NaN, double.PositiveInfinity), new GraphAxisRangeOption(-1, -2));

        Assert.Equal(
        [
            new(GraphAxisField.X, GraphAxisBound.Minimum, GraphAxisRangeProblemKind.NotFinite),
            new(GraphAxisField.X, GraphAxisBound.Maximum, GraphAxisRangeProblemKind.NotFinite),
            new(GraphAxisField.Y, GraphAxisBound.Minimum, GraphAxisRangeProblemKind.OutsideAxis),
            new(GraphAxisField.Y, GraphAxisBound.Maximum, GraphAxisRangeProblemKind.OutsideAxis)
        ],
        GraphAxisRangeRules.Check(ranges, GraphTypeDefinitions.For(GraphType.Histogram)));
        Assert.Equal(4, GraphConfigurationValidator.AxisRangeErrors(ranges, GraphTypeDefinitions.For(GraphType.Histogram)).Count);
    }

    [Theory]
    [InlineData(GraphAxisKind.Numeric, -1e300, true)]
    [InlineData(GraphAxisKind.NonNegative, -0.0001, false)]
    [InlineData(GraphAxisKind.NonNegative, 0d, true)]
    [InlineData(GraphAxisKind.Percent, 100d, true)]
    [InlineData(GraphAxisKind.Percent, 100.0001, false)]
    [InlineData(GraphAxisKind.ProbabilityPercent, 99.9999, true)]
    [InlineData(GraphAxisKind.ProbabilityPercent, 0.00009, false)]
    [InlineData(GraphAxisKind.None, 1d, false)]
    [InlineData(GraphAxisKind.Numeric, double.NaN, false)]
    public void WhatEachAxisKindAllows(GraphAxisKind kind, double value, bool allowed) =>
        Assert.Equal(allowed, GraphAxisRangeRules.Allows(kind, value));
}
