using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Enums;

namespace YAT.Application.Graphs;

// The values a graph's filter can be chosen from (Task #049): the distinct values of one column, in the order they
// first occur in the worksheet, at most GraphValueFilter.MaximumDistinctValues of them, with Missing and "more than
// listed" reported apart. It is what the setup is shown - the storage's own record of the values stays below the
// application.
//
// A column with more distinct values than listed (HasMore) is not a category to pick from: its values are never offered
// as if they were all of them.
public sealed class GraphFilterValues
{
    public GraphFilterValues(RawDistinctValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Raw = values;
    }

    public Guid ColumnId => Raw.ColumnId;

    public WorksheetDataType DataType => Raw.DataType;

    // The listed numbers of a Numeric column; empty for a String column.
    public IReadOnlyList<double> Numbers => Raw is NumericRawDistinctValues numeric ? numeric.Values : [];

    // The listed text of a String column; empty for a Numeric column.
    public IReadOnlyList<string> Texts => Raw is StringRawDistinctValues text ? text.Values : [];

    // How many values are listed, Missing not counted.
    public int Count => Raw.Count;

    // Whether some worksheet row has no value in the column.
    public bool HasMissing => Raw.HasMissing;

    // Whether the column has more distinct values than were listed.
    public bool HasMore => Raw.HasMore;

    internal RawDistinctValues Raw { get; }
}
