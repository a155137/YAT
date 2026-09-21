using YAT.Application.Analyses;

namespace YAT.Application.Tests;

// Reading a specification limit the user typed: the invariant numeric rule YAT parses every number with, a blank side
// meaning "not specified", and no silent zeros.
public class SpecificationLimitParserTests
{
    // 0
    [Theory]
    [InlineData("14500", 14500d)]
    [InlineData("0.10", 0.1d)]
    [InlineData("-3.5", -3.5d)]
    [InlineData("1.57E-06", 1.57E-06)]
    [InlineData("  20  ", 20d)]
    public void ANumberIsRead(string text, double expected)
    {
        Assert.True(SpecificationLimitParser.TryParse(text, out var limit));
        Assert.Equal(expected, limit!.Value, 1e-12);
    }

    // 1
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankSideIsNoLimitRatherThanZero(string? text)
    {
        Assert.True(SpecificationLimitParser.TryParse(text, out var limit));
        Assert.Null(limit);
    }

    // 2
    [Theory]
    [InlineData("abc")]
    [InlineData("14,500")]
    [InlineData("1.2.3")]
    [InlineData("--5")]
    public void TextThatIsNotANumberIsAFailureAndNeverZero(string text)
    {
        Assert.False(SpecificationLimitParser.TryParse(text, out var limit));
        Assert.Null(limit);
    }

    // 3
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("∞")]
    public void NonFiniteValuesAreNotLimits(string text)
    {
        Assert.False(SpecificationLimitParser.TryParse(text, out var limit));
        Assert.Null(limit);
    }

    // 4
    [Fact]
    public void TheDecimalSeparatorIsTheInvariantOneWhateverTheMachineIsSetTo()
    {
        // A comma is a thousands separator in the invariant culture and is not accepted in a number.
        Assert.True(SpecificationLimitParser.TryParse("0.25", out var point));
        Assert.Equal(0.25, point!.Value, 1e-12);
        Assert.False(SpecificationLimitParser.TryParse("0,25", out _));
    }
}
