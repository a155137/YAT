using YAT.Domain.Entities;

namespace YAT.Application.Queries;

// Bounded page metadata for a worksheet: column definitions in Index order and row counts,
// without reading or materializing raw cell values.
public sealed class WorksheetPageMetadata
{
    internal WorksheetPageMetadata(
        Guid worksheetId,
        long totalRowCount,
        long rowOffset,
        int rowCount,
        IReadOnlyList<WorksheetColumn> columns)
    {
        WorksheetId = worksheetId;
        TotalRowCount = totalRowCount;
        RowOffset = rowOffset;
        RowCount = rowCount;
        Columns = columns;
    }

    public Guid WorksheetId { get; }

    public long TotalRowCount { get; }

    public long RowOffset { get; }

    public int RowCount { get; }

    public IReadOnlyList<WorksheetColumn> Columns { get; }
}

// Bounded column-window result for a single row page: typed raw columns in the requested order.
public sealed class WorksheetColumnWindow
{
    internal WorksheetColumnWindow(
        Guid worksheetId,
        long rowOffset,
        int rowCount,
        IReadOnlyList<WorksheetDataPageColumn> columns)
    {
        WorksheetId = worksheetId;
        RowOffset = rowOffset;
        RowCount = rowCount;
        Columns = columns;
    }

    public Guid WorksheetId { get; }

    public long RowOffset { get; }

    public int RowCount { get; }

    public IReadOnlyList<WorksheetDataPageColumn> Columns { get; }
}
