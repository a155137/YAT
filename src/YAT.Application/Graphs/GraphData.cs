using YAT.Domain.Enums;

namespace YAT.Application.Graphs;

// The source column of graph values or groups, so a graph can label axes and legends without reading metadata again.
// The column id stays authoritative; the name is a display copy taken when the data was read.
public sealed record GraphColumnInfo(Guid ColumnId, string Name, WorksheetDataType DataType);

// The group each observation belongs to, in observation order. A null entry means the observation has no group value
// (the group cell was empty, or the group column has no value in that row): the observation is kept, unassigned.
public abstract record GraphGroupData(GraphColumnInfo Column)
{
    public abstract int Count { get; }

    public abstract bool IsMissing(int index);
}

public sealed record NumericGroupData : GraphGroupData
{
    public NumericGroupData(GraphColumnInfo column, ReadOnlyMemory<double?> values)
        : base(column)
    {
        Values = values;
    }

    // Numeric group values stay numeric: they are never formatted as text here.
    public ReadOnlyMemory<double?> Values { get; }

    public override int Count => Values.Length;

    public override bool IsMissing(int index) => Values.Span[index] is null;
}

public sealed record StringGroupData : GraphGroupData
{
    public StringGroupData(GraphColumnInfo column, ReadOnlyMemory<string?> values)
        : base(column)
    {
        Values = values;
    }

    public ReadOnlyMemory<string?> Values { get; }

    public override int Count => Values.Length;

    public override bool IsMissing(int index) => Values.Span[index] is null;
}

// Graph-ready observations of one graph configuration: the rows of one worksheet that the graph can use, in worksheet
// row order, with the graph's null rules already applied. Nothing is sorted, grouped or computed here.
public abstract record GraphData
{
    private protected GraphData(GraphType graphType, Guid worksheetId, int count, GraphGroupData? group)
    {
        if (group is not null && group.Count != count)
        {
            throw new ArgumentException("Group data must have one value per observation.", nameof(group));
        }

        GraphType = graphType;
        WorksheetId = worksheetId;
        Count = count;
        Group = group;
    }

    public GraphType GraphType { get; }

    public Guid WorksheetId { get; }

    // Observations left after filtering; every value and group buffer has exactly this length.
    public int Count { get; }

    // Null when the configuration assigned no Group column.
    public GraphGroupData? Group { get; }
}

// Scatter observations: X[i] and Y[i] come from the same worksheet row, and a row is kept only when both are present.
public sealed record ScatterGraphData : GraphData
{
    public ScatterGraphData(
        Guid worksheetId,
        GraphColumnInfo x,
        GraphColumnInfo y,
        ReadOnlyMemory<double> xValues,
        ReadOnlyMemory<double> yValues,
        GraphGroupData? group)
        : base(GraphType.ScatterPlot, worksheetId, xValues.Length, group)
    {
        if (xValues.Length != yValues.Length)
        {
            throw new ArgumentException("A scatter plot needs one X and one Y value per observation.", nameof(yValues));
        }

        X = x;
        Y = y;
        XValues = xValues;
        YValues = yValues;
    }

    public GraphColumnInfo X { get; }

    public GraphColumnInfo Y { get; }

    public ReadOnlyMemory<double> XValues { get; }

    public ReadOnlyMemory<double> YValues { get; }
}

// Several measured variables of one worksheet, read together: the shape a box plot reads.
//
// Each variable keeps its own observations and its own group values, because the rows they can use are not the same:
// a row where Reg1 is empty is dropped from Reg1 and kept for Reg2. The columns are still read in the same aligned row
// windows, so every observation is paired with the group value of the row it came from; only the compaction is
// per variable.
public sealed record MultiVariableGraphData : GraphData
{
    public MultiVariableGraphData(GraphType graphType, Guid worksheetId, IReadOnlyList<UnivariateGraphData> variables)
        : base(graphType, worksheetId, TotalCount(variables), null)
    {
        Variables = variables;
    }

    // In the order the configuration selected them.
    public IReadOnlyList<UnivariateGraphData> Variables { get; }

    private static int TotalCount(IReadOnlyList<UnivariateGraphData> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (variables.Count == 0)
        {
            throw new ArgumentException("A multi-variable graph needs at least one variable.", nameof(variables));
        }

        if (variables.Any(variable => variable is null))
        {
            throw new ArgumentException("A graph variable must not be null.", nameof(variables));
        }

        return variables.Sum(variable => variable.Count);
    }
}

// One measured variable: the shape a histogram, probability plot and empirical CDF all read. Rows whose variable is
// empty are dropped.
public sealed record UnivariateGraphData : GraphData
{
    public UnivariateGraphData(
        GraphType graphType,
        Guid worksheetId,
        GraphColumnInfo variable,
        ReadOnlyMemory<double> values,
        GraphGroupData? group)
        : base(graphType, worksheetId, values.Length, group)
    {
        Variable = variable;
        Values = values;
    }

    public GraphColumnInfo Variable { get; }

    public ReadOnlyMemory<double> Values { get; }
}
