using YAT.Domain.Enums;

namespace YAT.Application.Abstractions.Persistence;

// The distinct values of one raw column, in the order they first occur in the worksheet (the row of each value's first
// occurrence), at most as many as were asked for (see IWorksheetRawDataStore.GetDistinctValuesAsync).
//
// Missing is not a value: HasMissing says whether some worksheet row has no value in the column - an empty cell, or a
// row beyond the column's own length within the worksheet's rows - and is reported whatever the limit. HasMore says
// that the column has more distinct values than were listed, so "exactly the limit" (HasMore false) and "more than the
// limit" (HasMore true) can be told apart; the values listed are then the first ones only, never a complete list.
// Derivation is limited to this assembly so a subtype always reports the data type its values have.
public abstract class RawDistinctValues
{
    private protected RawDistinctValues(Guid columnId, bool hasMissing, bool hasMore)
    {
        ColumnId = columnId;
        HasMissing = hasMissing;
        HasMore = hasMore;
    }

    public Guid ColumnId { get; }

    public abstract WorksheetDataType DataType { get; }

    // How many values are listed, Missing not counted.
    public abstract int Count { get; }

    public bool HasMissing { get; }

    public bool HasMore { get; }
}

// Distinct finite numbers, compared by exact equality.
public sealed class NumericRawDistinctValues : RawDistinctValues
{
    public NumericRawDistinctValues(Guid columnId, IReadOnlyList<double> values, bool hasMissing, bool hasMore)
        : base(columnId, hasMissing, hasMore)
    {
        ArgumentNullException.ThrowIfNull(values);

        var copy = values.ToArray();
        var seen = new HashSet<double>();
        foreach (var value in copy)
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentException($"A distinct value must be a finite number ({value}).", nameof(values));
            }

            if (!seen.Add(value))
            {
                throw new ArgumentException($"The value {value} is listed twice.", nameof(values));
            }
        }

        Values = Array.AsReadOnly(copy);
    }

    public override WorksheetDataType DataType => WorksheetDataType.Numeric;

    public override int Count => Values.Count;

    public IReadOnlyList<double> Values { get; }
}

// Distinct text values, compared ordinally. Never null: an empty cell is HasMissing.
public sealed class StringRawDistinctValues : RawDistinctValues
{
    public StringRawDistinctValues(Guid columnId, IReadOnlyList<string> values, bool hasMissing, bool hasMore)
        : base(columnId, hasMissing, hasMore)
    {
        ArgumentNullException.ThrowIfNull(values);

        var copy = values.ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in copy)
        {
            if (value is null)
            {
                throw new ArgumentException("A distinct value must not be null; a missing value is HasMissing.", nameof(values));
            }

            if (!seen.Add(value))
            {
                throw new ArgumentException($"The value '{value}' is listed twice.", nameof(values));
            }
        }

        Values = Array.AsReadOnly(copy);
    }

    public override WorksheetDataType DataType => WorksheetDataType.String;

    public override int Count => Values.Count;

    public IReadOnlyList<string> Values { get; }
}
