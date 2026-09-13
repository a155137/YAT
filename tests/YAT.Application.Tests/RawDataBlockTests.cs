using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

public class RawDataBlockTests
{
    [Fact]
    public void NumericColumnAlwaysReportsNumeric()
    {
        var column = new NumericRawDataColumn(Guid.NewGuid(), [1, 0.132, null]);

        Assert.Equal(WorksheetDataType.Numeric, column.DataType);
    }

    [Fact]
    public void StringColumnAlwaysReportsString()
    {
        var column = new StringRawDataColumn(Guid.NewGuid(), ["N123", null]);

        Assert.Equal(WorksheetDataType.String, column.DataType);
    }

    [Fact]
    public void NumericColumnPreservesIdValuesAndEmptyCells()
    {
        var columnId = Guid.NewGuid();

        var column = new NumericRawDataColumn(columnId, [5, null, 2.5E-2, -0.123]);

        Assert.Equal(columnId, column.ColumnId);
        Assert.Equal(4, column.RowCount);
        Assert.Equal([5, null, 2.5E-2, -0.123], column.Values);
    }

    [Fact]
    public void StringColumnPreservesIdValuesAndEmptyCells()
    {
        var columnId = Guid.NewGuid();

        var column = new StringRawDataColumn(columnId, ["N123", null, "", "N124"]);

        Assert.Equal(columnId, column.ColumnId);
        Assert.Equal(4, column.RowCount);
        Assert.Equal(["N123", null, "", "N124"], column.Values);
    }

    [Fact]
    public void ColumnsCopyTheSuppliedValues()
    {
        var numbers = new List<double?> { 1, 2 };
        var texts = new List<string?> { "A", "B" };
        var numeric = new NumericRawDataColumn(Guid.NewGuid(), numbers);
        var text = new StringRawDataColumn(Guid.NewGuid(), texts);

        numbers.Add(3);
        numbers[0] = 99;
        texts.Clear();

        Assert.Equal([1, 2], numeric.Values);
        Assert.Equal(["A", "B"], text.Values);
    }

    [Fact]
    public void ColumnsRejectNullValues()
    {
        Assert.Throws<ArgumentNullException>(() => new NumericRawDataColumn(Guid.NewGuid(), null!));
        Assert.Throws<ArgumentNullException>(() => new StringRawDataColumn(Guid.NewGuid(), null!));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NumericColumnRejectsNonFiniteValues(double value)
    {
        var exception = Assert.Throws<ArgumentException>(() => new NumericRawDataColumn(Guid.NewGuid(), [1, null, value]));

        Assert.Equal("values", exception.ParamName);
        Assert.Contains("Row 2", exception.Message);
    }

    [Fact]
    public void NumericColumnAcceptsFiniteExtremesAndMissingValues()
    {
        var column = new NumericRawDataColumn(Guid.NewGuid(), [double.MaxValue, double.MinValue, double.Epsilon, -0.0, null]);

        Assert.Equal(5, column.RowCount);
        Assert.Null(column.Values[4]);
    }

    [Fact]
    public void BlockAcceptsRectangularColumnsOfMixedTypes()
    {
        var no = new NumericRawDataColumn(Guid.NewGuid(), [1, 2, 3]);
        var lot = new StringRawDataColumn(Guid.NewGuid(), ["N123", "N123", null]);

        var block = new RawDataBlock([no, lot]);

        Assert.Equal(3, block.RowCount);
        Assert.Equal([no, lot], block.Columns);
    }

    [Fact]
    public void BlockAcceptsColumnsWithoutRows()
    {
        var block = new RawDataBlock([new NumericRawDataColumn(Guid.NewGuid(), [])]);

        Assert.Equal(0, block.RowCount);
    }

    [Fact]
    public void BlockCopiesTheSuppliedColumnList()
    {
        var columns = new List<RawDataColumn> { new NumericRawDataColumn(Guid.NewGuid(), [1]) };
        var block = new RawDataBlock(columns);

        columns.Add(new NumericRawDataColumn(Guid.NewGuid(), [2]));

        Assert.Single(block.Columns);
    }

    [Fact]
    public void BlockRejectsColumnsWithDifferentRowCounts()
    {
        var reg1 = new NumericRawDataColumn(Guid.NewGuid(), [1, 2, 3]);
        var reg2 = new NumericRawDataColumn(Guid.NewGuid(), [1, 2]);

        Assert.Throws<ArgumentException>(() => new RawDataBlock([reg1, reg2]));
    }

    [Fact]
    public void BlockRejectsDuplicateColumnIds()
    {
        var columnId = Guid.NewGuid();
        var first = new NumericRawDataColumn(columnId, [1]);
        var second = new StringRawDataColumn(columnId, ["A"]);

        Assert.Throws<ArgumentException>(() => new RawDataBlock([first, second]));
    }

    [Fact]
    public void BlockRejectsEmptyColumnList()
    {
        Assert.Throws<ArgumentException>(() => new RawDataBlock([]));
    }

    [Fact]
    public void BlockRejectsNullColumns()
    {
        Assert.Throws<ArgumentNullException>(() => new RawDataBlock(null!));
        Assert.Throws<ArgumentException>(() => new RawDataBlock([null!]));
    }
}
