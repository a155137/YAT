using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// The display cap the graphs share: a fixed budget, spent the same way every time. The graphs that use it have their
// own tests for what it means in a plot; these are about the policy itself.
public class DisplaySamplingTests
{
    // 1
    [Fact]
    public void BelowTheCapEverySeriesDrawsEverything()
    {
        Assert.Equal([30, 20, 50], DisplaySampling.Quotas([30, 20, 50], 100, 100));
        Assert.Equal([1], DisplaySampling.Quotas([1], 1, 100));
    }

    // 2
    [Fact]
    public void AboveTheCapTheQuotasNeverAddUpToMoreThanIt()
    {
        var quotas = DisplaySampling.Quotas([500, 300, 200], 1_000, 100);

        Assert.Equal(100, quotas.Sum());
        Assert.All(quotas, quota => Assert.True(quota >= 1));
    }

    // 3
    [Fact]
    public void ASmallSeriesKeepsItsPlace()
    {
        var quotas = DisplaySampling.Quotas([999_999, 1], 1_000_000, 100);

        Assert.Equal(1, quotas[1]);
        Assert.Equal(100, quotas.Sum());
    }

    // 4
    [Fact]
    public void MoreSeriesThanTheCapStillRespectIt()
    {
        var counts = Enumerable.Repeat(10, 20).ToArray();

        var quotas = DisplaySampling.Quotas(counts, 200, 5);

        Assert.Equal(5, quotas.Sum());
        Assert.Equal([1, 1, 1, 1, 1], quotas.Take(5));
        Assert.All(quotas.Skip(5), quota => Assert.Equal(0, quota));
    }

    // 5
    [Theory]
    [InlineData(1_000, 1, 7)]
    [InlineData(1_000, 37, 100)]
    [InlineData(5_000, 250, 300)]
    [InlineData(1_000_000, 8, 100_000)]
    public void TheCapHoldsForEveryShape(int total, int seriesCount, int cap)
    {
        var counts = new int[seriesCount];
        for (var index = 0; index < total; index++)
        {
            counts[index % seriesCount]++;
        }

        var quotas = DisplaySampling.Quotas(counts, total, cap);

        Assert.True(quotas.Sum() <= cap, $"{quotas.Sum()} > {cap}");
        Assert.All(quotas.Zip(counts), pair => Assert.True(pair.First <= pair.Second));
    }

    // 6
    [Fact]
    public void ASampleKeepsTheFirstAndTheLastPoint()
    {
        Assert.Equal(0, DisplaySampling.SampleIndex(0, 1_000, 100));
        Assert.Equal(999, DisplaySampling.SampleIndex(99, 1_000, 100));
        Assert.Equal(0, DisplaySampling.SampleIndex(0, 1_000, 1));
    }

    // 7
    [Fact]
    public void ASampleIsSpreadEvenlyAndNeverRepeatsAPoint()
    {
        var indexes = Enumerable.Range(0, 250).Select(index => DisplaySampling.SampleIndex(index, 1_000, 250)).ToArray();

        Assert.Distinct(indexes);
        Assert.Equal(indexes.Order(), indexes);
        Assert.All(indexes, index => Assert.InRange(index, 0, 999));
    }

    // 8
    [Fact]
    public void TheSameRequestAlwaysGivesTheSameAnswer()
    {
        Assert.Equal(DisplaySampling.Quotas([7, 13, 5], 25, 10), DisplaySampling.Quotas([7, 13, 5], 25, 10));
        Assert.Equal(DisplaySampling.SampleIndex(17, 500, 60), DisplaySampling.SampleIndex(17, 500, 60));
    }

    // 9
    [Fact]
    public void ThePolicyNeedsSensibleNumbers()
    {
        Assert.Throws<ArgumentNullException>(() => DisplaySampling.Quotas(null!, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplaySampling.Quotas([1], 10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplaySampling.Quotas([1], -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplaySampling.SampleIndex(-1, 10, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplaySampling.SampleIndex(0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplaySampling.SampleIndex(0, 10, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplaySampling.SampleIndex(5, 10, 5));
    }
}
