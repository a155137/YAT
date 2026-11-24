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

    // ---- PMI-02: Numerical safety ----

    [Fact]
    public void LargeFiniteSumOverflowStillProducesFiniteMeanRangeAndStandardDeviation()
    {
        // D-1: three moving ranges of 1e308 each; raw sum overflows double.MaxValue, true MRbar = 1e308.
        var estimator = new MovingRangeSigma();
        foreach (var value in (double[])[0, 1e308, 0, 1e308])
        {
            estimator.Add(value);
        }

        Assert.Equal(3, estimator.RangeCount);
        Assert.NotNull(estimator.MeanRange);
        Assert.NotNull(estimator.StandardDeviation);
        Assert.Equal(1e308, estimator.MeanRange!.Value, 1e296);
        Assert.Equal(1e308 / MovingRangeSigma.D2, estimator.StandardDeviation!.Value, 1e296);
    }

    [Fact]
    public void LargeDifferenceOverflowWithRepresentableMeanRangeAndSigmaProducesFiniteResults()
    {
        // D-2: ranges are 2e308, 0, 0 -> MRbar = (2/3) * 1e308, sigma = MRbar / 1.128.
        var estimator = new MovingRangeSigma();
        foreach (var value in (double[])[-1e308, 1e308, 1e308, 1e308])
        {
            estimator.Add(value);
        }

        var expectedMeanRange = (2.0 / 3.0) * 1e308;
        var expectedSigma = ((2.0 / 3.0) / MovingRangeSigma.D2) * 1e308;

        Assert.Equal(3, estimator.RangeCount);
        Assert.Equal(expectedMeanRange, estimator.MeanRange!.Value, expectedMeanRange * 1e-12);
        Assert.Equal(expectedSigma, estimator.StandardDeviation!.Value, expectedSigma * 1e-12);
    }

    [Fact]
    public void TransitionFromFastPathToScaledPathPreservesPriorAccumulation()
    {
        // Range 1 = 1e308 (fast path), Range 2 = 2e308 (overflow -> scaled path), Range 3 = 0 -> MRbar = 1e308.
        var estimator = new MovingRangeSigma();
        foreach (var value in (double[])[0, 1e308, -1e308, -1e308])
        {
            estimator.Add(value);
        }

        Assert.Equal(3, estimator.RangeCount);
        Assert.Equal(1e308, estimator.MeanRange!.Value, 1e296);
        Assert.Equal(1e308 / MovingRangeSigma.D2, estimator.StandardDeviation!.Value, 1e296);
    }

    [Fact]
    public void UnrepresentableMeanRangeWithRepresentableSigmaReturnsNullMeanRangeAndFiniteSigma()
    {
        // D-3 (Option A): MRbar = 2e308 (> double.MaxValue -> null), sigma = 2e308 / 1.128 ≈ 1.7730496e308 (finite).
        var estimator = new MovingRangeSigma();
        estimator.Add(-1e308);
        estimator.Add(1e308);

        var expectedSigma = (2.0 / MovingRangeSigma.D2) * 1e308;

        Assert.Equal(1, estimator.RangeCount);
        Assert.Null(estimator.MeanRange);
        Assert.NotNull(estimator.StandardDeviation);
        Assert.True(double.IsFinite(estimator.StandardDeviation!.Value));
        Assert.Equal(expectedSigma, estimator.StandardDeviation!.Value, expectedSigma * 1e-12);
    }

    [Fact]
    public void AlternatingMaxValuesWhoseResultIsUnrepresentableReturnNullRatherThanInfinity()
    {
        // D-4: MRbar = 2 * MaxValue, sigma = 2 * MaxValue / 1.128 > MaxValue -> both unrepresentable (null).
        var estimator = new MovingRangeSigma();
        estimator.Add(-double.MaxValue);
        estimator.Add(double.MaxValue);
        estimator.Add(-double.MaxValue);

        Assert.Equal(2, estimator.RangeCount);
        Assert.Null(estimator.MeanRange);
        Assert.Null(estimator.StandardDeviation);
    }

    [Fact]
    public void SubnormalObservationsProduceFinitePositiveSpread()
    {
        var sigma = MovingRangeSigma.StandardDeviationOf([double.Epsilon, 2 * double.Epsilon, double.Epsilon]);

        Assert.NotNull(sigma);
        Assert.True(double.IsFinite(sigma!.Value));
        Assert.True(sigma.Value > 0);
    }
}
