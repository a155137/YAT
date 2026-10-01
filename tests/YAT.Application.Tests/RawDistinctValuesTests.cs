using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

// The distinct values of a raw column (Task #049): typed, distinct, in the order given, Missing and "more than listed"
// reported apart from them.
public class RawDistinctValuesTests
{
    private static readonly Guid ColumnId = Guid.NewGuid();

    [Fact]
    public void NumericValuesKeepTheirOrderAndFlags()
    {
        var values = new NumericRawDistinctValues(ColumnId, [3, 1, 2], hasMissing: true, hasMore: false);

        Assert.Equal(ColumnId, values.ColumnId);
        Assert.Equal(WorksheetDataType.Numeric, values.DataType);
        Assert.Equal([3, 1, 2], values.Values);
        Assert.Equal(3, values.Count);
        Assert.True(values.HasMissing);
        Assert.False(values.HasMore);
    }

    [Fact]
    public void TextValuesKeepTheirOrderAndFlags()
    {
        var values = new StringRawDistinctValues(ColumnId, ["b", "B", "a"], hasMissing: false, hasMore: true);

        Assert.Equal(WorksheetDataType.String, values.DataType);
        Assert.Equal(["b", "B", "a"], values.Values);
        Assert.Equal(3, values.Count);
        Assert.False(values.HasMissing);
        Assert.True(values.HasMore);
    }

    [Fact]
    public void AColumnMayHaveNoValues()
    {
        Assert.Equal(0, new NumericRawDistinctValues(ColumnId, [], hasMissing: true, hasMore: false).Count);
        Assert.Equal(0, new StringRawDistinctValues(ColumnId, [], hasMissing: false, hasMore: false).Count);
    }

    [Fact]
    public void ListedValuesAreDistinct()
    {
        Assert.Throws<ArgumentException>(() => new NumericRawDistinctValues(ColumnId, [1, 1], false, false));
        Assert.Throws<ArgumentException>(() => new NumericRawDistinctValues(ColumnId, [0.0, -0.0], false, false));
        Assert.Throws<ArgumentException>(() => new StringRawDistinctValues(ColumnId, ["A", "A"], false, false));
    }

    [Fact]
    public void ListedValuesAreValues()
    {
        Assert.Throws<ArgumentException>(() => new NumericRawDistinctValues(ColumnId, [double.NaN], false, false));
        Assert.Throws<ArgumentException>(() => new StringRawDistinctValues(ColumnId, [null!], false, false));
        Assert.Throws<ArgumentNullException>(() => new NumericRawDistinctValues(ColumnId, null!, false, false));
        Assert.Throws<ArgumentNullException>(() => new StringRawDistinctValues(ColumnId, null!, false, false));
    }

    [Fact]
    public void TheValuesAreACopy()
    {
        var source = new List<string> { "A" };
        var values = new StringRawDistinctValues(ColumnId, source, false, false);

        source.Add("B");

        Assert.Equal(["A"], values.Values);
    }
}
