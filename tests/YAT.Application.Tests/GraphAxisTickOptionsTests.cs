using YAT.Application.Graphs;

namespace YAT.Application.Tests;

// The ticks a user chooses for an axis (Task #054): Auto, a fixed interval or custom values - custom values kept
// ascending and without repeats, compared by value - and the rules they obey before any range is known: an interval
// above zero, at least one and at most a hundred values, every value one the axis can show (decimals included on a
// count axis).
public class GraphAxisTickOptionsTests
{
    private static GraphAxisTickOption.CustomValues Values(params double[] values) => new(values);

    private static IReadOnlyList<GraphAxisTickProblem> Check(GraphAxisTickOption option, GraphAxisKind kind = GraphAxisKind.Numeric) =>
        GraphAxisTickRules.Check(GraphAxisField.X, kind, option);

    // ---- The model ----

    [Fact]
    public void AutoIsTheDefaultOfBothAxes()
    {
        Assert.True(GraphAxisTickOption.Auto.IsAuto);
        Assert.True(GraphAxisTickOptions.Default.IsAuto);
        Assert.Same(GraphAxisTickOption.Auto, GraphAxisTickOptions.Default.X);
        Assert.Same(GraphAxisTickOption.Auto, GraphAxisTickOptions.Default.Y);
        Assert.False(new GraphAxisTickOption.FixedInterval(0.02).IsAuto);
        Assert.False(Values(1).IsAuto);
    }

    [Fact]
    public void CustomValuesAreSortedAndRepeatsRemoved()
    {
        var values = Values(15.1, 14.9, 15.0, 14.95, 15.0, 14.9);

        Assert.Equal([14.9, 14.95, 15.0, 15.1], values.Values);
    }

    [Fact]
    public void MinusZeroAndZeroAreOneValue()
    {
        var values = Values(-0d, 0d, 1);

        Assert.Equal([0d, 1d], values.Values);
        Assert.False(double.IsNegative(values.Values[0]));
    }

    [Fact]
    public void CustomValuesAreEqualByTheirValuesWhateverOrderTheyWereGivenIn()
    {
        Assert.Equal(Values(3, 1, 2), Values(1, 2, 3, 2));
        Assert.Equal(Values(3, 1, 2).GetHashCode(), Values(1, 2, 3).GetHashCode());
        Assert.NotEqual(Values(1, 2), Values(1, 2, 3));
        Assert.NotEqual<GraphAxisTickOption>(Values(1), new GraphAxisTickOption.FixedInterval(1));
        Assert.Equal(new GraphAxisTickOption.FixedInterval(0.5), new GraphAxisTickOption.FixedInterval(0.5));
    }

    [Fact]
    public void OneAxisIsReplacedAndTheOtherKept()
    {
        var interval = new GraphAxisTickOption.FixedInterval(2);
        var options = GraphAxisTickOptions.Default.With(GraphAxisField.Y, interval);

        Assert.Same(GraphAxisTickOption.Auto, options.X);
        Assert.Same(interval, options.Y);
        Assert.Same(interval, options.For(GraphAxisField.Y));
        Assert.False(options.IsAuto);
        Assert.Same(interval, options.With(GraphAxisField.X, Values(1)).Y);
    }

    // ---- The rules ----

    [Fact]
    public void AutoAndUsableTicksHaveNoProblems()
    {
        Assert.Empty(Check(GraphAxisTickOption.Auto));
        Assert.Empty(Check(new GraphAxisTickOption.FixedInterval(0.02)));
        Assert.Empty(Check(Values(14.9, 14.95, 15, 15.1)));
        Assert.Empty(Check(Values(-5, 0, 1e300)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnIntervalMustBeAFiniteNumberAboveZero(double interval)
    {
        var problem = Assert.Single(Check(new GraphAxisTickOption.FixedInterval(interval)));

        Assert.Equal(GraphAxisTickProblemKind.IntervalNotPositive, problem.Kind);
        Assert.Equal(GraphAxisField.X, problem.Axis);
    }

    [Fact]
    public void ValuesMustBeGivenAndAtMostAHundred()
    {
        Assert.Equal(GraphAxisTickProblemKind.NoValues, Assert.Single(Check(Values())).Kind);
        Assert.Empty(Check(Values([.. Enumerable.Range(0, GraphAxisTickRules.MaximumValues).Select(i => (double)i)])));
        Assert.Equal(
            GraphAxisTickProblemKind.TooManyValues,
            Assert.Single(Check(Values([.. Enumerable.Range(0, GraphAxisTickRules.MaximumValues + 1).Select(i => (double)i)]))).Kind);

        // A hundred and one values of which two repeat are a hundred.
        Assert.Empty(Check(Values([.. Enumerable.Range(0, GraphAxisTickRules.MaximumValues).Select(i => (double)i), 7])));
    }

    [Fact]
    public void EveryValueMustBeFinite()
    {
        var problem = Assert.Single(Check(Values(1, double.PositiveInfinity)));

        Assert.Equal((GraphAxisTickProblemKind.ValueNotFinite, (double?)double.PositiveInfinity), (problem.Kind, problem.Value));
    }

    [Theory]
    [InlineData(GraphAxisKind.NonNegative, -1)]
    [InlineData(GraphAxisKind.Percent, -0.5)]
    [InlineData(GraphAxisKind.Percent, 100.5)]
    [InlineData(GraphAxisKind.ProbabilityPercent, 0)]
    [InlineData(GraphAxisKind.ProbabilityPercent, 100)]
    [InlineData(GraphAxisKind.ProbabilityPercent, 0.00001)]
    public void EveryValueMustBeOneTheAxisCanShow(GraphAxisKind kind, double value)
    {
        var problem = Assert.Single(Check(Values(value, 50), kind));

        Assert.Equal((GraphAxisTickProblemKind.ValueOutsideAxis, (double?)value), (problem.Kind, problem.Value));
    }

    [Theory]
    [InlineData(GraphAxisKind.NonNegative, 0)]
    [InlineData(GraphAxisKind.Percent, 0)]
    [InlineData(GraphAxisKind.Percent, 100)]
    [InlineData(GraphAxisKind.ProbabilityPercent, GraphAxisRangeRules.MinimumProbabilityPercent)]
    [InlineData(GraphAxisKind.ProbabilityPercent, GraphAxisRangeRules.MaximumProbabilityPercent)]
    public void TheEndsOfWhatAnAxisCanShowAreValues(GraphAxisKind kind, double value) =>
        Assert.Empty(Check(Values(value), kind));

    [Fact]
    public void AnIntervalIsInTheUnitsTheAxisIsTypedIn()
    {
        // 10 on a probability axis is 10 %, 20 %, ...: the interval itself is not a percentage the axis must show.
        Assert.Empty(Check(new GraphAxisTickOption.FixedInterval(10), GraphAxisKind.ProbabilityPercent));
        Assert.Empty(Check(new GraphAxisTickOption.FixedInterval(250), GraphAxisKind.Percent));
    }

    [Fact]
    public void AHistogramsAxisTakesDecimalTicksAtOrAboveZero()
    {
        // A count axis's Auto ticks are whole counts; ticks a user chooses for it - or for a percent or density axis - may
        // lie between them, but never below zero.
        Assert.Empty(Check(new GraphAxisTickOption.FixedInterval(2.5), GraphAxisKind.NonNegative));
        Assert.Empty(Check(new GraphAxisTickOption.FixedInterval(5), GraphAxisKind.NonNegative));
        Assert.Empty(Check(Values(0, 2.5, 7.5, 12), GraphAxisKind.NonNegative));

        var value = Assert.Single(Check(Values(-1.5, 2.5), GraphAxisKind.NonNegative));
        Assert.Equal((GraphAxisTickProblemKind.ValueOutsideAxis, (double?)-1.5), (value.Kind, value.Value));
        Assert.Equal(
            GraphAxisTickProblemKind.IntervalNotPositive,
            Assert.Single(Check(new GraphAxisTickOption.FixedInterval(0), GraphAxisKind.NonNegative)).Kind);
        Assert.Equal(
            GraphAxisTickProblemKind.IntervalNotPositive,
            Assert.Single(Check(new GraphAxisTickOption.FixedInterval(-2.5), GraphAxisKind.NonNegative)).Kind);
    }

    [Fact]
    public void TheProblemNamesItsAxis() =>
        Assert.Equal(
            GraphAxisField.Y,
            Assert.Single(GraphAxisTickRules.Check(GraphAxisField.Y, GraphAxisKind.Numeric, Values())).Axis);
}
