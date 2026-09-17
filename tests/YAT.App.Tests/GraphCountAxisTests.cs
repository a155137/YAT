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
}
