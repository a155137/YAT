using YAT.Application.Graphs;
using YAT.Application.Specifications;

namespace YAT.Application.Tests;

// The shared specification: every value optional, finite when given, LSL < USL, and a target on or between the limits
// it has. Nothing here is graph-specific; the capability-only rule "at least one limit" is deliberately absent.
public class SpecificationTests
{
    // ---- Model ----

    [Fact]
    public void NoneHasNoValuesAndIsEmpty()
    {
        Assert.Null(Specification.None.LowerLimit);
        Assert.Null(Specification.None.Target);
        Assert.Null(Specification.None.UpperLimit);
        Assert.True(Specification.None.IsEmpty);
        Assert.Equal(Specification.None, new Specification());
    }

    [Theory]
    [InlineData(1.0, null, null)]
    [InlineData(null, 1.0, null)]
    [InlineData(null, null, 1.0)]
    public void AnyOneValueMakesASpecificationNonEmpty(double? lower, double? target, double? upper) =>
        Assert.False(new Specification(lower, target, upper).IsEmpty);

    [Fact]
    public void ASpecificationComparesByValue()
    {
        Assert.Equal(new Specification(14.5, 15, 15.5), new Specification(14.5, 15, 15.5));
        Assert.NotEqual(new Specification(14.5, 15, 15.5), new Specification(14.5, null, 15.5));
    }

    [Fact]
    public void AGraphConfigurationStartsWithoutASpecification()
    {
        var configuration = new GraphConfiguration(GraphType.Histogram, Guid.NewGuid(), []);

        Assert.Same(Specification.None, configuration.Specification);
    }

    [Fact]
    public void AGraphConfigurationCarriesItsSpecificationApartFromThePresentationOptions()
    {
        var configuration = new GraphConfiguration(GraphType.Histogram, Guid.NewGuid(), [])
        {
            Specification = new Specification(1, 2, 3)
        };

        Assert.Equal(new Specification(1, 2, 3), configuration.Specification);
        Assert.Equal(GraphPresentationOptions.Default, configuration.PresentationOptions);
    }

    // ---- Rules: what is valid ----

    public static TheoryData<double?, double?, double?> ValidSpecifications => new()
    {
        { null, null, null },            // none
        { 14.5, null, null },            // LSL only
        { null, null, 15.5 },            // USL only
        { null, 15, null },              // Target only
        { 14.5, 15, 15.5 },              // all three
        { 14.5, null, 15.5 },            // both limits
        { 14.5, 14.5, 15.5 },            // Target on LSL
        { 14.5, 15.5, 15.5 },            // Target on USL
        { 14.5, 1e9, null },             // Target above a lower limit only
        { null, -1e9, 15.5 },            // Target below an upper limit only
        { -3, 0, 0.001 },                // negative and zero
        { 1e-300, 2e-300, 3e-300 },      // tiny
        { -1.7e308, 0, 1.7e308 }         // huge
    };

    [Theory]
    [MemberData(nameof(ValidSpecifications))]
    public void ValidSpecificationsHaveNoProblems(double? lower, double? target, double? upper)
    {
        var specification = new Specification(lower, target, upper);

        Assert.Empty(SpecificationRules.Check(specification));
        Assert.True(SpecificationRules.IsValid(specification));
    }

    // ---- Rules: what is not ----

    [Fact]
    public void EqualLimitsAreOutOfOrder() =>
        AssertSingle(new Specification(15, null, 15), SpecificationProblemKind.LimitsOutOfOrder, SpecificationField.UpperLimit);

    [Fact]
    public void ALowerLimitAboveTheUpperOneIsOutOfOrder() =>
        AssertSingle(new Specification(16, null, 15), SpecificationProblemKind.LimitsOutOfOrder, SpecificationField.UpperLimit);

    [Fact]
    public void ATargetBelowTheLowerLimitIsOutside() =>
        AssertSingle(new Specification(14.5, 14.4, 15.5), SpecificationProblemKind.TargetOutsideLimits, SpecificationField.Target);

    [Fact]
    public void ATargetAboveTheUpperLimitIsOutside() =>
        AssertSingle(new Specification(14.5, 15.6, 15.5), SpecificationProblemKind.TargetOutsideLimits, SpecificationField.Target);

    [Fact]
    public void ATargetBelowAOneSidedLowerLimitIsOutside() =>
        AssertSingle(new Specification(14.5, 14, null), SpecificationProblemKind.TargetOutsideLimits, SpecificationField.Target);

    [Fact]
    public void ATargetAboveAOneSidedUpperLimitIsOutside() =>
        AssertSingle(new Specification(null, 16, 15.5), SpecificationProblemKind.TargetOutsideLimits, SpecificationField.Target);

    public static TheoryData<double> NonFinite => new() { double.NaN, double.PositiveInfinity, double.NegativeInfinity };

    [Theory]
    [MemberData(nameof(NonFinite))]
    public void NonFiniteValuesAreReportedForTheirField(double value)
    {
        AssertSingle(new Specification(value, null, null), SpecificationProblemKind.NotFinite, SpecificationField.LowerLimit);
        AssertSingle(new Specification(null, value, null), SpecificationProblemKind.NotFinite, SpecificationField.Target);
        AssertSingle(new Specification(null, null, value), SpecificationProblemKind.NotFinite, SpecificationField.UpperLimit);
    }

    [Fact]
    public void ANonFiniteValueTakesNoPartInTheOrderingRules()
    {
        // An infinite USL is reported as not finite, not also as "the target is above it" or "out of order".
        AssertSingle(new Specification(15, 16, double.NegativeInfinity), SpecificationProblemKind.NotFinite, SpecificationField.UpperLimit);
    }

    [Fact]
    public void EveryProblemIsReported()
    {
        var problems = SpecificationRules.Check(new Specification(16, 10, 15));

        Assert.Equal(
            [
                new SpecificationProblem(SpecificationProblemKind.LimitsOutOfOrder, SpecificationField.UpperLimit),
                new SpecificationProblem(SpecificationProblemKind.TargetOutsideLimits, SpecificationField.Target)
            ],
            problems);
    }

    private static void AssertSingle(Specification specification, SpecificationProblemKind kind, SpecificationField field)
    {
        var problem = Assert.Single(SpecificationRules.Check(specification));
        Assert.Equal(kind, problem.Kind);
        Assert.Equal(field, problem.Field);
        Assert.False(SpecificationRules.IsValid(specification));
    }
}
