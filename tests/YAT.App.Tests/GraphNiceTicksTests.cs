using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// The automatic axis scale: steps of 1, 2 or 5 times a power of ten, so an axis reads the way an engineer would write
// it. Deterministic, finite and invariant - no culture and no floating point noise in the labels.
public class GraphNiceTicksTests
{
    private static IReadOnlyList<GraphAxisTick> Ticks(double minimum, double maximum) =>
        GraphAxisTicks.Nice(new GraphAxisRange(minimum, maximum));

    // 1
    [Fact]
    public void ARangeOfDataFromZeroToOneHundredReadsInTwenties()
    {
        // 0..100 padded by the range policy, which is what an axis is really built from.
        var ticks = GraphAxisTicks.Nice(GraphAxisRanges.FromValues(0, 100));

        Assert.Equal([0, 20, 40, 60, 80, 100], ticks.Select(tick => tick.Value));
        Assert.Equal(["0", "20", "40", "60", "80", "100"], ticks.Select(tick => tick.Label));
    }

    // 2
    [Fact]
    public void ANegativeRangeReadsTheSameWay()
    {
        var ticks = GraphAxisTicks.Nice(GraphAxisRanges.FromValues(-100, -50));

        Assert.Equal([-100, -90, -80, -70, -60, -50], ticks.Select(tick => tick.Value));
        Assert.Equal(["-100", "-90", "-80", "-70", "-60", "-50"], ticks.Select(tick => tick.Label));
    }

    // 3
    [Fact]
    public void ARangeCrossingZeroHasATickAtZeroWrittenWithoutASign()
    {
        var ticks = GraphAxisTicks.Nice(GraphAxisRanges.FromValues(-10, 10));

        Assert.Equal([-10, -5, 0, 5, 10], ticks.Select(tick => tick.Value));
        Assert.Equal("0", Assert.Single(ticks, tick => tick.Value == 0).Label);
        Assert.DoesNotContain(ticks, tick => tick.Label == "-0");
    }

    // 4
    [Fact]
    public void SmallDecimalsKeepTheDecimalsTheyNeedAndNoMore()
    {
        var ticks = GraphAxisTicks.Nice(GraphAxisRanges.FromValues(0.001, 0.002));

        Assert.Equal(["0.0010", "0.0012", "0.0014", "0.0016", "0.0018", "0.0020"], ticks.Select(tick => tick.Label));
    }

    // 5
    [Fact]
    public void LargeValuesReadInScientificNotation()
    {
        var ticks = GraphAxisTicks.Nice(GraphAxisRanges.FromValues(0, 2e9));

        Assert.Equal([0, 5e8, 1e9, 1.5e9, 2e9], ticks.Select(tick => tick.Value));
        Assert.Equal(["0E+0", "5E+8", "1E+9", "1.5E+9", "2E+9"], ticks.Select(tick => tick.Label));
    }

    // 6
    [Theory]
    [InlineData(0d, 1d)]
    [InlineData(0d, 100d)]
    [InlineData(-1d, 1d)]
    [InlineData(-273.15d, 1234.5d)]
    [InlineData(0.000123d, 0.000456d)]
    [InlineData(1e6d, 1.0000001e6d)]
    [InlineData(-9.87e11d, 6.54e11d)]
    [InlineData(2.5d, 2.5000001d)]
    public void EveryRangeGetsAReasonableNumberOfFiniteTicksInsideIt(double minimum, double maximum)
    {
        var range = GraphAxisRanges.FromValues(minimum, maximum);
        var ticks = GraphAxisTicks.Nice(range);

        Assert.InRange(ticks.Count, 3, 11);
        Assert.All(ticks, tick =>
        {
            Assert.True(double.IsFinite(tick.Value));
            Assert.InRange(tick.Value, range.Minimum, range.Maximum);
            Assert.False(string.IsNullOrWhiteSpace(tick.Label));
            Assert.DoesNotContain(",", tick.Label);
        });
    }

    // 7
    [Fact]
    public void TheStepIsAlwaysOneTwoOrFiveTimesAPowerOfTen()
    {
        for (var exponent = -6; exponent <= 9; exponent++)
        {
            var range = GraphAxisRanges.FromValues(0, 7.3 * Math.Pow(10, exponent));
            var ticks = GraphAxisTicks.Nice(range);
            var step = ticks[1].Value - ticks[0].Value;
            var normalized = step / Math.Pow(10, Math.Floor(Math.Log10(step)));

            Assert.True(
                Math.Abs(normalized - 1) < 1e-6 || Math.Abs(normalized - 2) < 1e-6 || Math.Abs(normalized - 5) < 1e-6,
                $"step {step} of 10^{exponent} is not a 1-2-5 step.");
        }
    }

    // 8
    [Fact]
    public void TheSameRangeAlwaysGivesTheSameTicks()
    {
        var range = GraphAxisRanges.FromValues(-3.75, 21.5);

        Assert.Equal(
            GraphAxisTicks.Nice(range).Select(tick => (tick.Value, tick.Label)),
            GraphAxisTicks.Nice(range).Select(tick => (tick.Value, tick.Label)));
    }

    // 9
    [Fact]
    public void ARangeTooNarrowForItsOwnStepFallsBackToEvenTicks()
    {
        // Two ticks are the least an axis can be read from; a range that contains fewer multiples of its step is
        // divided evenly instead of losing its scale.
        var ticks = GraphAxisTicks.Nice(new GraphAxisRange(1.0000000000000002, 1.0000000000000004));

        Assert.True(ticks.Count >= 2);
        Assert.All(ticks, tick => Assert.True(double.IsFinite(tick.Value)));
    }

    // 10
    [Fact]
    public void ARangeMustBeValidAndTheIntervalsAtLeastTwo()
    {
        Assert.Throws<ArgumentException>(() => GraphAxisTicks.Nice(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphAxisTicks.Nice(new GraphAxisRange(0, 1), 1));
    }
}
