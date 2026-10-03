using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Enums;

namespace YAT.Application.Filtering;

// A row filter condition on a set of a column's own values (Task #049, generalized in #053): "is any of" keeps the rows
// whose value is one of the selected values - and, when IncludeMissing is set, the rows where the column has no value;
// "is not any of" (Exclude) keeps every other row. The selection is a set of tokens - the values and, optionally,
// Missing - so "is not any of {3}" keeps the rows without a value: Missing is excluded only when it is selected itself.
//
// The column is any Numeric or String column of the worksheet; it need not be one of the graph's or analysis's own. The
// selected values keep the column's own type - numbers as numbers, text as text - and are never compared as the text
// they are shown as: numbers match by exact double equality (-0 and 0 are one value), text by ordinal comparison, and a
// missing value is not a value at all but IncludeMissing.
//
// Two conditions are equal when they keep the same rows: the same column, the same values in any order, the same
// IncludeMissing and the same Exclude.
public abstract record ValueSetCondition : RowFilterCondition
{
    // The most distinct values a column may have for its values to be offered one by one (see
    // IWorksheetRawDataStore.GetDistinctValuesAsync): a column with more is not a category to pick from.
    public const int MaximumDistinctValues = 1_000;

    private protected ValueSetCondition(Guid columnId, bool includeMissing, bool exclude)
        : base(columnId)
    {
        IncludeMissing = includeMissing;
        Exclude = exclude;
    }

    // Whether Missing is one of the selected tokens: kept by "is any of", excluded by "is not any of".
    public bool IncludeMissing { get; }

    // "is not any of": the rows whose token is not selected are kept.
    public bool Exclude { get; }

    // How many values are selected, Missing not counted.
    public abstract int ValueCount { get; }

    // Nothing selected - no value and not Missing: a condition that would keep no row (or, excluding nothing, every
    // row), which is not a valid condition.
    public bool IsEmpty => ValueCount == 0 && !IncludeMissing;

    // Whether a row without a value in the column is kept.
    public bool MatchesMissing => IncludeMissing != Exclude;

    // The condition that means what this one means over a column's values: null - no condition, every row - for an
    // "is any of" that selects every value the column has and its missing values too (or the column has none), the
    // condition itself otherwise. Only a complete list of the column's values can tell: a column with more values than
    // were listed (available.HasMore) is never known to be covered, so its condition is always kept. "is not any of" is
    // never dropped.
    public static ValueSetCondition? Canonicalize(ValueSetCondition? condition, RawDistinctValues available)
    {
        ArgumentNullException.ThrowIfNull(available);

        if (condition is null)
        {
            return null;
        }

        if (condition.ColumnId != available.ColumnId)
        {
            throw new ArgumentException("The values must be of the condition's own column.", nameof(available));
        }

        if (condition.Exclude || available.HasMore || (available.HasMissing && !condition.IncludeMissing))
        {
            return condition;
        }

        return condition.Covers(available) ? null : condition;
    }

    // The same, over the values the setup was shown for the column.
    public static ValueSetCondition? Canonicalize(ValueSetCondition? condition, FilterValues available)
    {
        ArgumentNullException.ThrowIfNull(available);
        return Canonicalize(condition, available.Raw);
    }

    // Whether every value listed for the column is selected.
    private protected abstract bool Covers(RawDistinctValues available);

    public virtual bool Equals(ValueSetCondition? other) =>
        other is not null
        && EqualityContract == other.EqualityContract
        && ColumnId == other.ColumnId
        && IncludeMissing == other.IncludeMissing
        && Exclude == other.Exclude;

    public override int GetHashCode() => HashCode.Combine(EqualityContract, ColumnId, IncludeMissing, Exclude);
}

// The selected values of a Numeric column. Values are finite and distinct (-0 and 0 are one value, as they are equal).
public sealed record NumericValueSetCondition : ValueSetCondition
{
    private readonly HashSet<double> _set;

    public NumericValueSetCondition(Guid columnId, IReadOnlyList<double> values, bool includeMissing = false, bool exclude = false)
        : base(columnId, includeMissing, exclude)
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

    // Whether this value is selected.
    public bool Contains(double value) => _set.Contains(value);

    // Whether a row with this value (null: no value) is kept.
    public bool Matches(double? value) => value is { } number ? _set.Contains(number) != Exclude : MatchesMissing;

    private protected override bool Covers(RawDistinctValues available) =>
        available is NumericRawDistinctValues numeric && numeric.Values.All(_set.Contains);

    public bool Equals(NumericValueSetCondition? other) =>
        base.Equals(other) && _set.SetEquals(other._set);

    // Order-independent, like the equality: the values' hashes are combined without regard to their order.
    public override int GetHashCode() =>
        HashCode.Combine(base.GetHashCode(), Values.Count, Values.Aggregate(0, (hash, value) => hash ^ value.GetHashCode()));
}

// The selected values of a String column, compared ordinally: "a" and "A", "1" and "01" are different values. Values are
// distinct and never null (an empty cell is IncludeMissing).
public sealed record TextValueSetCondition : ValueSetCondition
{
    private readonly HashSet<string> _set;

    public TextValueSetCondition(Guid columnId, IReadOnlyList<string> values, bool includeMissing = false, bool exclude = false)
        : base(columnId, includeMissing, exclude)
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

    // Whether this value is selected.
    public bool Contains(string value) => _set.Contains(value);

    // Whether a row with this value (null: no value) is kept.
    public bool Matches(string? value) => value is not null ? _set.Contains(value) != Exclude : MatchesMissing;

    private protected override bool Covers(RawDistinctValues available) =>
        available is StringRawDistinctValues text && text.Values.All(_set.Contains);

    public bool Equals(TextValueSetCondition? other) =>
        base.Equals(other) && _set.SetEquals(other._set);

    // Order-independent, like the equality.
    public override int GetHashCode() =>
        HashCode.Combine(
            base.GetHashCode(),
            Values.Count,
            Values.Aggregate(0, (hash, value) => hash ^ StringComparer.Ordinal.GetHashCode(value)));
}
