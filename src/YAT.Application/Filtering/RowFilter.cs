using YAT.Domain.Enums;

namespace YAT.Application.Filtering;

// Which worksheet rows a graph or an analysis uses (Task #053, the generalization of #049's value filter): the rows that
// meet every condition - the conditions are combined with AND. A graph or analysis without a filter (null) uses every
// row. The worksheet itself is never changed: a filter selects rows for one graph or analysis, nothing else.
//
// The conditions are kept as the user gave them, in their order: duplicates and contradictions are not merged or
// dropped (contradictions simply keep no row). At most MaximumConditions; RowFilterValidator says when a filter breaks
// a rule.
//
// Two filters are equal when they have equal conditions in the same order.
public sealed record RowFilter
{
    public const int MaximumConditions = 20;

    public RowFilter(IEnumerable<RowFilterCondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);

        var copy = conditions.ToArray();
        if (copy.Length == 0)
        {
            throw new ArgumentException("A filter needs at least one condition; no filter is null.", nameof(conditions));
        }

        if (copy.Any(condition => condition is null))
        {
            throw new ArgumentException("A condition must not be null.", nameof(conditions));
        }

        Conditions = Array.AsReadOnly(copy);
    }

    // One condition (an #049 filter, for example).
    public RowFilter(RowFilterCondition condition)
        : this([condition])
    {
    }

    public IReadOnlyList<RowFilterCondition> Conditions { get; }

    // The columns the conditions read, each once, in the order they are first used.
    public IReadOnlyList<Guid> ColumnIds => [.. Conditions.Select(condition => condition.ColumnId).Distinct()];

    // The filter of these conditions, or null - every row - when there are none.
    public static RowFilter? Of(IEnumerable<RowFilterCondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        var copy = conditions.ToArray();
        return copy.Length == 0 ? null : new RowFilter(copy);
    }

    // "All rows", "1 condition", "3 conditions": a filter in a few words, for a setup and for a result.
    public static string Describe(RowFilter? filter) =>
        filter is null ? "All rows" : filter.Conditions.Count == 1 ? "1 condition" : $"{filter.Conditions.Count} conditions";

    public bool Equals(RowFilter? other) => other is not null && Conditions.SequenceEqual(other.Conditions);

    public override int GetHashCode() => Conditions.Aggregate(Conditions.Count, HashCode.Combine);
}

// One condition of a row filter, on one worksheet column: whether a row is kept depends on that column's value in it -
// or on its having none. The data type is the column type the condition's values are of.
public abstract record RowFilterCondition
{
    private protected RowFilterCondition(Guid columnId)
    {
        ColumnId = columnId;
    }

    // The worksheet column the condition reads.
    public Guid ColumnId { get; }

    public abstract WorksheetDataType DataType { get; }
}

// How a numeric comparison compares a row's value with the condition's.
public enum NumericComparison
{
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual
}

// A Numeric column's value compared with a number: =, !=, <, <=, >, >= (Task #053). The number is finite; equality is
// exact double equality (-0 and 0 are equal; no tolerance). A row without a value never matches - not even "!=".
public sealed record NumericComparisonCondition : RowFilterCondition
{
    public NumericComparisonCondition(Guid columnId, NumericComparison comparison, double value)
        : base(columnId)
    {
        if (!Enum.IsDefined(comparison))
        {
            throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "Not a comparison.");
        }

        if (!double.IsFinite(value))
        {
            throw new ArgumentException($"A comparison needs a finite number ({value}).", nameof(value));
        }

        Comparison = comparison;
        Value = value;
    }

    public NumericComparison Comparison { get; }

    public double Value { get; }

    public override WorksheetDataType DataType => WorksheetDataType.Numeric;

    // Whether a row with this value (null: no value) is kept.
    public bool Matches(double? value) => value is { } number && Comparison switch
    {
        NumericComparison.Equal => number == Value,
        NumericComparison.NotEqual => number != Value,
        NumericComparison.Less => number < Value,
        NumericComparison.LessOrEqual => number <= Value,
        NumericComparison.Greater => number > Value,
        _ => number >= Value
    };
}

// A Numeric column's value between two numbers, both included (Task #053). Lower may equal Upper; it may not be above
// it. A row without a value never matches.
public sealed record NumericBetweenCondition : RowFilterCondition
{
    public NumericBetweenCondition(Guid columnId, double lower, double upper)
        : base(columnId)
    {
        if (!double.IsFinite(lower) || !double.IsFinite(upper))
        {
            throw new ArgumentException($"Between needs finite numbers ({lower}, {upper}).", nameof(lower));
        }

        if (lower > upper)
        {
            throw new ArgumentException($"The lower number ({lower}) is above the upper one ({upper}).", nameof(lower));
        }

        Lower = lower;
        Upper = upper;
    }

    public double Lower { get; }

    public double Upper { get; }

    public override WorksheetDataType DataType => WorksheetDataType.Numeric;

    public bool Matches(double? value) => value is { } number && number >= Lower && number <= Upper;
}

// A String column's value compared with typed text: "is" or "is not" (Task #053). Exact, ordinal and case-sensitive -
// nothing is trimmed or folded. The text is not empty or blank (a blank cell is Missing). A row without a value never
// matches - not even "is not".
public sealed record TextComparisonCondition : RowFilterCondition
{
    public TextComparisonCondition(Guid columnId, string value, bool negated = false)
        : base(columnId)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The text must not be empty or blank; a blank cell is Missing.", nameof(value));
        }

        Value = value;
        Negated = negated;
    }

    public string Value { get; }

    // "is not".
    public bool Negated { get; }

    public override WorksheetDataType DataType => WorksheetDataType.String;

    public bool Matches(string? value) => value is not null && string.Equals(value, Value, StringComparison.Ordinal) != Negated;
}
