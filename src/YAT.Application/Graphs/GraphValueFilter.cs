using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Enums;

namespace YAT.Application.Graphs;

// Which worksheet rows a graph uses (Task #049): the rows whose value in one column is one of the selected values - and,
// when IncludeMissing is set, the rows where that column has no value. A graph without a filter (null) uses every row.
//
// The column is any Numeric or String column of the graph's worksheet; it need not be one of the graph's roles. The
// selected values keep the column's own type - numbers as numbers, text as text - and are never compared as the text a
// graph shows for them: numbers match by exact double equality, text by ordinal comparison, and a missing value is not a
// value at all but IncludeMissing.
//
// The filter is applied where the graph's rows are read (GraphDataQueryService), before anything is built from them, so
// every graph type, its statistics and its display sampling see only the rows it keeps. Nothing here reads worksheet
// values.
//
// Two filters are equal when they keep the same rows: the same column, the same values in any order, the same
// IncludeMissing.
public abstract record GraphValueFilter
{
    // The most distinct values a column may have for its values to be offered one by one (see
    // IWorksheetRawDataStore.GetDistinctValuesAsync): a column with more is not a category to pick from.
    public const int MaximumDistinctValues = 1_000;

    private protected GraphValueFilter(Guid columnId, bool includeMissing)
    {
        ColumnId = columnId;
        IncludeMissing = includeMissing;
    }

    // The worksheet column the rows are selected by.
    public Guid ColumnId { get; }

    // Whether rows where the column has no value are kept.
    public bool IncludeMissing { get; }

    // The data type of the column this filter's values are for.
    public abstract WorksheetDataType DataType { get; }

    // How many values are selected, Missing not counted.
    public abstract int ValueCount { get; }

    // Nothing selected - no value and not Missing: a filter that would keep no row, which is not a valid configuration.
    public bool IsEmpty => ValueCount == 0 && !IncludeMissing;

    // The filter that means what this one means over a column's values: null - every row - when it keeps every value the
    // column has and its missing values too (or the column has none), the filter itself otherwise. A column with more
    // values than were listed (available.HasMore) is never known to be covered, so its filter is kept.
    public static GraphValueFilter? Canonicalize(GraphValueFilter? filter, RawDistinctValues available)
    {
        ArgumentNullException.ThrowIfNull(available);

        if (filter is null)
        {
            return null;
        }

        if (filter.ColumnId != available.ColumnId)
        {
            throw new ArgumentException("The values must be of the filter's own column.", nameof(available));
        }

        if (available.HasMore || (available.HasMissing && !filter.IncludeMissing))
        {
            return filter;
        }

        return filter.Covers(available) ? null : filter;
    }

    // The same, over the values the setup was shown for the column.
    public static GraphValueFilter? Canonicalize(GraphValueFilter? filter, GraphFilterValues available)
    {
        ArgumentNullException.ThrowIfNull(available);
        return Canonicalize(filter, available.Raw);
    }

    // Whether every value listed for the column is selected.
    private protected abstract bool Covers(RawDistinctValues available);

    public virtual bool Equals(GraphValueFilter? other) =>
        other is not null
        && EqualityContract == other.EqualityContract
        && ColumnId == other.ColumnId
        && IncludeMissing == other.IncludeMissing;

    public override int GetHashCode() => HashCode.Combine(EqualityContract, ColumnId, IncludeMissing);
}

// The selected values of a Numeric column. Values are finite and distinct (-0 and 0 are one value, as they are equal).
public sealed record NumericValueFilter : GraphValueFilter
{
    private readonly HashSet<double> _set;

    public NumericValueFilter(Guid columnId, IReadOnlyList<double> values, bool includeMissing = false)
        : base(columnId, includeMissing)
    {
        ArgumentNullException.ThrowIfNull(values);

        var copy = values.ToArray();
        _set = [];
        foreach (var value in copy)
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentException($"A selected value must be a finite number ({value}).", nameof(values));
            }

            if (!_set.Add(value))
            {
                throw new ArgumentException($"The value {value} is selected twice.", nameof(values));
            }
        }

        Values = Array.AsReadOnly(copy);
    }

    // In the order they were given (the order the column's values are listed in).
    public IReadOnlyList<double> Values { get; }

    public override WorksheetDataType DataType => WorksheetDataType.Numeric;

    public override int ValueCount => Values.Count;

    // Whether a row with this value is kept.
    public bool Contains(double value) => _set.Contains(value);

    private protected override bool Covers(RawDistinctValues available) =>
        available is NumericRawDistinctValues numeric && numeric.Values.All(_set.Contains);

    public bool Equals(NumericValueFilter? other) =>
        base.Equals(other) && _set.SetEquals(other._set);

    // Order-independent, like the equality: the values' hashes are combined without regard to their order.
    public override int GetHashCode() =>
        HashCode.Combine(base.GetHashCode(), Values.Count, Values.Aggregate(0, (hash, value) => hash ^ value.GetHashCode()));
}

// The selected values of a String column, compared ordinally: "a" and "A", "1" and "01" are different values. Values are
// distinct and never null (an empty cell is IncludeMissing).
public sealed record TextValueFilter : GraphValueFilter
{
    private readonly HashSet<string> _set;

    public TextValueFilter(Guid columnId, IReadOnlyList<string> values, bool includeMissing = false)
        : base(columnId, includeMissing)
    {
        ArgumentNullException.ThrowIfNull(values);

        var copy = values.ToArray();
        _set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in copy)
        {
            if (value is null)
            {
                throw new ArgumentException("A selected value must not be null; select Missing instead.", nameof(values));
            }

            if (!_set.Add(value))
            {
                throw new ArgumentException($"The value '{value}' is selected twice.", nameof(values));
            }
        }

        Values = Array.AsReadOnly(copy);
    }

    // In the order they were given (the order the column's values are listed in).
    public IReadOnlyList<string> Values { get; }

    public override WorksheetDataType DataType => WorksheetDataType.String;

    public override int ValueCount => Values.Count;

    // Whether a row with this value is kept.
    public bool Contains(string value) => _set.Contains(value);

    private protected override bool Covers(RawDistinctValues available) =>
        available is StringRawDistinctValues text && text.Values.All(_set.Contains);

    public bool Equals(TextValueFilter? other) =>
        base.Equals(other) && _set.SetEquals(other._set);

    // Order-independent, like the equality.
    public override int GetHashCode() =>
        HashCode.Combine(
            base.GetHashCode(),
            Values.Count,
            Values.Aggregate(0, (hash, value) => hash ^ StringComparer.Ordinal.GetHashCode(value)));
}
