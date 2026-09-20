using YAT.Analytics.Statistics;

namespace YAT.Analytics.Tests;

// The standard normal distribution, in both directions. The numbers below are the published ones; the accuracy the
// implementation actually reaches is what these tests pin down.
public class NormalDistributionTests
{
    // 1
    [Fact]
    public void TheMiddleOfTheDistributionIsZero()
    {
        Assert.Equal(0, NormalDistribution.InverseCdf(0.5), 1e-12);
        Assert.Equal(0.5, NormalDistribution.Cdf(0), 1e-12);
    }

    // 2
    [Theory]
    [InlineData(0.025, -1.959964)]
    [InlineData(0.975, 1.959964)]
    [InlineData(0.01, -2.326348)]
    [InlineData(0.99, 2.326348)]
    [InlineData(0.05, -1.644854)]
    [InlineData(0.95, 1.644854)]
    [InlineData(0.001, -3.090232)]
    [InlineData(0.999, 3.090232)]
    public void TheScoresEveryTableAgreesOn(double probability, double expected)
    {
        Assert.Equal(expected, NormalDistribution.InverseCdf(probability), 1e-6);
    }

    // 3
    [Theory]
    [InlineData(1e-9)]
    [InlineData(1e-6)]
    [InlineData(0.0001)]
    [InlineData(0.001)]
    [InlineData(0.01)]
    [InlineData(0.025)]
    [InlineData(0.1)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(0.9)]
    [InlineData(0.975)]
    [InlineData(0.99)]
    [InlineData(0.999)]
    [InlineData(0.9999)]
    [InlineData(1 - 1e-9)]
    public void GoingThereAndBackAgainReturnsTheSameProbability(double probability)
    {
        var score = NormalDistribution.InverseCdf(probability);
        var roundTrip = NormalDistribution.Cdf(score);

        Assert.Equal(probability, roundTrip, Math.Abs(probability) * 1e-9);
    }

    // 4
    [Theory]
    [InlineData(-3, 0.001349898)]
    [InlineData(-2, 0.022750132)]
    [InlineData(-1, 0.158655254)]
    [InlineData(0, 0.5)]
    [InlineData(1, 0.841344746)]
    [InlineData(2, 0.977249868)]
    [InlineData(3, 0.998650102)]
    public void TheProbabilitiesEveryTableAgreesOn(double score, double expected)
    {
        Assert.Equal(expected, NormalDistribution.Cdf(score), 1e-9);
    }

    // 5
    [Fact]
    public void TheDistributionIsSymmetric()
    {
        foreach (var probability in (double[])[0.0001, 0.01, 0.2, 0.37, 0.499])
        {
            Assert.Equal(-NormalDistribution.InverseCdf(probability), NormalDistribution.InverseCdf(1 - probability), 1e-9);
        }

        foreach (var score in (double[])[0.5, 1.5, 2.5, 4])
        {
            Assert.Equal(1 - NormalDistribution.Cdf(score), NormalDistribution.Cdf(-score), 1e-12);
        }
    }

    // 6
    [Fact]
    public void BothDirectionsRiseWithTheirArgument()
    {
        var previousScore = double.NegativeInfinity;
        var previousProbability = 0d;

        for (var step = 1; step < 1_000; step++)
        {
            var probability = step / 1_000d;
            var score = NormalDistribution.InverseCdf(probability);

            Assert.True(score > previousScore, $"the score at {probability} did not rise");
            previousScore = score;

            var cumulative = NormalDistribution.Cdf(-5 + (step / 100d));
            Assert.True(cumulative > previousProbability, $"the probability at step {step} did not rise");
            previousProbability = cumulative;
        }
    }

    // 7
    [Fact]
    public void FarOutTheTailsStillAnswer()
    {
        Assert.True(NormalDistribution.InverseCdf(1e-300) < -37);
        Assert.True(NormalDistribution.InverseCdf(1 - 1e-15) > 7);
        Assert.Equal(0, NormalDistribution.Cdf(-40), 1e-300);
        Assert.Equal(1, NormalDistribution.Cdf(40), 1e-12);
        Assert.All(
            (double[])[1e-300, 1e-100, 1e-10, 0.5, 1 - 1e-10],
            probability => Assert.True(double.IsFinite(NormalDistribution.InverseCdf(probability))));
    }

    // 8
    [Fact]
    public void TheEndsOfTheScaleAreNotProbabilities()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NormalDistribution.InverseCdf(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NormalDistribution.InverseCdf(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NormalDistribution.InverseCdf(-0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NormalDistribution.InverseCdf(1.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NormalDistribution.InverseCdf(double.NaN));
        Assert.Throws<ArgumentException>(() => NormalDistribution.Cdf(double.NaN));
    }

    // 9
    [Fact]
    public void InfinityIsCertainty()
    {
        Assert.Equal(1, NormalDistribution.Cdf(double.PositiveInfinity));
        Assert.Equal(0, NormalDistribution.Cdf(double.NegativeInfinity));
    }

    // 10
    [Fact]
    public void TheSameArgumentAlwaysGivesTheSameAnswer()
    {
        Assert.Equal(NormalDistribution.InverseCdf(0.0673076923076923), NormalDistribution.InverseCdf(0.0673076923076923));
        Assert.Equal(NormalDistribution.Cdf(1.2345), NormalDistribution.Cdf(1.2345));
    }
}
