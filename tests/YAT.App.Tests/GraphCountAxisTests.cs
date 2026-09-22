using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// The axis a frequency is read on: whole numbers from zero, and always tall enough for the tallest bar.
public class GraphCountAxisTests
{
    // 1
    [Fact]
    public void TheExampleFromTheHistogramSpecification()
    {
        var axis = GraphAxisTicks.NiceCounts(83);

        Assert.Equal(0, axis.Range.Minimum);
        Assert.Equal(100, axis.Range.Maximum);
        Assert.Equal([0, 20, 40, 60, 80, 100], axis.Ticks.Select(tick => tick.Value));
        Assert.Equal(["0", "20", "40", "60", "80", "100"], axis.Ticks.Select(tick => tick.Label));
    }

    // 2
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(83)]
    [InlineData(1_000)]
    [InlineData(1_234_567)]
    public void EveryAxisStartsAtZeroCoversTheCountAndStepsInWholeNumbers(int maximumCount)
    {
        var axis = GraphAxisTicks.NiceCounts(maximumCount);

        Assert.Equal(0, axis.Range.Minimum);
        Assert.True(axis.Range.Maximum >= maximumCount, $"{axis.Range.Maximum} < {maximumCount}");
        Assert.True(axis.Range.Maximum > 0);
        Assert.Equal(0, axis.Ticks[0].Value);
        Assert.Equal(axis.Range.Maximum, axis.Ticks[^1].Value);

        Assert.All(axis.Ticks, tick => Assert.Equal(Math.Round(tick.Value), tick.Value));

        var step = axis.Ticks[1].Value - axis.Ticks[0].Value;
        Assert.True(step >= 1, $"step {step} is smaller than one count.");
        Assert.All(axis.Ticks.Zip(axis.Ticks.Skip(1)), pair => Assert.Equal(step, pair.Second.Value - pair.First.Value, 1e-9));
    }

    // 3
    [Fact]
    public void ASmallCountIsCountedOneByOne()
    {
        Assert.Equal([0, 1], GraphAxisTicks.NiceCounts(1).Ticks.Select(tick => tick.Value));
        Assert.Equal([0, 1, 2, 3], GraphAxisTicks.NiceCounts(3).Ticks.Select(tick => tick.Value));
    }

    // 4
    [Fact]
    public void AnEmptyHistogramStillHasAnAxis()
    {
        var axis = GraphAxisTicks.NiceCounts(0);

        Assert.True(axis.Range.IsValid);
        Assert.Equal([0, 1], axis.Ticks.Select(tick => tick.Value));
    }

    // 5
    [Fact]
    public void LargeCountsStayReadableAsWholeNumbers()
    {
        var axis = GraphAxisTicks.NiceCounts(1_000_000);

        Assert.Equal(1_000_000, axis.Range.Maximum);
        Assert.Equal("1000000", axis.Ticks[^1].Label);
        Assert.DoesNotContain(axis.Ticks, tick => tick.Label.Contains('E', StringComparison.Ordinal));
    }

    // 6
    [Fact]
    public void TheSameCountAlwaysGivesTheSameAxis()
    {
        Assert.Equal(GraphAxisTicks.NiceCounts(57).Range, GraphAxisTicks.NiceCounts(57).Range);
        Assert.Equal(
            GraphAxisTicks.NiceCounts(57).Ticks.Select(tick => tick.Label),
            GraphAxisTicks.NiceCounts(57).Ticks.Select(tick => tick.Label));
    }

    // 7
    [Fact]
    public void ACountCannotBeNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphAxisTicks.NiceCounts(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphAxisTicks.NiceCounts(10, intervals: 1));
    }

    // ---- NiceFromZero: the axis of a histogram's percent or density (#039) ----

    [Theory]
    [InlineData(23.4, 25, 5)]
    [InlineData(100, 100, 20)]
    [InlineData(0.0034, 0.0035, 0.0005)]
    [InlineData(7.5, 8, 2)]
    public void AZeroBasedAxisReachesTheLargestValueOnNiceSteps(double maximum, double top, double step)
    {
        var axis = GraphAxisTicks.NiceFromZero(maximum);

        Assert.Equal(0, axis.Range.Minimum);
        Assert.Equal(top, axis.Range.Maximum, 1e-12);
        Assert.True(axis.Range.Maximum >= maximum);
        Assert.Equal(0, axis.Ticks[0].Value);
        Assert.Equal(axis.Range.Maximum, axis.Ticks[^1].Value);
        Assert.All(axis.Ticks.Select((tick, index) => (tick, index)), pair => Assert.Equal(pair.index * step, pair.tick.Value, 1e-12));
    }

    [Fact]
    public void AZeroBasedAxisIsLabelledLikeEveryNiceAxis()
    {
        Assert.Equal(["0", "5", "10", "15", "20", "25"], GraphAxisTicks.NiceFromZero(23.4).Ticks.Select(tick => tick.Label));
        Assert.Equal(["0.0000", "0.0005", "0.0010", "0.0015", "0.0020", "0.0025", "0.0030", "0.0035"], GraphAxisTicks.NiceFromZero(0.0034).Ticks.Select(tick => tick.Label));
    }

    [Fact]
    public void AZeroBasedAxisCoversAValueThatDivisionRoundsDown()
    {
        // 0.30000000000000004 over steps of 0.1: the top must not stop at 3 x 0.1 if that is short of the value.
        foreach (var maximum in new[] { 0.1 + 0.2, 0.7 + 0.1, 1.1 * 3, 100d / 3, 1e-300, 1e300 })
        {
            var axis = GraphAxisTicks.NiceFromZero(maximum);
            Assert.True(axis.Range.Maximum >= maximum, $"{maximum:R} is above the axis top {axis.Range.Maximum:R}.");
            Assert.True(axis.Range.IsValid);
        }
    }

    [Fact]
    public void NothingToReachGivesAUnitAxis()
    {
        var axis = GraphAxisTicks.NiceFromZero(0);

        Assert.Equal(0, axis.Range.Minimum);
        Assert.Equal(1, axis.Range.Maximum);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AZeroBasedAxisNeedsAFiniteNonNegativeMaximum(double maximum) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphAxisTicks.NiceFromZero(maximum));
}
