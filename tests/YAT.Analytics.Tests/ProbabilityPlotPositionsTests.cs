using YAT.Analytics.Statistics;

namespace YAT.Analytics.Tests;

// Benard's median rank: where an observation of a given rank sits in the distribution.
public class ProbabilityPlotPositionsTests
{
    private const double Tolerance = 1e-12;

    // 1
    [Fact]
    public void ASampleOfOneSitsInTheMiddle()
    {
        // (1 - 0.3) / (1 + 0.4) = 0.5
        Assert.Equal(0.5, ProbabilityPlotPositions.Benard(1, 1), Tolerance);
    }

    // 2
    [Fact]
    public void TheEndsOfASampleOfTen()
    {
        Assert.Equal(0.7 / 10.4, ProbabilityPlotPositions.Benard(1, 10), Tolerance);
        Assert.Equal(9.7 / 10.4, ProbabilityPlotPositions.Benard(10, 10), Tolerance);
    }

    // 3
    [Fact]
    public void TheMiddleOfAnOddSampleIsAHalf()
    {
        Assert.Equal(0.5, ProbabilityPlotPositions.Benard(6, 11), Tolerance);
    }

    // 4
    [Fact]
    public void EveryPositionIsAProbabilityStrictlyBetweenZeroAndOne()
    {
        foreach (var sampleCount in (int[])[1, 2, 3, 10, 1_000, 1_000_000])
        {
            foreach (var rank in (int[])[1, sampleCount / 2 + 1, sampleCount])
            {
                var position = ProbabilityPlotPositions.Benard(rank, sampleCount);
                Assert.True(position > 0 && position < 1, $"rank {rank} of {sampleCount} gave {position}");
            }
        }
    }

    // 5
    [Fact]
    public void APositionRisesWithItsRank()
    {
        var previous = 0d;
        for (var rank = 1; rank <= 50; rank++)
        {
            var position = ProbabilityPlotPositions.Benard(rank, 50);
            Assert.True(position > previous);
            previous = position;
        }
    }

    // 6
    [Fact]
    public void TheEndsOfALargeSampleReachFurtherOut()
    {
        // What makes a million observations need probability labels past 0.1% and 99.9%.
        var smallest = NormalDistribution.InverseCdf(ProbabilityPlotPositions.Benard(1, 1_000_000));
        var largest = NormalDistribution.InverseCdf(ProbabilityPlotPositions.Benard(1_000_000, 1_000_000));

        Assert.InRange(smallest, -5.5, -4.5);
        Assert.InRange(largest, 4.5, 5.5);
    }

    // 7
    [Fact]
    public void ARankBelongsToItsSample()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbabilityPlotPositions.Benard(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbabilityPlotPositions.Benard(11, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbabilityPlotPositions.Benard(1, 0));
    }
}
