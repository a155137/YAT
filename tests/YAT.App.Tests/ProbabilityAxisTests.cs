using YAT.Analytics.Statistics;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// The percent axis of a probability plot: chosen percentages, each placed at the normal score it belongs to.
public class ProbabilityAxisTests
{
    private static readonly double StandardLow = NormalDistribution.InverseCdf(0.001);

    private static readonly double StandardHigh = NormalDistribution.InverseCdf(0.999);

    // 1
    [Fact]
    public void AnOrdinarySampleIsReadOnTheStandardPercentages()
    {
        var axis = ProbabilityAxis.Axis(-1.5, 1.5);

        Assert.Equal(ProbabilityAxis.Title, axis.Title);
        Assert.Equal(
            ["0.1", "0.5", "1", "2", "5", "10", "20", "30", "50", "70", "80", "90", "95", "98", "99", "99.5", "99.9"],
            axis.Ticks.Select(tick => tick.Label));
    }

    // 2
    [Fact]
    public void EveryTickSitsAtTheScoreOfItsPercentage()
    {
        var axis = ProbabilityAxis.Axis(-1, 1);

        foreach (var tick in axis.Ticks)
        {
            var percent = double.Parse(tick.Label, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(NormalDistribution.InverseCdf(percent / 100), tick.Value, 1e-12);
        }

        Assert.Equal(0, Assert.Single(axis.Ticks, tick => tick.Label == "50").Value, 1e-12);
    }

    // 3
    [Fact]
    public void TheSpacingOfThePercentagesIsNotLinear()
    {
        var axis = ProbabilityAxis.Axis(-1, 1);
        double Score(string label) => Assert.Single(axis.Ticks, tick => tick.Label == label).Value;

        // Equal steps in percent are not equal steps on the axis: 50% to 70% and 70% to 90% are both twenty points.
        var nearTheMiddle = Score("70") - Score("50");
        var furtherOut = Score("90") - Score("70");
        Assert.True(furtherOut > nearTheMiddle, $"{furtherOut} is not further than {nearTheMiddle}");

        // And the last four tenths of a percent take as much room as twenty whole percent do in the middle.
        var perPointInTheMiddle = nearTheMiddle / 20;
        var perPointInTheTail = (Score("99.9") - Score("99.5")) / 0.4;
        Assert.True(perPointInTheTail > perPointInTheMiddle * 20, $"{perPointInTheTail} is not stretched next to {perPointInTheMiddle}");
    }

    // 4
    [Fact]
    public void AnOrdinarySampleAlwaysCoversAtLeastTheStandardRange()
    {
        var axis = ProbabilityAxis.Axis(-0.2, 0.2);

        Assert.True(axis.Range.Minimum < StandardLow);
        Assert.True(axis.Range.Maximum > StandardHigh);
        Assert.All(axis.Ticks, tick => Assert.InRange(tick.Value, axis.Range.Minimum, axis.Range.Maximum));
    }

    // 5
    [Fact]
    public void TheAxisGrowsWithTheDataAndKeepsALittleRoomAroundIt()
    {
        var axis = ProbabilityAxis.Axis(-4.9, 4.9);

        Assert.True(axis.Range.Minimum < -4.9, "the smallest score is on the frame");
        Assert.True(axis.Range.Maximum > 4.9, "the largest score is on the frame");

        // Two percent of the score range, above and below.
        var padded = (axis.Range.Maximum - axis.Range.Minimum) / (1 + (2 * ProbabilityAxis.PaddingFraction));
        Assert.Equal(9.8, padded, 1e-9);
    }

    // 6
    [Fact]
    public void ALargeSampleGetsPercentagesForItsTailsToo()
    {
        // A million observations reach about five scores out, where the standard percentages stop at about three.
        var axis = ProbabilityAxis.Axis(-4.9, 4.9);

        Assert.Contains(axis.Ticks, tick => tick.Label == "0.01");
        Assert.Contains(axis.Ticks, tick => tick.Label == "0.001");
        Assert.Contains(axis.Ticks, tick => tick.Label == "0.0001");
        Assert.Contains(axis.Ticks, tick => tick.Label == "99.99");
        Assert.Contains(axis.Ticks, tick => tick.Label == "99.999");
        Assert.Contains(axis.Ticks, tick => tick.Label == "99.9999");
    }

    // 7
    [Fact]
    public void TailPercentagesAppearOnlyWhereTheAxisReaches()
    {
        var ordinary = ProbabilityAxis.Axis(-2, 2);
        var slightlyWider = ProbabilityAxis.Axis(-3.5, 3.5);

        Assert.DoesNotContain(ordinary.Ticks, tick => ProbabilityAxis.TailPercents.Contains(Percent(tick.Label)));
        Assert.DoesNotContain(slightlyWider.Ticks, tick => tick.Label == "0.01");
        Assert.Equal(ProbabilityAxis.StandardPercents.Count, ordinary.Ticks.Count);
    }

    // 8
    [Fact]
    public void TheTicksRiseWithTheirPercentages()
    {
        var axis = ProbabilityAxis.Axis(-5, 5);

        Assert.Equal(axis.Ticks.Select(tick => tick.Value).Order(), axis.Ticks.Select(tick => tick.Value));
        Assert.Equal(axis.Ticks.Select(tick => Percent(tick.Label)).Order(), axis.Ticks.Select(tick => Percent(tick.Label)));
    }

    // 9
    [Fact]
    public void TheSameScoresAlwaysGiveTheSameAxis()
    {
        Assert.Equal(ProbabilityAxis.Axis(-2, 3).Range, ProbabilityAxis.Axis(-2, 3).Range);
        Assert.Equal(
            ProbabilityAxis.Axis(-2, 3).Ticks.Select(tick => tick.Label),
            ProbabilityAxis.Axis(-2, 3).Ticks.Select(tick => tick.Label));
    }

    // 10
    [Fact]
    public void AnAxisNeedsFiniteScoresInOrder()
    {
        Assert.Throws<ArgumentException>(() => ProbabilityAxis.Axis(double.NaN, 1));
        Assert.Throws<ArgumentException>(() => ProbabilityAxis.Axis(0, double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => ProbabilityAxis.Axis(1, -1));
    }

    private static double Percent(string label) => double.Parse(label, System.Globalization.CultureInfo.InvariantCulture);
}
