using YAT.Application.Abstractions.Persistence;
using YAT.Application.Filtering;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// The row filter of Task #053 on its own: every operator and exactly what it keeps - Missing included - the conditions
// combined with AND, kept as given, the rules a filter is validated by, and "every value selected" turned into no
// condition only when the column's list of values is known to be complete.
public class RowFilterTests
{
    private static readonly Guid Current = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly Guid Bin = Guid.NewGuid();
    private static readonly Guid Tester = Guid.NewGuid();

    // ---- Numeric comparisons ----

    [Theory]
    [InlineData(NumericComparison.Equal, new[] { false, true, false })]
    [InlineData(NumericComparison.NotEqual, new[] { true, false, true })]
    [InlineData(NumericComparison.Less, new[] { true, false, false })]
    [InlineData(NumericComparison.LessOrEqual, new[] { true, true, false })]
    [InlineData(NumericComparison.Greater, new[] { false, false, true })]
    [InlineData(NumericComparison.GreaterOrEqual, new[] { false, true, true })]
    public void EachComparisonComparesExactlyAndNeverMatchesMissing(NumericComparison comparison, bool[] below_at_above)
    {
        var condition = new NumericComparisonCondition(Current, comparison, 14.5);

        Assert.Equal(below_at_above, new double?[] { 14.4, 14.5, 14.6 }.Select(condition.Matches));
        Assert.False(condition.Matches(null));
    }

    [Fact]
    public void NotEqualDoesNotMatchMissing()
    {
        Assert.False(new NumericComparisonCondition(Current, NumericComparison.NotEqual, 1).Matches(null));
    }

    [Fact]
    public void NegativeZeroIsZero()
    {
        Assert.True(new NumericComparisonCondition(Current, NumericComparison.Equal, -0.0).Matches(0.0));
        Assert.True(new NumericComparisonCondition(Current, NumericComparison.Equal, 0.0).Matches(-0.0));
        Assert.True(new NumericValueSetCondition(Site, [-0.0]).Matches(0.0));
        Assert.Throws<ArgumentException>(() => new NumericValueSetCondition(Site, [0.0, -0.0]));
    }

    [Fact]
    public void EqualityIsExactWithoutTolerance()
    {
        var condition = new NumericComparisonCondition(Current, NumericComparison.Equal, 0.3);

        Assert.False(condition.Matches(0.1 + 0.2));
        Assert.True(condition.Matches(0.3));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANonFiniteNumberIsNoCondition(double value)
    {
        Assert.Throws<ArgumentException>(() => new NumericComparisonCondition(Current, NumericComparison.Equal, value));
        Assert.Throws<ArgumentException>(() => new NumericBetweenCondition(Current, value, 1));
        Assert.Throws<ArgumentException>(() => new NumericBetweenCondition(Current, 1, value));
        Assert.Throws<ArgumentException>(() => new NumericValueSetCondition(Site, [value]));
    }

    [Fact]
    public void BetweenIncludesBothEndsAndNeverMatchesMissing()
    {
        var condition = new NumericBetweenCondition(Current, 14.5, 15.5);

        Assert.Equal([false, true, true, true, false], new double?[] { 14.49, 14.5, 15, 15.5, 15.51 }.Select(condition.Matches));
        Assert.False(condition.Matches(null));
    }

    [Fact]
    public void BetweenOneNumberIsThatNumberAndALowerAboveTheUpperIsRefused()
    {
        Assert.True(new NumericBetweenCondition(Current, 15, 15).Matches(15));
        Assert.False(new NumericBetweenCondition(Current, 15, 15).Matches(15.000001));
        Assert.Throws<ArgumentException>(() => new NumericBetweenCondition(Current, 15.5, 14.5));
    }

    // ---- Value sets: set semantics, Missing a token of its own ----

    [Fact]
    public void IsAnyOfKeepsTheSelectedValuesAndMissingOnlyWhenSelected()
    {
        var sites = new NumericValueSetCondition(Site, [1, 3, 5, 7]);
        var withMissing = new NumericValueSetCondition(Site, [1], includeMissing: true);

        Assert.Equal([true, false, true, false], new double?[] { 1, 2, 3, null }.Select(sites.Matches));
        Assert.Equal([true, false, true], new double?[] { 1, 2, null }.Select(withMissing.Matches));
    }

    [Fact]
    public void IsNotAnyOfKeepsMissingUnlessMissingIsExcludedToo()
    {
        var notThree = new NumericValueSetCondition(Bin, [3], exclude: true);
        var notThreeOrMissing = new NumericValueSetCondition(Bin, [3], includeMissing: true, exclude: true);

        Assert.Equal([true, false, true], new double?[] { 1, 3, null }.Select(notThree.Matches));
        Assert.Equal([true, false, false], new double?[] { 1, 3, null }.Select(notThreeOrMissing.Matches));
    }

    [Fact]
    public void TextValueSetsAreOrdinalAndCaseSensitive()
    {
        var any = new TextValueSetCondition(Tester, ["T1", "a"]);
        var none = new TextValueSetCondition(Tester, ["T1"], exclude: true);

        Assert.Equal([true, false, true, false, false], new[] { "T1", "t1", "a", "A", null }.Select(any.Matches));
        Assert.Equal([false, true, true], new[] { "T1", "t1", null }.Select(none.Matches));
    }

    [Fact]
    public void AValueSetIsEqualWhateverItsOrderButNotAcrossIncludeAndExclude()
    {
        Assert.Equal(new NumericValueSetCondition(Site, [1, 3]), new NumericValueSetCondition(Site, [3, 1]));
        Assert.NotEqual(new NumericValueSetCondition(Site, [1, 3]), new NumericValueSetCondition(Site, [1, 3], exclude: true));
        Assert.NotEqual<RowFilterCondition>(new NumericValueSetCondition(Site, [1]), new NumericComparisonCondition(Site, NumericComparison.Equal, 1));
    }

    // ---- Text comparisons ----

    [Fact]
    public void IsAndIsNotAreExactAndNeverMatchMissing()
    {
        var @is = new TextComparisonCondition(Tester, "T1");
        var isNot = new TextComparisonCondition(Tester, "T1", negated: true);

        Assert.Equal([true, false, false, false], new[] { "T1", "t1", " T1", null }.Select(@is.Matches));
        Assert.Equal([false, true, true, false], new[] { "T1", "t1", " T1", null }.Select(isNot.Matches));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankTextIsNoCondition(string text)
    {
        Assert.Throws<ArgumentException>(() => new TextComparisonCondition(Tester, text));
    }

    // ---- The filter: AND, kept as given ----

    private static RawDataBlock Block(double?[] current, double?[] site, string?[] tester) =>
        new([new NumericRawDataColumn(Current, current), new NumericRawDataColumn(Site, site), new StringRawDataColumn(Tester, tester)]);

    private static bool[] Keeps(RowFilter filter, RawDataBlock block)
    {
        var positions = new Dictionary<Guid, int> { [Current] = 0, [Site] = 1, [Tester] = 2 };
        var bound = RowFilterEvaluator.Create(filter, id => positions.TryGetValue(id, out var position) ? position : -1).Bind(block);
        return [.. Enumerable.Range(0, block.RowCount).Select(bound.Keeps)];
    }

    [Fact]
    public void EveryConditionMustBeMet()
    {
        var filter = new RowFilter(
        [
            new NumericValueSetCondition(Site, [1, 3, 5, 7]),
            new NumericComparisonCondition(Current, NumericComparison.GreaterOrEqual, 14.5),
            new NumericComparisonCondition(Current, NumericComparison.LessOrEqual, 15.5),
            new TextComparisonCondition(Tester, "T2", negated: true)
        ]);
        var block = Block(
            current: [15, 15, 14, 16, 15, null, 15],
            site: [1, 2, 3, 5, 7, 1, null],
            tester: ["T1", "T1", "T1", "T1", "T2", "T1", "T1"]);

        Assert.Equal([true, false, false, false, false, false, false], Keeps(filter, block));
    }

    [Fact]
    public void ContradictoryConditionsKeepNoRowAndDuplicatesChangeNothing()
    {
        var block = Block([14, 15, 16], [1, 1, 1], ["T1", "T1", "T1"]);
        var contradictory = new RowFilter(
        [
            new NumericComparisonCondition(Current, NumericComparison.Greater, 15),
            new NumericComparisonCondition(Current, NumericComparison.Less, 15)
        ]);
        var once = new RowFilter(new NumericComparisonCondition(Current, NumericComparison.GreaterOrEqual, 15));
        var twice = new RowFilter([once.Conditions[0], once.Conditions[0]]);

        Assert.Equal([false, false, false], Keeps(contradictory, block));
        Assert.Equal(Keeps(once, block), Keeps(twice, block));
        Assert.Equal(2, twice.Conditions.Count);
        Assert.Equal([Current], twice.ColumnIds);
    }

    [Fact]
    public void AColumnThatIsNotReadIsMissingInEveryRow()
    {
        var missingOnly = new RowFilter(new NumericValueSetCondition(Bin, [3], exclude: true));
        var comparison = new RowFilter(new NumericComparisonCondition(Bin, NumericComparison.NotEqual, 3));
        var block = Block([1, 2], [1, 2], ["a", "b"]);

        Assert.Equal([true, true], Keeps(missingOnly, block));
        Assert.Equal([false, false], Keeps(comparison, block));
        Assert.True(RowFilterEvaluator.Create(missingOnly, _ => -1).KeepsRowWithoutValues);
        Assert.False(RowFilterEvaluator.Create(comparison, _ => -1).KeepsRowWithoutValues);
    }

    [Fact]
    public void AFilterHasConditionsKeptInTheirOrder()
    {
        RowFilterCondition first = new NumericComparisonCondition(Current, NumericComparison.Less, 1);
        RowFilterCondition second = new TextComparisonCondition(Tester, "x");

        Assert.Null(RowFilter.Of([]));
        Assert.Throws<ArgumentException>(() => new RowFilter(Array.Empty<RowFilterCondition>()));
        Assert.Equal(new RowFilter([first, second]), new RowFilter([first, second]));
        Assert.NotEqual(new RowFilter([first, second]), new RowFilter([second, first]));
        Assert.Equal(("All rows", "1 condition", "2 conditions"), (RowFilter.Describe(null), RowFilter.Describe(new RowFilter(first)), RowFilter.Describe(new RowFilter([first, second]))));
    }

    // ---- Validation ----

    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, WorksheetColumn> ColumnsById = new[]
    {
        new WorksheetColumn { Id = Current, WorksheetId = WorksheetId, Name = "Current", DataType = WorksheetDataType.Numeric },
        new WorksheetColumn { Id = Site, WorksheetId = WorksheetId, Name = "Site", DataType = WorksheetDataType.Numeric },
        new WorksheetColumn { Id = Tester, WorksheetId = WorksheetId, Name = "Tester", DataType = WorksheetDataType.String },
        new WorksheetColumn { Id = Bin, WorksheetId = Guid.NewGuid(), Name = "Bin", DataType = WorksheetDataType.Numeric }
    }.ToDictionary(column => column.Id);

    [Fact]
    public void TwentyConditionsAreAllowedAndTwentyOneAreNot()
    {
        RowFilter Of(int count) => new(Enumerable.Range(0, count).Select(index => new NumericComparisonCondition(Current, NumericComparison.Greater, index)));

        Assert.Empty(RowFilterValidator.Validate(Of(RowFilter.MaximumConditions), WorksheetId, ColumnsById));
        Assert.Equal(
            [new RowFilterProblem(RowFilterProblemKind.TooManyConditions)],
            RowFilterValidator.Validate(Of(RowFilter.MaximumConditions + 1), WorksheetId, ColumnsById));
    }

    [Fact]
    public void EachConditionIsCheckedAgainstTheColumns()
    {
        var gone = Guid.NewGuid();
        var filter = new RowFilter(
        [
            new NumericComparisonCondition(gone, NumericComparison.Equal, 1),
            new NumericComparisonCondition(Bin, NumericComparison.Equal, 1),
            new TextComparisonCondition(Current, "x"),
            new NumericValueSetCondition(Site, []),
            new TextValueSetCondition(Tester, ["T1"])
        ]);

        Assert.Equal(
            [
                new RowFilterProblem(RowFilterProblemKind.ColumnNotFound, 0, gone),
                new RowFilterProblem(RowFilterProblemKind.ColumnFromAnotherWorksheet, 1, Bin),
                new RowFilterProblem(RowFilterProblemKind.ColumnIncompatibleType, 2, Current),
                new RowFilterProblem(RowFilterProblemKind.SelectionEmpty, 3, Site)
            ],
            RowFilterValidator.Validate(filter, WorksheetId, ColumnsById));
    }

    // ---- Every value selected ----

    [Fact]
    public void EveryValueAndMissingOfACompleteListIsNoCondition()
    {
        var available = new NumericRawDistinctValues(Site, [1, 2], hasMissing: true, hasMore: false);

        Assert.Null(ValueSetCondition.Canonicalize(new NumericValueSetCondition(Site, [2, 1], includeMissing: true), available));
        Assert.NotNull(ValueSetCondition.Canonicalize(new NumericValueSetCondition(Site, [1, 2]), available));
        Assert.NotNull(ValueSetCondition.Canonicalize(new NumericValueSetCondition(Site, [1]), available));
    }

    [Fact]
    public void EveryValueShownOfATruncatedListIsNeverEveryRow()
    {
        var shown = Enumerable.Range(0, ValueSetCondition.MaximumDistinctValues).Select(index => (double)index).ToArray();
        var truncated = new NumericRawDistinctValues(Site, shown, hasMissing: false, hasMore: true);
        var complete = new NumericRawDistinctValues(Site, shown, hasMissing: false, hasMore: false);
        var everyValueShown = new NumericValueSetCondition(Site, shown);

        Assert.Same(everyValueShown, ValueSetCondition.Canonicalize(everyValueShown, truncated));
        Assert.Null(ValueSetCondition.Canonicalize(everyValueShown, complete));
    }

    [Fact]
    public void IsNotAnyOfIsNeverDropped()
    {
        var available = new NumericRawDistinctValues(Site, [1, 2], hasMissing: false, hasMore: false);
        var excludeAll = new NumericValueSetCondition(Site, [1, 2], exclude: true);

        Assert.Same(excludeAll, ValueSetCondition.Canonicalize(excludeAll, available));
    }
}
