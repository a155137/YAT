using YAT.Domain.Enums;

namespace YAT.Application.Analyses;

// The source column of analysis values or groups, so a result can name a variable without reading metadata again. The
// column id stays authoritative; the name is a display copy taken when the data was read.
public sealed record AnalysisColumnInfo(Guid ColumnId, string Name, WorksheetDataType DataType);

// One measured variable over the worksheet's rows. A null entry is a row without a value for this variable: it is kept
// in place, because an analysis has to count what is missing, and because Values[i] must stay the i-th worksheet row.
public sealed record AnalysisVariableData(AnalysisColumnInfo Column, ReadOnlyMemory<double?> Values);

// The group each worksheet row belongs to, in row order. A null entry means the row has no group value (the cell is
// empty, or the group column does not reach that row): the row is kept, and the analysis shows it as "(Missing)".
public abstract record AnalysisGroupData(AnalysisColumnInfo Column)
{
    public abstract int Count { get; }

    public abstract bool IsMissing(int index);
}

public sealed record NumericAnalysisGroupData : AnalysisGroupData
{
    public NumericAnalysisGroupData(AnalysisColumnInfo column, ReadOnlyMemory<double?> values)
        : base(column)
    {
        Values = values;
    }

    // Numeric group values stay numeric: they are never formatted as text here.
    public ReadOnlyMemory<double?> Values { get; }

    public override int Count => Values.Length;

    public override bool IsMissing(int index) => Values.Span[index] is null;
}

public sealed record StringAnalysisGroupData : AnalysisGroupData
{
    public StringAnalysisGroupData(AnalysisColumnInfo column, ReadOnlyMemory<string?> values)
        : base(column)
    {
        Values = values;
    }

    public ReadOnlyMemory<string?> Values { get; }

    public override int Count => Values.Length;

    public override bool IsMissing(int index) => Values.Span[index] is null;
}

// The rows of one worksheet as an analysis sees them: every logical row of the worksheet, in row order, with the
// configured variables and the optional group column beside each other. Nothing is filtered, sorted, grouped or
// computed here.
//
// RowCount is the worksheet's logical row count, not the length of the longest selected column: what an analysis
// reports as missing must not change because another variable happened to be selected with it. Every variable and the
// group therefore have exactly RowCount entries, with null where a column does not reach that row.
public sealed record AnalysisData
{
    public AnalysisData(
        Guid worksheetId,
        int rowCount,
        IReadOnlyList<AnalysisVariableData> variables,
        AnalysisGroupData? group)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);

        if (variables.Count == 0)
        {
            throw new ArgumentException("An analysis needs at least one variable.", nameof(variables));
        }

        if (variables.Any(variable => variable.Values.Length != rowCount))
        {
            throw new ArgumentException("Every variable must have one value per worksheet row.", nameof(variables));
        }

        if (group is not null && group.Count != rowCount)
        {
            throw new ArgumentException("Group data must have one value per worksheet row.", nameof(group));
        }

        WorksheetId = worksheetId;
        RowCount = rowCount;
        Variables = variables;
        Group = group;
    }

    public Guid WorksheetId { get; }

    // The worksheet's logical row count: the rows every variable and the group are reported over.
    public int RowCount { get; }

    // In the order the configuration selected them.
    public IReadOnlyList<AnalysisVariableData> Variables { get; }

    // Null when the configuration selected no group column.
    public AnalysisGroupData? Group { get; }
}
