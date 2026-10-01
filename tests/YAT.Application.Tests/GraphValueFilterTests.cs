using YAT.Application.Abstractions.Persistence;
using YAT.Application.Graphs;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// The graph value filter (Task #049): typed values, Missing apart from them, equality by the rows a filter keeps, and the
// canonical form of a filter that keeps every row.
public class GraphValueFilterTests
{
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly Guid Lot = Guid.NewGuid();

    // ---- Construction ----

    [Fact]
    public void NumericFilterKeepsItsValuesInOrderAndItsColumn()
    {
        var filter = new NumericValueFilter(Site, [3, 1, 2], includeMissing: true);

        Assert.Equal(Site, filter.ColumnId);
        Assert.Equal([3, 1, 2], filter.Values);
        Assert.True(filter.IncludeMissing);
        Assert.Equal(3, filter.ValueCount);
        Assert.Equal(WorksheetDataType.Numeric, filter.DataType);
        Assert.False(filter.IsEmpty);
    }

    [Fact]
    public void TextFilterKeepsItsValuesInOrderAndItsColumn()
    {
        var filter = new TextValueFilter(Lot, ["B", "A"]);

        Assert.Equal(Lot, filter.ColumnId);
        Assert.Equal(["B", "A"], filter.Values);
        Assert.False(filter.IncludeMissing);
        Assert.Equal(2, filter.ValueCount);
        Assert.Equal(WorksheetDataType.String, filter.DataType);
    }

    [Fact]
    public void IncludeMissingIsOffUnlessAsked()
    {
        Assert.False(new NumericValueFilter(Site, [1]).IncludeMissing);
        Assert.False(new TextValueFilter(Lot, ["A"]).IncludeMissing);
    }

    [Fact]
    public void TheFilterDoesNotShareTheCallersList()
    {
        var values = new List<double> { 1, 2 };
        var filter = new NumericValueFilter(Site, values);

        values.Add(3);

        Assert.Equal([1, 2], filter.Values);
        Assert.False(filter.Contains(3));
    }

    [Fact]
    public void NothingSelectedIsEmpty()
    {
        Assert.True(new NumericValueFilter(Site, []).IsEmpty);
        Assert.True(new TextValueFilter(Lot, []).IsEmpty);
    }

    [Fact]
    public void OnlyMissingSelectedIsNotEmpty()
    {
        var filter = new TextValueFilter(Lot, [], includeMissing: true);

        Assert.False(filter.IsEmpty);
        Assert.Equal(0, filter.ValueCount);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANumberThatIsNotFiniteCannotBeSelected(double value)
    {
        Assert.Throws<ArgumentException>(() => new NumericValueFilter(Site, [1, value]));
    }

    [Fact]
    public void ANumberCannotBeSelectedTwice()
    {
        Assert.Throws<ArgumentException>(() => new NumericValueFilter(Site, [1, 2, 1]));
    }

    [Fact]
    public void MinusZeroAndZeroAreOneValue()
    {
        Assert.Throws<ArgumentException>(() => new NumericValueFilter(Site, [0.0, -0.0]));

        var filter = new NumericValueFilter(Site, [-0.0]);
        Assert.True(filter.Contains(0.0));
        Assert.True(filter.Contains(-0.0));
    }

    [Fact]
    public void TextCannotBeSelectedTwice()
    {
        Assert.Throws<ArgumentException>(() => new TextValueFilter(Lot, ["A", "A"]));
    }

    [Fact]
    public void TextThatDiffersOrdinallyIsTwoValues()
    {
        var filter = new TextValueFilter(Lot, ["a", "A", "1", "01", "a "]);

        Assert.Equal(5, filter.ValueCount);
        Assert.True(filter.Contains("A"));
        Assert.False(filter.Contains("b"));
        Assert.False(filter.Contains(" a"));
    }

    [Fact]
    public void AMissingValueIsNotATextValue()
    {
        Assert.Throws<ArgumentException>(() => new TextValueFilter(Lot, ["A", null!]));
    }

    [Fact]
    public void ValuesAreRequired()
    {
        Assert.Throws<ArgumentNullException>(() => new NumericValueFilter(Site, null!));
        Assert.Throws<ArgumentNullException>(() => new TextValueFilter(Lot, null!));
    }

    [Fact]
    public void NumbersMatchByExactEquality()
    {
        var filter = new NumericValueFilter(Site, [1.0, 0.1 + 0.2]);

        Assert.True(filter.Contains(1.0));
        Assert.True(filter.Contains(0.1 + 0.2));
        Assert.False(filter.Contains(0.3));
        Assert.False(filter.Contains(1.0000000000000002));
    }

    // ---- Equality ----

    [Fact]
    public void FiltersThatKeepTheSameRowsAreEqualWhateverTheOrderOfTheirValues()
    {
        var first = new NumericValueFilter(Site, [1, 3, 5], includeMissing: true);
        var second = new NumericValueFilter(Site, [5, 1, 3], includeMissing: true);

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal<GraphValueFilter>(first, second);

        Assert.Equal(new TextValueFilter(Lot, ["A", "B"]), new TextValueFilter(Lot, ["B", "A"]));
        Assert.Equal(
            new TextValueFilter(Lot, ["A", "B"]).GetHashCode(),
            new TextValueFilter(Lot, ["B", "A"]).GetHashCode());
    }

    [Fact]
    public void FiltersOfOtherValuesColumnsOrMissingAreNotEqual()
    {
        var filter = new NumericValueFilter(Site, [1, 2]);

        Assert.NotEqual(filter, new NumericValueFilter(Site, [1, 3]));
        Assert.NotEqual(filter, new NumericValueFilter(Site, [1]));
        Assert.NotEqual(filter, new NumericValueFilter(Site, [1, 2, 3]));
        Assert.NotEqual(filter, new NumericValueFilter(Lot, [1, 2]));
        Assert.NotEqual(filter, new NumericValueFilter(Site, [1, 2], includeMissing: true));
        Assert.NotEqual(new TextValueFilter(Lot, ["a"]), new TextValueFilter(Lot, ["A"]));
    }

    [Fact]
    public void ANumericAndATextFilterAreNeverEqual()
    {
        GraphValueFilter numeric = new NumericValueFilter(Site, [], includeMissing: true);
        GraphValueFilter text = new TextValueFilter(Site, [], includeMissing: true);

        Assert.NotEqual(numeric, text);
        Assert.False(numeric.Equals(text));
        Assert.False(text.Equals(numeric));
    }

    [Fact]
    public void AConfigurationHasNoFilterUnlessGivenOne()
    {
        var configuration = new GraphConfiguration(GraphType.Histogram, Guid.NewGuid(), []);

        Assert.Null(configuration.Filter);
        var filtered = configuration with { Filter = new NumericValueFilter(Site, [2]) };

        Assert.Equal(new NumericValueFilter(Site, [2]), filtered.Filter);
        Assert.Null(configuration.Filter);
    }

    [Fact]
    public void EachVariablesOwnGraphKeepsTheFilter()
    {
        var reg1 = Guid.NewGuid();
        var reg2 = Guid.NewGuid();
        var filter = new NumericValueFilter(Site, [1, 3]);
        var configuration = new GraphConfiguration(GraphType.Histogram, Guid.NewGuid(),
        [
            new GraphColumnAssignment(GraphVariableRole.Variable, reg1),
            new GraphColumnAssignment(GraphVariableRole.Variable, reg2)
        ])
        {
            Filter = filter
        };

        Assert.Same(filter, GraphSetupRequest.ForVariable(configuration, reg2).Filter);
    }

    // ---- Canonical form ----

    [Fact]
    public void NoFilterStaysNoFilter()
    {
        Assert.Null(GraphValueFilter.Canonicalize(null, new NumericRawDistinctValues(Site, [1, 2], hasMissing: true, hasMore: false)));
    }

    [Fact]
    public void EveryValueAndMissingSelectedIsNoFilter()
    {
        var available = new NumericRawDistinctValues(Site, [1, 2, 3], hasMissing: true, hasMore: false);

        Assert.Null(GraphValueFilter.Canonicalize(new NumericValueFilter(Site, [3, 2, 1], includeMissing: true), available));
    }

    [Fact]
    public void EveryValueSelectedOfAColumnWithoutMissingValuesIsNoFilter()
    {
        var available = new StringRawDistinctValues(Lot, ["A", "B"], hasMissing: false, hasMore: false);

        Assert.Null(GraphValueFilter.Canonicalize(new TextValueFilter(Lot, ["A", "B"]), available));
        Assert.Null(GraphValueFilter.Canonicalize(new TextValueFilter(Lot, ["A", "B"], includeMissing: true), available));
    }

    [Fact]
    public void EveryValueSelectedButNotMissingStaysAFilter()
    {
        var available = new NumericRawDistinctValues(Site, [1, 2], hasMissing: true, hasMore: false);
        var filter = new NumericValueFilter(Site, [1, 2]);

        Assert.Same(filter, GraphValueFilter.Canonicalize(filter, available));
    }

    [Fact]
    public void SomeValuesSelectedStaysAFilter()
    {
        var available = new StringRawDistinctValues(Lot, ["A", "B", "C"], hasMissing: false, hasMore: false);
        var filter = new TextValueFilter(Lot, ["A", "C"], includeMissing: true);

        Assert.Same(filter, GraphValueFilter.Canonicalize(filter, available));
    }

    [Fact]
    public void AColumnWithMoreValuesThanListedIsNeverCovered()
    {
        var available = new NumericRawDistinctValues(Site, [1, 2], hasMissing: false, hasMore: true);
        var filter = new NumericValueFilter(Site, [1, 2], includeMissing: true);

        Assert.Same(filter, GraphValueFilter.Canonicalize(filter, available));
    }

    [Fact]
    public void ValuesTheColumnNoLongerHasDoNotKeepAFilterThatCoversTheRest()
    {
        var available = new NumericRawDistinctValues(Site, [1, 2], hasMissing: false, hasMore: false);

        Assert.Null(GraphValueFilter.Canonicalize(new NumericValueFilter(Site, [1, 2, 9]), available));
    }

    [Fact]
    public void OnlyMissingOfAColumnWithoutValuesIsNoFilter()
    {
        var available = new StringRawDistinctValues(Lot, [], hasMissing: true, hasMore: false);

        Assert.Null(GraphValueFilter.Canonicalize(new TextValueFilter(Lot, [], includeMissing: true), available));
    }

    [Fact]
    public void ValuesOfAnotherTypeNeverCoverTheColumn()
    {
        var available = new StringRawDistinctValues(Site, ["1"], hasMissing: false, hasMore: false);
        var filter = new NumericValueFilter(Site, [1]);

        Assert.Same(filter, GraphValueFilter.Canonicalize(filter, available));
    }

    [Fact]
    public void ValuesOfAnotherColumnAreRefused()
    {
        var available = new NumericRawDistinctValues(Lot, [1], hasMissing: false, hasMore: false);

        Assert.Throws<ArgumentException>(() => GraphValueFilter.Canonicalize(new NumericValueFilter(Site, [1]), available));
    }

    [Fact]
    public void TheMostDistinctValuesOfferedIsOneThousand()
    {
        Assert.Equal(1_000, GraphValueFilter.MaximumDistinctValues);
    }
}
