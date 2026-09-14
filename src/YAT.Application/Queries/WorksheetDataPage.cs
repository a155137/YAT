using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Entities;

namespace YAT.Application.Queries;

// One bounded, column-oriented window of a worksheet's data for display. It never holds more than
// WorksheetDataQueryService.MaxPageRowCount rows, and never the worksheet's whole dataset.
public sealed class WorksheetDataPage
{
    internal WorksheetDataPage(
        Guid worksheetId,
        long totalRowCount,
        long rowOffset,
        int rowCount,
        IReadOnlyList<WorksheetDataPageColumn> columns)
    {
        WorksheetId = worksheetId;
        TotalRowCount = totalRowCount;
        RowOffset = rowOffset;
        RowCount = rowCount;
        Columns = columns;
    }

    public Guid WorksheetId { get; }

    // The worksheet's logical row count: the longest live raw column.
    public long TotalRowCount { get; }

    // Zero-based worksheet row of the first row in this page.
    public long RowOffset { get; }

    public int RowCount { get; }

    // Column metadata ordered by Index, each with exactly RowCount values.
    public IReadOnlyList<WorksheetDataPageColumn> Columns { get; }
}

// Values is typed by the stored raw column. A null value is an empty cell: a cell beyond a shorter column's length,
// or any cell of a column that has metadata but no stored raw values.
public sealed class WorksheetDataPageColumn
{
    internal WorksheetDataPageColumn(WorksheetColumn column, RawDataColumn values)
    {
        Column = column;
        Values = values;
    }

    public WorksheetColumn Column { get; }

    public RawDataColumn Values { get; }
}
