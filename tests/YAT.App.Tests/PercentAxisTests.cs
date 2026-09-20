using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// The plain percentage axis: nought to a hundred, read linearly.
public class PercentAxisTests
{
    // 1
    [Fact]
    public void TheAxisRunsFromNoughtToAHundred()
    {
        var axis = PercentAxis.Axis();

        Assert.Equal(0, axis.Range.Minimum);
        Assert.Equal(100, axis.Range.Maximum);
        Assert.Equal(PercentAxis.Title, axis.Title);
    }

    // 2
    [Fact]
    public void ItIsReadEveryTwentyPercent()
    {
        var axis = PercentAxis.Axis();

        Assert.Equal([0, 20, 40, 60, 80, 100], axis.Ticks.Select(tick => tick.Value));
        Assert.Equal(["0", "20", "40", "60", "80", "100"], axis.Ticks.Select(tick => tick.Label));
    }

    // 3
    [Fact]
    public void EqualSharesTakeEqualRoom()
    {
        // Unlike the probability axis, where the same twenty percent are stretched towards the tails.
        var axis = PercentAxis.Axis();
        var steps = axis.Ticks.Zip(axis.Ticks.Skip(1)).Select(pair => pair.Second.Value - pair.First.Value).ToArray();

        Assert.All(steps, step => Assert.Equal(20, step));
        Assert.NotEqual(
            ProbabilityAxis.Score(70) - ProbabilityAxis.Score(50),
            ProbabilityAxis.Score(90) - ProbabilityAxis.Score(70),
            1e-6);
    }

    // 4
    [Fact]
    public void TheAxisIsTheSameEveryTime()
    {
        Assert.Equal(PercentAxis.Axis().Range, PercentAxis.Axis().Range);
        Assert.Equal(PercentAxis.Axis().Ticks.Select(tick => tick.Label), PercentAxis.Axis().Ticks.Select(tick => tick.Label));
    }
}
