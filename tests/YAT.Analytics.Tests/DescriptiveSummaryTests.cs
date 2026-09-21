using YAT.Analytics.Statistics;

namespace YAT.Analytics.Tests;

// The descriptive summary of a sample, with the definitions written down: the standard deviation is the SAMPLE one,
// the quartiles are R-7, and a statistic the sample cannot support is null rather than zero.
public class DescriptiveSummaryTests
{
    private const double Tolerance = 1e-12;

    // 0
    [Fact]
    public void ASampleIsSummarisedWithTheStatisticsYatAlreadyDefines()
    {
        // Deliberately unsorted: the summary sorts what it is given.
        double[] values = [9, 2, 4, 5, 4, 7, 4, 5];

        var summary = DescriptiveSummary.ComputeInPlaceSorting(values, missingCount: 3);

        Assert.Equal(8, summary.Count);
        Assert.Equal(3, summary.MissingCount);
        Assert.Equal(5, summary.Mean!.Value, Tolerance);

        // Sample standard deviation: sqrt(32 / 7). The population one would be exactly 2.
        Assert.Equal(Math.Sqrt(32d / 7d), summary.StandardDeviation!.Value, Tolerance);
        Assert.Equal(2, summary.Minimum!.Value, Tolerance);
        Assert.Equal(9, summary.Maximum!.Value, Tolerance);
    }

    // 1
    [Fact]
    public void TheQuartilesAreTheR7OnesTheRestOfYatUses()
    {
        double[] values = [4, 1, 3, 2];

        var summary = DescriptiveSummary.ComputeInPlaceSorting(values, missingCount: 0);

        // h = (n - 1) * p, interpolated: the same numbers Quantiles.Linear (and Excel's PERCENTILE.INC) gives.
        Assert.Equal(Quantiles.Linear([1, 2, 3, 4], 0.25), summary.FirstQuartile!.Value, Tolerance);
        Assert.Equal(1.75, summary.FirstQuartile!.Value, Tolerance);
        Assert.Equal(2.5, summary.Median!.Value, Tolerance);
        Assert.Equal(3.25, summary.ThirdQuartile!.Value, Tolerance);
    }

    // 2
    [Fact]
    public void TheSampleComesBackSortedBecauseTheSummaryIsComputedInPlace()
    {
        double[] values = [3, 1, 2];

        DescriptiveSummary.ComputeInPlaceSorting(values, missingCount: 0);

        Assert.Equal([1, 2, 3], values);
    }

    // 3
    [Fact]
    public void ASampleWithoutObservationsHasNoStatisticsAtAll()
    {
        var summary = DescriptiveSummary.ComputeInPlaceSorting([], missingCount: 200);

        Assert.Equal(0, summary.Count);
        Assert.Equal(200, summary.MissingCount);
        Assert.Null(summary.Mean);
        Assert.Null(summary.StandardDeviation);
        Assert.Null(summary.Minimum);
        Assert.Null(summary.FirstQuartile);
        Assert.Null(summary.Median);
        Assert.Null(summary.ThirdQuartile);
        Assert.Null(summary.Maximum);
        Assert.Equal(summary, DescriptiveSummary.Empty(200));
    }

    // 4
    [Fact]
    public void ASingleObservationHasEveryStatisticExceptSpread()
    {
        double[] values = [0.132];

        var summary = DescriptiveSummary.ComputeInPlaceSorting(values, missingCount: 1);

        Assert.Equal(1, summary.Count);
        Assert.Equal(1, summary.MissingCount);
        Assert.Equal(0.132, summary.Mean!.Value, Tolerance);
        Assert.Equal(0.132, summary.Minimum!.Value, Tolerance);
        Assert.Equal(0.132, summary.FirstQuartile!.Value, Tolerance);
        Assert.Equal(0.132, summary.Median!.Value, Tolerance);
        Assert.Equal(0.132, summary.ThirdQuartile!.Value, Tolerance);
        Assert.Equal(0.132, summary.Maximum!.Value, Tolerance);

        // One observation says nothing about spread: n - 1 is 0, so there is no sample standard deviation.
        Assert.Null(summary.StandardDeviation);
    }

    // 5
    [Fact]
    public void TwoIdenticalObservationsHaveASpreadOfZeroWhichIsAMeasuredResultNotAMissingOne()
    {
        var summary = DescriptiveSummary.ComputeInPlaceSorting([2.5, 2.5], missingCount: 0);

        Assert.Equal(0, summary.StandardDeviation!.Value, Tolerance);
    }

    // 6
    [Fact]
    public void MissingObservationsAreCountedButNeverSummarised()
    {
        // The caller decides what is missing; the summary only reports it, and it never affects the statistics.
        var withMissing = DescriptiveSummary.ComputeInPlaceSorting([1, 2, 3], missingCount: 997);
        var withoutMissing = DescriptiveSummary.ComputeInPlaceSorting([1, 2, 3], missingCount: 0);

        Assert.Equal(997, withMissing.MissingCount);
        Assert.Equal(3, withMissing.Count);
        Assert.Equal(withoutMissing.Mean, withMissing.Mean);
        Assert.Equal(withoutMissing.StandardDeviation, withMissing.StandardDeviation);
        Assert.Equal(withoutMissing.Median, withMissing.Median);
    }

    // 7
    [Fact]
    public void ANegativeMissingCountIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DescriptiveSummary.Empty(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DescriptiveSummary.ComputeInPlaceSorting([1, 2], -1));
    }
}
