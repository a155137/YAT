using YAT.Domain.Enums;

namespace YAT.Application.Abstractions.Persistence;

// Typed raw values of one worksheet column, identified by WorksheetColumn.Id.
// Name, index and other metadata stay with IWorksheetColumnRepository. A null value is an empty cell.
// Derivation is limited to this assembly so a subtype always reports the data type its values have.
public abstract class RawDataColumn
{
    private protected RawDataColumn(Guid columnId)
    {
        ColumnId = columnId;
    }

    public Guid ColumnId { get; }

    public abstract WorksheetDataType DataType { get; }

    public abstract int RowCount { get; }
}

// Numeric values must be finite; a missing cell is null, never NaN or Infinity.
public sealed class NumericRawDataColumn : RawDataColumn
{
    public NumericRawDataColumn(Guid columnId, IReadOnlyList<double?> values)
        : base(columnId)
    {
        ArgumentNullException.ThrowIfNull(values);

        var copy = values.ToArray();
        for (var rowIndex = 0; rowIndex < copy.Length; rowIndex++)
        {
            if (copy[rowIndex] is { } value && !double.IsFinite(value))
            {
                throw new ArgumentException($"Row {rowIndex} contains a non-finite numeric value ({value}).", nameof(values));
            }
        }

        Values = Array.AsReadOnly(copy);
    }

    public override WorksheetDataType DataType => WorksheetDataType.Numeric;

    public override int RowCount => Values.Count;

    public IReadOnlyList<double?> Values { get; }
}

public sealed class StringRawDataColumn : RawDataColumn
{
    public StringRawDataColumn(Guid columnId, IReadOnlyList<string?> values)
        : base(columnId)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = Array.AsReadOnly(values.ToArray());
    }

    public override WorksheetDataType DataType => WorksheetDataType.String;

    public override int RowCount => Values.Count;

    public IReadOnlyList<string?> Values { get; }
}
