using YAT.Analytics.Statistics;

namespace YAT.Analytics.Tests;

// The capability indices and the summary that assembles them: the formulas are pinned down here, including what is
// left undefined and what a process outside its specification is allowed to report.
public class CapabilitySummaryTests
{
    private const double Tolerance = 1e-12;

    private static CapabilitySummary Summary(double? mean, double? sigma, double? lsl, double? usl) =>
        CapabilitySummary.Compute(count: 30, missingCount: 0, mean, sigma, lsl, usl);

    // 0
    [Fact]
    public void ATwoSidedSpecificationHasAllFourIndices()
    {
        // mean 15000, sigma 100, spec 14500..15500: Cp = 1000 / 600, Cpl = Cpu = 500 / 300.
        var summary = Summary(15000, 100, 14500, 15500);

        Assert.Equal(1000d / 600d, summary.Cp!.Value, Tolerance);
        Assert.Equal(500d / 300d, summary.Cpl!.Value, Tolerance);
        Assert.Equal(500d / 300d, summary.Cpu!.Value, Tolerance);
        Assert.Equal(500d / 300d, summary.Cpk!.Value, Tolerance);
        Assert.Equal(14500, summary.LowerSpecificationLimit);
        Assert.Equal(15500, summary.UpperSpecificationLimit);
    }

    // 1
    [Fact]
    public void CpkIsTheWorseOfTheTwoSides()
    {
        // Off centre towards the upper limit: Cpu = 200 / 300, Cpl = 800 / 300.
        var summary = Summary(15300, 100, 14500, 15500);

        Assert.Equal(200d / 300d, summary.Cpu!.Value, Tolerance);
        Assert.Equal(800d / 300d, summary.Cpl!.Value, Tolerance);
        Assert.Equal(summary.Cpu!.Value, summary.Cpk!.Value, Tolerance);

        // Cp does not move with the process: it only measures the room the specification leaves.
        Assert.Equal(1000d / 600d, summary.Cp!.Value, Tolerance);
    }

    // 2
    [Fact]
    public void AnUpperOnlySpecificationHasCpuAndCpkOnly()
    {
        var summary = Summary(480, 5, null, 500);

        Assert.Equal(20d / 15d, summary.Cpu!.Value, Tolerance);
        Assert.Equal(summary.Cpu!.Value, summary.Cpk!.Value, Tolerance);
        Assert.Null(summary.Cp);
        Assert.Null(summary.Cpl);
        Assert.Null(summary.LowerSpecificationLimit);
        Assert.Equal(500, summary.UpperSpecificationLimit);
    }

    // 3
    [Fact]
    public void ALowerOnlySpecificationHasCplAndCpkOnly()
    {
        var summary = Summary(26, 2, 20, null);

        Assert.Equal(6d / 6d, summary.Cpl!.Value, Tolerance);
        Assert.Equal(summary.Cpl!.Value, summary.Cpk!.Value, Tolerance);
        Assert.Null(summary.Cp);
        Assert.Null(summary.Cpu);
    }

    // 4
    [Fact]
    public void AProcessOutsideItsSpecificationReportsANegativeCpk()
    {
        // Mean 15600 is above the upper limit: Cpu = (15500 - 15600) / 300.
        var summary = Summary(15600, 100, 14500, 15500);

        Assert.Equal(-100d / 300d, summary.Cpu!.Value, Tolerance);
        Assert.Equal(-100d / 300d, summary.Cpk!.Value, Tolerance);
        Assert.True(summary.Cpk < 0);

        // Nothing is clamped: a negative index is the answer, not an error.
        Assert.Equal(-100d / 300d, CapabilityIndices.Cpk(summary.Cpl, summary.Cpu)!.Value, Tolerance);
    }

    // 5
    [Fact]
    public void AProcessThatNeverMovedHasNoCapabilityIndicesButStillReportsItsSpread()
    {
        var summary = Summary(15000, 0, 14500, 15500);

        Assert.Equal(0, summary.WithinStandardDeviation!.Value);
        Assert.Null(summary.Cp);
        Assert.Null(summary.Cpl);
        Assert.Null(summary.Cpu);
        Assert.Null(summary.Cpk);
    }

    // 6
    [Fact]
    public void WithoutASpreadThereAreNoCapabilityIndices()
    {
        var summary = Summary(15000, null, 14500, 15500);

        Assert.Null(summary.WithinStandardDeviation);
        Assert.Null(summary.Cp);
        Assert.Null(summary.Cpl);
        Assert.Null(summary.Cpu);
        Assert.Null(summary.Cpk);
        Assert.Equal(15000, summary.Mean);
    }

    // 7
    [Fact]
    public void ASequenceWithoutObservationsIsSafeAndKeepsItsSpecification()
    {
        var summary = CapabilitySummary.Compute(count: 0, missingCount: 200, mean: null, withinStandardDeviation: null, 14500, 15500);

        Assert.Equal(0, summary.Count);
        Assert.Equal(200, summary.MissingCount);
        Assert.Null(summary.Mean);
        Assert.Null(summary.WithinStandardDeviation);
        Assert.Null(summary.Cp);
        Assert.Null(summary.Cpk);
        Assert.Equal(14500, summary.LowerSpecificationLimit);
        Assert.Equal(15500, summary.UpperSpecificationLimit);
    }

    // 8
    [Fact]
    public void ASingleObservationHasAMeanButNoSpreadAndNoIndices()
    {
        var summary = CapabilitySummary.Compute(count: 1, missingCount: 4, mean: 15000, withinStandardDeviation: null, 14500, 15500);

        Assert.Equal(15000, summary.Mean);
        Assert.Null(summary.WithinStandardDeviation);
        Assert.Null(summary.Cp);
        Assert.Null(summary.Cpl);
        Assert.Null(summary.Cpu);
        Assert.Null(summary.Cpk);
    }

    // 9
    [Fact]
    public void AnIndexIsOnlyReportedWhenItsOwnInputsExist()
    {
        Assert.Null(CapabilityIndices.Cp(14500, null, 100));
        Assert.Null(CapabilityIndices.Cp(null, 15500, 100));
        Assert.Null(CapabilityIndices.Cpu(null, 15500, 100));
        Assert.Null(CapabilityIndices.Cpl(15000, null, 100));
        Assert.Null(CapabilityIndices.Cpk(null, null));
        Assert.Equal(2, CapabilityIndices.Cpk(2, null));
        Assert.Equal(3, CapabilityIndices.Cpk(null, 3));
    }

    // 10
    [Fact]
    public void ANegativeOrNonFiniteSpreadIsNoSpread()
    {
        Assert.Null(CapabilityIndices.Cp(0, 10, -1));
        Assert.Null(CapabilityIndices.Cp(0, 10, double.NaN));
        Assert.Null(CapabilityIndices.Cpu(5, 10, double.PositiveInfinity));
    }

    // 11
    [Fact]
    public void AnIndexThatOverflowsIsReportedAsNoResultRatherThanAsInfinity()
    {
        var summary = Summary(0, double.Epsilon, -double.MaxValue, double.MaxValue);

        Assert.Null(summary.Cp);
        Assert.Null(summary.Cpu);
        Assert.Null(summary.Cpl);
        Assert.Null(summary.Cpk);
    }

    // 12
    [Fact]
    public void LimitsTheProcessSitsExactlyOnGiveAnIndexOfZero()
    {
        var summary = Summary(15500, 100, 14500, 15500);

        Assert.Equal(0, summary.Cpu!.Value, Tolerance);
        Assert.Equal(0, summary.Cpk!.Value, Tolerance);
    }

    // 13
    [Fact]
    public void ANegativeCountIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CapabilitySummary.Compute(-1, 0, 1, 1, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => CapabilitySummary.Compute(1, -1, 1, 1, 0, 2));
    }
}
