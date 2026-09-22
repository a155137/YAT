using YAT.Analytics.Statistics;

namespace YAT.Analytics.Tests;

// What a box is made of, with the numbers written down: R-7 quartiles, Tukey fences, whiskers that end on real
// observations, and outliers that stay part of the sample.
public class BoxPlotSummaryTests
{
    private const double Tolerance = 1e-12;

    // 0
    [Fact]
    public void TheQuartilesAreTheR7OnesTheRestOfYatUses()
    {
        // 1..9: h = (n - 1) * p lands on whole indices here.
        double[] values = [5, 9, 1, 3, 7, 2, 8, 4, 6];

        var summary = BoxPlotSummary.ComputeInPlaceSorting(values);

        Assert.Equal(9, summary.Count);
        Assert.Equal(Quantiles.Linear([1, 2, 3, 4, 5, 6, 7, 8, 9], 0.25), summary.FirstQuartile!.Value, Tolerance);
        Assert.Equal(3, summary.FirstQuartile!.Value, Tolerance);
        Assert.Equal(5, summary.Median!.Value, Tolerance);
        Assert.Equal(7, summary.ThirdQuartile!.Value, Tolerance);
        Assert.Equal(4, summary.InterquartileRange!.Value, Tolerance);
        Assert.Equal(5, summary.Mean!.Value, Tolerance);
    }

    // 1
    [Fact]
    public void TheWhiskersEndOnObservationsInsideTheFences()
    {
        // Q1 = 3, Q3 = 7, IQR = 4, fences -3 and 13: every observation is inside, so the whiskers are the extremes.
        double[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9];

        var summary = BoxPlotSummary.ComputeInPlaceSorting(values);

        Assert.Equal(1, summary.LowerWhisker!.Value, Tolerance);
        Assert.Equal(9, summary.UpperWhisker!.Value, Tolerance);
        Assert.Empty(summary.Outliers);
    }

    // 2
    [Fact]
    public void AnObservationBeyondTheUpperFenceIsAnOutlierAndTheWhiskerStopsBeforeIt()
    {
        // 1..9 plus 100: Q1 = 3.25, Q3 = 7.75, IQR = 4.5, upper fence = 14.5.
        double[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 100];

        var summary = BoxPlotSummary.ComputeInPlaceSorting(values);

        Assert.Equal(3.25, summary.FirstQuartile!.Value, Tolerance);
        Assert.Equal(7.75, summary.ThirdQuartile!.Value, Tolerance);
        Assert.Equal(4.5, summary.InterquartileRange!.Value, Tolerance);

        // The whisker ends at 9, the largest observation at or below the fence; 100 is drawn as a point.
        Assert.Equal(9, summary.UpperWhisker!.Value, Tolerance);
        Assert.Equal(1, summary.LowerWhisker!.Value, Tolerance);
        Assert.Equal([100], summary.Outliers);

        // The outlier is still part of the sample: the mean says so.
        Assert.Equal(14.5, summary.Mean!.Value, Tolerance);
    }

    // 3
    [Fact]
    public void AnObservationBeyondTheLowerFenceIsAnOutlierToo()
    {
        double[] values = [-100, 1, 2, 3, 4, 5, 6, 7, 8, 9];

        var summary = BoxPlotSummary.ComputeInPlaceSorting(values);

        Assert.Equal([-100], summary.Outliers);
        Assert.Equal(1, summary.LowerWhisker!.Value, Tolerance);
        Assert.Equal(9, summary.UpperWhisker!.Value, Tolerance);
    }

    // 4
    [Fact]
    public void OutliersAtBothEndsAreReportedInAscendingOrder()
    {
        double[] values = [-50, -40, 1, 2, 3, 4, 5, 6, 7, 8, 9, 60, 70];

        var summary = BoxPlotSummary.ComputeInPlaceSorting(values);

        Assert.Equal([-50, -40, 60, 70], summary.Outliers);
        Assert.Equal(1, summary.LowerWhisker!.Value, Tolerance);
        Assert.Equal(9, summary.UpperWhisker!.Value, Tolerance);
    }

    // 5
    [Fact]
    public void TheFenceIsExactlyOneAndAHalfInterquartileRangesAndAnObservationOnItIsNotAnOutlier()
    {
        // Both samples have Q1 = 1, Q3 = 3 and IQR = 2, so their upper fence is exactly 6: an observation sitting on
        // the fence is inside it, one just past it is not.
        double[] onTheFence = [1, 1, 1, 1, 3, 3, 3, 6];
        double[] pastTheFence = [1, 1, 1, 1, 3, 3, 3, 6.5];

        var inside = BoxPlotSummary.ComputeInPlaceSorting(onTheFence);
        var outside = BoxPlotSummary.ComputeInPlaceSorting(pastTheFence);

        Assert.Equal(2, inside.InterquartileRange!.Value, Tolerance);
        Assert.Equal(6, inside.UpperWhisker!.Value, Tolerance);
        Assert.Empty(inside.Outliers);

        Assert.Equal(2, outside.InterquartileRange!.Value, Tolerance);
        Assert.Equal(3, outside.UpperWhisker!.Value, Tolerance);
        Assert.Equal([6.5], outside.Outliers);
        Assert.Equal(1.5, BoxPlotSummary.FenceMultiplier);
    }

    // 6
    [Fact]
    public void DuplicateObservationsAllCount()
    {
        double[] values = [5, 5, 5, 5, 5, 5, 5, 20, 20];

        var summary = BoxPlotSummary.ComputeInPlaceSorting(values);

        Assert.Equal(9, summary.Count);

        // Q1 = Q3 = 5, so the fences collapse onto 5 and both 20s are outliers - twice, not once.
        Assert.Equal(0, summary.InterquartileRange!.Value, Tolerance);
        Assert.Equal([20, 20], summary.Outliers);
        Assert.Equal(5, summary.LowerWhisker!.Value, Tolerance);
        Assert.Equal(5, summary.UpperWhisker!.Value, Tolerance);
        Assert.Equal((7 * 5d + 40) / 9, summary.Mean!.Value, Tolerance);
    }

    // 7
    [Fact]
    public void ConstantDataHasNoSpreadAndNoOutliers()
    {
        double[] values = [100, 100, 100, 100];

        var summary = BoxPlotSummary.ComputeInPlaceSorting(values);

        Assert.Equal(100, summary.FirstQuartile!.Value, Tolerance);
        Assert.Equal(100, summary.Median!.Value, Tolerance);
        Assert.Equal(100, summary.ThirdQuartile!.Value, Tolerance);
        Assert.Equal(0, summary.InterquartileRange!.Value, Tolerance);
        Assert.Equal(100, summary.LowerWhisker!.Value, Tolerance);
        Assert.Equal(100, summary.UpperWhisker!.Value, Tolerance);
        Assert.Equal(100, summary.Mean!.Value, Tolerance);
        Assert.Empty(summary.Outliers);
    }

    // 8
    [Fact]
    public void ASingleObservationIsItsOwnBox()
    {
        double[] values = [42];

        var summary = BoxPlotSummary.ComputeInPlaceSorting(values);

        Assert.Equal(1, summary.Count);
        Assert.Equal(42, summary.FirstQuartile!.Value, Tolerance);
        Assert.Equal(42, summary.Median!.Value, Tolerance);
        Assert.Equal(42, summary.ThirdQuartile!.Value, Tolerance);
        Assert.Equal(42, summary.Mean!.Value, Tolerance);
        Assert.Equal(42, summary.LowerWhisker!.Value, Tolerance);
        Assert.Equal(42, summary.UpperWhisker!.Value, Tolerance);
        Assert.Equal(0, summary.InterquartileRange!.Value, Tolerance);
        Assert.Empty(summary.Outliers);
    }

    // 9
    [Fact]
    public void ASampleWithoutObservationsHasNoBox()
    {
        var summary = BoxPlotSummary.ComputeInPlaceSorting([]);

        Assert.Equal(0, summary.Count);
        Assert.Null(summary.Mean);
        Assert.Null(summary.FirstQuartile);
        Assert.Null(summary.Median);
        Assert.Null(summary.ThirdQuartile);
        Assert.Null(summary.InterquartileRange);
        Assert.Null(summary.LowerWhisker);
        Assert.Null(summary.UpperWhisker);
        Assert.Empty(summary.Outliers);
        Assert.Equal(BoxPlotSummary.Empty, summary);
    }

    // 10
    [Fact]
    public void TheSampleComesBackSortedBecauseTheBoxIsComputedInPlace()
    {
        double[] values = [3, 1, 2];

        BoxPlotSummary.ComputeInPlaceSorting(values);

        Assert.Equal([1, 2, 3], values);
    }

    // 11
    [Fact]
    public void TheOrderTheObservationsArriveInChangesNothing()
    {
        double[] measured = [9, 1, 100, 5, 3, 7, 2, 8, 4, 6];
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 100];

        var fromMeasured = BoxPlotSummary.ComputeInPlaceSorting(measured);
        var fromSorted = BoxPlotSummary.ComputeInPlaceSorting(sorted);

        Assert.Equal(fromSorted with { Outliers = [] }, fromMeasured with { Outliers = [] });
        Assert.Equal(fromSorted.Outliers, fromMeasured.Outliers);
    }

    // Characterisation of the real-user crash data: the statistics themselves are right. R-7 interpolates Q3 between
    // 0.133 and 0.157, every 0.157 is beyond the upper fence, and so the upper whisker - the largest observation inside
    // the fence - is 0.133, below Q3. A whisker inside its own box is a correct result, not an inconsistent one.
    [Fact]
    public void AnUpperWhiskerCanEndInsideTheBoxWhenTheValueQ3IsInterpolatedTowardsIsAnOutlier()
    {
        var values = Enumerable.Range(0, 200).SelectMany(_ => (double[])[0.132, 0.157, 0.122, 0.133]).ToArray();

        var summary = BoxPlotSummary.ComputeInPlaceSorting(values);

        Assert.Equal(800, summary.Count);
        Assert.Equal(0.1295, summary.FirstQuartile!.Value, Tolerance);
        Assert.Equal(0.1325, summary.Median!.Value, Tolerance);
        Assert.Equal(0.139, summary.ThirdQuartile!.Value, Tolerance);
        Assert.Equal(0.0095, summary.InterquartileRange!.Value, Tolerance);
        Assert.Equal(0.122, summary.LowerWhisker!.Value, Tolerance);
        Assert.Equal(0.133, summary.UpperWhisker!.Value, Tolerance);
        Assert.True(summary.UpperWhisker < summary.ThirdQuartile);
        Assert.Equal(200, summary.Outliers.Count);
        Assert.All(summary.Outliers, outlier => Assert.Equal(0.157, outlier));
    }

    // 12
    [Fact]
    public void TwoObservationsStillProduceABox()
    {
        double[] values = [10, 20];

        var summary = BoxPlotSummary.ComputeInPlaceSorting(values);

        Assert.Equal(12.5, summary.FirstQuartile!.Value, Tolerance);
        Assert.Equal(15, summary.Median!.Value, Tolerance);
        Assert.Equal(17.5, summary.ThirdQuartile!.Value, Tolerance);
        Assert.Equal(10, summary.LowerWhisker!.Value, Tolerance);
        Assert.Equal(20, summary.UpperWhisker!.Value, Tolerance);
        Assert.Empty(summary.Outliers);
    }
}
