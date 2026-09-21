using YAT.Analytics.Statistics;

namespace YAT.Analytics.Tests;

// The within-sample spread estimated from moving ranges: measured order matters, and a missing measurement breaks the
// adjacency that a moving range is made of.
public class MovingRangeSigmaTests
{
    private const double Tolerance = 1e-12;

    // 0
    [Fact]
    public void TheMovingRangesAreTheGapsBetweenConsecutiveObservations()
    {
        // |101-100| = 1, |104-101| = 3, |102-104| = 2  ->  MRbar = 2
        var estimator = new MovingRangeSigma();
        foreach (var value in (double[])[100, 101, 104, 102])
        {
            estimator.Add(value);
        }

        Assert.Equal(3, estimator.RangeCount);
        Assert.Equal(2, estimator.MeanRange!.Value, Tolerance);
        Assert.Equal(2 / 1.128, estimator.StandardDeviation!.Value, Tolerance);
        Assert.Equal(1.128, MovingRangeSigma.D2);
    }

    // 1
    [Fact]
    public void TheSequenceIsReadInTheOrderItWasMeasuredAndNeverSorted()
    {
        // In measured order the ranges are 4, 3, 1 (MRbar = 8/3); sorted they would be 1, 1, 2 (MRbar = 4/3).
        double?[] measured = [100, 104, 101, 102];
        double?[] sorted = [100, 101, 102, 104];

        Assert.Equal((8.0 / 3.0) / 1.128, MovingRangeSigma.StandardDeviationOf(measured)!.Value, Tolerance);
        Assert.Equal((4.0 / 3.0) / 1.128, MovingRangeSigma.StandardDeviationOf(sorted)!.Value, Tolerance);
    }

    // 2
    [Fact]
    public void AMissingMeasurementBreaksTheAdjacencyOnBothSides()
    {
        // 100, 101, null, 103, 102: only |101-100| and |102-103| were measured one after the other.
        double?[] sequence = [100, 101, null, 103, 102];

        var estimator = new MovingRangeSigma();
        estimator.Add(100);
        estimator.Add(101);
        estimator.Break();
        estimator.Add(103);
        estimator.Add(102);

        Assert.Equal(2, estimator.RangeCount);
        Assert.Equal(1, estimator.MeanRange!.Value, Tolerance);
        Assert.Equal(1 / 1.128, MovingRangeSigma.StandardDeviationOf(sequence)!.Value, Tolerance);
    }

    // 3
    [Fact]
    public void ValuesEitherSideOfAGapNeverFormAMovingRange()
    {
        // 101 -> null -> 103 must not produce |103 - 101| = 2.
        double?[] sequence = [101, null, 103];

        Assert.Null(MovingRangeSigma.StandardDeviationOf(sequence));
    }

    // 4
    [Fact]
    public void ASingleObservationHasNoMovingRange()
    {
        Assert.Null(MovingRangeSigma.StandardDeviationOf([42]));
        Assert.Equal(0, new MovingRangeSigma().RangeCount);
        Assert.Null(new MovingRangeSigma().MeanRange);
    }

    // 5
    [Fact]
    public void ASequenceWithoutAnyAdjacentPairHasNoEstimateEvenWithSeveralObservations()
    {
        // Three observations, no two of them consecutive.
        double?[] sequence = [100, null, 101, null, 102];

        Assert.Null(MovingRangeSigma.StandardDeviationOf(sequence));
    }

    // 6
    [Fact]
    public void AProcessThatNeverMovedHasASpreadOfZeroWhichIsAMeasuredResult()
    {
        Assert.Equal(0, MovingRangeSigma.StandardDeviationOf([5, 5, 5, 5])!.Value, Tolerance);
    }

    // 7
    [Fact]
    public void LeadingAndTrailingGapsChangeNothingAboutTheRangesBetweenTheObservations()
    {
        double?[] padded = [null, 100, 101, null];

        Assert.Equal(1 / 1.128, MovingRangeSigma.StandardDeviationOf(padded)!.Value, Tolerance);
    }

    // 8
    [Fact]
    public void AnEmptySequenceHasNoEstimate()
    {
        Assert.Null(MovingRangeSigma.StandardDeviationOf([]));
        Assert.Null(MovingRangeSigma.StandardDeviationOf([null, null]));
    }

    // 9
    [Fact]
    public void RangesAreAbsoluteSoADecreasingProcessIsNotANegativeSpread()
    {
        Assert.Equal(2 / 1.128, MovingRangeSigma.StandardDeviationOf([104, 102, 100])!.Value, Tolerance);
    }
}
